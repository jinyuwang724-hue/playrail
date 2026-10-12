// Home page: live readings, quick tiles and a grouped list of every module; each
// module opens on its own page. A new module (e.g. vibration strength) only needs
// an entry here with its group; nothing relies on fixed slots.
export const toolboxGroups = [
  { id: "play", title: "游戏与硬件" },
  { id: "system", title: "系统与工具" },
];

export const toolboxModules = [
  { id: "power", group: "play", title: "性能与功耗", icon: "power" },
  { id: "magpie", group: "play", title: "画面缩放", icon: "magpie" },
  { id: "osd", group: "play", title: "性能显示", icon: "osd" },
  { id: "fan", group: "play", title: "风扇", icon: "fan" },
  { id: "mapping", group: "play", title: "按键映射", icon: "mapping" },
  { id: "haptics", group: "play", title: "震动", icon: "haptics" },
  { id: "games", group: "system", title: "添加游戏", icon: "games" },
  { id: "proxy", group: "system", title: "网络代理", icon: "proxy" },
  { id: "compatibility", group: "system", title: "兼容性修复", icon: "compatibility" },
];

const iconPaths = {
  power: '<path d="M13 2 4 14h7l-1 8 9-12h-7z"/>',
  magpie: '<rect x="3" y="5" width="18" height="12" rx="2"/><path d="M8 21h8M12 17v4"/>',
  osd: '<path d="M4 19V9M10 19V5M16 19v-7M22 19H2"/>',
  fan: '<circle cx="12" cy="12" r="2"/><path d="M12 10c0-4 1-7 4-7 2 0 2 3-1 5M14 12c4 0 7 1 7 4 0 2-3 2-5-1M12 14c0 4-1 7-4 7-2 0-2-3 1-5M10 12c-4 0-7-1-7-4 0-2 3-2 5 1"/>',
  mapping: '<rect x="2" y="7" width="20" height="11" rx="5"/><path d="M7 10v4M5 12h4M16 11h.01M18 13h.01"/>',
  haptics: '<path d="M8 6h8v12H8zM4 9v6M20 9v6M2 11v2M22 11v2"/>',
  games: '<path d="M4 4h6v16H4zM12 4h3v16h-3zM17 5l3 1-3 14-3-1z"/>',
  proxy: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18"/>',
  compatibility: '<path d="M14 7a4 4 0 0 0-5 5l-6 6 3 3 6-6a4 4 0 0 0 5-5l-2 2-3-3z"/>',
  play: '<path d="M7 4v16l13-8z" fill="currentColor"/>',
  plug: '<path d="M9 2v6M15 2v6M6 8h12v4a6 6 0 0 1-12 0zM12 18v4"/>',
  back: '<path d="M15 5l-7 7 7 7"/>',
  chevron: '<path d="M9 5l7 7-7 7"/>',
  warn: '<path d="M12 3 2 20h20z"/><path d="M12 10v4M12 17h.01"/>',
};

export function createModuleSelection(initial = null) {
  let value = initial;
  const listeners = new Set();
  return {
    get value() { return value; },
    select(id) {
      if (id === value || (id !== null && !toolboxModules.some(module => module.id === id))) return;
      value = id;
      listeners.forEach(listener => listener(value));
    },
    subscribe(listener) { listeners.add(listener); return () => listeners.delete(listener); },
  };
}

// SteamOS colours only (read from Steam's GamepadUI stylesheet): text #dcdedf / #b8bcbf /
// #8b929a, Steam blue #1a9fff for "on", white with #0e141b text for controller focus.
// No background colour of our own: the page sits on whatever the side panel (and any
// CSS Loader theme) paints. Rows and section titles are Steam's own Field and PanelSection.
export const toolboxStyles = `
.claw-toolbox-ui { --claw-fg:#dcdedf; --claw-fg-2:#b8bcbf; --claw-fg-3:#8b929a; --claw-off:#67707b; --claw-blue:#1a9fff; --claw-sep:rgba(255,255,255,.1); --claw-warn:#ffc82c; color:var(--claw-fg); padding-bottom:8px; }
.claw-toolbox-ui .claw-game { display:flex; align-items:baseline; gap:6px; min-width:0; padding-bottom:10px; font-size:12px; line-height:16px; color:var(--claw-fg-3); }
.claw-toolbox-ui .claw-game b { color:var(--claw-fg); font-weight:500; font-size:13px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.claw-toolbox-ui .claw-game .claw-source { margin-left:auto; white-space:nowrap; letter-spacing:.5px; }
.claw-toolbox-ui .claw-strip { display:grid; grid-template-columns:repeat(4,1fr); padding:2px 0 12px; border-bottom:1px solid var(--claw-sep); }
.claw-toolbox-ui .claw-metric { display:flex; flex-direction:column; gap:2px; min-width:0; }
.claw-toolbox-ui .claw-metric strong { font-size:20px; line-height:24px; font-weight:400; color:#fff; font-variant-numeric:tabular-nums; white-space:nowrap; }
.claw-toolbox-ui .claw-metric strong small { font-size:12px; color:var(--claw-fg-3); margin-left:2px; }
.claw-toolbox-ui .claw-metric strong.na { color:var(--claw-off); }
.claw-toolbox-ui .claw-metric span { font-size:11px; line-height:14px; color:var(--claw-fg-3); letter-spacing:.5px; }
.claw-toolbox-ui .claw-banner { display:flex; align-items:flex-start; gap:8px; margin-top:10px; font-size:12px; line-height:16px; color:var(--claw-fg-2); overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-banner svg { width:14px; height:14px; flex-shrink:0; margin-top:1px; color:var(--claw-warn); }
.claw-toolbox-ui .claw-tiles { display:grid; grid-template-columns:1fr 1fr; gap:8px; }
.claw-toolbox-ui .claw-tiles button.DialogButton.claw-tile.claw-tile { display:flex; flex-direction:column; align-items:stretch; gap:3px; width:100%; min-width:0 !important; min-height:74px; margin:0; padding:10px 12px; border:0; border-radius:4px; box-shadow:none; background:rgba(0,0,0,.22); color:var(--claw-fg); text-align:left; position:relative; overflow:hidden; }
.claw-toolbox-ui .claw-tile-label { display:flex; align-items:center; gap:6px; font-size:12px; line-height:16px; color:var(--claw-fg-3); }
.claw-toolbox-ui .claw-tile-label svg { width:15px; height:15px; flex-shrink:0; }
.claw-toolbox-ui .claw-tile-value { font-size:17px; line-height:22px; font-weight:500; color:var(--claw-fg-2); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.claw-toolbox-ui .claw-dots { display:flex; gap:3px; margin-top:4px; }
.claw-toolbox-ui .claw-dots i { flex:1; max-width:18px; height:3px; border-radius:2px; background:rgba(255,255,255,.12); }
.claw-toolbox-ui .claw-dots i.f { background:var(--claw-fg-2); }
.claw-toolbox-ui .claw-tile.on .claw-tile-label svg { color:var(--claw-blue); }
.claw-toolbox-ui .claw-tile.on .claw-tile-value { color:#fff; }
.claw-toolbox-ui .claw-tile.on .claw-dots i.f { background:var(--claw-blue); }
.claw-toolbox-ui .claw-tile.on:after { content:""; position:absolute; left:0; right:0; bottom:0; height:3px; background:var(--claw-blue); }
.claw-toolbox-ui .claw-tiles button.DialogButton.claw-tile.claw-tile.gpfocus { background:#fff; box-shadow:0 4px 4px rgba(0,0,0,.25); }
.claw-toolbox-ui .claw-tile.gpfocus .claw-tile-label { color:#3d4450; }
.claw-toolbox-ui .claw-tile.gpfocus .claw-tile-value { color:#0e141b; }
.claw-toolbox-ui .claw-tile.gpfocus .claw-dots i { background:rgba(14,20,27,.15); }
.claw-toolbox-ui .claw-tile.gpfocus .claw-dots i.f { background:#0e141b; }
.claw-toolbox-ui .claw-tile.on.gpfocus .claw-dots i.f { background:var(--claw-blue); }
.claw-toolbox-ui .claw-row-icon { display:flex; width:20px; height:20px; color:var(--claw-fg-2); }
.claw-toolbox-ui .claw-row-icon svg { width:20px; height:20px; }
.claw-toolbox-ui .claw-chev { display:flex; color:var(--claw-fg-3); }
.claw-toolbox-ui .claw-chev svg { width:14px; height:14px; }
.claw-toolbox-ui .claw-state { max-width:120px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:13px; color:var(--claw-fg-3); }
.claw-toolbox-ui .claw-sub-head { display:flex; align-items:center; gap:10px; margin:0 16px 12px; min-width:0; }
.claw-toolbox-ui button.DialogButton.claw-back.claw-back { display:flex; align-items:center; justify-content:center; width:32px; min-width:0 !important; height:32px; flex-shrink:0; margin:0; padding:0; border:0; border-radius:4px; box-shadow:none; background:rgba(0,0,0,.22); color:var(--claw-fg); }
.claw-toolbox-ui .claw-back svg { width:16px; height:16px; }
.claw-toolbox-ui button.DialogButton.claw-back.claw-back.gpfocus { background:#fff; color:#0e141b; }
.claw-toolbox-ui .claw-sub-title { flex:1; min-width:0; font-size:16px; line-height:22px; font-weight:600; letter-spacing:.5px; color:rgba(255,255,255,.7); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.claw-toolbox-ui .claw-section-label { padding:14px 0 6px; color:rgba(255,255,255,.7); font-size:14px; line-height:20px; font-weight:600; letter-spacing:.5px; }
.claw-toolbox-ui .claw-field { padding:8px 0; line-height:1.5; min-width:0; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-field-label { display:block; color:var(--claw-fg-2); font-size:12px; margin-bottom:3px; }
.claw-toolbox-ui .claw-field-value { color:var(--claw-fg); font-size:14px; }
.claw-toolbox-ui .claw-module-status { margin:0 0 6px; padding:0; color:var(--claw-fg-2); font-size:13px; line-height:18px; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-hint { color:var(--claw-fg-2); font-size:12px; line-height:16px; padding:4px 0 8px; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-action-grid { display:grid; grid-template-columns:1fr 1fr; gap:6px; }
.claw-toolbox-ui .claw-action-grid button { min-width:0; width:100%; }
.claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout { background:var(--claw-blue); color:#fff; }
.claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout:disabled, .claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout.Disabled { background:rgba(255,255,255,.15); color:var(--claw-off); }
.claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout.gpfocus { background:#fff; color:#0e141b; }
@media (prefers-reduced-motion:reduce) { .claw-toolbox-ui * { scroll-behavior:auto; } }
`;

export function createToolboxUI(React, ui) {
  const h = React.createElement;
  const Icon = ({ name }) => h("svg", { viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 1.8,
    strokeLinecap: "round", strokeLinejoin: "round", "aria-hidden": true, dangerouslySetInnerHTML: { __html: iconPaths[name] ?? "" } });

  // metrics: [{ label, value, unit }] with value null shown as a dash, never a guess.
  // tiles: [{ id, label, icon, value, steps, filled, on, onPress }].
  function Home({ game, source, metrics = [], banner, tiles = [], onOpen, modules = toolboxModules, groups = toolboxGroups }) {
    const known = new Set(groups.map(group => group.id));
    const sets = [...groups, { id: "other", title: "其他" }]
      .map(group => ({ group, items: modules.filter(module => group.id === "other" ? !known.has(module.group) : module.group === group.id) }))
      .filter(set => set.items.length);
    return h(ui.Focusable, { className: "claw-home", "flow-children": "column" },
      h(ui.PanelSection, null,
        (game || source) && h("div", { className: "claw-game" },
          game && h("b", null, game.name), game?.detail && h("span", null, `· ${game.detail}`),
          source && h("span", { className: "claw-source" }, source.text)),
        metrics.length > 0 && h("div", { className: "claw-strip", role: "group", "aria-label": "实时读数" }, metrics.map(metric =>
          h("div", { key: metric.label, className: "claw-metric" },
            h("strong", { className: metric.value == null ? "na" : "" }, metric.value ?? "—", metric.value != null && metric.unit && h("small", null, metric.unit)),
            h("span", null, metric.label)))),
        banner && h("div", { className: "claw-banner", role: "status" }, h(Icon, { name: "warn" }), h("span", null, banner))),
      tiles.length > 0 && h(ui.PanelSection, { title: "快捷切换" },
        h(ui.Focusable, { className: "claw-tiles", "flow-children": "grid" }, tiles.map(tile =>
          h(ui.DialogButton, { key: tile.id, className: tile.on ? "claw-tile on" : "claw-tile", onClick: tile.onPress, disabled: tile.disabled, "aria-label": `${tile.label}：${tile.value}` },
            h("span", { className: "claw-tile-label" }, h(Icon, { name: tile.icon }), tile.label),
            h("span", { className: "claw-tile-value" }, tile.value),
            tile.steps > 0 && h("span", { className: "claw-dots", "aria-hidden": true },
              Array.from({ length: tile.steps }, (_, i) => h("i", { key: i, className: i < tile.filled ? "f" : "" }))))))),
      sets.map(({ group, items }) => h(ui.PanelSection, { key: group.id, title: group.title }, items.map(module =>
        h(ui.PanelSectionRow, { key: module.id }, h(ui.Field, {
          className: "claw-row", label: module.title, icon: h("span", { className: "claw-row-icon" }, h(Icon, { name: module.icon })),
          onClick: () => onOpen(module.id), bottomSeparator: "standard",
        }, h("span", { className: "claw-chev", "aria-hidden": true }, h(Icon, { name: "chevron" }))))))));
  }
  // A module's full settings, on its own page; B (cancel) or the back button returns home.
  function SubPage({ module, state, onBack, children }) {
    return h(ui.Focusable, { className: "claw-sub", "flow-children": "column", onCancelButton: onBack, onCancelActionDescription: "返回" },
      h("div", { className: "claw-sub-head" },
        h(ui.DialogButton, { className: "claw-back", onClick: onBack, "aria-label": "返回首页" }, h(Icon, { name: "back" })),
        h("span", { className: "claw-sub-title" }, module.title),
        state?.text && h("span", { className: "claw-state" }, state.text)),
      h(ui.Focusable, { className: "claw-module-content", "flow-children": "column", role: "region", "aria-label": module.title }, children));
  }
  function SectionLabel({ children }) {
    return h(ui.PanelSectionRow, null, h("div", { className: "claw-section-label" }, children));
  }
  function StatusText({ children }) {
    return h(ui.PanelSectionRow, null, h("p", { className: "claw-module-status", role: "status" }, children));
  }
  return { Home, SubPage, SectionLabel, StatusText };
}
