Shader "Custom/MillionPoints"
{
    Properties
    {
        _Color("Color", Color) = (1, 1, 1, 1)
        _Smoothness("Smoothness", Range(0, 1)) = 0
        _Metallic("Metallic", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #if SHADER_TARGET >= 35 && (defined(SHADER_API_D3D11) || defined(SHADER_API_GLES3) || defined(SHADER_API_GLCORE) || defined(SHADER_API_XBOXONE) || defined(SHADER_API_PSSL) || defined(SHADER_API_SWITCH) || defined(SHADER_API_VULKAN) || (defined(SHADER_API_METAL) && defined(UNITY_COMPILER_HLSLCC)))
            #define SUPPORT_STRUCTUREDBUFFER
            #endif

            #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && defined(SUPPORT_STRUCTUREDBUFFER)
            #define ENABLE_INSTANCING
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR0;
            };

            struct ParticleData
            {
                float3 BasePosition;
                float3 Position;
                float3 Albedo;
                float RotationSpeed;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Smoothness;
                half _Metallic;
            CBUFFER_END

            float3 _CubeMeshScale;

            #if defined(ENABLE_INSTANCING)
            StructuredBuffer<ParticleData> _ParticleDataBuffer;
            #endif

            void setup()
            {
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                UNITY_SETUP_INSTANCE_ID(IN);

                float3 positionOS = IN.positionOS.xyz;
            #if defined(ENABLE_INSTANCING)
                ParticleData particle = _ParticleDataBuffer[unity_InstanceID];
                positionOS = positionOS * _CubeMeshScale + particle.Position;
                OUT.color = half4(particle.Albedo, 1);
            #else
                OUT.color = half4(1, 1, 1, 1);
            #endif

                OUT.positionCS = TransformObjectToHClip(positionOS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return IN.color * _Color;
            }

            ENDHLSL
        }
    }
}
