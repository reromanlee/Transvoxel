using System;
using UnityEngine;

namespace reromanlee.Transvoxel
{
    /// <summary>
    /// Identity of one octree node / terrain chunk: an LOD level plus the chunk
    /// coordinate at that level. A node at (lod, coord) covers the LOD0-voxel range
    /// [coord * chunkCells &lt;&lt; lod, (coord + 1) * chunkCells &lt;&lt; lod) on each axis,
    /// so a parent's coordinate is simply its child's coordinate shifted right by one.
    /// </summary>
    public readonly struct NodeKey : IEquatable<NodeKey>
    {
        /// <summary>0 = full resolution; each level up doubles the voxel stride.</summary>
        public readonly int Lod;

        /// <summary>Chunk coordinate on the grid of chunks at this LOD (may be negative).</summary>
        public readonly Vector3Int Coord;

        public NodeKey(int lod, Vector3Int coord)
        {
            Lod = lod;
            Coord = coord;
        }

        /// <summary>The key of the node containing this one at the next coarser level.</summary>
        public NodeKey Parent => new NodeKey(Lod + 1, new Vector3Int(Coord.x >> 1, Coord.y >> 1, Coord.z >> 1));

        /// <summary>Child key at the next finer level; childIndex bits (1,2,4) select +x,+y,+z.</summary>
        public NodeKey Child(int childIndex) => new NodeKey(Lod - 1, new Vector3Int(
            (Coord.x << 1) + (childIndex & 1),
            (Coord.y << 1) + ((childIndex >> 1) & 1),
            (Coord.z << 1) + ((childIndex >> 2) & 1)));

        /// <summary>Key of the face neighbour at the same LOD.</summary>
        public NodeKey Neighbour(CubeFace face) => new NodeKey(Lod, Coord + face.Offset());

        /// <summary>Minimum corner of this node on the LOD0 voxel lattice.</summary>
        public Vector3Int MinVoxel(int chunkCells)
        {
            int size = chunkCells << Lod;
            return new Vector3Int(Coord.x * size, Coord.y * size, Coord.z * size);
        }

        public bool Equals(NodeKey other) => Lod == other.Lod && Coord == other.Coord;
        public override bool Equals(object obj) => obj is NodeKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Lod;
                h = h * 486187739 + Coord.x;
                h = h * 486187739 + Coord.y;
                h = h * 486187739 + Coord.z;
                return h;
            }
        }

        public override string ToString() => $"lod{Lod} ({Coord.x},{Coord.y},{Coord.z})";
    }

    /// <summary>
    /// The six faces of a chunk, used for transition-cell bookkeeping.
    /// Order matches bit positions in transition masks: bit i = (int)face i.
    /// </summary>
    public enum CubeFace
    {
        NegX = 0,
        PosX = 1,
        NegY = 2,
        PosY = 3,
        NegZ = 4,
        PosZ = 5,
    }

    public static class CubeFaceExtensions
    {
        static readonly Vector3Int[] Offsets =
        {
            new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0),
            new Vector3Int(0, -1, 0), new Vector3Int(0, 1, 0),
            new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1),
        };

        /// <summary>Unit step towards the neighbour across this face.</summary>
        public static Vector3Int Offset(this CubeFace face) => Offsets[(int)face];

        /// <summary>0 = x, 1 = y, 2 = z.</summary>
        public static int Axis(this CubeFace face) => (int)face >> 1;

        /// <summary>True for the +x/+y/+z faces.</summary>
        public static bool IsPositive(this CubeFace face) => ((int)face & 1) != 0;

        public static int Bit(this CubeFace face) => 1 << (int)face;
    }
}
