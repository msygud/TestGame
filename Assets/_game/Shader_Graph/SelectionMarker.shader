Shader "Custom/URP/SelectionMarker"
{
    Properties
    {
        _Color ("Color", Color) = (0.2, 0.8, 1, 1)
        _RectSize ("Rect Size (X=Width, Y=Height)", Vector) = (1, 1, 0, 0)
        _Radius ("Corner Radius", Float) = 0.15
        _Thickness ("Border Thickness", Float) = 0.05
        _Softness ("Edge Softness", Float) = 0.01
        _ScaleX ("Scale X", Float) = 1.0
        _ScaleZ ("Scale Z", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "SelectionMarker"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float2 _RectSize;
                float  _Radius;
                float  _Thickness;
                float  _Softness;
                float  _ScaleX;
                float  _ScaleZ;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ── 핵심 SDF 함수 ──────────────────────────────────────
            // 둥근 사각형까지의 부호 있는 거리 반환
            float sdRoundedBox(float2 p, float2 halfSize, float radius)
            {
                float2 q = abs(p) - halfSize + radius;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
            }
            // ───────────────────────────────────────────────────────

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // UV를 [-0.5, 0.5] 중심 기반으로 변환
                OUT.uv = IN.uv - 0.5;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // ── 핵심: UV를 실제 스케일 비율로 보정 ──────────────
                float2 aspectUV = IN.uv * float2(_ScaleX, _ScaleZ);
                float2 halfSize = float2(_ScaleX, _ScaleZ) * 0.5;
                // ────────────────────────────────────────────────────

                // Radius, Thickness는 월드 공간 고정값
                float radius = clamp(_Radius, 0.001, min(halfSize.x, halfSize.y));

                float outerDist = sdRoundedBox(aspectUV, halfSize, radius);
                float innerDist = sdRoundedBox(aspectUV, halfSize - _Thickness, radius - _Thickness);

                float outerAlpha = 1.0 - smoothstep(-_Softness, _Softness, outerDist);
                float innerAlpha = 1.0 - smoothstep(-_Softness, _Softness, innerDist);

                float borderAlpha = outerAlpha * (1.0 - innerAlpha);

                half4 col = _Color;
                col.a *= borderAlpha;
                return col;
            }
            ENDHLSL
        }
    }
}
