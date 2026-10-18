#ifndef UNITY_COMPUTE_COMPRESSED_ATLAS_SAMPLING
#define UNITY_COMPUTE_COMPRESSED_ATLAS_SAMPLING
// Use the entry's UvScaleOffset and UvClamp. This atlas has one mip.
float2 CompressedAtlasUv(float2 localUv, float4 scaleOffset, float4 texelClamp)
{
    return clamp(localUv * scaleOffset.xy + scaleOffset.zw, texelClamp.xy, texelClamp.zw);
}
#endif
