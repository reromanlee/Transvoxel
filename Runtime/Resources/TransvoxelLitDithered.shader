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
        // Marker only: TransvoxelTerrain detects fade-aware materials via HasProperty.
        // The master fade itself (_TransvoxelFade) is a GLOBAL uniform — deliberately not
        // a serialized property, so the SRP Batcher can never lock it to a material value.
        [HideInInspector] _TransvoxelFadeAware("Fade Aware", Float) = 1
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 fadeData : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                float fade : TEXCOORD4;
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
                output.fade = TransvoxelVertexFade(input.fadeData);
                return output;
            }

            half4 LitPassFragment(Varyings input) : SV_Target
            {
                TransvoxelDitherClip(input.positionCS, input.positionWS, input.fade);

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

            fixed4 albedo = tex2D(_BaseMap, IN.uv_BaseMap) * _BaseColor;
            o.Albedo = albedo.rgb;
            o.Smoothness = _Smoothness;
            o.Alpha = 1;
        }
        ENDCG
    }

    FallBack "Diffuse"
}
