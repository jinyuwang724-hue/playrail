import test from 'node:test';
import assert from 'node:assert/strict';

let effects = [], pending, calls = [];
const healthy = { settings: { enabled: true, hapticsEnabled: true }, status: { phase: '桌面' },
  compatibility: { enabled: { steam: true, gamebar: true }, items: { steam: '正常', gamebar: '主页' }, busy: false, phase: '已检查' } };
global.window = { __DECKY_SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED_deckyLoaderAPIInit: {
  connect: () => ({ call: async (method, ...args) => {
    if (method === 'get_state') return healthy;
    if (method === 'get_clash_state') return { available: false, subscriptions: {} };
    if (method === 'set_scaling_pause') return {};
    // Heading summaries read power and picture state; reads never apply anything.
    if (method === 'get_power_state' || method === 'get_magpie_state') return {};
    calls.push([method, ...args]);
    return new Promise(resolve => { pending = resolve; });
  } })
} };
global.SP_REACT = { createElement: (type, props, ...children) => ({type, props: props ?? {}, children}),
  useState: value => [typeof value === 'function' ? value() : value, () => {}],
  useEffect: effect => effects.push(effect), useMemo: value => value() };
global.DFL = Object.fromEntries(['PanelSection', 'PanelSectionRow', 'ToggleField', 'ButtonItem', 'DropdownItem', 'TextField'].map(k => [k,k]));
const { Content } = await import('../plugin/frontend.js');
const { toolboxModules } = await import('../plugin/toolbox-ui.js');
function nodes(node) { return !node || typeof node !== 'object' ? [] : [node, ...node.children.flat(Infinity).flatMap(nodes)]; }
// The home page lists modules; opening one renders its page with the module content as the child.
function panel() {
  let tree = Content();
  if (tree.children[1].props.onOpen) { tree.children[1].props.onOpen('compatibility'); tree = Content(); }
  return tree.children[1].children[0];
}

test('compatibility module keeps two independent switches, pending operation and restore controls', async () => {
  assert.equal(toolboxModules.filter(item => item.id === 'compatibility').length, 1);
  Content(); effects.shift()();
  await new Promise(resolve => setImmediate(resolve));
  const controls = nodes(panel());
  const switches = controls.filter(node => node.type === 'ToggleField');
  assert.deepEqual(switches.map(node => [node.props.label,node.props.checked]), [['Steam 启动修复',true],['Game Bar 主页修复',true]]);
  const operation = switches[1].props.onChange(false);
  switches[1].props.onChange(false);
  assert.equal(calls.length, 1, 'remount or repeated input must not launch a second repair');
  assert.deepEqual(calls[0], ['manage_compatibility','set','gamebar',false]);
  assert.ok(nodes(panel()).filter(node => ['ToggleField','ButtonItem'].includes(node.type)).every(node => node.props.disabled));
  pending({ ...healthy.compatibility, enabled: { steam: true, gamebar: false } });
  await operation;
  const finished = nodes(panel());
  assert.deepEqual(finished.filter(node => node.type === 'ToggleField').map(node => node.props.checked), [true,false]);
  assert.equal(finished.filter(node => node.type === 'ButtonItem').length, 2);
});
