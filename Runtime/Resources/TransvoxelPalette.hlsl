// Reusable Transvoxel material-palette module — the blend of the terrain's per-layer
// texture arrays behind Transvoxel/Lit Dithered, packaged so URP shaders and Shader
// Graphs can render voxel materials too. (URP only — this package depends on URP.)
//
// Shader Graph usage (full walkthrough in the README's Voxel materials section):
//
//   1. Blackboard: Float property, reference name _TransvoxelPaletteAware, default 1 —
//      the marker TransvoxelTerrain looks for (next to the _TransvoxelFadeAware one).
//   2. Vertex stage: the one-hot corner weights must rasterize ACROSS the triangle into
//      barycentric weights — they cannot be derived per fragment (the interpolated
//      corner index is meaningless). Add a Custom Interpolator block (Vector2) to the
//      Vertex context and feed it a Custom Function node: Type File, Source this file,
//      Name "TransvoxelBlendCorner", input VertexColor (Vector4, wire a Vertex Color
//      node), output CornerWeights (Vector2).
//   3. Fragment stage: a Custom Function node from this file — "TransvoxelPaletteAlbedo"
//      (Albedo + Smoothness) for albedo/tint palettes, or "TransvoxelPaletteMaps" to add
//      the normal/occlusion/height-blend maps. Inputs: UV (Vector2, a UV node on UV0),
//      VertexColor (Vector4, a Vertex Color node), CornerWeights (Vector2, a Custom
//      Interpolator node); TransvoxelPaletteMaps also takes PositionWS (Position node,
//      World) and NormalWS (Normal Vector node, World).
//   4. Wire Albedo -> Base Color, Smoothness -> Smoothness; with TransvoxelPaletteMaps
//      also Occlusion -> Ambient Occlusion and Normal -> Normal, after setting the
//      graph's Fragment Normal Space to World (the output is a world-space normal —
//      terrain meshes carry no tangents, so the frame is rebuilt from screen-space
//      derivatives instead of tangent-space math).
//
// Unlike the bundled shader there is no keyword switching here — a graph samples
// whichever function it wires in, unconditionally. TransvoxelTerrain binds every array
// whenever a palette is active (map kinds the palette does not use hold neutral 4x4
// fallbacks: flat normal, white occlusion, mid height), so both fragment functions are
// always safe to call; pick TransvoxelPaletteAlbedo when you do not need the maps — it
// is a quarter of the texture samples. All inputs are GLOBAL uniforms pushed by the
// terrain — never redeclare them as material/Blackboard properties (see
// TransvoxelDither.hlsl for why). These functions are meant for terrain meshes built
// with a palette assigned; other meshes lack the blend data in the color channel.

#ifndef TRANSVOXEL_PALETTE_INCLUDED
#define TRANSVOXEL_PALETTE_INCLUDED

#if !defined(UNITY_COMMON_INCLUDED)
#error TransvoxelPalette.hlsl needs an SRP include context (a URP shader or Shader Graph); this package is URP-only.
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

// Material palette globals, driven by the terrain: one texture array per map kind holds
// every layer's texture — the material id indexes it per pixel, so nothing here grows
// with the palette. The fixed 64 is the declared capacity of
// TransvoxelMaterialPalette.MaxLayers, not a per-slot cost.
TEXTURE2D_ARRAY(_TransvoxelAlbedoArray);
SAMPLER(sampler_TransvoxelAlbedoArray);
float4 _TransvoxelLayerColors[64];    // rgb = tint, a = smoothness
float4 _TransvoxelLayerScales[64];    // x = uv scale, y = normal str, z = occlusion str
float _TransvoxelBlendSharpness;      // live materialBlendSharpness setting
float _TransvoxelPaletteLayerCount;

// Detail-map arrays, sharing the albedo sampler. Height does not displace anything: it
// steers the blend weights so the higher material (rock, cobbles) cuts through the
// lower one (sand) at boundaries.
TEXTURE2D_ARRAY(_TransvoxelNormalArray);
TEXTURE2D_ARRAY(_TransvoxelOcclusionArray);
TEXTURE2D_ARRAY(_TransvoxelHeightArray);
float _TransvoxelHeightBlend;         // 0 = plain crossfade, 1 = full height cut

// Decodes the MaterialBlendEncoder vertex attribute: rgb = the triangle's sorted
// material id triple (identical on all three vertices, so plain interpolation is
// exact), a = which corner of the triple this vertex is. The one-hot corner
// weights rasterize into exact barycentric weights (the third is 1 - x - y).
void TransvoxelDecodeBlend(float4 vertexColor, out float3 ids, out float2 cornerWeights)
{
    ids = vertexColor.rgb * 255.0;
    int corner = (int)round(vertexColor.a * 255.0);
    cornerWeights = float2(corner == 0 ? 1.0 : 0.0, corner == 1 ? 1.0 : 0.0);
}

half4 TransvoxelSampleLayer(float2 uv, int id)
{
    float4 layerColor = _TransvoxelLayerColors[id];
    half3 albedo = SAMPLE_TEXTURE2D_ARRAY(_TransvoxelAlbedoArray, sampler_TransvoxelAlbedoArray,
                                          uv * _TransvoxelLayerScales[id].x, id).rgb;
    return half4(albedo * layerColor.rgb, layerColor.a); // a carries smoothness
}

// Shared weight setup for both palette paths: clamp the id triple and sharpen the
// barycentric corner weights by pow() — 1 blends across the whole boundary cell, higher
// values tighten the transition toward a hard cut (the materialBlendSharpness setting).
void TransvoxelPaletteSetup(float3 idsRaw, float2 w01, out int3 ids, out float3 w)
{
    int maxLayer = max((int)_TransvoxelPaletteLayerCount - 1, 0);
    ids = clamp((int3)round(idsRaw), 0, maxLayer);

    w = float3(w01, saturate(1.0 - w01.x - w01.y));
    w = pow(max(w, 0.0), _TransvoxelBlendSharpness);
    w /= max(w.x + w.y + w.z, 1e-5);
}

// Blends the (up to) three palette layers of the triangle (albedo-only path).
// All three layers are sampled unconditionally so texture gradients stay uniform
// (uniform triangles fetch the same texel thrice — cache-free).
half4 TransvoxelPaletteBlend(float2 uv, float3 idsRaw, float2 w01)
{
    int3 ids;
    float3 w;
    TransvoxelPaletteSetup(idsRaw, w01, ids, w);

    return w.x * TransvoxelSampleLayer(uv, ids.x)
         + w.y * TransvoxelSampleLayer(uv, ids.y)
         + w.z * TransvoxelSampleLayer(uv, ids.z);
}

// ------------------------------------------------------------------ palette detail maps

struct TransvoxelSurface
{
    half3 albedo;
    half3 normalTS;
    half occlusion;
    half smoothness;
};

float TransvoxelLayerHeight(float2 uv, int id)
{
    return SAMPLE_TEXTURE2D_ARRAY(_TransvoxelHeightArray, sampler_TransvoxelAlbedoArray,
                                  uv * _TransvoxelLayerScales[id].x, id).r;
}

void TransvoxelAccumulateLayer(inout TransvoxelSurface s, float2 uv, int id, float weight)
{
    float4 layerColor = _TransvoxelLayerColors[id];
    float4 scales = _TransvoxelLayerScales[id];
    float2 layerUv = uv * scales.x;

    half3 albedo = SAMPLE_TEXTURE2D_ARRAY(_TransvoxelAlbedoArray,
        sampler_TransvoxelAlbedoArray, layerUv, id).rgb;
    half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D_ARRAY(_TransvoxelNormalArray,
        sampler_TransvoxelAlbedoArray, layerUv, id), scales.y);
    half occlusion = SAMPLE_TEXTURE2D_ARRAY(_TransvoxelOcclusionArray,
        sampler_TransvoxelAlbedoArray, layerUv, id).r;

    s.albedo += weight * albedo * layerColor.rgb;
    s.normalTS += weight * normalTS;
    s.occlusion += weight * (1.0 + scales.z * (occlusion - 1.0)); // occlusion strength
    s.smoothness += weight * layerColor.a;
}

// Full surface blend of the triangle's palette layers: albedo, tangent-space normal,
// occlusion and smoothness. The pow-sharpened weights are additionally steered by the
// height maps: multiplying by exp2(k·height) is an exact identity for equal heights (so
// map-less layers keep the plain crossfade) and lets the higher material win the
// boundary in proportion to _TransvoxelHeightBlend.
TransvoxelSurface TransvoxelPaletteBlendFull(float2 uv, float3 idsRaw, float2 w01)
{
    int3 ids;
    float3 w;
    TransvoxelPaletteSetup(idsRaw, w01, ids, w);

    float3 heights = float3(TransvoxelLayerHeight(uv, ids.x),
                            TransvoxelLayerHeight(uv, ids.y),
                            TransvoxelLayerHeight(uv, ids.z));
    w *= exp2(heights * (_TransvoxelHeightBlend * 8.0));
    w /= max(w.x + w.y + w.z, 1e-5);

    TransvoxelSurface s = (TransvoxelSurface)0;
    TransvoxelAccumulateLayer(s, uv, ids.x, w.x);
    TransvoxelAccumulateLayer(s, uv, ids.y, w.y);
    TransvoxelAccumulateLayer(s, uv, ids.z, w.z);
    return s;
}

// Applies the blended tangent-space normal without precomputed tangents (the meshes
// carry none): the frame is rebuilt from screen-space derivatives of the world position
// and UV (Schüler's cotangent-frame construction), so it matches whatever the UV
// mapping does at any surface orientation. Degenerate UV areas fall back to the
// geometric normal. Fragment stage only (ddx/ddy).
float3 TransvoxelPerturbNormal(float3 normalWS, half3 normalTS, float3 positionWS, float2 uv)
{
    float3 dpx = ddx(positionWS);
    float3 dpy = ddy(positionWS);
    float2 duvx = ddx(uv);
    float2 duvy = ddy(uv);

    float3 dpyPerp = cross(dpy, normalWS);
    float3 dpxPerp = cross(normalWS, dpx);
    float3 tangent = dpyPerp * duvx.x + dpxPerp * duvy.x;
    float3 bitangent = dpyPerp * duvx.y + dpxPerp * duvy.y;
    float invMax = rsqrt(max(max(dot(tangent, tangent), dot(bitangent, bitangent)), 1e-12));

    return normalize(normalTS.x * (tangent * invMax)
                   + normalTS.y * (bitangent * invMax)
                   + normalTS.z * normalWS);
}

// ------------------------------------------------------- triplanar & parallax occlusion
//
// Two opt-in upgrades driven by the palette asset. Both are OFF unless the palette turns
// them on, and the bundled shader compiles them out entirely via keywords; a Shader Graph
// has no keywords, so it branches on the *Enabled globals instead (same inputs either way).
//
//   * TRIPLANAR fixes this package's oldest texturing limitation. UV0 is a world-space XZ
//     planar map, so anything approaching vertical — cliffs, overhangs, cave walls, the
//     very features a voxel engine exists for — shows smeared vertical streaks. Triplanar
//     samples each layer on all three world planes and blends by the surface normal, so
//     every orientation gets an undistorted mapping. Costs 3x the texture fetches.
//
//   * PARALLAX OCCLUSION MAPPING gives the height maps real depth. Per pixel the view ray
//     is marched through the blended heightfield and the UV is displaced to where the ray
//     actually meets the surface, so bricks, cobbles and rock strata read as volume rather
//     than as a flat picture of volume — with self-occlusion, and without adding a single
//     triangle to meshes the GPU is already busy generating.
//
// EVERY texture read here uses explicit gradients. The ray march is a dynamic loop, so
// neighbouring pixels can exit on different iterations; implicit derivatives taken inside
// it are meaningless and pick wildly wrong mip levels, which shows up as smearing and
// streaking exactly where the effect is strongest. Derivatives are therefore taken ONCE,
// up front, from the undisplaced surface, and threaded through every sample — including
// the final surface reads at the displaced UV, whose own derivatives are discontinuous.
//
// Relationship to the height blending above: both read the same height array and they
// compose. Height *steering* decides WHICH material wins a boundary; parallax decides
// where the surface of the winner appears. Neither displaces geometry — silhouettes and
// shadows still follow the mesh, which is the standard limitation of the technique.

float _TransvoxelUvScale;              // terrain uvScale: UV0 = terrain-local XZ metres * this
float _TransvoxelTriplanarEnabled;     // 0/1, for shaders without keywords (graphs)
float _TransvoxelTriplanarSharpness;   // higher = narrower blend band between planes
float _TransvoxelParallaxEnabled;      // 0/1, as above
float _TransvoxelParallaxMinSteps;     // steps used head-on
float _TransvoxelParallaxMaxSteps;     // steps used at grazing angles
float _TransvoxelParallaxDistance;     // metres; the effect fades out to nothing by here

// One plane's UV and its screen-space derivatives, captured before any displacement.
struct TransvoxelPlaneUV
{
    float2 uv;
    float2 dx;
    float2 dy;
};

// The three world-plane projections of a point, plus how much each one counts.
struct TransvoxelPlanes
{
    TransvoxelPlaneUV x;   // looking along world X: (z, y)
    TransvoxelPlaneUV y;   // looking along world Y: (x, z) — matches UV0 on flat ground
    TransvoxelPlaneUV z;   // looking along world Z: (x, y)
    float3 blend;          // normal-driven weights, sum 1
    int dominant;          // 0/1/2: the plane facing the surface most directly
};

// UVs are a pure function of world position (never mirrored by the normal's sign), which
// is what keeps them continuous across chunk borders and LOD seams. `uv` is UV0, used as
// the world-Y plane when triplanar is off so that path stays bit-identical to the mapping
// the mesher baked in. Both derivative sets are taken unconditionally: in a Shader Graph
// the triplanar switch is a runtime branch, and ddx/ddy inside divergent control flow is
// undefined.
TransvoxelPlanes TransvoxelBuildPlanes(float2 uv, float3 positionWS, float3 normalWS,
    bool triplanar)
{
    float3 p = positionWS * _TransvoxelUvScale;
    float3 dpx = ddx(p);
    float3 dpy = ddy(p);
    float2 uvDx = ddx(uv);
    float2 uvDy = ddy(uv);

    TransvoxelPlanes planes;
    planes.x.uv = p.zy;  planes.x.dx = dpx.zy;  planes.x.dy = dpy.zy;
    planes.y.uv = p.xz;  planes.y.dx = dpx.xz;  planes.y.dy = dpy.xz;
    planes.z.uv = p.xy;  planes.z.dx = dpx.xy;  planes.z.dy = dpy.xy;

    if (!triplanar)
    {
        planes.y.uv = uv;
        planes.y.dx = uvDx;
        planes.y.dy = uvDy;
        planes.blend = float3(0.0, 1.0, 0.0);
        planes.dominant = 1;
        return planes;
    }

    float3 a = abs(normalWS);
    float3 w = pow(max(a, 1e-4), max(_TransvoxelTriplanarSharpness, 1.0));
    planes.blend = w / max(w.x + w.y + w.z, 1e-5);
    planes.dominant = (a.x > a.y && a.x > a.z) ? 0 : (a.z > a.y ? 2 : 1);
    return planes;
}

TransvoxelPlaneUV TransvoxelGetPlane(TransvoxelPlanes planes, int plane)
{
    // if/else rather than ?: — HLSL will not select between struct values with a ternary.
    if (plane == 0)
        return planes.x;
    if (plane == 1)
        return planes.y;
    return planes.z;
}

// View direction expressed in the tangent frame of one axis-aligned plane, matching the UV
// conventions above: X -> (T,B,N) = (+Z,+Y,+X), Y -> (+X,+Z,+Y), Z -> (+X,+Y,+Z). The N
// component keeps its sign, so a downward-facing surface marches the ray the other way and
// the parallax still leans correctly.
float3 TransvoxelViewTangent(int plane, float3 viewDirWS)
{
    if (plane == 0) return float3(viewDirWS.z, viewDirWS.y, viewDirWS.x);
    if (plane == 1) return float3(viewDirWS.x, viewDirWS.z, viewDirWS.y);
    return float3(viewDirWS.x, viewDirWS.y, viewDirWS.z);
}

// ---- gradient-explicit layer reads -------------------------------------------------

half3 TransvoxelAlbedoAt(TransvoxelPlaneUV plane, int id)
{
    float s = _TransvoxelLayerScales[id].x;
    return SAMPLE_TEXTURE2D_ARRAY_GRAD(_TransvoxelAlbedoArray, sampler_TransvoxelAlbedoArray,
        plane.uv * s, id, plane.dx * s, plane.dy * s).rgb;
}

half TransvoxelOcclusionAt(TransvoxelPlaneUV plane, int id)
{
    float s = _TransvoxelLayerScales[id].x;
    return SAMPLE_TEXTURE2D_ARRAY_GRAD(_TransvoxelOcclusionArray, sampler_TransvoxelAlbedoArray,
        plane.uv * s, id, plane.dx * s, plane.dy * s).r;
}

half3 TransvoxelNormalAt(TransvoxelPlaneUV plane, int id)
{
    float4 scales = _TransvoxelLayerScales[id];
    float s = scales.x;
    return UnpackNormalScale(SAMPLE_TEXTURE2D_ARRAY_GRAD(_TransvoxelNormalArray,
        sampler_TransvoxelAlbedoArray, plane.uv * s, id, plane.dx * s, plane.dy * s), scales.y);
}

float TransvoxelHeightAt(TransvoxelPlaneUV plane, int id)
{
    float s = _TransvoxelLayerScales[id].x;
    return SAMPLE_TEXTURE2D_ARRAY_GRAD(_TransvoxelHeightArray, sampler_TransvoxelAlbedoArray,
        plane.uv * s, id, plane.dx * s, plane.dy * s).r;
}

// Height of the triangle's three palette layers, blended with the material weights. This is
// what the ray march evaluates per step, so it stays deliberately cheap — and gradient
// explicit, because it runs inside a divergent loop.
float TransvoxelBlendedHeight(TransvoxelPlaneUV plane, int3 ids, float3 w)
{
    return w.x * TransvoxelHeightAt(plane, ids.x)
         + w.y * TransvoxelHeightAt(plane, ids.y)
         + w.z * TransvoxelHeightAt(plane, ids.z);
}

// Blended parallax amplitude of the triangle's layers (per-layer, so rock can be deeper
// than sand), faded out with distance so far chunks never pay for a ray march.
float TransvoxelParallaxAmplitude(int3 ids, float3 w, float3 positionWS)
{
    float amplitude = w.x * _TransvoxelLayerScales[ids.x].w
                    + w.y * _TransvoxelLayerScales[ids.y].w
                    + w.z * _TransvoxelLayerScales[ids.z].w;
    if (_TransvoxelParallaxDistance > 0.0)
    {
        float d = distance(positionWS, _WorldSpaceCameraPos.xyz);
        amplitude *= saturate(1.0 - d / _TransvoxelParallaxDistance);
    }
    return amplitude;
}

// Marches the blended heightfield along the view ray and returns the displaced plane UV
// (derivatives preserved from the undisplaced surface). Step count adapts to the viewing
// angle: head-on needs few steps, grazing needs many because the ray travels further.
TransvoxelPlaneUV TransvoxelParallaxMarch(TransvoxelPlaneUV plane, float3 viewTS,
    int3 ids, float3 w, float amplitude)
{
    // Near-grazing rays would need an unbounded number of steps and swim badly; the effect
    // is imperceptible there anyway.
    if (amplitude <= 1e-5 || abs(viewTS.z) < 0.15)
        return plane;

    float steps = lerp(_TransvoxelParallaxMaxSteps, _TransvoxelParallaxMinSteps, abs(viewTS.z));
    steps = clamp(steps, 2.0, 128.0);
    float layerDepth = 1.0 / steps;
    float2 deltaUV = (viewTS.xy / viewTS.z) * amplitude * layerDepth;

    TransvoxelPlaneUV current = plane;
    float currentDepth = 0.0;
    float height = TransvoxelBlendedHeight(current, ids, w);
    float prevHeight = height;
    float prevDepth = 0.0;

    [loop]
    for (int i = 0; i < (int)steps; i++)
    {
        if (currentDepth >= 1.0 - height)
            break;
        prevHeight = height;
        prevDepth = currentDepth;
        current.uv -= deltaUV;
        currentDepth += layerDepth;
        height = TransvoxelBlendedHeight(current, ids, w);
    }

    // Linear refinement between the last step outside the surface and the first inside it,
    // which removes the stair-stepping a fixed step count would otherwise show.
    float after = (1.0 - height) - currentDepth;      // <= 0
    float before = (1.0 - prevHeight) - prevDepth;    // >= 0
    float weight = saturate(after / min(after - before, -1e-6));
    current.uv = lerp(current.uv, current.uv + deltaUV, weight);
    return current;
}

// Surface of the projected paths, with the normal already resolved to world space: the
// triplanar blend produces one directly, and the planar path rebuilds a tangent frame from
// screen-space derivatives (terrain meshes carry no tangents).
struct TransvoxelProjectedSurface
{
    half3 albedo;
    half3 normalWS;
    half occlusion;
    half smoothness;
};

// The full surface blend with triplanar projection and/or parallax applied. `triplanar`
// and `parallax` are compile-time constants in the bundled shader (keywords) and dynamic
// globals in a Shader Graph.
TransvoxelProjectedSurface TransvoxelPaletteBlendProjected(float2 uv, float3 positionWS,
    float3 normalWS, float3 idsRaw, float2 w01, bool triplanar, bool parallax)
{
    int3 ids;
    float3 w;
    TransvoxelPaletteSetup(idsRaw, w01, ids, w);

    TransvoxelPlanes planes = TransvoxelBuildPlanes(uv, positionWS, normalWS, triplanar);
    int plane = planes.dominant;

    if (parallax)
    {
        float amplitude = TransvoxelParallaxAmplitude(ids, w, positionWS);
        float3 viewDirWS = normalize(_WorldSpaceCameraPos.xyz - positionWS);
        float3 viewTS = TransvoxelViewTangent(plane, viewDirWS);
        TransvoxelPlaneUV marched = TransvoxelParallaxMarch(TransvoxelGetPlane(planes, plane),
                                                            viewTS, ids, w, amplitude);

        // Only the dominant plane is marched: at any sensible triplanar sharpness it owns
        // almost all of the blend wherever parallax is actually visible, and marching three
        // heightfields would triple the cost of the most expensive part of the shader.
        if (plane == 0) planes.x = marched;
        else if (plane == 1) planes.y = marched;
        else planes.z = marched;
    }

    // Height-steered material weights, evaluated at the (possibly displaced) UV so the
    // material boundary follows the parallax rather than sliding against it.
    TransvoxelPlaneUV steer = TransvoxelGetPlane(planes, plane);
    float3 heights = float3(TransvoxelHeightAt(steer, ids.x),
                            TransvoxelHeightAt(steer, ids.y),
                            TransvoxelHeightAt(steer, ids.z));
    w *= exp2(heights * (_TransvoxelHeightBlend * 8.0));
    w /= max(w.x + w.y + w.z, 1e-5);

    TransvoxelProjectedSurface result;
    result.albedo = 0;
    result.occlusion = 0;
    result.smoothness = 0;
    half3 normalAccum = 0;

    int3 idArray = ids;
    float3 wArray = w;
    [unroll]
    for (int k = 0; k < 3; k++)
    {
        int id = k == 0 ? idArray.x : (k == 1 ? idArray.y : idArray.z);
        float weight = k == 0 ? wArray.x : (k == 1 ? wArray.y : wArray.z);
        float4 layerColor = _TransvoxelLayerColors[id];
        float occlusionStrength = _TransvoxelLayerScales[id].z;

        half3 albedo;
        half occlusion;
        half3 normal;
        if (triplanar)
        {
            albedo = TransvoxelAlbedoAt(planes.x, id) * planes.blend.x
                   + TransvoxelAlbedoAt(planes.y, id) * planes.blend.y
                   + TransvoxelAlbedoAt(planes.z, id) * planes.blend.z;
            occlusion = TransvoxelOcclusionAt(planes.x, id) * planes.blend.x
                      + TransvoxelOcclusionAt(planes.y, id) * planes.blend.y
                      + TransvoxelOcclusionAt(planes.z, id) * planes.blend.z;

            // Whiteout blend: fold the geometric normal into each plane's tangent-space
            // normal, then swizzle each one onto its world axes. The result is a WORLD
            // normal, so triplanar needs no tangent frame at all — which suits meshes that
            // carry none.
            half3 tx = TransvoxelNormalAt(planes.x, id);
            half3 ty = TransvoxelNormalAt(planes.y, id);
            half3 tz = TransvoxelNormalAt(planes.z, id);
            tx = half3(tx.xy + normalWS.zy, abs(tx.z) * normalWS.x);
            ty = half3(ty.xy + normalWS.xz, abs(ty.z) * normalWS.y);
            tz = half3(tz.xy + normalWS.xy, abs(tz.z) * normalWS.z);
            normal = tx.zyx * planes.blend.x + ty.xzy * planes.blend.y + tz.xyz * planes.blend.z;
        }
        else
        {
            albedo = TransvoxelAlbedoAt(planes.y, id);
            occlusion = TransvoxelOcclusionAt(planes.y, id);
            normal = TransvoxelNormalAt(planes.y, id); // tangent space, resolved below
        }

        result.albedo += weight * albedo * layerColor.rgb;
        result.occlusion += weight * (1.0 + occlusionStrength * (occlusion - 1.0));
        result.smoothness += weight * layerColor.a;
        normalAccum += weight * normal;
    }

    result.normalWS = triplanar
        ? normalize(normalAccum)
        : TransvoxelPerturbNormal(normalWS, normalAccum, positionWS, planes.y.uv);
    return result;
}

// Albedo-only triplanar blend, for palettes without detail maps. Parallax needs a
// heightfield, so it never applies on this path.
half4 TransvoxelPaletteBlendTriplanar(float2 uv, float3 positionWS, float3 normalWS,
    float3 idsRaw, float2 w01)
{
    int3 ids;
    float3 w;
    TransvoxelPaletteSetup(idsRaw, w01, ids, w);
    TransvoxelPlanes planes = TransvoxelBuildPlanes(uv, positionWS, normalWS, true);

    half4 result = 0;
    [unroll]
    for (int k = 0; k < 3; k++)
    {
        int id = k == 0 ? ids.x : (k == 1 ? ids.y : ids.z);
        float weight = k == 0 ? w.x : (k == 1 ? w.y : w.z);
        half3 albedo = TransvoxelAlbedoAt(planes.x, id) * planes.blend.x
                     + TransvoxelAlbedoAt(planes.y, id) * planes.blend.y
                     + TransvoxelAlbedoAt(planes.z, id) * planes.blend.z;
        float4 layerColor = _TransvoxelLayerColors[id];
        result += weight * half4(albedo * layerColor.rgb, layerColor.a);
    }
    return result;
}

// ------------------------------------------------------------- Shader Graph entry points

// VERTEX stage: expand this vertex's corner index (vertex color alpha) into the one-hot
// weights. Store the output in a Custom Interpolator block so rasterization turns the
// one-hots into exact barycentric weights (the third weight is 1 - x - y).
void TransvoxelBlendCorner_float(float4 VertexColor, out float2 CornerWeights)
{
    float3 ids;
    TransvoxelDecodeBlend(VertexColor, ids, CornerWeights);
}

void TransvoxelBlendCorner_half(half4 VertexColor, out half2 CornerWeights)
{
    float2 weights;
    TransvoxelBlendCorner_float(VertexColor, weights);
    CornerWeights = (half2)weights;
}

// FRAGMENT stage, albedo/tint palettes: blended albedo (tint applied) and smoothness.
void TransvoxelPaletteAlbedo_float(float2 UV, float4 VertexColor, float2 CornerWeights,
    out float3 Albedo, out float Smoothness)
{
    half4 blended = TransvoxelPaletteBlend(UV, VertexColor.rgb * 255.0, CornerWeights);
    Albedo = blended.rgb;
    Smoothness = blended.a;
}

void TransvoxelPaletteAlbedo_half(half2 UV, half4 VertexColor, half2 CornerWeights,
    out half3 Albedo, out half Smoothness)
{
    float3 albedo;
    float smoothness;
    TransvoxelPaletteAlbedo_float(UV, VertexColor, CornerWeights, albedo, smoothness);
    Albedo = (half3)albedo;
    Smoothness = (half)smoothness;
}

// FRAGMENT stage, full detail maps (normal / occlusion / height-steered blending).
// Normal is WORLD space — set the graph's Fragment Normal Space to World and wire it
// into the Normal block; Occlusion goes into Ambient Occlusion.
void TransvoxelPaletteMaps_float(float2 UV, float4 VertexColor, float2 CornerWeights,
    float3 PositionWS, float3 NormalWS,
    out float3 Albedo, out float3 Normal, out float Occlusion, out float Smoothness)
{
    TransvoxelSurface s = TransvoxelPaletteBlendFull(UV, VertexColor.rgb * 255.0, CornerWeights);
    Albedo = s.albedo;
    Normal = TransvoxelPerturbNormal(normalize(NormalWS), s.normalTS, PositionWS, UV);
    Occlusion = s.occlusion;
    Smoothness = s.smoothness;
}

void TransvoxelPaletteMaps_half(half2 UV, half4 VertexColor, half2 CornerWeights,
    half3 PositionWS, half3 NormalWS,
    out half3 Albedo, out half3 Normal, out half Occlusion, out half Smoothness)
{
    float3 albedo;
    float3 normal;
    float occlusion;
    float smoothness;
    TransvoxelPaletteMaps_float(UV, VertexColor, CornerWeights, PositionWS, NormalWS,
        albedo, normal, occlusion, smoothness);
    Albedo = (half3)albedo;
    Normal = (half3)normal;
    Occlusion = (half)occlusion;
    Smoothness = (half)smoothness;
}

// FRAGMENT stage, the works: the palette's own triplanar and parallax settings, honoured
// dynamically (a graph has no keyword machinery, so this branches on the globals the
// terrain pushes). Same wiring as TransvoxelPaletteMaps, and Normal is WORLD space again —
// set Graph Settings > Fragment Normal Space to World.
//
// Triplanar replaces UV0 entirely, so the UV input only matters while triplanar is off;
// wire it anyway so the palette can be switched between the two without re-wiring.
void TransvoxelPaletteProjected_float(float2 UV, float4 VertexColor, float2 CornerWeights,
    float3 PositionWS, float3 NormalWS,
    out float3 Albedo, out float3 Normal, out float Occlusion, out float Smoothness)
{
    TransvoxelProjectedSurface s = TransvoxelPaletteBlendProjected(
        UV, PositionWS, normalize(NormalWS), VertexColor.rgb * 255.0, CornerWeights,
        _TransvoxelTriplanarEnabled > 0.5, _TransvoxelParallaxEnabled > 0.5);
    Albedo = s.albedo;
    Normal = s.normalWS;
    Occlusion = s.occlusion;
    Smoothness = s.smoothness;
}

void TransvoxelPaletteProjected_half(half2 UV, half4 VertexColor, half2 CornerWeights,
    half3 PositionWS, half3 NormalWS,
    out half3 Albedo, out half3 Normal, out half Occlusion, out half Smoothness)
{
    float3 albedo;
    float3 normal;
    float occlusion;
    float smoothness;
    TransvoxelPaletteProjected_float(UV, VertexColor, CornerWeights, PositionWS, NormalWS,
        albedo, normal, occlusion, smoothness);
    Albedo = (half3)albedo;
    Normal = (half3)normal;
    Occlusion = (half)occlusion;
    Smoothness = (half)smoothness;
}

#endif // TRANSVOXEL_PALETTE_INCLUDED
