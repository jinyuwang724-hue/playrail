// 「隐现」背景（folia 的 Latent 背景）的像素着色器：一张全屏画面里叠出 folia 的三层——
//   1. MeshGradient（流体）：6 个颜色点按时间绕行、距离 3.5 次方反比加权，扭曲 + 旋涡；图层 scale(1.025)，
//      filter: saturate(1.04) brightness(0.94)；
//   2. Dithering（像素）：warp 形状 + 4x4 Bayer 抖动，像素格 2.5 CSS px，scale 0.6；图层 scale(1.015)，
//      mix-blend-mode: soft-light，opacity 0.55；
//   3. 主题底色 35 % 的遮罩。
// 两个着色器的数学移植自 Paper Shaders 0.0.80（https://github.com/paper-design/shaders，Apache-2.0）的
// mesh-gradient / dithering 片元着色器与 vertex-shader 的 object / pattern 坐标；各层参数与叠法照 folia
// LatentBackground 的默认值（没有音频时音频项都是 0）。GLSL 的 gl_FragCoord 原点在左下，这里把 D2D 的场景坐标翻过来。
//
// 编译：fxc /T ps_4_0 /E main /D D2D_FULL_SHADER /D D2D_ENTRY=main /I <Windows SDK>\Include\<ver>\um /Fo Latent.bin Latent.hlsl

#define D2D_INPUT_COUNT 0
#define D2D_REQUIRES_SCENE_POSITION
#include "d2d1effecthelpers.hlsli"

float2 resolution;      // 画布像素（整个视口的画布）
float2 origin;          // 这次只画画布里的一条（缝的开口）：这一条左上角在画布里的位置
float pixelRatio;       // 画布像素 / CSS px（folia：min(dpr, √(1280×720 / 像素数) × dpr)，下限 1）
float meshTime;         // u_time（秒 × 速度）
float ditherTime;
float distortion;
float swirl;
float grainOverlay;
float pxSize;           // 抖动像素格（CSS px）
float ditherScale;      // Dithering 的 scale（默认预设 0.6）
float ditherOpacity;
float overlayOpacity;
float displayMode;      // 0 mixed, 1 pixels, 2 fluid (Folia displayMode)
float4 c0;
float4 c1;
float4 c2;
float4 c3;
float4 c4;
float4 c5;
float4 ditherBack;
float4 ditherFront;
float4 themeBg;

static const float bayer4[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

float hash21(float2 p)
{
    p = frac(p * float2(0.3183099, 0.3678794)) + 0.1;
    p += dot(p, p + 19.19);
    return frac(p.x * p.y);
}

float valueNoise(float2 st)
{
    float2 i = floor(st);
    float2 f = frac(st);
    float a = hash21(i);
    float b = hash21(i + float2(1.0, 0.0));
    float c = hash21(i + float2(0.0, 1.0));
    float d = hash21(i + float2(1.0, 1.0));
    float2 u = f * f * (3.0 - 2.0 * f);
    float x1 = lerp(a, b, u.x);
    float x2 = lerp(c, d, u.x);
    return lerp(x1, x2, u.y);
}

// GLSL: mat2(cos, sin, -sin, cos) * uv（列主序）
float2 rotate2(float2 uv, float th)
{
    float c = cos(th), s = sin(th);
    return float2(c * uv.x - s * uv.y, s * uv.x + c * uv.y);
}

float2 getPosition(int i, float t)
{
    float fi = (float)i;
    float a = fi * 0.37;
    float b = 0.6 + frac(fi / 3.0) * 0.9;
    float c = 0.8 + frac((fi + 1.0) / 4.0);
    float x = sin(t * b + a);
    float y = cos(t * c + a * 1.5);
    return 0.5 + 0.5 * float2(x, y);
}

float4 colorAt(int i)
{
    if (i == 0) return c0;
    if (i == 1) return c1;
    if (i == 2) return c2;
    if (i == 3) return c3;
    if (i == 4) return c4;
    return c5;
}

// MeshGradient（fit: contain、scale 1）：fragCoord 是 GL 坐标（左下原点、像素中心 .5）
float4 meshGradient(float2 fragCoord)
{
    float m = min(resolution.x, resolution.y);
    float2 uv = (fragCoord / resolution - 0.5) * (resolution / m);   // v_objectUV
    uv += 0.5;
    float2 grainUV = uv * 1000.0;

    const float firstFrameOffset = 41.5;
    float t = 0.5 * (meshTime + firstFrameOffset);

    float radius = smoothstep(0.0, 1.0, length(uv - 0.5));
    float center = 1.0 - radius;
    [unroll] for (float i = 1.0; i <= 2.0; i += 1.0)
    {
        uv.x += distortion * center / i * sin(t + i * 0.4 * smoothstep(0.0, 1.0, uv.y)) * cos(0.2 * t + i * 2.4 * smoothstep(0.0, 1.0, uv.y));
        uv.y += distortion * center / i * cos(t + i * 2.0 * smoothstep(0.0, 1.0, uv.x));
    }

    float2 uvRotated = uv - 0.5;
    float angle = 3.0 * swirl * radius;
    uvRotated = rotate2(uvRotated, -angle) + 0.5;

    float3 color = 0;
    float opacity = 0;
    float totalWeight = 0;
    [unroll] for (int k = 0; k < 6; k++)
    {
        float4 ck = colorAt(k);
        float2 pos = getPosition(k, t);          // grainMixer = 0
        float dist = length(uvRotated - pos);
        dist = pow(dist, 3.5);
        float weight = 1.0 / (dist + 1e-3);
        color += ck.rgb * ck.a * weight;
        opacity += ck.a * weight;
        totalWeight += weight;
    }
    color /= max(1e-4, totalWeight);
    opacity /= max(1e-4, totalWeight);

    float g = valueNoise(rotate2(grainUV, 1.0) + float2(3.0, 3.0));
    g = lerp(g, valueNoise(rotate2(grainUV, 2.0) + float2(-1.0, -1.0)), 0.5);
    g = pow(g, 1.3);
    float gv = g * 2.0 - 1.0;
    float3 gColor = step(0.0, gv);
    float strength = pow(abs(grainOverlay * abs(gv)), 0.8);
    color = lerp(color, gColor, 0.35 * strength);
    opacity = saturate(opacity + 0.5 * strength);
    return float4(color, opacity);
}

// Dithering（shape: warp、type: 4x4、pattern 坐标、fit: none）
float3 dithering(float2 fragCoord)
{
    float t = 0.5 * ditherTime;
    float px = pxSize * pixelRatio;
    float2 pxSizeUV = (fragCoord - 0.5 * resolution) / px;
    float2 canvasPixelizedUV = (floor(pxSizeUV) + 0.5) * px;

    float2 shapeUV = canvasPixelizedUV / pixelRatio / ditherScale + 0.5;
    shapeUV *= 0.003;
    [unroll] for (float i = 1.0; i < 6.0; i += 1.0)
    {
        shapeUV.x += 0.6 / i * cos(i * 2.5 * shapeUV.y + t);
        shapeUV.y += 0.6 / i * cos(i * 1.5 * shapeUV.x + t);
    }
    float shape = 0.15 / max(0.001, abs(sin(t - shapeUV.y - shapeUV.x)));
    shape = smoothstep(0.02, 1.0, shape);

    int2 p = (int2)(frac(pxSizeUV / 4.0) * 4.0);
    float d = bayer4[p.y * 4 + p.x] / 16.0 - 0.5;
    float res = step(0.5, shape + d);

    float3 fg = ditherFront.rgb * ditherFront.a;
    float3 bg = ditherBack.rgb * ditherBack.a;
    float fgA = ditherFront.a * res;
    return fg * res + bg * (1.0 - fgA);
}

// CSS filter: saturate(s)
float3 saturateCss(float3 c, float s)
{
    float3x3 m = float3x3(
        0.213 + 0.787 * s, 0.715 - 0.715 * s, 0.072 - 0.072 * s,
        0.213 - 0.213 * s, 0.715 + 0.285 * s, 0.072 - 0.072 * s,
        0.213 - 0.213 * s, 0.715 - 0.715 * s, 0.072 + 0.928 * s);
    return mul(m, c);
}

// CSS mix-blend-mode: soft-light
float3 softLight(float3 cb, float3 cs)
{
    float3 d = (cb <= 0.25) ? ((16.0 * cb - 12.0) * cb + 4.0) * cb : sqrt(cb);
    float3 lo = cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb);
    float3 hi = cb + (2.0 * cs - 1.0) * (d - cb);
    return (cs <= 0.5) ? lo : hi;
}

D2D_PS_ENTRY(main)
{
    float2 scene = D2DGetScenePosition().xy + origin;
    float2 frag = float2(scene.x, resolution.y - scene.y);   // GL 坐标
    float2 mid = 0.5 * resolution;

    // 根节点底色
    float3 col = themeBg.rgb;

    // 流体层：scale(1.025)，saturate(1.04) brightness(0.94)
    if (displayMode != 1) {
        float4 mesh = meshGradient(mid + (frag - mid) / 1.025);
        float3 meshRgb = saturate(saturateCss(mesh.rgb, 1.04) * 0.94);
        col = meshRgb + col * (1.0 - mesh.a);
    }

    // 像素层：scale(1.015)，soft-light，opacity
    if (displayMode != 2) {
        float3 dith = saturate(dithering(mid + (frag - mid) / 1.015));
        col = displayMode == 1 ? dith : lerp(col, softLight(saturate(col), dith), ditherOpacity);
    }

    // 主题底色遮罩
    col = lerp(col, themeBg.rgb, overlayOpacity);
    return float4(col, 1.0);
}
