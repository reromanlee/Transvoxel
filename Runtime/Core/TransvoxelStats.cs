using System.Threading;

namespace reromanlee.Transvoxel
{
    /// <summary>
    /// Point-in-time approximation of the terrain's own resource usage, filled by
    /// <see cref="TransvoxelTerrain.CollectStats"/>. Raw numbers for your own debug UI or
    /// budgets; the bundled Terrain Stats window (Window ▸ Transvoxel ▸ Terrain Stats)
    /// displays them live.
    ///
    /// Scope: only what this package allocates and computes. Rendering, materials/textures,
    /// GameObject/component overhead and PhysX's cooked collider data (Unity exposes no
    /// size for it) are excluded. Memory numbers are computed exactly from the known data
    /// structures (list capacities, brick counts, vertex layouts); timing numbers are
    /// measured — see the field notes for how.
    /// </summary>
    public struct TransvoxelResourceStats
    {
        // ------------------------------------------------------------------ CPU time

        /// <summary>Main-thread ms per frame spent in the terrain's Update (smoothed).</summary>
        public float MainThreadMsPerFrame;

        /// <summary>Worst main-thread frame cost inside the last sampling window, in ms.</summary>
        public float MainThreadPeakMs;

        /// <summary>
        /// CPU ms per second consumed by background work: chunk sampling + meshing, GPU
        /// soup welding, material blend encoding, octree selection and collider bakes.
        /// (Spread across worker threads — divide by core count for average core load.)
        /// </summary>
        public float WorkerCpuMsPerSecond;

        /// <summary>Chunk builds finished per second, and average CPU ms of one build.</summary>
        public float BuildsPerSecond;
        public float AverageBuildCpuMs;

        // ------------------------------------------------------------------ GPU time

        /// <summary>
        /// GPU ms per second spent in this package's compute kernels. Sampled: tiny
        /// readbacks bracket each job's dispatches on the GPU timeline; single samples are
        /// quantized to frame boundaries, but the average converges on the true cost over
        /// many jobs. 0 while nothing builds — an idle terrain costs the GPU nothing.
        /// </summary>
        public float GpuComputeMsPerSecond;

        /// <summary>Average sampled GPU ms of one chunk job (same caveat as above).</summary>
        public float AverageGpuJobMs;

        public int GpuJobsInFlight;

        // ------------------------------------------------------------------ RAM

        /// <summary>Cached density grids (<see cref="Meshing.SampleCache"/>).</summary>
        public long DensityCacheBytes;

        /// <summary>Player-edit density bricks (<see cref="Density.VoxelEditLayer"/>).</summary>
        public long EditLayerBytes;

        /// <summary>Painted material-id bricks (<see cref="Density.VoxelMaterialLayer"/>).</summary>
        public long MaterialLayerBytes;

        /// <summary>Pooled and in-flight CPU meshing buffers (list capacities).</summary>
        public long MeshBufferBytes;

        /// <summary>
        /// CPU-side copies of the chunk meshes (live, cross-fade ghosts and meshes parked
        /// for collider bakes) — readable Unity meshes keep one.
        /// </summary>
        public long ChunkMeshCpuBytes;

        public long RamTotalBytes;

        // ------------------------------------------------------------------ GPU memory

        /// <summary>GPU vertex/index buffers of the same chunk meshes.</summary>
        public long ChunkMeshGpuBytes;

        /// <summary>
        /// Compute buffers of the GPU backend: per-job sets (volume grid + triangle
        /// append), the resident edit/material brick pool and the lookup tables.
        /// </summary>
        public long ComputeBufferBytes;

        public long GpuTotalBytes;

        // ------------------------------------------------------------------ scene

        public int LiveChunks;
        public int GhostChunks;
        public int PendingBuilds;
        public int CachedGrids;
        public int PooledChunkViews;
        public int PooledMeshBuffers;
        public long TotalVertices;
        public long TotalIndices;
    }

    /// <summary>
    /// The always-on accumulators behind <see cref="TransvoxelResourceStats"/>. Worker
    /// threads report finished work via interlocked adds; the terrain closes a sampling
    /// window from Update every half second and keeps smoothed rates. Costs a few atomic
    /// adds per chunk build — nothing per frame beyond one stopwatch read.
    /// </summary>
    internal sealed class TransvoxelWorkStats
    {
        const double WindowSeconds = 0.5;
        const float RateSmoothing = 0.5f;   // blend per window
        const float FrameSmoothing = 0.05f; // blend per frame

        static readonly double TicksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // Accumulated since the window started. Ticks/counts come from any thread; the
        // GPU samples only ever arrive on the main thread (readback callbacks).
        long workerTicks;
        long buildCount;
        long buildTicks;
        double gpuMs;
        int gpuSamples;

        // Window state and smoothed outputs (main thread).
        double windowStart = -1;
        float peakInWindow;
        float mainThreadMs;
        float mainThreadPeakMs;
        float workerMsPerSecond;
        float buildsPerSecond;
        float averageBuildMs;
        float gpuMsPerSecond;
        float averageGpuMs;

        /// <summary>A finished chunk build (sampling + meshing + welding + encoding).</summary>
        public void AddBuild(long ticks)
        {
            Interlocked.Add(ref workerTicks, ticks);
            Interlocked.Add(ref buildTicks, ticks);
            Interlocked.Increment(ref buildCount);
        }

        /// <summary>Other background work: octree selection, collider bakes.</summary>
        public void AddWorkerTicks(long ticks) => Interlocked.Add(ref workerTicks, ticks);

        /// <summary>One sampled GPU job duration (main thread — readback callback).</summary>
        public void AddGpuSampleMs(double ms)
        {
            gpuMs += ms;
            gpuSamples++;
        }

        /// <summary>Feed one frame's main-thread cost and roll the window. Main thread.</summary>
        public void EndFrame(float frameMs, double now)
        {
            mainThreadMs += (frameMs - mainThreadMs) * FrameSmoothing;
            if (frameMs > peakInWindow)
                peakInWindow = frameMs;

            if (windowStart < 0)
                windowStart = now;
            double elapsed = now - windowStart;
            if (elapsed < WindowSeconds)
                return;

            float workerMs = (float)(Interlocked.Exchange(ref workerTicks, 0) * TicksToMs);
            long builds = Interlocked.Exchange(ref buildCount, 0);
            float buildMs = (float)(Interlocked.Exchange(ref buildTicks, 0) * TicksToMs);

            Blend(ref workerMsPerSecond, (float)(workerMs / elapsed));
            Blend(ref buildsPerSecond, (float)(builds / elapsed));
            Blend(ref averageBuildMs, builds > 0 ? buildMs / builds : 0f);
            Blend(ref gpuMsPerSecond, (float)(gpuMs / elapsed));
            Blend(ref averageGpuMs, gpuSamples > 0 ? (float)(gpuMs / gpuSamples) : 0f);

            gpuMs = 0;
            gpuSamples = 0;
            mainThreadPeakMs = peakInWindow;
            peakInWindow = 0f;
            windowStart = now;
        }

        static void Blend(ref float smoothed, float value) =>
            smoothed += (value - smoothed) * RateSmoothing;

        public void FillTimings(ref TransvoxelResourceStats stats)
        {
            stats.MainThreadMsPerFrame = mainThreadMs;
            stats.MainThreadPeakMs = mainThreadPeakMs;
            stats.WorkerCpuMsPerSecond = workerMsPerSecond;
            stats.BuildsPerSecond = buildsPerSecond;
            stats.AverageBuildCpuMs = averageBuildMs;
            stats.GpuComputeMsPerSecond = gpuMsPerSecond;
            stats.AverageGpuJobMs = averageGpuMs;
        }
    }
}
