using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace reromanlee.Transvoxel.Meshing
{
    /// <summary>
    /// Plain CPU-side mesh data produced by the meshers on worker threads and applied
    /// to a UnityEngine.Mesh on the main thread. Pooled because chunk churn while the
    /// player moves would otherwise allocate large lists every frame.
    /// </summary>
    public sealed class MeshBuffers
    {
        public readonly List<Vector3> Vertices = new List<Vector3>(1024);
        public readonly List<Vector3> Normals = new List<Vector3>(1024);
        public readonly List<Vector2> Uvs = new List<Vector2>(1024);
        public readonly List<int> Indices = new List<int>(4096);

        /// <summary>
        /// Material id per vertex (empty when the terrain has no material palette): the
        /// palette layer of the solid voxel this vertex hugs. Meshing intermediate —
        /// <see cref="MaterialBlendEncoder"/> turns it into <see cref="MaterialBlend"/>,
        /// the per-vertex blend attribute the shader reads.
        /// </summary>
        public readonly List<byte> MaterialIds = new List<byte>(1024);

        /// <summary>
        /// Per-vertex material blend data uploaded as the mesh color channel:
        /// rgb = the triangle's (up to) three material ids, a = which of them this vertex
        /// is — see <see cref="MaterialBlendEncoder"/>. Empty = no palette, no upload.
        /// </summary>
        public readonly List<Color32> MaterialBlend = new List<Color32>(1024);

        public bool IsEmpty => Indices.Count == 0;

        public void Clear()
        {
            Vertices.Clear();
            Normals.Clear();
            Uvs.Clear();
            Indices.Clear();
            MaterialIds.Clear();
            MaterialBlend.Clear();
        }

        /// <summary>
        /// Rewrites the buffers so that every triangle has its own three vertices with a
        /// flat face normal — the "smooth off" mode of Concept.txt #6. Positions are kept
        /// bit-identical, so chunk borders still line up perfectly; only shading changes.
        /// </summary>
        public void ConvertToFlatShaded()
        {
            int triCount = Indices.Count / 3;
            bool hasMaterials = MaterialIds.Count > 0;
            var v = new List<Vector3>(triCount * 3);
            var n = new List<Vector3>(triCount * 3);
            var uv = new List<Vector2>(triCount * 3);
            var ids = hasMaterials ? new List<byte>(triCount * 3) : null;

            for (int t = 0; t < triCount; t++)
            {
                int i0 = Indices[t * 3], i1 = Indices[t * 3 + 1], i2 = Indices[t * 3 + 2];
                Vector3 p0 = Vertices[i0], p1 = Vertices[i1], p2 = Vertices[i2];
                Vector3 faceNormal = Vector3.Cross(p1 - p0, p2 - p0).normalized;

                v.Add(p0); v.Add(p1); v.Add(p2);
                n.Add(faceNormal); n.Add(faceNormal); n.Add(faceNormal);
                uv.Add(Uvs[i0]); uv.Add(Uvs[i1]); uv.Add(Uvs[i2]);
                if (hasMaterials)
                {
                    ids.Add(MaterialIds[i0]); ids.Add(MaterialIds[i1]); ids.Add(MaterialIds[i2]);
                }
            }

            Vertices.Clear(); Vertices.AddRange(v);
            Normals.Clear(); Normals.AddRange(n);
            Uvs.Clear(); Uvs.AddRange(uv);
            MaterialIds.Clear();
            if (hasMaterials) MaterialIds.AddRange(ids);
            Indices.Clear();
            for (int i = 0; i < triCount * 3; i++) Indices.Add(i);
        }

        // ---- pooling ----

        static readonly ConcurrentBag<MeshBuffers> Pool = new ConcurrentBag<MeshBuffers>();

        public static MeshBuffers Rent() => Pool.TryTake(out var buffers) ? buffers : new MeshBuffers();

        public static void Return(MeshBuffers buffers)
        {
            buffers.Clear();
            Pool.Add(buffers);
        }
    }
}
