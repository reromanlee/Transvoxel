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

        // GPU results arrive as one interleaved vertex stream (position, normal, uv — the
        // exact struct the compute kernel appends) with implicit sequential indices.
        static readonly VertexAttributeDescriptor[] RawVertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        };
        const int RawFloatsPerVertex = 8;

        // Shared, lazily grown 0,1,2,... index arrays for the sequential (triangle soup)
        // index buffers of GPU meshes. Main thread only.
        static ushort[] sequentialIndices16;
        static int[] sequentialIndices32;

        public NodeKey Key { get; private set; }
        public byte TransitionMask { get; set; }
        public int VertexCount { get; private set; }

        readonly GameObject gameObject;
        readonly MeshFilter meshFilter;
        readonly MeshRenderer meshRenderer;
        MeshCollider meshCollider;
        Mesh mesh;

        public TerrainChunk(NodeKey key, Transform parent, Material material, float voxelSize, int chunkCells)
        {
            gameObject = new GameObject();
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
            gameObject.name = $"Chunk {key}";
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

        /// <summary>
        /// Uploads a GPU build result: an interleaved position/normal/uv vertex stream with
        /// implicit sequential indices, copied straight into the mesh's vertex buffer via the
        /// advanced Mesh API — no per-vertex managed work at all. Bounds are the chunk's box
        /// (known analytically), so nothing needs recalculating.
        /// </summary>
        public void ApplyRaw(float[] interleavedVertices, int vertexCount, bool colorizeLod, Bounds localBounds)
        {
            EnsureMesh();
            mesh.Clear();
            VertexCount = vertexCount;
            if (vertexCount > 0)
            {
                const MeshUpdateFlags Fast = MeshUpdateFlags.DontRecalculateBounds
                                             | MeshUpdateFlags.DontValidateIndices
                                             | MeshUpdateFlags.DontNotifyMeshUsers;
                mesh.SetVertexBufferParams(vertexCount, RawVertexLayout);
                mesh.SetVertexBufferData(interleavedVertices, 0, 0, vertexCount * RawFloatsPerVertex, 0, Fast);

                if (vertexCount > ushort.MaxValue)
                {
                    EnsureSequential32(vertexCount);
                    mesh.SetIndexBufferParams(vertexCount, IndexFormat.UInt32);
                    mesh.SetIndexBufferData(sequentialIndices32, 0, 0, vertexCount, Fast);
                }
                else
                {
                    EnsureSequential16(vertexCount);
                    mesh.SetIndexBufferParams(vertexCount, IndexFormat.UInt16);
                    mesh.SetIndexBufferData(sequentialIndices16, 0, 0, vertexCount, Fast);
                }

                mesh.subMeshCount = 1;
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, vertexCount), Fast);
                mesh.bounds = localBounds;
            }

            meshRenderer.enabled = vertexCount > 0;
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

        static void EnsureSequential16(int count)
        {
            if (sequentialIndices16 != null && sequentialIndices16.Length >= count)
                return;
            int size = Mathf.NextPowerOfTwo(count);
            sequentialIndices16 = new ushort[size];
            for (int i = 0; i < size; i++) sequentialIndices16[i] = (ushort)i;
        }

        static void EnsureSequential32(int count)
        {
            if (sequentialIndices32 != null && sequentialIndices32.Length >= count)
                return;
            int size = Mathf.NextPowerOfTwo(count);
            sequentialIndices32 = new int[size];
            for (int i = 0; i < size; i++) sequentialIndices32[i] = i;
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
            var block = new MaterialPropertyBlock();
            if (visible)
            {
                var tint = LodTints[Mathf.Min(Key.Lod, LodTints.Length - 1)];
                block.SetColor(BaseColorId, tint);
                block.SetColor(ColorId, tint);
            }
            meshRenderer.SetPropertyBlock(block);
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
        /// physics bake still reads the mesh on another thread. The view itself can then be
        /// pooled; a fresh mesh is created on its next apply.
        /// </summary>
        public Mesh DetachMesh()
        {
            var kept = mesh;
            mesh = null;
            if (meshFilter != null)
                meshFilter.sharedMesh = null;
            if (meshCollider != null)
                meshCollider.sharedMesh = null;
            return kept;
        }
    }
}
