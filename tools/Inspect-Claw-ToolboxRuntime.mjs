import fs from 'node:fs';
import path from 'node:path';
const root = path.resolve(import.meta.dirname, '..');
const targets = await (await fetch('http://127.0.0.1:8080/json/list')).json();
const target = targets.find(target => target.title === 'SharedJSContext');
if (!target) throw new Error('Steam shared context unavailable.');
const socket = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
let serial = 0;
const scripts = [], pending = new Map();
socket.addEventListener('message', event => {
  const message = JSON.parse(event.data);
  if (message.method === 'Debugger.scriptParsed') scripts.push(message.params);
  const request = pending.get(message.id);
  if (request) { clearTimeout(request.timer); pending.delete(message.id); message.error ? request.reject(new Error(message.error.message)) : request.resolve(message.result); }
});
function rpc(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++serial;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('Steam inspection timed out')); }, 8000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
}
try {
  await rpc('Debugger.enable');
  const modules = [];
  for (const script of scripts.filter(script => /Playhub|Claw|gaming-mode/i.test(script.url))) {
    const source = (await rpc('Debugger.getScriptSource', { scriptId: script.scriptId })).scriptSource;
    if (source.includes('function nativePattern(') || source.includes('function installNavigationHaptics(')) {
      modules.push({ scriptId: script.scriptId, playhubNavigation: source.includes('function nativePattern('),
        oldPlayhubPatch: source.includes('function msiClawNavigationPulse('), toolbox: source.includes('function installNavigationHaptics(') });
    }
  }
  const result = await rpc('Runtime.evaluate', { expression: `Promise.all([
    window.DeckyBackend.call("loader/call_plugin_method","Claw 掌机工具箱","get_state"),
    fetch("http://127.0.0.1:47991/dash/haptic",{method:"POST",headers:{"Content-Type":"application/json"},
      body:JSON.stringify({stop:true,requestId:Date.now()*1000})}).then(r=>r.json())
  ]).then(([state,stop])=>({state,stop,hapticBridge:!!window.__clawToolboxHaptics,
    plugins:window.DeckyPluginLoader.plugins.map(p=>({name:p.name,version:p.version}))}))`, awaitPromise: true, returnByValue: true });
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.text);
  const report = { checkedAt: new Date().toISOString(), ...result.result.value, modules,
    physicalRetestPending: true };
  fs.writeFileSync(path.join(root, 'diagnostics/claw-toolbox-runtime.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report));
} finally { await rpc('Debugger.disable').catch(() => {}); socket.close(); }
