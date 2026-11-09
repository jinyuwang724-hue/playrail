import test from 'node:test';
import assert from 'node:assert/strict';
import { createScalingController, scalingModes, scalingFilters } from '../plugin/scaling.js';

const initial = { enabled: false, mode: 'auto', filter: 'linear', sharpness: .5, target: 0, available: true };
function fixture() {
  const calls = [], state = { ...initial };
  const controller = createScalingController({ call: async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'get_scaling_targets') return [{ hwnd: 123, title: 'Game', width: 800, height: 600 }];
    if (method === 'set_scaling_settings') Object.assign(state, args[0]);
    return { ...state };
  } });
  return { controller, calls };
}
test('all five modes and four independent filters are in the requested order', async () => {
  assert.deepEqual(scalingModes.map(x => x.label), ['自动','整数','适应','拉伸','填充']);
  assert.deepEqual(scalingFilters.map(x => x.label), ['线性','最近','FSR','NIS']);
  const { controller } = fixture();
  await controller.refresh();
  assert.equal(controller.value.suggestedTarget, 123);
  assert.equal(controller.value.enabled, false, 'A suggested window must never auto-start capture');
  for (const mode of scalingModes) for (const filter of scalingFilters) {
    await controller.set({ mode: mode.data, filter: filter.data });
    assert.equal(controller.value.mode, mode.data); assert.equal(controller.value.filter, filter.data);
  }
});
test('reopening and unchanged choices only read; they never configure capture', async () => {
  const { controller, calls } = fixture();
  await Promise.all([controller.refresh(), controller.refresh()]);
  await controller.set({ mode: 'auto' }); await controller.set({ enabled: false });
  await controller.refresh();
  assert(!calls.some(([method]) => method === 'set_scaling_settings'));
  assert.equal(calls.filter(([method]) => method === 'get_scaling_state').length, 2);
});
test('one visibility source closing cannot erase another source', async () => {
  const { controller, calls } = fixture();
  await controller.pause('overlay', true); await controller.pause('panel', true);
  await controller.pause('panel', false); await controller.pause('panel', false);
  assert.deepEqual(calls, [['set_scaling_pause','overlay',true], ['set_scaling_pause','panel',true], ['set_scaling_pause','panel',false]]);
});
test('a failed enable recovers actual disabled state and reports its error', async () => {
  const controller = createScalingController({ call: async method => {
    if (method === 'set_scaling_settings') throw Error('Capture unavailable');
    return initial;
  } });
  await controller.set({ target: 123, enabled: true });
  assert.equal(controller.value.enabled, false); assert.match(controller.value.message, /Capture unavailable/);
});
test('frontend unload explicitly releases an enabled capture', async () => {
  const { controller, calls } = fixture();
  await controller.set({ target: 123, enabled: true }); controller.dispose();
  assert.equal(calls.at(-1)[0], 'set_scaling_settings'); assert.equal(calls.at(-1)[1].enabled, false);
});
