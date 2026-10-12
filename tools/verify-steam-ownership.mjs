// Exercise Steam's native window lifecycle through its existing debug context.
// This does not inject hardware button events or change Playhub's mode.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
const run = promisify(execFile);
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function evaluate(expression) {
  const target = (await (await fetch('http://127.0.0.1:8080/json/list')).json()).find(t => t.title === 'SharedJSContext');
  assert.ok(target, 'Steam context missing');
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.addEventListener('open',resolve,{once:true}); ws.addEventListener('error',reject,{once:true}); });
  try {
    const result = await new Promise((resolve, reject) => {
      const timer=setTimeout(() => reject(new Error('Steam RPC timeout')),8000);
      ws.addEventListener('message', e => { const r=JSON.parse(e.data); if(r.id===1){clearTimeout(timer);resolve(r);} });
      ws.send(JSON.stringify({id:1,method:'Runtime.evaluate',params:{expression,awaitPromise:true,returnByValue:true}}));
    });
    if(result.result?.exceptionDetails) throw new Error(result.result.exceptionDetails.exception?.description);
    return result.result.result.value;
  } finally { ws.close(); }
}
const read = () => evaluate(`(async()=>({mode:SteamUIStore.MainInstanceUIMode,gamepad:!!SteamUIStore.WindowStore.GamepadUIMainWindowInstance,mainGamepad:SteamUIStore.WindowStore.MainWindowInstance?.IsMainGamepadUIWindow?.(),visible:SteamUIStore.MainWindowVisible.Value,state:await window.DeckyBackend.call('loader/call_plugin_method','Claw 掌机工具箱','get_state')}))()`);
const records=[];
async function checkpoint(name, taken) {
  const deadline=Date.now()+18000;
  let value;
  do {
    value=await read();
    if(value.state.status.released===taken && value.state.status.helperRunning===!taken && !value.state.status.error) {
      records.push({name,at:new Date().toISOString(),...value});
      console.log(JSON.stringify({name,mode:value.state.status.mode,released:value.state.status.released,helperRunning:value.state.status.helperRunning}));
      return value;
    }
    await sleep(250);
  } while(Date.now()<deadline);
  throw new Error(`${name}: ${JSON.stringify(value)}`);
}
const before=await read();
assert.equal(before.mainGamepad,true,'Start with Big Picture open');
assert.equal(before.state.settings.enabled,true,'Automatic ownership must be enabled');
const settings=before.state.settings;
const open=async()=>run('powershell.exe',['-NoProfile','-Command',"Start-Process -FilePath 'C:\\Software\\steam.exe' -ArgumentList 'steam://open/bigpicture' -WindowStyle Hidden"],{windowsHide:true});
try {
  await checkpoint('desktop-playhub-big-picture-open',true);
  await evaluate('SteamUIStore.WindowStore.GamepadUIMainWindowInstance.BrowserWindow.SteamClient.Window.Minimize()');
  // More than the 15 second lease proves that background reports continue.
  await sleep(18000);
  const minimized=await checkpoint('big-picture-minimized-18s',true);
  assert.equal(minimized.visible,false);
  await evaluate('SteamUIStore.WindowStore.GamepadUIMainWindowInstance.BrowserWindow.SteamClient.Window.BringToFront()');
  await evaluate('SteamClient.UI.ExitBigPictureMode()');
  const closed=await checkpoint('big-picture-exited',false);
  assert.equal(closed.mainGamepad,false);
  assert.equal(closed.state.status.restorePending,false);
  await open();
  await checkpoint('big-picture-reopened',true);
  assert.deepEqual((await read()).state.settings,settings);
} finally {
  const value=await read().catch(()=>null);
  if(!value?.mainGamepad) await open();
  await fs.mkdir('artifacts/steam-ownership',{recursive:true});
  await fs.writeFile('artifacts/steam-ownership/lifecycle.json',JSON.stringify(records,null,2));
}
