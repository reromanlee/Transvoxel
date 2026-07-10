using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace reromanlee.Transvoxel.Gpu
{
    /// <summary>
    /// Welds the GPU's triangle soup (3 unique vertices per triangle — cells on the GPU
    /// cannot share the paper's serial reuse decks) back into an indexed mesh on a worker
    /// thread. Coincident vertices produced by neighbouring cells are bit-identical (same
    /// instructions, same inputs), so welding is an exact hash over the raw float bits —
    /// no epsilon, no false merges. The result renders exactly like a CPU-meshed chunk:
    /// roughly a third of the vertices, vertex-cache-friendly indices, smaller uploads.
    /// Flat-shaded soups weld only within a face (normals differ across faces), which is
    /// precisely what flat shading needs.
    ///
    /// Instances hold reusable scratch; rent them via <see cref="Rent"/>/<see cref="Return"/>
    /// like the CPU mesher pool.
    /// </summary>
    public sealed class GpuMeshWelder
    {
        /// <summary>Interleaved floats per soup vertex: position, normal, uv.</summary>
        public const int FloatsPerVertex = 8;

        readonly struct VertexKey : IEquatable<VertexKey>
        {
            readonly int p0, p1, p2, n0, n1, n2, u0, u1;

            public VertexKey(float[] soup, int floatIndex)
            {
                p0 = BitConverter.SingleToInt32Bits(soup[floatIndex]);
                p1 = BitConverter.SingleToInt32Bits(soup[floatIndex + 1]);
                p2 = BitConverter.SingleToInt32Bits(soup[floatIndex + 2]);
                n0 = BitConverter.SingleToInt32Bits(soup[floatIndex + 3]);
                n1 = BitConverter.SingleToInt32Bits(soup[floatIndex + 4]);
                n2 = BitConverter.SingleToInt32Bits(soup[floatIndex + 5]);
                u0 = BitConverter.SingleToInt32Bits(soup[floatIndex + 6]);
                u1 = BitConverter.SingleToInt32Bits(soup[floatIndex + 7]);
            }

            public bool Equals(VertexKey other) =>
                p0 == other.p0 && p1 == other.p1 && p2 == other.p2
                && n0 == other.n0 && n1 == other.n1 && n2 == other.n2
                && u0 == other.u0 && u1 == other.u1;

            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = p0;
                    h = h * 486187739 + p1;
                    h = h * 486187739 + p2;
                    h = h * 486187739 + n0;
                    h = h * 486187739 + n1;
                    h = h * 486187739 + n2;
                    h = h * 486187739 + u0;
                    h = h * 486187739 + u1;
                    return h;
                }
            }
        }

        readonly Dictionary<VertexKey, int> vertexIds = new Dictionary<VertexKey, int>(4096);

        /// <summary>
        /// Welds <paramref name="triangleCount"/> triangles of interleaved soup into indexed
        /// buffers. Winding and vertex data are preserved verbatim.
        /// </summary>
        public void Weld(float[] soup, int triangleCount, Meshing.MeshBuffers output)
        {
            vertexIds.Clear();
            int totalVertices = triangleCount * 3;

            for (int v = 0; v < totalVertices; v++)
            {
                int f = v * FloatsPerVertex;
                var key = new VertexKey(soup, f);
                if (!vertexIds.TryGetValue(key, out int index))
                {
                    index = output.Vertices.Count;
                    vertexIds.Add(key, index);
                    output.Vertices.Add(new Vector3(soup[f], soup[f + 1], soup[f + 2]));
                    output.Normals.Add(new Vector3(soup[f + 3], soup[f + 4], soup[f + 5]));
                    output.Uvs.Add(new Vector2(soup[f + 6], soup[f + 7]));
                }
                output.Indices.Add(index);
            }
        }

        // ---- pooling (welding runs on worker tasks; one instance per concurrent weld) ----

        static readonly ConcurrentBag<GpuMeshWelder> Pool = new ConcurrentBag<GpuMeshWelder>();

        public static GpuMeshWelder Rent() => Pool.TryTake(out var welder) ? welder : new GpuMeshWelder();

        public static void Return(GpuMeshWelder welder) => Pool.Add(welder);
    }
}
