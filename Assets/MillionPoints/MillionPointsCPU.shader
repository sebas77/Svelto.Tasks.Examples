Shader "Custom/MillionPointsCPU"
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

			// Procedural instancing driven by Graphics.DrawMeshInstancedIndirect:
			// the per-instance data comes from the particle buffers, not from an
			// instanced transform buffer.
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
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			CBUFFER_START(UnityPerMaterial)
				half4 _Color;
				half _Smoothness;
				half _Metallic;
			CBUFFER_END

			float3 _CubeMeshScale;

			#if defined(ENABLE_INSTANCING)

			// SoA layout: per-frame positions (dynamic) and never-changing albedo
			// (static, uploaded once) live in two separate buffers.
			StructuredBuffer<float3> _ParticleDataBuffer;
			StructuredBuffer<float3> _AlbedoBuffer;

			#endif

			void setup()
			{
				//unity_ObjectToWorld = _LocalToWorld;
				//unity_WorldToObject = _WorldToLocal;
			}

			Varyings vert(Attributes IN)
			{
				Varyings OUT;

				UNITY_SETUP_INSTANCE_ID(IN);
				UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

				// Apply scale and translation of the particle.
				float3 positionOS = IN.positionOS.xyz;
			#if defined(ENABLE_INSTANCING)
				positionOS = positionOS * _CubeMeshScale.xyz + _ParticleDataBuffer[unity_InstanceID];
				OUT.color = half4(_AlbedoBuffer[unity_InstanceID], 1);
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
