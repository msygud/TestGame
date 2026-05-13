// SnowGround.shader - DOTS Instancing compatible
// Use on a tessellated plane (subdivided ~100+) that sits over your terrain.
Shader "WeatherECS/SnowGround"
{
    Properties
    {
        _SnowColor      ("Snow Color", Color) = (0.95, 0.96, 1.0, 1)
        _GroundColor    ("Ground Color", Color) = (0.25, 0.18, 0.10, 1)
        _MaxDisplacement("Max Displacement (m)", Float) = 0.5
        _SnowSparkle    ("Snow Sparkle Intensity", Range(0,1)) = 0.25
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            // REQUIRED for DOTS_INSTANCING_ON support
            #pragma target 4.5
            #pragma exclude_renderers gles gles3 glcore

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Globals (set by SnowAccumulationSystem)
            TEXTURE2D(_GlobalSnowHeightMap); SAMPLER(sampler_GlobalSnowHeightMap);
            float4 _GlobalSnowOriginXZ;
            float  _GlobalSnowWorldSize;
            float  _GlobalSnowMaxHeight;

            CBUFFER_START(UnityPerMaterial)
                float4 _SnowColor;
                float4 _GroundColor;
                float  _MaxDisplacement;
                float  _SnowSparkle;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
                UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                    UNITY_DOTS_INSTANCED_PROP(float4, _SnowColor)
                    UNITY_DOTS_INSTANCED_PROP(float4, _GroundColor)
                    UNITY_DOTS_INSTANCED_PROP(float , _MaxDisplacement)
                    UNITY_DOTS_INSTANCED_PROP(float , _SnowSparkle)
                UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

                #define _SnowColor       UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _SnowColor)
                #define _GroundColor     UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _GroundColor)
                #define _MaxDisplacement UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _MaxDisplacement)
                #define _SnowSparkle     UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _SnowSparkle)
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 worldPos   : TEXCOORD0;
                float  snowH      : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float SampleHeight(float2 worldXZ)
            {
                float2 uv = (worldXZ - _GlobalSnowOriginXZ.xy) / _GlobalSnowWorldSize;
                if (any(uv < 0) || any(uv > 1)) return _GlobalSnowMaxHeight;
                return SAMPLE_TEXTURE2D_LOD(_GlobalSnowHeightMap, sampler_GlobalSnowHeightMap, uv, 0).r;
            }

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                float h = SampleHeight(worldPos.xz);
                worldPos.y += h * _MaxDisplacement;

                float texel = _GlobalSnowWorldSize / 512.0;
                float hL = SampleHeight(worldPos.xz + float2(-texel, 0));
                float hR = SampleHeight(worldPos.xz + float2( texel, 0));
                float hD = SampleHeight(worldPos.xz + float2(0, -texel));
                float hU = SampleHeight(worldPos.xz + float2(0,  texel));
                float3 n = normalize(float3((hL - hR) * _MaxDisplacement,
                                            2.0 * texel,
                                            (hD - hU) * _MaxDisplacement));

                OUT.worldPos   = worldPos;
                OUT.snowH      = h;
                OUT.normalWS   = n;
                OUT.positionCS = TransformWorldToHClip(worldPos);
                return OUT;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                Light mainLight = GetMainLight();
                float ndl = saturate(dot(IN.normalWS, mainLight.direction));

                float sparkle = step(0.985, Hash(floor(IN.worldPos.xz * 80))) * _SnowSparkle;

                float3 snow   = _SnowColor.rgb + sparkle;
                float3 ground = _GroundColor.rgb;
                float t = saturate(IN.snowH / max(0.001, _GlobalSnowMaxHeight));
                float3 albedo = lerp(ground, snow, t);

                float3 color = albedo * (mainLight.color * ndl + 0.25);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // ---- Shadow caster pass ----
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles gles3 glcore

            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_GlobalSnowHeightMap); SAMPLER(sampler_GlobalSnowHeightMap);
            float4 _GlobalSnowOriginXZ;
            float  _GlobalSnowWorldSize;
            float  _GlobalSnowMaxHeight;

            CBUFFER_START(UnityPerMaterial)
                float4 _SnowColor;
                float4 _GroundColor;
                float  _MaxDisplacement;
                float  _SnowSparkle;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
                UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                    UNITY_DOTS_INSTANCED_PROP(float , _MaxDisplacement)
                UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
                #define _MaxDisplacement UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _MaxDisplacement)
            #endif

            struct AttributesS { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct VaryingsS   { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            float SampleHeightS(float2 worldXZ)
            {
                float2 uv = (worldXZ - _GlobalSnowOriginXZ.xy) / _GlobalSnowWorldSize;
                if (any(uv < 0) || any(uv > 1)) return _GlobalSnowMaxHeight;
                return SAMPLE_TEXTURE2D_LOD(_GlobalSnowHeightMap, sampler_GlobalSnowHeightMap, uv, 0).r;
            }

            VaryingsS vertShadow(AttributesS IN)
            {
                VaryingsS OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                worldPos.y += SampleHeightS(worldPos.xz) * _MaxDisplacement;
                OUT.positionCS = TransformWorldToHClip(worldPos);
                return OUT;
            }

            half4 fragShadow(VaryingsS IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                return 0;
            }
            ENDHLSL
        }

        // ---- Depth-only pass ----
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles gles3 glcore

            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_GlobalSnowHeightMap); SAMPLER(sampler_GlobalSnowHeightMap);
            float4 _GlobalSnowOriginXZ;
            float  _GlobalSnowWorldSize;
            float  _GlobalSnowMaxHeight;

            CBUFFER_START(UnityPerMaterial)
                float4 _SnowColor;
                float4 _GroundColor;
                float  _MaxDisplacement;
                float  _SnowSparkle;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
                UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                    UNITY_DOTS_INSTANCED_PROP(float , _MaxDisplacement)
                UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
                #define _MaxDisplacement UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _MaxDisplacement)
            #endif

            struct AttributesD { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct VaryingsD   { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            float SampleHeightD(float2 worldXZ)
            {
                float2 uv = (worldXZ - _GlobalSnowOriginXZ.xy) / _GlobalSnowWorldSize;
                if (any(uv < 0) || any(uv > 1)) return _GlobalSnowMaxHeight;
                return SAMPLE_TEXTURE2D_LOD(_GlobalSnowHeightMap, sampler_GlobalSnowHeightMap, uv, 0).r;
            }

            VaryingsD vertDepth(AttributesD IN)
            {
                VaryingsD OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                worldPos.y += SampleHeightD(worldPos.xz) * _MaxDisplacement;
                OUT.positionCS = TransformWorldToHClip(worldPos);
                return OUT;
            }

            half4 fragDepth(VaryingsD IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                return 0;
            }
            ENDHLSL
        }
    }
}
