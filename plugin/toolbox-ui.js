export const toolboxModules = [
  { id: "magpie", title: "缩放与锐化", subtitle: "等比铺满 · Magpie", icon: "Display", accent: "#8abfff" },
  { id: "power", title: "性能与功耗", subtitle: "功耗 · CPU 频率", icon: "Battery", accent: "#f2a07b" },
  { id: "games", title: "添加游戏", subtitle: "非 Steam 游戏", icon: "Library", accent: "#79c7ff" },
  { id: "proxy", title: "网络代理", subtitle: "订阅与节点", icon: "Globe", accent: "#74dbc6" },
  { id: "mapping", title: "按键映射", subtitle: "掌机快捷按键", icon: "Controller", accent: "#f3c784" },
  { id: "haptics", title: "导航反馈", subtitle: "手柄震动", icon: "Haptics", accent: "#c5b2f5" },
  { id: "compatibility", title: "兼容性修复", subtitle: "Steam 启动与 Game Bar", icon: "Controller", accent: "#92d2b1" },
];

export function createModuleSelection(initial = "games") {
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
.claw-toolbox-ui { color:#edf2f7; padding-bottom:16px; }
.claw-toolbox-ui .claw-module-heading-row { margin:0 16px; padding:4px 0; border-top:1px solid #34414e; }
.claw-toolbox-ui .claw-module-section:first-child .claw-module-heading-row { border-top:0; }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-module-button { display:flex; align-items:center; gap:10px; width:100%; min-width:0; min-height:62px; margin:0; padding:10px 5px; border:0; border-radius:3px; box-shadow:none; background:transparent; color:#edf2f7; text-align:left; }
.claw-toolbox-ui .claw-module-copy { flex:1; min-width:0; }
.claw-toolbox-ui .claw-module-title { display:block; font-size:16px; font-weight:650; line-height:22px; }
.claw-toolbox-ui .claw-module-icon { display:flex; align-items:center; flex-shrink:0; color:var(--claw-accent); }
.claw-toolbox-ui .claw-module-icon svg { width:20px; height:20px; }
.claw-toolbox-ui .claw-module-subtitle { display:block; margin-top:2px; font-size:12px; line-height:17px; color:#b8c5d1; }
.claw-toolbox-ui .claw-module-chevron { display:flex; flex-shrink:0; color:#b8c5d1; }
.claw-toolbox-ui .claw-module-chevron svg { width:14px; height:14px; }
.claw-toolbox-ui button.DialogButton._DialogLayout.claw-module-button.gpfocus, .claw-toolbox-ui button.DialogButton._DialogLayout.claw-module-button:focus-visible { background:#edf2f7; color:#17212b; outline:2px solid #edf2f7; outline-offset:0; }
.claw-toolbox-ui .claw-module-button.gpfocus .claw-module-subtitle, .claw-toolbox-ui .claw-module-button:focus-visible .claw-module-subtitle { color:#34414e; }
.claw-toolbox-ui .claw-module-button.gpfocus .claw-module-icon, .claw-toolbox-ui .claw-module-button:focus-visible .claw-module-icon, .claw-toolbox-ui .claw-module-button.gpfocus .claw-module-chevron, .claw-toolbox-ui .claw-module-button:focus-visible .claw-module-chevron { color:#34526e; }
.claw-toolbox-ui .claw-module-content { padding-bottom:12px; }
.claw-toolbox-ui .claw-section-label { padding:8px 0 7px; color:#b8c5d1; font-size:12px; font-weight:650; letter-spacing:.4px; border-bottom:1px solid #34414e; margin:8px 0 5px; }
.claw-toolbox-ui .claw-field { padding:8px 0; line-height:1.5; min-width:0; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-field-label { display:block; color:#b8c5d1; font-size:12px; font-weight:500; margin-bottom:3px; }
.claw-toolbox-ui .claw-field-value { color:#edf2f7; font-size:14px; }
.claw-toolbox-ui .claw-module-status { margin:0 0 12px; padding:0; color:#b8c5d1; font-size:13px; line-height:20px; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-hint { color:#b8c5d1; font-size:12px; line-height:18px; padding:4px 0 8px; overflow-wrap:anywhere; }
.claw-toolbox-ui .claw-action-grid { display:grid; grid-template-columns:1fr 1fr; gap:8px; }
.claw-toolbox-ui .claw-action-grid button { min-width:0; width:100%; }
.claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout { background:#67b8ed; color:#101b24; font-weight:650; }
.claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout:disabled, .claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout.Disabled { background:#2b3946; color:#9aa9b7; }
.claw-toolbox-ui .claw-primary-action button.DialogButton._DialogLayout.gpfocus { background:#d9f0ff; color:#101b24; }
.claw-toolbox-ui .claw-content { --claw-accent:#79c7ff; }
@media (prefers-reduced-motion:reduce) { .claw-toolbox-ui * { scroll-behavior:auto; } }
`;

export function createToolboxUI(React, ui) {
  const h = React.createElement;
  // Render each registered module in sequence; adding a module never needs a grid slot.
  function ModuleStack({ active, onSelect, contents, modules = toolboxModules }) {
    return h(ui.Focusable, { className: "claw-module-list", "flow-children": "column", "aria-label": "工具箱功能模块" },
      modules.map(module => {
        const expanded = active === module.id;
        const headingId = `claw-module-${module.id}`;
        const contentId = `${headingId}-content`;
        return h(ui.Focusable, { key: module.id, className: "claw-module-section", "flow-children": "column", style: { "--claw-accent": module.accent } },
          h("div", { className: "claw-module-heading-row" }, h(ui.DialogButton, {
            id: headingId, className: "claw-module-button", style: { width: "100%", minWidth: 0, padding: "10px 5px" },
            "aria-expanded": expanded, "aria-controls": contentId, onClick: () => onSelect(expanded ? null : module.id),
          }, h("span", { className: "claw-module-icon" }, ui.IconsModule?.[module.icon] && h(ui.IconsModule[module.icon])),
          h("span", { className: "claw-module-copy" }, h("span", { className: "claw-module-title" }, module.title), h("span", { className: "claw-module-subtitle" }, module.subtitle)),
          h("span", { className: "claw-module-chevron", "aria-hidden": true }, ui.IconsModule?.Chevron && h(ui.IconsModule.Chevron, { direction: expanded ? "down" : "right" })))),
          expanded && h(ui.Focusable, { id: contentId, className: "claw-module-content", "flow-children": "column", role: "region", "aria-labelledby": headingId }, contents[module.id]));
      }));
  }
  function SectionLabel({ children }) {
    return h(ui.PanelSectionRow, null, h("div", { className: "claw-section-label" }, children));
  }
  function StatusText({ children }) {
    return h(ui.PanelSectionRow, null, h("p", { className: "claw-module-status", role: "status" }, children));
  }
  return { ModuleStack, SectionLabel, StatusText };
}
