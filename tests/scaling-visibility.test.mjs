import test from 'node:test';
import assert from 'node:assert/strict';
import { createMenuRouter } from '../plugin/menu-router.js';

test('menu visibility is observed independently of a retained plugin component', async () => {
  let side = 0, changed, read;
  const calls = [];
  const menu = { GetOpenSideMenu: () => side };
  const env = { SteamUIStore: { MainRunningAppID: 0, WindowStore: { GamepadUIMainWindowInstance: { MenuStore: menu } } } };
  const router = createMenuRouter(env, {}, {
    reaction(reader, effect) { read = reader; changed = effect; return () => {}; },
    onMenuVisibility: async (...args) => calls.push(args),
  });
  const closed = read(); side = 2;
  assert.notEqual(read(), closed); changed(); await Promise.resolve();
  assert.deepEqual(calls.filter(x => x[0] === 'panel').at(-1), ['panel',true]);
  side = 0; changed(); await Promise.resolve();
  assert.equal(read(), closed);
  assert.deepEqual(calls.filter(x => x[0] === 'panel').at(-1), ['panel',false]);
  router.dispose();
});
