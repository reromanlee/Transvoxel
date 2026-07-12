using reromanlee.Transvoxel.Density;
using UnityEngine;

namespace reromanlee.Transvoxel.Meshing
{
    /// <summary>
    /// All density samples one chunk build needs, fetched up front on the worker thread.
    ///
    /// Values are stored as signed distances d = isoLevel - density, matching the sign
    /// convention of Lengyel's tables: negative = inside solid ground, positive = air,
    /// zero = exactly on the surface (classified as air).
    ///
    /// The main grid has (cells + 3)³ points: the (cells + 1)³ cell corners plus one
    /// extra layer before the minimum boundary and two after the maximum, exactly the
    /// volume of Figure 3.7 in the paper, so central-difference normals never read out
    /// of range. Points sit every 2^lod voxels — a coarse chunk reads only the lattice
    /// points it renders, never the fine data in between (Concept.txt #5).
    ///
    /// For every face that will carry transition cells, a (2·cells + 1)² sheet of samples
    /// at half stride (the neighbour's finer lattice) is fetched as well.
    /// </summary>
    public sealed class ChunkSamples
    {
        public NodeKey Key;
        public int ChunkCells;
        public byte TransitionMask;
        public float IsoLevel;

        /// <summary>Points per axis of the main grid: ChunkCells + 3.</summary>
        public int PointsPerAxis;

        /// <summary>Voxel stride between grid points: 1 &lt;&lt; lod.</summary>
        public int LodStep;

        /// <summary>Chunk minimum corner on the LOD0 voxel lattice.</summary>
        public Vector3Int MinVoxel;

        /// <summary>Main grid, index = x + y·P + z·P². Grid point (i,j,k) sits at chunk-local voxel (i-1)·LodStep.</summary>
        public float[] Main;

        /// <summary>Per <see cref="CubeFace"/>: fine-lattice sheet for transition cells, or null. Index = u + v·(2·cells+1).</summary>
        public readonly float[][] FaceSheets = new float[6][];

        public float MainAt(int x, int y, int z) => Main[x + PointsPerAxis * (y + PointsPerAxis * z)];

        /// <summary>Payload bytes of this grid (main grid + sampled face sheets), for stats.</summary>
        public long EstimateBytes()
        {
            long floats = Main.Length;
            foreach (float[] sheet in FaceSheets)
            {
                if (sheet != null)
                    floats += sheet.Length;
            }
            return floats * sizeof(float);
        }

        /// <summary>
        /// Central-difference gradient of the signed distance at a cell corner (grid point).
        /// Points from solid towards air, i.e. along the outward surface normal. Unnormalized;
        /// the uniform 2·LodStep denominator is dropped because callers normalize.
        /// </summary>
        public Vector3 GradientAt(int x, int y, int z)
        {
            return new Vector3(
                MainAt(x + 1, y, z) - MainAt(x - 1, y, z),
                MainAt(x, y + 1, z) - MainAt(x, y - 1, z),
                MainAt(x, y, z + 1) - MainAt(x, y, z - 1));
        }

        public static float SampleSigned(IDensitySource source, float isoLevel, int x, int y, int z)
            => isoLevel - source.SampleVoxel(x, y, z);

        public static ChunkSamples Sample(IDensitySource source, NodeKey key, int chunkCells, float isoLevel,
            byte transitionMask)
        {
            int n = chunkCells;
            int p = n + 3;
            int step = 1 << key.Lod;
            var samples = new ChunkSamples
            {
                Key = key,
                ChunkCells = n,
                TransitionMask = transitionMask,
                IsoLevel = isoLevel,
                PointsPerAxis = p,
                LodStep = step,
                MinVoxel = key.MinVoxel(chunkCells),
                Main = new float[p * p * p],
            };
            var min = samples.MinVoxel;

            // y is the innermost loop so that height-field style density sources see one
            // (x, z) column at a time and can reuse their 2D noise per column.
            for (int z = 0; z < p; z++)
            for (int x = 0; x < p; x++)
            {
                int vx = min.x + (x - 1) * step;
                int vz = min.z + (z - 1) * step;
                int rowIndex = x + p * p * z;
                for (int y = 0; y < p; y++)
                {
                    int vy = min.y + (y - 1) * step;
                    samples.Main[rowIndex + p * y] = isoLevel - source.SampleVoxel(vx, vy, vz);
                }
            }

            if (transitionMask != 0 && key.Lod > 0)
            {
                for (int f = 0; f < 6; f++)
                {
                    if ((transitionMask & (1 << f)) != 0)
                        samples.FaceSheets[f] = SampleFaceSheet(source, samples, (CubeFace)f);
                }
            }

            return samples;
        }

        /// <summary>
        /// A view of this grid carrying <em>exactly</em> the given transition mask. The base
        /// grid (<see cref="Main"/>) is mask-independent and shared as-is; only the per-face
        /// sheets differ, so we reuse any this grid already sampled and fetch the rest.
        ///
        /// This is what keeps LOD seams closed as the viewer moves: the secondary-position
        /// shift and the transition cells are both driven by <see cref="TransitionMask"/>, so
        /// that value must match the faces actually being generated for this build — never a
        /// stale superset left in the cache from when the chunk had different neighbours.
        /// </summary>
        public ChunkSamples WithMask(IDensitySource source, byte mask)
        {
            if (mask == TransitionMask)
                return this;

            var view = new ChunkSamples
            {
                Key = Key,
                ChunkCells = ChunkCells,
                TransitionMask = mask,
                IsoLevel = IsoLevel,
                PointsPerAxis = PointsPerAxis,
                LodStep = LodStep,
                MinVoxel = MinVoxel,
                Main = Main, // shared, read-only during meshing
            };

            if (mask != 0 && Key.Lod > 0)
            {
                for (int f = 0; f < 6; f++)
                {
                    if ((mask & (1 << f)) == 0)
                        continue;
                    view.FaceSheets[f] = FaceSheets[f] ?? SampleFaceSheet(source, view, (CubeFace)f);
                }
            }

            return view;
        }

        /// <summary>
        /// Samples the fine (half-stride) lattice covering one chunk face — the lattice the
        /// finer neighbour meshes with, so transition cells line up with it bit-exactly.
        /// </summary>
        static float[] SampleFaceSheet(IDensitySource source, ChunkSamples samples, CubeFace face)
        {
            int n = samples.ChunkCells;
            int fineStep = samples.LodStep >> 1;
            int pointsPerAxis = 2 * n + 1;
            var sheet = new float[pointsPerAxis * pointsPerAxis];

            int axisN = face.Axis();
            int axisU = axisN == 0 ? 1 : 0;
            int axisV = axisN == 2 ? 1 : 2;
            int planeLocal = face.IsPositive() ? n * samples.LodStep : 0;

            var voxel = new int[3];
            voxel[axisN] = Axis(samples.MinVoxel, axisN) + planeLocal;

            for (int v = 0; v < pointsPerAxis; v++)
            {
                voxel[axisV] = Axis(samples.MinVoxel, axisV) + v * fineStep;
                for (int u = 0; u < pointsPerAxis; u++)
                {
                    voxel[axisU] = Axis(samples.MinVoxel, axisU) + u * fineStep;
                    sheet[u + v * pointsPerAxis] =
                        samples.IsoLevel - source.SampleVoxel(voxel[0], voxel[1], voxel[2]);
                }
            }

            return sheet;
        }

        static int Axis(Vector3Int v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;
    }
}
