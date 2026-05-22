Shader "Custom/URP_WaterShader"
{
    Properties
    {
        [Header(Surface Color)]
        _ShallowColor ("Shallow Color", Color) = (0.1, 0.6, 0.8, 0.6)
        _DeepColor ("Deep Color", Color) = (0.02, 0.15, 0.35, 0.95)
        _DepthMaxDistance ("Depth Max Distance", Float) = 3.0

        [Header(Waves)]
        _WaveSpeed ("Wave Speed", Float) = 1.0
        _WaveAmplitude ("Wave Amplitude", Float) = 0.2
        _WaveFrequency ("Wave Frequency", Float) = 1.0
        _WaveDirection1 ("Wave Direction 1 (XY)", Vector) = (1, 0.5, 0, 0)
        _WaveDirection2 ("Wave Direction 2 (XY)", Vector) = (-0.5, 1, 0, 0)
        _WaveDirection3 ("Wave Direction 3 (XY)", Vector) = (0.8, -0.6, 0, 0)

        [Header(Normal Map)]
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0, 3)) = 0.8
        _NormalScrollSpeed ("Normal Scroll Speed", Vector) = (0.05, 0.03, -0.04, 0.02)
        _NormalTiling ("Normal Tiling", Float) = 2.0
        _NormalWorldSize ("Normal World Size", Float) = 10.0

        [Header(Foam)]
        _FoamColor ("Foam Color", Color) = (1, 1, 1, 1)
        _FoamDistance ("Foam Distance", Float) = 0.5
        _FoamFalloff ("Foam Falloff", Float) = 2.0
        _FoamNoise ("Foam Noise Texture", 2D) = "white" {}
        _FoamNoiseScale ("Foam Noise Scale", Float) = 3.0
        _FoamNoiseSpeed ("Foam Noise Speed", Float) = 0.5
        _FoamIntensity ("Foam Intensity", Range(0, 2)) = 1.0

        [Header(Specular and Reflection)]
        _Smoothness ("Smoothness", Range(0, 1)) = 0.9
        _SpecularIntensity ("Specular Intensity", Range(0, 2)) = 1.0
        _ReflectionStrength ("Reflection Strength", Range(0, 1)) = 0.5
        _FresnelPower ("Fresnel Power", Range(0.1, 10)) = 3.0

        [Header(Refraction)]
        _RefractionStrength ("Refraction Strength", Range(0, 0.2)) = 0.05

        [Header(Transparency)]
        _Opacity ("Opacity", Range(0, 1)) = 0.85

        [Header(Caustics)]
        _CausticsTex ("Caustics Texture", 2D) = "white" {}
        _CausticsStrength ("Caustics Strength", Range(0, 1)) = 0.3
        _CausticsScale ("Caustics Scale", Float) = 5.0
        _CausticsSpeed ("Caustics Speed", Float) = 0.5

        [HideInInspector] _Mode ("__mode", Float) = 3.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles gles3 glcore

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            // ─── Properties ───────────────────────────────────────────────
            CBUFFER_START(UnityPerMaterial)
                // Surface Color
                half4 _ShallowColor;
                half4 _DeepColor;
                float _DepthMaxDistance;

                // Waves
                float _WaveSpeed;
                float _WaveAmplitude;
                float _WaveFrequency;
                float4 _WaveDirection1;
                float4 _WaveDirection2;
                float4 _WaveDirection3;

                // Normal
                float4 _NormalMap_ST;
                float _NormalStrength;
                float4 _NormalScrollSpeed;
                float _NormalTiling;
                float _NormalWorldSize;

                // Foam
                half4 _FoamColor;
                float _FoamDistance;
                float _FoamFalloff;
                float4 _FoamNoise_ST;
                float _FoamNoiseScale;
                float _FoamNoiseSpeed;
                float _FoamIntensity;

                // Specular
                float _Smoothness;
                float _SpecularIntensity;
                float _ReflectionStrength;
                float _FresnelPower;

                // Refraction
                float _RefractionStrength;

                // Opacity
                float _Opacity;

                // Caustics
                float4 _CausticsTex_ST;
                float _CausticsStrength;
                float _CausticsScale;
                float _CausticsSpeed;
            CBUFFER_END

            TEXTURE2D(_NormalMap);   SAMPLER(sampler_NormalMap);
            TEXTURE2D(_FoamNoise);   SAMPLER(sampler_FoamNoise);
            TEXTURE2D(_CausticsTex); SAMPLER(sampler_CausticsTex);

            // ─── Structs ───────────────────────────────────────────────────
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS     : SV_POSITION;
                float3 positionWS     : TEXCOORD0;
                float3 normalWS       : TEXCOORD1;
                float3 tangentWS      : TEXCOORD2;
                float3 bitangentWS    : TEXCOORD3;
                float4 screenPos      : TEXCOORD4;
                float2 uv             : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // ─── Gerstner Wave ─────────────────────────────────────────────
            struct GerstnerWaveResult
            {
                float3 offset;
                float3 normal;
            };

            GerstnerWaveResult GerstnerWave(float2 dir, float steepness, float waveLen,
                                             float3 p, float t)
            {
                GerstnerWaveResult r;
                dir = normalize(dir);
                float k  = TWO_PI / waveLen;
                float c  = sqrt(9.8 / k);
                float f  = k * (dot(dir, p.xz) - c * t);
                float a  = steepness / k;

                r.offset.x = dir.x * a * cos(f);
                r.offset.y = a * sin(f);
                r.offset.z = dir.y * a * cos(f);

                r.normal.x = -dir.x * k * a * cos(f);
                r.normal.y = steepness * sin(f);
                r.normal.z = -dir.y * k * a * cos(f);
                return r;
            }

            // ─── Vertex ────────────────────────────────────────────────────
            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float t = _Time.y * _WaveSpeed;

                float3 posOS = IN.positionOS.xyz;
                float3 baseWS = TransformObjectToWorld(posOS);

                // 3-layer Gerstner waves
                GerstnerWaveResult w1 = GerstnerWave(_WaveDirection1.xy, _WaveAmplitude,
                                                      6.28 / _WaveFrequency, baseWS, t);
                GerstnerWaveResult w2 = GerstnerWave(_WaveDirection2.xy, _WaveAmplitude * 0.6,
                                                      6.28 / (_WaveFrequency * 1.5), baseWS, t * 1.3);
                GerstnerWaveResult w3 = GerstnerWave(_WaveDirection3.xy, _WaveAmplitude * 0.4,
                                                      6.28 / (_WaveFrequency * 2.1), baseWS, t * 0.9);

                float3 displacedWS = baseWS + w1.offset + w2.offset + w3.offset;

                float3 waveNormal = float3(0,1,0) - (w1.normal + w2.normal + w3.normal);
                waveNormal = normalize(waveNormal);

                float3 tangentWS = normalize(TransformObjectToWorldDir(IN.tangentOS.xyz));
                float3 bitangentWS = normalize(cross(waveNormal, tangentWS) * IN.tangentOS.w);

                OUT.positionCS  = TransformWorldToHClip(displacedWS);
                OUT.positionWS  = displacedWS;
                OUT.normalWS    = waveNormal;
                OUT.tangentWS   = tangentWS;
                OUT.bitangentWS = bitangentWS;
                OUT.screenPos   = ComputeScreenPos(OUT.positionCS);
                OUT.uv          = IN.uv;
                return OUT;
            }

            // ─── Fragment ──────────────────────────────────────────────────
            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                float t = _Time.y;

                // ── Depth ──────────────────────────────────────────────────
                float sceneRawDepth  = SampleSceneDepth(screenUV);
                float sceneLinear    = LinearEyeDepth(sceneRawDepth, _ZBufferParams);
                float surfaceLinear  = IN.screenPos.w;
                float depthDiff      = sceneLinear - surfaceLinear;
                float depthNorm      = saturate(depthDiff / _DepthMaxDistance);

                // ── Normal map ─────────────────────────────────────────────
                float2 uvBase = IN.positionWS.xz * (_NormalTiling / max(_NormalWorldSize, 0.0001));
                float2 uv1 = uvBase + _NormalScrollSpeed.xy * t;
                float2 uv2 = uvBase * 0.7 + _NormalScrollSpeed.zw * t;

                half3 n1 = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv1));
                half3 n2 = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv2));
                half3 blendedNormal = normalize(half3(n1.xy + n2.xy, n1.z));
                blendedNormal.xy *= _NormalStrength;

                float3x3 TBN = float3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS);
                float3 worldNormal = normalize(mul(blendedNormal, TBN));

                // ── Refraction ─────────────────────────────────────────────
                float2 refrUV = screenUV + blendedNormal.xy * _RefractionStrength * (1 - depthNorm);
                half3 sceneColor = SampleSceneColor(refrUV);

                // ── Water color ────────────────────────────────────────────
                half4 waterColor = lerp(_ShallowColor, _DeepColor, depthNorm);

                // ── Caustics ───────────────────────────────────────────────
                float2 causticsUV1 = IN.positionWS.xz / _CausticsScale + float2( t, t) * _CausticsSpeed * 0.1;
                float2 causticsUV2 = IN.positionWS.xz / _CausticsScale + float2(-t, t) * _CausticsSpeed * 0.1;
                half3 caus1 = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, causticsUV1).rgb;
                half3 caus2 = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, causticsUV2).rgb;
                half3 caustics = min(caus1, caus2) * _CausticsStrength * (1 - depthNorm);
                waterColor.rgb += caustics;

                // ── Fresnel ────────────────────────────────────────────────
                float3 viewDir = normalize(GetWorldSpaceViewDir(IN.positionWS));
                float fresnel  = pow(1.0 - saturate(dot(worldNormal, viewDir)), _FresnelPower);

                // ── Lighting (main light) ──────────────────────────────────
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight    = GetMainLight(shadowCoord);

                half3 diffuse = LightingLambert(mainLight.color, mainLight.direction, worldNormal)
                                * mainLight.shadowAttenuation;

                float3 halfDir  = normalize(mainLight.direction + viewDir);
                float  specNdH  = max(0, dot(worldNormal, halfDir));
                float  specPow  = exp2(_Smoothness * 10.0 + 1.0);
                half3  specular = pow(specNdH, specPow) * mainLight.color * _SpecularIntensity
                                  * mainLight.shadowAttenuation;

                // ── Combine surface ────────────────────────────────────────
                half3 finalColor = lerp(sceneColor, waterColor.rgb, waterColor.a);
                finalColor = finalColor * (diffuse + 0.2) + specular;

                // ── Reflection (env) ───────────────────────────────────────
                float3 reflDir  = reflect(-viewDir, worldNormal);
                half4  reflCol  = SAMPLE_TEXTURECUBE(unity_SpecCube0, samplerunity_SpecCube0, reflDir);
                finalColor = lerp(finalColor, reflCol.rgb, fresnel * _ReflectionStrength);

                // ── Shore Foam ─────────────────────────────────────────────
                // max(0, depthDiff) 로 음수 제거 → pow(음수, 소수지수) = NaN 방지
                float foamDepthRatio = max(0.0, depthDiff) / max(_FoamDistance, 0.0001);
                float foamMask = saturate(1.0 - pow(foamDepthRatio, _FoamFalloff));

                float2 foamUV  = IN.positionWS.xz / _FoamNoiseScale
                                 + float2(t * _FoamNoiseSpeed, t * _FoamNoiseSpeed * 0.7);
                half   foamNoise = SAMPLE_TEXTURE2D(_FoamNoise, sampler_FoamNoise, foamUV).r;
                // Animated edge shimmer
                foamNoise = saturate(foamNoise + sin(t * 3 + IN.positionWS.x * 5 + IN.positionWS.z * 3) * 0.1);
                float  foam = foamMask * foamNoise * _FoamIntensity;

                finalColor = lerp(finalColor, _FoamColor.rgb, foam);

                // ── Alpha ──────────────────────────────────────────────────
                float alpha = lerp(_ShallowColor.a, _Opacity, depthNorm);
                alpha = max(alpha, foam);

                return half4(finalColor, alpha);
            }
            ENDHLSL
        }

        // Shadow Caster (투명하므로 그림자는 생략 – 필요시 아래 주석 해제)
        // UsePass "Universal Render Pipeline/Lit/ShadowCaster"
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"

    CustomEditor "WaterShaderGUI"
}
