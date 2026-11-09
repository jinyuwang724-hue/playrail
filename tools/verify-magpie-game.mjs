// Call the installed Decky backend through its existing Steam connection.
// Real-game window activation/screenshots are performed separately with Computer Use.
import fs from 'node:fs';
import path from 'node:path';
const targets = await (await fetch('http://127.0.0.1:8080/json/list')).json();
const target = targets.find(t => t.title === 'SharedJSContext');
if (!target) throw Error('Steam shared context unavailable');
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve,reject) => { ws.addEventListener('open',resolve,{once:true}); ws.addEventListener('error',reject,{once:true}); });
let serial = 0;
async function evaluate(expression) {
  const id = ++serial;
  return new Promise((resolve,reject) => {
    const timeout = setTimeout(() => { ws.removeEventListener('message',read); reject(Error('Steam RPC timeout')); }, 15000);
    const read = event => {
      const reply = JSON.parse(event.data); if (reply.id !== id) return;
      clearTimeout(timeout); ws.removeEventListener('message',read);
      if (reply.error || reply.result.exceptionDetails) reject(Error(JSON.stringify(reply.error || reply.result.exceptionDetails)));
      else resolve(reply.result.result.value);
    };
    ws.addEventListener('message',read);
    ws.send(JSON.stringify({id,method:'Runtime.evaluate',params:{expression,awaitPromise:true,returnByValue:true}}));
  });
}
const call = (method,...args) => evaluate(`window.DeckyBackend.call("loader/call_plugin_method","Claw 掌机工具箱",${JSON.stringify(method)},${args.map(a=>JSON.stringify(a)).join(',')})`.replace(',)',')'));
const folder = path.resolve(import.meta.dirname,'../artifacts/magpie-real-game'); fs.mkdirSync(folder,{recursive:true});
const mode = process.argv[2] || 'state';
try {
  let state = await call('get_magpie_state');
  if (mode === 'apply') {
    const windows = await call('get_magpie_targets');
    const game = windows.find(w => w.path.endsWith('\\常轨脱离Creative\\hamidashi.exe'));
    if (!game) throw Error('Real game window unavailable');
    const name = process.argv[3] || 'quick';
    const catalog = await call('get_magpie_catalog');
    const pipeline = name === 'quick' ? [] : catalog.presets.find(p => p.name === name)?.effects;
    if (!pipeline) throw Error('Unknown preset');
    state = await call('apply_magpie',{...state.preferences,target:game.hwnd,pipeline,
      sharpen:name === 'quick' && process.argv[4] === 'sharpen',strength:1,cropX:0,cropY:25});
    for(let i=0;i<120 && state.enabled && !state.active;i++) {
      await new Promise(resolve=>setTimeout(resolve,250)); state=await call('get_magpie_state');
    }
    if (!state.active) throw Error('No visible game output: '+JSON.stringify(state));
  } else if(mode === 'pause') state=await call('set_magpie_pause','panel',true);
  else if(mode === 'resume') {
    state=await call('set_magpie_pause','panel',false);
    for(let i=0;i<120 && state.enabled && !state.active;i++) {
      await new Promise(resolve=>setTimeout(resolve,250)); state=await call('get_magpie_state');
    }
    if(!state.active) throw Error('Game did not resume');
  } else if(mode === 'stop') state=await call('stop_magpie');
  fs.appendFileSync(path.join(folder,'states.jsonl'),JSON.stringify({time:new Date().toISOString(),mode,preset:process.argv[3],state})+'\n');
  console.log(JSON.stringify(state));
} finally { ws.close(); }
