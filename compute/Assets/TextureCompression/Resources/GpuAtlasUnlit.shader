Shader "UnityCompute/GpuAtlasUnlit"
{
    Properties
    {
        _AtlasTex ("Atlas", 2D) = "white" {}
        [PerRendererData] _AtlasImageId ("Image ID", Integer) = 0
        [PerRendererData] _AtlasTint ("Tint", Color) = (1,1,1,1)
        [PerRendererData] _AtlasSourceBorder ("Source borders", Vector) = (.333333,.333333,.333333,.333333)
        [PerRendererData] _AtlasDestinationBorder ("Destination borders", Vector) = (.333333,.333333,.333333,.333333)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Stencil { Ref 128 Comp Always Pass Replace }
        Pass
        {
            Name "2D"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex AtlasVert
            #pragma fragment AtlasFrag
            #pragma multi_compile_instancing
            #include "GpuAtlasRender.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForwardOnly" }
            Stencil { Comp Always Pass Keep WriteMask 0 }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex AtlasVert
            #pragma fragment AtlasFrag
            #pragma multi_compile_instancing
            #include "GpuAtlasRender.hlsl"
            ENDHLSL
        }
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            Name "BuiltIn"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex AtlasVert
            #pragma fragment AtlasFrag
            #pragma multi_compile_instancing
            #include "GpuAtlasRender.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
