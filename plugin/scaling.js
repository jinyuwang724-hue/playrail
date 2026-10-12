export const scalingModes = [
  { label: "自动", data: "auto", description: "保持比例，最多放大到 2 倍。" },
  { label: "整数", data: "integer", description: "按整数倍放大，保留完整画面。" },
  { label: "适应", data: "fit", description: "保持比例，完整显示；可能留黑边。" },
  { label: "拉伸", data: "stretch", description: "铺满屏幕，画面比例可能改变。" },
  { label: "填充", data: "fill", description: "保持比例铺满，裁剪超出屏幕的部分。" },
];
export const scalingFilters = [
  { label: "线性", data: "linear" }, { label: "最近", data: "nearest" },
  { label: "FSR", data: "fsr" }, { label: "NIS", data: "nis" },
];

export function createScalingController(api) {
  let value = { enabled: false, mode: "auto", filter: "linear", sharpness: .5, target: 0,
    phase: "正在读取缩放设置", available: false, targets: [], suggestedTarget: 0, busy: false, message: "" };
  const listeners = new Set(), blockers = new Map();
  let reading, disposed = false, changing = false;
  function update(patch) {
    if (disposed) return;
    const next = { ...value, ...patch };
    if (JSON.stringify(next) === JSON.stringify(value)) return;
    value = next; listeners.forEach(fn => fn(value));
  }
  const settings = patch => Object.fromEntries(["enabled", "mode", "filter", "sharpness", "target"].map(k => [k, patch[k] ?? value[k]]));
  return {
    get value() { return value; },
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    accept(state) { update(state); },
    refresh() {
      if (!reading) reading = (async () => {
        update(await api.call("get_scaling_state"));
        if (value.available) {
          const targets = await api.call("get_scaling_targets");
          update({ targets, suggestedTarget: targets.length === 1 ? targets[0].hwnd : 0 });
        }
      })().catch(error => update({ message: String(error) })).finally(() => { reading = null; });
      return reading;
    },
    async set(patch) {
      if (changing || disposed) return;
      const next = settings(patch);
      if (JSON.stringify(next) === JSON.stringify(settings({}))) return;
      changing = true; update({ busy: true, message: "" });
      try { update(await api.call("set_scaling_settings", next)); }
      catch (error) {
        try { update(await api.call("get_scaling_state")); } catch { }
        update({ message: String(error) });
      } finally { changing = false; update({ busy: false }); }
    },
    async pause(source, visible) {
      if (disposed || blockers.get(source) === visible) return;
      blockers.set(source, visible);
      try { await api.call("set_scaling_pause", source, visible); }
      catch (error) { blockers.delete(source); update({ message: `缩放菜单交接失败：${String(error)}` }); }
    },
    dispose() {
      disposed = true; listeners.clear();
      // A frontend unload must not leave a topmost scaler without its controls.
      if (value.enabled) api.call("set_scaling_settings", settings({ enabled: false })).catch(() => {});
    },
  };
}

export function createScalingPanel(React, ui, controller, StatusText) {
  const h = React.createElement;
  return function ScalingPanel() {
    const [state, setState] = React.useState(() => controller.value);
    React.useEffect(() => {
      const unsubscribe = controller.subscribe(setState);
      controller.refresh();
      return unsubscribe;
    }, []);
    const row = (key, child) => h(ui.PanelSectionRow, { key }, child);
    const choose = (label, key, choices) => {
      const index = choices.findIndex(choice => choice.data === state[key]);
      return row(key, ui.SliderField ? h(ui.SliderField, {
        label, description: choices[index]?.description, value: Math.max(0, index), min: 0, max: choices.length - 1,
        step: 1, validValues: "steps", minimumDpadGranularity: 1, notchCount: choices.length,
        notchTicksVisible: true, notchLabels: choices.map((choice, i) => ({ notchIndex: i, label: choice.label })),
        showValue: false, disabled: state.busy,
        onChange: value => controller.set({ [key]: choices[Math.round(value)]?.data ?? state[key] }),
      }) : h(ui.DropdownItem, { label, rgOptions: choices, selectedOption: state[key], disabled: state.busy, onChange: option => controller.set({ [key]: option.data }) }));
    };
    const selectedTarget = state.target || state.suggestedTarget;
    const targetOptions = state.targets.map(target => ({ label: `${target.title} · ${target.width}×${target.height}`, data: target.hwnd }));
    if (state.target && !targetOptions.some(option => option.data === state.target)) targetOptions.unshift({ label: "当前窗口（请刷新列表确认）", data: state.target });
    if (!targetOptions.length || !selectedTarget) targetOptions.unshift({ label: "请选择正在运行的游戏", data: 0 });
    return h(React.Fragment, null,
      h(StatusText, null, state.error || (!state.enabled ? "设置已保存，尚未应用到游戏" : state.phase)),
      row("enabled", h(ui.ToggleField, { label: "开启画面缩放", checked: state.enabled,
        description: !selectedTarget ? "选择游戏窗口后开启；只调整下面的选项不会启动处理。" : "开启后关闭菜单并返回游戏，所选模式和过滤器才会应用。", disabled: state.busy || !state.available || !selectedTarget,
        onChange: enabled => controller.set({ enabled, target: selectedTarget }) })),
      row("target", h(ui.DropdownItem, { label: "游戏窗口", rgOptions: targetOptions, selectedOption: selectedTarget,
        disabled: state.busy, onChange: option => controller.set({ target: option.data, ...(option.data === 0 ? { enabled: false } : {}) }) })),
      row("refresh", h(ui.ButtonItem, { layout: "below", disabled: state.busy, onClick: () => controller.refresh() }, "刷新游戏窗口")),
      choose("缩放模式", "mode", scalingModes), choose("缩放过滤器", "filter", scalingFilters),
      ["fsr", "nis"].includes(state.filter) && row("sharpness", h(ui.SliderField, {
        label: `${state.filter.toUpperCase()} 锐化强度`, value: Math.round(state.sharpness * 100), min: 0, max: 100, step: 5,
        showValue: true, valueSuffix: "%", validValues: "steps", minimumDpadGranularity: 5,
        disabled: state.busy, onChange: value => controller.set({ sharpness: Math.round(value) / 100 }),
      })),
      state.inputWidth > 0 && row("dimensions", h("div", { className: "claw-hint" },
        `${state.inputWidth}×${state.inputHeight} → ${state.outputWidth}×${state.outputHeight} · 实际过滤器：${scalingFilters.find(filter => filter.data === state.effectiveFilter)?.label ?? "等待画面"}`)),
      state.note && h(StatusText, null, state.note),
      row("hint", h("div", { className: "claw-hint" }, "缩放作用于捕获到的窗口尺寸。窗口与屏幕同尺寸时，模式可能无可见差别，但 FSR / NIS 仍执行锐化。要比较缩放，请将游戏设为较低分辨率的窗口模式。当前支持 SDR。")),
      state.message && h(StatusText, null, state.message));
  };
}
