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
        static readonly Color[] LodTints =
        {
            new Color(1f, 1f, 1f),        // LOD0 white
            new Color(0.6f, 1f, 0.6f),    // LOD1 green
            new Color(0.6f, 0.8f, 1f),    // LOD2 blue
            new Color(1f, 1f, 0.5f),      // LOD3 yellow
            new Color(1f, 0.7f, 0.4f),    // LOD4 orange
            new Color(1f, 0.5f, 0.5f),    // LOD5 red
            new Color(1f, 0.6f, 1f),      // LOD6 magenta
            new Color(0.7f, 0.7f, 0.7f),  // LOD7+ grey
        };

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"); // URP/HDRP Lit
        static readonly int ColorId = Shader.PropertyToID("_Color");         // Built-in Standard
        static readonly int FadeId = Shader.PropertyToID("_TransvoxelFade"); // stipple fade-in

        public NodeKey Key { get; private set; }
        public byte TransitionMask { get; set; }
        public int VertexCount { get; private set; }

        /// <summary>When this view was (re)targeted at its chunk — the fade-in ramp start.</summary>
        public float SpawnTime { get; private set; }

        /// <summary>
        /// True once the chunk is fully dithered in (or fading is off). Replaced chunks are
        /// only retired against fully-visible replacements, or LOD swaps would show the
        /// stipple holes of a half-faded successor.
        /// </summary>
        public bool FadeInComplete => appliedFade >= 1f;

        readonly GameObject gameObject;
        readonly MeshFilter meshFilter;
        readonly MeshRenderer meshRenderer;
        readonly MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
        MeshCollider meshCollider;
        Mesh mesh;
        bool tintVisible;
        float appliedFade = 1f; // 1 = solid; (0,1) = fading in; negative = ghost fading out

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
            appliedFade = 1f;
            tintVisible = false;
#if UNITY_EDITOR
            // Handy in the hierarchy, but a per-activation string allocation in builds.
            gameObject.name = $"Chunk {key}";
#endif
            var min = key.MinVoxel(chunkCells);
            gameObject.transform.localPosition = new Vector3(min.x, min.y, min.z) * voxelSize;
            meshRenderer.sharedMaterial = material;
            PushPropertyBlock();
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
            if (mesh != null)
                mesh.Clear();
            if (meshCollider != null)
                meshCollider.sharedMesh = null;
        }

        /// <summary>Uploads freshly built CPU buffers into this chunk's mesh (main thread only).</summary>
        public void Apply(MeshBuffers buffers, bool colorizeLod)
        {
            EnsureMesh();
            mesh.Clear();
            VertexCount = buffers.Vertices.Count;
            if (!buffers.IsEmpty)
            {
                mesh.indexFormat = buffers.Vertices.Count > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16;
                mesh.SetVertices(buffers.Vertices);
                mesh.SetNormals(buffers.Normals);
                mesh.SetUVs(0, buffers.Uvs);
                mesh.SetTriangles(buffers.Indices, 0, calculateBounds: true);
            }

            meshRenderer.enabled = !buffers.IsEmpty;
            SetLodTintVisible(colorizeLod);
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

        public void SetLodTintVisible(bool visible)
        {
            tintVisible = visible;
            PushPropertyBlock();
        }

        /// <summary>
        /// Drives the stipple fade-in (0 = invisible, 1 = solid). Quantized so the property
        /// block is only touched when the value visibly changes; shaders without
        /// _TransvoxelFade simply ignore it.
        /// </summary>
        public void SetFadeIn(float fade)
        {
            fade = Mathf.Round(Mathf.Clamp01(fade) * 64f) / 64f;
            if (fade == appliedFade)
                return;
            appliedFade = fade;
            PushPropertyBlock();
        }

        /// <summary>
        /// Drives a ghost's fade-out (1 = fully covering, 0 = gone). Ghosts carry a NEGATIVE
        /// fade value: the shader then keeps exactly the pixels the successor chunk (fading
        /// in with the complementary value) does not draw yet, so a cross-fading pair always
        /// covers the surface — no holes, no double-drawn pixels.
        /// </summary>
        public void SetGhostFade(float ghost)
        {
            float fade = -Mathf.Round(Mathf.Clamp01(ghost) * 64f) / 64f;
            if (fade == appliedFade)
                return;
            appliedFade = fade;
            PushPropertyBlock();
        }

        /// <summary>Restarts the fade-in ramp — used when a live chunk's mesh is replaced (terraform, LOD mask change) and cross-fades against its ghost.</summary>
        public void RestartFadeIn()
        {
            SpawnTime = Time.time;
            SetFadeIn(0f);
        }

        /// <summary>
        /// A renderer with a MaterialPropertyBlock is excluded from the SRP Batcher, so the
        /// block only exists while it says something: mid-fade or LOD-tinted. The moment a
        /// chunk is fully faded in and untinted the block is removed and the chunk batches
        /// with every other steady chunk — at thousands of live chunks this is a large
        /// render-thread saving.
        /// </summary>
        void PushPropertyBlock()
        {
            bool identity = !tintVisible && appliedFade >= 1f;
            if (identity)
            {
                meshRenderer.SetPropertyBlock(null);
                return;
            }

            propertyBlock.Clear();
            propertyBlock.SetFloat(FadeId, appliedFade);
            if (tintVisible)
            {
                var tint = LodTints[Mathf.Min(Key.Lod, LodTints.Length - 1)];
                propertyBlock.SetColor(BaseColorId, tint);
                propertyBlock.SetColor(ColorId, tint);
            }
            meshRenderer.SetPropertyBlock(propertyBlock);
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
            if (meshFilter != null)
                meshFilter.sharedMesh = null;
            if (!keepColliderShape && meshCollider != null)
                meshCollider.sharedMesh = null;
            return kept;
        }

        /// <summary>
        /// Turns this (freshly activated) view into a cross-fade ghost showing a mesh
        /// detached from another view. Purely visual: no collider, fades out via
        /// <see cref="SetGhostFade"/> and is then recycled.
        /// </summary>
        public void AttachGhostMesh(Mesh ghostMesh)
        {
            mesh = ghostMesh;
            meshFilter.sharedMesh = ghostMesh;
            VertexCount = ghostMesh != null ? ghostMesh.vertexCount : 0;
            meshRenderer.enabled = VertexCount > 0;
        }
    }
}
