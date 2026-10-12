// Exercise only game controls against the running Steam client; never launch a game.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const artifactDir = path.join(root, 'artifacts', 'game-name-verification');
fs.mkdirSync(artifactDir, { recursive: true });
const temporaryExecutable = !process.argv[2] || process.argv[2] === '--temporary-executable';
const testExe = temporaryExecutable ? path.join(artifactDir, 'claw-name-verification.exe') : process.argv[2];
const collectionName = process.argv[3] || null;
const realNavigation = process.argv[4] === '--real-navigation';
if (temporaryExecutable) fs.copyFileSync(path.join(process.env.WINDIR, 'System32', 'notepad.exe'), testExe);
if (!fs.statSync(testExe).isFile()) throw new Error('Test executable unavailable');
const factory = fs.readFileSync(path.join(root, 'plugin', 'game-add.js'), 'utf8').replace('export function', 'function');
const harness = fs.readFileSync(path.join(root, 'tests', 'helpers', 'game-adder-harness.mjs'), 'utf8').replace('export function', 'function');
const targets = await (await fetch('http://127.0.0.1:8080/json/list')).json();
const target = targets.find(item => item.title === 'SharedJSContext');
if (!target) throw new Error('Steam shared context unavailable');
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });
let requestId = 0;
function evaluate(expression) {
  const id = ++requestId;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { ws.removeEventListener('message', receive); reject(new Error('Steam test timed out')); }, 40000);
    function receive(event) {
      const message = JSON.parse(event.data);
      if (message.id !== id) return;
      clearTimeout(timer); ws.removeEventListener('message', receive);
      if (message.error) reject(new Error(message.error.message));
      else if (message.result?.exceptionDetails) reject(new Error(message.result.exceptionDetails.exception?.description || message.result.exceptionDetails.text));
      else resolve(message.result?.result?.value);
    }
    ws.addEventListener('message', receive);
    ws.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }));
  });
}
function readShortcuts() {
  const bytes = fs.readFileSync('C:/Software/userdata/836107557/config/shortcuts.vdf');
  let offset = 0;
  function string() {
    const end = bytes.indexOf(0, offset);
    if (end < 0) throw new Error('Invalid VDF string');
    const result = bytes.toString('utf8', offset, end); offset = end + 1; return result;
  }
  function object() {
    const result = {};
    while (offset < bytes.length) {
      const type = bytes[offset++];
      if (type === 8) return result;
      const key = string();
      if (type === 0) result[key] = object();
      else if (type === 1) result[key] = string();
      else if (type === 2) { result[key] = bytes.readUInt32LE(offset); offset += 4; }
      else if (type === 7) { result[key] = bytes.readBigUInt64LE(offset).toString(); offset += 8; }
      else throw new Error('Unsupported VDF type ' + type);
    }
    return result;
  }
  return Object.values(object().shortcuts || {});
}
const checks = { testExecutable: testExe, temporaryExecutable, functionalTestsExecuted: true,
  collectionName, realNavigation, proxyOperationsExecuted: false, gameLaunchExecuted: false };
let failure;
try {
  checks.steam = await evaluate(`(async () => {
    ${factory}
    ${harness}
    const baseline = appStore.allApps.filter(app => app.BIsShortcut?.()).map(app => ({ id: app.appid, name: app.display_name }));
    const record = window.__clawGameNameVerification = { baseline, ids: [], adds: 0, renames: [], routes: [] };
    const nativeRouter = DFL.Router;
    const mainWindow = nativeRouter.WindowStore.GamepadUIMainWindowInstance;
    const snapshot = () => ({ path: mainWindow.m_history.location.pathname, lastTab: nativeRouter.GetLastLibraryTab(), menu: mainWindow.MenuStore.GetOpenSideMenu() });
    if (${realNavigation}) {
      record.historyEvents = [];
      record.stopHistory = mainWindow.m_history.listen((location, action) => record.historyEvents.push({ path: location.pathname, action, at: Date.now() }));
    }
    const selectedCollection = ${JSON.stringify(collectionName)} && [...collectionStore.collectionsFromStorage.values()].find(item => item.displayName === ${JSON.stringify(collectionName)});
    if (${Boolean(collectionName)} && !selectedCollection) throw new Error('Test collection unavailable');
    if (selectedCollection) {
      record.collectionId = selectedCollection.id;
      record.collectionBaselineAdded = [...selectedCollection.internalAddedList].sort((a,b) => a-b);
      record.collectionBaselineRemoved = [...selectedCollection.internalRemovedList].sort((a,b) => a-b);
    }
    if (${realNavigation} && selectedCollection) {
      nativeRouter.Navigate('/library/collection/' + encodeURIComponent(selectedCollection.id));
      await new Promise(resolve => setTimeout(resolve, 300));
      mainWindow.MenuStore.OpenQuickAccessMenu();
      record.before = snapshot();
    }
    const native = SteamClient.Apps;
    // Steam's native methods require their original namespace as the receiver.
    const bound = Object.fromEntries(Object.keys(native).map(key => [key, typeof native[key] === 'function' ? native[key].bind(native) : native[key]]));
    const apps = { ...bound, async AddShortcut(...args) {
      const id = (await native.AddShortcut(...args)) >>> 0;
      if (baseline.some(app => app.id === id)) throw new Error('Test target collided with an existing shortcut');
      record.ids.push(id); record.adds++; return id;
    }, async SetShortcutName(id, name) {
      if (!record.ids.includes(id)) throw new Error('Refusing to rename a non-test entry');
      record.renames.push({ id, name }); return native.SetShortcutName(id, name);
    } };
    const api = { call: (method, ...args) => {
      if (!['get_game_picker_locations', 'get_game_picker_path', 'inspect_game_file'].includes(method)) throw new Error('Unexpected backend call');
      return DeckyBackend.call('loader/call_plugin_method', 'Claw 掌机工具箱', method, ...args);
    }, openFilePicker: async () => ({ path: ${JSON.stringify(testExe)} }) };
    const env = { setTimeout: window.setTimeout.bind(window), clearTimeout: window.clearTimeout.bind(window),
      SteamClient: { Apps: apps }, appStore, appDetailsStore, collectionStore };
    const router = { Navigate: (...args) => { record.routes.push(args[0]); if (${realNavigation}) return nativeRouter.Navigate(...args); },
      CloseSideMenus: () => { if (${realNavigation}) nativeRouter.CloseSideMenus(); },
      ...( ${realNavigation} ? { WindowStore: nativeRouter.WindowStore, SetLastLibraryTab: nativeRouter.SetLastLibraryTab.bind(nativeRouter), GetLastLibraryTab: nativeRouter.GetLastLibraryTab.bind(nativeRouter) } : {}) };
    const form = createGameAdderHarness(createGameAdder, env, api, router);
    function check(condition, message) { if (!condition) throw new Error(message + ': ' + form.text()); }
    await form.ready();
    check(Boolean(form.field('游戏名称') && form.dropdown('游戏分类')), 'Form is not expanded');
    check(!form.button('返回') && !form.button('添加非 Steam 游戏'), 'Old buttons remain');
    record.drives = form.dropdown('浏览磁盘').props.rgOptions.map(item => item.data);
    if (selectedCollection) form.select('游戏分类', selectedCollection.id);
    await form.click('选择游戏启动文件');
    form.change('游戏名称', 'Claw 名称验证 中文游戏');
    await form.click('添加并打开游戏库');
    if (${realNavigation} && record.routes.length === 0) record.routes.push(snapshot().path);
    check(record.ids.length === 1 && record.routes.length === 1, 'New entry did not complete');
    check(record.routes[0] === '/library/tab/DesktopApps', 'New entry has the wrong library destination');
    const id = record.ids[0];
    check(appStore.GetAppOverviewByAppID(id)?.display_name === 'Claw 名称验证 中文游戏', 'New entry name differs');
    check(Boolean(form.field('游戏名称') && form.button('选择游戏启动文件')), 'Successful add collapsed the form');
    record.initialSavedName = appStore.GetAppOverviewByAppID(id).display_name;
    if (selectedCollection) check(selectedCollection.internalAddedList.has(id), 'Test entry was not saved to collection');
    if (${realNavigation}) {
      await new Promise(resolve => setTimeout(resolve, 1200));
      record.firstNavigation = snapshot();
      check(record.firstNavigation.path === '/library/tab/DesktopApps' && record.firstNavigation.lastTab.strActiveTab === 'DesktopApps' && !record.firstNavigation.lastTab.collectionid,
        'Collection context overrode the first navigation');
      nativeRouter.Navigate('/library/collection/' + encodeURIComponent(selectedCollection.id));
      await new Promise(resolve => setTimeout(resolve, 300));
      mainWindow.MenuStore.OpenQuickAccessMenu();
    }
    await form.click('选择游戏启动文件');
    form.change('游戏名称', 'Claw 名称验证 已有入口改名');
    await form.click('添加并打开游戏库');
    if (${realNavigation} && record.routes.length === 1) record.routes.push(snapshot().path);
    check(record.adds === 1 && record.routes.length === 2, 'Reuse created another entry or failed');
    check(record.routes[1] === '/library/tab/DesktopApps', 'Reused entry has the wrong library destination');
    check(appStore.GetAppOverviewByAppID(id)?.display_name === 'Claw 名称验证 已有入口改名', 'Reused entry name differs');
    record.finalSavedName = appStore.GetAppOverviewByAppID(id).display_name;
    if (${realNavigation}) {
      await new Promise(resolve => setTimeout(resolve, 1200));
      record.secondNavigation = snapshot();
      check(record.secondNavigation.path === '/library/tab/DesktopApps' && record.secondNavigation.lastTab.strActiveTab === 'DesktopApps' && !record.secondNavigation.lastTab.collectionid,
        'Collection context overrode the reuse navigation');
    }
    form.unmount();
    return { id, adds: record.adds, renames: record.renames, initialSavedName: record.initialSavedName,
      finalSavedName: record.finalSavedName, routes: record.routes, drives: record.drives,
      defaultExpanded: true, remainedExpandedAfterAdd: true, backButtonRemoved: true,
      collectionId: record.collectionId, collectionSaved: Boolean(selectedCollection), before: record.before,
      firstNavigation: record.firstNavigation, secondNavigation: record.secondNavigation,
      historyEvents: record.historyEvents };
  })()`);
  for (let attempt = 0; attempt < 30; attempt++) {
    const entry = readShortcuts().find(item => item.appid === checks.steam.id);
    if ((entry?.AppName || entry?.appname) === checks.steam.finalSavedName) {
      checks.namePersistedToSteamFile = true; break;
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  assert.equal(checks.namePersistedToSteamFile, true, 'Confirmed name was not persisted by Steam');
} catch (error) {
  failure = error; checks.error = String(error.message || error);
  try { checks.failureTrace = await evaluate('window.__clawGameNameVerification'); } catch {}
}
finally {
  try {
    checks.cleanup = await evaluate(`(async () => {
      const record = window.__clawGameNameVerification;
      if (!record) return { removed: 0, baselinePreserved: true, collectionBaselinePreserved: true };
      record.stopHistory?.();
      let collectionBaselinePreserved = true;
      if (record.collectionId) {
        const collection = collectionStore.GetCollection(record.collectionId);
        for (const id of record.ids) { collection.internalAddedList.delete(id); collection.internalRemovedList.delete(id); }
        await collection.Save();
        collectionBaselinePreserved = JSON.stringify([...collection.internalAddedList].sort((a,b) => a-b)) === JSON.stringify(record.collectionBaselineAdded)
          && JSON.stringify([...collection.internalRemovedList].sort((a,b) => a-b)) === JSON.stringify(record.collectionBaselineRemoved);
      }
      for (const id of record.ids) await SteamClient.Apps.RemoveShortcut(id);
      for (let attempt = 0; attempt < 50 && record.ids.some(id => appStore.GetAppOverviewByAppID(id)); attempt++)
        await new Promise(resolve => setTimeout(resolve, 100));
      const current = appStore.allApps.filter(app => app.BIsShortcut?.()).map(app => ({ id: app.appid, name: app.display_name }));
      const sort = list => list.slice().sort((a, b) => a.id - b.id);
      const baselinePreserved = JSON.stringify(sort(current)) === JSON.stringify(sort(record.baseline));
      const remainingTestIds = record.ids.filter(id => current.some(app => app.id === id));
      delete window.__clawGameNameVerification;
      return { removed: record.ids.length - remainingTestIds.length, remainingTestIds, baselinePreserved, collectionBaselinePreserved };
    })()`);
    assert.equal(checks.cleanup.baselinePreserved, true, 'Original library entries changed');
    assert.equal(checks.cleanup.collectionBaselinePreserved, true, 'Original collection membership changed');
    assert.equal(checks.cleanup.remainingTestIds?.length || 0, 0, 'Temporary entry remains');
    if (checks.steam?.id) {
      for (let attempt = 0; attempt < 30 && readShortcuts().some(item => item.appid === checks.steam.id); attempt++)
        await new Promise(resolve => setTimeout(resolve, 100));
      assert.equal(readShortcuts().some(item => item.appid === checks.steam.id), false, 'Temporary entry remains in Steam file');
    }
    if (temporaryExecutable) { fs.unlinkSync(testExe); checks.temporaryExecutableRemoved = true; }
    else checks.userExecutablePreserved = fs.statSync(testExe).isFile();
  } catch (error) { checks.cleanupError = String(error.message || error); failure ||= error; }
  ws.close();
  checks.passed = !failure;
  fs.writeFileSync(path.join(root, 'diagnostics', collectionName ? 'game-add-collection-navigation-verification.json' : 'game-add-name-live-verification.json'), JSON.stringify(checks, null, 2));
}
console.log(JSON.stringify(checks, null, 2));
if (failure) process.exitCode = 1;
