#define A_GPU 1
#define A_HLSL 1
#include "ffx_a.h"
Texture2D<float4> source : register(t0);
SamplerState samplerLinearClamp : register(s0);
RWTexture2D<float4> destination : register(u0);
cbuffer Constants : register(b0) { uint4 con0; uint4 con1; uint4 con2; uint4 con3; uint4 dimensions; };
#ifdef RCAS
#define FSR_RCAS_F 1
AF4 FsrRcasLoadF(ASU2 p) { uint w, h; source.GetDimensions(w,h); return source.Load(int3(clamp(p, int2(0,0), int2(w-1,h-1)),0)); }
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) { }
#else
#define FSR_EASU_F 1
AF4 FsrEasuRF(AF2 p) { return source.GatherRed(samplerLinearClamp,p); }
AF4 FsrEasuGF(AF2 p) { return source.GatherGreen(samplerLinearClamp,p); }
AF4 FsrEasuBF(AF2 p) { return source.GatherBlue(samplerLinearClamp,p); }
#endif
#include "ffx_fsr1.h"
[numthreads(8,8,1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (id.x >= dimensions.x || id.y >= dimensions.y) return;
    float3 result;
#ifdef RCAS
    FsrRcasF(result.r,result.g,result.b,id.xy,con0);
#else
    FsrEasuF(result,id.xy,con0,con1,con2,con3);
#endif
    destination[id.xy] = float4(saturate(result),1);
}
