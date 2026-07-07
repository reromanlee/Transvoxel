using System.Collections.Generic;
using reromanlee.Transvoxel.Meshing;
using UnityEngine;
using UnityEngine.Rendering;

namespace reromanlee.Transvoxel
{
    /// <summary>
    /// The scene-side view of one live chunk: a GameObject with mesh components under the
    /// terrain root. Purely presentation — all decisions happen in <see cref="TransvoxelTerrain"/>.
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

        public NodeKey Key { get; }
        public byte TransitionMask { get; set; }
        public int VertexCount { get; private set; }

        readonly GameObject gameObject;
        readonly MeshFilter meshFilter;
        readonly MeshRenderer meshRenderer;
        MeshCollider meshCollider;
        Mesh mesh;

        public TerrainChunk(NodeKey key, Transform parent, Material material, float voxelSize, int chunkCells)
        {
            Key = key;
            gameObject = new GameObject($"Chunk {key}");
            gameObject.transform.SetParent(parent, false);
            var min = key.MinVoxel(chunkCells);
            gameObject.transform.localPosition = new Vector3(min.x, min.y, min.z) * voxelSize;

            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
        }

        /// <summary>Uploads freshly built buffers into this chunk's mesh (main thread only).</summary>
        public void Apply(MeshBuffers buffers, bool colorizeLod)
        {
            if (mesh == null)
            {
                mesh = new Mesh { name = $"Transvoxel {Key}" };
                mesh.MarkDynamic();
                meshFilter.sharedMesh = mesh;
            }

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

        /// <summary>Attaches the physics shape once its bake (done off-thread) has finished.</summary>
        public void AttachBakedCollider()
        {
            if (gameObject == null || mesh == null || mesh.vertexCount == 0)
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
        /// Destroys the scene object but hands the mesh back instead of destroying it —
        /// used when a physics bake still reads the mesh on another thread.
        /// </summary>
        public Mesh DestroyKeepMesh()
        {
            var kept = mesh;
            mesh = null;
            if (gameObject != null)
                Object.Destroy(gameObject);
            return kept;
        }
    }
}
