// Power limit and CPU frequency cap. The backend owns the hardware; this keeps a
// draft so sliders move freely and only the last value of a drag is written.
export function createPowerController(api) {
  let value = { view: null, draft: null, busy: false, message: "" };
  const listeners = new Set();
  let timer, pending = null, saving = false;
  const set = patch => { value = { ...value, ...patch }; listeners.forEach(fn => fn(value)); };
  const accept = view => { if (view) set({ view, draft: pending || saving ? value.draft ?? view.settings : view.settings }); };
  async function flush() {
    timer = undefined;
    if (saving) { timer = setTimeout(flush, 200); return; }
    const settings = pending; pending = null;
    if (!settings) return;
    saving = true; set({ busy: true });
    try { accept(await api.call("save_power", settings)); set({ message: "" }); }
    catch (error) { set({ message: String(error) }); }
    finally { saving = false; set({ busy: false }); }
  }
  return {
    get value() { return value; },
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    accept,
    async refresh(sample = true) {
      try { accept(await api.call("get_power_state", sample)); }
      catch (error) { set({ message: String(error) }); }
    },
    edit(patch, delay = 400) {
      if (!value.draft) return;
      const draft = { ...value.draft, ...patch };
      pending = draft; set({ draft });
      clearTimeout(timer); timer = setTimeout(flush, delay);
    },
    async restore() {
      clearTimeout(timer); pending = null; set({ busy: true });
      try { accept(await api.call("restore_power")); set({ message: "已恢复原始功耗和频率设置，工具箱不再接管" }); }
      catch (error) { set({ message: String(error) }); }
      finally { set({ busy: false }); }
    },
    async setEcore(enabled) {
      set({ busy: true });
      try { accept(await api.call("set_ecore", enabled)); set({ message: "" }); }
      catch (error) { set({ message: String(error) }); }
      finally { set({ busy: false }); }
    },
    dispose() { clearTimeout(timer); listeners.clear(); },
  };
}

const freqLabel = mhz => mhz ? `${+(mhz / 1000).toFixed(2)} GHz` : "不限";
const intelProfile = id => ({ performance: "性能", balanced: "均衡", battery: "续航" })[id] ?? "性能";
const watts = v => typeof v === "number" ? `${Math.round(v * 10) / 10} W` : "—";
const fanModes = { firmware: "固件自动", quiet: "静音", balanced: "均衡", performance: "性能" };

// One-line summaries for the collapsed module headings (power, osd, fan).
export function powerSummaries(value) {
  const view = value?.view, draft = value?.draft;
  if (!view || !draft) return {};
  const mode = draft.mode === "manual" ? `手动 ${draft.manual.tdp} W` : draft.mode === "smart" ? `Intel · ${intelProfile(draft.smart?.profile)}`
    : draft.mode === "autotdp" ? `Auto · ${draft.autotdp.target} 帧`
    : `预设 ${(view.charger ? draft.ac : draft.dc).tdp} W`;
  return {
    power: draft.enabled ? { text: mode } : { text: "未接管", off: true },
    osd: draft.osd?.enabled ? { text: `第 ${draft.osd.level} 档` } : { text: "关", off: true },
    fan: { text: `${fanModes[draft.fan?.mode] ?? "—"}${draft.fan?.full && draft.fan.mode !== "firmware" ? " · 全速" : ""}`, off: draft.fan?.mode === "firmware" },
  };
}

// Home quick tiles. Each press steps to the next level; edits wait 800 ms, so a
// run of presses writes only the level it stops on. Power is not taken over from
// a tile: while it is off, its tile opens the power page instead.
const fanCycle = ["firmware", "quiet", "balanced", "performance"];
export function powerTiles(value, controller, open) {
  const view = value?.view, draft = value?.draft;
  if (!view || !draft) return [];
  const quick = draft.quick ?? [];
  const powerIndex = draft.mode === "autotdp" ? 0 : draft.mode === "smart" ? 1 : draft.mode === "manual" ? quick.indexOf(draft.manual.tdp) + 2 : -1;
  const powerValue = draft.mode === "smart" ? `Intel · ${intelProfile(draft.smart?.profile)}`
    : draft.mode === "autotdp" ? `Auto · ${draft.autotdp.target} 帧`
    : draft.mode === "manual" ? `${draft.manual.tdp} W` : `预设 ${(view.charger ? draft.ac : draft.dc).tdp} W`;
  const level = draft.osd.enabled ? draft.osd.level : 0;
  const fanIndex = fanCycle.indexOf(draft.fan.mode);
  return [
    { id: "power", label: "功耗", icon: "power", on: draft.enabled, steps: quick.length + 2, filled: draft.enabled ? powerIndex + 1 : 0,
      value: draft.enabled ? powerValue : "未接管",
      onPress: () => {
        if (!draft.enabled) return open("power");
        const next = (powerIndex + 1) % (quick.length + 2);
        controller.edit(next < 2 ? { mode: next === 0 ? "autotdp" : "smart" } : { mode: "manual", manual: { ...draft.manual, tdp: quick[next - 2] } }, 800);
      } },
    { id: "osd", label: "性能显示", icon: "osd", on: level > 0, steps: 4, filled: level, value: level ? `第 ${level} 档` : "关",
      onPress: () => {
        const next = (level + 1) % 5;
        controller.edit({ osd: { ...draft.osd, enabled: next > 0, level: next || draft.osd.level } }, 800);
      } },
    { id: "fan", label: "风扇", icon: "fan", on: draft.fan.mode !== "firmware", steps: 3, filled: Math.max(0, fanIndex),
      value: `${fanModes[draft.fan.mode] ?? "—"}${draft.fan.full && draft.fan.mode !== "firmware" ? " · 全速" : ""}`,
      onPress: () => controller.edit({ fan: { ...draft.fan, mode: fanCycle[(fanIndex + 1) % fanCycle.length] } }, 800) },
  ];
}

// The power controller backs three toolbox modules: `section` picks power, osd or fan.
export function createPowerPanel(React, ui, controller, StatusText, SectionLabel) {
  const h = React.createElement;
  return function PowerPanel({ section = "power" }) {
    const [state, setState] = React.useState(() => controller.value);
    React.useEffect(() => {
      const remove = controller.subscribe(setState);
      controller.refresh();
      // Live readback of the limits and actual package power while the panel is open.
      const poll = setInterval(() => controller.refresh(), 3000);
      return () => { clearInterval(poll); remove(); };
    }, []);
    const row = (key, child) => h(ui.PanelSectionRow, { key }, child);
    const view = state.view, draft = state.draft;
    if (!view || !draft) return h(ui.PanelSection, null, h(StatusText, null, state.message || "正在读取功耗状态…"));
    const limits = view.limits, actual = view.actual;
    // "No cap" runs up to the CPU's own maximum turbo, so show that instead of a vague "unlimited".
    const freqOptions = limits.freq.map(v => ({ label: v || !limits.cpuMax ? freqLabel(v) : `最高 ${freqLabel(limits.cpuMax)}`, data: v }));
    const slider = (key, label, profile) => row(key, h(ui.SliderField, {
      label, min: limits.tdpMin, max: limits.tdpMax, step: 1, validValues: "steps", minimumDpadGranularity: 1,
      notchTicksVisible: false, showValue: true, valueSuffix: " W", value: draft[profile].tdp,
      onChange: v => controller.edit({ [profile]: { ...draft[profile], tdp: Math.round(v) } }),
    }));
    const freq = (key, label, profile) => row(key, h(ui.DropdownItem, {
      label, rgOptions: freqOptions, selectedOption: draft[profile].freq,
      onChange: o => controller.edit({ [profile]: { ...draft[profile], freq: o.data } }, 0),
    }));
    const source = view.charger ? "插电" : "电池";
    const live = draft.mode === "autotdp" ? view.autotdp : view.smart;
    // One status line: what drives the limit now, and the limit and draw actually in effect.
    const driver = live?.game ? [live.game, live.fps != null ? `${Math.round(live.fps)} 帧` : ""].filter(Boolean).join(" ") : source;
    const phase = view.error ? view.error : !draft.enabled ? `${source} · 未接管` : actual
      ? `${driver} · 上限 ${watts(actual.pl1)}${typeof actual.package === "number" ? ` · 实际 ${watts(actual.package)}` : ""}`
      : driver;
    const manual = draft.mode === "manual", smart = draft.mode === "smart", auto = draft.mode === "autotdp";
    const autoSetting = (key, label, min, max, unit) => row(`auto-${key}`, h(ui.SliderField, {
      label, min, max, step: 1, validValues: "steps", minimumDpadGranularity: 1,
      showValue: true, valueSuffix: unit, value: draft.autotdp[key],
      onChange: v => controller.edit({ autotdp: { ...draft.autotdp, [key]: Math.round(v) } }),
    }));
    if (section === "osd") return h(ui.PanelSection, null,
      row("osd", h(ui.ToggleField, { label: "显示性能信息",
        checked: draft.osd.enabled, onChange: enabled => controller.edit({ osd: { ...draft.osd, enabled } }, 0) })),
      draft.osd.enabled && row("osd-level", h(ui.SliderField, { label: "显示详细程度", min: 1, max: 4, step: 1, validValues: "steps",
        minimumDpadGranularity: 1, notchCount: 4, notchTicksVisible: true, showValue: true, value: draft.osd.level,
        onChange: v => controller.edit({ osd: { ...draft.osd, level: Math.round(v) } }) })),
      draft.osd.enabled && (view.osd?.error || view.osd?.rtss === false) && row("osd-state", h("div", { className: "claw-hint" },
        view.osd?.error || "RTSS 没有运行，OSD 暂不显示。")),
      state.message && row("message", h("div", { className: "claw-hint" }, state.message)));
    const fanMode = draft.fan.mode;
    const fanDefault = view.fan?.presets?.[fanMode];
    const fanCurve = fanMode !== "firmware" ? draft.fan.curves?.[fanMode] ?? fanDefault : null;
    const editCurve = curve => controller.edit({ fan: { ...draft.fan, curves: { ...view.fan?.presets, ...draft.fan.curves, [fanMode]: curve } } });
    if (section === "fan") return h(ui.PanelSection, null,
      row("fan-state", h("div", { className: "claw-hint" }, view.fan?.error ? view.fan.error
        : view.fan?.rpm1 ? `${view.fan.rpm1} 转 · 占空比 ${view.fan.duty1}%${typeof view.live?.cpuTemp === "number" ? ` · CPU ${view.live.cpuTemp} °C` : ""} · ${view.fan.software ? "工具箱曲线" : "固件控制"}${view.fan.fullSpeed ? " · 全速" : ""}`
        : draft.fan.mode === "firmware" ? "固件自动控制" : "正在读取风扇状态…")),
      row("fan-mode", h(ui.DropdownItem, { label: "风扇模式", selectedOption: draft.fan.mode,
        rgOptions: [{ label: "固件自动", data: "firmware" }, { label: "静音", data: "quiet" }, { label: "均衡", data: "balanced" },
          { label: "性能", data: "performance" }],
        onChange: o => controller.edit({ fan: { ...draft.fan, mode: o.data } }, 0) })),
      fanCurve && h(React.Fragment, null, ...fanCurve.map((duty, i) => row(`fan-${i}`, h(ui.SliderField, {
        label: `${view.fan?.axis?.[i] ?? [50, 60, 70, 75, 80, 90][i]} °C`, min: 0, max: view.fan?.caps?.[fanMode] ?? 76, step: 1, validValues: "steps",
        minimumDpadGranularity: 1, showValue: true, valueSuffix: "%", value: duty,
        onChange: v => editCurve(fanCurve.map((d, j) => j === i ? Math.round(v) : d)),
      })))),
      fanCurve && fanDefault && fanCurve.some((d, i) => d !== fanDefault[i]) && row("fan-reset", h(ui.ButtonItem, { layout: "below",
        onClick: () => editCurve([...fanDefault]) }, "恢复默认曲线")),
      draft.fan.mode !== "firmware" && row("fan-full", h(ui.ToggleField, { label: "风扇全速",
        checked: draft.fan.full, onChange: full => controller.edit({ fan: { ...draft.fan, full } }, 0) })),
      state.message && row("message", h("div", { className: "claw-hint" }, state.message)));
    return h(ui.PanelSection, null,
      h(StatusText, null, phase),
      draft.enabled && view.otherTool && row("other", h("div", { className: "claw-hint" }, "体感助手也在设置功耗，两边会互相改写。")),
      row("enabled", h(ui.ToggleField, { label: "接管功耗控制",
        checked: draft.enabled, onChange: enabled => controller.edit({ enabled }, 0) })),
      draft.enabled && row("mode", h(ui.DropdownItem, { label: "调节方式", selectedOption: draft.mode,
        rgOptions: [{ label: "Auto · 目标帧率", data: "autotdp" }, { label: "Intel 智能", data: "smart" }, { label: "按电源", data: "auto" }, { label: "手动", data: "manual" }],
        onChange: o => controller.edit({ mode: o.data }, 0) })),
      draft.enabled && (smart ? h(React.Fragment, null,
        row("intel-profile", h(ui.DropdownItem, { label: "Intel 档位",
          rgOptions: (view.endurance?.profiles ?? []).map(p => ({ label: intelProfile(p.id), data: p.id })),
          selectedOption: draft.smart.profile, disabled: !view.endurance?.available,
          onChange: o => controller.edit({ smart: { profile: o.data } }, 0) })),
        row("intel-state", h("div", { className: "claw-hint" }, view.endurance?.error || view.error || live?.reason || "Intel Endurance Gaming 自动调节")),
        row("intel-note", h("div", { className: "claw-hint" }, "电池供电时自动调节，插电时待机。调整档位后请重新启动游戏。")))
      : auto ? h(React.Fragment, null,
        autoSetting("target", "目标帧率", 20, 240, " 帧"),
        autoSetting("floor", "功耗下限", limits.tdpMin, Math.min(draft.autotdp.acMax, draft.autotdp.dcMax), " W"),
        autoSetting("acMax", "插电上限", draft.autotdp.floor, limits.tdpMax, " W"),
        autoSetting("dcMax", "电池上限", draft.autotdp.floor, limits.tdpMax, " W"),
        row("auto-state", h("div", { className: "claw-hint" }, live?.reason || "等待游戏帧率")),
        row("auto-note", h("div", { className: "claw-hint" }, "按真实渲染帧率调节功耗；稳定后缓慢降低，采集异常时保持。CPU 不限频。")))
      : manual ? h(React.Fragment, null,
        slider("manual-tdp", "功耗", "manual"),
        freq("manual-freq", "CPU 频率", "manual"))
      : h(React.Fragment, null,
        slider("ac-tdp", "插电功耗", "ac"),
        freq("ac-freq", "插电 CPU 频率", "ac"),
        slider("dc-tdp", "电池功耗", "dc"),
        freq("dc-freq", "电池 CPU 频率", "dc"))),
      draft.enabled && !smart && row("boost", h(ui.SliderField, { label: "短时功耗加成",
        min: 0, max: limits.boostMax, step: 1, validValues: "steps", minimumDpadGranularity: 1, showValue: true, valueSuffix: " W",
        value: draft.boost, onChange: v => controller.edit({ boost: Math.round(v) }) })),
      view.ecore && row("ecore", h(ui.ToggleField, { label: "全部核心按 E 核调度",
        description: view.ecore.pending ? "重启后生效" : undefined,
        checked: view.ecore.enabled, disabled: state.busy, onChange: enabled => controller.setEcore(enabled) })),
      view.backup && row("restore", h(ui.ButtonItem, { layout: "below", disabled: state.busy, onClick: () => controller.restore() },
        "恢复原始功耗")),
      state.message && row("message", h("div", { className: "claw-hint" }, state.message)));
  };
}
