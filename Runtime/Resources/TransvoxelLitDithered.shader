// Lit terrain shader with screen-space stipple (Bayer dither) fading — the same visual
// idea as Unity's LOD Group cross-fade. Two fade sources multiply into one clip test:
//
//   * _TransvoxelFade (per chunk, via MaterialPropertyBlock): 0→1 ramp while a freshly
//     built chunk dithers in.
//   * The global viewer uniforms (_TransvoxelViewerPos / _TransvoxelViewDistance /
//     _TransvoxelEdgeFadeBand, set by TransvoxelTerrain every frame): a per-PIXEL fade
//     toward the draw-distance edge, so distant terrain dissolves smoothly like fog
//     instead of popping — even when one far chunk spans kilometers.
//
// Because the dither pattern is fixed in screen space, overlapping old/new chunks at the
// same fade value cut identical pixels, so LOD swaps never open holes mid-fade.
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
        _TransvoxelFade("Fade", Range(0, 1)) = 1
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
        float _TransvoxelFade;
        CBUFFER_END

        // Globals driven by TransvoxelTerrain (band 0 = edge fade off).
        float4 _TransvoxelViewerPos;
        float _TransvoxelViewDistance;
        float _TransvoxelEdgeFadeBand;

        // 4x4 Bayer matrix, thresholds centered so fade 1 keeps every pixel.
        static const float TransvoxelDither[16] =
        {
             0.5 / 16.0,  8.5 / 16.0,  2.5 / 16.0, 10.5 / 16.0,
            12.5 / 16.0,  4.5 / 16.0, 14.5 / 16.0,  6.5 / 16.0,
             3.5 / 16.0, 11.5 / 16.0,  1.5 / 16.0,  9.5 / 16.0,
            15.5 / 16.0,  7.5 / 16.0, 13.5 / 16.0,  5.5 / 16.0
        };

        // _TransvoxelFade in [0,1] fades a chunk in: keep pixels with threshold <= fade.
        // NEGATIVE fade marks a ghost — an old mesh cross-fading out while its successor
        // (driven with the complementary value) fades in: the ghost keeps exactly the
        // pixels the successor does not draw yet, so the pair always covers the surface
        // with no holes and no double-drawn pixels. Both windows are additionally capped
        // by the per-pixel draw-distance dissolve.
        void TransvoxelDitherClip(float4 positionCS, float3 positionWS)
        {
            float fade = _TransvoxelFade;
            float edge = 1.0;
            if (_TransvoxelEdgeFadeBand > 0.0)
            {
                float viewerDistance = distance(positionWS, _TransvoxelViewerPos.xyz);
                edge = saturate((_TransvoxelViewDistance - viewerDistance) / _TransvoxelEdgeFadeBand);
            }
            if (fade >= 1.0 && edge >= 1.0)
                return;

            uint2 pixel = uint2(positionCS.xy) & 3;
            float threshold = TransvoxelDither[pixel.y * 4 + pixel.x];

            if (fade < 0.0)
            {
                // Ghost visibility g = -fade (1 -> 0): keep thresholds in [1 - g, edge].
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                half fogFactor : TEXCOORD3;
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
                return output;
            }

            half4 LitPassFragment(Varyings input) : SV_Target
            {
                TransvoxelDitherClip(input.positionCS, input.positionWS);

                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
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
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
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
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                TransvoxelDitherClip(input.positionCS, input.positionWS);
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
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_Target
            {
                TransvoxelDitherClip(input.positionCS, input.positionWS);
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
        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma target 3.5

        sampler2D _BaseMap;
        fixed4 _BaseColor;
        half _Smoothness;
        float _TransvoxelFade;

        float4 _TransvoxelViewerPos;
        float _TransvoxelViewDistance;
        float _TransvoxelEdgeFadeBand;

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
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            // Same clip rules as the URP subshader: positive fade dithers in, negative fade
            // is a ghost keeping the complementary pixel set, both capped by the edge dissolve.
            float fade = _TransvoxelFade;
            float edge = 1.0;
            if (_TransvoxelEdgeFadeBand > 0.0)
            {
                float viewerDistance = distance(IN.worldPos, _TransvoxelViewerPos.xyz);
                edge = saturate((_TransvoxelViewDistance - viewerDistance) / _TransvoxelEdgeFadeBand);
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

            fixed4 albedo = tex2D(_BaseMap, IN.uv_BaseMap) * _BaseColor;
            o.Albedo = albedo.rgb;
            o.Smoothness = _Smoothness;
            o.Alpha = 1;
        }
        ENDCG
    }

    FallBack "Diffuse"
}
