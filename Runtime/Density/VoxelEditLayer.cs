using System.Collections.Generic;
using UnityEngine;

namespace reromanlee.Transvoxel.Density
{
    /// <summary>
    /// Layer A of the terrain: sparse storage for every voxel the player has changed
    /// (Concept.txt #2). Voxels are grouped into 16³ bricks held in a dictionary, so
    /// memory scales with the amount of terraforming, not the world size.
    ///
    /// Thread-safety is copy-on-write: readers grab the current brick dictionary
    /// reference and read immutable arrays with no locks (meshing threads do tens of
    /// thousands of lookups per chunk), while writers clone the touched bricks plus the
    /// dictionary and publish the new snapshot with a single volatile store. Writes are
    /// rare (one brush stroke) so the cloning cost is irrelevant.
    /// </summary>
    public sealed class VoxelEditLayer
    {
        public const int BrickBits = 4;
        public const int BrickSize = 1 << BrickBits;          // 16
        public const int BrickMask = BrickSize - 1;
        public const int BrickVolume = BrickSize * BrickSize * BrickSize;

        /// <summary>Marks "no edit here" inside a brick.</summary>
        const float NoEdit = float.NaN;

        volatile Dictionary<Vector3Int, float[]> bricks = new Dictionary<Vector3Int, float[]>();

        public int BrickCount => bricks.Count;

        /// <summary>True (with the stored density) if the player has edited this voxel.</summary>
        public bool TryGetVoxel(int x, int y, int z, out float value)
        {
            var snapshot = bricks;
            if (snapshot.Count == 0)
            {
                value = 0f;
                return false;
            }

            var brickCoord = new Vector3Int(x >> BrickBits, y >> BrickBits, z >> BrickBits);
            if (!snapshot.TryGetValue(brickCoord, out float[] brick))
            {
                value = 0f;
                return false;
            }

            float v = brick[Index(x, y, z)];
            value = v;
            return !float.IsNaN(v);
        }

        /// <summary>
        /// Applies a batch of voxel writes as one atomic snapshot swap.
        /// The writer callback receives (setVoxel) and performs all its writes through it.
        /// Call from one thread at a time (the main thread in practice).
        /// </summary>
        public void WriteBatch(System.Action<System.Action<int, int, int, float>> writer)
        {
            var next = new Dictionary<Vector3Int, float[]>(bricks);
            var cloned = new HashSet<Vector3Int>();

            void Set(int x, int y, int z, float value)
            {
                var bc = new Vector3Int(x >> BrickBits, y >> BrickBits, z >> BrickBits);
                if (!next.TryGetValue(bc, out float[] brick))
                {
                    brick = NewBrick();
                    next[bc] = brick;
                    cloned.Add(bc);
                }
                else if (cloned.Add(bc))
                {
                    // First touch of a shared brick in this batch: clone before writing
                    // so threads reading the previous snapshot are undisturbed.
                    brick = (float[])brick.Clone();
                    next[bc] = brick;
                }
                next[bc][Index(x, y, z)] = value;
            }

            writer(Set);
            bricks = next;
        }

        public void Clear() => bricks = new Dictionary<Vector3Int, float[]>();

        /// <summary>Collects every brick in the layer (initial upload of the GPU brick pool).</summary>
        public void CollectAllBricks(List<(Vector3Int coord, float[] data)> results)
        {
            results.Clear();
            foreach (var entry in bricks)
                results.Add((entry.Key, entry.Value));
        }

        /// <summary>
        /// Collects every brick that overlaps the given voxel region, for upload to the GPU
        /// density kernel. The returned arrays are the immutable copy-on-write snapshot
        /// bricks — safe to read (never mutated), must not be written. Unedited voxels hold
        /// <see cref="float.NaN"/>; convert to the GPU sentinel while packing.
        /// </summary>
        public void CollectBricks(BoundsInt voxelBounds, List<(Vector3Int coord, float[] data)> results)
        {
            results.Clear();
            var snapshot = bricks;
            if (snapshot.Count == 0)
                return;

            var minBrick = new Vector3Int(voxelBounds.xMin >> BrickBits, voxelBounds.yMin >> BrickBits,
                voxelBounds.zMin >> BrickBits);
            var maxBrick = new Vector3Int((voxelBounds.xMax - 1) >> BrickBits, (voxelBounds.yMax - 1) >> BrickBits,
                (voxelBounds.zMax - 1) >> BrickBits);

            long candidates = (long)(maxBrick.x - minBrick.x + 1)
                              * (maxBrick.y - minBrick.y + 1)
                              * (maxBrick.z - minBrick.z + 1);
            if (candidates <= snapshot.Count)
            {
                for (int z = minBrick.z; z <= maxBrick.z; z++)
                for (int y = minBrick.y; y <= maxBrick.y; y++)
                for (int x = minBrick.x; x <= maxBrick.x; x++)
                {
                    var coord = new Vector3Int(x, y, z);
                    if (snapshot.TryGetValue(coord, out float[] brick))
                        results.Add((coord, brick));
                }
            }
            else
            {
                foreach (var entry in snapshot)
                {
                    var c = entry.Key;
                    if (c.x >= minBrick.x && c.x <= maxBrick.x
                        && c.y >= minBrick.y && c.y <= maxBrick.y
                        && c.z >= minBrick.z && c.z <= maxBrick.z)
                        results.Add((c, entry.Value));
                }
            }
        }

        static float[] NewBrick()
        {
            var brick = new float[BrickVolume];
            for (int i = 0; i < BrickVolume; i++) brick[i] = NoEdit;
            return brick;
        }

        static int Index(int x, int y, int z) =>
            (x & BrickMask) | ((y & BrickMask) << BrickBits) | ((z & BrickMask) << (2 * BrickBits));
    }
}
