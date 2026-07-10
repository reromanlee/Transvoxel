using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using reromanlee.Transvoxel.Density;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace reromanlee.Transvoxel.Gpu
{
    /// <summary>
    /// The GPU meshing backend. Pops jobs from the shared <see cref="BuildQueue"/> (same
    /// distance priority as the CPU workers), dispatches the three compute kernels —
    /// density volume, regular cells, transition cells — and streams finished triangle
    /// soups back with <see cref="AsyncGPUReadback"/>, so the main thread never waits on
    /// the GPU. Results enter the exact same completed-builds pipeline as CPU meshes.
    ///
    /// Per-job GPU buffers are pooled; at most <c>gpuJobsInFlight</c> chunks are on the
    /// GPU at once. A readback goes through two stages (triangle count, then exactly that
    /// many triangles) so empty or small chunks never transfer the worst-case buffer.
    ///
    /// Main thread only. Created per pipeline; any settings change rebuilds it.
    /// </summary>
    public sealed class GpuChunkBuilder : IDisposable
    {
        const int TriangleFloatStride = 24; // 3 vertices x (3 position + 3 normal + 2 uv)
        const int TriangleByteStride = TriangleFloatStride * 4;

        sealed class JobSet
        {
            public ComputeBuffer Volume;        // (cells+3)^3 floats
            public ComputeBuffer Triangles;     // append, worst-case capacity
            public ComputeBuffer TriangleCount; // 1 raw uint, CopyCount target
            public ComputeBuffer EditCoords;    // int3 per brick
            public ComputeBuffer EditData;      // 4096 floats per brick
            public int EditCapacity;            // bricks
        }

        readonly ComputeShader shader; // private instance — uniforms are per-builder state
        readonly VoxelEditLayer edits;
        readonly int kernelVolume;
        readonly int kernelRegular;
        readonly int kernelTransition;
        readonly int chunkCells;
        readonly int pointsPerAxis;
        readonly int triangleCapacity;
        readonly int maxInFlight;

        readonly ComputeBuffer permBuffer;
        readonly List<ComputeBuffer> tableBuffers = new List<ComputeBuffer>();

        readonly Stack<JobSet> setPool = new Stack<JobSet>();
        readonly List<JobSet> allSets = new List<JobSet>();
        readonly List<(Vector3Int coord, float[] data)> brickScratch =
            new List<(Vector3Int, float[])>();
        int[] coordScratch = Array.Empty<int>();
        float[] editScratch = Array.Empty<float>();

        int inFlight;
        bool disposed;

        public int InFlight => inFlight;

        public GpuChunkBuilder(ComputeShader shaderAsset, TransvoxelSettings settings, VoxelEditLayer editLayer)
        {
            shader = UnityEngine.Object.Instantiate(shaderAsset);
            edits = editLayer;
            chunkCells = settings.chunkCells;
            pointsPerAxis = chunkCells + 3;
            maxInFlight = settings.gpuJobsInFlight;

            // Worst case: 5 triangles per regular cell plus 12 per transition cell on all faces.
            triangleCapacity = chunkCells * chunkCells * chunkCells * 5
                               + chunkCells * chunkCells * 6 * 12;

            kernelVolume = shader.FindKernel("CSVolume");
            kernelRegular = shader.FindKernel("CSRegular");
            kernelTransition = shader.FindKernel("CSTransition");

            // Pipeline-constant uniforms; a settings change rebuilds the whole builder.
            shader.SetInt("_Cells", chunkCells);
            shader.SetInt("_PointsPerAxis", pointsPerAxis);
            shader.SetFloat("_IsoLevel", settings.isoLevel);
            shader.SetFloat("_VoxelSize", settings.voxelSize);
            shader.SetFloat("_UvScale", settings.uvScale);

            NoiseSettings noise = settings.noise;
            shader.SetFloat("_GroundLevel", noise.groundLevel);
            shader.SetFloat("_HeightAmplitude", noise.heightAmplitude);
            shader.SetFloat("_Frequency", noise.frequency);
            shader.SetFloat("_Lacunarity", noise.lacunarity);
            shader.SetFloat("_Persistence", noise.persistence);
            shader.SetFloat("_SurfaceBlend", noise.surfaceBlend);
            shader.SetFloat("_CaveStrength", noise.caveStrength);
            shader.SetFloat("_CaveFrequency", noise.caveFrequency);
            shader.SetFloat("_CaveThreshold", noise.caveThreshold);
            shader.SetInt("_Octaves", noise.octaves);

            // Same permutation table as the CPU's FractalNoise: same seed, same landscape.
            permBuffer = new ComputeBuffer(512, sizeof(int));
            permBuffer.SetData(new FractalNoise(noise.seed).ExportPermutationTable());
            shader.SetBuffer(kernelVolume, "_Perm", permBuffer);
            shader.SetBuffer(kernelTransition, "_Perm", permBuffer);

            BindTable(kernelRegular, "_RegularCellClass", TransvoxelGpuTables.RegularCellClass);
            BindTable(kernelRegular, "_RegularCellCounts", TransvoxelGpuTables.RegularCellCounts);
            BindTable(kernelRegular, "_RegularCellIndices", TransvoxelGpuTables.RegularCellIndices);
            BindTable(kernelRegular, "_RegularVertexData", TransvoxelGpuTables.RegularVertexData);
            BindTable(kernelTransition, "_TransitionCellClass", TransvoxelGpuTables.TransitionCellClass);
            BindTable(kernelTransition, "_TransitionCellCounts", TransvoxelGpuTables.TransitionCellCounts);
            BindTable(kernelTransition, "_TransitionCellIndices", TransvoxelGpuTables.TransitionCellIndices);
            BindTable(kernelTransition, "_TransitionVertexData", TransvoxelGpuTables.TransitionVertexData);
        }

        void BindTable(int kernel, string name, int[] data)
        {
            var buffer = new ComputeBuffer(data.Length, sizeof(int));
            buffer.SetData(data);
            tableBuffers.Add(buffer);
            shader.SetBuffer(kernel, name, buffer);
        }

        /// <summary>
        /// Starts as many queued builds as the in-flight limit allows. Call once per frame
        /// from the main thread.
        /// </summary>
        public void Pump(BuildQueue queue, ConcurrentQueue<ChunkBuildResult> results)
        {
            if (disposed)
                return;
            while (inFlight < maxInFlight && queue.TryDequeue(out ChunkBuildJob job))
            {
                if (job.Superseded)
                    continue;
                DispatchJob(job, results);
            }
        }

        void DispatchJob(ChunkBuildJob job, ConcurrentQueue<ChunkBuildResult> results)
        {
            JobSet set = RentSet();
            inFlight++;

            int lodStep = 1 << job.Key.Lod;
            Vector3Int min = job.Key.MinVoxel(chunkCells);

            UploadEdits(set, min, lodStep);

            shader.SetInt("_MinVoxelX", min.x);
            shader.SetInt("_MinVoxelY", min.y);
            shader.SetInt("_MinVoxelZ", min.z);
            shader.SetInt("_LodStep", lodStep);
            shader.SetInt("_TransitionMask", job.Mask);
            shader.SetInt("_FlatShading", job.SmoothShading ? 0 : 1);

            shader.SetBuffer(kernelVolume, "_Volume", set.Volume);
            shader.SetBuffer(kernelVolume, "_EditBrickCoords", set.EditCoords);
            shader.SetBuffer(kernelVolume, "_EditBrickData", set.EditData);
            int volumeGroups = (pointsPerAxis + 3) / 4;
            shader.Dispatch(kernelVolume, volumeGroups, volumeGroups, volumeGroups);

            set.Triangles.SetCounterValue(0);
            shader.SetBuffer(kernelRegular, "_Volume", set.Volume);
            shader.SetBuffer(kernelRegular, "_Triangles", set.Triangles);
            int regularGroups = (chunkCells + 3) / 4;
            shader.Dispatch(kernelRegular, regularGroups, regularGroups, regularGroups);

            if (job.Mask != 0 && job.Key.Lod > 0)
            {
                shader.SetBuffer(kernelTransition, "_Volume", set.Volume);
                shader.SetBuffer(kernelTransition, "_Triangles", set.Triangles);
                shader.SetBuffer(kernelTransition, "_EditBrickCoords", set.EditCoords);
                shader.SetBuffer(kernelTransition, "_EditBrickData", set.EditData);
                int transitionGroups = (chunkCells + 7) / 8;
                for (int face = 0; face < 6; face++)
                {
                    if ((job.Mask & (1 << face)) == 0)
                        continue;
                    shader.SetInt("_Face", face);
                    shader.Dispatch(kernelTransition, transitionGroups, transitionGroups, 1);
                }
            }

            ComputeBuffer.CopyCount(set.Triangles, set.TriangleCount, 0);
            AsyncGPUReadback.Request(set.TriangleCount,
                request => OnCountReady(request, job, set, results));
        }

        /// <summary>
        /// Gathers the player-edit bricks the chunk's samples can read (the grid plus one
        /// coarse step of margin for gradients and fine-lattice probes) and uploads them.
        /// NaN "untouched" markers become the GPU sentinel -1.
        /// </summary>
        void UploadEdits(JobSet set, Vector3Int min, int lodStep)
        {
            int brickCount = 0;
            if (edits != null)
            {
                int size = (chunkCells + 2) * lodStep + 1;
                var bounds = new BoundsInt(min.x - lodStep, min.y - lodStep, min.z - lodStep, size, size, size);
                edits.CollectBricks(bounds, brickScratch);
                brickCount = brickScratch.Count;
            }

            if (brickCount > 0)
            {
                EnsureEditCapacity(set, brickCount);
                if (coordScratch.Length < brickCount * 3)
                    coordScratch = new int[Mathf.NextPowerOfTwo(brickCount * 3)];
                if (editScratch.Length < brickCount * VoxelEditLayer.BrickVolume)
                    editScratch = new float[Mathf.NextPowerOfTwo(brickCount * VoxelEditLayer.BrickVolume)];

                for (int b = 0; b < brickCount; b++)
                {
                    (Vector3Int coord, float[] data) = brickScratch[b];
                    coordScratch[b * 3] = coord.x;
                    coordScratch[b * 3 + 1] = coord.y;
                    coordScratch[b * 3 + 2] = coord.z;
                    int baseIndex = b * VoxelEditLayer.BrickVolume;
                    for (int i = 0; i < VoxelEditLayer.BrickVolume; i++)
                    {
                        float value = data[i];
                        editScratch[baseIndex + i] = float.IsNaN(value) ? -1f : value;
                    }
                }
                set.EditCoords.SetData(coordScratch, 0, 0, brickCount * 3);
                set.EditData.SetData(editScratch, 0, 0, brickCount * VoxelEditLayer.BrickVolume);
            }

            shader.SetInt("_EditBrickCount", brickCount);
        }

        void OnCountReady(AsyncGPUReadbackRequest request, ChunkBuildJob job, JobSet set,
            ConcurrentQueue<ChunkBuildResult> results)
        {
            if (disposed)
            {
                ReleaseSet(set);
                return;
            }
            if (request.hasError)
            {
                results.Enqueue(FailedResult(job));
                ReleaseSet(set);
                return;
            }
            if (job.Superseded)
            {
                ReleaseSet(set);
                return;
            }

            int triangleCount = Mathf.Min(request.GetData<int>()[0], triangleCapacity);
            if (triangleCount <= 0)
            {
                results.Enqueue(new ChunkBuildResult { Key = job.Key, Mask = job.Mask, Ticket = job });
                ReleaseSet(set);
                return;
            }

            AsyncGPUReadback.Request(set.Triangles, triangleCount * TriangleByteStride, 0,
                dataRequest => OnDataReady(dataRequest, job, set, results, triangleCount));
        }

        void OnDataReady(AsyncGPUReadbackRequest request, ChunkBuildJob job, JobSet set,
            ConcurrentQueue<ChunkBuildResult> results, int triangleCount)
        {
            if (disposed)
            {
                ReleaseSet(set);
                return;
            }
            if (request.hasError)
            {
                results.Enqueue(FailedResult(job));
                ReleaseSet(set);
                return;
            }
            if (job.Superseded)
            {
                ReleaseSet(set);
                return;
            }

            int floatCount = triangleCount * TriangleFloatStride;
            NativeArray<float> data = request.GetData<float>();
            float[] managed = ArrayPool<float>.Shared.Rent(floatCount);
            NativeArray<float>.Copy(data, 0, managed, 0, floatCount);

            results.Enqueue(new ChunkBuildResult
            {
                Key = job.Key,
                Mask = job.Mask,
                Ticket = job,
                RawVertices = managed,
                RawVertexCount = triangleCount * 3,
            });
            ReleaseSet(set);
        }

        static ChunkBuildResult FailedResult(ChunkBuildJob job) =>
            new ChunkBuildResult { Key = job.Key, Mask = job.Mask, Ticket = job, Failed = true };

        // ---- job set pool ----

        JobSet RentSet()
        {
            if (setPool.Count > 0)
                return setPool.Pop();

            var set = new JobSet
            {
                Volume = new ComputeBuffer(pointsPerAxis * pointsPerAxis * pointsPerAxis, sizeof(float)),
                Triangles = new ComputeBuffer(triangleCapacity, TriangleByteStride, ComputeBufferType.Append),
                TriangleCount = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw),
                EditCoords = new ComputeBuffer(1, 3 * sizeof(int)),
                EditData = new ComputeBuffer(VoxelEditLayer.BrickVolume, sizeof(float)),
                EditCapacity = 1,
            };
            allSets.Add(set);
            return set;
        }

        void EnsureEditCapacity(JobSet set, int bricks)
        {
            if (set.EditCapacity >= bricks)
                return;
            int capacity = Mathf.NextPowerOfTwo(bricks);
            set.EditCoords.Release();
            set.EditData.Release();
            set.EditCoords = new ComputeBuffer(capacity, 3 * sizeof(int));
            set.EditData = new ComputeBuffer(capacity * VoxelEditLayer.BrickVolume, sizeof(float));
            set.EditCapacity = capacity;
        }

        void ReleaseSet(JobSet set)
        {
            inFlight--;
            if (!disposed)
                setPool.Push(set);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            // Forces every pending readback to complete (their callbacks run and drop out on
            // the disposed flag), so no request still references a buffer we are releasing.
            AsyncGPUReadback.WaitAllRequests();

            foreach (JobSet set in allSets)
            {
                set.Volume.Release();
                set.Triangles.Release();
                set.TriangleCount.Release();
                set.EditCoords.Release();
                set.EditData.Release();
            }
            allSets.Clear();
            setPool.Clear();

            permBuffer.Release();
            foreach (ComputeBuffer table in tableBuffers)
                table.Release();
            tableBuffers.Clear();

            UnityEngine.Object.Destroy(shader);
        }
    }
}
