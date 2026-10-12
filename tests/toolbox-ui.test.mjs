import test from 'node:test';
import assert from 'node:assert/strict';
import { createToolboxUI, toolboxGroups, toolboxModules, createModuleSelection } from '../plugin/toolbox-ui.js';
import { powerSummaries, powerTiles } from '../plugin/power.js';

const React = { createElement: (type, props, ...children) => ({ type, props: props ?? {}, children }), Fragment: 'Fragment' };
const ui = { Focusable: 'Focusable', DialogButton: 'DialogButton', PanelSectionRow: 'PanelSectionRow' };
// Expand function components so the whole tree can be searched.
function nodes(node) {
  if (!node || typeof node !== 'object') return [];
  if (typeof node.type === 'function') return nodes(node.type({ ...node.props, children: node.children }));
  return [node, ...node.children.flat(Infinity).flatMap(nodes)];
}
const text = node => nodes(node).flatMap(n => n.children.flat(Infinity).filter(c => typeof c === 'string' || typeof c === 'number').map(String));
const { Home, SubPage } = createToolboxUI(React, ui);

test('every module belongs to a listed group and ids stay unique', () => {
  const groups = new Set(toolboxGroups.map(group => group.id));
  assert.ok(toolboxModules.every(module => groups.has(module.group)));
  assert.equal(new Set(toolboxModules.map(module => module.id)).size, toolboxModules.length);
});

test('the toolbox opens on the home page', () => {
  assert.equal(createModuleSelection().value, null);
});

test('home lists groups in order, shows module states and opens a module', () => {
  const opened = [];
  const tree = Home({ states: { fan: { text: '固件自动', off: true } }, onOpen: id => opened.push(id) });
  const words = text(tree);
  assert.ok(words.indexOf('游戏与硬件') < words.indexOf('系统与工具'));
  const state = nodes(tree).find(n => String(n.props.className).startsWith('claw-state'));
  assert.equal(state.props.className, 'claw-state off');
  const rows = nodes(tree).filter(n => n.props.className === 'claw-row');
  assert.equal(rows.length, toolboxModules.length);
  rows[3].props.onClick();
  assert.deepEqual(opened, [toolboxModules[3].id]);
});

test('missing readings show a dash instead of a number', () => {
  const tree = Home({ metrics: [{ label: '功耗', value: null, unit: 'W' }, { label: '电量', value: 72, unit: '%' }], onOpen: () => {} });
  const strong = nodes(tree).filter(n => n.type === 'strong');
  assert.equal(strong[0].props.className, 'na');
  assert.deepEqual(strong[0].children.filter(Boolean), ['—']);
  assert.ok(text(strong[1]).includes('72'));
});

test('a module with an unlisted group still shows under a trailing group', () => {
  const tree = Home({ onOpen: () => {}, modules: [{ id: 'vibe', group: 'future', title: '振动强度', icon: 'haptics' }] });
  assert.ok(text(tree).includes('其他'));
  assert.ok(text(tree).includes('振动强度'));
});

test('module page goes back from its button and from B', () => {
  let back = 0;
  const tree = SubPage({ module: toolboxModules[0], onBack: () => back++ }, 'content');
  assert.equal(tree.props.onCancelButton !== undefined, true);
  tree.props.onCancelButton();
  nodes(tree).find(n => n.props.className === 'claw-back').props.onClick();
  assert.equal(back, 2);
});

const draft = { enabled: true, mode: 'smart', quick: [8, 15, 25], manual: { tdp: 12 }, ac: { tdp: 25 }, dc: { tdp: 15 },
  osd: { enabled: false, level: 2 }, fan: { mode: 'firmware', full: false } };
function controller() { const edits = []; return { edits, edit: (patch, delay) => edits.push([patch, delay]) }; }

test('power tile steps smart, then each quick wattage, then back to smart', () => {
  const c = controller();
  let tiles = powerTiles({ view: { charger: true, smart: { limit: 14 } }, draft }, c, () => {});
  assert.equal(tiles[0].value, '智能 14 W');
  tiles[0].onPress();
  assert.deepEqual(c.edits[0], [{ mode: 'manual', manual: { tdp: 8 } }, 800]);
  tiles = powerTiles({ view: { charger: true }, draft: { ...draft, mode: 'manual', manual: { tdp: 25 } } }, c, () => {});
  tiles[0].onPress();
  assert.deepEqual(c.edits[1], [{ mode: 'smart' }, 800]);
});

test('power tile never takes over power; it opens the power page', () => {
  const c = controller(), opened = [];
  const [tile] = powerTiles({ view: { charger: false }, draft: { ...draft, enabled: false } }, c, id => opened.push(id));
  assert.equal(tile.value, '未接管');
  tile.onPress();
  assert.deepEqual(opened, ['power']);
  assert.equal(c.edits.length, 0);
});

test('OSD and fan tiles cycle through their levels', () => {
  const c = controller();
  const [, osd, fan] = powerTiles({ view: { charger: true }, draft }, c, () => {});
  osd.onPress(); fan.onPress();
  assert.deepEqual(c.edits[0][0], { osd: { enabled: true, level: 1 } });
  assert.deepEqual(c.edits[1][0], { fan: { mode: 'quiet', full: false } });
  const [, osd4, fanTop] = powerTiles({ view: { charger: true }, draft: { ...draft, osd: { enabled: true, level: 4 }, fan: { mode: 'performance', full: false } } }, c, () => {});
  osd4.onPress(); fanTop.onPress();
  assert.deepEqual(c.edits[2][0], { osd: { enabled: false, level: 4 } });
  assert.deepEqual(c.edits[3][0], { fan: { mode: 'firmware', full: false } });
});

test('power summaries describe power, OSD and fan for the module list', () => {
  assert.deepEqual(powerSummaries({}), {});
  const d = { ...draft, mode: 'manual', osd: { enabled: true, level: 2 }, fan: { mode: 'quiet', full: true } };
  assert.deepEqual(powerSummaries({ view: { charger: true }, draft: d }), {
    power: { text: '手动 12 W' }, osd: { text: '第 2 档' }, fan: { text: '静音 · 全速', off: false } });
});
