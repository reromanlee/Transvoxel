using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using reromanlee.Transvoxel.Density;
using reromanlee.Transvoxel.Meshing;
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
    /// the GPU. Each readback is then WELDED into an indexed mesh on a worker task
    /// (<see cref="GpuMeshWelder"/>) before entering the shared completed-builds pipeline:
    /// GPU chunks end up rendering exactly like CPU chunks (~3x fewer vertices than the
    /// raw soup), which is what keeps the frame rate flat at large view distances.
    ///
    /// Player edits live in a RESIDENT brick pool on the GPU: bricks upload once (and
    /// re-upload individually when a terraform stroke changes them via
    /// <see cref="NotifyEditsChanged"/>); a chunk build only sends the few slot indices it
    /// overlaps. Heavy terraforming therefore adds no per-chunk transfer cost.
    ///
    /// Per-job GPU buffers are pooled; at most <c>gpuJobsInFlight</c> chunks are on the
    /// GPU at once. Main thread only (worker tasks touch no GPU objects). Created per
    /// pipeline; any settings change rebuilds it.
    /// </summary>
    public sealed class GpuChunkBuilder : IDisposable
    {
        // With a material palette every soup vertex carries one extra float (the material
        // id), switched by the TRANSVOXEL_MATERIALS kernel variant — chunks without a
        // palette keep the leaner readbacks.
        const string MaterialsKeyword = "TRANSVOXEL_MATERIALS";

        /// <summary>Packed material uints per brick: 16³ ids, 4 per uint.</summary>
        const int PackedUintsPerBrick = VoxelEditLayer.BrickVolume / 4;

        // Cached uniform ids — these are set per dispatch, so avoid the string hashing.
        static readonly int MinVoxelXId = Shader.PropertyToID("_MinVoxelX");
        static readonly int MinVoxelYId = Shader.PropertyToID("_MinVoxelY");
        static readonly int MinVoxelZId = Shader.PropertyToID("_MinVoxelZ");
        static readonly int LodStepId = Shader.PropertyToID("_LodStep");
        static readonly int TransitionMaskId = Shader.PropertyToID("_TransitionMask");
        static readonly int FlatShadingId = Shader.PropertyToID("_FlatShading");
        static readonly int FaceId = Shader.PropertyToID("_Face");
        static readonly int EditSlotCountId = Shader.PropertyToID("_EditSlotCount");
        static readonly int VolumeId = Shader.PropertyToID("_Volume");
        static readonly int TrianglesId = Shader.PropertyToID("_Triangles");
        static readonly int ChunkEditSlotsId = Shader.PropertyToID("_ChunkEditSlots");
        static readonly int EditBrickCoordsId = Shader.PropertyToID("_EditBrickCoords");
        static readonly int EditBrickDataId = Shader.PropertyToID("_EditBrickData");
        static readonly int EditBrickMaterialsId = Shader.PropertyToID("_EditBrickMaterials");

        sealed class JobSet
        {
            public ComputeBuffer Volume;        // (cells+3)^3 floats
            public ComputeBuffer Triangles;     // append, worst-case capacity
            public ComputeBuffer TriangleCount; // 1 raw uint, CopyCount target
            public ComputeBuffer Slots;         // per-chunk edit pool slots
            public int SlotCapacity;
        }

        readonly ComputeShader shader; // private instance — uniforms are per-builder state
        readonly VoxelEditLayer edits;
        readonly VoxelMaterialLayer materials; // null = no palette, kernels skip material work
        readonly bool materialsEnabled;
        readonly int kernelVolume;
        readonly int kernelRegular;
        readonly int kernelTransition;
        readonly int chunkCells;
        readonly int pointsPerAxis;
        readonly int triangleCapacity;
        readonly int triangleFloatStride;
        readonly int triangleByteStride;
        readonly int maxInFlight;

        readonly ComputeBuffer permBuffer;
        readonly List<ComputeBuffer> tableBuffers = new List<ComputeBuffer>();

        readonly Stack<JobSet> setPool = new Stack<JobSet>();
        readonly List<JobSet> allSets = new List<JobSet>();

        // ---- resident edit brick pool ----
        // One slot space for BOTH layers: density edits and material ids share brick
        // geometry, so the pool holds the union of their bricks and every voxel resolves
        // with a single slot lookup. A brick missing from one layer uploads as that
        // layer's neutral data (density: all untouched; materials: all id 0).
        readonly Dictionary<Vector3Int, int> brickSlots = new Dictionary<Vector3Int, int>();
        ComputeBuffer editCoordsBuffer;    // int3 per slot
        ComputeBuffer editDataBuffer;      // BrickVolume floats per slot
        ComputeBuffer editMaterialsBuffer; // PackedUintsPerBrick uints per slot (palette only)
        int brickCount;
        int brickCapacity;
        int editPoolGeneration;
        readonly float[] brickConvertScratch = new float[VoxelEditLayer.BrickVolume];
        readonly uint[] materialPackScratch = new uint[PackedUintsPerBrick];
        readonly int[] coordUploadScratch = new int[3];
        readonly List<(Vector3Int coord, float[] data)> brickScratch =
            new List<(Vector3Int, float[])>();
        readonly List<(Vector3Int coord, byte[] data)> materialBrickScratch =
            new List<(Vector3Int, byte[])>();
        readonly HashSet<Vector3Int> unionCoordScratch = new HashSet<Vector3Int>();

        // Material brick data for the coords currently being uploaded; refreshed by
        // CollectBrickUnion and (with the full layer) by RebuildEditPool.
        readonly Dictionary<Vector3Int, byte[]> materialByCoord =
            new Dictionary<Vector3Int, byte[]>();
        int[] slotScratch = Array.Empty<int>();

        int inFlight;
        bool disposed;

        public int InFlight => inFlight;

        public GpuChunkBuilder(ComputeShader shaderAsset, TransvoxelSettings settings, VoxelEditLayer editLayer,
            VoxelMaterialLayer materialLayer = null)
        {
            shader = UnityEngine.Object.Instantiate(shaderAsset);
            edits = editLayer;
            materials = materialLayer;
            materialsEnabled = materialLayer != null;
            if (materialsEnabled)
                shader.EnableKeyword(MaterialsKeyword);
            else
                shader.DisableKeyword(MaterialsKeyword);
            triangleFloatStride = 3 * (materialsEnabled
                ? GpuMeshWelder.FloatsPerVertexWithMaterial
                : GpuMeshWelder.FloatsPerVertex);
            triangleByteStride = triangleFloatStride * 4;
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

            RebuildEditPool(64);
        }

        void BindTable(int kernel, string name, int[] data)
        {
            var buffer = new ComputeBuffer(data.Length, sizeof(int));
            buffer.SetData(data);
            tableBuffers.Add(buffer);
            shader.SetBuffer(kernel, name, buffer);
        }

        // ------------------------------------------------------------------ edit brick pool

        /// <summary>
        /// Call after the edit or material layer changed inside <paramref name="voxelBounds"/>
        /// (one terraform stroke): the touched bricks are re-uploaded into their pool slots.
        /// Cost is proportional to the stroke, never to the amount of terrain edited so far.
        /// </summary>
        public void NotifyEditsChanged(BoundsInt voxelBounds)
        {
            if (disposed || edits == null)
                return;
            CollectBrickUnion(voxelBounds);
            int generation = editPoolGeneration;
            foreach (var (coord, data) in brickScratch)
            {
                UploadBrick(coord, data);
                if (editPoolGeneration != generation)
                    return; // pool grew and re-uploaded everything, including the rest
            }
        }

        /// <summary>
        /// Fills <see cref="brickScratch"/> with every density brick in the region plus —
        /// with materials — the painted bricks, paired via <see cref="materialByCoord"/>.
        /// Material-only bricks (painted ground that was never density-edited) enter the
        /// union with null density data; <see cref="UploadBrick"/> turns that into a brick
        /// of untouched voxels, which the density kernel falls through per voxel anyway.
        /// </summary>
        void CollectBrickUnion(BoundsInt voxelBounds)
        {
            edits.CollectBricks(voxelBounds, brickScratch);
            materialByCoord.Clear();
            if (materials == null)
                return;

            materials.CollectBricks(voxelBounds, materialBrickScratch);
            if (materialBrickScratch.Count == 0)
                return;
            foreach (var (coord, data) in materialBrickScratch)
                materialByCoord[coord] = data;

            unionCoordScratch.Clear();
            foreach (var (coord, _) in brickScratch)
                unionCoordScratch.Add(coord);
            foreach (var (coord, data) in materialBrickScratch)
            {
                if (!unionCoordScratch.Contains(coord))
                    brickScratch.Add((coord, null));
            }
        }

        void UploadBrick(Vector3Int coord, float[] data)
        {
            if (!brickSlots.TryGetValue(coord, out int slot))
            {
                if (brickCount >= brickCapacity)
                {
                    RebuildEditPool(brickCount + 1); // re-collects and uploads every brick
                    return;
                }
                slot = brickCount++;
                brickSlots.Add(coord, slot);
                coordUploadScratch[0] = coord.x;
                coordUploadScratch[1] = coord.y;
                coordUploadScratch[2] = coord.z;
                editCoordsBuffer.SetData(coordUploadScratch, 0, slot * 3, 3);
            }

            if (data == null)
            {
                // Material-only brick: no density was ever edited here.
                for (int i = 0; i < VoxelEditLayer.BrickVolume; i++)
                    brickConvertScratch[i] = -1f;
            }
            else
            {
                for (int i = 0; i < VoxelEditLayer.BrickVolume; i++)
                {
                    float value = data[i];
                    brickConvertScratch[i] = float.IsNaN(value) ? -1f : value;
                }
            }
            editDataBuffer.SetData(brickConvertScratch, 0, slot * VoxelEditLayer.BrickVolume,
                VoxelEditLayer.BrickVolume);

            if (materialsEnabled)
            {
                materialByCoord.TryGetValue(coord, out byte[] ids);
                PackMaterialBrick(ids); // all id 0 when the brick holds no paint
                editMaterialsBuffer.SetData(materialPackScratch, 0, slot * PackedUintsPerBrick,
                    PackedUintsPerBrick);
            }
        }

        void PackMaterialBrick(byte[] ids)
        {
            if (ids == null)
            {
                Array.Clear(materialPackScratch, 0, PackedUintsPerBrick);
                return;
            }
            for (int i = 0; i < PackedUintsPerBrick; i++)
            {
                int b = i * 4;
                materialPackScratch[i] = (uint)(ids[b] | (ids[b + 1] << 8)
                                                | (ids[b + 2] << 16) | (ids[b + 3] << 24));
            }
        }

        /// <summary>(Re)creates the pool with room for at least <paramref name="minCapacity"/> bricks and uploads both layers whole.</summary>
        void RebuildEditPool(int minCapacity)
        {
            editPoolGeneration++;
            brickSlots.Clear();
            brickCount = 0;

            // Local lists: a rebuild can trigger inside a loop that is iterating the
            // shared scratch (UploadBrick growing the pool mid-NotifyEditsChanged).
            var allBricks = new List<(Vector3Int coord, float[] data)>();
            if (edits != null)
                edits.CollectAllBricks(allBricks);
            materialByCoord.Clear();
            if (materials != null)
            {
                var allMaterials = new List<(Vector3Int coord, byte[] data)>();
                materials.CollectAllBricks(allMaterials);
                var densityCoords = new HashSet<Vector3Int>();
                foreach (var (coord, _) in allBricks)
                    densityCoords.Add(coord);
                foreach (var (coord, data) in allMaterials)
                {
                    materialByCoord[coord] = data;
                    if (!densityCoords.Contains(coord))
                        allBricks.Add((coord, null));
                }
            }
            brickCapacity = Mathf.NextPowerOfTwo(Mathf.Max(minCapacity, allBricks.Count, 64));

            editCoordsBuffer?.Release();
            editDataBuffer?.Release();
            editMaterialsBuffer?.Release();
            editCoordsBuffer = new ComputeBuffer(brickCapacity, 3 * sizeof(int));
            editDataBuffer = new ComputeBuffer(brickCapacity * VoxelEditLayer.BrickVolume, sizeof(float));
            if (materialsEnabled)
                editMaterialsBuffer = new ComputeBuffer(brickCapacity * PackedUintsPerBrick, sizeof(uint));

            foreach (var (coord, data) in allBricks)
                UploadBrick(coord, data);

            shader.SetBuffer(kernelVolume, EditBrickCoordsId, editCoordsBuffer);
            shader.SetBuffer(kernelVolume, EditBrickDataId, editDataBuffer);
            shader.SetBuffer(kernelTransition, EditBrickCoordsId, editCoordsBuffer);
            shader.SetBuffer(kernelTransition, EditBrickDataId, editDataBuffer);
            if (materialsEnabled)
            {
                // The regular kernel reads the pool only for materials (density comes from
                // the volume grid), so it binds nothing without a palette.
                shader.SetBuffer(kernelRegular, EditBrickCoordsId, editCoordsBuffer);
                shader.SetBuffer(kernelRegular, EditBrickMaterialsId, editMaterialsBuffer);
                shader.SetBuffer(kernelTransition, EditBrickMaterialsId, editMaterialsBuffer);
            }
        }

        // ------------------------------------------------------------------ dispatch

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

            BindJobEdits(set, min, lodStep);

            shader.SetInt(MinVoxelXId, min.x);
            shader.SetInt(MinVoxelYId, min.y);
            shader.SetInt(MinVoxelZId, min.z);
            shader.SetInt(LodStepId, lodStep);
            shader.SetInt(TransitionMaskId, job.Mask);
            shader.SetInt(FlatShadingId, job.SmoothShading ? 0 : 1);

            shader.SetBuffer(kernelVolume, VolumeId, set.Volume);
            int volumeGroups = (pointsPerAxis + 3) / 4;
            shader.Dispatch(kernelVolume, volumeGroups, volumeGroups, volumeGroups);

            set.Triangles.SetCounterValue(0);
            shader.SetBuffer(kernelRegular, VolumeId, set.Volume);
            shader.SetBuffer(kernelRegular, TrianglesId, set.Triangles);
            int regularGroups = (chunkCells + 3) / 4;
            shader.Dispatch(kernelRegular, regularGroups, regularGroups, regularGroups);

            if (job.Mask != 0 && job.Key.Lod > 0)
            {
                shader.SetBuffer(kernelTransition, VolumeId, set.Volume);
                shader.SetBuffer(kernelTransition, TrianglesId, set.Triangles);
                int transitionGroups = (chunkCells + 7) / 8;
                for (int face = 0; face < 6; face++)
                {
                    if ((job.Mask & (1 << face)) == 0)
                        continue;
                    shader.SetInt(FaceId, face);
                    shader.Dispatch(kernelTransition, transitionGroups, transitionGroups, 1);
                }
            }

            ComputeBuffer.CopyCount(set.Triangles, set.TriangleCount, 0);
            AsyncGPUReadback.Request(set.TriangleCount,
                request => OnCountReady(request, job, set, results));
        }

        /// <summary>
        /// Uploads the pool slot indices of the bricks this chunk's samples can read — a
        /// handful of ints, not the brick data itself (that is already resident).
        /// </summary>
        void BindJobEdits(JobSet set, Vector3Int min, int lodStep)
        {
            int slotCount = 0;
            if (edits != null && brickCount > 0)
            {
                int size = (chunkCells + 2) * lodStep + 1;
                var bounds = new BoundsInt(min.x - lodStep, min.y - lodStep, min.z - lodStep, size, size, size);
                CollectBrickUnion(bounds);
                slotCount = brickScratch.Count;

                if (slotCount > 0)
                {
                    EnsureSlotCapacity(set, slotCount);
                    if (slotScratch.Length < slotCount)
                        slotScratch = new int[Mathf.NextPowerOfTwo(slotCount)];

                    int generation = editPoolGeneration;
                    for (int b = 0; b < slotCount; b++)
                    {
                        var (coord, data) = brickScratch[b];
                        if (!brickSlots.TryGetValue(coord, out int slot))
                        {
                            // Shouldn't happen (NotifyEditsChanged keeps the pool a superset),
                            // but recover rather than mesh with stale density.
                            UploadBrick(coord, data);
                            if (editPoolGeneration != generation)
                            {
                                // The pool grew and every slot was reassigned; restart so
                                // already-written indices aren't stale.
                                generation = editPoolGeneration;
                                b = -1;
                                continue;
                            }
                            if (!brickSlots.TryGetValue(coord, out slot))
                                slot = 0; // vanished mid-frame; coord check in the kernel makes this benign
                        }
                        slotScratch[b] = slot;
                    }
                    set.Slots.SetData(slotScratch, 0, 0, slotCount);
                }
            }

            shader.SetInt(EditSlotCountId, slotCount);
            shader.SetBuffer(kernelVolume, ChunkEditSlotsId, set.Slots);
            shader.SetBuffer(kernelTransition, ChunkEditSlotsId, set.Slots);
            if (materialsEnabled)
                shader.SetBuffer(kernelRegular, ChunkEditSlotsId, set.Slots);
        }

        // ------------------------------------------------------------------ readback + weld

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
                results.Enqueue(new ChunkBuildResult
                {
                    Key = job.Key,
                    Mask = job.Mask,
                    Ticket = job,
                    Buffers = MeshBuffers.Rent(), // empty — air or solid chunk
                });
                ReleaseSet(set);
                return;
            }

            AsyncGPUReadback.Request(set.Triangles, triangleCount * triangleByteStride, 0,
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

            // Copy the soup out (the native readback memory dies with this callback), free
            // the GPU buffers immediately, and weld into an indexed mesh off the main thread.
            int floatCount = triangleCount * triangleFloatStride;
            NativeArray<float> data = request.GetData<float>();
            float[] soup = ArrayPool<float>.Shared.Rent(floatCount);
            NativeArray<float>.Copy(data, 0, soup, 0, floatCount);
            ReleaseSet(set);

            bool withMaterials = materialsEnabled;
            Task.Run(() =>
            {
                MeshBuffers buffers = null;
                try
                {
                    if (job.Superseded)
                        return;
                    buffers = MeshBuffers.Rent();
                    var welder = GpuMeshWelder.Rent();
                    try
                    {
                        welder.Weld(soup, triangleCount, buffers, withMaterials);
                    }
                    finally
                    {
                        GpuMeshWelder.Return(welder);
                    }
                    if (withMaterials)
                    {
                        // Same worker-side finish as the CPU pipeline: ids -> blend attribute.
                        var encoder = MaterialBlendEncoder.Rent();
                        try
                        {
                            encoder.Encode(buffers);
                        }
                        finally
                        {
                            MaterialBlendEncoder.Return(encoder);
                        }
                    }
                    results.Enqueue(new ChunkBuildResult
                    {
                        Key = job.Key,
                        Mask = job.Mask,
                        Ticket = job,
                        Buffers = buffers,
                    });
                    buffers = null; // ownership transferred
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    if (buffers != null)
                        MeshBuffers.Return(buffers);
                    results.Enqueue(FailedResult(job));
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(soup);
                }
            });
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
                Triangles = new ComputeBuffer(triangleCapacity, triangleByteStride, ComputeBufferType.Append),
                TriangleCount = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw),
                Slots = new ComputeBuffer(64, sizeof(int)),
                SlotCapacity = 64,
            };
            allSets.Add(set);
            return set;
        }

        static void DestroySetBuffers(JobSet set)
        {
            set.Volume.Release();
            set.Triangles.Release();
            set.TriangleCount.Release();
            set.Slots.Release();
        }

        void EnsureSlotCapacity(JobSet set, int slots)
        {
            if (set.SlotCapacity >= slots)
                return;
            set.Slots.Release();
            set.SlotCapacity = Mathf.NextPowerOfTwo(slots);
            set.Slots = new ComputeBuffer(set.SlotCapacity, sizeof(int));
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
                DestroySetBuffers(set);
            allSets.Clear();
            setPool.Clear();

            permBuffer.Release();
            foreach (ComputeBuffer table in tableBuffers)
                table.Release();
            tableBuffers.Clear();
            editCoordsBuffer?.Release();
            editDataBuffer?.Release();
            editMaterialsBuffer?.Release();

            UnityEngine.Object.Destroy(shader);
        }
    }
}
