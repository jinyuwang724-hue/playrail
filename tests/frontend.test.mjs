import test from 'node:test';
import assert from 'node:assert/strict';
global.window = { __DECKY_SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED_deckyLoaderAPIInit: { connect: () => ({}) } };
global.SP_REACT = { createElement() {} };
global.DFL = {};
const { executeMenu } = await import('../plugin/frontend.js');
function fixture(app = 0, refuse = false) {
  let side = 0;
  const menu = { GetOpenSideMenu: () => side, OpenSideMenu: s => { if (!refuse) side = s; },
    OpenQuickAccessMenu: () => { if (!refuse) side = 2; }, CloseSideMenus: () => { side = 0; } };
  return { env: { SteamUIStore: { MainRunningAppID: app, WindowStore: { GamepadUIMainWindowInstance: { MenuStore: menu } } } }, side: () => side };
}
test('physical buttons open and close their corresponding menus', async () => {
  const f = fixture();
  assert.equal((await executeMenu('MainMenu', f.env, {})).ok, true); assert.equal(f.side(), 1);
  assert.equal((await executeMenu('QuickAccess', f.env, {})).ok, true); assert.equal(f.side(), 2);
  assert.equal((await executeMenu('QuickAccess', f.env, {})).ok, true); assert.equal(f.side(), 0);
});
test('a Steam method that does not open a menu is reported as failure', async () => {
  assert.equal((await executeMenu('MainMenu', fixture(0,true).env, {})).ok, false);
});
test('a running game without overlay information never receives a hidden home menu', async () => {
  const f=fixture(123);assert.equal((await executeMenu('QuickAccess',f.env,{})).ok,false);assert.equal(f.side(),0);
});
test('unavailable Steam APIs fail closed', async () => {
  assert.equal((await executeMenu('MainMenu', {}, {})).ok, false);
  assert.equal((await executeMenu('ArbitraryCommand',fixture().env,{})).ok,false);
});
