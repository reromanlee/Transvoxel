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
| **Density** — *where* is there ground? | `Runtime/Density/` | Answers `SampleVoxel(x,y,z) → 0..1`. Two stacked layers: player edits (A) over procedural landscape (B). A parallel sparse layer answers `SampleMaterial(x,y,z) → palette id`. | `IDensitySource`, `LayeredDensitySource`, `VoxelMaterialLayer` |
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
- **LMB** to dig, **Shift + LMB** to build — with the material picked in the overlay
  (the demo ships a small grass/rock/sand/snow palette),
- toggle smooth/flat shading and LOD colorization from the on-screen overlay, and tune the
  material blend sharpness live.

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
- **materialPalette / materialBlendSharpness** — the terrain's material set and how sharply
  neighbouring voxel materials cut into each other (see *Voxel materials* below). The
  sharpness is a live shader global — drag it during Play, nothing rebuilds.
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
  brighten as you approach. **`edgeFadeCurve`** reshapes that falloff: the terrain bakes the
  curve into a small LUT (`_TransvoxelEdgeFadeCurve`) and the shader remaps the dither
  opacity through it per pixel — X = raw fade (0 at the draw distance, 1 at the viewer),
  Y = kept opacity. Lift the middle/left to keep near and mid LODs solid (less grain) while
  the far edge still dissolves. The default straight line is the plain linear ramp.

Fading needs shader support. The bundled **`Transvoxel/Lit Dithered`** shader (URP and
Built-in pipeline subshaders; the default runtime material uses it automatically outside
HDRP) implements it. To make your **own** material fade, add this to its shader — or
reproduce it with a Custom Function node in Shader Graph:

```hlsl
// Properties: [HideInInspector] _TransvoxelFadeAware("Fade Aware", Float) = 1
// (a marker only — declaring it is what tags the shader as fade-aware)
//
// IMPORTANT: every fade uniform below must live in GLOBAL scope — never inside the
// UnityPerMaterial cbuffer and never as a serialized property. The SRP Batcher sources
// per-material cbuffer values from the material and ignores Shader.SetGlobalFloat for
// them, which would lock the fade at the inspector value for batched draws.
float4 _TransvoxelViewerPos;   // set by TransvoxelTerrain every frame
float  _TransvoxelViewDistance;
float  _TransvoxelEdgeFadeBand;
float  _TransvoxelFade;        // global master fade (terrain sets 1 every frame)
TEXTURE2D(_TransvoxelEdgeFadeCurve); // edgeFadeCurve LUT: raw edge fade -> kept opacity
SAMPLER(sampler_TransvoxelEdgeFadeCurve); // (Built-in: sampler2D _TransvoxelEdgeFadeCurve;)

// VERTEX stage — the terrain bakes (fadeStartTime, ±fadeDuration) into UV2 (TEXCOORD1);
// the sign marks a cross-fade ghost, 0 = solid. Time base is Unity's built-in _Time.y.
// Pass the result to the fragment stage as a varying:
float TransvoxelVertexFade(float2 fadeData)
{
    float duration = abs(fadeData.y);
    if (duration <= 0) return 1;
    float t = saturate((_Time.y - fadeData.x) / duration);
    return fadeData.y < 0 ? -(1 - t) : t; // negative = cross-fade ghost
}

// FRAGMENT stage (positionCS = SV_POSITION, positionWS = world position):
float fade = vertexFade * _TransvoxelFade;
float edge = 1.0;
if (_TransvoxelEdgeFadeBand > 0)
{
    float rawEdge = saturate((_TransvoxelViewDistance
                              - distance(positionWS, _TransvoxelViewerPos.xyz))
                             / _TransvoxelEdgeFadeBand);
    // Reshape through the edgeFadeCurve LUT (Built-in: tex2D). A missing texture would
    // read 0 — if you skip the curve, use `edge = rawEdge;` instead.
    edge = SAMPLE_TEXTURE2D_LOD(_TransvoxelEdgeFadeCurve,
                                sampler_TransvoxelEdgeFadeCurve, float2(rawEdge, 0.5), 0).r;
}
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

## Voxel materials

Every voxel can be made of a different material. Create a palette via **Assets ▸ Create ▸
Transvoxel ▸ Material Palette**, add layers (albedo texture, tint, smoothness, per-layer UV
scale — each with a rotatable preview sphere in the Inspector), assign it to
`TransvoxelSettings.materialPalette`, and build with it:

```csharp
terrain.Terraform(worldPoint, radius: 5f, strength: 0.9f, build: true, materialId: 2);
```

- **The list index is the material id.** Layer 0 fills the whole world by default; build
  strokes stamp the selected id onto every solid voxel they touch (so the placed blob reads
  as one substance), digging never touches ids. Custom painting code can write
  `terrain.Materials` directly and call `InvalidateRegion` to re-mesh.
- **Storage is sparse and tiny.** Ids live in 16³ byte bricks
  (`VoxelMaterialLayer`, 4 KB per painted brick) next to the 16 KB density bricks a stroke
  creates anyway — an unpainted world costs zero bytes, and the GPU backend folds both
  layers into the one resident brick pool (ids packed 4-per-uint).
- **One material, any number of textures.** Layers are texture/parameter sets — not
  `Material` assets, whose arbitrary shaders could not run on one blended pixel. All layer
  albedos bake into a single `Texture2DArray` indexed per pixel (a plain GPU copy when the
  layers share size/format/mips; mixed inputs are resized and stored uncompressed), so the
  whole landscape still renders with **one material and full SRP batching**, whatever the
  palette size.
- **Transitions blend per pixel — and the width is live-tunable.** Each vertex takes the id
  of the solid voxel it hugs; each triangle carries its (up to three) ids plus one-hot corner
  weights in the mesh color channel (`MaterialBlendEncoder`, 4 bytes per vertex — vertices
  split only along material boundaries). The shader sharpens the rasterized barycentric
  weights with `pow(w, materialBlendSharpness)`: 1 blends across the whole boundary cell,
  16 is a near-hard cut. Material ids resolve identically across chunk borders, LOD seams
  and both meshers, so blends never tear at a seam — proven by the watertightness +
  seam-consistency tests.
- **Everything else composes.** Painting re-meshes through the normal edit-group path, so a
  material change cross-fades through the stipple ghosts like any terraform; CPU, GPU and
  Hybrid backends produce identical ids (the `TRANSVOXEL_MATERIALS` kernel variant adds one
  float per soup vertex, welded and encoded exactly like the CPU path).

Like fading, this needs shader support (`_TransvoxelPaletteAware` marker; palette inputs are
global uniforms + the `TRANSVOXEL_PALETTE` keyword). The bundled `Transvoxel/Lit Dithered`
shader implements it in both pipelines; with a palette assigned but a non-palette-aware
material, voxel materials are cleanly disabled with a console warning. Palette *content*
edits (textures, tints, sharpness) re-bind live without rebuilding chunks; assigning or
swapping the palette asset re-meshes the world once so every vertex carries blend data.

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

## Resource stats (debug)

The terrain measures its own footprint — just this package's work, excluding rendering,
materials/textures and PhysX collider cooking — so you can see, right now, what all the live
chunks and their computation cost:

```csharp
TransvoxelResourceStats s = terrain.CollectStats();
Debug.Log($"CPU main {s.MainThreadMsPerFrame:0.00} ms/frame, workers {s.WorkerCpuMsPerSecond:0.0} ms/s, "
        + $"GPU {s.GpuComputeMsPerSecond:0.0} ms/s, RAM {s.RamTotalBytes >> 20} MB, VRAM {s.GpuTotalBytes >> 20} MB");
```

- **CPU ms** — main-thread cost of the terrain's `Update` (smoothed + a peak), and worker
  ms/second across all background threads (sampling, meshing, GPU-soup welding, blend
  encoding, octree selection, collider bakes), plus builds/second and average build ms.
- **GPU ms** — compute-kernel ms/second, *sampled*: a tiny readback brackets each job's
  dispatches on the GPU timeline. Any single sample is quantized to a frame boundary, but the
  average converges on the true cost over many jobs (Unity exposes no per-dispatch GPU timer
  at runtime). 0 on the CPU backend.
- **RAM** — computed exactly from the structures the package owns: density-cache grids, edit
  and material bricks, pooled + in-flight meshing buffers, and the CPU copies of the chunk
  meshes.
- **GPU memory** — the chunk meshes' vertex/index buffers plus every compute buffer (per-job
  volume/append sets, the resident brick pool, the lookup tables).

Open **Window ▸ Transvoxel ▸ Terrain Stats** for a live UI Toolkit readout of all of it
(auto-picks the scene terrain; most useful in Play mode). The bundled demo overlay also shows
a compact CPU/GPU/RAM/VRAM summary, so the numbers appear in standalone builds too.

## Tests

EditMode tests live in `Editor/` (Window ▸ General ▸ Test Runner ▸ EditMode). They prove the
core invariant — the union of all chunk meshes is a closed, consistently wound 2-manifold —
for single chunks, same-LOD borders, every LOD-transition face, and after a transition-mask
change (the stale-cache regression); plus, for voxel materials: watertightness of encoded
(split) meshes, blend-attribute structure, and material-id agreement at every shared vertex
across chunk borders, LOD seams and both meshers; and the resource-stat memory estimators
against known structure sizes. Requires the `com.unity.test-framework` package.

## Requirements

- Unity **6000.0+** (developed and verified on **6000.5**). Uses `EntityId` (the Unity 6.2+
  replacement for instance IDs) in the collider-baking path.
- No external packages. A default lit material is created at runtime if none is assigned; a
  triplanar shader is recommended for cliffs and multi-texture terrain (Concept.txt #7).

## Reference

Lengyel, Eric. "Voxel-Based Terrain for Real-Time Virtual Simulations." PhD diss., University
of California at Davis, 2010. Data tables © 2009 Eric Lengyel, from <https://transvoxel.org/>.