// Import the pinned Apache-2.0 Paper Shaders 0.0.80 package, never Folia's React code.
// Usage: node Import-PaperShaders.mjs <extracted @paper-design/shaders directory>
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
const source = path.resolve(process.argv[2]);
const target = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../ClawLibrary');
const common = `// Ported from Paper Shaders 0.0.80, Apache-2.0. See Assets/Licenses.
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
`;
const lens = {spread:.45,bias:1,angle:0,perspective:.4,count:20,dispersion:.65,dispersionShift:0,dispersionColor:.6,focusCenter:.55,focusEdges:.8,swirl:.08,noise:0,noiseFrequency:.25,noiseOffset:0,lensBulge:.3,lensCircle:.1,grainMixer:0,grainOverlay:0,imageX:0,imageY:0};
const paper = {contrast:.32,roughness:.42,fiber:.3,fiberSize:.2,crumples:.35,crumpleSize:.35,folds:.65,foldCount:5,drops:.2,seed:5.8,fade:0};
function splats(s) {
  // HLSL scalar casts broadcast; unlike GLSL, floatN(scalar) constructors do not.
  for (let start=s.length-1; start>=0; start--) {
    const m=s.slice(start).match(/^float([234])\(/); if (!m) continue;
    let j=start+m[0].length, depth=1, comma=false;
    for (;j<s.length && depth;j++) { if(s[j]==='(')depth++; if(s[j]===')')depth--; if(s[j]===','&&depth===1)comma=true; }
    if (!comma) s=s.slice(0,start)+`(float${m[1]})(`+s.slice(start+m[0].length,j)+s.slice(j);
  } return s;
}
for (const [file, exported, constants] of [['lens-distortion','lensDistortionFragmentShader',lens],['paper-texture','paperTextureFragmentShader',paper]]) {
  const module = await import(pathToFileURL(path.join(source,'dist/shaders',file+'.js')));
  let s=module[exported];
  s=s.replace(/#version[^\n]*\n|precision\s+[^;]*;|uniform\s+[^;]*;|in vec2 v_imageUV;|out vec4 fragColor;/g,'');
  s=s.replace(/vec2 rotate\(vec2 uv, float th\)\s*\{[\s\S]*?\}/,'');
  s=s.replace(/texture\(u_image, /g,'sampleImage(').replace(/texture\(u_noiseTexture, /g,'sampleNoise(');
  s=s.replace(/\bvec([234])\b/g,'float$1').replace(/\bfract\(/g,'frac(').replace(/\bmix\(/g,'lerp(').replace(/\bdFdx\(/g,'ddx(').replace(/\bdFdy\(/g,'ddy(');
  s=s.replace('axis = mat2(cs, -sn, sn, cs) * axis;', 'axis = float2(cs*axis.x+sn*axis.y, -sn*axis.x+cs*axis.y);');
  s=s.replace('void main() {', `D2D_PS_ENTRY(main) {\n  float2 scene = D2DGetScenePosition().xy;\n  float2 v_imageUV = imageUv(scene);\n  float4 gl_FragCoord = float4(scene.x, resolution.y-scene.y, 0, 1);\n  float4 fragColor;`);
  // HLSL disallows the GLSL local variable / function name shadowing.
  for (const n of ['roughness','drops']) s=s.replace(new RegExp('\\b'+n+'\\(', 'g'),n+'Shape(');
  const last=s.lastIndexOf('}');
  s=s.slice(0,last)+'\n  float3 opaque = fragColor.rgb + themeBg.rgb*(1.-fragColor.a);\n  return float4(lerp(opaque, themeBg.rgb, .35), 1);\n'+s.slice(last);
  s=splats(s);
  const defaults=Object.entries(constants).map(([k,v])=>`static const float u_${k} = ${v};`).join('\n');
  fs.writeFileSync(path.join(target,'Shaders',file==='lens-distortion'?'NomandLens.hlsl':'NomandPaper.hlsl'),(common+defaults+'\n'+s).replace(/[ \t]+$/gm,''));
}
const noise=fs.readFileSync(path.join(source,'dist/get-shader-noise-texture.js'),'utf8').match(/data:image\/png;base64,([^\"]+)/)[1];
fs.writeFileSync(path.join(target,'Assets/PaperNoise.png'),Buffer.from(noise,'base64'));
fs.mkdirSync(path.join(target,'Assets/Licenses'),{recursive:true});
fs.copyFileSync(path.join(source,'LICENSE'),path.join(target,'Assets/Licenses/Paper-Shaders-Apache-2.0.txt'));
fs.copyFileSync(path.join(source,'NOTICE'),path.join(target,'Assets/Licenses/Paper-Shaders-NOTICE.txt'));
console.log('Imported lens, paper, original noise texture, license and notice from Paper Shaders 0.0.80.');
