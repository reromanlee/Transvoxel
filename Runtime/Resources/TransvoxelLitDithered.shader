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
// URP only. The package depends on URP and this shader's single SubShader declares that
// requirement, so it simply does not compile elsewhere; the terrain falls back to a plain
// lit material (no fading, no voxel materials) when the active pipeline is not URP.

Shader "Transvoxel/Lit Dithered"
{
    Properties
    {
        _BaseColor("Color", Color) = (0.42, 0.55, 0.3, 1)
        _BaseMap("Albedo", 2D) = "white" {}
        _Smoothness("Smoothness", Range(0, 1)) = 0.1
        // Debug LOD tint, multiplied into the FINAL albedo of every variant. White = off.
        // The terrain drives it through per-LOD shared material variants rather than a
        // MaterialPropertyBlock, so tinted chunks keep SRP batching and the tint survives
        // batched render paths. It is a real per-material property for exactly that reason.
        [HideInInspector] _TransvoxelLodTint("LOD Tint (debug)", Color) = (1, 1, 1, 1)
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

        // Keywords as bool literals, so the shared projection function can take them as
        // ordinary arguments and the compiler still folds away the unused branch.
        #if defined(TRANSVOXEL_TRIPLANAR)
            #define TRANSVOXEL_TRIPLANAR_ON true
        #else
            #define TRANSVOXEL_TRIPLANAR_ON false
        #endif
        #if defined(TRANSVOXEL_PARALLAX)
            #define TRANSVOXEL_PARALLAX_ON true
        #else
            #define TRANSVOXEL_PARALLAX_ON false
        #endif

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST;
        half4 _BaseColor;
        half _Smoothness;
        half4 _TransvoxelLodTint;
        CBUFFER_END

        // The palette blend (globals, layer sampling, the detail-map surface and the
        // derivative-based normal mapping) lives in the palette module — the same file a
        // Shader Graph or custom URP shader includes to render voxel materials. The
        // keyword variants above only pick which of its paths THIS shader calls.
        #include "TransvoxelPalette.hlsl"

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
            // Both are palette-only and opt-in per palette asset, so a terrain that uses
            // neither compiles and costs exactly what it did before they existed.
            #pragma multi_compile _ TRANSVOXEL_TRIPLANAR
            #pragma multi_compile _ TRANSVOXEL_PARALLAX

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
                // The projected path covers plain, triplanar, parallax and both at once —
                // the keywords fold into compile-time constants, so each variant keeps only
                // the code it needs, and it resolves the world normal itself.
                TransvoxelProjectedSurface surface = TransvoxelPaletteBlendProjected(
                    input.uv, input.positionWS, normalWS, input.materialIds,
                    input.fadeAndWeights.yz, TRANSVOXEL_TRIPLANAR_ON, TRANSVOXEL_PARALLAX_ON);
                half3 albedo = surface.albedo;
                normalWS = surface.normalWS;
                occlusion = surface.occlusion;
#elif defined(TRANSVOXEL_PALETTE)
                // Albedo-only palettes have no heightfield to march, so only triplanar
                // applies here.
    #if defined(TRANSVOXEL_TRIPLANAR)
                half3 albedo = TransvoxelPaletteBlendTriplanar(input.uv, input.positionWS,
                                                               normalWS, input.materialIds,
                                                               input.fadeAndWeights.yz).rgb;
    #else
                half3 albedo = TransvoxelPaletteBlend(input.uv, input.materialIds,
                                                      input.fadeAndWeights.yz).rgb;
    #endif
#else
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
#endif
                // Debug LOD tint last, so it applies to the palette variants too — they
                // build albedo entirely from the palette and never read _BaseColor.
                albedo *= _TransvoxelLodTint.rgb;

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

    FallBack "Universal Render Pipeline/Lit"
}
