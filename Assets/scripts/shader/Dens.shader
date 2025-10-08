Shader "Test/Density"
{
    Properties
    {
        _BaseMap    ("Base (RGB) Alpha (A)", 2D) = "white" {}
        _BaseColor  ("Tint", Color) = (1,1,1,1)

        _DensityTex ("Density (A used)", 2D) = "white" {}
    }

    SubShader
    {
        Tags {
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Transparent"
            "RenderType"="Transparent"
        }

        ZWrite Off
        Cull Back
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            // ---------- Material properties ----------
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
            CBUFFER_END

            // ---------- Textures / samplers ----------
            TEXTURE2D(_BaseMap);     SAMPLER(sampler_BaseMap);
            TEXTURE2D(_DensityTex);  SAMPLER(sampler_DensityTex);

            // ---------- Vertex/fragment I/O ----------
            struct Attributes {
                float4 positionOS : POSITION; // object-space vertex position
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;    // optional vertex color
            };

            struct Varyings {
                float4 positionCS : SV_POSITION; // clip-space position A must, somehow??
                float2 uv         : TEXCOORD0;   // mesh UVs (for BaseMap)
                float4 color      : COLOR;
                float4 test       : TEXCOORD3;
            };

            // ---------- Vertex shader ----------
            Varyings vert (Attributes v)
            {
                VertexPositionInputs positions = GetVertexPositionInputs(v.positionOS);
                Varyings o;
                o.positionCS = TransformObjectToHClip(positions.positionWS);
                o.uv         = v.uv;
                o.color      = v.color * _BaseColor; // apply tint early
                o.test = positions.positionNDC;
                return o;
            }

            // ---------- Fragment shader ----------
            float4 frag (Varyings i) : SV_Target
            {
                float4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _BaseColor;

                // Screen-space UV that matches URP’s convention
                float2 suv = i.test.xy / i.test.w; // 0..1

                float aMask = SAMPLE_TEXTURE2D(_DensityTex, sampler_DensityTex, suv).a;
                col.a *= aMask;

                return saturate(col);
            }
            ENDHLSL
        }
    }
}

