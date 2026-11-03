import test from "node:test";
import assert from "node:assert/strict";
import { createPowerController, createPowerPanel, powerSummaries, powerTiles } from "../plugin/power.js";

test("core setting controller reports failed writes and restores through its dedicated API", async () => {
  const calls = [];
  const controller = createPowerController({ async call(method, ...args) {
    calls.push([method, ...args]);
    if (method === "set_ecore") throw new Error("permission denied");
    return { settings: {}, ecore: { enabled: false, pending: true } };
  } });
  await controller.setEcore(true);
  assert.match(controller.value.message, /permission denied/);
  assert.equal(controller.value.busy, false);
  assert.equal(controller.value.view, null);
  await controller.restoreEcore();
  assert.deepEqual(calls, [["set_ecore", true], ["restore_ecore"]]);
  assert.equal(controller.value.view.ecore.enabled, false);
  assert.match(controller.value.message, /重启/);
});

test("core setting rows keep the restart reminder, preserve restore and disable unsupported topology", () => {
  const value = state();
  value.view.ecore = { enabled: false, pending: null, status: "", supported: false, backup: true };
  const h = (type, props, ...children) => ({ type, props: props ?? {}, children: children.flat() });
  const React = { createElement: h, Fragment: "Fragment", useState: f => [f(), () => {}], useEffect() {} };
  const ui = new Proxy({}, { get: (_, name) => name });
  const Panel = createPowerPanel(React, ui, { value, subscribe() {}, refresh() {} }, "Status", "Section");
  const walk = n => n && typeof n === "object" ? [n, ...n.children.flatMap(walk)] : [];
  const rows = walk(Panel({ section: "power" }));
  const core = rows.find(n => n.props.label === "模拟全核心 E 核");
  assert.equal(core.props.checked, false);
  assert.equal(core.props.disabled, true);
  assert.match(core.props.description, /重启/);
  const restore = rows.find(n => n.children.includes("恢复原始核心设置"));
  assert.equal(restore.props.disabled, true);
});

function state(mode = "autotdp") {
  const draft = { enabled: true, mode, autotdp: { target: 60, floor: 12, acMax: 35, dcMax: 25 },
    smart: { profile: "balanced" }, manual: { tdp: 20, freq: 3000 }, ac: { tdp: 35, freq: 2750 }, dc: { tdp: 25, freq: 2750 },
    boost: 1, quick: [15, 20, 30], fan: { mode: "balanced" }, osd: { enabled: true, level: 2 } };
  return { draft, view: { settings: draft, charger: false, limits: { tdpMin: 8, tdpMax: 35, boostMax: 15, freq: [0, 3000] },
    autotdp: { target: 60, fps: 59, reason: "帧率达标 · 观察稳定性" }, endurance: { available: true }, actual: { pl1: 24 } } };
}

test("power quick tile cycles Auto and manual presets without Intel", () => {
  let patch;
  const controller = { edit(value) { patch = value; } };
  assert.equal(powerSummaries(state()).power.text, "Auto · 60 帧");
  powerTiles(state(), controller, () => {})[0].onPress();
  assert.equal(patch.mode, "manual");
  assert.equal(patch.manual.tdp, 15);
  const last = state("manual"); last.draft.manual.tdp = 30;
  powerTiles(last, controller, () => {})[0].onPress();
  assert.equal(patch.mode, "autotdp");
});

test("Auto controls preserve the target and enforce floor/ceiling ranges", () => {
  let patch;
  const React = { createElement: (type, props, ...children) => ({ type, props, children }), Fragment: "Fragment",
    useState: fn => [fn(), () => {}], useEffect: () => {} };
  const ui = Object.fromEntries(["PanelSection", "PanelSectionRow", "DropdownItem", "SliderField", "ToggleField", "ButtonItem"].map(x => [x, x]));
  const controller = { value: state(), edit: value => { patch = value; } };
  const Panel = createPowerPanel(React, ui, controller, "StatusText", "SectionLabel");
  const nodes = [];
  function walk(node) {
    if (Array.isArray(node)) return node.forEach(walk);
    if (!node || typeof node !== "object") return;
    nodes.push(node); node.children?.forEach(walk);
  }
  walk(Panel({}));
  const byLabel = label => nodes.find(n => n.props?.label === label);
  assert.deepEqual(byLabel("调节方式").props.rgOptions.map(o => o.data), ["autotdp", "auto", "manual"]);
  assert.equal(byLabel("功耗下限").props.max, 25);
  assert.equal(byLabel("插电上限").props.min, 12);
  assert.equal(byLabel("电池上限").props.min, 12);
  assert.equal(byLabel("CPU 频率"), undefined);
  byLabel("目标帧率").props.onChange(90);
  assert.deepEqual(patch.autotdp, { target: 90, floor: 12, acMax: 35, dcMax: 25 });
});

test("fan quick tile cycles through exactly the three presets", () => {
  const value = state();
  const modes = [];
  for (let i = 0; i < 3; i++) {
    const tile = powerTiles(value, { edit(patch) { value.draft.fan = patch.fan; } }, () => {}).find(t => t.id === "fan");
    assert.equal(tile.steps, 3);
    tile.onPress(); modes.push(value.draft.fan.mode);
  }
  assert.deepEqual(modes, ["performance", "quiet", "balanced"]);
});
