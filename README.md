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
| **Meshing** — *how* do we triangulate a chunk? | `Runtime/Meshing/` | Turns a sampled chunk into vertices/normals/UVs using the Transvoxel tables. Regular cells + transition cells. | `TransvoxelMesher`, `TransvoxelTransitionMesher` |
| **Octree** — *what* should exist right now? | `Runtime/Octree/` | Pure function of viewer position → the set of chunks and which faces need transition cells. | `TerrainOctree` → `ChunkDrawCommand` |
| **Orchestrator** — wire it together | `Runtime/TransvoxelTerrain.cs` | Diffs the octree's wishes against the live scene, runs sampling + meshing on worker `Task`s, uploads finished meshes under a per-frame budget. | `TransvoxelTerrain` |

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

- **maxLodLevels** — LOD levels above LOD0 (e.g. `4` → LOD0..LOD4).
- **viewDistance** — meters beyond which nothing is generated.
- **lodSplitFactor** — higher = more detail further away (more chunks).
- **smoothShading** — smooth shared-vertex normals vs. flat low-poly triangles.
- **colliderMaxLod** — which LODs get a `MeshCollider` (baked off the main thread).
- **lodSwapLinger** — how long a replaced chunk lingers after its replacement is ready, so
  LOD swaps never flash a crack while a neighbour finishes rebuilding. Raise it if you see a
  brief hole when moving fast; lower it toward 0 for minimal overdraw.

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

- Chunks are sampled and meshed on background `Task`s; only the final mesh upload touches the
  main thread, under `meshApplyBudgetPerFrame`, so movement stays smooth (Concept.txt: async).
- Coarse LODs sample the density field **only at the lattice points they actually use**, so
  looking far into the distance costs far less than full-resolution detail (Concept.txt #5).
- Old chunks stay on screen until their replacements are ready *and* the rest of the newly
  selected set has settled (plus a small `lodSwapLinger`), so LOD swaps never flash holes or
  cracks even while a neighbour is still rebuilding its transition faces.

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