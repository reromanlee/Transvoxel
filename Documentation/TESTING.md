### Testing

The package ships its tests alongside its code. They check the invariants that are easy to break silently and hard to spot by eye.

### What is covered

**EditMode tests** live in `UnityPackage/Editor/TransvoxelTests.cs`. Their central instrument is watertightness: for a closed density field, the union of all chunk meshes must form a closed, consistently wound 2-manifold, with every directed edge appearing exactly once alongside its reverse. That single property catches wrong tables, broken vertex reuse, mismatched interpolation across chunks, wrong transition-cell layout, bad winding and incorrect secondary-position shifts.

1. **Seams.** Watertightness of single chunks, same-LOD borders, every LOD transition face, and chunks after a transition-mask change.
2. **Voxel materials.** Watertightness of meshes split along material boundaries, the structure of the blend attributes, and material ids agreeing at every shared vertex across chunk borders, LOD seams and both meshers.
3. **Shaders.** The bundled shader compiles and still exposes its LOD-tint and marker properties, and chunk meshes always carry the fade vertex channel.
4. **Robustness.** Palette texture-array baking survives formats a `Texture2DArray` can't sample, and cached transition-face samples are reused rather than re-sampled.
5. **Resource stats.** The memory estimators match known structure sizes.

**PlayMode tests** live in `UnityPackage/Tests/Runtime/` and assert on rendered pixels, because the most expensive bugs in this package were invisible in code and obvious on screen:

1. LOD colorization changes the image while a palette is assigned.
2. `chunkFadeInSeconds = 0` renders the same solid surface as fading enabled.
3. Triplanar removes vertical streaking on a vertical face, measured as the collapse of vertical variation.
4. Parallax occlusion visibly changes the surface.

Each was confirmed to fail against the defect it describes. They skip themselves when there's no graphics device.

**The Interactive Demo sample** carries its own tests for the panel's mouse-only input, which run once the sample is imported.

### Running the tests locally

Add the package to `testables` in your project's `Packages/manifest.json`:

```json
{
  "testables": [
    "com.reromanlee.transvoxel"
  ]
}
```

Then open **Window ▸ General ▸ Test Runner** and run the **EditMode** and **PlayMode** tests. The tests need the `com.unity.test-framework` package, which new Unity projects include.
