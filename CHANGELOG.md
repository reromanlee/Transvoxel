# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-09-17

### Breaking

- **URP is now a package dependency** and the bundled `Transvoxel/Lit Dithered` shader ships
  a single URP SubShader. The Built-in pipeline surface shader that duplicated the palette
  blend inline has been removed, along with the non-SRP branch of the dither module. It
  existed to cover Built-in but meant maintaining two implementations of the same math that
  could drift apart, and the triplanar/parallax work would have doubled it again.
  The mesher itself is pipeline-agnostic and still runs anywhere: on a non-URP project the
  terrain builds, LODs, collides and terraforms as before, but falls back to that pipeline's
  default lit material and warns once that fades and voxel materials need URP.
- **`Runtime/Demo` has been removed.** The demo now ships as a Package Manager sample
  (*Window ▸ Package Manager ▸ Transvoxel ▸ Samples ▸ Import*). Scripts that referenced
  `TransvoxelDemo` or `FlyCamera` should use the sample's `TransvoxelDemoController` and
  `TransvoxelFlyCamera`, or their own controls.
- `TerrainChunk.Apply` no longer takes a `colorizeLod` argument, and
  `TerrainChunk.SetLodTintVisible` is replaced by `SetMaterial`. Only relevant to code
  driving chunk views directly.

### Added

- **Triplanar projection** (palette asset, off by default). Samples every layer on all three
  world planes and blends by the surface normal, removing the stretched, smeared texturing
  that the world-XZ planar UV map produces on cliffs, overhangs and cave walls. Normals use
  a whiteout blend in world space, which suits meshes that carry no tangents. (#25)
- **Parallax occlusion mapping** (palette asset, off by default). Marches the view ray
  through the blended heightfield per pixel so surfaces read as real depth with
  self-occlusion, without adding geometry. Per-layer height amplitude, view-angle-adaptive
  step count, and a distance fade so far chunks pay nothing. Only switches on when a layer
  actually carries a height map. (#25)
- `TransvoxelPaletteProjected` Shader Graph entry point covering both of the above, plus
  `_TransvoxelTriplanarEnabled` / `_TransvoxelParallaxEnabled` globals so graphs — which
  have no keywords — can branch on the palette's switches at runtime.
- **Interactive Demo sample**: an authored scene with camera, sun, terrain and a four-material
  palette carrying generated albedo and height maps, driven by the Input System (mouse,
  keyboard and gamepad) and a UI Toolkit overlay built from UXML + USS. The panel exposes
  nearly every setting live, including the ones that rebuild the world. (#5, #6, #19)
- `TransvoxelMaterialPalette.HasHeightMaps` and `ParallaxActive`.
- Tests covering shader compilation, the LOD-tint and marker shader properties, the fade
  vertex channel, palette array baking against unsupported formats, and face-sheet reuse.
- **PlayMode render tests** in `Tests/Runtime/`, asserting on rendered pixels: LOD colorization
  changes the image with a palette assigned, fading disabled renders the same solid surface as
  fading enabled, triplanar removes vertical streaking on a vertical face, and parallax visibly
  changes the surface. Each one was confirmed to fail against the defect it describes. Add
  `"testables": [ "com.reromanlee.transvoxel" ]` to a project's manifest to run them.
- Tests in the Interactive Demo sample covering the panel's mouse-only input.

### Fixed

- **LOD colorization did nothing whenever a material palette was assigned.** The tint was
  pushed through a `MaterialPropertyBlock` as `_BaseColor`, but the palette shader variants
  build albedo entirely from the palette and never read it, so the value was written and
  silently discarded. The tint now travels as `_TransvoxelLodTint`, multiplied into the final
  albedo of every variant and delivered through per-LOD shared materials. Tinted chunks
  consequently stay inside the SRP Batcher — a property block excluded them — and the tint
  survives batched render paths such as URP's GPU Resident Drawer.
- **`chunkFadeInSeconds = 0` rendered the terrain full of holes.** The UV2 fade channel was
  only written when the duration was positive, but the shader declares that vertex input in
  every variant and pass, so meshes built with fading off left the attribute unbound and the
  fragment shader dither-clipped against undefined data. The channel is now always written;
  a duration of 0 reads as solid.
- **A palette texture in a format that cannot be sampled from a `Texture2DArray` crashed the
  terrain on enable** with a `NullReferenceException`. RGB24 — any PNG without an alpha
  channel, imported uncompressed — is the common case. The bake now checks format support
  and falls back to its resize path.
- Toggling `colorizeLods` in the Inspector during Play tore down and re-meshed the entire
  world; it and `chunkFadeInSeconds` are now refreshed in place, like the edge-fade curve.
- Disabling one terrain stripped voxel materials from every other terrain still rendering.
  The global palette keywords are now refcounted across instances, and a second terrain with
  a *different* palette warns that the palette bindings are global.
- Transition-face sample sheets were re-sampled on every rebuild instead of being published
  back into the cached grid — roughly double the sampling work for a chunk with several
  transition faces.
- The sphere brush stored an edit for every voxel it touched, including those at the clamp
  (digging air already empty, building ground already solid) and at the rim where influence
  fades out, pinning them into the sparse edit layer forever for an invisible change.
- **The sample's overlay drove itself while the player flew the camera.** A runtime UI
  Toolkit panel has built-in navigation, and the Input System's default UI map binds
  WASD/arrows to Navigate and Space/Enter to Submit — so focus walked through the panel
  (the ScrollView scrolling to follow it, making the panel appear to move on its own),
  sliders under focus changed value, and toggles flipped. The panel now swallows navigation
  events at its root AND makes nothing in it focusable, so navigation has nowhere to land:
  no values change, no Foldout sections open and close, and no focus ring flickers across
  the panel as you fly. It is mouse-only; the sliders' numeric fields are read-outs.

### Changed

- The material palette inspector is now UI Toolkit, the last IMGUI in the editor assembly.
- Height maps are documented as what they are: they steer material boundaries *and* feed the
  parallax ray march.
- Corrected two comments that misdescribed the code: `TerrainOctree`'s scratch state is not
  main-thread-only (selection runs on a worker task), and `BuildQueue`'s LOD priority bias is
  one voxel per level rather than negligible.

## [1.2.1] and earlier

Released before this changelog was kept; see the commit history.
