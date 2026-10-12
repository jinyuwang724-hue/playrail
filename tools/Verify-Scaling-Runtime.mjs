import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createInterface } from 'node:readline';

const root = path.resolve(import.meta.dirname, '..');
const targets = await (await fetch('http://127.0.0.1:8080/json/list')).json();
const socket = new WebSocket(targets.find(x => x.title === 'SharedJSContext').webSocketDebuggerUrl);
await once(socket, 'open');
let serial = 0;
const pending = new Map();
socket.addEventListener('message', event => {
  const message = JSON.parse(event.data), waiter = pending.get(message.id);
  if (!waiter) return;
  clearTimeout(waiter.timer); pending.delete(message.id);
  const error = message.error || message.result?.exceptionDetails;
  if (error) waiter.reject(Error(error.exception?.description ?? error.message ?? error.text));
  else waiter.resolve(message.result.result.value);
});
const evaluate = expression => new Promise((resolve, reject) => {
  const id = ++serial;
  const timer = setTimeout(() => { pending.delete(id); reject(Error('Runtime check timed out')); }, 25000);
  pending.set(id, { resolve, reject, timer });
  socket.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }));
});
const call = (method, ...args) => evaluate(`window.DeckyBackend.call("loader/call_plugin_method","Claw 掌机工具箱",${JSON.stringify(method)}${args.map(x => ',' + JSON.stringify(x)).join('')})`);
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const before = await call('get_scaling_state');
const pluginVersion = await evaluate('window.DeckyPluginLoader.plugins.find(p=>p.name==="Claw 掌机工具箱").version');
assert.equal(before.enabled, false, 'Never interrupt an existing game capture for this test');
await evaluate('(()=>{SteamUIStore.WindowStore.GamepadUIMainWindowInstance.MenuStore.CloseSideMenus();return true})()');
// This owned diagnostic is not registered as a Steam game. Temporarily let it own
// the foreground instead of letting the Big Picture home window cover it.
await evaluate('SteamUIStore.WindowStore.GamepadUIMainWindowInstance.BrowserWindow.SteamClient.Window.Minimize()');
const game = spawn(path.join(root, 'artifacts/scaling-validation/ScalingTestGame.exe'), [], { windowsHide: true });
let focusLog = ''; game.stderr.on('data', data => { focusLog += data; });
const lines = createInterface({ input: game.stdout });
const [line] = await once(lines, 'line');
const hwnd = Number(line), combinations = [];
assert(hwnd > 0);
try {
  await delay(100);
  const windows = await call('get_scaling_targets');
  assert(windows.some(x => x.hwnd === hwnd && x.width === 640 && x.height === 480), JSON.stringify({ hwnd, windows }));
  const settings = { enabled: true, mode: 'auto', filter: 'linear', sharpness: .5, target: hwnd };
  for (const mode of ['auto','integer','fit','stretch','fill']) {
    for (const filter of ['linear','nearest','fsr','nis']) {
      Object.assign(settings, { mode, filter });
      await call('set_scaling_settings', settings);
      game.stdin.write('focus\n');
      let state;
      for (let attempt = 0; attempt < 30; attempt++) {
        await delay(100); state = await call('get_scaling_state');
        if (state.active && state.effectiveFilter === filter) break;
      }
      assert.equal(state.enabled, true, state.error);
      assert.equal(state.active, true, JSON.stringify(state) + '\n' + focusLog);
      assert.equal(state.effectiveFilter, filter);
      assert.equal(state.inputWidth, 640); assert.equal(state.inputHeight, 480);
      combinations.push({ mode, filter, frames: state.frames, output: [state.outputWidth, state.outputHeight] });
    }
  }
  await call('set_scaling_pause', 'overlay', true);
  assert.equal((await call('get_scaling_state')).active, false);
  await call('set_scaling_pause', 'panel', true);
  await call('set_scaling_pause', 'overlay', false);
  assert.equal((await call('get_scaling_state')).paused, true);
  await call('set_scaling_pause', 'panel', false);
  game.stdin.write('focus\n'); await delay(250);
  assert.equal((await call('get_scaling_state')).active, true);
  // Exercise the real mounted plugin and actual menu closure, not a fabricated pause command.
  await evaluate(`(()=>{ window.DeckyPluginLoader.deckyState.setActivePlugin("Claw 掌机工具箱");
    SteamUIStore.WindowStore.GamepadUIMainWindowInstance.BrowserWindow.SteamClient.Window.BringToFront();
    SteamUIStore.WindowStore.GamepadUIMainWindowInstance.MenuStore.OpenQuickAccessMenu(DFL.QuickAccessTab.Decky); return true; })()`);
  await delay(400);
  assert.equal((await call('get_scaling_state')).paused, true, 'Visible QAM did not pause capture');
  await evaluate('(()=>{SteamUIStore.WindowStore.GamepadUIMainWindowInstance.MenuStore.CloseSideMenus();return true})()');
  await evaluate('SteamUIStore.WindowStore.GamepadUIMainWindowInstance.BrowserWindow.SteamClient.Window.Minimize()');
  game.stdin.write('focus\n');
  let resumed;
  for (let attempt = 0; attempt < 30; attempt++) {
    await delay(100); resumed = await call('get_scaling_state');
    if (!resumed.paused && resumed.active) break;
  }
  assert.equal(resumed.paused, false, 'Hidden but mounted QAM kept capture paused');
  assert.equal(resumed.active, true, 'Closing actual QAM did not recover output');
  game.stdin.write('close\n'); await once(game, 'exit');
  await delay(200);
  const ended = await call('get_scaling_state');
  assert.equal(ended.enabled, false); assert.equal(ended.active, false);
  const report = { checkedAt: new Date().toISOString(), plugin: pluginVersion, combinations,
    realPluginCapture: true, actualQamCloseResumes: true, menuPauseAggregation: true, gameExitReleased: true };
  fs.writeFileSync(path.join(root, 'artifacts/scaling-validation/plugin-runtime.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report));
} finally {
  await evaluate('(()=>{SteamUIStore.WindowStore.GamepadUIMainWindowInstance.MenuStore.CloseSideMenus();return true})()').catch(() => {});
  await call('set_scaling_pause', 'overlay', false).catch(() => {});
  await call('set_scaling_pause', 'panel', false).catch(() => {});
  await call('set_scaling_settings', { enabled: false, mode: before.mode, filter: before.filter, sharpness: before.sharpness, target: 0 }).catch(() => {});
  game.stdin.end();
  await evaluate('SteamUIStore.WindowStore.GamepadUIMainWindowInstance.BrowserWindow.SteamClient.Window.BringToFront()').catch(() => {});
  socket.close();
}
