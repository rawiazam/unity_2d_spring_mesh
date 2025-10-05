HLSLINCLUDE
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
ENDHLSL

// File: QuadDensity.shader
Shader "Custom/QuadDensity"
{
    Properties
    {
        _Max ("Exposure Max", Float) = 64.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            // You can switch to Opaque + ZWrite On if you want it to occlude.
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5                   // for Texture2D<uint> in frag
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Declare as uint texture (RenderTextureFormat.RInt)
            Texture2D<uint> _DensityU32;
            float4 _DensityU32_TexelSize;        // x=1/w y=1/h z=w w=h (Unity auto set)
            float _Max;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_Position;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex.xyz);
                o.uv  = v.uv;
                return o;
            }

            float3 BlueWhite(float t)
            {
                // dark blue -> white
                return lerp(float3(0.00, 0.0, 0.00), float3(1.0, 1.0, 1.0), saturate(t));
            }

            float4 frag(v2f i) : SV_Target
            {
                // Convert [0,1] UV into integer texel coords based on the texture size
                int2 texel = int2(saturate(i.uv) * _DensityU32_TexelSize.zw);
                uint d = _DensityU32.Load(int3(texel, 0)).r;

                // Smooth exposure curve so low densities still show
                float t = 1.0 - exp(-(float)d / max(_Max, 1e-6));
                return float4(BlueWhite(t), 1.0);
            }
            ENDHLSL
        }
    }
}
