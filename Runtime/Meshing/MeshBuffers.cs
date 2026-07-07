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

        public bool IsEmpty => Indices.Count == 0;

        public void Clear()
        {
            Vertices.Clear();
            Normals.Clear();
            Uvs.Clear();
            Indices.Clear();
        }

        /// <summary>
        /// Rewrites the buffers so that every triangle has its own three vertices with a
        /// flat face normal — the "smooth off" mode of Concept.txt #6. Positions are kept
        /// bit-identical, so chunk borders still line up perfectly; only shading changes.
        /// </summary>
        public void ConvertToFlatShaded()
        {
            int triCount = Indices.Count / 3;
            var v = new List<Vector3>(triCount * 3);
            var n = new List<Vector3>(triCount * 3);
            var uv = new List<Vector2>(triCount * 3);

            for (int t = 0; t < triCount; t++)
            {
                int i0 = Indices[t * 3], i1 = Indices[t * 3 + 1], i2 = Indices[t * 3 + 2];
                Vector3 p0 = Vertices[i0], p1 = Vertices[i1], p2 = Vertices[i2];
                Vector3 faceNormal = Vector3.Cross(p1 - p0, p2 - p0).normalized;

                v.Add(p0); v.Add(p1); v.Add(p2);
                n.Add(faceNormal); n.Add(faceNormal); n.Add(faceNormal);
                uv.Add(Uvs[i0]); uv.Add(Uvs[i1]); uv.Add(Uvs[i2]);
            }

            Vertices.Clear(); Vertices.AddRange(v);
            Normals.Clear(); Normals.AddRange(n);
            Uvs.Clear(); Uvs.AddRange(uv);
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
