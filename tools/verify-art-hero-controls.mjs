import fs from 'node:fs';
import path from 'node:path';
const targets = await (await fetch('http://127.0.0.1:8080/json/list')).json();
const shared = targets.find(t => t.title === 'SharedJSContext');
const picture = targets.find(t => /大屏幕|Big Picture/.test(t.title));
if (!shared || !picture) throw new Error('Steam Big Picture and Decky must be open');

async function evaluate(target, expression) {
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  let timer;
  try {
    await new Promise((resolve,reject) => {socket.addEventListener('open',resolve,{once:true});socket.addEventListener('error',reject,{once:true});});
    const response = await new Promise((resolve,reject) => {
      timer=setTimeout(()=>reject(new Error('Steam inspection timed out')),12000);
      socket.addEventListener('message',event=>{const message=JSON.parse(event.data);if(message.id===1)resolve(message);});
      socket.send(JSON.stringify({id:1,method:'Runtime.evaluate',params:{expression,returnByValue:true,awaitPromise:true}}));
    });
    if(response.error || response.result?.exceptionDetails) throw new Error(JSON.stringify(response.error ?? response.result.exceptionDetails));
    return response.result.result.value;
  } finally {clearTimeout(timer);socket.close();}
}
async function call(method, args={}) {
  const response = await evaluate(shared, `window.DeckyBackend.call("loader/call_legacy_plugin_method","CSS Loader",${JSON.stringify(method)},${JSON.stringify(args)})`);
  if(!response?.success || response.result?.success===false) throw new Error(JSON.stringify(response));
  return response.result;
}
const getPatch = themes => themes.find(t=>t.name==='Art Hero').patches.find(p=>p.name==='Gradient Settings').value;
const getAppearance = themes => themes.find(t=>t.name==='Art Hero').patches.find(p=>p.name==='Hero Appearance').value;
const getProfile = themes => themes.find(t=>t.enabled && t.flags.includes('PRESET'));
const display = () => evaluate(picture, `(()=>{const container=document.querySelector(".QNkOtW3xS-yj6LviWHvnd");const inner=document.querySelector("._282X0J4BtrSF1IXctmOe-X");const image=document.querySelector("._3Mp8R2wnoCl6ko0HAX8IHE");return {mask:container?getComputedStyle(container).webkitMaskImage:null,innerHeight:inner?getComputedStyle(inner).height:null,imageMask:image?getComputedStyle(image).webkitMaskImage:null,playhubDesignCss:[...document.querySelectorAll(".css-loader-style")].some(s=>s.textContent.includes("--playhub-home-full-hero-height"))};})()`);

const initial = await call('get_themes');
const profile = getProfile(initial);
if(!profile) throw new Error('No active CSS Loader profile');
const original = getPatch(initial);
const originalAppearance = getAppearance(initial);
const testAppearance = originalAppearance === 'Full Screen Hero' ? 'Wide Hero' : 'Full Screen Hero';
const testValue = original === 'Subtle Gradient' ? 'Linear Gradient' : 'Subtle Gradient';
const report = {checkedAt:new Date().toISOString(),profile:profile.name,original,testValue,originalAppearance,testAppearance,before:await display()};
try {
  await call('set_patch_of_theme',{themeName:'Art Hero',patchName:'Gradient Settings',value:testValue});
  await call('generate_preset_theme',{name:profile.name});
  report.immediate={savedValue:getPatch(await call('get_themes')),display:await display()};
  report.reset=await call('reset');
  report.afterReload={savedValue:getPatch(await call('get_themes')),display:await display()};
  await call('set_patch_of_theme',{themeName:'Art Hero',patchName:'Hero Appearance',value:testAppearance});
  report.appearance={savedValue:getAppearance(await call('get_themes')),display:await display()};
  const maskType=testValue==='Subtle Gradient'?'radial-gradient':'linear-gradient';
  report.passed=report.immediate.savedValue===testValue && report.afterReload.savedValue===testValue && report.immediate.display.mask?.startsWith(maskType) && report.afterReload.display.mask?.startsWith(maskType) && !report.afterReload.display.playhubDesignCss && report.appearance.savedValue===testAppearance && report.appearance.display.innerHeight!==report.before.innerHeight;
  if(!report.passed) throw new Error('Art Hero did not control the displayed gradient');
} finally {
  const current=await call('get_themes');
  if(getAppearance(current)===testAppearance) await call('set_patch_of_theme',{themeName:'Art Hero',patchName:'Hero Appearance',value:originalAppearance});
  if(getPatch(current)===testValue) {
    await call('set_patch_of_theme',{themeName:'Art Hero',patchName:'Gradient Settings',value:original});
    const active=getProfile(await call('get_themes'));
    if(active) await call('generate_preset_theme',{name:active.name});
  }
  const final=await call('get_themes');
  report.final={profile:getProfile(final)?.name,savedValue:getPatch(final),appearance:getAppearance(final),display:await display()};
  report.loadErrors=await call('get_last_load_errors');
  await evaluate(shared, `(async()=>{const r=await window.DeckyBackend.call("loader/call_legacy_plugin_method","CSS Loader","get_themes",{});const s=window.DeckyPluginLoader.plugins.find(p=>p.name==="CSS Loader").titleView.props.cssLoaderStateClass;s.setGlobalState("localThemeList",r.result);s.setGlobalState("selectedPreset",r.result.find(t=>t.enabled&&t.flags.includes("PRESET")));window.__cssLoaderEditObservation?.stop?.();return true;})()`);
  const destination=path.resolve('artifacts/css-loader-setup/art-hero-verification.json');
  fs.writeFileSync(destination,JSON.stringify(report,null,2));
}
console.log(JSON.stringify(report));
