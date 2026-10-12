import test from 'node:test';
import assert from 'node:assert/strict';
import { createRumbleController, RUMBLE_WRITE_DELAY_MS } from '../plugin/rumble.js';

function fakeTimers() {
  let now = 0, next = 1; const pending = new Map();
  return {
    setTimeout(fn, ms) { const id = next++; pending.set(id, { fn, at: now + ms }); return id; },
    clearTimeout(id) { pending.delete(id); },
    async advance(ms) {
      now += ms;
      for (const [id, item] of [...pending]) if (item.at <= now) { pending.delete(id); await item.fn(); }
    },
  };
}

test('slider drags become one firmware write after the value settles', async () => {
  const calls = [];
  const api = { call: async (method, ...args) => { calls.push([method, ...args]); return { strength: args[0], error: null }; } };
  const timers = fakeTimers();
  const controller = createRumbleController(api, timers);
  for (const v of [90, 80, 70, 60]) { controller.edit(v); await timers.advance(100); }
  assert.equal(controller.value.pending, 60);
  assert.deepEqual(calls, []);
  await timers.advance(RUMBLE_WRITE_DELAY_MS);
  assert.deepEqual(calls, [['set_rumble_strength', 60]]);
  assert.equal(controller.value.strength, 60);
  assert.equal(controller.value.pending, null);
});

test('errors are shown instead of a stale value', async () => {
  const api = { call: async () => { throw new Error('没有找到唯一的 Claw 手柄配置接口'); } };
  const controller = createRumbleController(api, fakeTimers());
  await controller.refresh();
  assert.match(controller.value.error, /Claw 手柄/);
});

test('the test pulse explains why it stays silent during a game', async () => {
  const api = { call: async () => ({ handled: true, path: 'steam-ui-only' }) };
  const controller = createRumbleController(api, fakeTimers());
  await controller.test();
  assert.match(controller.value.error, /游戏运行时/);
});
