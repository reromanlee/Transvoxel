// Reusable Transvoxel stipple-fade module — the single implementation behind
// Transvoxel/Lit Dithered, packaged so ANY shader can opt into the chunk cross-fades and
// the draw-distance dissolve. Three ways to consume it (full walkthrough in the README):
//
//   * Shader Graph: add a Custom Function node, Type "File", Source = this file,
//     Name = "TransvoxelDitherFade". Inputs: FadeData (Vector2, wire a UV node set to
//     UV1), PositionWS (Vector3, wire a Position node in World space), ScreenPos
//     (Vector4, wire a Screen Position node in Raw mode). Output: Alpha (Float, 0 or 1).
//     Wire Alpha into the Master Stack's Alpha, enable Alpha Clipping, keep the
//     threshold at 0.5. Add a Float property with reference name _TransvoxelFadeAware
//     (default 1) so TransvoxelTerrain detects the material as fade-aware. Save the
//     wired-up node as a Sub Graph once and drop it into every graph after that.
//
//   * Hand-written URP shader: #include this file after URP's Core.hlsl. Vertex stage:
//     read float2 fadeData : TEXCOORD1 and pass TransvoxelVertexFade(fadeData) down as a
//     varying. Fragment stage, first statement:
//     TransvoxelDitherClip(input.positionCS, input.positionWS, fade). Repeat for every
//     pass that draws the mesh (forward, ShadowCaster, DepthOnly) or shadows/depth will
//     not dissolve with the surface. Declare the _TransvoxelFadeAware marker property.
//
//   * Built-in pipeline (CGPROGRAM): same include and calls. Surface shaders have no
//     SV_POSITION input — use TransvoxelDitherClipScreenPos(IN.screenPos, IN.worldPos,
//     fade) instead. TransvoxelLitDithered.shader's second subshader is the reference.
//
// Everything here is driven by mesh UV1 (TEXCOORD1 — what Mesh.uv2 stores) plus GLOBAL
// uniforms only. Never redeclare these uniforms as material properties, Properties-block
// entries or Shader Graph Blackboard properties: the SRP Batcher sources UnityPerMaterial
// values from the material and ignores Shader.SetGlobal* for them, which would lock the
// fade at the inspector value for batched draws. Meshes without UV1 read (0,0) and render
// solid, so the module is safe on any mesh. TransvoxelShaderGlobals boots the master fade
// global to its neutral 1 at editor/player startup; a running terrain drives it (and the
// edge-dissolve globals) every frame.

#ifndef TRANSVOXEL_DITHER_INCLUDED
#define TRANSVOXEL_DITHER_INCLUDED

float4 _TransvoxelViewerPos;
float _TransvoxelViewDistance;
float _TransvoxelEdgeFadeBand;  // 0 = edge dissolve off
float _TransvoxelFade;          // master fade (1 = normal; terrain writes it every frame)

// 256x1 LUT baked from the edgeFadeCurve setting: input = raw edge fade (0 at the draw
// distance, 1 at the viewer), output = kept opacity. Only sampled while the band is > 0,
// so an unbound texture is never read. SRP texture macros when an SRP core include
// preceded us (URP shaders, every Shader Graph target); classic sampler2D otherwise
// (Built-in pipeline).
#if defined(UNITY_COMMON_INCLUDED)
TEXTURE2D(_TransvoxelEdgeFadeCurve);
SAMPLER(sampler_TransvoxelEdgeFadeCurve);
#define TransvoxelSampleEdgeCurve(rawEdge) \
    SAMPLE_TEXTURE2D_LOD(_TransvoxelEdgeFadeCurve, sampler_TransvoxelEdgeFadeCurve, \
                         float2(rawEdge, 0.5), 0).r
#else
sampler2D _TransvoxelEdgeFadeCurve;
#define TransvoxelSampleEdgeCurve(rawEdge) \
    tex2Dlod(_TransvoxelEdgeFadeCurve, float4(rawEdge, 0.5, 0, 0)).r
#endif

// 4x4 Bayer matrix, thresholds centered so fade 1 keeps every pixel.
static const float TransvoxelDither[16] =
{
     0.5 / 16.0,  8.5 / 16.0,  2.5 / 16.0, 10.5 / 16.0,
    12.5 / 16.0,  4.5 / 16.0, 14.5 / 16.0,  6.5 / 16.0,
     3.5 / 16.0, 11.5 / 16.0,  1.5 / 16.0,  9.5 / 16.0,
    15.5 / 16.0,  7.5 / 16.0, 13.5 / 16.0,  5.5 / 16.0
};

// Per-vertex fade from UV1 = (startTime, signedDuration): duration's sign marks a
// cross-fade ghost, its magnitude is the fade length in seconds, 0 = solid (meshes
// without UV1 read zero). Time base: Unity's built-in _Time.y (time since level load) —
// TerrainChunk writes start times on the same clock. Positive result = fading in,
// negative = ghost fading out with visibility -result (complementary window below).
float TransvoxelVertexFade(float2 fadeData)
{
    float duration = abs(fadeData.y);
    if (duration <= 0.0)
        return 1.0;
    float t = saturate((_Time.y - fadeData.x) / duration);
    return fadeData.y < 0.0 ? -(1.0 - t) : t;
}

// Draw-distance dissolve: kept opacity at this world position, reshaped through the
// edgeFadeCurve LUT. 1 when the edge fade is disabled.
float TransvoxelEdgeFade(float3 positionWS)
{
    if (_TransvoxelEdgeFadeBand <= 0.0)
        return 1.0;
    float rawEdge = saturate((_TransvoxelViewDistance - distance(positionWS, _TransvoxelViewerPos.xyz))
                             / _TransvoxelEdgeFadeBand);
    return TransvoxelSampleEdgeCurve(rawEdge);
}

// Signed keep-value for one pixel: >= 0 keeps it, < 0 discards it. Fading-in surfaces
// keep thresholds in [0, min(fade, edge)); a ghost (fade < 0, visibility g = -fade) keeps
// [1 - g, edge] — exactly the pixels its successor (which started fading in at the same
// moment) does not draw yet, so the pair always covers the surface with no holes and no
// double-drawn pixels.
float TransvoxelDitherKeepValue(uint2 pixel, float fade, float edge)
{
    float threshold = TransvoxelDither[(pixel.y & 3) * 4 + (pixel.x & 3)];
    if (fade < 0.0)
        return min(threshold - (1.0 + fade), edge - threshold);
    return min(fade, edge) - threshold;
}

// Fragment-stage clip for shaders with an SV_POSITION input (its xy are pixel coords).
void TransvoxelDitherClip(float4 positionCS, float3 positionWS, float vertexFade)
{
    float fade = vertexFade * _TransvoxelFade;
    float edge = TransvoxelEdgeFade(positionWS);
    if (fade >= 1.0 && edge >= 1.0)
        return;
    clip(TransvoxelDitherKeepValue(uint2(positionCS.xy), fade, edge));
}

// The same clip from a ComputeScreenPos-style raw screen position — for Built-in surface
// shaders (Input.screenPos) and anywhere SV_POSITION is not available.
void TransvoxelDitherClipScreenPos(float4 screenPos, float3 positionWS, float vertexFade)
{
    float fade = vertexFade * _TransvoxelFade;
    float edge = TransvoxelEdgeFade(positionWS);
    if (fade >= 1.0 && edge >= 1.0)
        return;
    float2 pixel = screenPos.xy / max(screenPos.w, 1e-4) * _ScreenParams.xy;
    clip(TransvoxelDitherKeepValue(uint2(pixel), fade, edge));
}

// ------------------------------------------------------------- Shader Graph entry points
//
// Binary alpha (1 = keep, 0 = discard) instead of clip(): wire it into the Master
// Stack's Alpha with Alpha Clipping enabled and the threshold at 0.5. The ghost logic is
// a threshold WINDOW, not a plain comparison, so the cutout must be decided here — a
// fractional alpha against a fixed threshold could not express it. Runs per fragment;
// FadeData is constant across a chunk, so evaluating the vertex fade here is exact.
void TransvoxelDitherFade_float(float2 FadeData, float3 PositionWS, float4 ScreenPos,
    out float Alpha)
{
    float fade = TransvoxelVertexFade(FadeData) * _TransvoxelFade;
    float edge = TransvoxelEdgeFade(PositionWS);
    if (fade >= 1.0 && edge >= 1.0)
    {
        Alpha = 1.0;
        return;
    }
    float2 pixel = ScreenPos.xy / max(ScreenPos.w, 1e-4) * _ScreenParams.xy;
    Alpha = TransvoxelDitherKeepValue(uint2(pixel), fade, edge) >= 0.0 ? 1.0 : 0.0;
}

void TransvoxelDitherFade_half(half2 FadeData, half3 PositionWS, half4 ScreenPos,
    out half Alpha)
{
    float alpha;
    TransvoxelDitherFade_float(FadeData, PositionWS, ScreenPos, alpha);
    Alpha = (half)alpha;
}

#endif // TRANSVOXEL_DITHER_INCLUDED
