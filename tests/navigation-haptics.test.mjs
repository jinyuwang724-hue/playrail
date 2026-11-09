import test from 'node:test';
import assert from 'node:assert/strict';
import { installNavigationHaptics, navigationUIActive } from '../plugin/navigation-haptics.js';

const request = { vendorId: 3504, productId: 6401, pattern: 'moveLeft', side: 0,
  magnitude: .3726774465009565, requestId: 100 };
const endpoint = 'http://127.0.0.1:47991/dash/haptic';
function fixture(enabled = true, handler) {
  const forwarded = [], calls = [];
  const original = async (...args) => { forwarded.push(args); return new Response('{"path":"original"}'); };
  const listeners = new Set();
  const env = { fetch: original, Response, SteamUIStore: {
    MainRunningAppID: 0, IsGamepadUIWindowActive: () => true,
    WindowStore: { MainWindowInstance: { IsMainGamepadUIWindow: () => true } },
    MainWindowVisible: { Value: true, Subscribe(fn) { listeners.add(fn); return { Unsubscribe: () => listeners.delete(fn) }; } },
  } };
  const call = async (...args) => { calls.push(args); return handler ? handler(...args) : { handled: true, path: 'claw-xinput' }; };
  const owner = installNavigationHaptics(env, call, { enabled: () => enabled, frontendId: 'current' });
  const pulses = () => calls.filter(([method]) => method === 'play_navigation_haptic');
  const visible = value => { env.SteamUIStore.MainWindowVisible.Value = value; listeners.forEach(fn => fn(value)); };
  return { env, original, forwarded, calls, owner, pulses, visible, listeners };
}
const post = (f, value = request, url = endpoint, extra = {}) => f.env.fetch(url,
  { method: 'POST', body: JSON.stringify(value), ...extra });

test('Claw requests reach the toolbox with original pattern, side and magnitude', async () => {
  const f = fixture();
  assert.equal((await (await post(f)).json()).path, 'claw-xinput');
  assert.equal(f.forwarded.length, 0);
  for (const key of Object.keys(request)) assert.equal(f.pulses()[0][1][key], request[key]);
  assert(f.pulses()[0][1].expiresAtMs > Date.now());
  assert.equal(f.pulses()[0][1].frontendId, 'current');
  assert.equal(f.pulses()[0][1].revision, 1);
  f.owner.dispose();
});
test('Other pads, unrelated endpoints and disabled compatibility keep original behavior', async () => {
  const f = fixture();
  await post(f, { ...request, vendorId: 1118 });
  await post(f, request, 'https://example.com/dash/haptic');
  await post(f, request, 'http://127.0.0.1:47991/dash/status');
  assert.equal(f.calls.length, 0); assert.equal(f.forwarded.length, 3);
  const off = fixture(false); await post(off);
  assert.equal(off.pulses().length, 0); assert.equal(off.forwarded.length, 1);
});
test('Unload restores fetch, and reload creates one compatibility owner', async () => {
  const f = fixture();
  const second = installNavigationHaptics(f.env, async (...args) => {
    f.calls.push(args); return { handled: true };
  }, { enabled: () => true, frontendId: 'current' });
  f.owner.dispose();
  await post(f);
  assert.equal(f.pulses().length, 1);
  second.dispose(); assert.equal(f.env.fetch, f.original);
  await post(f); assert.equal(f.pulses().length, 1);
  assert.equal(f.forwarded.filter(([, init]) => JSON.parse(init.body).stop !== true).length, 1);
  assert.equal(f.listeners.size, 0);
});
test('An already aborted navigation does not generate output', async () => {
  const f = fixture(); const controller = new AbortController(); controller.abort();
  await assert.rejects(post(f, request, endpoint, { signal: controller.signal }), { name: 'AbortError' });
  assert.equal(f.calls.length, 0);
});
test('Transport failure cannot launch unmanaged feedback, and a recovered bridge can retry', async () => {
  const f = fixture(); let failed = false, unavailable = true;
  installNavigationHaptics(f.env, async () => { throw new Error('Unavailable'); },
    { enabled: () => true, frontendId: 'current', onError: () => { failed = true; } });
  assert.equal((await (await post(f)).json()).path, 'bridge-unavailable'); assert(failed);
  assert.equal(f.forwarded.filter(([, init]) => JSON.parse(init.body).stop !== true).length, 0);
  const retry = fixture(true, async () => { if (unavailable) throw new Error('Offline'); return { handled: true, path: 'claw-xinput' }; });
  await post(retry); unavailable = false;
  assert.equal((await (await post(retry)).json()).path, 'claw-xinput');
  const outputFailure = fixture(true, async method => method === 'set_navigation_context'
    ? { handled: true } : { handled: false, path: 'xinput-unavailable' });
  const result = await (await post(outputFailure)).json();
  assert.equal(result.handled, true); assert.equal(result.path, 'xinput-unavailable');
  assert.equal(outputFailure.forwarded.length, 0);
});
test('Stop requests stop compatibility and original output together', async () => {
  const f = fixture(); await post(f, { stop: true, requestId: 101 });
  assert.equal(f.calls.length, 1); assert.equal(f.forwarded.length, 1);
  assert.equal(f.calls[0][1].stop, true);
});
test('A Request object receives the same compatibility behavior', async () => {
  const f = fixture();
  await f.env.fetch(new Request(endpoint, { method: 'POST', body: JSON.stringify(request) }));
  assert.equal(f.pulses().length, 1); assert.equal(f.forwarded.length, 0);
});

test('Ordinary Steam, hidden Big Picture and games never generate navigation rumble', async () => {
  for (const change of [
    env => { env.SteamUIStore.WindowStore.MainWindowInstance.IsMainGamepadUIWindow = () => false; },
    env => { env.SteamUIStore.IsGamepadUIWindowActive = () => false; },
    env => { env.SteamUIStore.MainWindowVisible.Value = false; },
    env => { env.SteamUIStore.MainRunningAppID = 123; },
    env => { delete env.SteamUIStore; },
  ]) {
    const f = fixture(); change(f.env);
    assert.equal(navigationUIActive(f.env), false);
    assert.equal((await (await post(f)).json()).path, 'navigation-inactive');
    assert.equal(f.pulses().length, 0); assert.equal(f.forwarded.length, 0);
  }
});

test('Desktop switch drains both outputs before Playhub can kill the plugin', async () => {
  const f = fixture(); await post(f);
  const response = await post(f, {}, 'http://127.0.0.1:47991/mode/desktop/switch');
  assert.equal(response.status, 200);
  const inactive = f.calls.find(([method, , , active]) => method === 'set_navigation_context' && active === false);
  assert(inactive);
  assert.equal(JSON.parse(f.forwarded[0][1].body).stop, true);
  assert.equal(f.forwarded[1][0], 'http://127.0.0.1:47991/mode/desktop/switch');
});

test('A pulse waiting on the bridge is discarded when Desktop transition starts', async () => {
  let release;
  const f = fixture(true, async (method, id, revision, active) => {
    if (method === 'set_navigation_context' && active === true) await new Promise(resolve => { release = resolve; });
    return { handled: true };
  });
  const pulse = post(f);
  await new Promise(resolve => setImmediate(resolve));
  const exit = post(f, {}, 'http://127.0.0.1:47991/mode/desktop/switch');
  release();
  assert.equal((await (await pulse).json()).path, 'navigation-inactive');
  await exit;
  assert.equal(f.pulses().length, 0);
});

test('A delayed failed pulse never falls back after Desktop transition', async () => {
  let rejectPulse;
  const f = fixture(true, async method => {
    if (method === 'play_navigation_haptic') return new Promise((_, reject) => { rejectPulse = reject; });
    return { handled: true };
  });
  const pulse = post(f);
  await new Promise(resolve => setImmediate(resolve));
  await post(f, {}, 'http://127.0.0.1:47991/mode/desktop/switch');
  rejectPulse(new Error('Plugin killed'));
  assert.equal((await (await pulse).json()).path, 'bridge-unavailable');
  assert.equal(f.forwarded.filter(([url, init]) => url === endpoint && JSON.parse(init.body).stop !== true).length, 0);
});

test('Visibility uses an existing subscription and allows a new Big Picture session', async () => {
  const f = fixture(); await post(f);
  f.visible(false); await f.owner.sync();
  assert.equal(f.calls.at(-1)[3], false);
  assert.equal((await (await post(f)).json()).path, 'navigation-inactive');
  f.visible(true);
  assert.equal((await (await post(f)).json()).path, 'claw-xinput');
  assert.equal(f.pulses().at(-1)[1].revision, 3);
});

test('Disposal during asynchronous Request parsing suppresses the pending pulse', async () => {
  const f = fixture(); let release;
  const pending = f.env.fetch({ url: endpoint, method: 'POST', clone: () => ({
    text: () => new Promise(resolve => { release = resolve; }),
  }) });
  f.owner.dispose(); release(JSON.stringify(request));
  assert.equal((await (await pending).json()).path, 'navigation-inactive');
  assert.equal(f.pulses().length, 0);
});

test('An unresponsive frontend startup cannot block returning to Desktop', async () => {
  const f = fixture();
  installNavigationHaptics(f.env, async () => { throw new Error('Must wait for startup'); }, {
    enabled: () => true, frontendId: 'new', ready: () => new Promise(() => {}),
  });
  const result = await post(f, {}, 'http://127.0.0.1:47991/mode/desktop/switch');
  assert.equal(result.status, 200);
  assert(f.forwarded.some(([url]) => url.endsWith('/mode/desktop/switch')));
});
