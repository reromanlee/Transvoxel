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

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST;
        half4 _BaseColor;
        half _Smoothness;
        CBUFFER_END

        // Globals driven by TransvoxelTerrain every frame. All fade inputs live in global
        // scope (never in UnityPerMaterial): the SRP Batcher sources per-material cbuffer
        // values from the material and ignores Shader.SetGlobalFloat for them, so a fade
        // value trapped there would be locked at the inspector value for batched draws.
        // The per-chunk time fade additionally uses only Unity's built-in _Time.
        float4 _TransvoxelViewerPos;
        float _TransvoxelViewDistance;
        float _TransvoxelEdgeFadeBand;  // 0 = edge dissolve off
        float _TransvoxelFade;          // master fade, set globally (1 = normal)

        // 256x1 LUT baked from the edgeFadeCurve: input = raw edge fade (0 at the draw
        // distance, 1 at the viewer), output = kept opacity. Identity ramp by default, so a
        // missing/unbound texture just needs the branch below skipped (band 0) to stay safe.
        TEXTURE2D(_TransvoxelEdgeFadeCurve);
        SAMPLER(sampler_TransvoxelEdgeFadeCurve);

        // Material palette (TRANSVOXEL_PALETTE variants; all globals, driven by the
        // terrain): one texture array holds every layer's albedo — the material id indexes
        // it per pixel, so nothing here grows with the palette. The fixed 64 is the
        // declared capacity of TransvoxelMaterialPalette.MaxLayers, not a per-slot cost.
        TEXTURE2D_ARRAY(_TransvoxelAlbedoArray);
        SAMPLER(sampler_TransvoxelAlbedoArray);
        float4 _TransvoxelLayerColors[64];    // rgb = tint, a = smoothness
        float4 _TransvoxelLayerScales[64];    // x = uv scale multiplier
        float _TransvoxelBlendSharpness;      // live materialBlendSharpness setting
        float _TransvoxelPaletteLayerCount;

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

        // Blends the (up to) three palette layers of the triangle. The barycentric weights
        // are sharpened by pow(): 1 blends across the whole boundary cell, higher values
        // tighten the transition toward a hard cut — the materialBlendSharpness setting.
        // All three layers are sampled unconditionally so texture gradients stay uniform
        // (uniform triangles fetch the same texel thrice — cache-free).
        half4 TransvoxelPaletteBlend(float2 uv, float3 idsRaw, float2 w01)
        {
            int maxLayer = max((int)_TransvoxelPaletteLayerCount - 1, 0);
            int3 ids = clamp((int3)round(idsRaw), 0, maxLayer);

            float3 w = float3(w01, saturate(1.0 - w01.x - w01.y));
            w = pow(max(w, 0.0), _TransvoxelBlendSharpness);
            w /= max(w.x + w.y + w.z, 1e-5);

            return w.x * TransvoxelSampleLayer(uv, ids.x)
                 + w.y * TransvoxelSampleLayer(uv, ids.y)
                 + w.z * TransvoxelSampleLayer(uv, ids.z);
        }

        // 4x4 Bayer matrix, thresholds centered so fade 1 keeps every pixel.
        static const float TransvoxelDither[16] =
        {
             0.5 / 16.0,  8.5 / 16.0,  2.5 / 16.0, 10.5 / 16.0,
            12.5 / 16.0,  4.5 / 16.0, 14.5 / 16.0,  6.5 / 16.0,
             3.5 / 16.0, 11.5 / 16.0,  1.5 / 16.0,  9.5 / 16.0,
            15.5 / 16.0,  7.5 / 16.0, 13.5 / 16.0,  5.5 / 16.0
        };

        // Per-vertex fade from UV2 = (startTime, signedDuration): duration's sign marks a
        // ghost, its magnitude is the fade length in seconds, 0 = solid (meshes without
        // UV2 read zero). Time base: Unity's built-in _Time.y (time since level load) —
        // the C# side writes start times on the same clock. Positive result = fading in,
        // negative = ghost fading out with visibility -result (complementary clip below).
        float TransvoxelVertexFade(float2 fadeData)
        {
            float duration = abs(fadeData.y);
            if (duration <= 0.0)
                return 1.0;
            float t = saturate((_Time.y - fadeData.x) / duration);
            return fadeData.y < 0.0 ? -(1.0 - t) : t;
        }

        void TransvoxelDitherClip(float4 positionCS, float3 positionWS, float vertexFade)
        {
            float fade = vertexFade * _TransvoxelFade;
            float edge = 1.0;
            if (_TransvoxelEdgeFadeBand > 0.0)
            {
                float viewerDistance = distance(positionWS, _TransvoxelViewerPos.xyz);
                float rawEdge = saturate((_TransvoxelViewDistance - viewerDistance) / _TransvoxelEdgeFadeBand);
                edge = SAMPLE_TEXTURE2D_LOD(_TransvoxelEdgeFadeCurve, sampler_TransvoxelEdgeFadeCurve,
                                            float2(rawEdge, 0.5), 0).r;
            }
            if (fade >= 1.0 && edge >= 1.0)
                return;

            uint2 pixel = uint2(positionCS.xy) & 3;
            float threshold = TransvoxelDither[pixel.y * 4 + pixel.x];

            if (fade < 0.0)
            {
                // Ghost visibility g = -fade (1 -> 0): keep thresholds in [1 - g, edge] —
                // exactly the pixels the successor's window [0, min(fade, edge)] omits.
                clip(min(threshold - (1.0 + fade), edge - threshold));
                return;
            }
            clip(min(fade, edge) - threshold);
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
            #pragma multi_compile _ TRANSVOXEL_PALETTE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 fadeData : TEXCOORD1;
#if defined(TRANSVOXEL_PALETTE)
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
#if defined(TRANSVOXEL_PALETTE)
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
#if defined(TRANSVOXEL_PALETTE)
                float2 cornerWeights;
                TransvoxelDecodeBlend(input.color, output.materialIds, cornerWeights);
                output.fadeAndWeights.yz = cornerWeights;
#endif
                return output;
            }

            half4 LitPassFragment(Varyings input) : SV_Target
            {
                TransvoxelDitherClip(input.positionCS, input.positionWS, input.fadeAndWeights.x);

#if defined(TRANSVOXEL_PALETTE)
                half3 albedo = TransvoxelPaletteBlend(input.uv, input.materialIds,
                                                      input.fadeAndWeights.yz).rgb;
#else
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
#endif
                float3 normalWS = normalize(input.normalWS);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half3 lighting = SampleSH(normalWS);
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
        #pragma multi_compile _ TRANSVOXEL_PALETTE
        #pragma target 3.5

        sampler2D _BaseMap;
        fixed4 _BaseColor;
        half _Smoothness;

        // Globals (see the URP subshader note: fade inputs are never material properties).
        float4 _TransvoxelViewerPos;
        float _TransvoxelViewDistance;
        float _TransvoxelEdgeFadeBand;
        float _TransvoxelFade; // master fade, set globally (1 = normal)
        sampler2D _TransvoxelEdgeFadeCurve; // edgeFadeCurve LUT: raw edge fade -> kept opacity

        // Material palette globals — same contract as the URP subshader.
        UNITY_DECLARE_TEX2DARRAY(_TransvoxelAlbedoArray);
        float4 _TransvoxelLayerColors[64];    // rgb = tint, a = smoothness
        float4 _TransvoxelLayerScales[64];    // x = uv scale multiplier
        float _TransvoxelBlendSharpness;
        float _TransvoxelPaletteLayerCount;

        static const float TransvoxelDither[16] =
        {
             0.5 / 16.0,  8.5 / 16.0,  2.5 / 16.0, 10.5 / 16.0,
            12.5 / 16.0,  4.5 / 16.0, 14.5 / 16.0,  6.5 / 16.0,
             3.5 / 16.0, 11.5 / 16.0,  1.5 / 16.0,  9.5 / 16.0,
            15.5 / 16.0,  7.5 / 16.0, 13.5 / 16.0,  5.5 / 16.0
        };

        struct Input
        {
            float2 uv_BaseMap;
            float4 screenPos;
            float3 worldPos;
            float tvFade;
            // Material blend (used by TRANSVOXEL_PALETTE variants only): the triangle's
            // id triple and this pixel's first two barycentric corner weights.
            float3 tvIds;
            float2 tvWeights;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            float fade = 1.0;
            float duration = abs(v.texcoord1.y);
            if (duration > 0.0)
            {
                float t = saturate((_Time.y - v.texcoord1.x) / duration);
                fade = v.texcoord1.y < 0.0 ? -(1.0 - t) : t;
            }
            o.tvFade = fade;
#if defined(TRANSVOXEL_PALETTE)
            o.tvIds = v.color.rgb * 255.0;
            int corner = (int)round(v.color.a * 255.0);
            o.tvWeights = float2(corner == 0 ? 1.0 : 0.0, corner == 1 ? 1.0 : 0.0);
#endif
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            // Same clip rules as the URP subshader: positive fade dithers in, negative fade
            // is a ghost keeping the complementary pixel set, both capped by the edge dissolve.
            float fade = IN.tvFade * _TransvoxelFade;
            float edge = 1.0;
            if (_TransvoxelEdgeFadeBand > 0.0)
            {
                float viewerDistance = distance(IN.worldPos, _TransvoxelViewerPos.xyz);
                float rawEdge = saturate((_TransvoxelViewDistance - viewerDistance) / _TransvoxelEdgeFadeBand);
                edge = tex2D(_TransvoxelEdgeFadeCurve, float2(rawEdge, 0.5)).r;
            }
            if (fade < 1.0 || edge < 1.0)
            {
                float2 pixel = IN.screenPos.xy / max(IN.screenPos.w, 1e-4) * _ScreenParams.xy;
                uint2 p = (uint2)pixel & 3;
                float threshold = TransvoxelDither[p.y * 4 + p.x];
                if (fade < 0.0)
                    clip(min(threshold - (1.0 + fade), edge - threshold));
                else
                    clip(min(fade, edge) - threshold);
            }

#if defined(TRANSVOXEL_PALETTE)
            // Blend the triangle's palette layers with sharpened barycentric weights —
            // the same math as the URP subshader's TransvoxelPaletteBlend.
            int maxLayer = max((int)_TransvoxelPaletteLayerCount - 1, 0);
            int3 ids = clamp((int3)round(IN.tvIds), 0, maxLayer);
            float3 w = float3(IN.tvWeights, saturate(1.0 - IN.tvWeights.x - IN.tvWeights.y));
            w = pow(max(w, 0.0), _TransvoxelBlendSharpness);
            w /= max(w.x + w.y + w.z, 1e-5);

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
