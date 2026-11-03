### Shaders

The bundled **Transvoxel/Lit Dithered** shader supports everything: fading, voxel materials, triplanar and parallax. This page is for making your own URP shaders and Shader Graphs do the same. Both features live in reusable HLSL modules in `Runtime/Resources/`:

1. [`TransvoxelDither.hlsl`](../UnityPackage/Runtime/Resources/TransvoxelDither.hlsl) for dithered fading.
2. [`TransvoxelPalette.hlsl`](../UnityPackage/Runtime/Resources/TransvoxelPalette.hlsl) for voxel materials, triplanar and parallax.

Both modules are URP-only.

### Rules for every shader

1. **Inputs are global uniforms driven by the terrain.** Never redeclare them as material properties, `Properties` block entries or Blackboard properties: the SRP Batcher would lock a per-material copy at its inspector value.
2. **A marker property opts a material in.** The terrain checks its material at startup for `_TransvoxelFadeAware` and `_TransvoxelPaletteAware`. Without a marker, that feature is cleanly turned off with a console warning, and the material keeps rendering.
3. **Fade data travels in UV1.** The terrain bakes `(fadeStartTime, ±fadeDuration)` into `TEXCOORD1`, which is what `Mesh.uv2` stores; a negative duration marks a cross-fade ghost. Meshes without that channel read `(0, 0)` and render solid, so a fade-aware material is safe on any mesh.
4. **Fade-aware materials are visible before any terrain runs.** `TransvoxelShaderGlobals` sets the master fade to its neutral value at editor and player startup.

### Fading in Shader Graph

One Custom Function node makes any graph fade-aware. Build it once, save it as a Sub Graph and reuse it everywhere.

1. Add a **Custom Function** node: Type **File**, Source **`TransvoxelDither.hlsl`**, Name **`TransvoxelDitherFade`**.
2. Give it inputs `FadeData` (Vector2), `PositionWS` (Vector3) and `ScreenPos` (Vector4), and one output `Alpha` (Float). Wire a **UV** node on **UV1** to `FadeData`, a **Position** node in **World** space to `PositionWS`, and a **Screen Position** node in **Raw** mode to `ScreenPos`.
3. Wire `Alpha` to the Master Stack's **Alpha**, enable **Alpha Clipping** in Graph Settings and leave the threshold at **0.5**. The node outputs a binary 0 or 1, because the ghost logic decides the cutout inside the function.
4. On the Blackboard, add a **Float** property with reference name **`_TransvoxelFadeAware`**, default **1**, exposed.

Alpha clipping makes Shader Graph apply the same cutout to the shadow and depth passes, so shadows dissolve with the surface.

### Fading in hand-written shaders

```hlsl
Properties
{
    // Marker only: declaring it tags the material as fade-aware.
    [HideInInspector] _TransvoxelFadeAware("Fade Aware", Float) = 1
}

// Include after URP's Core.hlsl.
#include "Packages/com.reromanlee.transvoxel/Runtime/Resources/TransvoxelDither.hlsl"

// Vertex: read the fade channel and pass it down.
//   Attributes: float2 fadeData : TEXCOORD1;
output.fade = TransvoxelVertexFade(input.fadeData);

// Fragment: first statement, before any shading work.
TransvoxelDitherClip(input.positionCS, input.positionWS, input.fade);
```

Add both calls to **every pass that draws the mesh**, including ShadowCaster and DepthOnly, or shadows and depth keep the un-faded surface; `TransvoxelLitDithered.shader` shows the pattern. A fragment stage without an `SV_POSITION` input can call `TransvoxelDitherClipScreenPos(screenPos, positionWS, fade)` instead, which takes a `ComputeScreenPos`-style raw screen position.

### Voxel materials in Shader Graph

A graph needs the marker, the corner weights interpolated across each triangle, and one sampling node. It composes freely with the fading node above, and most graphs want both.

1. **Marker.** On the Blackboard, add a **Float** property with reference name **`_TransvoxelPaletteAware`**, default **1**, exposed.
2. **Vertex stage.** The one-hot corner weights must be computed per vertex so they rasterize into barycentric weights. Add a **Custom Interpolator** block (Vector2) to the Vertex context and feed it a **Custom Function** node: Type **File**, Source **`TransvoxelPalette.hlsl`**, Name **`TransvoxelBlendCorner`**, input `VertexColor` (Vector4) from a **Vertex Color** node, output `CornerWeights` (Vector2).
3. **Fragment stage.** Add a Custom Function node from the same file, picked to match your palette:

| Function | Inputs | Outputs |
| --- | --- | --- |
| `TransvoxelPaletteAlbedo` for albedo and tint only | `UV` (UV node on **UV0**), `VertexColor` (Vertex Color node), `CornerWeights` (Custom Interpolator node) | `Albedo` to Base Color, `Smoothness` to Smoothness |
| `TransvoxelPaletteMaps` adds normal, occlusion and height maps | The above, plus `PositionWS` (Position node, World) and `NormalWS` (Normal Vector node, World) | The above, plus `Occlusion` to Ambient Occlusion and `Normal` to Normal |
| `TransvoxelPaletteProjected` adds triplanar and parallax | Same as `TransvoxelPaletteMaps` | Same as `TransvoxelPaletteMaps` |

For the last two, set **Graph Settings ▸ Fragment Normal Space** to **World**: the module outputs a world-space normal, since terrain meshes have no tangents. A graph has no automatic variant switching, so `TransvoxelPaletteMaps` always pays for the full 12-sample path. `TransvoxelPaletteProjected` reads the palette's triplanar and parallax switches as globals and branches on them at runtime, so one node follows whatever the palette has enabled.

### Voxel materials in hand-written shaders

Declare `_TransvoxelPaletteAware` the same way as the fade marker and include `TransvoxelPalette.hlsl` after URP's Core.hlsl. Whenever a palette is active, the terrain binds every map array, filling kinds the palette doesn't use with tiny neutral textures, so a custom shader can sample them unconditionally.

Only the bundled shader uses the `TRANSVOXEL_PALETTE` and `TRANSVOXEL_PALETTE_MAPS` keywords to keep map-free palettes on the cheaper path. A custom shader that wants the same should list both in one `multi_compile` set. `TransvoxelLitDithered.shader` is the reference for the full vertex and fragment wiring.
