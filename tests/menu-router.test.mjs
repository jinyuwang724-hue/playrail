import test from 'node:test';
import assert from 'node:assert/strict';
import { createMenuRouter } from '../plugin/menu-router.js';

test('scaler yields before native menu opens and releases after it closes', async () => {
  const f = fixture(), changes = [];
  const router = createMenuRouter(f.env, f.ui, { onMenuVisibility: async (...args) => changes.push(args) });
  await router.execute('QuickAccess');
  assert.deepEqual(changes.filter(x => x[0] === 'overlay')[0], ['overlay',true]);
  assert.deepEqual(changes.filter(x => x[0] === 'overlay').at(-1), ['overlay',true]);
  await router.execute('QuickAccess');
  assert.deepEqual(changes.filter(x => x[0] === 'overlay').at(-1), ['overlay',false]);
  router.dispose();
});
test('fallback menu owns a separate scaling blocker until focus is restored', async () => {
  const f = fixture({ fallback: true }), changes = [];
  const router = createMenuRouter(f.env, f.ui, { onMenuVisibility: async (...args) => changes.push(args) });
  await router.execute('MainMenu'); assert.deepEqual(changes.filter(x => x[0] === 'focus'), [['focus',true]]);
  await router.execute('MainMenu'); assert.deepEqual(changes.filter(x => x[0] === 'focus').at(-1), ['focus',false]);
  router.dispose();
});

function fixture({ app = 2825300, gameID = String(app), signal = true, fallback = false, grant = true } = {}) {
  const callbacks = new Set(), browserCallbacks = new Set(), runningCallbacks = new Set(), calls = [];
  const steamBrowserOwner = () => {};
  let nativeBrowserOwner = steamBrowserOwner, nativeRegistrations = 0;
  const info = { appID: app, unPID: 123, nBrowserID: 456, gameID };
  let composition = 0, foreignAcquisitions = 0, infoReads = 0;
  const emit = active => callbacks.forEach(fn => fn(info.unPID, info.appID, active));
  class Menu {
    side = 0; tab = 999;
    GetOpenSideMenu() { return this.side; }
    GetQuickAccessTab() { return this.tab; }
    OpenSideMenu(side) { this.side = side; }
    OpenQuickAccessMenu(tab) { this.tab = tab; this.OpenSideMenu(2); }
    CloseSideMenus() { this.side = 0; }
  }
  const main = new Menu(), overlay = new Menu();
  const rawOpen = main.OpenSideMenu;
  main.OpenSideMenu = function(side) { foreignAcquisitions++; rawOpen.call(this, side); };
  overlay.OpenSideMenu = function(side) { this.side = side; composition = 2; if (signal) emit(true); };
  overlay.CloseSideMenus = function() { this.side = 0; composition = 0; if (signal) emit(false); };
  const instance = { MenuStore: overlay, IsGamepadUIOverlayWindow: () => true,
    params: { browserInfo: { m_unAppID: app, m_unPID: 123, m_nBrowserID: 456, m_gameID: gameID } },
    BrowserWindow: { document: { body: {} } }, CompositionStateStore: { GetCompositionState: () => composition } };
  let nextFrame = 0; const frames = new Map(), layoutEvents = [];
  const mainBrowser = { Event: class { constructor(type) { this.type = type; } },
    requestAnimationFrame(fn) { frames.set(++nextFrame, fn); return nextFrame; },
    cancelAnimationFrame(id) { frames.delete(id); }, dispatchEvent(event) { layoutEvents.push(event.type); } };
  const windowStore = { GamepadUIMainWindowInstance: { MenuStore: main, BrowserWindow: mainBrowser }, OverlayWindows: [instance], GetOverlayInstance: () => instance };
  const env = { SteamUIStore: { MainRunningAppID: app, WindowStore: windowStore }, SteamClient: { Overlay: {
    GetOverlayBrowserInfo: async () => { infoReads++; return fallback ? [] : [info]; },
    RegisterForOverlayActivated: fn => { callbacks.add(fn); return { unregister: () => callbacks.delete(fn) }; },
    RegisterOverlayBrowserInfoChanged: fn => { nativeRegistrations++; nativeBrowserOwner = fn; return { unregister: () => { nativeBrowserOwner = undefined; } }; },
    SetOverlayState: async (id, state) => { calls.push({ id, state }); if (state === 0) overlay.CloseSideMenus(); },
  } }, fetch: async (url, options) => {
    const action = url.split('/').at(-1), body = JSON.parse(options.body);
    calls.push({ action, ...body });
    return { ok: true, json: async () => ({ ok: action !== 'acquire' || grant, reason: grant ? 'Exact game restored.' : 'No live game window.' }) };
  }, overlayStore: Object.fromEntries(['RegisterForBrowserCreated', 'RegisterForBrowserClosed', 'RegisterForBrowserUpdated'].map(method =>
    [method, fn => { browserCallbacks.add(fn); return { unregister: () => browserCallbacks.delete(fn) }; }])) };
  const ui = { afterPatch(object, method, handler) {
    const original = object[method];
    const patched = function(...args) { return handler(args, original.apply(this, args)); };
    object[method] = patched;
    return { unpatch() { if (object[method] === patched) object[method] = original; } };
  } };
  return { env, ui, info, instance, main, overlay, windowStore, calls, emit, callbacks,
    browserCallbacks, infoReads: () => infoReads, browserChanged: () => browserCallbacks.forEach(fn => fn()),
    nativeRegistrations: () => nativeRegistrations, steamBrowserOwner, nativeBrowserOwner: () => nativeBrowserOwner,
    foreignAcquisitions: () => foreignAcquisitions, runningCallbacks, frames, layoutEvents,
    paint() { const pending = [...frames.values()]; frames.clear(); pending.forEach(fn => fn()); },
    changeApp(next) { env.SteamUIStore.MainRunningAppID = next; runningCallbacks.forEach(fn => fn()); },
    router: () => createMenuRouter(env, ui, { timeout: 80, defer: fn => mainBrowser.requestAnimationFrame(fn), reaction(_read, effect) {
      runningCallbacks.add(effect); return () => runningCallbacks.delete(effect);
    } }) };
}

test('returning from a game schedules one layout refresh without changing menus', () => {
  const f = fixture(), router = f.router(); f.main.side = 1;
  f.changeApp(0); f.browserChanged(); f.browserChanged();
  assert.equal(f.frames.size, 1); f.paint();
  assert.deepEqual(f.layoutEvents, ['resize']); assert.equal(f.main.side, 1);
  assert.equal(f.calls.length, 0); router.dispose();
});

test('a new game or plugin unload cancels a stale return refresh', () => {
  for (const unload of [false, true]) {
    const f = fixture(), router = f.router(); f.changeApp(0);
    if (unload) router.dispose(); else f.changeApp(999);
    f.paint(); assert.deepEqual(f.layoutEvents, []); router.dispose();
  }
});

test('production return refresh uses a microtask even when the main window cannot paint', async () => {
  const f = fixture(); let changed;
  const router = createMenuRouter(f.env, f.ui, { reaction(_read, effect) { changed = effect; return () => {}; } });
  f.env.SteamUIStore.MainRunningAppID = 0; changed();
  assert.equal(f.layoutEvents.length, 0);
  await Promise.resolve();
  assert.deepEqual(f.layoutEvents, ['resize']); assert.equal(f.frames.size, 0);
  router.dispose();
});

test('game exit releases its fallback lease and closes only its own menu without a button press', async () => {
  const f = fixture({ fallback: true }), router = f.router();
  await router.execute('QuickAccess'); f.changeApp(0);
  await Promise.resolve();
  assert.equal(f.main.side, 0);
  assert.deepEqual(f.calls.map(c => c.action), ['acquire', 'release']);
  assert.equal(f.calls[0].requestId, f.calls[1].requestId);
  router.dispose(); assert.equal(f.runningCallbacks.size, 0);
});

test('game change never closes a menu subsequently opened by another owner', async () => {
  const f = fixture({ fallback: true }), router = f.router();
  await router.execute('QuickAccess'); f.main.side = 1; f.changeApp(999);
  await Promise.resolve();
  assert.equal(f.main.side, 1);
  assert.deepEqual(f.calls.map(c => c.action), ['acquire', 'release']);
  router.dispose();
});

test('game exit during focus acquisition cannot reopen the departed game menu', async () => {
  const f = fixture({ fallback: true }); let started, finish;
  const began = new Promise(resolve => { started = resolve; });
  f.env.fetch = async (url, options) => {
    const action = url.split('/').at(-1); f.calls.push({ action, ...JSON.parse(options.body) });
    if (action === 'acquire') { started(); return new Promise(resolve => { finish = () => resolve({ ok: true, json: async () => ({ ok: true }) }); }); }
    return { ok: true, json: async () => ({ ok: true }) };
  };
  const router = f.router(), result = router.execute('QuickAccess');
  await began; f.changeApp(0); finish();
  assert.equal((await result).ok, false); assert.equal(f.main.side, 0);
  assert.deepEqual(f.calls.map(c => c.action), ['acquire', 'release']); router.dispose();
});

test('in-game menus target the exact overlay and close back to the game', async () => {
  const f = fixture(), router = f.router();
  assert.equal((await router.execute('QuickAccess')).path, 'overlay');
  assert.equal(f.overlay.side, 2); assert.equal(f.main.side, 0); assert.equal(f.overlay.tab, 999);
  assert.equal((await router.execute('MainMenu')).ok, true); assert.equal(f.overlay.side, 1);
  assert.equal((await router.execute('MainMenu')).ok, true); assert.equal(f.overlay.side, 0);
  assert.deepEqual(f.calls, [{ id: '2825300', state: 0 }]);
  router.dispose(); assert.equal(f.callbacks.size, 0);
});

test('a changed menu without native overlay activation is not reported as success', async () => {
  const f = fixture({ signal: false }), router = f.router();
  assert.equal((await router.execute('QuickAccess')).ok, false); router.dispose();
});

test('unsigned shortcut app IDs and 64-bit game IDs remain exact', async () => {
  const f = fixture({ app: 4000000000, gameID: '17179869184858993408' });
  f.env.SteamUIStore.MainRunningAppID = 4000000000 | 0;
  const router = f.router();
  assert.equal((await router.execute('MainMenu')).ok, true);
  assert.equal((await router.execute('MainMenu')).ok, true);
  assert.equal(f.calls[0].id, '17179869184858993408'); router.dispose();
});

test('ambiguous or incomplete overlay identities never touch the main window', async () => {
  for (const mode of ['ambiguous', 'incomplete', 'wrong-window']) {
    const f = fixture();
    if (mode === 'ambiguous') f.env.SteamClient.Overlay.GetOverlayBrowserInfo = async () => [f.info, { ...f.info, unPID: 999 }];
    if (mode === 'incomplete') f.info.nBrowserID = undefined;
    if (mode === 'wrong-window') f.instance.params.browserInfo.m_unPID = 999;
    const router = f.router();
    assert.equal((await router.execute('QuickAccess')).ok, false);
    assert.equal(f.main.side, 0); assert.equal(f.overlay.side, 0); router.dispose();
  }
});

test('closing a cold overlay cancels its pending open', async () => {
  const f = fixture();
  f.windowStore.GetOverlayInstance = () => undefined; f.windowStore.OverlayWindows = [];
  f.env.SteamClient.Overlay.SetOverlayState = async (id, state) => { f.calls.push({ id, state }); if (state === 2) f.emit(false); };
  const router = f.router();
  assert.equal((await router.execute('QuickAccess')).ok, false);
  assert.equal(f.overlay.side, 0); assert.deepEqual(f.calls.map(c => c.state), [2, 0]); router.dispose();
});

test('two concurrent presses serialize into open then close', async () => {
  const f = fixture(), router = f.router();
  const result = await Promise.all([router.execute('QuickAccess'), router.execute('QuickAccess')]);
  assert.equal(result.every(r => r.ok), true); assert.equal(f.overlay.side, 0); router.dispose();
});

test('plugin load and unload preserve Steam exclusive browser callback', async () => {
  const f = fixture(), router = f.router();
  assert.equal(f.browserCallbacks.size, 3);
  await router.execute('QuickAccess'); await router.execute('QuickAccess');
  f.browserChanged(); await router.execute('MainMenu');
  assert.equal(f.nativeRegistrations(), 0);
  assert.equal(f.nativeBrowserOwner(), f.steamBrowserOwner);
  router.dispose(); assert.equal(f.browserCallbacks.size, 0);
  assert.equal(f.nativeBrowserOwner(), f.steamBrowserOwner);
});

test('each press rechecks identity even without Steam fan-out subscriptions', async () => {
  const f = fixture(); delete f.env.overlayStore;
  const router = f.router();
  await router.execute('QuickAccess'); await router.execute('QuickAccess');
  assert.equal(f.infoReads(), 2);
  f.info.nBrowserID = 999;
  assert.equal((await router.execute('MainMenu')).ok, false);
  assert.equal(f.main.side, 0); router.dispose();
});

test('no-overlay fallback owns one exact focus request and restores it on menu close', async () => {
  const f = fixture({ fallback: true }), router = f.router();
  assert.equal((await router.execute('QuickAccess')).path, 'playhub-window');
  assert.equal((await router.execute('MainMenu')).ok, true);
  assert.equal(f.foreignAcquisitions(), 0);
  assert.equal((await router.execute('MainMenu')).ok, true);
  assert.deepEqual(f.calls.map(c => c.action), ['acquire', 'close']);
  assert.equal(f.calls[0].requestId, f.calls[1].requestId); router.dispose();
});

test('denied focus acquisition leaves all menus alone', async () => {
  const f = fixture({ fallback: true, grant: false }), router = f.router();
  assert.equal((await router.execute('MainMenu')).ok, false);
  assert.equal(f.main.side, 0); assert.deepEqual(f.calls.map(c => c.action), ['acquire', 'release']); router.dispose();
});

test('mode changes release the focus request without returning to a game', async () => {
  const f = fixture({ fallback: true }), router = f.router();
  await router.execute('MainMenu'); router.modeChanged('Desktop');
  await Promise.resolve(); await Promise.resolve();
  assert.deepEqual(f.calls.map(c => c.action), ['acquire', 'release']);
  f.main.CloseSideMenus(); assert.equal(f.calls.length, 2); router.dispose();
});

test('disposal during focus acquisition prevents a delayed menu open', async () => {
  const f = fixture({ fallback: true }); let resolve;
  const started = new Promise(r => { resolve = r; }); let finish;
  f.env.fetch = async (url, options) => {
    const action = url.split('/').at(-1); f.calls.push({ action, ...JSON.parse(options.body) });
    if (action === 'acquire') { resolve(); return new Promise(r => { finish = () => r({ ok: true, json: async () => ({ ok: true }) }); }); }
    return { ok: true, json: async () => ({ ok: true }) };
  };
  const router = f.router(), result = router.execute('MainMenu');
  await started; router.dispose(); finish();
  assert.equal((await result).ok, false); assert.equal(f.main.side, 0);
  assert.deepEqual(f.calls.map(c => c.action), ['acquire', 'release']);
});
