import test from 'node:test';
import assert from 'node:assert/strict';
import { createMagpieController, overlayInsetCss, syncOverlayInset, syncQuickAccessSize } from '../plugin/magpie.js';

test('reading, unchanged selection and remount preserve draft without applying', async () => {
  const calls = [];
  const state = { available:true, enabled:false, target:0, preferences:{fill:true,sharpen:true,strength:.5,capture:'graphics',cropX:0,cropY:0} };
  const api = { call:async (method,...args) => { calls.push([method,...args]); return method === 'get_magpie_targets' ? [{hwnd:123}] : method === 'get_magpie_catalog' ? {effects:[],presets:[]} : state; } };
  const c = createMagpieController(api);
  await c.refresh(); c.edit({strength:.8}); await c.refresh();
  assert.equal(c.value.draft.strength,.8);
  assert.equal(c.value.draft.target,123);
  assert.equal(calls.filter(([m]) => m === 'apply_magpie').length,0);
  await c.apply();
  assert.deepEqual(calls.at(-1),['apply_magpie',{fill:true,sharpen:true,strength:.8,capture:'graphics',cropX:0,cropY:0,target:123,
    pipeline:[],maxFrameRate:60,game3D:false,adjustCursor:true}]);
});

test('repeated apply while pending sends only one request', async () => {
  let complete, count=0;
  const c = createMagpieController({ call:() => { count++; return new Promise(r => complete=r); } });
  c.edit({target:123}); const first=c.apply(); await c.apply();
  assert.equal(count,1); complete({enabled:true}); await first; assert.equal(c.value.busy,false);
});

test('old frontend disposal cannot send stop to replacement backend', () => {
  const calls=[];
  const c=createMagpieController({call:(...args)=>calls.push(args)});
  c.accept({enabled:true}); c.dispose();
  assert.deepEqual(calls,[]);
});

test('status synchronizes untouched settings but preserves a pending edit', () => {
  const c=createMagpieController({call:async()=>({})});
  c.accept({target:123,preferences:{strength:.5,pipeline:[]}});
  c.accept({target:123,preferences:{strength:.8,pipeline:[]}});
  assert.equal(c.value.draft.strength,.8);
  c.edit({strength:.7});
  c.accept({target:123,preferences:{strength:.9,pipeline:[]}});
  assert.equal(c.value.draft.strength,.7);
});

test('fill insets the Steam overlay UI to the visible part of the frame and removes it otherwise', () => {
  const filled = { enabled:true, active:true, dest:[-106,0,2027,1200] };
  assert.match(overlayInsetCss(filled), /left:4\.970%.*width:90\.061%/);
  assert.equal(overlayInsetCss({ ...filled, dest:[0,60,1920,1140] }), '');  // whole frame visible
  assert.equal(overlayInsetCss({ ...filled, active:false }), '');
  const styles = new Map();
  const doc = { head:{ appendChild:s => styles.set(s.id, s) }, getElementById:id => styles.get(id),
    createElement:() => ({ remove() { styles.delete(this.id); } }) };
  const env = { SteamUIStore:{ WindowStore:{ OverlayWindows:[{ BrowserWindow:{ document:doc } }] } } };
  syncOverlayInset(env, filled);
  assert.match(styles.get('claw-fill-inset').textContent, /#popup_target/);
  syncOverlayInset(env, null);
  assert.equal(styles.size, 0);
});
test('quick access zoom and slide stay together even when the browser normalizes zoom', () => {
  // style.zoom reads back normalized ("0.6000" -> "0.6"), like Chromium.
  const zoomStyle = { _z: '', get zoom() { return this._z ? String(parseFloat(this._z)) : ''; }, set zoom(v) { this._z = v; } };
  const view = { closed: false, document: { title: 'QuickAccess_uid7', documentElement: { style: zoomStyle } } };
  const attrs = {};
  const placeholder = { style: { translate: '', width: '' }, getAttribute: k => attrs[k] ?? null, hasAttribute: k => k in attrs,
    setAttribute: (k, v) => { attrs[k] = v; }, removeAttribute: k => { delete attrs[k]; },
    getBoundingClientRect() { const s = parseFloat(this.style.translate) || 0; return { left: 463 + s, right: 1317 + s, height: 480, width: 854 }; } };
  const doc = {
    querySelectorAll: sel => sel === '#popup_target *' ? [placeholder] : ('data-claw-qam-shift' in attrs ? [placeholder] : []),
    querySelector: () => ('data-claw-qam-shift' in attrs ? placeholder : null),
    getElementById: () => ({ getBoundingClientRect: () => ({ left: 42.4, right: 810.9 }) }),
  };
  const win = { innerWidth: 853, innerHeight: 480, devicePixelRatio: 1.5, document: doc, requestAnimationFrame: f => f(), setTimeout: f => f(),
    SteamClient: { Browser: { GetBrowserID: () => 7 } } };
  let open = 2;
  const env = { open: () => view, SteamUIStore: { WindowStore: { OverlayWindows: [{ BrowserWindow: win, MenuStore: { GetOpenSideMenu: () => open } }] } } };
  const value = { enabled: true, active: true, dest: [-106, 0, 2027, 1200] };
  syncQuickAccessSize(env, value);
  assert.equal(zoomStyle.zoom, '0.6');
  assert.equal(placeholder.style.translate, '139px 0');
  syncQuickAccessSize(env, value);  // a second pass keeps both
  assert.equal(placeholder.style.translate, '139px 0');
  open = 0;  // closed: zoom and slide stay for the next open
  syncQuickAccessSize(env, value);
  assert.equal(zoomStyle.zoom, '0.6');
  assert.equal(placeholder.style.translate, '139px 0');
  syncQuickAccessSize(env, { enabled: false });  // fill off: native size again
  assert.equal(zoomStyle.zoom, '');
  assert.equal(placeholder.style.translate, '');
});
