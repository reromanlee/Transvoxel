// Lit terrain shader with screen-space stipple (Bayer dither) fading — the same visual
// idea as Unity's LOD Group cross-fade.
//
// The fade is driven ENTIRELY by mesh vertex data plus global uniforms — deliberately no
// per-renderer state (MaterialPropertyBlocks) and no per-chunk material values, because
// batched render paths (e.g. URP's GPU Resident Drawer) can bypass those. Mesh data and
// globals reach every pixel on every path.
//
//   * UV2 per vertex = (fadeStartTime, ghostFlag), written once by TerrainChunk:
//       ghostFlag 0 — the chunk dithers IN:  fade goes 0 -> 1 after fadeStartTime;
//       ghostFlag 1 — a cross-fade GHOST dithering OUT: the shader keeps exactly the
//                     pixels the successor (which started fading in at the same moment)
//                     does not draw yet, so the pair always covers the surface with no
//                     holes and no double-drawn pixels.
//   * _TransvoxelTime / _TransvoxelFadeSeconds globals animate the fade — zero per-frame
//     CPU work per chunk.
//   * _TransvoxelViewerPos / _TransvoxelViewDistance / _TransvoxelEdgeFadeBand globals
//     add a per-PIXEL dissolve toward the draw distance, so distant terrain fades like
//     fog instead of popping — even when one far chunk spans kilometers.
//
// Meshes without UV2 read (0,0): with the default _TransvoxelFade of 1 and start time 0
// they render solid, so the shader is safe on any mesh.
//
// The fade/dither core itself lives in TransvoxelDither.hlsl (same folder) — a reusable
// module both subshaders include, and the same file a Shader Graph Custom Function node
// or any custom shader pulls in to become fade-aware (see the README's Dithered fading
// section).
//
// SubShader 1 targets URP (skipped automatically when URP is absent), SubShader 2 is the
// Built-in pipeline fallback. HDRP is not supported — keep the HDRP/Lit default there.

Shader "Transvoxel/Lit Dithered"
{
    Properties
    {
        _BaseColor("Color", Color) = (0.42, 0.55, 0.3, 1)
        _BaseMap("Albedo", 2D) = "white" {}
        _Smoothness("Smoothness", Range(0, 1)) = 0.1
        // Markers only: TransvoxelTerrain detects fade/palette-aware materials via
        // HasProperty. The actual inputs (_TransvoxelFade, the palette arrays) are GLOBAL
        // uniforms — deliberately not serialized properties, so the SRP Batcher can never
        // lock them to a material value.
        [HideInInspector] _TransvoxelFadeAware("Fade Aware", Float) = 1
        [HideInInspector] _TransvoxelPaletteAware("Palette Aware", Float) = 1
    }

    // ------------------------------------------------------------------ URP
    SubShader
    {
        PackageRequirements
        {
            "com.unity.render-pipelines.universal"
        }
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

        // The whole fade/dither core (globals, Bayer matrix, TransvoxelVertexFade,
        // TransvoxelDitherClip) lives in the reusable module — the same file a Shader
        // Graph Custom Function node or any custom shader includes. All fade inputs are
        // GLOBAL uniforms (never in UnityPerMaterial): the SRP Batcher sources
        // per-material cbuffer values from the material and ignores Shader.SetGlobal*
        // for them, so a fade value trapped there would be locked at the inspector value
        // for batched draws. The per-chunk time fade uses only Unity's built-in _Time.
        #include "TransvoxelDither.hlsl"

        // Either palette variant: albedo-only, or with the detail-map arrays. The two
        // keywords are one multi_compile set, so exactly one (or neither) is active.
        #if defined(TRANSVOXEL_PALETTE) || defined(TRANSVOXEL_PALETTE_MAPS)
            #define TRANSVOXEL_ANY_PALETTE 1
        #endif

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST;
        half4 _BaseColor;
        half _Smoothness;
        CBUFFER_END

        // Material palette (TRANSVOXEL_PALETTE / TRANSVOXEL_PALETTE_MAPS variants; all
        // globals, driven by the terrain): one texture array per map kind holds every
        // layer's texture — the material id indexes it per pixel, so nothing here grows
        // with the palette. The fixed 64 is the declared capacity of
        // TransvoxelMaterialPalette.MaxLayers, not a per-slot cost.
        TEXTURE2D_ARRAY(_TransvoxelAlbedoArray);
        SAMPLER(sampler_TransvoxelAlbedoArray);
        float4 _TransvoxelLayerColors[64];    // rgb = tint, a = smoothness
        float4 _TransvoxelLayerScales[64];    // x = uv scale, y = normal str, z = occlusion str
        float _TransvoxelBlendSharpness;      // live materialBlendSharpness setting
        float _TransvoxelPaletteLayerCount;

        // Detail-map arrays (TRANSVOXEL_PALETTE_MAPS only; bound by the terrain only for
        // palettes that contain such maps — empty slots hold baked neutral fallbacks:
        // flat normal, white occlusion, mid height). All share the albedo sampler. Height
        // does not displace anything: it steers the blend weights so the higher material
        // (rock, cobbles) cuts through the lower one (sand) at boundaries.
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

        // Shared weight setup for both palette variants: clamp the id triple and sharpen
        // the barycentric corner weights by pow() — 1 blends across the whole boundary
        // cell, higher values tighten the transition toward a hard cut (the
        // materialBlendSharpness setting).
        void TransvoxelPaletteSetup(float3 idsRaw, float2 w01, out int3 ids, out float3 w)
        {
            int maxLayer = max((int)_TransvoxelPaletteLayerCount - 1, 0);
            ids = clamp((int3)round(idsRaw), 0, maxLayer);

            w = float3(w01, saturate(1.0 - w01.x - w01.y));
            w = pow(max(w, 0.0), _TransvoxelBlendSharpness);
            w /= max(w.x + w.y + w.z, 1e-5);
        }

        // Blends the (up to) three palette layers of the triangle (albedo-only variant).
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

        // ---------------------------------------------------- palette detail maps (MAPS)

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

        // Full surface blend of the triangle's palette layers: albedo, tangent-space
        // normal, occlusion and smoothness. The pow-sharpened weights are additionally
        // steered by the height maps: multiplying by exp2(k·height) is an exact identity
        // for equal heights (so map-less layers keep the plain crossfade) and lets the
        // higher material win the boundary in proportion to _TransvoxelHeightBlend.
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

        // Applies the blended tangent-space normal without precomputed tangents (the
        // meshes carry none): the frame is rebuilt from screen-space derivatives of the
        // world position and UV (Schüler's cotangent-frame construction), so it matches
        // whatever the UV mapping does at any surface orientation. Degenerate UV areas
        // fall back to the geometric normal. Fragment stage only (ddx/ddy).
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

        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex LitPassVertex
            #pragma fragment LitPassFragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile _ TRANSVOXEL_PALETTE TRANSVOXEL_PALETTE_MAPS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 fadeData : TEXCOORD1;
#if defined(TRANSVOXEL_ANY_PALETTE)
                float4 color : COLOR; // material blend data (MaterialBlendEncoder)
#endif
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                // x = time fade; yz = material corner weights (barycentric after
                // interpolation; the third weight is 1 - y - z).
                float3 fadeAndWeights : TEXCOORD4;
#if defined(TRANSVOXEL_ANY_PALETTE)
                float3 materialIds : TEXCOORD5;
#endif
            };

            Varyings LitPassVertex(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = normal.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
                output.fadeAndWeights = float3(TransvoxelVertexFade(input.fadeData), 0.0, 0.0);
#if defined(TRANSVOXEL_ANY_PALETTE)
                float2 cornerWeights;
                TransvoxelDecodeBlend(input.color, output.materialIds, cornerWeights);
                output.fadeAndWeights.yz = cornerWeights;
#endif
                return output;
            }

            half4 LitPassFragment(Varyings input) : SV_Target
            {
                TransvoxelDitherClip(input.positionCS, input.positionWS, input.fadeAndWeights.x);

                float3 normalWS = normalize(input.normalWS);
                half occlusion = 1.0;
#if defined(TRANSVOXEL_PALETTE_MAPS)
                TransvoxelSurface surface = TransvoxelPaletteBlendFull(input.uv, input.materialIds,
                                                                       input.fadeAndWeights.yz);
                half3 albedo = surface.albedo;
                normalWS = TransvoxelPerturbNormal(normalWS, surface.normalTS,
                                                   input.positionWS, input.uv);
                occlusion = surface.occlusion;
#elif defined(TRANSVOXEL_PALETTE)
                half3 albedo = TransvoxelPaletteBlend(input.uv, input.materialIds,
                                                      input.fadeAndWeights.yz).rgb;
#else
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
#endif

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                // Occlusion attenuates the ambient/indirect term only, like Unity's Lit
                // shaders (it folds to ×1 outside the MAPS variant).
                half3 lighting = SampleSH(normalWS) * occlusion;
                lighting += mainLight.color
                            * (mainLight.shadowAttenuation * saturate(dot(normalWS, mainLight.direction)));

                half3 color = albedo * lighting;
                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 fadeData : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float fade : TEXCOORD1;
            };

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
#if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif
                output.positionCS = positionCS;
                output.positionWS = positionWS;
                output.fade = TransvoxelVertexFade(input.fadeData);
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                TransvoxelDitherClip(input.positionCS, input.positionWS, input.fade);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 fadeData : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float fade : TEXCOORD1;
            };

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.fade = TransvoxelVertexFade(input.fadeData);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_Target
            {
                TransvoxelDitherClip(input.positionCS, input.positionWS, input.fade);
                return 0;
            }
            ENDHLSL
        }
    }

    // ------------------------------------------------------------------ Built-in pipeline
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        // addshadow regenerates the shadow pass from surf, so the dither clip also
        // dissolves the chunk's shadow while it fades.
        #pragma surface surf Standard fullforwardshadows addshadow vertex:vert
        #pragma multi_compile _ TRANSVOXEL_PALETTE TRANSVOXEL_PALETTE_MAPS
        #pragma target 3.5

        sampler2D _BaseMap;
        fixed4 _BaseColor;
        half _Smoothness;

        // The shared fade/dither module (see the URP subshader note: fade inputs are
        // never material properties). Without an SRP core include it declares classic
        // sampler2D resources, so it compiles cleanly in CGPROGRAM.
        #include "TransvoxelDither.hlsl"

        // Material palette globals — same contract as the URP subshader. The detail-map
        // arrays share the albedo array's sampler.
        UNITY_DECLARE_TEX2DARRAY(_TransvoxelAlbedoArray);
        UNITY_DECLARE_TEX2DARRAY_NOSAMPLER(_TransvoxelNormalArray);
        UNITY_DECLARE_TEX2DARRAY_NOSAMPLER(_TransvoxelOcclusionArray);
        UNITY_DECLARE_TEX2DARRAY_NOSAMPLER(_TransvoxelHeightArray);
        float4 _TransvoxelLayerColors[64];    // rgb = tint, a = smoothness
        float4 _TransvoxelLayerScales[64];    // x = uv scale, y = normal str, z = occlusion str
        float _TransvoxelBlendSharpness;
        float _TransvoxelPaletteLayerCount;
        float _TransvoxelHeightBlend;

#if defined(TRANSVOXEL_PALETTE_MAPS)
        // Full surface blend — same math as the URP subshader's TransvoxelPaletteBlendFull.
        struct TransvoxelSurface
        {
            float3 albedo;
            float3 normalTS;
            float occlusion;
            float smoothness;
        };

        float TransvoxelLayerHeight(float2 uv, int id)
        {
            return UNITY_SAMPLE_TEX2DARRAY_SAMPLER(_TransvoxelHeightArray, _TransvoxelAlbedoArray,
                float3(uv * _TransvoxelLayerScales[id].x, id)).r;
        }

        void TransvoxelAccumulateLayer(inout TransvoxelSurface s, float2 uv, int id, float weight)
        {
            float4 layerColor = _TransvoxelLayerColors[id];
            float4 scales = _TransvoxelLayerScales[id];
            float3 layerUv = float3(uv * scales.x, id);

            float3 albedo = UNITY_SAMPLE_TEX2DARRAY(_TransvoxelAlbedoArray, layerUv).rgb;
            float3 normalTS = UnpackScaleNormal(
                UNITY_SAMPLE_TEX2DARRAY_SAMPLER(_TransvoxelNormalArray, _TransvoxelAlbedoArray, layerUv),
                scales.y);
            float occlusion = UNITY_SAMPLE_TEX2DARRAY_SAMPLER(_TransvoxelOcclusionArray,
                _TransvoxelAlbedoArray, layerUv).r;

            s.albedo += weight * albedo * layerColor.rgb;
            s.normalTS += weight * normalTS;
            s.occlusion += weight * (1.0 + scales.z * (occlusion - 1.0)); // occlusion strength
            s.smoothness += weight * layerColor.a;
        }
#endif

        struct Input
        {
            float2 uv_BaseMap;
            float4 screenPos;
            float3 worldPos;
            float tvFade;
            // Material blend (palette variants only): the triangle's id triple and this
            // pixel's first two barycentric corner weights.
            float3 tvIds;
            float2 tvWeights;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.tvFade = TransvoxelVertexFade(v.texcoord1.xy);
#if defined(TRANSVOXEL_PALETTE) || defined(TRANSVOXEL_PALETTE_MAPS)
            o.tvIds = v.color.rgb * 255.0;
            int corner = (int)round(v.color.a * 255.0);
            o.tvWeights = float2(corner == 0 ? 1.0 : 0.0, corner == 1 ? 1.0 : 0.0);
#endif
#if defined(TRANSVOXEL_PALETTE_MAPS)
            // The meshes carry no tangents, and UV0 is the world-space XZ planar map —
            // so the tangent frame is the fixed terrain one (+X tangent; w = -1 makes
            // cross(normal, tangent) the +Z bitangent, matching v = z). Chunks are
            // axis-aligned, so object axes are world axes.
            v.tangent = float4(1.0, 0.0, 0.0, -1.0);
#endif
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            // Same clip rules as the URP subshader (shared module): positive fade dithers
            // in, a negative fade is a ghost keeping the complementary pixel set, both
            // capped by the edge dissolve.
            TransvoxelDitherClipScreenPos(IN.screenPos, IN.worldPos, IN.tvFade);

#if defined(TRANSVOXEL_PALETTE) || defined(TRANSVOXEL_PALETTE_MAPS)
            // Blend the triangle's palette layers with sharpened barycentric weights —
            // the same math as the URP subshader's TransvoxelPaletteSetup.
            int maxLayer = max((int)_TransvoxelPaletteLayerCount - 1, 0);
            int3 ids = clamp((int3)round(IN.tvIds), 0, maxLayer);
            float3 w = float3(IN.tvWeights, saturate(1.0 - IN.tvWeights.x - IN.tvWeights.y));
            w = pow(max(w, 0.0), _TransvoxelBlendSharpness);
            w /= max(w.x + w.y + w.z, 1e-5);
#endif

#if defined(TRANSVOXEL_PALETTE_MAPS)
            // Height-steered weights, then the full surface blend — same math as the URP
            // subshader's TransvoxelPaletteBlendFull.
            float3 heights = float3(TransvoxelLayerHeight(IN.uv_BaseMap, ids.x),
                                    TransvoxelLayerHeight(IN.uv_BaseMap, ids.y),
                                    TransvoxelLayerHeight(IN.uv_BaseMap, ids.z));
            w *= exp2(heights * (_TransvoxelHeightBlend * 8.0));
            w /= max(w.x + w.y + w.z, 1e-5);

            TransvoxelSurface s = (TransvoxelSurface)0;
            TransvoxelAccumulateLayer(s, IN.uv_BaseMap, ids.x, w.x);
            TransvoxelAccumulateLayer(s, IN.uv_BaseMap, ids.y, w.y);
            TransvoxelAccumulateLayer(s, IN.uv_BaseMap, ids.z, w.z);

            o.Albedo = s.albedo;
            o.Normal = normalize(s.normalTS);
            o.Occlusion = s.occlusion;
            o.Smoothness = s.smoothness;
#elif defined(TRANSVOXEL_PALETTE)
            float4 blended = 0;
            blended += w.x * float4(UNITY_SAMPLE_TEX2DARRAY(_TransvoxelAlbedoArray,
                float3(IN.uv_BaseMap * _TransvoxelLayerScales[ids.x].x, ids.x)).rgb
                * _TransvoxelLayerColors[ids.x].rgb, _TransvoxelLayerColors[ids.x].a);
            blended += w.y * float4(UNITY_SAMPLE_TEX2DARRAY(_TransvoxelAlbedoArray,
                float3(IN.uv_BaseMap * _TransvoxelLayerScales[ids.y].x, ids.y)).rgb
                * _TransvoxelLayerColors[ids.y].rgb, _TransvoxelLayerColors[ids.y].a);
            blended += w.z * float4(UNITY_SAMPLE_TEX2DARRAY(_TransvoxelAlbedoArray,
                float3(IN.uv_BaseMap * _TransvoxelLayerScales[ids.z].x, ids.z)).rgb
                * _TransvoxelLayerColors[ids.z].rgb, _TransvoxelLayerColors[ids.z].a);

            o.Albedo = blended.rgb;
            o.Smoothness = blended.a;
#else
            fixed4 albedo = tex2D(_BaseMap, IN.uv_BaseMap) * _BaseColor;
            o.Albedo = albedo.rgb;
            o.Smoothness = _Smoothness;
#endif
            o.Alpha = 1;
        }
        ENDCG
    }

    FallBack "Diffuse"
}
