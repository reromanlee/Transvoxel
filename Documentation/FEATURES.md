### Features

A detailed look at what Transvoxel does and how it works. For setup and code, see [USAGE.md](USAGE.md); for your own shaders, see [SHADERS.md](SHADERS.md).

### Architecture

The pipeline is four independent layers. Each one knows the layer below it only through a small interface, so any of them can be replaced on its own. Folders are relative to the package root, [`UnityPackage/`](../UnityPackage).

| Layer | Folder | Responsibility | Key types |
| --- | --- | --- | --- |
| **Density**: where is there ground? | `Runtime/Density/` | Answers `SampleVoxel(x, y, z) → 0..1`. Player edits are stacked over the procedural landscape, and a parallel sparse layer answers `SampleMaterial(x, y, z) → palette id`. | `IDensitySource`, `LayeredDensitySource`, `VoxelMaterialLayer` |
| **Meshing (CPU)**: how is a chunk triangulated? | `Runtime/Meshing/` | Turns a sampled chunk into vertices, normals and UVs with the Transvoxel tables, using regular and transition cells. | `TransvoxelMesher`, `TransvoxelTransitionMesher` |
| **Meshing (GPU)**: the same, in compute shaders | `Runtime/Gpu/`, `Runtime/Resources/TransvoxelCompute.compute` | Density and the whole triangulation run in three compute kernels, and triangles stream back through async readback. | `GpuChunkBuilder`, `TransvoxelGpuTables` |
| **Octree**: what should exist right now? | `Runtime/Octree/` | A pure function of the viewer position: the set of chunks, and which of their faces need transition cells. | `TerrainOctree`, `ChunkDrawCommand` |
| **Orchestrator**: wires it together | `Runtime/TransvoxelTerrain.cs` | Diffs the octree's selection against the live scene, feeds a distance-prioritized build queue, and uploads finished meshes under a per-frame time budget. | `TransvoxelTerrain` |

The lookup tables, translated to C# from Lengyel's original C++, live in `Runtime/TransvoxelDataTables.cs`.

### Seamless levels of detail

Adjacent chunks may differ by one level of detail, and the octree enforces this 2:1 balance. Where a coarse chunk meets finer neighbours, a naive mesh leaves cracks. The Transvoxel fix is owned entirely by the coarser chunk:

1. **Its full-resolution transition face** sits exactly on the boundary and reproduces the finer neighbour's triangulation bit for bit.
2. **Its half-resolution face vertices** are pushed inward (the paper's *secondary position* shift) to land exactly on the coarse chunk's own shrunk boundary.

Together the three surfaces (fine mesh, transition sheet, coarse mesh) close every seam without sharing any data between neighbours. A headless watertightness test proves it: every LOD boundary produces zero unmatched edges.

Density is sampled on an integer voxel lattice rather than at free world positions, so every chunk, transition cell and terraform stroke reads bit-identical values at shared positions. Coarser levels sample only every 2<sup>lod</sup>-th lattice point and never read anything in between.

### Meshing backends

Every backend produces the same landscape: the GPU noise uses the same permutation table as the CPU's `FractalNoise`, so the same seed gives the same terrain. All of them share the octree, the build queue, colliders and terraforming, and you can switch between them live without losing edits.

1. **CpuThreads.** Chunks are sampled and meshed on a pool of worker tasks, one per core. Runs everywhere.
2. **GpuCompute.** Three kernels in `TransvoxelCompute.compute` do the work: `CSVolume` builds the chunk's density grid (procedural noise overridden by player edits), `CSRegular` runs one thread per cell over Lengyel's tables, and `CSTransition` stitches the LOD seams with one thread per face cell. Triangles return through `AsyncGPUReadback` in two stages (count, then exactly that many triangles), so the CPU never waits on the GPU.
3. **Hybrid.** CPU workers and the GPU pipeline pull from the same nearest-first queue, and whichever is free builds the next chunk. This gives the highest throughput, which suits teleports and very large view distances.

**GPU chunks render exactly like CPU chunks.** GPU cells can't share the paper's serial vertex reuse, so they emit unindexed triangles, which a light worker task welds back into an indexed mesh. Coincident vertices are bit-identical, so welding is an exact hash with no epsilon, and the result has about 3× fewer vertices than the raw output.

**Player edits never round-trip.** Edits are stored in 16³ voxel bricks, kept in one resident GPU buffer pool: uploaded once, then only the bricks a terraform stroke touched are re-uploaded, and each chunk build sends just the few pool slots it overlaps. Editing half the map costs GPU builds nothing extra. The saveable copy stays on the CPU in `VoxelEditLayer`.

**GPU and Hybrid fall back to CpuThreads** with a console warning when the platform lacks compute shaders or async readback, or when a custom `DensityOverride` is active, since arbitrary C# can't run on the GPU.

### Dithered fading

Nothing about the landscape pops. Every visual change runs through one screen-space Bayer dither clip, the same technique as Unity's LOD Group cross-fading:

1. **Fade-in.** A freshly built chunk dithers from invisible to solid over `chunkFadeInSeconds`.
2. **Fade-out.** A retired chunk (a LOD swap, or out of range) dithers away over the same duration, and only after its replacements have fully faded in underneath it.
3. **Mesh-swap cross-fade.** When a live chunk re-meshes after terraforming or a transition-mask change, its old surface moves onto a short-lived *ghost* that dithers out with the complementary pattern while the new mesh dithers in. Every pixel is drawn by exactly one of the two, so there are no holes and no double brightness. Rapid re-edits keep at most one ghost per chunk.
4. **Draw-distance dissolve.** Terrain fades out toward `viewDistance` per pixel over the last `edgeFadeFraction` of it, so even a kilometer-wide coarse chunk dissolves smoothly like fog. Chunks leaving the range are fully transparent before they're removed, and new frontier chunks are born inside the faded band.

**`edgeFadeCurve` reshapes the dissolve.** The terrain bakes the curve into a small lookup texture, and the shader remaps the dither opacity through it per pixel: X is the raw fade (0 at the draw distance, 1 at the viewer) and Y is the opacity kept. Lift the middle of the curve to keep near and mid LODs solid while the far edge still dissolves. The default straight line is a plain linear ramp.

**Fades are batching-proof.** Each chunk mesh carries its fade start time and duration in the UV1 vertex channel, and the shader animates them from global time. There are no per-renderer properties, no property blocks and no per-chunk materials, so fading survives the SRP Batcher and URP's GPU Resident Drawer, and costs no CPU per frame.

Fading needs shader support, which the bundled **Transvoxel/Lit Dithered** shader provides. Custom materials without it still render, but switch chunks instantly; see [SHADERS.md](SHADERS.md) to add it.

### Voxel materials

Every voxel can be made of a different material. A **Material Palette** asset holds up to 64 layers, each with an albedo texture and tint, smoothness, normal, ambient-occlusion and height maps with their own strengths, and a UV scale.

1. **The layer index is the material id.** Layer 0 fills the whole world by default. Build strokes stamp the selected id onto every solid voxel they touch, so a placed blob reads as one substance; digging never changes ids.
2. **Storage is sparse and tiny.** Ids live in 16³ byte bricks (`VoxelMaterialLayer`, 4 KB per painted brick) next to the 16 KB density bricks a stroke creates anyway. An unpainted world costs zero bytes, and the GPU backend folds both into its one resident brick pool, packing four ids per `uint`.
3. **One material, any number of textures.** Each map kind (albedo, normal, occlusion, height) is baked into a single `Texture2DArray` sampled per pixel. Layers that share size, format and mip count are copied on the GPU; mixed inputs are resized and stored uncompressed. The whole landscape still renders with one material and full SRP batching, whatever the palette size.
4. **Transitions blend per pixel.** Each vertex takes the id of the solid voxel it hugs, and each triangle carries up to three ids plus one-hot corner weights in the vertex color channel (`MaterialBlendEncoder`, 4 bytes per vertex; vertices split only along material boundaries). The shader sharpens the interpolated weights with `pow(w, materialBlendSharpness)`: 1 blends across the whole boundary cell, 16 is a near-hard cut, and changing it is live with no rebuild.
5. **Blends never tear at a seam.** Material ids resolve identically across chunk borders, LOD seams and both meshers, which the watertightness and seam-consistency tests check.
6. **Detail maps are pay-for-what-you-use.** A palette without normal, occlusion or height maps renders on the cheaper albedo-only shader variant. Normal maps need no mesh tangents, since the shader rebuilds the tangent frame per pixel from screen-space derivatives. Occlusion only attenuates ambient and indirect light.
7. **Height maps steer the blend.** The higher material (rock, cobbles) cuts through the lower one (sand) at boundaries; the palette's **Height Blend** scales this from a plain crossfade to a hard cut. The same maps feed parallax occlusion.

**Everything else composes.** Painting re-meshes through the same path as terraforming, so a material change cross-fades like any edit, and the CPU, GPU and Hybrid backends produce identical ids. Editing a palette's textures, tints or sharpness re-binds live without rebuilding chunks; assigning a different palette asset re-meshes the world once.

**One palette per scene.** Palette bindings are global shader state, for the same reason fade inputs are: batched render paths bypass per-renderer state. Two terrains therefore share whichever palette was bound last, and the terrain warns once if it finds two with different palettes.

### Triplanar projection

Terrain UVs are a world-space XZ planar map. That suits rolling ground but not anything near vertical: on a cliff face the texture smears into vertical streaks, and that lands hardest on the shapes a voxel engine exists for, like cliffs, overhangs and cave walls.

Tick **Triplanar** on the palette and every layer is sampled on all three world planes and blended by the surface normal, so each orientation gets an undistorted mapping. Normals use a whiteout blend in world space, which suits meshes without tangents. **Triplanar Sharpness** narrows the band where two planes mix, from broad at 1 toward a hard switch at the diagonals.

It costs roughly 3× the texture fetches. UVs stay a pure function of world position and are never mirrored by the normal's sign, which keeps them continuous across chunk borders and LOD seams.

### Parallax occlusion

Height maps steer material boundaries, but on a single material that does nothing. **Parallax Occlusion** is the other thing a height map is for: the view ray is marched through the blended heightfield per pixel and the UV is moved to where the ray actually meets the surface. Brick, cobbles and rock strata read as volume with self-occlusion, without tessellating or displacing any geometry.

1. **Height Scale** (per layer) is the apparent depth in UV units, so rock can be deeper than sand. Keep it well under the size of one feature in the texture; a value wider than the gaps in the pattern steps over them and smears.
2. **Parallax Min / Max Steps** set the march length, chosen by viewing angle: head-on needs few steps, grazing rays need many.
3. **Parallax Distance** fades the effect out by that range, so distant and low-LOD chunks pay nothing for depth too small to see.

Parallax only switches on when some layer has a height map, and only the dominant triplanar plane is marched. It's most useful with triplanar on, since a displaced UV on a smeared cliff mapping only amplifies the smear. Both triplanar and parallax are off by default and keyword-gated, so a palette that uses neither costs exactly what it did without them.

### Reference

Lengyel, Eric. "Voxel-Based Terrain for Real-Time Virtual Simulations." PhD diss., University of California at Davis, 2010. Data tables © 2009 Eric Lengyel, from [transvoxel.org](https://transvoxel.org/). The paper and the original C++ tables are in the repository's [`Sources/`](../Sources) folder.
