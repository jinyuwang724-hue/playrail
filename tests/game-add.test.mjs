import test from 'node:test';
import assert from 'node:assert/strict';
import { createGameAdder } from '../plugin/game-add.js';
import { createGameAdderHarness } from './helpers/game-adder-harness.mjs';

function fixture({ existing = false, renameFailure = false, delayRename = false } = {}) {
  const calls = [], routes = [], overviews = new Map();
  const path = 'D:\\Games\\example.exe';
  let cancel = false, locationsRead = 0;
  function overview(id, name) {
    return { appid: id, display_name: name, BIsShortcut: () => true };
  }
  if (existing) overviews.set(2468000123, overview(2468000123, 'example'));
  const apps = {
    async GetShortcutDataForPath() { return { strAppName: 'example', strExePath: path }; },
    async AddShortcut(name, exe) {
      calls.push(['add', name, exe]);
      // Exercise a native client that initially keeps the executable's name.
      overviews.set(2468000123, overview(2468000123, 'example'));
      return 2468000123;
    },
    async SetShortcutName(id, name) {
      calls.push(['name', id, name]);
      if (renameFailure) { renameFailure = false; throw new Error('名称暂未保存'); }
      const update = () => { overviews.get(id).display_name = name; };
      delayRename ? setTimeout(update, 20) : update();
    },
    async SetShortcutStartDir(...args) { calls.push(['directory', ...args]); },
    async SetShortcutLaunchOptions(...args) { calls.push(['arguments', ...args]); },
  };
  const env = {
    setTimeout, clearTimeout, SteamClient: { Apps: apps },
    collectionStore: { collectionsFromStorage: new Map(), GetCollection() {} },
    appStore: { get allApps() { return [...overviews.values()]; }, GetAppOverviewByAppID: id => overviews.get(id) },
    appDetailsStore: { GetAppDetails: () => ({ strShortcutExe: `"${path}"`, strLaunchOptions: '--existing' }) },
  };
  const api = {
    async call(method, ...args) {
      if (method === 'get_game_picker_locations') {
        locationsRead++;
        return { drives: [{ id: 'C:', label: 'C 盘', path: 'C:/.' }, { id: 'D:', label: 'D 盘', path: 'D:/.' }], defaultDrive: 'C:', defaultPath: 'C:/Game' };
      }
      if (method === 'get_game_picker_path') return args[1];
      if (method === 'inspect_game_file') return { path, directory: 'D:\\Games', name: 'example', extension: '.exe' };
      throw new Error('Unexpected backend method: ' + method);
    },
    async openFilePicker(type, start) {
      calls.push(['picker', start]);
      if (cancel) throw 'User canceled';
      return { path };
    },
  };
  const router = {
    Navigate(route) { routes.push({ route, name: overviews.get(2468000123)?.display_name }); }, CloseSideMenus() {},
  };
  const form = createGameAdderHarness(createGameAdder, env, api, router);
  return { form, calls, routes, overviews, env, router, path, cancel: () => { cancel = true; }, locationsRead: () => locationsRead };
}

function collectionNavigation(f, loseFirstNavigation = false) {
  const collection = { id: 'uc-test', displayName: '旮旯game', bAllowsDragAndDrop: true, bIsEditable: true,
    internalAddedList: new Set(), internalRemovedList: new Set(),
    async Save() { f.calls.push(['category', ...this.internalAddedList]); } };
  f.env.collectionStore.collectionsFromStorage.set(collection.id, collection);
  f.env.collectionStore.GetCollection = id => id === collection.id ? collection : undefined;
  const history = { location: { pathname: '/library/collection/uc-test' } };
  let lastTab = { strActiveTab: 'Collections', collectionid: collection.id }, navigations = 0;
  Object.assign(f.router, {
    CloseSideMenus() {
      f.calls.push(['close-menu']);
      setTimeout(() => {
        history.location.pathname = '/library/collection/uc-test';
        lastTab = { strActiveTab: 'Collections', collectionid: collection.id };
        f.calls.push(['restore-focus']);
      }, 0);
    },
    SetLastLibraryTab(tab, id) { lastTab = { strActiveTab: tab, collectionid: id }; f.calls.push(['remember-tab', tab, id]); },
    GetLastLibraryTab: () => lastTab,
    WindowStore: { GamepadUIMainWindowInstance: { m_history: history, Navigator: {
      LibraryTab(tab, options) {
        f.calls.push(['native-tab', tab, options]);
        history.location.pathname = '/library/tab/' + tab;
        if (loseFirstNavigation && navigations++ === 0) setTimeout(() => {
          history.location.pathname = '/library/collection/uc-test';
          lastTab = { strActiveTab: 'Collections', collectionid: collection.id };
        }, 20);
      },
    } } },
  });
  return { collection, history, lastTab: () => lastTab };
}

test('all main options are shown immediately and reopening retains the draft without reinitializing', async () => {
  const f = fixture();
  await f.form.ready();
  assert.ok(f.form.dropdown('浏览磁盘'));
  assert.ok(f.form.field('游戏名称'));
  assert.ok(f.form.dropdown('游戏分类'));
  assert.match(f.form.text(), /启动文件尚未选择/);
  assert.equal(f.form.button('添加非 Steam 游戏'), undefined);
  assert.equal(f.form.button('返回'), undefined);
  await f.form.click('选择游戏启动文件');
  f.form.change('游戏名称', '我的中文游戏');
  f.form.unmount();
  assert.equal(f.form.field('游戏名称').props.value, '我的中文游戏');
  assert.equal(f.locationsRead(), 1);
  assert.equal(f.form.button('添加并打开游戏库').props.disabled, false);
  assert.equal(f.calls.some(item => item[0] === 'add'), false);
});

test('a new shortcut uses the edited name and navigation waits for Steam to reflect it', async () => {
  const f = fixture({ delayRename: true });
  await f.form.ready();
  await f.form.click('选择游戏启动文件');
  f.form.change('游戏名称', '甜蜜冒险 中文版');
  await f.form.click('添加并打开游戏库');
  assert.deepEqual(f.calls.find(item => item[0] === 'name'), ['name', 2468000123, '甜蜜冒险 中文版']);
  assert.deepEqual(f.routes, [{ route: '/library/tab/DesktopApps', name: '甜蜜冒险 中文版' }]);
  assert.ok(f.form.field('游戏名称'));
  assert.equal(f.form.field('游戏名称').props.value, '');
  assert.ok(f.form.button('选择游戏启动文件'));
  assert.equal(f.form.button('返回'), undefined);
});

test('an existing target is renamed to the confirmed name without creating another entry or changing launch settings', async () => {
  const f = fixture({ existing: true });
  await f.form.ready();
  await f.form.click('选择游戏启动文件');
  f.form.change('游戏名称', '已在库中的中文游戏');
  await f.form.click('添加并打开游戏库');
  assert.equal(f.overviews.size, 1);
  assert.equal(f.overviews.get(2468000123).display_name, '已在库中的中文游戏');
  assert.equal(f.calls.some(item => ['add', 'directory', 'arguments'].includes(item[0])), false);
  assert.equal(f.routes[0].name, '已在库中的中文游戏');
  assert.equal(f.routes[0].route, '/library/tab/DesktopApps');
});

test('a failed rename retains the entry and retry saves the same ID before navigating', async () => {
  const f = fixture({ renameFailure: true });
  await f.form.ready();
  await f.form.click('选择游戏启动文件');
  f.form.change('游戏名称', '重试后保存的名称');
  await f.form.click('添加并打开游戏库');
  assert.equal(f.routes.length, 0);
  assert.match(f.form.text(), /游戏入口已保留/);
  await f.form.click('继续完成并打开游戏库');
  assert.equal(f.calls.filter(item => item[0] === 'add').length, 1);
  assert.equal(f.calls.filter(item => item[0] === 'name').length, 2);
  assert.equal(f.routes[0].name, '重试后保存的名称');
});

test('canceling a re-selection preserves the edited name and creates no entry', async () => {
  const f = fixture();
  await f.form.ready();
  await f.form.click('选择游戏启动文件');
  f.form.change('游戏名称', '保留手动名称');
  f.cancel();
  await f.form.click('重新选择启动文件');
  assert.equal(f.form.field('游戏名称').props.value, '保留手动名称');
  assert.equal(f.calls.some(item => item[0] === 'add'), false);
});

test('drive changes start at an absolute root and retain the selected folder for that drive', async () => {
  const f = fixture();
  await f.form.ready();
  f.form.select('浏览磁盘', 'D:');
  await f.form.click('选择游戏启动文件');
  f.form.select('浏览磁盘', 'C:');
  await f.form.click('重新选择启动文件');
  f.form.select('浏览磁盘', 'D:');
  await f.form.click('重新选择启动文件');
  assert.deepEqual(f.calls.filter(item => item[0] === 'picker').map(item => item[1]), ['D:/.', 'C:/Game', 'D:/Games']);
});

test('saving a collection closes the side menu and clears its restored context before the native tab switch', async () => {
  const f = fixture();
  const context = collectionNavigation(f);
  await f.form.ready();
  f.form.select('游戏分类', 'uc-test');
  await f.form.click('选择游戏启动文件');
  await f.form.click('添加并打开游戏库');
  assert.equal(context.collection.internalAddedList.has(2468000123), true);
  assert.equal(context.history.location.pathname, '/library/tab/DesktopApps');
  assert.equal(context.lastTab().strActiveTab, 'DesktopApps');
  assert.equal(context.lastTab().collectionid, undefined);
  assert.deepEqual(f.calls.filter(item => ['category', 'close-menu', 'restore-focus', 'remember-tab', 'native-tab'].includes(item[0])), [
    ['category', 2468000123], ['close-menu'], ['restore-focus'], ['remember-tab', 'DesktopApps', undefined], ['native-tab', 'DesktopApps', { bReplace: true }],
  ]);
  assert.equal(f.routes.length, 0);
});

test('a remembered collection is cleared even when the new game is not assigned to a collection', async () => {
  const f = fixture();
  const context = collectionNavigation(f);
  await f.form.ready();
  await f.form.click('选择游戏启动文件');
  await f.form.click('添加并打开游戏库');
  assert.equal(context.collection.internalAddedList.size, 0);
  assert.equal(context.lastTab().collectionid, undefined);
  assert.equal(context.history.location.pathname, '/library/tab/DesktopApps');
});

test('a delayed return to the collection preserves the entry for a navigation retry without adding or saving membership twice', async () => {
  const f = fixture();
  const context = collectionNavigation(f, true);
  await f.form.ready();
  f.form.select('游戏分类', 'uc-test');
  await f.form.click('选择游戏启动文件');
  await f.form.click('添加并打开游戏库');
  assert.match(f.form.text(), /非 Steam 游戏页面尚未打开/);
  assert.ok(f.form.button('继续完成并打开游戏库'));
  await f.form.click('继续完成并打开游戏库');
  assert.equal(f.calls.filter(item => item[0] === 'add').length, 1);
  assert.equal(f.calls.filter(item => item[0] === 'category').length, 1);
  assert.equal(context.history.location.pathname, '/library/tab/DesktopApps');
  assert.equal(f.form.field('游戏名称').props.value, '');
});
