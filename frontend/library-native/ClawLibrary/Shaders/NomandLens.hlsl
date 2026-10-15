// Ported from Paper Shaders 0.0.80, Apache-2.0. See Assets/Licenses.
// Changes: GLSL -> Direct2D HLSL; Folia Nomand defaults; cover UV and opaque theme composite.
#define D2D_INPUT_COUNT 2
#define D2D_INPUT0_COMPLEX
#define D2D_INPUT1_COMPLEX
#define D2D_REQUIRES_SCENE_POSITION
#include "d2d1effecthelpers.hlsli"
float2 resolution;
float2 imageSize;
float pixelRatio;
float imageScale;
float4 themeBg;
float4 themeAccent;
#define u_imageAspectRatio (imageSize.x / imageSize.y)
#define u_resolution resolution
#define u_pixelRatio pixelRatio
#define u_colorFront themeAccent
#define u_colorBack themeBg
float mod(float x, float y) { return x - y * floor(x / y); }
float3 mod(float3 x, float y) { return x - y * floor(x / y); }
float2 rotate(float2 uv, float th) { return float2(cos(th)*uv.x-sin(th)*uv.y, sin(th)*uv.x+cos(th)*uv.y); }
float4 sampleImage(float2 uv) { return D2DSampleInputAtPosition(0, clamp(uv, .5/imageSize, 1.-.5/imageSize) * imageSize); }
float4 sampleNoise(float2 uv) { return D2DSampleInputAtPosition(1, frac(uv) * 128.); }
float2 imageUv(float2 pos) {
    float2 box = imageSize * max(resolution.x/imageSize.x, resolution.y/imageSize.y);
    return (pos - .5*resolution) / box / imageScale + .5;
}
static const float u_spread = 0.45;
static const float u_bias = 1;
static const float u_angle = 0;
static const float u_perspective = 0.4;
static const float u_count = 20;
static const float u_dispersion = 0.65;
static const float u_dispersionShift = 0;
static const float u_dispersionColor = 0.6;
static const float u_focusCenter = 0.55;
static const float u_focusEdges = 0.8;
static const float u_swirl = 0.08;
static const float u_noise = 0;
static const float u_noiseFrequency = 0.25;
static const float u_noiseOffset = 0;
static const float u_lensBulge = 0.3;
static const float u_lensCircle = 0.1;
static const float u_grainMixer = 0;
static const float u_grainOverlay = 0;
static const float u_imageX = 0;
static const float u_imageY = 0;






























#define TWO_PI 6.28318530718
#define PI 3.14159265358979323846


  float hash21(float2 p) {
    p = frac(p * float2(0.3183099, 0.3678794)) + 0.1;
    p += dot(p, p + 19.19);
    return frac(p.x * p.y);
  }





float valueNoise(float2 st) {
  float2 i = floor(st);
  float2 f = frac(st);
  float a = hash21(i);
  float b = hash21(i + float2(1., 0.));
  float c = hash21(i + float2(0., 1.));
  float d = hash21(i + float2(1., 1.));
  float2 u = f * f * (3. - 2. * f);
  return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float getUvFrame(float2 uv) {
  float2 invAA = 1. / clamp(fwidth(uv), 1e-5, .02);
  float2 lo = clamp(uv * invAA + .5, 0., 1.);
  float2 hi = clamp((1. - uv) * invAA + .5, 0., 1.);
  return lo.x * hi.x * lo.y * hi.y;
}

float4 sampleOverWhite(float2 uv) {
  float4 img = sampleImage(uv);
  float cover = img.a * getUvFrame(uv);
  float3 colorOverWhite = lerp((float3)(1.), img.rgb, cover);
  return float4(colorOverWhite, cover);
}

float3 hueColor(float hue) {
  float3 rgb = clamp(abs(mod(hue * 6. + float3(0., 4., 2.), 6.) - 3.) - 1., 0., 1.);
  rgb = rgb * rgb * (3. - 2. * rgb);
  return rgb;
}
                                            
float boxInradius() {
  return .5 * min(u_imageAspectRatio, 1.);
}

float boxOutradius() {
  return .5 * length(float2(u_imageAspectRatio, 1.));
}

float spreadReach() {
  return .7 * pow(u_spread, 1.3 + 2.7 * u_spread);
}

float innerCircleMask(float radius, float inradius) {
  return 1. - smoothstep(.5 * inradius, 1.1 * inradius, radius);
}

float dispersionCurve(float t, float biasPow) {
  float mirroredT = u_bias < 0. ? 1. - t : t;
  float curved = pow(mirroredT, biasPow);
  return u_bias < 0. ? 1. - curved : curved;
}

float2 getSpread(float2 fromCenter, float radius, float2 warpedUV, float edgeAA, float inradius, float outradius, float reach, out float outStrength) {
  float angleRad = radians(u_angle);
  float2 uniformDir = float2(cos(angleRad), sin(angleRad));

  float invInradius = 1. / inradius;
  float2 radialDir = fromCenter * invInradius;
  float maxLen = (outradius + reach) * invInradius;
  float radialLen = radius * invInradius;
  if (radialLen > maxLen) radialDir *= maxLen / radialLen;
  float2 spreadDir = lerp(uniformDir, radialDir, u_perspective);

  float bandProximity = smoothstep(inradius * .8, inradius, radius);
  float lensCircleMaxing = bandProximity * u_lensCircle * u_lensCircle * u_lensCircle;
  spreadDir = lerp(spreadDir, radialDir, lensCircleMaxing);

  float2 warpedAbs = abs(warpedUV - .5);
  float inner = lerp(1., smoothstep(0., lerp(inradius, outradius, u_focusCenter), radius), u_focusCenter);
  float boxDist = max(warpedAbs.x, warpedAbs.y) * 2.;
  float outer = lerp(1., 1. - min(boxDist, 1.), u_focusEdges);
  float strength = inner * outer;

  strength *= lerp(1., lerp(.15, .03, max(-u_lensBulge, 0.)), lensCircleMaxing);

  float2 outside = max(warpedAbs - .5, 0.);
  float margin = max(reach * length(spreadDir) * (1. - u_lensCircle), edgeAA);
  strength *= 1. - smoothstep(0., margin, length(outside));

  outStrength = strength;
  float2 axis = spreadDir * (reach * strength);

  if (u_noise > 0.) {
    float turn = (valueNoise(fromCenter * u_noiseFrequency * 18. + u_noiseOffset * 30.) - .5) * 2. * u_noise;
    float cs = cos(turn), sn = sin(turn);
    axis = float2(cs*axis.x+sn*axis.y, -sn*axis.x+cs*axis.y);
  }

  axis.x /= u_imageAspectRatio;
  return axis;
}

float2 lensWarp(float2 fromCenter, float radius, float inradius, out float bulgeFade) {
  bulgeFade = 1.;
  if (u_lensBulge == 0. && u_lensCircle <= 0.) return fromCenter;
  if (radius < 1e-5) return fromCenter;
  
  float r = radius;

  if (u_lensBulge != 0.) {
    float rn = radius / inradius;
    float bulge = abs(u_lensBulge) * (u_lensBulge > 0. ? 1.4 : 1.2);
    if (u_lensBulge > 0.) bulgeFade = 1. - smoothstep(1.45, 1.53, rn * bulge);
    float map = u_lensBulge > 0.
      ? tan(min(rn * bulge, 1.53)) / tan(bulge)
      : atan(rn * tan(bulge)) / bulge;
    float bulgeScale = map / rn;
    fromCenter *= bulgeScale;
    r *= bulgeScale;
  }

  if (u_lensCircle > 0.) {
    float2 dir = fromCenter / max(r, 1e-5);
    float2 halfBox = float2(u_imageAspectRatio, 1.) * .5;
    float rBox = min(halfBox.x / max(abs(dir.x), 1e-4), halfBox.y / max(abs(dir.y), 1e-4));
    float band = inradius * lerp(.03, .2, .5 * (u_lensBulge + 1.));
    float innerEdge = inradius - band;
    float over = smoothstep(0., 1., (r - innerEdge) / band);
    float g = r + (rBox - inradius) * pow(over, 14.);
    fromCenter = dir * lerp(r, g, u_lensCircle);
  }

  return fromCenter;
}

D2D_PS_ENTRY(main) {
  float2 scene = D2DGetScenePosition().xy;
  float2 v_imageUV = imageUv(scene);
  float4 gl_FragCoord = float4(scene.x, resolution.y-scene.y, 0, 1);
  float4 fragColor;
  float2 uv = v_imageUV;

  float invAspect = 1. / u_imageAspectRatio;
  float inradius = boxInradius();
  float outradius = boxOutradius();
  float reach = spreadReach();

  float2 fromCenter = uv - .5;
  fromCenter.x *= u_imageAspectRatio;
  float radius = length(fromCenter);

  float bulgeFade;
  float2 warpedFromCenter = lensWarp(fromCenter, radius, inradius, bulgeFade);
  float2 baseUV = float2(warpedFromCenter.x * invAspect, warpedFromCenter.y) + .5;

  float2 baseDerivative = fwidth(baseUV);
  float edgeAA = clamp(2. * max(baseDerivative.x, baseDerivative.y), .001, .02);

  float spreadStrength;
  float2 spreadAxis = getSpread(fromCenter, radius, baseUV, edgeAA, inradius, outradius, reach, spreadStrength);

  float2 grainUV = (float2)(0.);
  if (u_grainMixer > 0. || u_grainOverlay > 0.) {
    float2 dudx = ddx(v_imageUV);
    float2 dudy = ddy(v_imageUV);
    grainUV = (v_imageUV - .5) * (.8 / float2(length(dudx), length(dudy))) + .5;
  }

  if (u_grainMixer > 0.) {
    float grainSeed = valueNoise(grainUV);
    float2 jit = frac(grainSeed * float2(157.31, 113.57)) * 2. - 1.;
    spreadAxis += jit * .3 * u_grainMixer * length(spreadAxis);
  }

  float count = floor(u_count);
  int countInteger = int(count);

  float invCount = 1. / count;
  float invSpan = 1. / max(count - 1., 1.);
  float hueBase = u_dispersionColor + .5;
  float biasPower = 1. + 2. * abs(u_bias);
  float2 tapBias = .5 - float2(u_imageX, u_imageY);

  float centerAmount = clamp(1. - u_dispersionShift, 0., 1.);
  float edgesAmount = clamp(1. + u_dispersionShift, 0., 1.);
  float dispersion = u_dispersion * lerp(edgesAmount, centerAmount, innerCircleMask(radius, inradius));
  float dispersionPower = pow(dispersion, .8);

  float warpedRadius = length(warpedFromCenter);
  float swirlAngle = u_swirl * .4 * PI * spreadStrength * u_spread * min(1., inradius / max(warpedRadius, 1e-4));

  float3 colorSum = (float3)(0.);
  float3 weightSum = (float3)(0.);
  float coverSum = 0.;

  for (int i = 0; i < 50; i++) {
    if (i >= countInteger) break;

    float layer = float(i);
    float hue = hueBase + layer * invCount;

    float spread = layer * invSpan;
    if (u_bias != 0.) spread = dispersionCurve(spread, biasPower);

    // lerp(axis, -axis, spread) is axis scaled by the signed position along the fan
    float fanPos = 1. - 2. * spread;
    float2 tapUV = baseUV + spreadAxis * fanPos - .5;

    if (u_swirl != 0.) {
      tapUV.x *= u_imageAspectRatio;
      tapUV = rotate(tapUV, swirlAngle * fanPos);
      tapUV.x *= invAspect;
    }

    float4 tap = sampleOverWhite(tapUV + tapBias);
    float3 weight = 1. - dispersionPower * hueColor(hue);

    colorSum += tap.rgb * weight;
    weightSum += weight;
    coverSum += tap.a;
  }

  float3 color = colorSum / max(weightSum, 1e-4);
  float coverAvg = coverSum * invCount;

  float ground = min(color.r, min(color.g, color.b));
  float alpha = max(coverAvg, 1. - ground);
  float3 premult = max(color - (1. - alpha), 0.);
  fragColor = float4(premult, alpha) * bulgeFade;

  if (u_grainOverlay > 0.) {
    float grain = valueNoise(rotate(grainUV, 1.) + (float2)(3.));
    grain = lerp(grain, valueNoise(rotate(grainUV, 2.) + (float2)(-1.)), .5);
    grain = pow(grain, 1.3);
    float grainV = grain * 2. - 1.;
    float grainStrength = pow(u_grainOverlay * abs(grainV), .8) * fragColor.a;
    fragColor.rgb = lerp(fragColor.rgb, (float3)(step(0., grainV)) * fragColor.a, .35 * grainStrength);
  }

  float3 opaque = fragColor.rgb + themeBg.rgb*(1.-fragColor.a);
  return float4(lerp(opaque, themeBg.rgb, .35), 1);
}
