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
static const float u_contrast = 0.32;
static const float u_roughness = 0.42;
static const float u_fiber = 0.3;
static const float u_fiberSize = 0.2;
static const float u_crumples = 0.35;
static const float u_crumpleSize = 0.35;
static const float u_folds = 0.65;
static const float u_foldCount = 5;
static const float u_drops = 0.2;
static const float u_seed = 5.8;
static const float u_fade = 0;





























float getUvFrame(float2 uv) {
  float aax = 2. * fwidth(uv.x);
  float aay = 2. * fwidth(uv.y);

  float left   = smoothstep(0., aax, uv.x);
  float right = 1. - smoothstep(1. - aax, 1., uv.x);
  float bottom = smoothstep(0., aay, uv.y);
  float top = 1. - smoothstep(1. - aay, 1., uv.y);

  return left * right * bottom * top;
}


#define TWO_PI 6.28318530718
#define PI 3.14159265358979323846





  float randomR(float2 p) {
    float2 uv = floor(p) / 100. + .5;
    return sampleNoise(frac(uv)).r;
  }

float valueNoise(float2 st) {
  float2 i = floor(st);
  float2 f = frac(st);
  float a = randomR(i);
  float b = randomR(i + float2(1.0, 0.0));
  float c = randomR(i + float2(0.0, 1.0));
  float d = randomR(i + float2(1.0, 1.0));
  float2 u = f * f * (3.0 - 2.0 * f);
  float x1 = lerp(a, b, u.x);
  float x2 = lerp(c, d, u.x);
  return lerp(x1, x2, u.y);
}
float fbm(float2 n) {
  float total = 0.0, amplitude = .4;
  for (int i = 0; i < 3; i++) {
    total += valueNoise(n) * amplitude;
    n *= 1.99;
    amplitude *= 0.65;
  }
  return total;
}


float randomG(float2 p) {
  float2 uv = floor(p) / 50. + .5;
  return sampleNoise(frac(uv)).g;
}
float roughnessShape(float2 p) {
  p *= .1;
  float o = 0.;
  for (float i = 0.; ++i < 4.; p *= 2.1) {
    float4 w = float4(floor(p), ceil(p));
    float2 f = frac(p);
    o += lerp(
    lerp(randomG(w.xy), randomG(w.xw), f.y),
    lerp(randomG(w.zy), randomG(w.zw), f.y),
    f.x);
    o += .2 / exp(2. * abs(sin(.2 * p.x + .5 * p.y)));
  }
  return o / 3.;
}


float fiberRandom(float2 p) {
  float2 uv = floor(p) / 100.;
  return sampleNoise(frac(uv)).b;
}

float fiberValueNoise(float2 st) {
  float2 i = floor(st);
  float2 f = frac(st);
  float a = fiberRandom(i);
  float b = fiberRandom(i + float2(1.0, 0.0));
  float c = fiberRandom(i + float2(0.0, 1.0));
  float d = fiberRandom(i + float2(1.0, 1.0));
  float2 u = f * f * (3.0 - 2.0 * f);
  float x1 = lerp(a, b, u.x);
  float x2 = lerp(c, d, u.x);
  return lerp(x1, x2, u.y);
}

float fiberNoiseFbm(in float2 n, float2 seedOffset) {
  float total = 0.0, amplitude = 1.;
  for (int i = 0; i < 4; i++) {
    n = rotate(n, .7);
    total += fiberValueNoise(n + seedOffset) * amplitude;
    n *= 2.;
    amplitude *= 0.6;
  }
  return total;
}

float fiberNoise(float2 uv, float2 seedOffset) {
  float epsilon = 0.001;
  float n1 = fiberNoiseFbm(uv + float2(epsilon, 0.0), seedOffset);
  float n2 = fiberNoiseFbm(uv - float2(epsilon, 0.0), seedOffset);
  float n3 = fiberNoiseFbm(uv + float2(0.0, epsilon), seedOffset);
  float n4 = fiberNoiseFbm(uv - float2(0.0, epsilon), seedOffset);
  return length(float2(n1 - n2, n3 - n4)) / (2.0 * epsilon);
}


float2 randomGB(float2 p) {
  float2 uv = floor(p) / 50. + .5;
  return sampleNoise(frac(uv)).gb;
}
float crumpledNoise(float2 t, float pw) {
  float2 p = floor(t);
  float wsum = 0.;
  float cl = 0.;
  for (int y = -1; y < 2; y += 1) {
    for (int x = -1; x < 2; x += 1) {
      float2 b = float2(float(x), float(y));
      float2 q = b + p;
      float2 q2 = q - floor(q / 8.) * 8.;
      float2 c = q + randomGB(q2);
      float2 r = c - t;
      float w = pow(smoothstep(0., 1., 1. - abs(r.x)), pw) * pow(smoothstep(0., 1., 1. - abs(r.y)), pw);
      cl += (.5 + .5 * sin((q2.x + q2.y * 5.) * 8.)) * w;
      wsum += w;
    }
  }
  return pow(wsum != 0.0 ? cl / wsum : 0.0, .5) * 2.;
}
float crumplesShape(float2 uv) {
  return crumpledNoise(uv * .25, 16.) * crumpledNoise(uv * .5, 2.);
}


float2 folds(float2 uv) {
  float3 pp = (float3)(0.);
  float l = 9.;
  for (float i = 0.; i < 15.; i++) {
    if (i >= u_foldCount) break;
    float2 rand = randomGB(float2(i, i * u_seed));
    float an = rand.x * TWO_PI;
    float2 p = float2(cos(an), sin(an)) * rand.y;
    float dist = distance(uv, p);
    l = min(l, dist);

    if (l == dist) {
      pp.xy = (uv - p.xy);
      pp.z = dist;
    }
  }
  return lerp(pp.xy, (float2)(0.), pow(pp.z, .25));
}

float dropsShape(float2 uv) {
  float2 iDropsUV = floor(uv);
  float2 fDropsUV = frac(uv);
  float dropsMinDist = 1.;
  for (int j = -1; j <= 1; j++) {
    for (int i = -1; i <= 1; i++) {
      float2 neighbor = float2(float(i), float(j));
      float2 offset = randomGB(iDropsUV + neighbor);
      offset = .5 + .5 * sin(10. * u_seed + TWO_PI * offset);
      float2 pos = neighbor + offset - fDropsUV;
      float dist = length(pos);
      dropsMinDist = min(dropsMinDist, dropsMinDist*dist);
    }
  }
  return 1. - smoothstep(.05, .09, pow(dropsMinDist, .5));
}

float lst(float edge0, float edge1, float x) {
  return clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
}

D2D_PS_ENTRY(main) {
  float2 scene = D2DGetScenePosition().xy;
  float2 v_imageUV = imageUv(scene);
  float4 gl_FragCoord = float4(scene.x, resolution.y-scene.y, 0, 1);
  float4 fragColor;

  float2 imageUV = v_imageUV;
  float2 patternUV = v_imageUV - .5;
  patternUV = 5. * (patternUV * float2(u_imageAspectRatio, 1.));

  float2 roughnessUv = 1.5 * (gl_FragCoord.xy - .5 * u_resolution) / u_pixelRatio;
  float roughness = roughnessShape(roughnessUv + float2(1., 0.)) - roughnessShape(roughnessUv - float2(1., 0.));

  float2 crumplesUV = frac(patternUV * .02 / u_crumpleSize - u_seed) * 32.;
  float crumples = u_crumples * (crumplesShape(crumplesUV + float2(.05, 0.)) - crumplesShape(crumplesUV));

  float2 fiberUV = 2. / u_fiberSize * patternUV;
  float fiber = fiberNoise(fiberUV, (float2)(0.));
  fiber = .5 * u_fiber * (fiber - 1.);

  float2 normal = (float2)(0.);
  float2 normalImage = (float2)(0.);

  float2 foldsUV = patternUV * .12;
  foldsUV = rotate(foldsUV, 4. * u_seed);
  float2 w = folds(foldsUV);
  foldsUV = rotate(foldsUV + .007 * cos(u_seed), .01 * sin(u_seed));
  float2 w2 = folds(foldsUV);

  float drops = u_drops * dropsShape(patternUV * 2.);

  float fade = u_fade * fbm(.17 * patternUV + 10. * u_seed);
  fade = clamp(8. * fade * fade * fade, 0., 1.);

  w = lerp(w, (float2)(0.), fade);
  w2 = lerp(w2, (float2)(0.), fade);
  crumples = lerp(crumples, 0., fade);
  drops = lerp(drops, 0., fade);
  fiber *= lerp(1., .5, fade);
  roughness *= lerp(1., .5, fade);

  normal.xy += u_folds * min(5. * u_contrast, 1.) * 4. * max((float2)(0.), w + w2);
  normalImage.xy += u_folds * 2. * w;

  normal.xy += crumples;
  normalImage.xy += 1.5 * crumples;

  normal.xy += 3. * drops;
  normalImage.xy += .2 * drops;

  normal.xy += u_roughness * 1.5 * roughness;
  normal.xy += fiber;

  normalImage += u_roughness * .75 * roughness;
  normalImage += .2 * fiber;

  float3 lightPos = float3(1., 2., 1.);
  float res = dot(normalize(float3(normal, 9.5 - 9. * pow(u_contrast, .1))), normalize(lightPos));

  float3 fgColor = u_colorFront.rgb * u_colorFront.a;
  float fgOpacity = u_colorFront.a;
  float3 bgColor = u_colorBack.rgb * u_colorBack.a;
  float bgOpacity = u_colorBack.a;

  imageUV += .02 * normalImage;
  float frame = getUvFrame(imageUV);
  float4 image = sampleImage(imageUV);
  image.rgb += .6 * pow(u_contrast, .4) * (res - .7);

  frame *= image.a;

  float3 color = fgColor * res;
  float opacity = fgOpacity * res;

  color += bgColor * (1. - opacity);
  opacity += bgOpacity * (1. - opacity);
  opacity = lerp(opacity, 1., frame);

  color -= .007 * drops;

  color.rgb = lerp(color, image.rgb, frame);

  fragColor = float4(color, opacity);

  float3 opaque = fragColor.rgb + themeBg.rgb*(1.-fragColor.a);
  return float4(lerp(opaque, themeBg.rgb, .35), 1);
}
