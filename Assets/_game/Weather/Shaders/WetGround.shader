// WetGround.shader - DOTS Instancing compatible
// Apply on a flat plane just above your ground for rain ripple effect.
Shader "WeatherECS/WetGround"
{
    Properties
    {
        _BaseColor    ("Base Color", Color) = (0.15, 0.15, 0.18, 1)
        _Wetness      ("Wetness (0-1)", Range(0,1)) = 0.7
        _RippleSpeed  ("Ripple Speed", Float) = 1.5
        _RippleScale  ("Ripple Scale", Float) = 12.0
        _RippleStrength("Ripple Strength", Range(0,1)) = 0.6
        _RainAmount   ("Rain Amount (0-1)", Range(0,1)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles gles3 glcore

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float  _Wetness;
                float  _RippleSpeed;
                float  _RippleScale;
                float  _RippleStrength;
                float  _RainAmount;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
                UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
                    UNITY_DOTS_INSTANCED_PROP(float , _Wetness)
                    UNITY_DOTS_INSTANCED_PROP(float , _RippleSpeed)
                    UNITY_DOTS_INSTANCED_PROP(float , _RippleScale)
                    UNITY_DOTS_INSTANCED_PROP(float , _RippleStrength)
                    UNITY_DOTS_INSTANCED_PROP(float , _RainAmount)
                UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

                #define _BaseColor      UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor)
                #define _Wetness        UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Wetness)
                #define _RippleSpeed    UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _RippleSpeed)
                #define _RippleScale    UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _RippleScale)
                #define _RippleStrength UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _RippleStrength)
                #define _RainAmount     UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _RainAmount)
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 worldPos   : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 wp = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.worldPos   = wp;
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionCS = TransformWorldToHClip(wp);
                return OUT;
            }

            float2 Hash22(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                return frac(sin(p) * 43758.5453);
            }

            float Ripple(float2 worldXZ, float time)
            {
                float2 uv = worldXZ * _RippleScale * 0.05;
                float2 cell = floor(uv);
                float2 frac_uv = frac(uv);

                float total = 0.0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 nb = cell + float2(x, y);
                        float2 r = Hash22(nb);
                        float phase = frac(time * _RippleSpeed + r.x);
                        float2 center = float2(x, y) + r * 0.7 + 0.15;
                        float dist = length(frac_uv - center);

                        float ring = sin((dist - phase) * 30.0) * smoothstep(0.4, 0.0, abs(dist - phase));
                        float fade = 1.0 - phase;
                        float active = step(1.0 - _RainAmount * 0.7, r.y);

                        total += ring * fade * active;
                    }
                }
                return total * _RippleStrength;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float t = _Time.y;
                float ripples = Ripple(IN.worldPos.xz, t);

                float3 n = normalize(IN.normalWS + float3(ripples, 0, ripples * 0.5));
                Light mainLight = GetMainLight();
                float3 v = normalize(_WorldSpaceCameraPos - IN.worldPos);
                float3 h = normalize(mainLight.direction + v);
                float spec = pow(saturate(dot(n, h)), 80.0) * _Wetness;

                float3 col = _BaseColor.rgb * (1.0 - _Wetness * 0.5);
                col += spec * mainLight.color;
                col += abs(ripples) * 0.15;

                float alpha = saturate(_Wetness + abs(ripples) * 0.5);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
