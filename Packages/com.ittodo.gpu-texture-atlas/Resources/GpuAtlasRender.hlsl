#ifndef UNITY_COMPUTE_GPU_ATLAS_RENDER
#define UNITY_COMPUTE_GPU_ATLAS_RENDER
#include "UnityCG.cginc"

struct AtlasRecord
{
    float4 scaleOffset;
    float4 clamp;
    uint id, occupied, reserved0, reserved1;
};
StructuredBuffer<AtlasRecord> _AtlasEntries;
uint _AtlasHashMask;
sampler2D _AtlasTex;
UNITY_INSTANCING_BUFFER_START(AtlasInstance)
    UNITY_DEFINE_INSTANCED_PROP(int, _AtlasImageId)
    UNITY_DEFINE_INSTANCED_PROP(float4, _AtlasTint)
    UNITY_DEFINE_INSTANCED_PROP(float4, _AtlasSourceBorder)
    UNITY_DEFINE_INSTANCED_PROP(float4, _AtlasDestinationBorder)
UNITY_INSTANCING_BUFFER_END(AtlasInstance)

uint AtlasHash(uint value)
{
    value ^= value >> 16; value *= 0x7feb352d;
    value ^= value >> 15; value *= 0x846ca68b; return value ^ (value >> 16);
}
bool AtlasLookup(uint id, out AtlasRecord record)
{
    uint slot = AtlasHash(id) & _AtlasHashMask;
    [loop] for (uint probe = 0; probe <= _AtlasHashMask; probe++)
    {
        record = _AtlasEntries[slot];
        if (record.occupied == 0) return false;
        if (record.id == id) return true;
        slot = (slot + 1) & _AtlasHashMask;
    }
    record = (AtlasRecord)0; return false;
}
struct AtlasVertex
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};
struct AtlasVaryings
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    nointerpolation float4 uvClamp : TEXCOORD1;
    nointerpolation float valid : TEXCOORD2;
    float4 tint : COLOR0;
    UNITY_VERTEX_OUTPUT_STEREO
};
float2 AtlasGridCoordinate(float2 grid, float4 border)
{
    return float2(grid.x < .166667 ? 0 : grid.x < .5 ? border.x : grid.x < .833333 ? 1 - border.z : 1,
                  grid.y < .166667 ? 0 : grid.y < .5 ? border.y : grid.y < .833333 ? 1 - border.w : 1);
}
AtlasVaryings AtlasVert(AtlasVertex input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    AtlasVaryings output; UNITY_INITIALIZE_OUTPUT(AtlasVaryings, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    float2 position = AtlasGridCoordinate(input.uv, UNITY_ACCESS_INSTANCED_PROP(AtlasInstance, _AtlasDestinationBorder));
    output.position = UnityObjectToClipPos(float4(position - .5, input.vertex.z, 1));
    AtlasRecord record;
    output.valid = AtlasLookup((uint)UNITY_ACCESS_INSTANCED_PROP(AtlasInstance, _AtlasImageId), record) ? 1 : 0;
    float2 sourceUv = AtlasGridCoordinate(input.uv, UNITY_ACCESS_INSTANCED_PROP(AtlasInstance, _AtlasSourceBorder));
    output.uv = sourceUv * record.scaleOffset.xy + record.scaleOffset.zw;
    output.uvClamp = record.clamp;
    output.tint = UNITY_ACCESS_INSTANCED_PROP(AtlasInstance, _AtlasTint);
    return output;
}
float4 AtlasFrag(AtlasVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    clip(input.valid - .5);
    float2 uv = clamp(input.uv, input.uvClamp.xy, input.uvClamp.zw);
    return tex2D(_AtlasTex, uv) * input.tint;
}
#endif
