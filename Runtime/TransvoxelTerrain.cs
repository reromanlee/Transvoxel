using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using reromanlee.Transvoxel.Density;
using reromanlee.Transvoxel.Gpu;
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
    ///  - the meshers in Meshing/ (CPU) or the compute kernels in Gpu/ know *how* to
    ///    triangulate a chunk — selectable via <see cref="TransvoxelSettings.meshingBackend"/>,
    ///  - the density sources in Density/ answer *where* ground is,
    ///  - this component wires them together and keeps every per-frame cost bounded:
    ///      * octree selection runs on a worker task (never a main-thread spike),
    ///      * builds pop off a distance-prioritized queue, nearest first, re-sorted whenever
    ///        the viewer moves — so teleports and high speeds fill in around the player,
    ///      * finished meshes upload under a millisecond budget per frame,
    ///      * chunk GameObjects and meshes are pooled instead of created/destroyed.
    ///
    /// Old chunks are kept on screen until their replacements are ready, so LOD changes
    /// swap without holes; chunks invalidated by one terraform stroke swap in the same
    /// frame, so brushing never flashes a gap between neighbouring chunks.
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

        /// <summary>
        /// Which palette material each voxel is made of (id 0 everywhere until something
        /// paints it). Written by <see cref="Terraform(Vector3, float, float, bool, byte)"/>;
        /// custom painting code may write it directly — call <see cref="InvalidateRegion"/>
        /// afterwards. Only sampled while <see cref="TransvoxelSettings.materialPalette"/>
        /// is assigned, but the data survives palette swaps and settings rebuilds.
        /// </summary>
        public VoxelMaterialLayer Materials { get; private set; } = new VoxelMaterialLayer();

        /// <summary>The backend actually in use (GPU requests fall back to CPU when unsupported).</summary>
        public MeshingBackend ActiveBackend { get; private set; }

        public int LiveChunkCount => live.Count;
        public int PendingBuildCount => pending.Count;
        public long TotalVertices { get; private set; }

        sealed class MesherPair
        {
            public readonly TransvoxelMesher Regular = new TransvoxelMesher();
            public readonly TransvoxelTransitionMesher Transition = new TransvoxelTransitionMesher();
        }

        static readonly ConcurrentBag<MesherPair> MesherPool = new ConcurrentBag<MesherPair>();

        /// <summary>Everything a CPU worker needs, snapshotted per pipeline.</summary>
        sealed class BuildContext
        {
            public BuildQueue Queue;
            public IDensitySource Density;
            public IVoxelMaterialSource Materials; // null = no palette, meshes carry no blend data
            public SampleCache Cache;
            public int Cells;
            public float Iso;
            public float VoxelSize;
            public float UvScale;
            public ConcurrentQueue<ChunkBuildResult> Results;
        }

        /// <summary>Chunks invalidated by one terraform stroke, swapped atomically (one frame).</summary>
        sealed class EditGroup
        {
            public readonly HashSet<NodeKey> Waiting = new HashSet<NodeKey>();
            public readonly List<ChunkBuildResult> Ready = new List<ChunkBuildResult>();
        }

        const int MaxPooledChunkViews = 512;

        // ---- pipeline (rebuilt on any settings change) ----
        IDensitySource density;
        TerrainOctree octree;
        SampleCache cache;
        BuildQueue buildQueue;
        CancellationTokenSource workerCancellation;
        GpuChunkBuilder gpuBuilder;
        Material runtimeMaterial;
        Material generatedMaterial;
        TransvoxelSettings subscribedSettings;
        bool settingsDirty;
        int pipelineGeneration;

        // ---- chunk bookkeeping (main thread) ----
        readonly Dictionary<NodeKey, TerrainChunk> live = new Dictionary<NodeKey, TerrainChunk>();
        readonly Dictionary<NodeKey, ChunkBuildJob> pending = new Dictionary<NodeKey, ChunkBuildJob>();
        readonly Dictionary<NodeKey, byte> desired = new Dictionary<NodeKey, byte>();

        // Every desired key plus all of its ancestors. Lets SubtreeReady answer "is anything
        // wanted below this node?" with one lookup instead of recursing through 8^depth
        // children — the difference between microseconds and multi-second freezes when a
        // teleport obsoletes deep trees at high maxLodLevels.
        readonly HashSet<NodeKey> desiredAncestors = new HashSet<NodeKey>();
        readonly HashSet<NodeKey> obsolete = new HashSet<NodeKey>();
        readonly Dictionary<NodeKey, float> obsoleteSince = new Dictionary<NodeKey, float>();
        readonly List<NodeKey> keyScratch = new List<NodeKey>();
        readonly Stack<TerrainChunk> chunkViewPool = new Stack<TerrainChunk>();

        // Cross-fade ghosts: retired chunks and replaced meshes dither out here instead of
        // popping. At most one ghost per chunk key, so rapid brushing can't stack them.
        sealed class DyingChunk
        {
            public TerrainChunk View;
            public float StartTime;
        }

        readonly List<DyingChunk> dying = new List<DyingChunk>();
        readonly Dictionary<NodeKey, DyingChunk> dyingByKey = new Dictionary<NodeKey, DyingChunk>();

        ConcurrentQueue<ChunkBuildResult> completedBuilds = new ConcurrentQueue<ChunkBuildResult>();
        List<ChunkBuildResult> deferredApplies = new List<ChunkBuildResult>();
        readonly System.Diagnostics.Stopwatch applyStopwatch = new System.Diagnostics.Stopwatch();

        // ---- terraform edit groups (task: no one-frame holes while brushing) ----
        readonly Dictionary<int, EditGroup> editGroups = new Dictionary<int, EditGroup>();
        readonly Dictionary<NodeKey, int> editGroupOf = new Dictionary<NodeKey, int>();
        readonly List<int> generationScratch = new List<int>();
        int nextEditGeneration = 1;

        // ---- async octree selection ----
        readonly List<ChunkDrawCommand> selectionOutput = new List<ChunkDrawCommand>(1024);
        Task selectionTask;
        Vector3 selectionViewerLocal;
        int selectionGeneration;
        Vector3 lastSelectPosition;
        bool needsSelect;

        // Collider bakes run on worker threads; a mesh must not be edited or destroyed
        // while its bake is in flight, so those meshes are parked and cleaned up after.
        // Meshes are tracked by EntityId (Unity 6.2+ replacement for instance IDs).
        readonly ConcurrentQueue<(TerrainChunk chunk, EntityId meshId)> bakedColliders =
            new ConcurrentQueue<(TerrainChunk, EntityId)>();
        readonly HashSet<EntityId> bakingMeshIds = new HashSet<EntityId>();
        readonly Dictionary<EntityId, Mesh> parkedMeshes = new Dictionary<EntityId, Mesh>();

        // Stipple-fade globals for fade-aware shaders (see TransvoxelLitDithered.shader) —
        // edge dissolve only. The per-chunk time fade is baked into each mesh's UV2 and
        // animated by the shader from Unity's built-in _Time.
        static readonly int ViewerPosId = Shader.PropertyToID("_TransvoxelViewerPos");
        static readonly int ViewDistanceId = Shader.PropertyToID("_TransvoxelViewDistance");
        static readonly int EdgeFadeBandId = Shader.PropertyToID("_TransvoxelEdgeFadeBand");
        static readonly int FadePropertyId = Shader.PropertyToID("_TransvoxelFade");
        static readonly int FadeAwareMarkerId = Shader.PropertyToID("_TransvoxelFadeAware");
        static readonly int EdgeFadeCurveId = Shader.PropertyToID("_TransvoxelEdgeFadeCurve");

        // The edgeFadeCurve is baked into a small LUT texture (curve.Evaluate can't run per
        // pixel) and pushed as a global so it reaches every render path, like the other fade
        // inputs. Rebaked on pipeline (re)build and on a fade-only settings edit.
        const int EdgeFadeLutSize = 256;
        Texture2D edgeFadeLut;
        readonly float[] edgeFadeLutScratch = new float[EdgeFadeLutSize];

        // Signature of the settings that require a full geometry rebuild. A settings edit that
        // leaves it unchanged (only edgeFadeFraction / edgeFadeCurve moved) refreshes the fade
        // LUT in place instead of tearing down every chunk — so the curve can be tuned live.
        string appliedStructuralKey;

        // Whether the terrain material's shader declares _TransvoxelFade. Without shader
        // support a cross-fade ghost would just sit fully opaque on top of the new mesh for
        // the whole duration and then pop — worse than no fade — so every fade feature is
        // gated on this. effectiveFadeSeconds is chunkFadeInSeconds or 0 accordingly.
        bool fadeAwareMaterial;
        float effectiveFadeSeconds;

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
            while (chunkViewPool.Count > 0)
                chunkViewPool.Pop().Destroy();
            if (edgeFadeLut != null)
            {
                Destroy(edgeFadeLut);
                edgeFadeLut = null;
            }
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
        /// (Re)builds the octree, density stack, sample cache, material and meshing backend
        /// from the current <see cref="settings"/>. Player edits (density layer A) and the
        /// generated default material are carried across so a settings tweak doesn't wipe
        /// terraforming.
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
            buildQueue = new BuildQueue(settings.chunkCells);
            runtimeMaterial = settings.material != null
                ? settings.material
                : generatedMaterial ??= CreateDefaultMaterial();

            // Fading is implemented in the shader; a material without _TransvoxelFade can't
            // show it, so disable the whole fade/ghost machinery instead of producing
            // opaque ghosts and invisible delays.
            // The marker property tags this package's shader; _TransvoxelFade covers custom
            // shaders that followed the older README snippet (a material property there
            // still detects, even though our own shader now keeps the fade global-only).
            fadeAwareMaterial = runtimeMaterial.HasProperty(FadeAwareMarkerId)
                                || runtimeMaterial.HasProperty(FadePropertyId);
            effectiveFadeSeconds = fadeAwareMaterial ? settings.chunkFadeInSeconds : 0f;
            if (!fadeAwareMaterial && (settings.chunkFadeInSeconds > 0f || settings.edgeFadeFraction > 0f))
                Debug.LogWarning("[Transvoxel] Chunk fading is enabled in the settings, but the " +
                                 $"terrain material's shader ('{runtimeMaterial.shader.name}') has no " +
                                 "_TransvoxelFade support — fading and cross-fades are disabled. Switch " +
                                 "the material to 'Transvoxel/Lit Dithered' or add the fade snippet from " +
                                 "the README to your shader.", this);
            WarnIfGpuResidentDrawerActive();

            pipelineGeneration++;
            ActiveBackend = ResolveBackend(out ComputeShader gpuShader);
            if (ActiveBackend != MeshingBackend.CpuThreads)
                gpuBuilder = new GpuChunkBuilder(gpuShader, settings, Layers.Edits);
            if (ActiveBackend != MeshingBackend.GpuCompute)
                StartCpuWorkers(); // CpuThreads and Hybrid: CPU workers pull the same queue

            BakeEdgeFadeCurve();
            appliedStructuralKey = ComputeStructuralKey();
            needsSelect = true;
        }

        /// <summary>
        /// URP's GPU Resident Drawer draws "stable" renderers through a batched path that
        /// can bypass renderer and material state for shaders without DOTS-instancing
        /// support (this package's dithered shader included) — chunks then render with
        /// baked defaults: no fades, sometimes not even globals. Detect it via reflection
        /// (this package has no URP assembly reference) and tell the user, once.
        /// </summary>
        static bool warnedAboutResidentDrawer;

        void WarnIfGpuResidentDrawerActive()
        {
            if (warnedAboutResidentDrawer || !fadeAwareMaterial)
                return;
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline == null)
                return;
            var property = pipeline.GetType().GetProperty("gpuResidentDrawerMode");
            if (property == null)
                return;
            object mode = property.GetValue(pipeline);
            if (mode == null || mode.ToString() == "Disabled")
                return;
            warnedAboutResidentDrawer = true;
            Debug.LogWarning("[Transvoxel] The render pipeline asset has 'GPU Resident Drawer' set to " +
                             $"'{mode}'. That path can render terrain chunks with baked shader state, " +
                             "breaking the stipple fades (chunks pop or stay solid). If fades don't " +
                             "animate, set GPU Resident Drawer to 'Disabled' on the URP asset " +
                             "(Rendering section).", this);
        }

        MeshingBackend ResolveBackend(out ComputeShader gpuShader)
        {
            gpuShader = null;
            if (settings.meshingBackend == MeshingBackend.CpuThreads)
                return MeshingBackend.CpuThreads;

            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
            {
                Debug.LogWarning("[Transvoxel] GPU meshing requested but the platform lacks compute " +
                                 "shaders or async GPU readback; using CPU threads.", this);
                return MeshingBackend.CpuThreads;
            }
            if (DensityOverride != null)
            {
                Debug.LogWarning("[Transvoxel] GPU meshing cannot run a custom DensityOverride " +
                                 "(arbitrary C# density code); using CPU threads.", this);
                return MeshingBackend.CpuThreads;
            }

            gpuShader = settings.gpuComputeOverride != null
                ? settings.gpuComputeOverride
                : Resources.Load<ComputeShader>("TransvoxelCompute");
            if (gpuShader == null)
            {
                Debug.LogWarning("[Transvoxel] TransvoxelCompute.compute not found in Resources; " +
                                 "using CPU threads.", this);
                return MeshingBackend.CpuThreads;
            }
            return settings.meshingBackend;
        }

        /// <summary>Materials are meshed only with a non-empty palette to blend them with.</summary>
        bool MaterialsActive =>
            settings.materialPalette != null && settings.materialPalette.LayerCount > 0;

        void StartCpuWorkers()
        {
            workerCancellation = new CancellationTokenSource();
            var context = new BuildContext
            {
                Queue = buildQueue,
                Density = density,
                Materials = MaterialsActive ? Materials : null,
                Cache = cache,
                Cells = settings.chunkCells,
                Iso = settings.isoLevel,
                VoxelSize = settings.voxelSize,
                UvScale = settings.uvScale,
                Results = completedBuilds,
            };
            CancellationToken token = workerCancellation.Token;
            int workers = settings.EffectiveMaxConcurrentBuilds;
            for (int i = 0; i < workers; i++)
                Task.Run(() => CpuWorkerLoop(context, token));
        }

        /// <summary>
        /// One CPU meshing worker: pops the nearest queued chunk, samples + meshes it, and
        /// hands the buffers to the main thread. Superseded jobs are dropped at every stage.
        /// </summary>
        static async Task CpuWorkerLoop(BuildContext context, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                ChunkBuildJob job;
                try
                {
                    job = await context.Queue.DequeueAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (job.Superseded)
                    continue;

                MeshBuffers buffers = null;
                try
                {
                    var samples = context.Cache.GetOrSample(context.Density, job.Key, context.Cells,
                        context.Iso, job.Mask);
                    if (job.Superseded)
                        continue;

                    buffers = MeshBuffers.Rent();
                    if (!MesherPool.TryTake(out var meshers))
                        meshers = new MesherPair();
                    try
                    {
                        meshers.Regular.GenerateRegularMesh(samples, context.VoxelSize, context.UvScale, buffers,
                            context.Materials);
                        for (int f = 0; f < 6; f++)
                        {
                            if ((job.Mask & (1 << f)) != 0)
                                meshers.Transition.GenerateTransitionMesh(samples, (CubeFace)f, context.Density,
                                    context.VoxelSize, context.UvScale, buffers, context.Materials);
                        }
                        if (!job.SmoothShading)
                            buffers.ConvertToFlatShaded();
                    }
                    finally
                    {
                        MesherPool.Add(meshers);
                    }

                    context.Results.Enqueue(new ChunkBuildResult
                    {
                        Key = job.Key,
                        Mask = job.Mask,
                        Ticket = job,
                        Buffers = buffers,
                    });
                    buffers = null; // ownership transferred
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    if (buffers != null)
                        MeshBuffers.Return(buffers);
                    context.Results.Enqueue(new ChunkBuildResult
                    {
                        Key = job.Key,
                        Mask = job.Mask,
                        Ticket = job,
                        Failed = true,
                    });
                }
            }
        }

        /// <summary>Tears down every live and pending chunk, leaving the pipeline fields intact.</summary>
        void ClearScene()
        {
            // Supersede first: workers and GPU readbacks flushed below then drop their
            // results instead of finishing work nobody wants.
            foreach (var job in pending.Values)
                job.Superseded = true;
            pending.Clear();

            if (workerCancellation != null)
            {
                workerCancellation.Cancel();
                workerCancellation.Dispose();
                workerCancellation = null;
            }
            if (gpuBuilder != null)
            {
                gpuBuilder.Dispose();
                gpuBuilder = null;
            }

            // Anything still in flight will enqueue into this queue; nobody drains the old
            // one anymore, so replace it and let the GC take the stragglers.
            completedBuilds = new ConcurrentQueue<ChunkBuildResult>();
            foreach (var result in deferredApplies)
                result.ReleasePayload();
            deferredApplies = new List<ChunkBuildResult>();

            foreach (var group in editGroups.Values)
                foreach (var result in group.Ready)
                    result.ReleasePayload();
            editGroups.Clear();
            editGroupOf.Clear();

            foreach (var chunk in live.Values)
                DestroyChunkView(chunk);
            live.Clear();
            foreach (var entry in dying)
                DestroyChunkView(entry.View);
            dying.Clear();
            dyingByKey.Clear();
            desired.Clear();
            desiredAncestors.Clear();
            obsolete.Clear();
            obsoleteSince.Clear();
            pipelineGeneration++; // orphans any selection task still running
            TotalVertices = 0;
        }

        /// <summary>
        /// Rebuilds the whole world from the current <see cref="settings"/>. Runs
        /// automatically the frame after the settings asset changes, so Inspector tweaks to
        /// LOD levels, view distance, noise, iso level, backend and the rest apply live.
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
                // A fade-only edit (edge fade fraction/curve) leaves the structural key intact:
                // just rebake the LUT and let UpdateChunkFades push the band, keeping every live
                // chunk on screen so the curve can be tuned without a full rebuild + re-fade.
                if (appliedStructuralKey != null && ComputeStructuralKey() == appliedStructuralKey)
                    BakeEdgeFadeCurve();
                else
                    ApplySettings();
            }

            DrainBakedColliders();
            DrainCompletedBuilds();
            ReleaseEditGroups();
            PumpSelection();
            gpuBuilder?.Pump(buildQueue, completedBuilds);
            UpdateChunkFades(); // before cleanup: retirement checks replacements' fade state
            CleanupObsoleteChunks();
            UpdateStats();
        }

        /// <summary>
        /// Drives the stipple fading: pushes the per-pixel draw-distance fade uniforms for
        /// fade-aware shaders, and ramps every chunk's fade-in value. Shaders without the
        /// properties ignore all of it, so the feature is safe with any material.
        /// </summary>
        void UpdateChunkFades()
        {
            // The time fade lives entirely in each mesh's UV2 + the shader (via _Time).
            // The master fade is a GLOBAL uniform (globals default to 0 — without this the
            // terrain would be invisible); it is deliberately not a material property so
            // the SRP Batcher can never lock it to an inspector value.
            Shader.SetGlobalFloat(FadePropertyId, 1f);
            float band = fadeAwareMaterial ? settings.edgeFadeFraction * settings.viewDistance : 0f;
            Shader.SetGlobalFloat(EdgeFadeBandId, band);
            if (band > 0f)
            {
                Shader.SetGlobalVector(ViewerPosId, viewer.position);
                Shader.SetGlobalFloat(ViewDistanceId, settings.viewDistance);
            }

            UpdateDyingChunks(Time.time, Mathf.Max(effectiveFadeSeconds, 1e-3f));
        }

        /// <summary>
        /// Bakes <see cref="TransvoxelSettings.edgeFadeCurve"/> into a 1D LUT texture and
        /// publishes it as the <c>_TransvoxelEdgeFadeCurve</c> global. The shader samples it by
        /// the raw edge fade (0 at the draw distance, 1 at the viewer) to reshape the dither
        /// opacity per pixel. An empty curve falls back to the identity ramp (a no-op remap).
        /// </summary>
        void BakeEdgeFadeCurve()
        {
            if (edgeFadeLut == null)
            {
                edgeFadeLut = new Texture2D(EdgeFadeLutSize, 1, TextureFormat.RFloat, false, true)
                {
                    name = "Transvoxel Edge Fade Curve LUT",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }

            AnimationCurve curve = settings.edgeFadeCurve;
            bool usable = curve != null && curve.length > 0;
            for (int i = 0; i < EdgeFadeLutSize; i++)
            {
                float x = i / (float)(EdgeFadeLutSize - 1);
                edgeFadeLutScratch[i] = usable ? Mathf.Clamp01(curve.Evaluate(x)) : x;
            }
            edgeFadeLut.SetPixelData(edgeFadeLutScratch, 0);
            edgeFadeLut.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            Shader.SetGlobalTexture(EdgeFadeCurveId, edgeFadeLut);
        }

        /// <summary>
        /// A fingerprint of every setting that requires rebuilding the octree/density/meshing
        /// pipeline. <see cref="TransvoxelSettings.edgeFadeFraction"/>,
        /// <see cref="TransvoxelSettings.edgeFadeCurve"/> and
        /// <see cref="TransvoxelSettings.materialBlendSharpness"/> are deliberately excluded —
        /// they only feed shader globals and the fade LUT, so editing them refreshes those in
        /// place instead of tearing down the whole scene. The material palette counts only by
        /// identity: swapping the asset re-meshes (vertices carry blend data), while edits
        /// inside it just re-bind textures and uniforms. Keep this in sync when adding
        /// settings that affect geometry.
        /// </summary>
        string ComputeStructuralKey()
        {
            return string.Join("|",
                settings.voxelSize, settings.chunkCells, settings.maxLodLevels, settings.viewDistance,
                settings.lodSplitFactor, settings.isoLevel, settings.smoothShading,
                settings.material != null ? settings.material.GetEntityId().ToString() : "0", settings.uvScale,
                settings.colorizeLods, settings.chunkFadeInSeconds,
                settings.materialPalette != null ? settings.materialPalette.GetEntityId().ToString() : "0",
                JsonUtility.ToJson(settings.noise),
                (int)settings.meshingBackend,
                settings.gpuComputeOverride != null ? settings.gpuComputeOverride.GetEntityId().ToString() : "0",
                settings.gpuJobsInFlight, settings.meshApplyBudgetMs, settings.meshApplyBudgetPerFrame,
                settings.maxConcurrentBuilds, settings.colliderMaxLod, settings.viewerMoveThreshold,
                settings.lodSwapLinger, settings.densityCacheChunks);
        }

        // ------------------------------------------------------------------ cross-fade ghosts

        /// <summary>
        /// Puts a view on death row: it dithers out over the fade duration (complementary
        /// pattern — see the shader) and is then recycled. Replaces any older ghost for the
        /// same chunk, so rapid re-edits cross-fade between the latest two states only.
        /// </summary>
        void AddDyingChunk(TerrainChunk view)
        {
            if (effectiveFadeSeconds <= 0f)
            {
                DestroyChunkView(view);
                return;
            }
            // A mesh being read by an off-thread physics bake must not be rewritten; just
            // drop it instantly in that rare case.
            EntityId meshId = view.MeshEntityId;
            if (meshId != EntityId.None && bakingMeshIds.Contains(meshId))
            {
                DestroyChunkView(view);
                return;
            }

            if (dyingByKey.TryGetValue(view.Key, out var previous))
            {
                DestroyChunkView(previous.View);
                dying.Remove(previous);
            }
            view.MakeGhost(effectiveFadeSeconds);
            var entry = new DyingChunk { View = view, StartTime = Time.time };
            dying.Add(entry);
            dyingByKey[view.Key] = entry;
        }

        void UpdateDyingChunks(float now, float duration)
        {
            // The fade-out itself is shader-animated; this only recycles finished ghosts.
            for (int i = dying.Count - 1; i >= 0; i--)
            {
                var entry = dying[i];
                if (now - entry.StartTime < duration)
                    continue;
                if (dyingByKey.TryGetValue(entry.View.Key, out var current) && current == entry)
                    dyingByKey.Remove(entry.View.Key);
                DestroyChunkView(entry.View);
                dying.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ selection

        /// <summary>
        /// Runs octree selection on a worker task (it walks thousands of nodes — too much
        /// for a frame while flying fast) and integrates the finished result on the main
        /// thread. At most one selection is in flight; the newest viewer position wins.
        /// </summary>
        void PumpSelection()
        {
            if (selectionTask != null)
            {
                if (!selectionTask.IsCompleted)
                    return;
                Task finished = selectionTask;
                selectionTask = null;
                if (finished.IsFaulted)
                {
                    Debug.LogException(finished.Exception);
                    needsSelect = true;
                }
                else if (selectionGeneration == pipelineGeneration)
                {
                    IntegrateSelection();
                }
            }

            Vector3 viewerLocal = transform.InverseTransformPoint(viewer.position);
            float threshold = settings.viewerMoveThreshold * settings.chunkCells * settings.voxelSize;
            if (!needsSelect && (viewerLocal - lastSelectPosition).sqrMagnitude < threshold * threshold)
                return;

            needsSelect = false;
            lastSelectPosition = viewerLocal;
            selectionViewerLocal = viewerLocal;
            selectionGeneration = pipelineGeneration;

            TerrainOctree tree = octree;
            List<ChunkDrawCommand> output = selectionOutput;
            output.Clear();
            selectionTask = Task.Run(() => tree.SelectChunks(viewerLocal, output));
        }

        void IntegrateSelection()
        {
            desired.Clear();
            foreach (var command in selectionOutput)
                desired[command.Key] = command.TransitionMask;

            desiredAncestors.Clear();
            foreach (var command in selectionOutput)
            {
                var walk = command.Key;
                while (desiredAncestors.Add(walk) && walk.Lod < settings.maxLodLevels)
                    walk = walk.Parent;
            }

            // Schedule anything new or changed.
            foreach (var command in selectionOutput)
            {
                bool isLive = live.TryGetValue(command.Key, out var chunk)
                              && chunk.TransitionMask == command.TransitionMask;
                bool isPending = pending.TryGetValue(command.Key, out var job)
                                 && job.Mask == command.TransitionMask;
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
            {
                pending.Remove(key);
                RemoveFromEditGroup(key);
            }

            // Live chunks that fell out of the desired set wait in `obsolete` until their
            // replacements are on screen. Stamp the time a chunk first becomes obsolete so the
            // linger timeout in CleanupObsoleteChunks can measure from it.
            foreach (var key in live.Keys)
            {
                if (!desired.ContainsKey(key) && obsolete.Add(key))
                    obsoleteSince[key] = Time.time;
            }

            // Everything still queued gets re-sorted around where the viewer is *now*.
            buildQueue.UpdateViewer(selectionViewerLocal / settings.voxelSize);
        }

        // ------------------------------------------------------------------ building

        void ScheduleBuild(NodeKey key, byte mask, bool rush = false)
        {
            if (pending.TryGetValue(key, out var previous))
                previous.Superseded = true;

            var job = new ChunkBuildJob
            {
                Key = key,
                Mask = mask,
                Rush = rush,
                SmoothShading = settings.smoothShading,
            };
            pending[key] = job;
            buildQueue.Enqueue(job);
        }

        void DrainCompletedBuilds()
        {
            applyStopwatch.Restart();
            long budgetTicks = (long)(settings.meshApplyBudgetMs
                                      * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
            int applyBudget = settings.meshApplyBudgetPerFrame;

            // Results deferred earlier (bake in flight / budget ran out) go first.
            if (deferredApplies.Count > 0)
            {
                var retry = deferredApplies;
                deferredApplies = new List<ChunkBuildResult>();
                foreach (var result in retry)
                    ProcessResult(result, ref applyBudget, budgetTicks);
            }

            while (applyBudget > 0 && applyStopwatch.ElapsedTicks < budgetTicks
                   && completedBuilds.TryDequeue(out var result))
                ProcessResult(result, ref applyBudget, budgetTicks);
        }

        void ProcessResult(ChunkBuildResult result, ref int applyBudget, long budgetTicks)
        {
            bool isCurrent = pending.TryGetValue(result.Key, out var ticket)
                             && ReferenceEquals(ticket, result.Ticket);
            if (!isCurrent)
            {
                result.ReleasePayload();
                return;
            }

            if (result.Failed)
            {
                // Build failed; drop the ticket so the next selection pass retries.
                pending.Remove(result.Key);
                RemoveFromEditGroup(result.Key);
                needsSelect = true;
                return;
            }

            if (!desired.ContainsKey(result.Key))
            {
                pending.Remove(result.Key);
                RemoveFromEditGroup(result.Key);
                result.ReleasePayload();
                return;
            }

            // Terraform group member: stash it — the whole group swaps in one frame, so
            // neighbouring chunks can never disagree at a shared border mid-stroke.
            if (editGroupOf.TryGetValue(result.Key, out int generation)
                && editGroups.TryGetValue(generation, out var group))
            {
                group.Waiting.Remove(result.Key);
                group.Ready.Add(result);
                return;
            }

            live.TryGetValue(result.Key, out var existing);

            if (existing != null && bakingMeshIds.Contains(existing.MeshEntityId))
            {
                // The physics bake still reads this mesh; try again next frame.
                deferredApplies.Add(result);
                return;
            }

            // A live chunk whose transition mask changed must not swap before the
            // neighbours that caused the change are on screen, or the seam is open for
            // however long the apply queue takes — very visible at distant LOD rings.
            // Its current mesh still matches the old neighbourhood, so waiting is free;
            // lodSwapLinger bounds the wait (0 disables the gate entirely).
            if (existing != null && existing.TransitionMask != result.Mask
                && !NeighboursReadyForMaskSwap(result.Key, result.Mask, existing.TransitionMask))
            {
                if (result.FirstDeferredTime == 0f)
                    result.FirstDeferredTime = Time.time;
                if (Time.time - result.FirstDeferredTime < settings.lodSwapLinger)
                {
                    deferredApplies.Add(result);
                    return;
                }
                // Waited long enough — swap anyway rather than stall forever.
            }

            if (applyBudget <= 0 || applyStopwatch.ElapsedTicks >= budgetTicks)
            {
                deferredApplies.Add(result);
                return;
            }

            ApplyResultToScene(result);
            applyBudget--;
        }

        /// <summary>
        /// True when every neighbour whose LOD change flipped one of this chunk's transition
        /// mask bits is already live. A bit that turned on means finer chunks appeared across
        /// that face (they must be on screen before we shift our boundary toward them); a bit
        /// that turned off means the finer neighbours merged into a same-LOD chunk (it must
        /// exist before we unshift). Gated chunks only ever wait on brand-new chunks, which
        /// apply ungated — so chains cannot deadlock.
        /// </summary>
        bool NeighboursReadyForMaskSwap(NodeKey key, byte newMask, byte oldMask)
        {
            int changed = newMask ^ oldMask;
            if (changed == 0)
                return true;

            for (int f = 0; f < 6; f++)
            {
                if ((changed & (1 << f)) == 0)
                    continue;
                var face = (CubeFace)f;
                var neighbour = key.Neighbour(face);

                if ((newMask & (1 << f)) != 0)
                {
                    // Gained transition cells: the finer chunks across the face (exactly one
                    // level down, by the 2:1 balance) must be live.
                    if (!FaceChildrenLive(neighbour, face))
                        return false;
                }
                else if (desired.ContainsKey(neighbour) && !live.ContainsKey(neighbour))
                {
                    // Lost the transition: the same-LOD neighbour replacing the finer ones
                    // is still building. (A now-coarser neighbour owns the seam itself and
                    // is gated on us instead.)
                    return false;
                }
            }
            return true;
        }

        /// <summary>Are the four children of <paramref name="neighbour"/> touching the shared face live (where desired)?</summary>
        bool FaceChildrenLive(NodeKey neighbour, CubeFace face)
        {
            if (neighbour.Lod == 0)
                return true;
            int axis = face.Axis();
            // Crossing our +axis face means the neighbour's children on its -axis side touch us.
            int axisBit = face.IsPositive() ? 0 : 1;
            for (int child = 0; child < 8; child++)
            {
                if (((child >> axis) & 1) != axisBit)
                    continue;
                var childKey = neighbour.Child(child);
                if (desired.ContainsKey(childKey) && !live.ContainsKey(childKey))
                    return false;
            }
            return true;
        }

        void ApplyResultToScene(ChunkBuildResult result)
        {
            pending.Remove(result.Key);

            if (!live.TryGetValue(result.Key, out var chunk))
            {
                chunk = RentChunkView(result.Key);
                live.Add(result.Key, chunk);
            }
            else if (effectiveFadeSeconds > 0f)
            {
                // Cross-fade the mesh swap (terraform, transition-mask change): the old
                // surface moves onto a ghost view that dithers out with the complementary
                // pattern while this chunk's new mesh dithers in — the pair always covers
                // the surface, so the swap is seamless. The collider keeps the old shape
                // until the new mesh's bake attaches.
                if (chunk.VertexCount > 0)
                {
                    Mesh oldMesh = chunk.DetachMesh(keepColliderShape: true);
                    if (oldMesh != null)
                    {
                        var ghost = RentChunkView(result.Key);
                        ghost.AttachGhostMesh(oldMesh);
                        ghost.SetLodTintVisible(settings.colorizeLods);
                        AddDyingChunk(ghost);
                    }
                }
                chunk.RestartFadeIn();
            }

            chunk.TransitionMask = result.Mask;
            bool empty = result.IsEmpty;
            chunk.Apply(result.Buffers, settings.colorizeLods, effectiveFadeSeconds);
            result.ReleasePayload();

            bool wantCollider = settings.colliderMaxLod >= 0
                                && result.Key.Lod <= settings.colliderMaxLod
                                && !empty;
            if (wantCollider)
                StartColliderBake(chunk);
        }

        // ------------------------------------------------------------------ terraform groups

        void RemoveFromEditGroup(NodeKey key)
        {
            if (!editGroupOf.TryGetValue(key, out int generation))
                return;
            editGroupOf.Remove(key);
            if (!editGroups.TryGetValue(generation, out var group))
                return;

            group.Waiting.Remove(key);
            for (int i = group.Ready.Count - 1; i >= 0; i--)
            {
                if (group.Ready[i].Key.Equals(key))
                {
                    group.Ready[i].ReleasePayload();
                    group.Ready.RemoveAt(i);
                }
            }
            if (group.Waiting.Count == 0 && group.Ready.Count == 0)
                editGroups.Remove(generation);
        }

        /// <summary>
        /// Applies every terraform group whose members have all finished building — in one
        /// frame, bypassing the apply budget, so a brush stroke never shows chunk A's new
        /// surface next to chunk B's old one (the "one-frame hole").
        /// </summary>
        void ReleaseEditGroups()
        {
            if (editGroups.Count == 0)
                return;

            generationScratch.Clear();
            foreach (var entry in editGroups)
            {
                EditGroup group = entry.Value;
                if (group.Waiting.Count > 0)
                    continue;

                // If any member's mesh is locked by a physics bake, retry next frame — the
                // group must swap together or not at all.
                bool blocked = false;
                foreach (var result in group.Ready)
                {
                    bool current = pending.TryGetValue(result.Key, out var ticket)
                                   && ReferenceEquals(ticket, result.Ticket);
                    if (current && live.TryGetValue(result.Key, out var chunk)
                                && bakingMeshIds.Contains(chunk.MeshEntityId))
                    {
                        blocked = true;
                        break;
                    }
                }
                if (blocked)
                    continue;

                foreach (var result in group.Ready)
                {
                    editGroupOf.Remove(result.Key);
                    bool current = pending.TryGetValue(result.Key, out var ticket)
                                   && ReferenceEquals(ticket, result.Ticket);
                    if (!current)
                    {
                        result.ReleasePayload(); // superseded by a newer stroke's build
                        continue;
                    }
                    if (!desired.ContainsKey(result.Key))
                    {
                        pending.Remove(result.Key);
                        result.ReleasePayload();
                        continue;
                    }
                    ApplyResultToScene(result);
                }
                generationScratch.Add(entry.Key);
            }

            foreach (int generation in generationScratch)
                editGroups.Remove(generation);
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
                    // The chunk view was recycled mid-bake; the mesh was kept alive for the
                    // bake and can go now.
                    parkedMeshes.Remove(baked.meshId);
                    Destroy(parked);
                }
                else if (baked.chunk.MeshEntityId == baked.meshId)
                {
                    // Only attach if the view still shows the mesh we baked — a pooled view
                    // may already display a different chunk with a fresh mesh.
                    baked.chunk.AttachBakedCollider();
                }
            }
        }

        // ------------------------------------------------------------------ retiring & pooling

        void CleanupObsoleteChunks()
        {
            if (obsolete.Count == 0)
                return;

            // An obsolete chunk is retired once (a) its own footprint is covered by
            // replacements AND (b) the rest of the newly-selected set has settled — nothing
            // pending — so a neighbour still rebuilding its transition mask can't briefly crack
            // the seam as the old geometry disappears. When the viewer keeps moving and builds
            // never fully drain, a per-chunk linger timeout releases it anyway (lodSwapLinger).
            //
            // Retirement is time-boxed: a teleport can obsolete thousands of chunks at once,
            // and destroying (or even pooling) them all in one frame is a visible hitch.
            // Leftovers keep their old geometry on screen and go next frame — harmless.
            bool settled = pending.Count == 0;
            float now = Time.time;
            const int MaxRetirementsPerFrame = 256;
            int retired = 0;
            applyStopwatch.Restart();
            long budgetTicks = (long)(settings.meshApplyBudgetMs
                                      * (System.Diagnostics.Stopwatch.Frequency / 1000.0));

            keyScratch.Clear();
            foreach (var key in obsolete)
            {
                if (retired >= MaxRetirementsPerFrame || applyStopwatch.ElapsedTicks >= budgetTicks)
                    break;

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
                    // Dither out instead of popping; replacements are already fully faded
                    // in underneath (LiveAndFadedIn), so this reads as a cross-fade.
                    AddDyingChunk(chunk);
                    live.Remove(key);
                    RemoveFromEditGroup(key);
                    retired++;
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
                    return LiveAndFadedIn(ancestor);
            }
            return SubtreeReady(key);
        }

        bool SubtreeReady(NodeKey key)
        {
            if (desired.ContainsKey(key))
                return LiveAndFadedIn(key);
            if (!desiredAncestors.Contains(key))
                return true; // nothing wanted anywhere below — region is not rendered
            if (key.Lod == 0)
                return true;
            for (int child = 0; child < 8; child++)
            {
                if (!SubtreeReady(key.Child(child)))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// A replacement counts only when applied AND fully dithered in — retiring the old
        /// chunk against a half-faded successor would show the successor's stipple holes.
        /// (Empty chunks have nothing to fade and count immediately.)
        /// </summary>
        bool LiveAndFadedIn(NodeKey key)
        {
            if (!live.TryGetValue(key, out var chunk) || pending.ContainsKey(key))
                return false;
            return chunk.VertexCount == 0 || Time.time - chunk.SpawnTime >= effectiveFadeSeconds;
        }

        TerrainChunk RentChunkView(NodeKey key)
        {
            if (chunkViewPool.Count > 0)
            {
                var pooled = chunkViewPool.Pop();
                pooled.Activate(key, runtimeMaterial, settings.voxelSize, settings.chunkCells);
                return pooled;
            }
            return new TerrainChunk(key, transform, runtimeMaterial, settings.voxelSize, settings.chunkCells);
        }

        void DestroyChunkView(TerrainChunk chunk)
        {
            // A mesh still being read by an off-thread physics bake must stay alive; park it
            // and let DrainBakedColliders destroy it once the bake finishes. The view itself
            // is pooled either way — it just loses its mesh and grows a fresh one on reuse.
            EntityId meshId = chunk.MeshEntityId;
            if (meshId != EntityId.None && bakingMeshIds.Contains(meshId))
            {
                var mesh = chunk.DetachMesh();
                if (mesh != null)
                    parkedMeshes[meshId] = mesh;
            }

            if (chunkViewPool.Count < MaxPooledChunkViews)
            {
                chunk.Deactivate();
                chunkViewPool.Push(chunk);
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
        /// background (at rush priority) and the whole set hot-swaps in a single frame.
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

        /// <summary>
        /// Re-meshes every live chunk whose data overlaps the given voxel region. The
        /// affected chunks form one edit group: their new meshes are applied together in
        /// the same frame, so the swap never flashes a hole along a chunk border.
        /// </summary>
        public void InvalidateRegion(BoundsInt voxelBounds)
        {
            cache.RemoveOverlapping(voxelBounds, settings.chunkCells);
            gpuBuilder?.NotifyEditsChanged(voxelBounds); // re-uploads only the touched bricks

            EditGroup group = null;
            int generation = 0;

            foreach (var entry in live)
            {
                if (!ChunkOverlaps(entry.Key, voxelBounds))
                    continue;

                if (group == null)
                {
                    generation = nextEditGeneration++;
                    group = new EditGroup();
                    editGroups.Add(generation, group);
                }

                // A chunk still waiting on a previous stroke pulls that whole group into
                // this one, so overlapping strokes stay a single atomic swap.
                if (editGroupOf.TryGetValue(entry.Key, out int oldGeneration) && oldGeneration != generation)
                    MergeEditGroup(oldGeneration, generation, group);

                group.Waiting.Add(entry.Key);
                editGroupOf[entry.Key] = generation;
                ScheduleBuild(entry.Key, entry.Value.TransitionMask, rush: true);
            }

            // Chunks that are still building their *first* mesh may have sampled pre-edit
            // density; restart them so they never appear with stale terrain.
            keyScratch.Clear();
            foreach (var entry in pending)
            {
                if (!live.ContainsKey(entry.Key) && ChunkOverlaps(entry.Key, voxelBounds))
                    keyScratch.Add(entry.Key);
            }
            foreach (var key in keyScratch)
                ScheduleBuild(key, pending[key].Mask, rush: true);
        }

        void MergeEditGroup(int fromGeneration, int intoGeneration, EditGroup target)
        {
            if (!editGroups.TryGetValue(fromGeneration, out var source))
                return;
            foreach (var key in source.Waiting)
            {
                target.Waiting.Add(key);
                editGroupOf[key] = intoGeneration;
            }
            foreach (var result in source.Ready)
            {
                target.Ready.Add(result);
                editGroupOf[result.Key] = intoGeneration;
            }
            editGroups.Remove(fromGeneration);
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
            // Prefer the package's stipple-fading shader (URP + Built-in subshaders inside);
            // HDRP is not covered by it, so HDRP keeps its own Lit (no fading there).
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            bool isHdrp = pipeline != null && pipeline.GetType().Name.Contains("HD");
            Shader shader = isHdrp ? null : Shader.Find("Transvoxel/Lit Dithered");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
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
