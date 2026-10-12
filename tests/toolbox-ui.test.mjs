import test from 'node:test';
import assert from 'node:assert/strict';
import { createToolboxUI, toolboxGroups, toolboxModules, createModuleSelection } from '../plugin/toolbox-ui.js';
import { powerSummaries } from '../plugin/power.js';

const React = { createElement: (type, props, ...children) => ({ type, props: props ?? {}, children }) };
const ui = { Focusable: 'Focusable', DialogButton: 'DialogButton', PanelSectionRow: 'PanelSectionRow' };
function nodes(node) { return !node || typeof node !== 'object' ? [] : [node, ...node.children.flat(Infinity).flatMap(nodes)]; }
const text = node => nodes(node).flatMap(n => n.children.flat(Infinity).filter(c => typeof c === 'string'));

test('every module belongs to a listed group and ids stay unique', () => {
  const groups = new Set(toolboxGroups.map(group => group.id));
  assert.ok(toolboxModules.every(module => groups.has(module.group)));
  assert.equal(new Set(toolboxModules.map(module => module.id)).size, toolboxModules.length);
});

test('module list renders group titles in order with their modules and heading states', () => {
  const { ModuleStack } = createToolboxUI(React, ui);
  const tree = ModuleStack({ active: null, onSelect: () => {}, contents: {}, states: { fan: { text: '固件自动', off: true } } });
  const titles = nodes(tree).filter(n => n.props.className === 'claw-group-title').map(n => n.children[0]);
  assert.deepEqual(titles, toolboxGroups.map(group => group.title));
  const state = nodes(tree).find(n => String(n.props.className).startsWith('claw-module-state'));
  assert.equal(state.props.className, 'claw-module-state off');
  assert.deepEqual(state.children, ['固件自动']);
  // Collapsed: no module content is mounted.
  assert.equal(nodes(tree).filter(n => n.props.role === 'region').length, 0);
});

test('a module with an unlisted group still shows under a trailing group', () => {
  const { ModuleStack } = createToolboxUI(React, ui);
  const tree = ModuleStack({ active: null, onSelect: () => {}, contents: {}, modules: [{ id: 'vibe', group: 'future', title: '振动强度', subtitle: '' }] });
  assert.ok(text(tree).includes('其他'));
  assert.ok(text(tree).includes('振动强度'));
});

test('the toolbox opens with every module collapsed', () => {
  assert.equal(createModuleSelection().value, null);
});

test('power summaries describe power, OSD and fan without expanding', () => {
  assert.deepEqual(powerSummaries({}), {});
  const draft = { enabled: true, mode: 'manual', manual: { tdp: 12 }, ac: { tdp: 25 }, dc: { tdp: 15 },
    osd: { enabled: true, level: 2 }, fan: { mode: 'quiet', full: true } };
  assert.deepEqual(powerSummaries({ view: { charger: true }, draft }), {
    power: { text: '手动 12 W' }, osd: { text: '第 2 档' }, fan: { text: '静音 · 全速', off: false } });
  const off = powerSummaries({ view: { charger: false }, draft: { ...draft, enabled: false, osd: { enabled: false }, fan: { mode: 'firmware', full: true } } });
  assert.deepEqual(off, { power: { text: '未接管', off: true }, osd: { text: '关', off: true }, fan: { text: '固件自动', off: true } });
  assert.equal(powerSummaries({ view: { charger: false }, draft: { ...draft, mode: 'auto' } }).power.text, '预设 15 W');
});
