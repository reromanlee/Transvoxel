using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace reromanlee.Transvoxel.Meshing
{
    /// <summary>
    /// Turns the per-vertex material ids sampled by the meshers into the blend attribute
    /// the terrain shader reads, run as the last worker-side step of every build (CPU
    /// meshers and GPU weld alike).
    ///
    /// Material ids cannot be interpolated across a triangle, so each triangle instead
    /// carries its own id set: every one of its vertices stores the SAME sorted id triple
    /// in the color's rgb and its own place in that triple in alpha
    /// (<c>Color32(idA, idB, idC, corner)</c>). The vertex shader expands alpha into a
    /// one-hot weight that rasterization interpolates into exact barycentric weights, and
    /// the fragment shader blends the three palette layers with those weights — sharpened
    /// by the live materialBlendSharpness setting.
    ///
    /// A vertex shared by triangles that disagree on the id triple is split. That happens
    /// only along material boundaries; the interior of a uniform region — and the entire
    /// mesh of an unpainted chunk — shares every vertex exactly as before. Duplicate ids
    /// inside a triple are harmless (the weights of equal ids add up), so corners map to
    /// the FIRST occurrence of their id, which maximizes sharing along boundaries too.
    ///
    /// Not thread-safe across concurrent calls — rent one instance per weld/build, like
    /// the mesher pools.
    /// </summary>
    public sealed class MaterialBlendEncoder
    {
        const byte Unassigned = byte.MaxValue; // alpha sentinel; real corners are 0..2

        // (source vertex, id triple) -> split vertex index, valid for one Encode call.
        readonly Dictionary<long, int> splitVertices = new Dictionary<long, int>(256);

        /// <summary>
        /// Fills <see cref="MeshBuffers.MaterialBlend"/> from
        /// <see cref="MeshBuffers.MaterialIds"/> and the index buffer, splitting boundary
        /// vertices as needed. No-op when the build carried no material ids.
        /// </summary>
        public void Encode(MeshBuffers buffers)
        {
            List<byte> ids = buffers.MaterialIds;
            List<Color32> blend = buffers.MaterialBlend;
            blend.Clear();
            if (ids.Count == 0)
                return;

            if (AllSameId(ids, out byte uniform))
            {
                var constant = new Color32(uniform, uniform, uniform, 0);
                for (int i = 0; i < ids.Count; i++)
                    blend.Add(constant);
                return;
            }

            var pending = new Color32(0, 0, 0, Unassigned);
            for (int i = 0; i < ids.Count; i++)
                blend.Add(pending);
            splitVertices.Clear();

            List<int> indices = buffers.Indices;
            for (int t = 0; t < indices.Count; t += 3)
            {
                byte a = ids[indices[t]], b = ids[indices[t + 1]], c = ids[indices[t + 2]];
                if (a > b) (a, b) = (b, a);
                if (b > c) (b, c) = (c, b);
                if (a > b) (a, b) = (b, a);

                for (int k = 0; k < 3; k++)
                {
                    int vertex = indices[t + k];
                    byte own = ids[vertex];
                    byte corner = own == a ? (byte)0 : own == b ? (byte)1 : (byte)2;
                    var desired = new Color32(a, b, c, corner);

                    Color32 current = blend[vertex];
                    if (current.a == Unassigned)
                        blend[vertex] = desired;
                    else if (current.r != desired.r || current.g != desired.g || current.b != desired.b)
                        indices[t + k] = SplitVertex(buffers, vertex, desired);
                    // Same triple implies the same corner (it is a function of the triple
                    // and the vertex's own id), so nothing to do on a match.
                }
            }

            // Vertices orphaned by degenerate-triangle culling are never rasterized; give
            // them a valid single-id attribute instead of the sentinel anyway.
            for (int i = 0; i < blend.Count; i++)
            {
                if (blend[i].a == Unassigned)
                {
                    byte own = ids[i];
                    blend[i] = new Color32(own, own, own, 0);
                }
            }
        }

        static bool AllSameId(List<byte> ids, out byte uniform)
        {
            uniform = ids[0];
            for (int i = 1; i < ids.Count; i++)
            {
                if (ids[i] != uniform)
                    return false;
            }
            return true;
        }

        int SplitVertex(MeshBuffers buffers, int source, Color32 desired)
        {
            long key = ((long)source << 24)
                       | ((long)desired.r << 16) | ((long)desired.g << 8) | desired.b;
            if (splitVertices.TryGetValue(key, out int existing))
                return existing;

            int index = buffers.Vertices.Count;
            buffers.Vertices.Add(buffers.Vertices[source]);
            buffers.Normals.Add(buffers.Normals[source]);
            buffers.Uvs.Add(buffers.Uvs[source]);
            buffers.MaterialIds.Add(buffers.MaterialIds[source]);
            buffers.MaterialBlend.Add(desired);
            splitVertices.Add(key, index);
            return index;
        }

        // ---- pooling (encoding runs on worker threads; one instance per build) ----

        static readonly ConcurrentBag<MaterialBlendEncoder> Pool = new ConcurrentBag<MaterialBlendEncoder>();

        public static MaterialBlendEncoder Rent() =>
            Pool.TryTake(out var encoder) ? encoder : new MaterialBlendEncoder();

        public static void Return(MaterialBlendEncoder encoder) => Pool.Add(encoder);
    }
}
