import test from 'node:test';
import assert from 'node:assert/strict';
import { steamBigPictureOpen, installSteamContext } from '../plugin/steam-context.js';

function fixture() {
  let big = true, callback, tick, disposed = false;
  const calls = [];
  const env = { SteamUIStore: { MainWindowVisible: { Value: true }, MainRunningAppID: 0,
    WindowStore: { MainWindowInstance: { IsMainGamepadUIWindow: () => big }, GamepadUIMainWindowInstance: {} } } };
  const options = { frontendId: 'current', ready: Promise.resolve(), reaction: (read, changed) => {
    callback = changed; return () => { disposed = true; };
  }, setInterval: fn => { tick = fn; return 1; }, clearInterval: () => { tick = undefined; } };
  const bridge = installSteamContext(env, {}, async (...args) => calls.push(args), options);
  return { env, calls, bridge, change: value => { big = value; callback(); }, heartbeat: () => tick?.(), disposed: () => disposed };
}

test('Big Picture ownership survives minimized UI, gameplay and loss of focus', () => {
  const f = fixture();
  f.env.SteamUIStore.MainWindowVisible.Value = false;
  f.env.SteamUIStore.MainRunningAppID = 1604380;
  f.env.SteamUIStore.IsGamepadUIWindowActive = () => false;
  assert.equal(steamBigPictureOpen(f.env), true);
  f.bridge.dispose();
});
test('normal Steam with a stale gamepad instance does not acquire ownership', async () => {
  const f = fixture();
  await f.bridge.sync();
  f.change(false); await f.bridge.sync();
  assert.equal(f.calls.at(-1)[3], false);
  assert.equal(steamBigPictureOpen({}), false);
  f.bridge.dispose();
});
test('heartbeats renew Steam context without touching mapping settings', async () => {
  const f = fixture(); await f.bridge.sync();
  const before = f.calls.at(-1)[2]; f.heartbeat(); await f.bridge.sync();
  assert.equal(f.calls.at(-1)[3], true);
  assert.ok(f.calls.at(-1)[2] > before);
  assert.ok(f.calls.every(c => c[0] === 'set_steam_context' && c[1] === 'current'));
  f.bridge.dispose();
});
test('a closed UI queued before bridge readiness cannot replay the previous open signal', async () => {
  let ready; const pending = new Promise(resolve => { ready = resolve; });
  let big = true, tick; const calls = [];
  const env = { SteamUIStore: { WindowStore: { MainWindowInstance: { IsMainGamepadUIWindow: () => big }, GamepadUIMainWindowInstance: {} } } };
  const bridge = installSteamContext(env, {}, async (...args) => calls.push(args), { frontendId: 'f', ready: pending,
    setInterval: fn => { tick = fn; }, clearInterval() {} });
  big = false; tick(); ready(); await bridge.sync();
  assert.equal(calls.length, 1); assert.equal(calls[0][3], false);
  bridge.dispose();
});
test('disposed frontend removes observers and cancels pending reports', async () => {
  const f = fixture(); f.bridge.dispose(); await f.bridge.sync();
  assert.equal(f.disposed(), true); assert.deepEqual(f.calls, []);
});
