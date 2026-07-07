using System.Collections.Generic;
using UnityEngine;

namespace reromanlee.Transvoxel.Octree
{
    /// <summary>
    /// One chunk the octree wants on screen: which chunk, and which of its faces border
    /// finer neighbours and therefore need transition cells and shifted boundary vertices.
    /// This is the whole contract between the octree and the meshing side (Concept.txt #1):
    /// the octree decides *what* to draw, the meshers know *how*.
    /// </summary>
    public struct ChunkDrawCommand
    {
        public NodeKey Key;
        public byte TransitionMask; // bit per CubeFace

        public ChunkDrawCommand(NodeKey key, byte transitionMask)
        {
            Key = key;
            TransitionMask = transitionMask;
        }
    }

    /// <summary>
    /// Viewer-centred LOD selection over a conceptually infinite world.
    ///
    /// The world is tiled by "root" nodes at the coarsest LOD; roots appear and disappear
    /// as the viewer moves, so there is no fixed world size. Each call to
    /// <see cref="SelectChunks"/> recursively splits nodes whose centre is close to the
    /// viewer, producing concentric rings of finer and finer chunks, then enforces the
    /// 2:1 balance rule (adjacent chunks differ by at most one LOD — a hard requirement
    /// of the Transvoxel transition cells) and computes each leaf's transition mask.
    ///
    /// The octree never touches density data or meshes; it is a pure function from the
    /// viewer position to the set of chunks that should exist.
    /// </summary>
    public sealed class TerrainOctree
    {
        readonly int chunkCells;
        readonly float voxelSize;
        readonly int maxLod;
        readonly float viewDistanceVoxels;
        readonly float splitFactor;

        // Scratch collections, reused between calls (main thread only).
        readonly HashSet<NodeKey> leaves = new HashSet<NodeKey>();
        readonly Queue<NodeKey> balanceQueue = new Queue<NodeKey>();

        /// <param name="chunkCells">Cells per chunk axis.</param>
        /// <param name="voxelSize">World size of one LOD0 voxel, in meters.</param>
        /// <param name="maxLod">Number of LOD levels above LOD0 (Concept.txt #4).</param>
        /// <param name="viewDistance">Meters beyond which no chunks are produced.</param>
        /// <param name="splitFactor">
        /// A node splits into finer chunks while the viewer is closer than
        /// splitFactor × the node's world size. Larger values push high detail further out.
        /// </param>
        public TerrainOctree(int chunkCells, float voxelSize, int maxLod, float viewDistance, float splitFactor)
        {
            this.chunkCells = chunkCells;
            this.voxelSize = voxelSize;
            this.maxLod = maxLod;
            viewDistanceVoxels = viewDistance / voxelSize;
            this.splitFactor = Mathf.Max(1f, splitFactor);
        }

        /// <summary>
        /// Computes the desired chunk set for a viewer at the given position
        /// (in terrain-local space). Results are appended to <paramref name="results"/>.
        /// </summary>
        public void SelectChunks(Vector3 viewerLocalPosition, List<ChunkDrawCommand> results)
        {
            leaves.Clear();
            Vector3 viewerVoxel = viewerLocalPosition / voxelSize;

            // Visit every root whose box could intersect the view sphere.
            int rootSize = chunkCells << maxLod;
            Vector3Int minRoot = FloorDiv(viewerVoxel - Vector3.one * viewDistanceVoxels, rootSize);
            Vector3Int maxRoot = FloorDiv(viewerVoxel + Vector3.one * viewDistanceVoxels, rootSize);

            for (int z = minRoot.z; z <= maxRoot.z; z++)
            for (int y = minRoot.y; y <= maxRoot.y; y++)
            for (int x = minRoot.x; x <= maxRoot.x; x++)
                Visit(new NodeKey(maxLod, new Vector3Int(x, y, z)), viewerVoxel);

            Balance();

            foreach (var leaf in leaves)
                results.Add(new ChunkDrawCommand(leaf, ComputeTransitionMask(leaf)));
        }

        void Visit(NodeKey key, Vector3 viewerVoxel)
        {
            int size = chunkCells << key.Lod;
            var min = key.MinVoxel(chunkCells);

            if (DistanceToBox(viewerVoxel, min, size) > viewDistanceVoxels)
                return; // beyond draw distance

            if (key.Lod > 0 && ShouldSplit(key, viewerVoxel))
            {
                for (int child = 0; child < 8; child++)
                    Visit(key.Child(child), viewerVoxel);
            }
            else
            {
                leaves.Add(key);
            }
        }

        bool ShouldSplit(NodeKey key, Vector3 viewerVoxel)
        {
            int size = chunkCells << key.Lod;
            var min = key.MinVoxel(chunkCells);
            Vector3 center = new Vector3(min.x + size * 0.5f, min.y + size * 0.5f, min.z + size * 0.5f);
            float splitDistance = splitFactor * size;
            return (viewerVoxel - center).sqrMagnitude < splitDistance * splitDistance;
        }

        /// <summary>
        /// Enforces the 2:1 rule: whenever a leaf's face neighbour is two or more levels
        /// coarser, the coarse leaf is split, and the check propagates until stable. Only
        /// then is "one transition mesh per face" enough to close every LOD seam.
        /// </summary>
        void Balance()
        {
            balanceQueue.Clear();
            foreach (var leaf in leaves)
                balanceQueue.Enqueue(leaf);

            while (balanceQueue.Count > 0)
            {
                var leaf = balanceQueue.Dequeue();
                if (!leaves.Contains(leaf))
                    continue; // got split in the meantime

                for (int f = 0; f < 6; f++)
                {
                    var neighbour = leaf.Neighbour((CubeFace)f);

                    // Find the leaf covering the neighbour position at ancestor levels
                    // that would violate balance (2+ levels coarser than this leaf).
                    for (int lod = leaf.Lod + 2; lod <= maxLod; lod++)
                    {
                        int shift = lod - leaf.Lod;
                        var ancestor = new NodeKey(lod, new Vector3Int(
                            neighbour.Coord.x >> shift, neighbour.Coord.y >> shift, neighbour.Coord.z >> shift));
                        if (!leaves.Remove(ancestor))
                            continue;

                        for (int child = 0; child < 8; child++)
                        {
                            var childKey = ancestor.Child(child);
                            leaves.Add(childKey);
                            balanceQueue.Enqueue(childKey);
                        }
                        // Re-check this leaf: the neighbour may still be too coarse.
                        balanceQueue.Enqueue(leaf);
                        break;
                    }
                }
            }
        }

        byte ComputeTransitionMask(NodeKey leaf)
        {
            if (leaf.Lod == 0)
                return 0; // nothing is finer than LOD0

            byte mask = 0;
            for (int f = 0; f < 6; f++)
            {
                var face = (CubeFace)f;
                var neighbour = leaf.Neighbour(face);

                if (leaves.Contains(neighbour))
                    continue; // same LOD, plain shared face

                // A coarser neighbour owns the transition cells itself.
                bool coarser = false;
                for (int lod = leaf.Lod + 1; lod <= maxLod && !coarser; lod++)
                {
                    int shift = lod - leaf.Lod;
                    coarser = leaves.Contains(new NodeKey(lod, new Vector3Int(
                        neighbour.Coord.x >> shift, neighbour.Coord.y >> shift, neighbour.Coord.z >> shift)));
                }
                if (coarser)
                    continue;

                // Finer neighbours: thanks to balancing they are exactly one level down.
                // Probe one of the four children touching our shared face; a split node
                // always has all of them. If nothing exists there at all (world edge,
                // beyond view distance) we render a plain face — no seam partner exists.
                var probe = FaceChildProbe(neighbour, face);
                if (leaves.Contains(probe))
                    mask |= (byte)(1 << f);
            }

            return mask;
        }

        /// <summary>
        /// Child key of <paramref name="neighbour"/> that touches the shared face:
        /// mirrored back towards the leaf along the face axis, minimal on the others.
        /// </summary>
        NodeKey FaceChildProbe(NodeKey neighbour, CubeFace face)
        {
            var c = new Vector3Int(neighbour.Coord.x << 1, neighbour.Coord.y << 1, neighbour.Coord.z << 1);
            int axis = face.Axis();
            // Crossing a +axis face means the neighbour's children on its -axis side touch
            // us, and vice versa.
            if (!face.IsPositive())
            {
                if (axis == 0) c.x += 1;
                else if (axis == 1) c.y += 1;
                else c.z += 1;
            }
            return new NodeKey(neighbour.Lod - 1, c);
        }

        static float DistanceToBox(Vector3 point, Vector3Int boxMinVoxel, int boxSize)
        {
            float dx = Mathf.Max(boxMinVoxel.x - point.x, 0f, point.x - (boxMinVoxel.x + boxSize));
            float dy = Mathf.Max(boxMinVoxel.y - point.y, 0f, point.y - (boxMinVoxel.y + boxSize));
            float dz = Mathf.Max(boxMinVoxel.z - point.z, 0f, point.z - (boxMinVoxel.z + boxSize));
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static Vector3Int FloorDiv(Vector3 v, int divisor)
        {
            return new Vector3Int(
                Mathf.FloorToInt(v.x / divisor),
                Mathf.FloorToInt(v.y / divisor),
                Mathf.FloorToInt(v.z / divisor));
        }
    }
}
