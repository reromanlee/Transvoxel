using System.Collections.Generic;
using UnityEngine;

namespace reromanlee.Transvoxel.Density
{
    /// <summary>
    /// Sparse storage for voxel material ids: which palette entry each voxel is made of.
    /// The whole world defaults to material 0, so only voxels painted with something else
    /// ever cost memory — a brick of ids is 4 KB next to the 16 KB density brick that a
    /// terraform stroke on the same region creates anyway.
    ///
    /// Bricks share the geometry of <see cref="VoxelEditLayer"/> (same 16³ grouping, same
    /// coordinates), which lets the GPU backend upload both layers into one resident brick
    /// pool with a single slot lookup per voxel.
    ///
    /// Thread-safety is the same copy-on-write scheme as <see cref="VoxelEditLayer"/>:
    /// readers grab the current snapshot without locks, writers clone the touched bricks
    /// and publish a new snapshot with one volatile store.
    /// </summary>
    public sealed class VoxelMaterialLayer : IVoxelMaterialSource
    {
        const int BrickBits = VoxelEditLayer.BrickBits;
        const int BrickMask = VoxelEditLayer.BrickMask;
        public const int BrickVolume = VoxelEditLayer.BrickVolume;

        volatile Dictionary<Vector3Int, byte[]> bricks = new Dictionary<Vector3Int, byte[]>();

        public int BrickCount => bricks.Count;

        /// <summary>Payload bytes of the stored bricks (one byte per voxel), for stats.</summary>
        public long EstimateBytes() => (long)bricks.Count * BrickVolume;

        /// <summary>Material id of a voxel; 0 (the default material) anywhere unpainted.</summary>
        public byte SampleMaterial(int x, int y, int z)
        {
            var snapshot = bricks;
            if (snapshot.Count == 0)
                return 0;

            var brickCoord = new Vector3Int(x >> BrickBits, y >> BrickBits, z >> BrickBits);
            return snapshot.TryGetValue(brickCoord, out byte[] brick)
                ? brick[Index(x, y, z)]
                : (byte)0;
        }

        /// <summary>
        /// Applies a batch of material writes as one atomic snapshot swap, mirroring
        /// <see cref="VoxelEditLayer.WriteBatch"/>. Call from one thread at a time
        /// (the main thread in practice).
        /// </summary>
        public void WriteBatch(System.Action<System.Action<int, int, int, byte>> writer)
        {
            var next = new Dictionary<Vector3Int, byte[]>(bricks);
            var cloned = new HashSet<Vector3Int>();

            void Set(int x, int y, int z, byte id)
            {
                var bc = new Vector3Int(x >> BrickBits, y >> BrickBits, z >> BrickBits);
                if (!next.TryGetValue(bc, out byte[] brick))
                {
                    if (id == 0)
                        return; // writing the default into an untracked region is a no-op
                    brick = new byte[BrickVolume];
                    next[bc] = brick;
                    cloned.Add(bc);
                }
                else if (cloned.Add(bc))
                {
                    brick = (byte[])brick.Clone();
                    next[bc] = brick;
                }
                next[bc][Index(x, y, z)] = id;
            }

            writer(Set);
            bricks = next;
        }

        public void Clear() => bricks = new Dictionary<Vector3Int, byte[]>();

        /// <summary>Collects every brick in the layer (initial upload of the GPU brick pool).</summary>
        public void CollectAllBricks(List<(Vector3Int coord, byte[] data)> results)
        {
            results.Clear();
            foreach (var entry in bricks)
                results.Add((entry.Key, entry.Value));
        }

        /// <summary>
        /// Collects every brick overlapping the given voxel region, for upload to the GPU
        /// brick pool. The arrays are immutable copy-on-write snapshots — read-only.
        /// </summary>
        public void CollectBricks(BoundsInt voxelBounds, List<(Vector3Int coord, byte[] data)> results)
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
                    if (snapshot.TryGetValue(coord, out byte[] brick))
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

        static int Index(int x, int y, int z) =>
            (x & BrickMask) | ((y & BrickMask) << BrickBits) | ((z & BrickMask) << (2 * BrickBits));
    }
}
