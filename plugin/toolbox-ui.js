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

export const toolboxStyles = `
.claw-toolbox-ui { --claw-raise:#1f2b38; --claw-raise-2:#283746; --claw-line:#2a3644; --claw-fg:#e8eef4; --claw-fg-2:#a8b6c4; --claw-fg-3:#6f7f90; --claw-accent:#ffb347; --claw-accent-ink:#1d1407; --claw-on:#6fd49a; --claw-warn:#ff8a65; --claw-num:"Bahnschrift","Barlow Semi Condensed","Arial Narrow",sans-serif; color:var(--claw-fg); padding-bottom:16px; }
.claw-toolbox-ui .claw-home { display:flex; flex-direction:column; gap:10px; padding:4px 16px 0; }
.claw-toolbox-ui .claw-game { display:flex; align-items:center; gap:6px; font-size:12px; color:var(--claw-fg-2); min-width:0; }
.claw-toolbox-ui .claw-game svg { width:11px; height:11px; color:var(--claw-on); flex-shrink:0; }
.claw-toolbox-ui .claw-game b { color:var(--claw-fg); font-weight:500; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.claw-toolbox-ui .claw-game .claw-chip { margin-left:auto; display:flex; align-items:center; gap:3px; padding:1px 8px; border-radius:99px; background:var(--claw-raise); white-space:nowrap; }
.claw-toolbox-ui .claw-game .claw-chip svg { color:var(--claw-fg-2); }
.claw-toolbox-ui .claw-strip { display:grid; grid-template-columns:repeat(4,1fr); background:var(--claw-raise); border-radius:8px; padding:9px 2px 8px; }
.claw-toolbox-ui .claw-metric { display:flex; flex-direction:column; align-items:center; gap:1px; position:relative; min-width:0; }
.claw-toolbox-ui .claw-metric + .claw-metric:before { content:""; position:absolute; left:0; top:5px; bottom:5px; width:1px; background:var(--claw-line); }
.claw-toolbox-ui .claw-metric strong { font-family:var(--claw-num); font-size:20px; font-weight:600; line-height:1.15; font-variant-numeric:tabular-nums; white-space:nowrap; }
.claw-toolbox-ui .claw-metric strong small { font-size:11px; color:var(--claw-fg-2); margin-left:1px; font-weight:500; }
.claw-toolbox-ui .claw-metric strong.na { color:var(--claw-fg-3); }
.claw-toolbox-ui .claw-metric strong.good { color:var(--claw-on); }
.claw-toolbox-ui .claw-metric span { font-size:11px; color:var(--claw-fg-3); }
.claw-toolbox-ui .claw-banner { display:flex; gap:8px; font-size:12px; line-height:1.6; background:rgba(255,138,101,.08); border:1px solid rgba(255,138,101,.35); border-radius:8px; padding:7px 10px; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-banner i { font-style:normal; color:var(--claw-warn); font-weight:700; }
.claw-toolbox-ui .claw-label { display:flex; justify-content:space-between; padding:4px 2px 0; font-size:11px; letter-spacing:1px; color:var(--claw-fg-3); font-weight:500; }
.claw-toolbox-ui .claw-label span { letter-spacing:0; }
.claw-toolbox-ui .claw-tiles { display:grid; grid-template-columns:1fr 1fr; gap:8px; }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-tile { display:flex; flex-direction:column; align-items:stretch; gap:2px; width:100%; min-width:0; min-height:70px; margin:0; padding:9px 10px 8px; border:0; border-radius:8px; box-shadow:none; background:var(--claw-raise); color:var(--claw-fg); text-align:left; }
.claw-toolbox-ui .claw-tile .claw-tile-label { display:flex; align-items:center; gap:5px; font-size:12px; color:var(--claw-fg-2); }
.claw-toolbox-ui .claw-tile .claw-tile-label svg { width:13px; height:13px; flex-shrink:0; }
.claw-toolbox-ui .claw-tile .claw-tile-value { font-size:15px; font-weight:500; line-height:1.4; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.claw-toolbox-ui .claw-tile .claw-dots { display:flex; gap:3px; margin-top:3px; }
.claw-toolbox-ui .claw-tile .claw-dots i { flex:1; max-width:16px; height:3px; border-radius:2px; background:currentColor; opacity:.22; }
.claw-toolbox-ui .claw-tile .claw-dots i.f { opacity:.85; }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-tile.on { background:var(--claw-accent); color:var(--claw-accent-ink); }
.claw-toolbox-ui .claw-tile.on .claw-tile-label { color:#4a3210; }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-tile.gpfocus, .claw-toolbox-ui button.DialogButton._DialogLayout.claw-tile:focus-visible { outline:2px solid #f4f7fa; outline-offset:1px; background:var(--claw-raise-2); }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-tile.on.gpfocus, .claw-toolbox-ui button.DialogButton._DialogLayout.claw-tile.on:focus-visible { background:#ffc46e; }
.claw-toolbox-ui .claw-rows { display:flex; flex-direction:column; background:var(--claw-raise); border-radius:8px; overflow:hidden; }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-row { display:flex; align-items:center; gap:10px; width:100%; min-width:0; min-height:44px; margin:0; padding:8px 12px; border:0; border-top:1px solid var(--claw-line); border-radius:0; box-shadow:none; background:transparent; color:var(--claw-fg); text-align:left; }
.claw-toolbox-ui .claw-row-wrap:first-child button.DialogButton._DialogLayout.claw-row { border-top:0; }
.claw-toolbox-ui .claw-row .claw-row-icon { display:flex; width:20px; height:20px; flex-shrink:0; color:var(--claw-fg-2); }
.claw-toolbox-ui .claw-row .claw-row-icon svg { width:20px; height:20px; }
.claw-toolbox-ui .claw-row .claw-row-name { flex:1; min-width:0; font-size:14px; }
.claw-toolbox-ui .claw-state { flex-shrink:0; max-width:45%; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:12px; color:var(--claw-on); }
.claw-toolbox-ui .claw-state.off { color:var(--claw-fg-3); }
.claw-toolbox-ui .claw-row .claw-chev { color:var(--claw-fg-3); font-size:15px; }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-row.gpfocus, .claw-toolbox-ui button.DialogButton._DialogLayout.claw-row:focus-visible { background:#f4f7fa; color:#121a23; }
.claw-toolbox-ui .claw-row.gpfocus .claw-row-icon, .claw-toolbox-ui .claw-row.gpfocus .claw-chev, .claw-toolbox-ui .claw-row:focus-visible .claw-row-icon { color:#34414e; }
.claw-toolbox-ui .claw-row.gpfocus .claw-state { color:#2d6a44; }
.claw-toolbox-ui .claw-row.gpfocus .claw-state.off { color:#56606b; }
.claw-toolbox-ui .claw-sub-head { display:flex; align-items:center; gap:8px; margin:0 16px 6px; padding-bottom:6px; border-bottom:1px solid var(--claw-line); }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-back { display:flex; align-items:center; gap:4px; width:auto; min-width:0; flex-shrink:0; margin:0; padding:5px 10px 5px 4px; border:0; border-radius:6px; box-shadow:none; background:transparent; color:var(--claw-fg-2); font-size:13px; }
.claw-toolbox-ui .claw-back svg { width:16px; height:16px; }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-back.gpfocus, .claw-toolbox-ui button.DialogButton._DialogLayout.claw-back:focus-visible { background:#f4f7fa; color:#121a23; }
.claw-toolbox-ui .claw-sub-title { flex:1; min-width:0; font-size:15px; font-weight:500; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.claw-toolbox-ui .claw-module-content { padding-bottom:12px; }
.claw-toolbox-ui .claw-section-label { padding:12px 0 5px; color:var(--claw-fg-3); font-size:11px; font-weight:500; letter-spacing:1px; border-bottom:1px solid var(--claw-line); margin:4px 0 4px; }
.claw-toolbox-ui .claw-field { padding:8px 0; line-height:1.5; min-width:0; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-field-label { display:block; color:var(--claw-fg-2); font-size:12px; font-weight:500; margin-bottom:3px; }
.claw-toolbox-ui .claw-field-value { color:var(--claw-fg); font-size:14px; }
.claw-toolbox-ui .claw-module-status { margin:0 0 8px; padding:0; color:var(--claw-fg-2); font-size:13px; line-height:20px; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-hint { color:var(--claw-fg-2); font-size:12px; line-height:18px; padding:4px 0 8px; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-action-grid { display:grid; grid-template-columns:1fr 1fr; gap:8px; }
.claw-toolbox-ui .claw-action-grid button { min-width:0; width:100%; }
.claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout { background:var(--claw-accent); color:var(--claw-accent-ink); font-weight:650; }
.claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout:disabled, .claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout.Disabled { background:#2b3946; color:#9aa9b7; }
.claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout.gpfocus { background:#ffd9a0; color:var(--claw-accent-ink); }
@media (prefers-reduced-motion:reduce) { .claw-toolbox-ui * { scroll-behavior:auto; } }
`;

export function createToolboxUI(React, ui) {
  const h = React.createElement;
  const Icon = ({ name }) => h("svg", { viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 1.8,
    strokeLinecap: "round", strokeLinejoin: "round", "aria-hidden": true, dangerouslySetInnerHTML: { __html: iconPaths[name] ?? "" } });
  const stateSpan = state => state?.text ? h("span", { className: state.off ? "claw-state off" : "claw-state" }, state.text) : null;

  // metrics: [{ label, value, unit, tone }] with value null shown as a dash, never a guess.
  // tiles: [{ id, label, icon, value, steps, filled, on, onPress }]; states: module id -> { text, off }.
  function Home({ game, chip, metrics = [], banner, tiles = [], states = {}, onOpen, modules = toolboxModules, groups = toolboxGroups }) {
    const known = new Set(groups.map(group => group.id));
    const sets = [...groups, { id: "other", title: "其他" }]
      .map(group => ({ group, items: modules.filter(module => group.id === "other" ? !known.has(module.group) : module.group === group.id) }))
      .filter(set => set.items.length);
    return h(ui.Focusable, { className: "claw-home", "flow-children": "column" },
      (game || chip) && h("div", { className: "claw-game" },
        game && h(Icon, { name: "play" }), game && h("b", null, game.name), game?.detail && h("span", null, `· ${game.detail}`),
        chip && h("span", { className: "claw-chip" }, chip.icon && h(Icon, { name: chip.icon }), chip.text)),
      metrics.length > 0 && h("div", { className: "claw-strip", role: "group", "aria-label": "实时读数" }, metrics.map(metric =>
        h("div", { key: metric.label, className: "claw-metric" },
          h("strong", { className: metric.value == null ? "na" : metric.tone ?? "" }, metric.value ?? "—", metric.value != null && metric.unit && h("small", null, metric.unit)),
          h("span", null, metric.label)))),
      banner && h("div", { className: "claw-banner", role: "status" }, h("i", null, "!"), h("span", null, banner)),
      tiles.length > 0 && h("div", { className: "claw-label" }, "快捷切换", h("span", null, "按 A 切到下一档")),
      tiles.length > 0 && h(ui.Focusable, { className: "claw-tiles", "flow-children": "grid" }, tiles.map(tile =>
        h(ui.DialogButton, { key: tile.id, className: tile.on ? "claw-tile on" : "claw-tile", onClick: tile.onPress, disabled: tile.disabled, "aria-label": `${tile.label}：${tile.value}` },
          h("span", { className: "claw-tile-label" }, h(Icon, { name: tile.icon }), tile.label),
          h("span", { className: "claw-tile-value" }, tile.value),
          tile.steps > 0 && h("span", { className: "claw-dots", "aria-hidden": true },
            Array.from({ length: tile.steps }, (_, i) => h("i", { key: i, className: i < tile.filled ? "f" : "" })))))),
      sets.map(({ group, items }) => h(React.Fragment, { key: group.id },
        h("div", { className: "claw-label" }, group.title),
        h(ui.Focusable, { className: "claw-rows", "flow-children": "column", role: "group", "aria-label": group.title }, items.map(module =>
          h("div", { key: module.id, className: "claw-row-wrap" }, h(ui.DialogButton, { className: "claw-row", onClick: () => onOpen(module.id) },
            h("span", { className: "claw-row-icon" }, h(Icon, { name: module.icon })),
            h("span", { className: "claw-row-name" }, module.title),
            stateSpan(states[module.id]),
            h("span", { className: "claw-chev", "aria-hidden": true }, "›"))))))));
  }
  // A module's full settings, on its own page; B (cancel) or the back button returns home.
  function SubPage({ module, state, onBack, children }) {
    return h(ui.Focusable, { className: "claw-sub", "flow-children": "column", onCancelButton: onBack, onCancelActionDescription: "返回" },
      h("div", { className: "claw-sub-head" },
        h(ui.DialogButton, { className: "claw-back", onClick: onBack, "aria-label": "返回首页" }, h(Icon, { name: "back" }), "返回"),
        h("span", { className: "claw-sub-title" }, module.title), stateSpan(state)),
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
