using System.Collections.Generic;
using reromanlee.Transvoxel.Meshing;
using UnityEngine;
using UnityEngine.Rendering;

namespace reromanlee.Transvoxel
{
    /// <summary>
    /// The scene-side view of one live chunk: a GameObject with mesh components under the
    /// terrain root. Purely presentation — all decisions happen in <see cref="TransvoxelTerrain"/>.
    ///
    /// Views are pooled by the terrain: while the player moves fast, hundreds of chunks per
    /// second come and go, and creating/destroying GameObjects and Mesh objects at that rate
    /// costs more main-thread time than the mesh uploads themselves. <see cref="Deactivate"/>
    /// parks a view for reuse; <see cref="Activate"/> re-targets it at a new chunk.
    /// </summary>
    public sealed class TerrainChunk
    {
        // Fade parameters ride in the mesh itself as a UV2 channel of (fadeStartTime,
        // signedFadeDuration): renderer state (MaterialPropertyBlocks) and per-chunk
        // material values are bypassed by some render paths (e.g. URP's GPU Resident
        // Drawer), but vertex data reaches the shader on every path. The shader animates
        // the fade from Unity's built-in _Time — no custom uniform in the time path at
        // all — so nothing per-chunk is touched per frame. The duration's sign marks a
        // cross-fade ghost; zero renders solid.
        //
        // The channel is written on EVERY apply, including when fading is switched off
        // (duration 0). It must exist unconditionally: the shader declares the TEXCOORD1
        // input in every variant, and a mesh that lacks the channel leaves that vertex
        // attribute unbound — the fragment shader then reads whatever happens to be
        // there and clips the surface into holes at random.
        static readonly List<Vector2> FadeDataScratch = new List<Vector2>(8192); // main thread only

        public NodeKey Key { get; private set; }
        public byte TransitionMask { get; set; }
        public int VertexCount { get; private set; }
        public int IndexCount { get; private set; }

        /// <summary>
        /// Bytes of this view's mesh data (vertex layout + index buffer), for stats. The
        /// mesh is readable, so the same amount lives once in RAM and once on the GPU.
        /// </summary>
        public long MeshBytes { get; private set; }

        /// <summary>
        /// Start of the fade-in ramp, baked into the mesh's UV2 on apply. The terrain uses
        /// it to decide when the chunk is fully dithered in (SpawnTime + fade duration).
        /// </summary>
        public float SpawnTime { get; private set; }

        readonly GameObject gameObject;
        readonly MeshFilter meshFilter;
        readonly MeshRenderer meshRenderer;
        MeshCollider meshCollider;
        Mesh mesh;

        public TerrainChunk(NodeKey key, Transform parent, Material material, float voxelSize, int chunkCells)
        {
            gameObject = new GameObject("Transvoxel Chunk");
            gameObject.transform.SetParent(parent, false);
            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            Activate(key, material, voxelSize, chunkCells);
        }

        /// <summary>Points a (fresh or pooled) view at a chunk and switches it on.</summary>
        public void Activate(NodeKey key, Material material, float voxelSize, int chunkCells)
        {
            Key = key;
            TransitionMask = 0;
            SpawnTime = Time.time;
#if UNITY_EDITOR
            // Handy in the hierarchy, but a per-activation string allocation in builds.
            gameObject.name = $"Chunk {key}";
#endif
            var min = key.MinVoxel(chunkCells);
            gameObject.transform.localPosition = new Vector3(min.x, min.y, min.z) * voxelSize;
            meshRenderer.sharedMaterial = material;
            gameObject.SetActive(true);
        }

        /// <summary>
        /// Parks the view for reuse: hides the GameObject and empties (but keeps) the mesh,
        /// so the next <see cref="Activate"/> + apply pays for neither object creation nor
        /// component setup.
        /// </summary>
        public void Deactivate()
        {
            gameObject.SetActive(false);
            VertexCount = 0;
            IndexCount = 0;
            MeshBytes = 0;
            if (mesh != null)
                mesh.Clear();
            if (meshCollider != null)
                meshCollider.sharedMesh = null;
        }

        /// <summary>Uploads freshly built buffers into this chunk's mesh (main thread only).</summary>
        public void Apply(MeshBuffers buffers, float fadeSeconds)
        {
            EnsureMesh();
            mesh.Clear();
            VertexCount = buffers.Vertices.Count;
            IndexCount = buffers.Indices.Count;
            MeshBytes = 0;
            if (!buffers.IsEmpty)
            {
                int vertexStride = 12 + 12 + 8 + 8                      // position, normal, uv0, fade uv2
                                   + (buffers.MaterialBlend.Count > 0 ? 4 : 0); // blend color
                int indexStride = buffers.Vertices.Count > ushort.MaxValue ? 4 : 2;
                MeshBytes = (long)VertexCount * vertexStride + (long)IndexCount * indexStride;
                mesh.indexFormat = buffers.Vertices.Count > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16;
                mesh.SetVertices(buffers.Vertices);
                mesh.SetNormals(buffers.Normals);
                mesh.SetUVs(0, buffers.Uvs);
                // Material blend rides the color channel (present only with a palette);
                // like the fade UV2 it is mesh data, so it reaches every render path.
                if (buffers.MaterialBlend.Count > 0)
                    mesh.SetColors(buffers.MaterialBlend);
                WriteFadeData(fadeSeconds); // always — see the note on the fade channel above
                mesh.SetTriangles(buffers.Indices, 0, calculateBounds: true);
            }

            meshRenderer.enabled = !buffers.IsEmpty;
        }

        /// <summary>
        /// Marks this view's mesh as a cross-fade ghost: from now on, the shader draws
        /// exactly the pixels the successor doesn't draw yet, shrinking to nothing over the
        /// fade duration. Do not call while the mesh is being baked.
        /// </summary>
        public void MakeGhost(float fadeSeconds)
        {
            if (mesh == null || mesh.vertexCount == 0)
                return;
            WriteFadeData(-fadeSeconds);
        }

        void WriteFadeData(float signedDuration)
        {
            // The shader compares against Unity's built-in _Time.y = time since level load.
            var value = new Vector2(Time.timeSinceLevelLoad, signedDuration);
            int count = mesh.vertexCount;
            FadeDataScratch.Clear();
            for (int i = 0; i < count; i++)
                FadeDataScratch.Add(value);
            mesh.SetUVs(1, FadeDataScratch);
        }

        void EnsureMesh()
        {
            if (mesh != null)
                return;
            mesh = new Mesh { name = "Transvoxel Chunk" };
            mesh.MarkDynamic();
            meshFilter.sharedMesh = mesh;
        }

        /// <summary>Attaches the physics shape once its bake (done off-thread) has finished.</summary>
        public void AttachBakedCollider()
        {
            if (gameObject == null || !gameObject.activeSelf || mesh == null || mesh.vertexCount == 0)
                return;
            if (meshCollider == null)
                meshCollider = gameObject.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = mesh;
        }

        public EntityId MeshEntityId => mesh != null ? mesh.GetEntityId() : EntityId.None;

        /// <summary>
        /// Swaps the material this view renders with. The terrain uses it for the LOD debug
        /// tint, which is a per-LOD SHARED material rather than a per-renderer override:
        /// renderer state is bypassed by batched render paths, and a MaterialPropertyBlock
        /// would additionally drop every tinted chunk out of the SRP Batcher — a frame-rate
        /// cliff exactly when the debug view is switched on.
        /// </summary>
        public void SetMaterial(Material material)
        {
            if (meshRenderer != null)
                meshRenderer.sharedMaterial = material;
        }

        /// <summary>
        /// Restarts the fade-in clock — used when a live chunk's mesh is replaced
        /// (terraform, LOD mask change) and cross-fades against its ghost. The following
        /// <see cref="Apply"/> bakes the new start time into the mesh.
        /// </summary>
        public void RestartFadeIn()
        {
            SpawnTime = Time.time;
        }

        public void Destroy()
        {
            if (mesh != null)
                Object.Destroy(mesh);
            if (gameObject != null)
                Object.Destroy(gameObject);
        }

        /// <summary>
        /// Releases the mesh from this view without destroying the GameObject — used when a
        /// physics bake still reads the mesh on another thread, or to hand the old surface
        /// to a cross-fade ghost. A fresh mesh is created on the next apply.
        /// With <paramref name="keepColliderShape"/> the collider keeps referencing the old
        /// mesh so physics stays solid until the replacement's bake attaches.
        /// </summary>
        public Mesh DetachMesh(bool keepColliderShape = false)
        {
            var kept = mesh;
            mesh = null;
            MeshBytes = 0;
            if (meshFilter != null)
                meshFilter.sharedMesh = null;
            if (!keepColliderShape && meshCollider != null)
                meshCollider.sharedMesh = null;
            return kept;
        }

        /// <summary>
        /// Turns this (freshly activated) view into a cross-fade ghost showing a mesh
        /// detached from another view. Purely visual: no collider, fades out via
        /// <see cref="MakeGhost"/> and is then recycled.
        /// </summary>
        public void AttachGhostMesh(Mesh ghostMesh)
        {
            mesh = ghostMesh;
            meshFilter.sharedMesh = ghostMesh;
            VertexCount = ghostMesh != null ? ghostMesh.vertexCount : 0;
            IndexCount = ghostMesh != null ? (int)ghostMesh.GetIndexCount(0) : 0;
            MeshBytes = EstimateMeshBytes(ghostMesh);
            meshRenderer.enabled = VertexCount > 0;
        }

        /// <summary>
        /// Mesh data bytes from an arbitrary mesh (ghosts, meshes parked for collider
        /// bakes), derived from its actual vertex layout and index format.
        /// </summary>
        public static long EstimateMeshBytes(Mesh target)
        {
            if (target == null || target.vertexCount == 0)
                return 0;
            int vertexStride =
                (target.HasVertexAttribute(VertexAttribute.Position) ? 12 : 0)
                + (target.HasVertexAttribute(VertexAttribute.Normal) ? 12 : 0)
                + (target.HasVertexAttribute(VertexAttribute.TexCoord0) ? 8 : 0)
                + (target.HasVertexAttribute(VertexAttribute.TexCoord1) ? 8 : 0)
                + (target.HasVertexAttribute(VertexAttribute.Color) ? 4 : 0);
            int indexStride = target.indexFormat == IndexFormat.UInt32 ? 4 : 2;
            return (long)target.vertexCount * vertexStride
                   + (long)target.GetIndexCount(0) * indexStride;
        }
    }
}
