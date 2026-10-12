import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { test } from 'node:test';

const source = fs.readFileSync('repairs/playhub-haptics/index.js', 'utf8');
const start = source.indexOf('function msiClawNavigationPulse(');
const end = source.indexOf('\nfunction rumblePulse(', start);
assert(start >= 0 && end > start, 'Repaired feedback implementation must be present.');
const sideStart = source.indexOf('function actionSide(action) {');
const sideEnd = source.indexOf('\nfunction ', sideStart + 1);
assert(sideStart >= 0 && sideEnd > sideStart);

function harness(controller, result = { handled: true, path: 'windows-gamepad' }) {
  const requests = [];
  let cleared = 0;
  const context = {
    controllerFor: () => controller,
    config: { intensity: 100 },
    API_BASE: 'http://127.0.0.1:47991',
    AbortController,
    nextNativeRequestId: () => 123,
    actionSide: action => action === 'moveLeft' ? 0 : action === 'moveRight' ? 1 : 2,
    window: { setTimeout: () => 1, clearTimeout: () => { cleared++; } },
    fetch: async (url, options) => {
      requests.push({ url, body: JSON.parse(options.body), signal: options.signal });
      return { ok: true, json: async () => result };
    },
    call: async (method, body) => {
      requests.push({ method, body });
      return result;
    },
  };
  vm.createContext(context);
  vm.runInContext(source.slice(sideStart, sideEnd), context);
  vm.runInContext(source.slice(start, end), context);
  return { context, requests, get cleared() { return cleared; } };
}

const claw = { unVendorID: 0x0db0, unProductID: 0x1901 };

test('Claw navigation uses exact hardware and official intensity scaling', async () => {
  const h = harness(claw);
  assert.equal(await h.context.nativePattern(0, 'moveLeft'), true);
  const request = h.requests[0];
  assert.equal(request.method, 'play_claw_haptic');
  assert.equal(request.body.durationMs, 16);
  assert(Math.abs(request.body.magnitude - 0.95 * 0.86) < 1e-12);
  assert.equal(request.body.pattern, undefined);
  assert.equal(request.body.side, 0);
  assert.equal(request.body.vendorId, 0x0db0);
  assert.equal(request.body.productId, 0x1901);
  assert.equal(request.body.requestId, 123);
  assert.equal(h.cleared, 0);
});

test('Intensity remains adjustable, with distinct confirmation and slider pulses', async () => {
  const h = harness(claw);
  h.context.config.intensity = 50;
  await h.context.nativePattern(0, 'confirm');
  await h.context.nativePattern(0, 'sliderIncrease');
  assert.equal(h.requests[0].body.durationMs, 21);
  assert.equal(h.requests[1].body.durationMs, 12);
  assert(h.requests[0].body.magnitude > 0.05 && h.requests[0].body.magnitude < 0.95);
});

test('Every action matches official first-step motor, scale and duration', async () => {
  const official = fs.readFileSync('diagnostics/ControllerHapticPatterns-upstream.cs', 'utf8');
  const blocks = official.matchAll(/((?:"[^"]+"\s+or\s+)*"[^"]+"|_)\s*=>\s*\[\s*new\((side|[012]),\s*([\d.]+),\s*(\d+),/g);
  let matchedActions = 0;
  for (const block of blocks) {
    const actions = [...block[1].matchAll(/"([^"]+)"/g)].map(match => match[1]);
    if (block[1] === '_') actions.push('unrecognized-action');
    for (const action of actions) {
      for (const intensity of [5, 50, 100]) {
        const h = harness(claw);
        h.context.config.intensity = intensity;
        await h.context.nativePattern(0, action);
        const actual = h.requests[0].body;
        const expectedSide = block[2] === 'side' ? h.context.actionSide(action) : Number(block[2]);
        const base = Math.min(1, Math.max(0.05, Math.pow(intensity / 100, 1.35) * 0.95));
        assert.equal(actual.durationMs, Number(block[4]), action);
        assert.equal(actual.side, expectedSide, action);
        assert(Math.abs(actual.magnitude - base * Number(block[3])) < 1e-12, action);
      }
      matchedActions++;
    }
  }
  assert.equal(matchedActions, 17, 'Validate every official action and the fallback.');
});

test('Other hardware retains the existing pattern request', async () => {
  for (const controller of [
    { unVendorID: 0x045e, unProductID: 0x028e },
    { unVendorID: 0x054c, unProductID: 0x0ce6 },
    { unVendorID: 0x0db0, unProductID: 0x1234 },
  ]) {
    const h = harness(controller);
    await h.context.nativePattern(0, 'moveRight');
    assert.equal(h.requests[0].body.pattern, 'moveRight');
    assert.equal(h.requests[0].body.durationMs, undefined);
  }
});

test('Unavailable hardware and transport failures retain bounded failure behavior', async () => {
  const missing = harness({});
  assert.equal(await missing.context.nativePattern(0, 'confirm'), false);
  assert.equal(missing.requests.length, 0);
  const failed = harness(claw);
  failed.context.call = async () => { throw new Error('Service unavailable'); };
  assert.equal(await failed.context.nativePattern(0, 'confirm'), false);
  assert.equal(failed.cleared, 0);
  const unsupported = harness(claw, { handled: false });
  assert.equal(await unsupported.context.nativePattern(0, 'confirm'), false);
});
