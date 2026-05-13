#ifndef SNOW_GROUND_INCLUDED
#define SNOW_GROUND_INCLUDED

// Include Core.hlsl so IDEs (Rider, Visual Studio) recognize TEXTURE2D, SAMPLER,
// SAMPLE_TEXTURE2D_LOD, etc. The include guards in Core.hlsl prevent duplicate
// inclusion when this file is used inside a .shader pass.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// Globals set by SnowAccumulationSystem
TEXTURE2D(_GlobalSnowHeightMap);
SAMPLER(sampler_GlobalSnowHeightMap);
float4 _GlobalSnowOriginXZ;   // xy = origin, zw unused
float  _GlobalSnowWorldSize;
float  _GlobalSnowMaxHeight;

// Returns snow height (0-1) at a given world XZ.
// Use this in a Custom Function node in Shader Graph, or call from HLSL directly.
void SampleSnowHeight_float(float3 worldPos, out float height, out float2 uv)
{
    uv = (worldPos.xz - _GlobalSnowOriginXZ.xy) / _GlobalSnowWorldSize;
    if (any(uv < 0) || any(uv > 1))
    {
        height = _GlobalSnowMaxHeight; // outside region, assume full snow
        return;
    }
    height = SAMPLE_TEXTURE2D_LOD(_GlobalSnowHeightMap, sampler_GlobalSnowHeightMap, uv, 0).r;
}

// Computes a normal from the heightmap by sampling neighbors (for lighting on snow surface)
void SnowNormal_float(float3 worldPos, float strength, out float3 normal)
{
    float2 uv = (worldPos.xz - _GlobalSnowOriginXZ.xy) / _GlobalSnowWorldSize;
    float texel = 1.0 / 512.0; // assume 512; could be uniform

    float hL = SAMPLE_TEXTURE2D_LOD(_GlobalSnowHeightMap, sampler_GlobalSnowHeightMap, uv + float2(-texel,0), 0).r;
    float hR = SAMPLE_TEXTURE2D_LOD(_GlobalSnowHeightMap, sampler_GlobalSnowHeightMap, uv + float2( texel,0), 0).r;
    float hD = SAMPLE_TEXTURE2D_LOD(_GlobalSnowHeightMap, sampler_GlobalSnowHeightMap, uv + float2(0,-texel), 0).r;
    float hU = SAMPLE_TEXTURE2D_LOD(_GlobalSnowHeightMap, sampler_GlobalSnowHeightMap, uv + float2(0, texel), 0).r;

    float3 n = normalize(float3((hL - hR) * strength, 1.0, (hD - hU) * strength));
    normal = n;
}

#endif
