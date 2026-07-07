using UnityEngine;

namespace reromanlee.Transvoxel.Meshing
{
    /// <summary>
    /// Geometry rules shared by the regular-cell and transition-cell meshers. They live in
    /// one place because both meshers must apply them bit-identically: a regular boundary
    /// vertex and the matching transition-cell vertex only stay glued together if they run
    /// through exactly the same arithmetic.
    /// </summary>
    public static class TransvoxelGeometry
    {
        /// <summary>
        /// Width of the transition cell layer as a fraction of a (coarse) cell.
        /// The paper uses w(k) = 2^(k-2) voxels = ¼ cell (Section 4.4); Figure 4.11 describes
        /// the same value as "half the size of the adjacent full-resolution cell".
        /// </summary>
        public const float TransitionWidthCells = 0.25f;

        /// <summary>
        /// Whether to swap two indices of every emitted triangle.
        ///
        /// The Transvoxel tables, fed our sign convention (negative = inside solid), already
        /// wind triangles so that Cross(v1-v0, v2-v0) points along the outward density
        /// gradient — which is exactly Unity's front-face convention (a clockwise-in-screen
        /// front face has its cross product pointing toward the viewer). So no flip is
        /// needed. Verified by the headless signed-volume test: a solid sphere comes out
        /// with positive enclosed volume and face normals agreeing with the smooth normals.
        /// </summary>
        public const bool FlipWinding = false;

        /// <summary>
        /// Moves a chunk-boundary vertex to its "secondary position" to make room for
        /// transition cells (paper Section 4.4, Equations 4.2 and 4.3).
        ///
        /// A vertex is "near" a face when it lies inside the one-cell-thick boundary layer
        /// (vertices exactly on the face count). The shift is applied only when every face
        /// the vertex is near carries transition cells; if any of them borders a same-LOD
        /// (or coarser) chunk the vertex must stay put, because the neighbour's copy of
        /// that vertex will not move either (this is the primary/secondary selection rule
        /// of Figure 4.13). The offset is then projected onto the tangent plane of the
        /// surface so the shift slides along the terrain instead of denting it (Eq. 4.3).
        /// </summary>
        /// <param name="position">Vertex position in chunk-local voxel units.</param>
        /// <param name="normal">Unit surface normal at the vertex (from the density gradient).</param>
        /// <param name="transitionMask">Bit per <see cref="CubeFace"/> that carries transition cells.</param>
        /// <param name="chunkCells">Cells per chunk axis.</param>
        /// <param name="lodStep">Voxel stride of one cell: 1 &lt;&lt; lod.</param>
        public static Vector3 ApplySecondaryShift(Vector3 position, Vector3 normal, byte transitionMask,
            int chunkCells, int lodStep)
        {
            if (transitionMask == 0)
                return position;

            float cell = lodStep;
            float max = chunkCells * cell;
            Vector3 delta = Vector3.zero;
            bool anyNear = false;

            for (int axis = 0; axis < 3; axis++)
            {
                float p = position[axis];
                if (p < cell)
                {
                    // Near the negative face.
                    anyNear = true;
                    if ((transitionMask & (1 << (axis * 2))) == 0)
                        return position; // that face has no transition -> keep primary position
                    delta[axis] = TransitionWidthCells * (cell - p); // shift inward (+axis)
                }
                else if (p > max - cell)
                {
                    // Near the positive face.
                    anyNear = true;
                    if ((transitionMask & (1 << (axis * 2 + 1))) == 0)
                        return position;
                    delta[axis] = TransitionWidthCells * (max - cell - p); // shift inward (-axis)
                }
            }

            if (!anyNear)
                return position;

            // Eq. 4.3: project the offset onto the tangent plane of the surface.
            return position + (delta - normal * Vector3.Dot(normal, delta));
        }

        /// <summary>
        /// True when a triangle has (numerically) zero area. Cells with corner samples that
        /// sit exactly on the isosurface legally produce such triangles (Section 3.3), and
        /// they are dropped here.
        /// </summary>
        public static bool IsDegenerate(Vector3 a, Vector3 b, Vector3 c)
        {
            return Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-12f;
        }

        /// <summary>Appends a triangle honouring <see cref="FlipWinding"/> and an extra per-call flip.</summary>
        public static void EmitTriangle(System.Collections.Generic.List<int> indices, int i0, int i1, int i2, bool flip)
        {
            if (FlipWinding ^ flip)
            {
                indices.Add(i0);
                indices.Add(i2);
                indices.Add(i1);
            }
            else
            {
                indices.Add(i0);
                indices.Add(i1);
                indices.Add(i2);
            }
        }
    }
}
