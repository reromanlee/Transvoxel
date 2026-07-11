# Transvoxel

A clean, modular implementation of Eric Lengyel's **Transvoxel** algorithm — seamless
level-of-detail (LOD) triangulation of a voxel density field — driven by an octree and
built for large, editable, real-time landscapes.

<table>
  <tr>
    <td>
      <img src=".github/transvoxel-lods.gif" alt="Transvoxel LODs" width="100%">
    </td>
    <td>
      <img src=".github/transvoxel-terraforming.gif" alt="Transvoxel terraforming" width="100%">
    </td>
  </tr>
</table>

## Separation of concerns

The pipeline is four independent layers. Each one only knows about the layer below it
through a tiny interface, so you can replace any of them on its own.

| Layer | Folder | Responsibility | Key type |
|-------|--------|----------------|----------|
| **Density** — *where* is there ground? | `Runtime/Density/` | Answers `SampleVoxel(x,y,z) → 0..1`. Two stacked layers: player edits (A) over procedural landscape (B). | `IDensitySource`, `LayeredDensitySource` |
| **Meshing (CPU)** — *how* do we triangulate a chunk? | `Runtime/Meshing/` | Turns a sampled chunk into vertices/normals/UVs using the Transvoxel tables. Regular cells + transition cells. | `TransvoxelMesher`, `TransvoxelTransitionMesher` |
| **Meshing (GPU)** — the same, in compute shaders | `Runtime/Gpu/`, `Runtime/Resources/TransvoxelCompute.compute` | Density (noise + edit overlay) and the whole triangulation run in three compute kernels; triangles stream back via async readback. | `GpuChunkBuilder`, `TransvoxelGpuTables` |
| **Octree** — *what* should exist right now? | `Runtime/Octree/` | Pure function of viewer position → the set of chunks and which faces need transition cells. | `TerrainOctree` → `ChunkDrawCommand` |
| **Orchestrator** — wire it together | `Runtime/TransvoxelTerrain.cs` | Diffs the octree's wishes against the live scene, feeds a distance-prioritized build queue (CPU workers or GPU dispatches), uploads finished meshes under a per-frame time budget. | `TransvoxelTerrain` |

The raw lookup tables translated from Lengyel's C++ live in
`Runtime/TransvoxelDataTables.cs` (Concept.txt #3).

## Quick start — the demo

1. Create an empty scene.
2. Add an empty GameObject and put **`TransvoxelDemo`** (`Runtime/Demo/`) on it.
3. Press **Play**.

It spawns a camera, a light and the terrain, then lets you:

- **RMB drag** to look, **WASD + Q/E** to fly (**Shift** = faster),
- **LMB** to dig, **Shift + LMB** to build,
- toggle smooth/flat shading and LOD colorization from the on-screen overlay.

> Camera and terraforming controls use the legacy Input Manager. If your project is set to
> the new Input System only, open **Project Settings ▸ Player ▸ Active Input Handling** and
> choose **Both**.

## Using it in your own scene

1. Add **`TransvoxelTerrain`** to a GameObject.
2. Assign a **viewer** transform (defaults to `Camera.main`).
3. Assign a **settings** asset — create one via **Assets ▸ Create ▸ Transvoxel ▸ Terrain
   Settings** — or leave it empty for sensible defaults.

```csharp
// Terraforming from your own tools:
terrain.Terraform(worldPoint, radius: 5f, strength: 0.9f, build: false); // dig
terrain.RaycastDensity(cameraRay, 400f, out Vector3 hit);                // find the surface
```

### Settings (`TransvoxelSettings`) apply live

Every field on the settings asset is applied **while the game is running** — change the view
distance, LOD count, noise frequency/height, iso level, etc. in the Inspector during Play and
the terrain rebuilds itself the next frame (player edits are preserved). This is wired through
`TransvoxelSettings.Changed`; if you change a field from code, call `settings.NotifyChanged()`.

Notable knobs (Concept.txt #4, #6):

- **meshingBackend** — `CpuThreads` (worker threads, runs everywhere) or `GpuCompute`
  (compute shaders, see below). Switchable live, edits preserved.
- **maxLodLevels** — LOD levels above LOD0 (e.g. `4` → LOD0..LOD4).
- **viewDistance** — meters beyond which nothing is generated.
- **lodSplitFactor** — higher = more detail further away (more chunks).
- **smoothShading** — smooth shared-vertex normals vs. flat low-poly triangles.
- **colliderMaxLod** — which LODs get a `MeshCollider` (baked off the main thread).
- **chunkFadeInSeconds / edgeFadeFraction** — stipple cross-fade (see *Dithered fading*
  below): how long new chunks take to dither in, and the per-pixel dissolve band at the
  draw-distance edge.
- **meshApplyBudgetMs** — the main-thread time slice per frame for uploading finished
  meshes. Bursts of hundreds of chunks (teleport, high-speed flight) spread over frames
  instead of spiking one.
- **gpuJobsInFlight** — GPU mode: chunks allowed on the GPU at once (throughput vs. VRAM).
- **lodSwapLinger** — smoothness window for LOD swaps: how long a replaced chunk may linger
  after its replacement is ready, and how long a re-meshed chunk may wait for the neighbours
  that changed its transition mask before swapping anyway. Raise it if you see brief holes
  or seams when moving fast; 0 disables both protections for minimal latency/overdraw.

## CPU, GPU and Hybrid meshing backends

All backends produce the same landscape (the GPU noise runs the same permutation table as
the CPU's `FractalNoise` — same seed, same terrain) and share the octree, the priority
queue, colliders and terraforming. Set `meshingBackend` on the settings asset:

- **CpuThreads** — chunks are sampled and meshed on a pool of worker tasks, one per core.
- **GpuCompute** — three kernels in `TransvoxelCompute.compute` do the heavy lifting:
  1. `CSVolume` builds the chunk's density grid: procedural noise overridden by the
     player-edit bricks;
  2. `CSRegular` runs one thread per cell over Lengyel's tables (uploaded once as buffers —
     they are far too large for HLSL initializers);
  3. `CSTransition` stitches the LOD seams with transition cells, one thread per face cell.
  Finished triangles return via `AsyncGPUReadback` (two-stage: count, then exactly that many
  triangles), so the CPU never blocks on the GPU. Because GPU cells cannot share the paper's
  serial reuse decks, they emit triangle soup — which a light worker task then **welds back
  into an indexed mesh** (coincident vertices are bit-identical, so welding is an exact hash,
  no epsilon). GPU chunks therefore render exactly like CPU chunks: ~3× fewer vertices than
  the raw soup, vertex-cache-friendly, small uploads.
- **Hybrid** — CPU workers *and* the GPU pipeline pull from the same nearest-first queue at
  once: whichever processor is free builds the next chunk. Highest build throughput — ideal
  for teleports and very large view distances.

**The edit layer never round-trips.** Player-edit bricks (16³ voxels) live in one resident
GPU buffer pool: uploaded once, then only the bricks touched by a terraform stroke are
re-uploaded — and each chunk build sends just the few pool slot indices it overlaps. Editing
half the map costs GPU builds nothing extra per chunk. The saveable copy stays in C#
(`VoxelEditLayer`), exactly as before.

If the platform lacks compute shaders or async readback (or a custom `DensityOverride` is
active — arbitrary C# can't run on the GPU), GPU and Hybrid fall back to `CpuThreads` with a
console warning.

## Dithered fading (stipple cross-fade)

Nothing about the landscape ever pops. Every visual change runs through one screen-space
Bayer-dither clip — the same technique as Unity LOD Group cross-fading:

- **Fade-in** (`chunkFadeInSeconds`): a freshly built chunk dithers from invisible to solid.
- **Fade-out**: a retired chunk (LOD swap, moved out of range) dithers away over the same
  duration — and only after its replacements are *fully* faded in underneath.
- **Mesh-swap cross-fade**: when a live chunk re-meshes (terraforming, a transition-mask
  change as LOD rings shift), its old surface moves onto a short-lived *ghost* that dithers
  out with the **complementary** stipple pattern while the new mesh dithers in — at every
  moment each screen pixel is drawn by exactly one of the two, so the swap is seamless:
  no holes, no double-brightness. Rapid re-edits keep at most one ghost per chunk.
- **Draw-distance dissolve** (`edgeFadeFraction`): terrain fades out toward `viewDistance`
  **per pixel** (driven by shader globals, not per chunk), so even a kilometers-wide coarse
  chunk dissolves smoothly like fog. Chunks leaving the view range are fully transparent
  before they are actually removed; new frontier chunks are born inside the faded band and
  brighten as you approach.

Fading needs shader support. The bundled **`Transvoxel/Lit Dithered`** shader (URP and
Built-in pipeline subshaders; the default runtime material uses it automatically outside
HDRP) implements it. To make your **own** material fade, add this to its shader — or
reproduce it with a Custom Function node in Shader Graph:

```hlsl
// Properties: _TransvoxelFade("Fade (master)", Range(0,1)) = 1
// (declaring this property is what marks a shader as fade-aware)
float _TransvoxelFade;
// Globals set by TransvoxelTerrain every frame:
float  _TransvoxelTime;
float  _TransvoxelFadeSeconds;
float4 _TransvoxelViewerPos;
float  _TransvoxelViewDistance;
float  _TransvoxelEdgeFadeBand;

// VERTEX stage — the terrain bakes (fadeStartTime, ghostFlag) into UV2 (TEXCOORD1).
// Pass the result to the fragment stage as a varying:
float TransvoxelVertexFade(float2 fadeData)
{
    if (_TransvoxelFadeSeconds <= 0) return 1;
    float t = saturate((_TransvoxelTime - fadeData.x) / _TransvoxelFadeSeconds);
    return fadeData.y > 0.5 ? -(1 - t) : t; // negative = cross-fade ghost
}

// FRAGMENT stage (positionCS = SV_POSITION, positionWS = world position):
float fade = vertexFade * _TransvoxelFade;
float edge = 1.0;
if (_TransvoxelEdgeFadeBand > 0)
    edge = saturate((_TransvoxelViewDistance
                     - distance(positionWS, _TransvoxelViewerPos.xyz))
                    / _TransvoxelEdgeFadeBand);
if (fade < 1 || edge < 1)
{
    const float dither[16] = { 0.5/16, 8.5/16, 2.5/16, 10.5/16, 12.5/16, 4.5/16, 14.5/16, 6.5/16,
                               3.5/16, 11.5/16, 1.5/16, 9.5/16, 15.5/16, 7.5/16, 13.5/16, 5.5/16 };
    uint2 p = uint2(positionCS.xy) & 3;
    float threshold = dither[p.y * 4 + p.x];
    if (fade < 0) // ghost: draw exactly what the successor doesn't draw yet
        clip(min(threshold - (1 + fade), edge - threshold));
    else
        clip(min(fade, edge) - threshold);
}
```

The terrain checks at startup whether its material declares `_TransvoxelFade`. If it does
not, all fading (including cross-fade ghosts and the edge dissolve) is cleanly disabled —
chunks switch instantly, with a console warning telling you how to enable it. So a custom
material without the snippet keeps working; it just cannot fade.

## How the seamless LOD works (the interesting part)

Adjacent chunks may differ by one LOD level (the octree enforces this **2:1 balance**). Where
a coarse chunk meets finer neighbours, a naïve mesh leaves cracks. The Transvoxel fix, owned
entirely by the **coarser** chunk:

- its **full-resolution** transition face sits exactly on the boundary and reproduces the
  finer neighbour's triangulation bit-for-bit, while
- its **half-resolution** face vertices are pushed inward (the paper's *secondary position*
  shift) to land exactly on the coarse chunk's own shrunk boundary.

Together the three surfaces — fine mesh, transition sheet, coarse mesh — close every seam with
no shared data between neighbours. Correctness is proven by a headless watertightness test
(every LOD boundary produces **0 unmatched edges**).

### Performance notes

Everything per-frame is bounded, so the frame rate stays flat at any movement speed — fly,
sprint or teleport; the worst case is unbuilt terrain filling in near-first, never a freeze:

- **Distance-prioritized builds.** Scheduled chunks sit in a priority queue keyed by
  distance to the viewer; CPU workers (or the GPU pump) always take the nearest one, and the
  whole queue re-sorts whenever the viewer moves. After a teleport the ground under the
  player meshes first, the horizon last. Terraform rebuilds jump the queue entirely.
- **Async octree selection.** Deciding *what* should exist walks thousands of octree nodes;
  that walk runs on a worker task, and only the cheap diff against the live scene touches
  the main thread.
- **Time-budgeted uploads.** Finished meshes upload under `meshApplyBudgetMs` per frame
  (plus a count cap), so a burst of hundreds of finished chunks spreads across frames.
- **Pooled chunk views.** Chunk GameObjects and their `Mesh` objects are recycled, not
  created/destroyed — high-speed chunk churn costs no instantiation or GC spikes.
- **Coarse LODs sample only their own lattice points**, so looking far into the distance
  costs far less than full-resolution detail (Concept.txt #5).
- **Hole-free swaps.** Old chunks stay on screen until their replacements are ready *and*
  the newly selected set has settled (plus `lodSwapLinger`). A chunk whose transition mask
  changed waits (bounded by `lodSwapLinger`) for the neighbours that caused the change to be
  on screen before swapping, so LOD-ring shifts don't flash seams in the distance. All
  chunks touched by one terraform stroke swap **in the same frame** (an "edit group"), so
  brushing never flashes a one-frame hole along a chunk border.
- **Bounded retirement.** Obsolete chunks are destroyed under a per-frame cap, so even a
  far teleport (thousands of chunks replaced at once) never spends a whole frame cleaning up.
- **Batching-proof fades.** Fade parameters are baked into each mesh (a UV2 channel of
  start time + ghost flag) and animated by the shader from global time — no per-renderer
  state, no MaterialPropertyBlocks, no per-chunk materials. This survives every render
  path (SRP Batcher, URP GPU Resident Drawer, Built-in) and costs zero per-frame CPU.
  Only the debug LOD tint uses a property block.
- Collider bakes run off the main thread (`Physics.BakeMesh`), attached when ready.

## Tests

EditMode tests live in `Editor/` (Window ▸ General ▸ Test Runner ▸ EditMode). They prove the
core invariant — the union of all chunk meshes is a closed, consistently wound 2-manifold —
for single chunks, same-LOD borders, every LOD-transition face, and after a transition-mask
change (the stale-cache regression). Requires the `com.unity.test-framework` package.

## Requirements

- Unity **6000.0+** (developed and verified on **6000.5**). Uses `EntityId` (the Unity 6.2+
  replacement for instance IDs) in the collider-baking path.
- No external packages. A default lit material is created at runtime if none is assigned; a
  triplanar shader is recommended for cliffs and multi-texture terrain (Concept.txt #7).

## Reference

Lengyel, Eric. "Voxel-Based Terrain for Real-Time Virtual Simulations." PhD diss., University
of California at Davis, 2010. Data tables © 2009 Eric Lengyel, from <https://transvoxel.org/>.