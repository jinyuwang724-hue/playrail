Texture2D<float4> source : register(t0);
SamplerState sampleMode : register(s0);
cbuffer View : register(b0) { float4 geometry; float4 screen; };
struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
Vertex VS(uint index : SV_VertexID) {
    Vertex v;
    v.uv = float2((index << 1) & 2, index & 2);
    v.position = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}
float4 PS(Vertex v) : SV_TARGET {
    float2 pixelPosition = v.position.xy;
    float2 uv = (pixelPosition - geometry.xy) / geometry.zw;
    if (any(uv < 0) || any(uv > 1)) return float4(0, 0, 0, 1);
    return float4(source.SampleLevel(sampleMode, uv, 0).rgb, 1);
}
