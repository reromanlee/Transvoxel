### Usage

Everything beyond the README quick start. The importable **Interactive Demo** sample shows these features running, with every setting on a live panel.

### Interactive Demo

1. Open **Window ▸ Package Manager ▸ Transvoxel ▸ Samples** and import the **Interactive Demo**.
2. Open `TransvoxelDemo.unity` from the imported folder and press **Play**.

The scene comes wired with a camera, sun, terrain, a four-material palette with albedo and height maps, and the settings overlay.

1. **Fly** by holding the right mouse button to look and moving with **WASD** and **Q / E**, or a gamepad's sticks. **Shift** flies faster.
2. **Dig** with the left mouse button, **build** with **Shift + left mouse button**, and pick the material with **1–4** or the gamepad's shoulder buttons.
3. **Change any setting live** from the panel: brush, materials and blend, view distance, LOD levels and split factor, voxel size, shading, LOD tint, fades, triplanar, parallax, meshing backend and collider LOD.

The sample needs the **Input System** package with **Project Settings ▸ Player ▸ Active Input Handling** set to *Input System Package* or *Both*. The package itself doesn't.

**Importing copies the sample.** The Package Manager copies it into `Assets/Samples/Transvoxel/<version>/`, and later package updates never touch that copy. After updating the package, close the demo scene and press **Import** again to pick up changes; it asks before overwriting, and your own edits inside `Assets/Samples/` are replaced.

### Adding a terrain

1. **Add Component ▸ Transvoxel ▸ Transvoxel Terrain** on any GameObject.
2. Assign a **Viewer** transform for the levels of detail to center on, or leave it empty to use the main camera.
3. Assign a **Settings** asset from **Assets ▸ Create ▸ Transvoxel ▸ Terrain Settings**, or leave it empty to run on defaults.

Leave the settings' **Material** empty to use the bundled **Transvoxel/Lit Dithered** shader, which supports fading, voxel materials, triplanar and parallax. A custom material keeps working but only gets those features if its shader opts in; see [SHADERS.md](SHADERS.md).

### Settings

Every field on a **Terrain Settings** asset applies while the game runs: change it in the Inspector during Play and the terrain rebuilds what it needs the next frame, keeping player edits. Shading-only fields, like blend sharpness, fade curves and the LOD tint, apply without rebuilding anything. After changing a field from code, call `settings.NotifyChanged()`.

| Setting | What it does |
| --- | --- |
| **Voxel Size** | World size of one LOD0 voxel in meters. 1 gives bit-exact chunk borders. |
| **Chunk Cells** | Cells per chunk axis. 16 matches the paper. |
| **Max Lod Levels** | Levels of detail above LOD0: 4 means LOD0 to LOD4, each covering twice the size at half the resolution. |
| **View Distance** | No chunks are generated beyond this distance from the viewer, in meters. |
| **Lod Split Factor** | A chunk splits into finer ones while the viewer is closer than this factor times its size. Higher gives more detail further away and more chunks. |
| **Iso Level** | Density value that counts as the terrain surface. |
| **Smooth Shading** | Smooth shared-vertex normals, or flat low-poly triangles. |
| **Material** | The terrain material. Empty uses the bundled Lit Dithered shader. |
| **Uv Scale** | Texture repeats per meter in the world-space UVs. |
| **Colorize Lods** | Tints chunks by level of detail to visualize the octree. |
| **Chunk Fade In Seconds** | Duration of every dither cross-fade. 0 switches chunks instantly. |
| **Edge Fade Fraction** | Fraction of the view distance over which terrain dissolves toward its edge. |
| **Edge Fade Curve** | Reshapes that dissolve; see [FEATURES.md](FEATURES.md#dithered-fading). |
| **Material Palette** | The voxel material set. Empty renders a single material. |
| **Material Blend Sharpness** | How sharply neighbouring voxel materials cut into each other: 1 blends broadly, 16 is a near-hard cut. |
| **Noise** | The procedural landscape: seed, ground level, height amplitude, frequency, octaves, lacunarity, persistence, surface blend and cave carving. |
| **Meshing Backend** | `CpuThreads`, `GpuCompute` or `Hybrid`; see [FEATURES.md](FEATURES.md#meshing-backends). |
| **Gpu Compute Override** | Replaces the bundled compute shader. |
| **Gpu Jobs In Flight** | Chunk builds allowed on the GPU at once: throughput against VRAM. |
| **Mesh Apply Budget Ms** | Main-thread time per frame for uploading finished meshes. |
| **Mesh Apply Budget Per Frame** | Hard cap on mesh uploads per frame, on top of the time budget. |
| **Max Concurrent Builds** | Parallel CPU builds. 0 uses the processor count minus 2. |
| **Collider Max Lod** | Chunks up to this level of detail get a `MeshCollider`, baked off the main thread. −1 disables colliders. |
| **Viewer Move Threshold** | The chunk set is recomputed after the viewer moves this fraction of a LOD0 chunk. |
| **Lod Swap Linger** | How long a replaced chunk may stay after its replacement is ready. Raise it if you see brief seams while moving fast; 0 minimizes latency and overdraw. |
| **Density Cache Chunks** | Density grids kept in memory for fast re-meshing after edits and LOD changes. |

### Terraforming

```csharp
using reromanlee.Transvoxel;

[SerializeField] private TransvoxelTerrain terrain;

void Dig(Ray ray)
{
    if (terrain.RaycastDensity(ray, 400f, out Vector3 hit))
        terrain.Terraform(hit, radiusMeters: 5f, strength: 0.9f, build: false);
}

void Build(Vector3 point, byte materialId)
    => terrain.Terraform(point, radiusMeters: 5f, strength: 0.9f, build: true, materialId);
```

1. **`RaycastDensity`** marches a ray through the density field and returns the first surface hit, with no colliders needed.
2. **`Terraform`** applies a soft sphere brush to the player-edit layer. Every affected chunk re-meshes in the background at top priority, and the whole set swaps in the same frame, so a stroke never flashes a hole along a chunk border.
3. **Build strokes** stamp `materialId` onto every solid voxel they touch. Digging never changes material ids.

### Voxel materials

1. Create a palette from **Assets ▸ Create ▸ Transvoxel ▸ Material Palette**.
2. Add layers. Each takes an albedo texture and tint, smoothness, and optional normal, occlusion and height maps with their own strengths, plus a UV scale. Double-click the asset, or open **Window ▸ Transvoxel ▸ Material Palette**, for a reorderable layer list with a rotatable preview sphere.
3. Assign it to the settings' **Material Palette**.

The layer index is the material id, and layer 0 fills the world by default. The palette also holds **Height Blend**, which scales how strongly height maps steer boundaries, and the **Triplanar** and **Parallax Occlusion** switches with their tuning; all of them apply live. How each works is in [FEATURES.md](FEATURES.md#voxel-materials).

**Paint from your own code** by writing ids on the voxel lattice directly, then re-meshing the region:

```csharp
using reromanlee.Transvoxel.Density;

terrain.Materials.WriteBatch(set =>
{
    for (int x = min.x; x < max.x; x++)
    for (int y = min.y; y < max.y; y++)
    for (int z = min.z; z < max.z; z++)
        set(x, y, z, 3);
});

terrain.InvalidateRegion(new BoundsInt(min, max - min));
```

Coordinates are LOD0 voxel indices, so a world position divides by **Voxel Size** in the terrain's local space.

### Custom density

The terrain samples an `IDensitySource`: a field that returns 0 to 1 for each integer voxel position, where values above the iso level are ground. Implementations must be thread-safe, since chunks are sampled on worker threads.

```csharp
using reromanlee.Transvoxel.Density;

sealed class FloatingIsland : IDensitySource
{
    public float SampleVoxel(int x, int y, int z)
    {
        float distance = new Vector3(x, y * 2f, z).magnitude;
        return Mathf.Clamp01(0.5f + (120f - distance) * 0.05f);
    }
}
```

Set it as `DensityOverride` before the terrain is enabled, for example with the component disabled in the scene:

```csharp
void Awake()
{
    terrain.DensityOverride = new LayeredDensitySource(new FloatingIsland(), new VoxelEditLayer());
    terrain.enabled = true;
}
```

**Wrap it in a `LayeredDensitySource` to keep terraforming.** It stacks a player-edit layer over your field; a bare `IDensitySource` works but can't be edited. A custom density source always meshes on the CPU, since arbitrary C# can't run on the GPU; the GPU and Hybrid backends fall back with a warning.

### Saving edits

There's no built-in save format, but both edit layers expose their sparse bricks. `terrain.Layers.Edits` holds the density edits and `terrain.Materials` the material ids; `CollectAllBricks` lists every brick for saving, and `WriteBatch` writes voxels back. Call `terrain.InvalidateRegion` for the restored region afterwards.

### Debugging

1. **Colorize Lods** tints every chunk by its level of detail, to see the octree at work.
2. **Window ▸ Transvoxel ▸ Terrain Stats** shows the terrain's live CPU, GPU, RAM and VRAM cost; see [PERFORMANCE.md](PERFORMANCE.md#measuring).
3. `ActiveBackend`, `LiveChunkCount`, `PendingBuildCount` and `TotalVertices` on the terrain report its state from code.
