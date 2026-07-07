using System.Collections.Generic;
using reromanlee.Transvoxel.Density;
using UnityEngine;

namespace reromanlee.Transvoxel.Meshing
{
    /// <summary>
    /// LRU cache of sampled chunk density grids, so re-meshing a chunk (transition mask
    /// changed, smooth toggle, neighbouring terraform) does not pay for sampling again.
    /// This is the "cached chunk information" of Concept.txt #1: the octree pipeline asks
    /// here first and only falls back to the density source on a miss.
    ///
    /// Thread-safe: lookups and inserts take a short lock; the sampling itself runs
    /// outside it. Two workers racing on the same key may sample twice — harmless, the
    /// second insert just wins.
    /// </summary>
    public sealed class SampleCache
    {
        readonly object gate = new object();
        readonly Dictionary<NodeKey, ChunkSamples> map = new Dictionary<NodeKey, ChunkSamples>();
        readonly LinkedList<NodeKey> lru = new LinkedList<NodeKey>();
        readonly Dictionary<NodeKey, LinkedListNode<NodeKey>> lruNodes =
            new Dictionary<NodeKey, LinkedListNode<NodeKey>>();
        readonly int capacity;

        public SampleCache(int capacity)
        {
            this.capacity = capacity;
        }

        public ChunkSamples GetOrSample(IDensitySource source, NodeKey key, int chunkCells, float isoLevel,
            byte transitionMask)
        {
            if (capacity <= 0)
                return ChunkSamples.Sample(source, key, chunkCells, isoLevel, transitionMask);

            lock (gate)
            {
                if (map.TryGetValue(key, out var cached)
                    && (cached.TransitionMask & transitionMask) == transitionMask)
                {
                    Touch(key);
                    return cached;
                }
            }

            // Miss, or the cached grid lacks a face sheet this build needs: sample outside
            // the lock (this is the expensive part) and publish the fresh grid.
            var samples = ChunkSamples.Sample(source, key, chunkCells, isoLevel, transitionMask);

            lock (gate)
            {
                map[key] = samples;
                Touch(key);
                while (map.Count > capacity && lru.Last != null)
                {
                    var evict = lru.Last.Value;
                    lru.RemoveLast();
                    lruNodes.Remove(evict);
                    map.Remove(evict);
                }
            }

            return samples;
        }

        /// <summary>Drops every cached grid that reads voxels inside the changed region.</summary>
        public void RemoveOverlapping(BoundsInt voxelBounds, int chunkCells)
        {
            lock (gate)
            {
                List<NodeKey> stale = null;
                foreach (var key in map.Keys)
                {
                    int size = chunkCells << key.Lod;
                    int pad = 2 * (1 << key.Lod);
                    Vector3Int min = key.MinVoxel(chunkCells);
                    bool overlaps =
                        min.x - pad < voxelBounds.xMax && min.x + size + pad > voxelBounds.xMin
                        && min.y - pad < voxelBounds.yMax && min.y + size + pad > voxelBounds.yMin
                        && min.z - pad < voxelBounds.zMax && min.z + size + pad > voxelBounds.zMin;
                    if (overlaps)
                        (stale ??= new List<NodeKey>()).Add(key);
                }
                if (stale == null)
                    return;
                foreach (var key in stale)
                {
                    map.Remove(key);
                    if (lruNodes.TryGetValue(key, out var node))
                    {
                        lru.Remove(node);
                        lruNodes.Remove(key);
                    }
                }
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                map.Clear();
                lru.Clear();
                lruNodes.Clear();
            }
        }

        void Touch(NodeKey key)
        {
            if (lruNodes.TryGetValue(key, out var node))
                lru.Remove(node);
            lruNodes[key] = lru.AddFirst(key);
        }
    }
}
