export function createMagpieController(api) {
  const defaults = { fill: true, sharpen: true, strength: .5, capture: "graphics", cropX: 0, cropY: 0, target: 0,
    pipeline: [], maxFrameRate: 60, game3D: false, adjustCursor: true };
  let value = { available: false, enabled: false, active: false, phase: "正在读取画面设置", draft: { ...defaults },
    targets: [], catalog: { effects: [], presets: [] }, advanced: false, custom: false, busy: false, message: "" };
  const listeners = new Set(), blockers = new Map();
  let loaded = false, dirty = false, reading, disposed = false, pauses = Promise.resolve();
  const update = patch => {
    if (disposed) return;
    const next = { ...value, ...patch };
    if (JSON.stringify(value) === JSON.stringify(next)) return;
    value = next; listeners.forEach(fn => fn(value));
  };
  const accept = state => {
    const draft = (!loaded || !dirty) && state.preferences
      ? { ...defaults, ...state.preferences, target: state.target || value.draft.target || 0 } : value.draft;
    loaded = true; update({ ...state, draft });
  };
  async function action(method, ...args) {
    if (value.busy || disposed) return;
    update({ busy: true, message: "" });
    try { const state = await api.call(method, ...args); if (method === "apply_magpie") dirty = false; accept(state); }
    catch (error) { update({ message: String(error) }); }
    finally { update({ busy: false }); }
  }
  return {
    get value() { return value; },
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    accept,
    showAdvanced(advanced) { update({ advanced }); },
    custom(custom) { update({ custom }); },
    edit(patch) { if (!value.busy) { const draft = { ...value.draft, ...patch }; if (JSON.stringify(draft) !== JSON.stringify(value.draft)) { dirty = true; update({ draft }); } } },
    refresh() {
      if (!reading) reading = (async () => {
        accept(await api.call("get_magpie_state"));
        const targets = await api.call("get_magpie_targets");
        const catalog = await api.call("get_magpie_catalog");
        // Suggestions are local drafts. Reading and closing a dropdown never applies.
        // Preselect the game being played: a remembered game, else the focused or only window.
        const live = targets.some(t => t.hwnd === value.draft.target) ? value.draft.target : 0;
        const guess = targets.find(t => t.remembered) ?? targets.find(t => t.foreground) ?? (targets.length === 1 ? targets[0] : undefined);
        const target = live || guess?.hwnd || value.draft.target || 0;
        // A remembered game brings its own saved settings (per-game configuration).
        const own = !dirty && targets.find(t => t.hwnd === target)?.preferences;
        update({ targets, catalog, draft: { ...value.draft, ...(own || {}), target } });
      })().catch(error => update({ message: String(error) })).finally(() => { reading = null; });
      return reading;
    },
    apply() { return action("apply_magpie", { ...value.draft }); },
    stop() { return action("stop_magpie"); },
    // Mapped button: turn upscaling + fill on for the focused game, or off again.
    async toggle() {
      if (disposed) return { ok: false, reason: "插件正在重载" };
      try {
        const state = await api.call("toggle_magpie"); dirty = false; accept(state);
        return state.error ? { ok: false, reason: state.error }
          : { ok: true, message: state.enabled ? "已开启，再按一次关闭" : "已关闭，恢复原画面" };
      } catch (error) { update({ message: String(error) }); return { ok: false, reason: String(error).replace(/^Error:\s*/, "") }; }
    },
    // A launched game the player filled before resumes automatically.
    gameStarted() { if (!disposed) return api.call("magpie_auto_start").then(accept).catch(() => {}); },
    pause(source, visible) {
      if (disposed || blockers.get(source) === visible) return pauses;
      blockers.set(source, visible);
      // Overlay show/hide arrive in quick bursts; concurrent calls could reach the
      // backend out of order and leave a closed menu marked open (no fill). Send
      // them one at a time, in order.
      pauses = pauses.then(async () => {
        if (disposed) return;
        try { accept(await api.call("set_magpie_pause", source, visible)); }
        catch (error) { if (blockers.get(source) === visible) blockers.delete(source); update({ message: String(error) }); }
      });
      return pauses;
    },
    // Backend unload closes its owned child. A late request from an old frontend
    // must not stop the replacement backend after a reload.
    dispose() { disposed = true; listeners.clear(); },
  };
}

// Steam draws its in-game side menus into the game frame, which Magpie's fill
// mode then crops at the screen edges. Lay the overlay UI out inside the part of
// the frame that stays visible, so the menus land exactly on the screen edges
// while the game itself stays filled. Removed whenever fill is not on screen.
export function overlayInsetCss(value) {
  const d = value?.dest;
  if (!value?.enabled || !value.active || !Array.isArray(d) || d.length !== 4) return "";
  const width = d[2] - d[0], height = d[3] - d[1];
  if (!(width > 0 && height > 0)) return "";
  const x = Math.max(0, -d[0]) / width * 100, y = Math.max(0, -d[1]) / height * 100;
  if (x < .1 && y < .1) return "";
  const pct = v => `${v.toFixed(3)}%`;
  return `#popup_target{position:fixed!important;left:${pct(x)}!important;top:${pct(y)}!important;` +
    `width:${pct(100 - 2 * x)}!important;height:${pct(100 - 2 * y)}!important;transform:translateZ(0);overflow:hidden}`;
}

// The quick access panel is laid out for the 720p game frame and enlarged with the
// game, so it ends up covering almost half of a 1920x1200 screen. Give it Steam's
// normal on-screen size: zoom its view (Steam names it "QuickAccess_uid<id>" and
// opened it from this context, so window.open with an empty URL returns it) and
// slide its placeholder right by the width it no longer needs.
function quickAccessZoom(value, win) {
  const d = value?.dest;
  if (!value?.enabled || !value.active || !Array.isArray(d) || d.length !== 4 || !(win?.innerHeight > 0 && win.devicePixelRatio > 0)) return 1;
  const zoom = win.devicePixelRatio * win.innerHeight / (d[3] - d[1]);
  return zoom > 0 && zoom < .98 ? Math.max(.3, zoom) : 1;
}

function quickAccessPlaceholder(win) {
  const W = win.innerWidth, H = win.innerHeight;
  return [...win.document.querySelectorAll("#popup_target *")].filter(e => {
    const r = e.getBoundingClientRect();
    return r.height >= H * .9 && r.right >= W * .9 && r.left > W * .25 && r.left < W * .9;
  }).sort((a, b) => a.getBoundingClientRect().left - b.getBoundingClientRect().left)[0];
}

const sideViews = new Map();
let lastNudge = 0, nudging = false;
// Zoom one of Steam's side menu views ("MainMenu_uid<id>" / "QuickAccess_uid<id>").
// A view is looked up only while its menu is open (window.open with an unknown name
// would create a window); once found, its zoom is kept while fill is on, so the next
// open shows the right size from its first frame. Returns whether the zoom is in effect.
function zoomSideView(env, name, zoom, open) {
  let view = sideViews.get(name);
  // A replaced view leaves the old window object behind; never zoom a stale one.
  try { if (view && (view.closed || view.document?.title !== name)) view = undefined; } catch { view = undefined; }
  if (!view) sideViews.delete(name);
  if (!view && open) {
    view = env.open("", name);
    // Steam titles the view after its name; never keep or touch anything else.
    if (view?.document?.title !== name) { try { if (view && view !== env && !view.document?.title) view.close(); } catch { } view = undefined; }
    if (view) sideViews.set(name, view);
  }
  const root = view ? view.document?.documentElement : null;
  if (!root) return false;
  // The browser normalizes the value it hands back ("0.6000" reads as "0.6"),
  // so compare numbers, not strings.
  const current = () => parseFloat(root.style.zoom) || 1;
  if (Math.abs(current() - zoom) > 1e-3) root.style.zoom = zoom === 1 ? "" : zoom.toFixed(4);
  return zoom !== 1 && Math.abs(current() - zoom) <= 1e-3;
}

// The quick access panel is laid out for the game frame and enlarged with it; a 720p
// game shows it 1.67x too large. Zoom its view back to Steam's normal on-screen size.
// (The left main menu is left as is: zooming its view only shrinks the content into
// the left part of the screen while Steam's own header and footer stay large.)
// The panel also needs a slide: the zoom (on the view) and the slide (on
// the overlay placeholder) only make sense together, since a slide without the zoom
// pushes a full-size panel past the screen edge and cuts its text off. Steam rebuilds
// the view and rewrites the placeholder's style on its own, so every pass re-checks
// both and drops the slide whenever the zoom is not in place.
export function syncQuickAccessSize(env, value) {
  let windows = [];
  try { windows = [...(env.SteamUIStore?.WindowStore?.OverlayWindows ?? [])]; } catch { }
  for (const overlay of windows) {
    try {
      const win = overlay?.BrowserWindow ?? overlay?.m_BrowserWindow;
      const id = win?.SteamClient?.Browser?.GetBrowserID?.();
      if (!id) continue;
      const side = (overlay.MenuStore ?? overlay.m_MenuStore)?.GetOpenSideMenu?.();
      const open = side === 2;
      const zoom = quickAccessZoom(value, win);
      // Clears a zoom an earlier version left on the main menu view.
      zoomSideView(env, `MainMenu_uid${id}`, 1, side === 1);
      const zoomed = zoomSideView(env, `QuickAccess_uid${id}`, zoom, open) && open;
      const candidate = open ? quickAccessPlaceholder(win) : undefined;
      const marked = win.document.querySelector("[data-claw-qam-shift]");
      // While the zoom stays on, the slide also stays on the placeholder after the panel
      // closes, so Steam reads the final position as soon as the panel opens again.
      const keep = zoom === 1 ? undefined : zoomed ? candidate : open ? undefined : marked;
      const stale = new Set(win.document.querySelectorAll("[data-claw-qam-shift]"));
      if (candidate && candidate !== keep && candidate.style.translate) stale.add(candidate);
      for (const el of stale) {
        if (el !== keep) { el.style.translate = ""; el.removeAttribute("data-claw-qam-shift"); }
      }
      const outer = win.document.getElementById("popup_target")?.getBoundingClientRect();
      if (!zoomed || !candidate || !outer) continue;
      const placeholder = candidate, rect = placeholder.getBoundingClientRect(), now = Date.now();
      // Read the slide actually in effect; Steam may have dropped or kept ours.
      const applied = parseFloat(placeholder.style.translate) || 0;
      const shift = Math.round((1 - zoom) * (Math.min(outer.right, win.innerWidth) - (rect.left - applied)));
      if (placeholder.hasAttribute("data-claw-qam-shift") && Math.abs(shift - applied) < 3) {
        // Steam can keep the view at its old bounds when the slide lands during its own
        // open animation (the panel then sits too far left). Check where the view really
        // is and make Steam re-read the bounds, at most once in a while: every re-read
        // restarts Steam's own layout, so repeated nudges would make the panel jump.
        const view = sideViews.get(`QuickAccess_uid${id}`);
        if (nudging || !view || Math.abs(view.screenX - rect.left) <= 3 || now - lastNudge < 800) continue;
      } else {
        placeholder.setAttribute("data-claw-qam-shift", String(shift));
        placeholder.style.translate = `${shift}px 0`;
        if (nudging) continue;  // the nudge in progress picks the new position up
      }
      // Steam re-reads the view bounds on a size change of the placeholder, but not
      // for a change that is undone within a frame or two: hold it briefly.
      nudging = true; lastNudge = now;
      const width = placeholder.style.width;
      placeholder.style.width = `${rect.width - 1}px`;
      win.setTimeout(() => { placeholder.style.width = width; nudging = false; lastNudge = Date.now(); }, 250);
    } catch { }
  }
}

export function syncOverlayInset(env, value) {
  let windows = [];
  try { windows = [...(env.SteamUIStore?.WindowStore?.OverlayWindows ?? [])]; } catch { }
  for (const overlay of windows) {
    try {
      const doc = (overlay?.BrowserWindow ?? overlay?.m_BrowserWindow)?.document;
      if (!doc?.head) continue;
      const css = overlayInsetCss(value);
      let style = doc.getElementById("claw-fill-inset");
      if (!css) { style?.remove(); continue; }
      if (!style) { style = doc.createElement("style"); style.id = "claw-fill-inset"; doc.head.appendChild(style); }
      if (style.textContent !== css) style.textContent = css;
    } catch { }
  }
}

export function createMagpiePanel(React, ui, controller, StatusText) {
  const h = React.createElement;
  return function MagpiePanel() {
    const [state, setState] = React.useState(() => controller.value);
    const advanced = state.advanced, setAdvanced = v => controller.showAdvanced(v);
    React.useEffect(() => { const remove = controller.subscribe(setState); controller.refresh(); return remove; }, []);
    const draft = state.draft;
    const catalog = state.catalog || { effects: [], presets: [] };
    const row = (key, child) => h(ui.PanelSectionRow, { key }, child);
    const choices = [{ label: "请选择运行中的游戏", data: 0 }, ...state.targets.map(t => ({ label: `${t.title} · ${t.width}×${t.height}`, data: t.hwnd }))];
    if (draft.target && !choices.some(c => c.data === draft.target)) choices.push({ label: "此前选择的窗口（请刷新确认）", data: draft.target });
    const slider = (key, label, max, step, suffix = "") => row(key, h(ui.SliderField, {
      label, min: 0, max, step, validValues: "steps", minimumDpadGranularity: step,
      value: key === "strength" ? Math.round(draft.strength * 100) : draft[key], showValue: true, valueSuffix: suffix,
      disabled: state.busy, onChange: v => controller.edit({ [key]: key === "strength" ? Math.round(v) / 100 : Math.round(v) }),
    }));
    const rectSize = r => r?.length === 4 ? `${r[2] - r[0]}×${r[3] - r[1]}` : "";
    const normalize = chain => JSON.stringify(chain.map(e => [e.name, e.scalingType ?? (catalog.effects.find(i => i.name === e.name)?.canScale ? 1 : 0),
      e.scale || [1, 1], Object.entries(e.parameters || {}).sort(([a], [b]) => a.localeCompare(b))]));
    const preset = !draft.pipeline.length ? "quick" : state.custom ? "custom" : catalog.presets.find(p => normalize(p.effects) === normalize(draft.pipeline))?.name || "custom";
    const setChain = pipeline => { controller.edit({ pipeline }); controller.custom(true); };
    const stageEdit = (index, patch) => setChain(draft.pipeline.map((stage, i) => i === index ? { ...stage, ...patch } : stage));
    const effectChoices = catalog.effects.slice().sort((a, b) => a.name.localeCompare(b.name)).map(e => ({ label: e.name.replaceAll("\\", " / "), data: e.name }));
    const labels = { sharpness: "锐化强度", paramB: "B 参数", paramC: "C 参数", strength: "强度", radius: "半径",
      curvature: "曲率", CRTGamma: "CRT 伽马", monitorGamma: "屏幕伽马", interlace: "交错扫描", cornerSize: "圆角大小" };
    const chainRows = draft.pipeline.flatMap((stage, index) => {
      const info = catalog.effects.find(e => e.name === stage.name);
      const rows = [row(`effect-${index}`, h(ui.DropdownItem, { label: `效果 ${index + 1}`, rgOptions: effectChoices, selectedOption: stage.name,
        disabled: state.busy, onChange: o => stageEdit(index, { name: o.data, parameters: {},
          scalingType: catalog.effects.find(e => e.name === o.data)?.canScale ? 1 : 0, scale: [1, 1] }) }))];
      for (const p of info?.parameters || []) rows.push(row(`param-${index}-${p.name}`, h(ui.SliderField, {
        label: labels[p.name] || p.label || p.name, min: p.min, max: p.max, step: p.step || (p.integer ? 1 : .01),
        minimumDpadGranularity: p.step || (p.integer ? 1 : .01), validValues: "steps", showValue: true,
        value: stage.parameters?.[p.name] ?? p.default, disabled: state.busy,
        onChange: v => stageEdit(index, { parameters: { ...stage.parameters, [p.name]: Math.max(p.min, Math.min(p.max, p.integer ? Math.round(v) : Number(v.toFixed(6)))) } }),
      })));
      rows.push(row(`effect-actions-${index}`, h("div", { className: "claw-action-grid" },
        h(ui.ButtonItem, { layout: "below", disabled: state.busy || index === 0, onClick: () => {
          const next = draft.pipeline.slice(); [next[index - 1], next[index]] = [next[index], next[index - 1]]; setChain(next);
        } }, "上移"), h(ui.ButtonItem, { layout: "below", disabled: state.busy, onClick: () => setChain(draft.pipeline.filter((_, i) => i !== index)) }, "移除"))));
      return rows;
    });
    return h(ui.PanelSection, null,
      h(StatusText, null, state.error || state.phase),
      row("target", h(ui.DropdownItem, { label: "游戏窗口", rgOptions: choices, selectedOption: draft.target, disabled: state.busy,
        onChange: o => controller.edit({ ...(state.targets.find(t => t.hwnd === o.data)?.preferences || {}), target: o.data }) })),
      row("refresh", h(ui.ButtonItem, { layout: "below", disabled: state.busy, onClick: () => controller.refresh() }, "刷新游戏窗口")),
      row("fill", h(ui.ToggleField, { label: "等比铺满屏幕", description: "保持比例，裁掉超出屏幕的边缘。关闭时完整显示画面。", checked: draft.fill,
        disabled: state.busy, onChange: fill => controller.edit({ fill }) })),
      row("preset", h(ui.DropdownItem, { label: "效果方案", selectedOption: preset, disabled: state.busy,
        rgOptions: [{ label: "Bicubic + 可选 RCAS 锐化", data: "quick" }, ...catalog.presets.map(p => ({ label: p.name, data: p.name })), { label: "自定义效果链", data: "custom" }],
        onChange: o => {
          if (o.data === "quick") { controller.edit({ pipeline: [], sharpen: true }); controller.custom(false); }
          else if (o.data === "custom") { if (!draft.pipeline.length) controller.edit({ pipeline: [{ name: "Lanczos", parameters: {}, scalingType: 1, scale: [1, 1] }], sharpen: false }); controller.custom(true); setAdvanced(true); }
          else { controller.edit({ pipeline: JSON.parse(JSON.stringify(catalog.presets.find(p => p.name === o.data).effects)), sharpen: false }); controller.custom(false); }
        } })),
      row("sharpen", h(ui.ToggleField, { label: "追加 RCAS 锐化", description: "在所选效果链末尾增加 FSR RCAS。FSR 方案自身已含锐化。", checked: draft.sharpen,
        disabled: state.busy, onChange: sharpen => controller.edit({ sharpen }) })),
      draft.sharpen && slider("strength", "锐化强度", 100, 5, "%"),
      row("apply", h("div", { className: "claw-primary-action" }, h(ui.ButtonItem, { layout: "below", disabled: state.busy || !state.available || !draft.target || (!draft.fill && !draft.sharpen && !draft.pipeline.length),
        onClick: () => controller.apply() }, state.enabled ? "应用画面设置" : "应用到游戏"))),
      row("stop", h(ui.ButtonItem, { layout: "below", disabled: state.busy || !state.enabled, onClick: () => controller.stop() }, "恢复原游戏画面")),
      state.remembered && row("remembered", h("div", { className: "claw-hint" }, "已记住此游戏和它自己的画面设置：下次启动自动按这套设置应用；点“恢复原游戏画面”即取消。")),
      state.active && row("actual", h("div", { className: "claw-hint" }, `实际画面：${rectSize(state.source)} → ${rectSize(state.dest)}`)),
      row("advanced", h(ui.ButtonItem, { layout: "below", onClick: () => setAdvanced(!advanced) }, advanced ? "收起效果与捕获设置" : "效果与捕获设置")),
      advanced && h(React.Fragment, null,
      ...chainRows,
      row("add-effect", h(ui.ButtonItem, { layout: "below", disabled: state.busy || draft.pipeline.length >= 12,
        onClick: () => {
          if (draft.pipeline.length) setChain([...draft.pipeline, { name: "FSR\\FSR_RCAS", parameters: {}, scalingType: 0, scale: [1, 1] }]);
          else { controller.edit({ pipeline: [{ name: "Lanczos", parameters: {}, scalingType: 1, scale: [1, 1] }], sharpen: false }); controller.custom(true); }
        } },
        draft.pipeline.length ? "添加效果" : "创建自定义效果链")),
      row("capture", h(ui.DropdownItem, { label: "捕获方式", rgOptions: [{ label: "Graphics Capture（推荐）", data: "graphics" },
        { label: "Desktop Duplication", data: "desktop" }, { label: "GDI 兼容捕获", data: "gdi" }, { label: "DWM 共享表面", data: "dwm" }],
        selectedOption: draft.capture, disabled: state.busy, onChange: o => controller.edit({ capture: o.data }) })),
      row("fps", h(ui.DropdownItem, { label: "处理帧率上限", rgOptions: [0, 30, 60, 90, 120, 144, 165, 240].map(v => ({ label: v ? `${v} FPS` : "不限制", data: v })),
        selectedOption: draft.maxFrameRate, disabled: state.busy, onChange: o => controller.edit({ maxFrameRate: o.data }) })),
      row("3d", h(ui.ToggleField, { label: "3D 游戏模式", description: "适用于鼠标控制视角的 3D 游戏。", checked: draft.game3D,
        disabled: state.busy, onChange: game3D => controller.edit({ game3D }) })),
      row("cursor", h(ui.ToggleField, { label: "随缩放调整鼠标速度", checked: draft.adjustCursor,
        disabled: state.busy, onChange: adjustCursor => controller.edit({ adjustCursor }) })),
      slider("cropX", "内置黑边：左右每边裁剪", 400, 5, " 像素"),
      slider("cropY", "内置黑边：上下每边裁剪", 400, 5, " 像素")),
      row("hint", h("div", { className: "claw-hint" }, "应用一次即记住该游戏，以后启动自动生效。游戏若是自带全屏，返回游戏时会自动按 Alt+Enter 切成窗口再铺满。修改选项后点击应用才生效。菜单打开时自动暂停。效果链会按顺序执行并最终等比适配屏幕；神经网络效果处理开销较高。支持 SDR；触控和原版自动配置功能尚未接入。")),
      state.message && h(StatusText, null, state.message));
  };
}
