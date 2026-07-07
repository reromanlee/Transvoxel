using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using reromanlee.Transvoxel.Density;
using reromanlee.Transvoxel.Meshing;
using reromanlee.Transvoxel.Octree;
using UnityEngine;

namespace reromanlee.Transvoxel
{
    /// <summary>
    /// The terrain orchestrator. Drop it on an empty GameObject, assign (or let it create)
    /// a <see cref="TransvoxelSettings"/> asset and a viewer transform, and it keeps the
    /// landscape around the viewer meshed at the right LODs.
    ///
    /// Division of labour (Concept.txt #1):
    ///  - <see cref="TerrainOctree"/> decides *what* should exist (chunks + transition faces),
    ///  - the meshers in Meshing/ know *how* to triangulate a chunk,
    ///  - the density sources in Density/ answer *where* ground is,
    ///  - this component wires them together: it diffs the octree's wishes against the live
    ///    scene, runs sampling + meshing on worker Tasks, and uploads finished meshes on the
    ///    main thread under a per-frame budget so the frame rate stays flat while moving.
    ///
    /// Old chunks are kept on screen until their replacements are ready, so LOD changes
    /// swap without holes.
    /// </summary>
    [AddComponentMenu("Transvoxel/Transvoxel Terrain")]
    public sealed class TransvoxelTerrain : MonoBehaviour
    {
        [Tooltip("Tuning asset. Leave empty to create default settings at runtime.")]
        public TransvoxelSettings settings;

        [Tooltip("LODs center on this transform. Defaults to the main camera.")]
        public Transform viewer;

        /// <summary>Set before the component is enabled to replace the whole density stack.</summary>
        public IDensitySource DensityOverride { get; set; }

        /// <summary>The default two-layer density stack (edits over procedural). Null if overridden.</summary>
        public LayeredDensitySource Layers { get; private set; }

        public int LiveChunkCount => live.Count;
        public int PendingBuildCount => pending.Count;
        public long TotalVertices { get; private set; }

        sealed class PendingBuild
        {
            public byte Mask;
            public volatile bool Superseded;
        }

        struct BuildResult
        {
            public NodeKey Key;
            public byte Mask;
            public PendingBuild Ticket;
            public MeshBuffers Buffers; // null when the build failed
        }

        sealed class MesherPair
        {
            public readonly TransvoxelMesher Regular = new TransvoxelMesher();
            public readonly TransvoxelTransitionMesher Transition = new TransvoxelTransitionMesher();
        }

        static readonly ConcurrentBag<MesherPair> MesherPool = new ConcurrentBag<MesherPair>();

        IDensitySource density;
        TerrainOctree octree;
        SampleCache cache;
        SemaphoreSlim buildSemaphore;
        Material runtimeMaterial;
        Material generatedMaterial;
        TransvoxelSettings subscribedSettings;
        bool settingsDirty;

        readonly Dictionary<NodeKey, TerrainChunk> live = new Dictionary<NodeKey, TerrainChunk>();
        readonly Dictionary<NodeKey, PendingBuild> pending = new Dictionary<NodeKey, PendingBuild>();
        readonly Dictionary<NodeKey, byte> desired = new Dictionary<NodeKey, byte>();
        readonly List<ChunkDrawCommand> selectScratch = new List<ChunkDrawCommand>(1024);
        readonly HashSet<NodeKey> obsolete = new HashSet<NodeKey>();
        readonly Dictionary<NodeKey, float> obsoleteSince = new Dictionary<NodeKey, float>();
        readonly List<NodeKey> keyScratch = new List<NodeKey>();

        ConcurrentQueue<BuildResult> completedBuilds = new ConcurrentQueue<BuildResult>();
        List<BuildResult> deferredApplies = new List<BuildResult>();

        // Collider bakes run on worker threads; a mesh must not be edited or destroyed
        // while its bake is in flight, so those meshes are parked and cleaned up after.
        // Meshes are tracked by EntityId (Unity 6.2+ replacement for instance IDs).
        readonly ConcurrentQueue<(TerrainChunk chunk, EntityId meshId)> bakedColliders =
            new ConcurrentQueue<(TerrainChunk, EntityId)>();
        readonly HashSet<EntityId> bakingMeshIds = new HashSet<EntityId>();
        readonly Dictionary<EntityId, Mesh> parkedMeshes = new Dictionary<EntityId, Mesh>();

        Vector3 lastSelectPosition;
        bool needsSelect;
        int statsCountdown;

        void OnEnable()
        {
            BindSettings();
            BuildPipeline();
        }

        void OnDisable()
        {
            if (subscribedSettings != null)
                subscribedSettings.Changed -= OnSettingsChanged;
            subscribedSettings = null;
            ClearScene();
        }

        // Editing the settings asset (even during Play) fires TransvoxelSettings.Changed.
        // We only flag it here: OnValidate runs on Unity's validation pass, where destroying
        // or creating GameObjects is illegal, so the actual rebuild is deferred to Update.
        void OnSettingsChanged() => settingsDirty = true;

        /// <summary>
        /// Makes sure we hold a settings asset and are subscribed to the one currently in the
        /// <see cref="settings"/> field. Callers commonly assign <c>settings</c> *after*
        /// AddComponent has already run OnEnable (Unity runs it synchronously), so we detect
        /// that swap in Update and rebind — otherwise the assigned asset would be ignored.
        /// </summary>
        void BindSettings()
        {
            if (settings == null)
                settings = ScriptableObject.CreateInstance<TransvoxelSettings>();
            if (ReferenceEquals(settings, subscribedSettings))
                return;
            if (subscribedSettings != null)
                subscribedSettings.Changed -= OnSettingsChanged;
            settings.Changed += OnSettingsChanged;
            subscribedSettings = settings;
        }

        /// <summary>
        /// (Re)builds the octree, density stack, sample cache and material from the current
        /// <see cref="settings"/>. Player edits (density layer A) and the generated default
        /// material are carried across so a settings tweak doesn't wipe terraforming.
        /// </summary>
        void BuildPipeline()
        {
            if (DensityOverride != null)
            {
                density = DensityOverride;
                Layers = DensityOverride as LayeredDensitySource;
            }
            else
            {
                // Rebuild only the procedural landscape (layer B) from the new noise; keep
                // the existing player-edit layer (A) so terraforming survives a settings change.
                var edits = Layers != null ? Layers.Edits : new VoxelEditLayer();
                Layers = new LayeredDensitySource(
                    new ProceduralDensitySource(settings.noise, settings.voxelSize), edits);
                density = Layers;
            }

            octree = new TerrainOctree(settings.chunkCells, settings.voxelSize, settings.maxLodLevels,
                settings.viewDistance, settings.lodSplitFactor);
            cache = new SampleCache(settings.densityCacheChunks);
            buildSemaphore = new SemaphoreSlim(settings.EffectiveMaxConcurrentBuilds);
            runtimeMaterial = settings.material != null
                ? settings.material
                : generatedMaterial ??= CreateDefaultMaterial();
            needsSelect = true;
        }

        /// <summary>Tears down every live and pending chunk, leaving the pipeline fields intact.</summary>
        void ClearScene()
        {
            foreach (var entry in pending.Values)
                entry.Superseded = true;
            pending.Clear();

            // Anything still in flight will enqueue into this queue; nobody drains the old
            // one anymore, so replace it and let the GC take the stragglers.
            completedBuilds = new ConcurrentQueue<BuildResult>();
            deferredApplies = new List<BuildResult>();

            foreach (var chunk in live.Values)
                DestroyChunkView(chunk);
            live.Clear();
            desired.Clear();
            obsolete.Clear();
            obsoleteSince.Clear();
            TotalVertices = 0;
        }

        /// <summary>
        /// Rebuilds the whole world from the current <see cref="settings"/>. Runs
        /// automatically the frame after the settings asset changes, so Inspector tweaks to
        /// LOD levels, view distance, noise, iso level and the rest take effect live.
        /// </summary>
        public void ApplySettings()
        {
            ClearScene();
            BuildPipeline();
        }

        void Update()
        {
            if (viewer == null)
            {
                var mainCamera = Camera.main;
                if (mainCamera == null)
                    return;
                viewer = mainCamera.transform;
            }

            // The settings field may have been assigned or swapped after OnEnable ran; rebind
            // to the new asset and rebuild from it.
            if (!ReferenceEquals(settings, subscribedSettings))
            {
                BindSettings();
                settingsDirty = true;
            }

            if (settingsDirty)
            {
                settingsDirty = false;
                ApplySettings();
            }

            DrainBakedColliders();
            DrainCompletedBuilds();
            SelectChunksIfNeeded();
            CleanupObsoleteChunks();
            UpdateStats();
        }

        // ------------------------------------------------------------------ selection

        void SelectChunksIfNeeded()
        {
            Vector3 viewerLocal = transform.InverseTransformPoint(viewer.position);
            float threshold = settings.viewerMoveThreshold * settings.chunkCells * settings.voxelSize;
            if (!needsSelect && (viewerLocal - lastSelectPosition).sqrMagnitude < threshold * threshold)
                return;

            needsSelect = false;
            lastSelectPosition = viewerLocal;

            selectScratch.Clear();
            octree.SelectChunks(viewerLocal, selectScratch);

            desired.Clear();
            foreach (var command in selectScratch)
                desired[command.Key] = command.TransitionMask;

            // Schedule anything new or changed.
            foreach (var command in selectScratch)
            {
                bool isLive = live.TryGetValue(command.Key, out var chunk)
                              && chunk.TransitionMask == command.TransitionMask;
                bool isPending = pending.TryGetValue(command.Key, out var ticket)
                                 && ticket.Mask == command.TransitionMask;
                if (!isLive && !isPending)
                    ScheduleBuild(command.Key, command.TransitionMask);
            }

            // Retire pending builds nobody wants anymore.
            keyScratch.Clear();
            foreach (var entry in pending)
            {
                if (!desired.ContainsKey(entry.Key))
                {
                    entry.Value.Superseded = true;
                    keyScratch.Add(entry.Key);
                }
            }
            foreach (var key in keyScratch)
                pending.Remove(key);

            // Live chunks that fell out of the desired set wait in `obsolete` until their
            // replacements are on screen. Stamp the time a chunk first becomes obsolete so the
            // linger timeout in CleanupObsoleteChunks can measure from it.
            foreach (var key in live.Keys)
            {
                if (!desired.ContainsKey(key) && obsolete.Add(key))
                    obsoleteSince[key] = Time.time;
            }
        }

        // ------------------------------------------------------------------ building

        void ScheduleBuild(NodeKey key, byte mask)
        {
            if (pending.TryGetValue(key, out var previous))
                previous.Superseded = true;

            var ticket = new PendingBuild { Mask = mask };
            pending[key] = ticket;

            // Snapshot everything the worker needs; settings may change on the main thread.
            var source = density;
            var sampleCache = cache;
            int cells = settings.chunkCells;
            float iso = settings.isoLevel;
            float voxelSize = settings.voxelSize;
            float uvScale = settings.uvScale;
            bool smooth = settings.smoothShading;
            var resultQueue = completedBuilds;
            var semaphore = buildSemaphore;

            Task.Run(async () =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (ticket.Superseded)
                        return;

                    var samples = sampleCache.GetOrSample(source, key, cells, iso, mask);
                    if (ticket.Superseded)
                        return;

                    var buffers = MeshBuffers.Rent();
                    if (!MesherPool.TryTake(out var meshers))
                        meshers = new MesherPair();
                    try
                    {
                        meshers.Regular.GenerateRegularMesh(samples, voxelSize, uvScale, buffers);
                        for (int f = 0; f < 6; f++)
                        {
                            if ((mask & (1 << f)) != 0)
                                meshers.Transition.GenerateTransitionMesh(samples, (CubeFace)f, source,
                                    voxelSize, uvScale, buffers);
                        }
                        if (!smooth)
                            buffers.ConvertToFlatShaded();
                    }
                    finally
                    {
                        MesherPool.Add(meshers);
                    }

                    resultQueue.Enqueue(new BuildResult { Key = key, Mask = mask, Ticket = ticket, Buffers = buffers });
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    resultQueue.Enqueue(new BuildResult { Key = key, Mask = mask, Ticket = ticket, Buffers = null });
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }

        void DrainCompletedBuilds()
        {
            int budget = settings.meshApplyBudgetPerFrame;

            // Results deferred earlier (their mesh had a collider bake in flight) go first.
            if (deferredApplies.Count > 0)
            {
                var retry = deferredApplies;
                deferredApplies = new List<BuildResult>();
                foreach (var result in retry)
                    budget = ApplyOrDefer(result, budget);
            }

            while (budget > 0 && completedBuilds.TryDequeue(out var result))
                budget = ApplyOrDefer(result, budget);
        }

        int ApplyOrDefer(BuildResult result, int budget)
        {
            bool isCurrent = pending.TryGetValue(result.Key, out var ticket) && ReferenceEquals(ticket, result.Ticket);
            if (!isCurrent || !desired.ContainsKey(result.Key))
            {
                if (result.Buffers != null)
                    MeshBuffers.Return(result.Buffers);
                if (isCurrent)
                    pending.Remove(result.Key);
                return budget;
            }

            if (result.Buffers == null)
            {
                // Build failed; drop the ticket so the next selection pass retries.
                pending.Remove(result.Key);
                needsSelect = true;
                return budget;
            }

            if (live.TryGetValue(result.Key, out var existing) && bakingMeshIds.Contains(existing.MeshEntityId))
            {
                // The physics bake still reads this mesh; try again next frame.
                deferredApplies.Add(result);
                return budget;
            }

            pending.Remove(result.Key);

            if (existing == null)
            {
                existing = new TerrainChunk(result.Key, transform, runtimeMaterial,
                    settings.voxelSize, settings.chunkCells);
                live.Add(result.Key, existing);
            }

            existing.TransitionMask = result.Mask;
            existing.Apply(result.Buffers, settings.colorizeLods);
            bool wantCollider = settings.colliderMaxLod >= 0
                                && result.Key.Lod <= settings.colliderMaxLod
                                && !result.Buffers.IsEmpty;
            MeshBuffers.Return(result.Buffers);

            if (wantCollider)
                StartColliderBake(existing);

            return budget - 1;
        }

        // ------------------------------------------------------------------ colliders

        void StartColliderBake(TerrainChunk chunk)
        {
            EntityId meshId = chunk.MeshEntityId;
            if (meshId == EntityId.None || !bakingMeshIds.Add(meshId))
                return;

            var queue = bakedColliders;
            Task.Run(() =>
            {
                try
                {
                    Physics.BakeMesh(meshId, false);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
                queue.Enqueue((chunk, meshId));
            });
        }

        void DrainBakedColliders()
        {
            while (bakedColliders.TryDequeue(out var baked))
            {
                bakingMeshIds.Remove(baked.meshId);
                if (parkedMeshes.TryGetValue(baked.meshId, out var parked))
                {
                    // The chunk was destroyed mid-bake; the mesh was kept alive for the
                    // bake and can go now.
                    parkedMeshes.Remove(baked.meshId);
                    Destroy(parked);
                }
                else
                {
                    baked.chunk.AttachBakedCollider();
                }
            }
        }

        // ------------------------------------------------------------------ retiring

        void CleanupObsoleteChunks()
        {
            if (obsolete.Count == 0)
                return;

            // An obsolete chunk is retired once (a) its own footprint is covered by
            // replacements AND (b) the rest of the newly-selected set has settled — nothing
            // pending — so a neighbour still rebuilding its transition mask can't briefly crack
            // the seam as the old geometry disappears. When the viewer keeps moving and builds
            // never fully drain, a per-chunk linger timeout releases it anyway (lodSwapLinger).
            bool settled = pending.Count == 0;
            float now = Time.time;

            keyScratch.Clear();
            foreach (var key in obsolete)
            {
                if (desired.ContainsKey(key))
                {
                    keyScratch.Add(key); // wanted again; no longer obsolete
                    continue;
                }
                if (!ReplacementsReady(key))
                    continue;

                bool lingered = !obsoleteSince.TryGetValue(key, out float since)
                                || now - since >= settings.lodSwapLinger;
                if (!settled && !lingered)
                    continue;

                if (live.TryGetValue(key, out var chunk))
                {
                    DestroyChunkView(chunk);
                    live.Remove(key);
                }
                keyScratch.Add(key);
            }
            foreach (var key in keyScratch)
            {
                obsolete.Remove(key);
                obsoleteSince.Remove(key);
            }
        }

        /// <summary>
        /// True when the space of a retired chunk is covered again: either a desired
        /// ancestor (LOD merge) or all desired descendants (LOD split) are live, or the
        /// region simply is not rendered anymore (moved out of range).
        /// </summary>
        bool ReplacementsReady(NodeKey key)
        {
            var ancestor = key;
            for (int lod = key.Lod + 1; lod <= settings.maxLodLevels; lod++)
            {
                ancestor = ancestor.Parent;
                if (desired.ContainsKey(ancestor))
                    return live.ContainsKey(ancestor) && !pending.ContainsKey(ancestor);
            }
            return SubtreeReady(key);
        }

        bool SubtreeReady(NodeKey key)
        {
            if (desired.ContainsKey(key))
                return live.ContainsKey(key) && !pending.ContainsKey(key);
            if (key.Lod == 0)
                return true; // nothing wanted here
            for (int child = 0; child < 8; child++)
            {
                if (!SubtreeReady(key.Child(child)))
                    return false;
            }
            return true;
        }

        void DestroyChunkView(TerrainChunk chunk)
        {
            // Read the id before DestroyKeepMesh() clears the mesh reference, or we would
            // park the mesh under EntityId.None and never reclaim it in DrainBakedColliders.
            EntityId meshId = chunk.MeshEntityId;
            if (bakingMeshIds.Contains(meshId))
            {
                var mesh = chunk.DestroyKeepMesh();
                if (mesh != null)
                    parkedMeshes[meshId] = mesh;
            }
            else
            {
                chunk.Destroy();
            }
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Digs (or with <paramref name="build"/> raises) a soft-edged sphere of terrain.
        /// Writes go to the player-edit layer; every affected chunk re-meshes in the
        /// background and hot-swaps when ready.
        /// </summary>
        public void Terraform(Vector3 worldPosition, float radiusMeters, float strength, bool build)
        {
            if (Layers == null)
            {
                Debug.LogWarning("Terraforming needs the layered density stack (a custom DensityOverride is active).");
                return;
            }

            Vector3 centerVoxel = transform.InverseTransformPoint(worldPosition) / settings.voxelSize;
            float radiusVoxels = radiusMeters / settings.voxelSize;
            BoundsInt changed = Layers.ApplySphereBrush(centerVoxel, radiusVoxels, strength, build);
            InvalidateRegion(changed);
        }

        /// <summary>Re-meshes every live chunk whose data overlaps the given voxel region.</summary>
        public void InvalidateRegion(BoundsInt voxelBounds)
        {
            cache.RemoveOverlapping(voxelBounds, settings.chunkCells);

            foreach (var entry in live)
            {
                if (ChunkOverlaps(entry.Key, voxelBounds))
                    ScheduleBuild(entry.Key, entry.Value.TransitionMask);
            }
        }

        /// <summary>
        /// Does the chunk read any voxel inside the given region? Chunk data reaches a bit
        /// past its own box (sample margin, gradients, boundary shift), so pad by two cells.
        /// </summary>
        bool ChunkOverlaps(NodeKey key, BoundsInt voxelBounds)
        {
            int size = settings.chunkCells << key.Lod;
            int pad = 2 * (1 << key.Lod);
            Vector3Int min = key.MinVoxel(settings.chunkCells);
            return min.x - pad < voxelBounds.xMax && min.x + size + pad > voxelBounds.xMin
                && min.y - pad < voxelBounds.yMax && min.y + size + pad > voxelBounds.yMin
                && min.z - pad < voxelBounds.zMax && min.z + size + pad > voxelBounds.zMin;
        }

        /// <summary>Rebuild everything (used when toggling smooth shading and similar global switches).</summary>
        public void RebuildAllChunks()
        {
            foreach (var entry in live)
                ScheduleBuild(entry.Key, entry.Value.TransitionMask);
        }

        /// <summary>Re-applies the LOD debug tint to all live chunks.</summary>
        public void RefreshLodTint()
        {
            foreach (var chunk in live.Values)
                chunk.SetLodTintVisible(settings.colorizeLods);
        }

        /// <summary>
        /// Marches the density field along a ray and returns the surface hit, if any.
        /// Works at full resolution regardless of chunk LOD or colliders — handy for
        /// terraforming tools.
        /// </summary>
        public bool RaycastDensity(Ray worldRay, float maxDistance, out Vector3 worldHit)
        {
            float voxelSize = settings.voxelSize;
            float iso = settings.isoLevel;
            Vector3 origin = transform.InverseTransformPoint(worldRay.origin) / voxelSize;
            Vector3 direction = transform.InverseTransformDirection(worldRay.direction).normalized;
            float maxVoxels = maxDistance / voxelSize;

            bool Solid(Vector3 p) => density.SampleVoxel(
                Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z)) > iso;

            const float step = 0.5f;
            bool wasSolid = Solid(origin);
            for (float t = step; t <= maxVoxels; t += step)
            {
                Vector3 p = origin + direction * t;
                bool solid = Solid(p);
                if (solid && !wasSolid)
                {
                    // Bisect between the last two samples for a tight hit point.
                    float lo = t - step, hi = t;
                    for (int i = 0; i < 8; i++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if (Solid(origin + direction * mid)) hi = mid;
                        else lo = mid;
                    }
                    worldHit = transform.TransformPoint((origin + direction * hi) * voxelSize);
                    return true;
                }
                wasSolid = solid;
            }

            worldHit = default;
            return false;
        }

        // ------------------------------------------------------------------ misc

        void UpdateStats()
        {
            if (--statsCountdown > 0)
                return;
            statsCountdown = 30;
            long total = 0;
            foreach (var chunk in live.Values)
                total += chunk.VertexCount;
            TotalVertices = total;
        }

        static Material CreateDefaultMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("HDRP/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var material = new Material(shader) { name = "Transvoxel Default" };
            var grass = new Color(0.42f, 0.55f, 0.3f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", grass);
            if (material.HasProperty("_Color")) material.SetColor("_Color", grass);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.1f);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.1f);
            return material;
        }
    }
}
