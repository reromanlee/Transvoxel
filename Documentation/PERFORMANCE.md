### Performance

How Transvoxel keeps its cost bounded, how to measure it on your own hardware, and which settings trade detail for speed.

### Per-frame budget

Everything per frame is bounded, so the frame rate stays flat at any movement speed. Flying, sprinting or teleporting at worst leaves unbuilt terrain filling in nearest-first, never a freeze.

1. **Distance-prioritized builds.** Scheduled chunks sit in a priority queue keyed by distance to the viewer, and CPU workers or the GPU always take the nearest one. The queue re-sorts whenever the viewer moves, so after a teleport the ground under the player meshes first and the horizon last. Terraform rebuilds jump the queue.
2. **Async octree selection.** Deciding what should exist walks thousands of octree nodes on a worker task; only the cheap diff against the live scene touches the main thread.
3. **Time-budgeted uploads.** Finished meshes upload within `meshApplyBudgetMs` per frame, 3 ms by default, plus a per-frame count cap. A burst of hundreds of finished chunks spreads across frames instead of spiking one.
4. **Pooled chunk views.** Chunk GameObjects and their meshes are recycled rather than created and destroyed, so fast chunk churn costs no instantiation or garbage collection spikes.
5. **Coarse LODs sample only their own lattice points.** Looking far into the distance costs far less than full-resolution detail, and nothing between a coarse chunk's samples is ever read.
6. **Hole-free swaps.** Old chunks stay on screen until their replacements are ready and the new selection has settled. A chunk whose transition mask changed waits for the neighbours that caused it, bounded by `lodSwapLinger`, so shifting LOD rings don't flash seams. All chunks touched by one terraform stroke swap in the same frame.
7. **Bounded retirement.** Obsolete chunks are destroyed under a per-frame cap, so even a far teleport that replaces thousands of chunks never spends a whole frame cleaning up.
8. **Fades cost no CPU.** Fade parameters are baked into each mesh and animated by the shader from global time, with no per-renderer state, property blocks or per-chunk materials.
9. **Off-thread colliders.** Collider meshes bake on worker threads with `Physics.BakeMesh` and attach once ready.

### Measuring

The terrain measures its own footprint: only this package's work, excluding rendering, materials, textures and PhysX collider cooking.

```csharp
TransvoxelResourceStats stats = terrain.CollectStats();

Debug.Log($"CPU main {stats.MainThreadMsPerFrame:0.00} ms/frame, workers {stats.WorkerCpuMsPerSecond:0.0} ms/s, "
        + $"GPU {stats.GpuComputeMsPerSecond:0.0} ms/s, RAM {stats.RamTotalBytes >> 20} MB, VRAM {stats.GpuTotalBytes >> 20} MB");
```

1. **CPU time.** The main-thread cost of the terrain's `Update`, smoothed and as a peak, plus worker milliseconds per second across all background threads (sampling, meshing, welding, blend encoding, octree selection and collider bakes), builds per second and the average build time.
2. **GPU time.** Compute-kernel milliseconds per second, sampled by a tiny readback around each job's dispatches. A single sample is quantized to a frame boundary, since Unity exposes no per-dispatch GPU timer at runtime, but the average converges on the true cost over many jobs. Zero on the CPU backend.
3. **RAM.** Computed exactly from the structures the package owns: cached density grids, edit and material bricks, pooled and in-flight meshing buffers, and the CPU copies of chunk meshes.
4. **GPU memory.** Chunk vertex and index buffers plus every compute buffer: per-job volumes, the resident brick pool and the lookup tables.

**Window ▸ Transvoxel ▸ Terrain Stats** shows all of it live and picks the scene's terrain on its own; it's most useful in Play mode. The Interactive Demo's overlay shows a compact CPU, GPU, RAM and VRAM summary, so the numbers appear in standalone builds too.

### Tuning

| Setting | Lower | Higher |
| --- | --- | --- |
| `viewDistance` | Fewer chunks, less memory | More landscape visible |
| `lodSplitFactor` | Coarser detail sooner, fewer chunks | More detail further away |
| `maxLodLevels` | Finer far terrain, more chunks | Coarser far terrain, fewer chunks |
| `meshApplyBudgetMs` | Smoother frames, slower fill-in | Faster fill-in, longer frames during bursts |
| `gpuJobsInFlight` | Less VRAM in flight | Higher GPU throughput |
| `maxConcurrentBuilds` | More CPU left for the game | Faster CPU builds (0 uses processor count − 2) |
| `colliderMaxLod` | Fewer collider bakes (−1 disables them) | Colliders further out |
| `densityCacheChunks` | Less RAM | Faster re-meshing after edits and LOD changes |

On the GPU side, a palette without normal, occlusion or height maps uses the cheaper albedo-only shader variant. Triplanar costs roughly 3× the texture fetches, and parallax occlusion adds a ray march that fades out by **Parallax Distance**, so distant chunks don't pay for it.
