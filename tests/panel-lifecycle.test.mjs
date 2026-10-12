import test from 'node:test';
import assert from 'node:assert/strict';
import { syncPluginPanel } from '../plugin/panel-lifecycle.js';

function fixture(active = 'old') {
  const name = 'Claw 掌机工具箱';
  const Content = function Content() {};
  const old = { name, version: '0.7.2', content: { type: function OldContent() {} } };
  const current = { name, version: '0.7.5', content: { type: Content } };
  const other = { name: 'Other plugin' };
  const state = { plugins: [old], activePlugin: active === 'other' ? other : active === null ? null : active === 'current' ? current : old };
  const changes = [];
  let callback;
  const store = {
    publicState: () => state,
    setActivePlugin(value) { changes.push(value); state.activePlugin = state.plugins.find(plugin => plugin.name === value); },
    closeActivePlugin() { changes.push(null); state.activePlugin = null; },
  };
  const env = { DeckyPluginLoader: { deckyState: store }, setTimeout(fn) { callback = fn; return 1; }, clearTimeout() { callback = null; } };
  const dispose = syncPluginPanel(env, name, Content);
  state.plugins = [current, other]; // Decky publishes the new object after the plugin factory returns.
  return { state, current, changes, dispose, flush: () => callback?.() };
}

test('a reload replaces the stale active panel with the newly loaded plugin object', () => {
  const f = fixture();
  assert.equal(f.state.activePlugin.version, '0.7.2');
  f.flush();
  assert.equal(f.state.activePlugin, f.current);
  assert.deepEqual(f.changes, ['Claw 掌机工具箱']);
});

test('loading does not open a closed panel or replace another active plugin', () => {
  for (const active of [null, 'other', 'current']) {
    const f = fixture(active);
    f.flush();
    assert.deepEqual(f.changes, []);
  }
});

test('unloading clears only the panel belonging to this instance and cancels a pending refresh', () => {
  const f = fixture('current');
  f.dispose();
  f.flush();
  assert.equal(f.state.activePlugin, null);
  assert.deepEqual(f.changes, [null]);
  const replacement = fixture();
  replacement.dispose();
  replacement.flush();
  assert.equal(replacement.state.activePlugin.version, '0.7.2');
  assert.deepEqual(replacement.changes, []);
});
