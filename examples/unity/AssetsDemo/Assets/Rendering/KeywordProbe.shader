Shader "AI Native/Keyword Probe"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode"="SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ PROBE_GREEN
            #pragma multi_compile_local _ PROBE_EXCLUDED
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 Vert(float3 position : POSITION) : SV_POSITION { return TransformObjectToHClip(position); }
            half4 Frag() : SV_Target
            {
                #if defined(PROBE_GREEN)
                return half4(0,1,0,1);
                #else
                return half4(1,0,0,1);
                #endif
            }
            ENDHLSL
        }
    }
}
