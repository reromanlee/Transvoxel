using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using reromanlee.Transvoxel.Meshing;
using UnityEngine;

namespace reromanlee.Transvoxel
{
    /// <summary>
    /// One finished chunk build, produced by a CPU worker or a GPU readback and consumed on
    /// the main thread. Exactly one payload is set: <see cref="Buffers"/> (CPU meshing),
    /// <see cref="RawVertices"/> (GPU triangle soup: interleaved position/normal/uv floats,
    /// rented from <see cref="System.Buffers.ArrayPool{T}"/>), or neither with
    /// <see cref="Failed"/> raised so the scheduler can retry.
    /// </summary>
    public struct ChunkBuildResult
    {
        public NodeKey Key;
        public byte Mask;
        public ChunkBuildJob Ticket;
        public MeshBuffers Buffers;
        public float[] RawVertices;
        public int RawVertexCount;
        public bool Failed;

        /// <summary>
        /// Time the result was first deferred while waiting for the neighbours that changed
        /// its transition mask to come on screen; 0 until then. Bounds the wait.
        /// </summary>
        public float FirstDeferredTime;

        public bool IsEmpty => Buffers != null ? Buffers.IsEmpty : RawVertexCount == 0;

        /// <summary>Returns pooled payloads. Call exactly once when the result is dropped or applied.</summary>
        public void ReleasePayload()
        {
            if (Buffers != null)
            {
                MeshBuffers.Return(Buffers);
                Buffers = null;
            }
            if (RawVertices != null)
            {
                System.Buffers.ArrayPool<float>.Shared.Return(RawVertices);
                RawVertices = null;
            }
        }
    }

    /// <summary>
    /// One scheduled chunk (re)build. Doubles as the pending "ticket": scheduling a newer
    /// build for the same chunk sets <see cref="Superseded"/> so in-flight work can bail
    /// out early at every stage (queue pop, after sampling, before GPU readback).
    /// </summary>
    public sealed class ChunkBuildJob
    {
        public NodeKey Key;
        public byte Mask;

        /// <summary>Set when a newer build replaces this one; consumers drop the job.</summary>
        public volatile bool Superseded;

        /// <summary>Terraform rebuilds jump the whole queue — the player is looking at them.</summary>
        public bool Rush;

        /// <summary>Snapshotted at schedule time (the only knob that changes without a pipeline rebuild).</summary>
        public bool SmoothShading;

        /// <summary>Heap key, maintained by <see cref="BuildQueue"/> under its lock.</summary>
        internal float Priority;
    }

    /// <summary>
    /// Distance-prioritized queue of chunk builds. The old scheme ran one Task per build in
    /// whatever order the thread pool picked them up; after a teleport that meant far chunks
    /// could mesh before the ground under the player's feet. Here workers (or the GPU pump)
    /// always pop the job nearest to the viewer, and <see cref="UpdateViewer"/> re-sorts
    /// everything still queued whenever the viewer moves — so even mid-flight direction
    /// changes redirect the build effort instantly.
    ///
    /// Thread-safe. Producers enqueue from the main thread; consumers pop from worker tasks
    /// (<see cref="DequeueAsync"/>) or the main-thread GPU pump (<see cref="TryDequeue"/>).
    /// </summary>
    public sealed class BuildQueue
    {
        // Rush jobs sort below every distance the world can produce.
        const float RushBias = 1e12f;

        readonly object gate = new object();
        readonly List<ChunkBuildJob> heap = new List<ChunkBuildJob>(256);
        readonly SemaphoreSlim items = new SemaphoreSlim(0);
        readonly int chunkCells;
        Vector3 viewerVoxel;

        public BuildQueue(int chunkCells)
        {
            this.chunkCells = chunkCells;
        }

        public int Count
        {
            get { lock (gate) return heap.Count; }
        }

        public void Enqueue(ChunkBuildJob job)
        {
            lock (gate)
            {
                job.Priority = ComputePriority(job);
                heap.Add(job);
                SiftUp(heap.Count - 1);
            }
            items.Release();
        }

        /// <summary>Blocks (asynchronously) until a job is available. Used by CPU workers.</summary>
        public async Task<ChunkBuildJob> DequeueAsync(CancellationToken cancellation)
        {
            await items.WaitAsync(cancellation).ConfigureAwait(false);
            lock (gate)
                return PopMin();
        }

        /// <summary>Non-blocking pop for the main-thread GPU pump.</summary>
        public bool TryDequeue(out ChunkBuildJob job)
        {
            if (!items.Wait(0))
            {
                job = null;
                return false;
            }
            lock (gate)
                job = PopMin();
            return true;
        }

        /// <summary>
        /// Re-sorts every queued job around the new viewer position (terrain-local voxel
        /// units). O(n) rebuild — selection passes are far rarer than pops.
        /// </summary>
        public void UpdateViewer(Vector3 viewerVoxelPosition)
        {
            lock (gate)
            {
                viewerVoxel = viewerVoxelPosition;
                for (int i = 0; i < heap.Count; i++)
                    heap[i].Priority = ComputePriority(heap[i]);
                for (int i = heap.Count / 2 - 1; i >= 0; i--)
                    SiftDown(i);
            }
        }

        /// <summary>
        /// Distance from the viewer to the chunk's box, in voxels (0 inside). A tiny LOD
        /// penalty breaks ties in favour of fine chunks: at equal distance the ground under
        /// the player (colliders live there) beats a far-reaching coarse shell.
        /// </summary>
        float ComputePriority(ChunkBuildJob job)
        {
            int size = chunkCells << job.Key.Lod;
            Vector3Int min = job.Key.MinVoxel(chunkCells);
            float dx = Mathf.Max(min.x - viewerVoxel.x, 0f, viewerVoxel.x - (min.x + size));
            float dy = Mathf.Max(min.y - viewerVoxel.y, 0f, viewerVoxel.y - (min.y + size));
            float dz = Mathf.Max(min.z - viewerVoxel.z, 0f, viewerVoxel.z - (min.z + size));
            float distance = Mathf.Sqrt(dx * dx + dy * dy + dz * dz) + job.Key.Lod;
            return job.Rush ? distance - RushBias : distance;
        }

        // ---- binary min-heap ----

        ChunkBuildJob PopMin()
        {
            var top = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            if (heap.Count > 0)
                SiftDown(0);
            return top;
        }

        void SiftUp(int index)
        {
            var item = heap[index];
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (heap[parent].Priority <= item.Priority)
                    break;
                heap[index] = heap[parent];
                index = parent;
            }
            heap[index] = item;
        }

        void SiftDown(int index)
        {
            var item = heap[index];
            int count = heap.Count;
            while (true)
            {
                int child = index * 2 + 1;
                if (child >= count)
                    break;
                if (child + 1 < count && heap[child + 1].Priority < heap[child].Priority)
                    child++;
                if (heap[child].Priority >= item.Priority)
                    break;
                heap[index] = heap[child];
                index = child;
            }
            heap[index] = item;
        }
    }
}
