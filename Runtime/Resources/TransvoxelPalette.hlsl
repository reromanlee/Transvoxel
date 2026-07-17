// Reusable Transvoxel material-palette module — the blend of the terrain's per-layer
// texture arrays behind Transvoxel/Lit Dithered, packaged so URP shaders and Shader
// Graphs can render voxel materials too. (SRP contexts only: the Built-in pipeline
// palette path lives in TransvoxelLitDithered.shader's CG subshader.)
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
#error TransvoxelPalette.hlsl needs an SRP include context (a URP shader or Shader Graph); the Built-in pipeline palette path is implemented directly in TransvoxelLitDithered.shader.
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

#endif // TRANSVOXEL_PALETTE_INCLUDED
