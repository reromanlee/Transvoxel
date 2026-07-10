using System;

namespace reromanlee.Transvoxel.Gpu
{
    /// <summary>
    /// Lengyel's lookup tables flattened into plain int arrays with fixed strides, ready to
    /// upload as StructuredBuffers. The tables are far too large to hard-code inside a
    /// compute shader (HLSL initializer limits), so the GPU reads them from buffers instead —
    /// uploaded once per pipeline, identical data to <see cref="TransvoxelDataTables"/>.
    /// </summary>
    public static class TransvoxelGpuTables
    {
        /// <summary>Max vertex codes per case; both vertex-data tables fit in this stride.</summary>
        public const int VertexDataStride = 12;

        /// <summary>Ints per regular cell class entry in <see cref="RegularCellIndices"/>.</summary>
        public const int RegularIndexStride = 15;

        /// <summary>Ints per transition cell class entry in <see cref="TransitionCellIndices"/>.</summary>
        public const int TransitionIndexStride = 36;

        public static readonly int[] RegularCellClass;        // 256
        public static readonly int[] RegularCellCounts;       // 16 (geometryCounts)
        public static readonly int[] RegularCellIndices;      // 16 * 15
        public static readonly int[] RegularVertexData;       // 256 * 12

        public static readonly int[] TransitionCellClass;     // 512 (bit 7 = inverted winding)
        public static readonly int[] TransitionCellCounts;    // 56 (geometryCounts)
        public static readonly int[] TransitionCellIndices;   // 56 * 36
        public static readonly int[] TransitionVertexData;    // 512 * 12

        static TransvoxelGpuTables()
        {
            RegularCellClass = TransvoxelDataTables.regularCellClass;
            TransitionCellClass = TransvoxelDataTables.transitionCellClass;

            var regular = TransvoxelDataTables.regularCellData;
            RegularCellCounts = new int[regular.Length];
            RegularCellIndices = new int[regular.Length * RegularIndexStride];
            for (int c = 0; c < regular.Length; c++)
            {
                RegularCellCounts[c] = regular[c].geometryCounts;
                Array.Copy(regular[c].vertexIndex, 0, RegularCellIndices, c * RegularIndexStride,
                    RegularIndexStride);
            }

            var transition = TransvoxelDataTables.transitionCellData;
            TransitionCellCounts = new int[transition.Length];
            TransitionCellIndices = new int[transition.Length * TransitionIndexStride];
            for (int c = 0; c < transition.Length; c++)
            {
                TransitionCellCounts[c] = transition[c].geometryCounts;
                Array.Copy(transition[c].vertexIndex, 0, TransitionCellIndices, c * TransitionIndexStride,
                    TransitionIndexStride);
            }

            RegularVertexData = FlattenRagged(TransvoxelDataTables.regularVertexData, VertexDataStride);
            TransitionVertexData = FlattenRagged(TransvoxelDataTables.transitionVertexData, VertexDataStride);
        }

        static int[] FlattenRagged(int[][] ragged, int stride)
        {
            var flat = new int[ragged.Length * stride];
            for (int row = 0; row < ragged.Length; row++)
            {
                int[] source = ragged[row];
                if (source == null)
                    continue;
                if (source.Length > stride)
                    throw new InvalidOperationException(
                        $"Transvoxel table row {row} has {source.Length} entries, stride is {stride}.");
                Array.Copy(source, 0, flat, row * stride, source.Length);
            }
            return flat;
        }
    }
}
