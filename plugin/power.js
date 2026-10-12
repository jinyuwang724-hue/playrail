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

const freqLabel = mhz => mhz ? `${mhz} MHz` : "不限制";
const watts = v => typeof v === "number" ? `${Math.round(v * 10) / 10} W` : "—";
const fanModes = { firmware: "固件自动", quiet: "静音", balanced: "均衡", performance: "性能" };

// One-line summaries for the collapsed module headings (power, osd, fan).
export function powerSummaries(value) {
  const view = value?.view, draft = value?.draft;
  if (!view || !draft) return {};
  const mode = draft.mode === "manual" ? `手动 ${draft.manual.tdp} W` : draft.mode === "smart" ? `智能${view.smart?.limit ? ` ${view.smart.limit} W` : ""}`
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
  const powerIndex = draft.mode === "smart" ? 0 : draft.mode === "manual" ? quick.indexOf(draft.manual.tdp) + 1 : -1;
  const powerValue = draft.mode === "smart" ? `智能${view.smart?.limit ? ` ${view.smart.limit} W` : ""}`
    : draft.mode === "manual" ? `${draft.manual.tdp} W` : `预设 ${(view.charger ? draft.ac : draft.dc).tdp} W`;
  const level = draft.osd.enabled ? draft.osd.level : 0;
  const fanIndex = fanCycle.indexOf(draft.fan.mode);
  return [
    { id: "power", label: "功耗", icon: "power", on: draft.enabled, steps: quick.length + 1, filled: draft.enabled ? powerIndex + 1 : 0,
      value: draft.enabled ? powerValue : "未接管",
      onPress: () => {
        if (!draft.enabled) return open("power");
        const next = (powerIndex + 1) % (quick.length + 1);
        controller.edit(next === 0 ? { mode: "smart" } : { mode: "manual", manual: { ...draft.manual, tdp: quick[next - 1] } }, 800);
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
    const freqOptions = limits.freq.map(v => ({ label: freqLabel(v), data: v }));
    const slider = (key, label, profile, description) => row(key, h(ui.SliderField, {
      label, description, min: limits.tdpMin, max: limits.tdpMax, step: 1, validValues: "steps", minimumDpadGranularity: 1,
      notchTicksVisible: false, showValue: true, valueSuffix: " W", value: draft[profile].tdp,
      onChange: v => controller.edit({ [profile]: { ...draft[profile], tdp: Math.round(v) } }),
    }));
    const freq = (key, label, profile) => row(key, h(ui.DropdownItem, {
      label, rgOptions: freqOptions, selectedOption: draft[profile].freq,
      onChange: o => controller.edit({ [profile]: { ...draft[profile], freq: o.data } }, 0),
    }));
    const source = view.charger ? "插电" : "电池";
    const using = !draft.enabled ? "未接管" : draft.mode === "manual" ? "手动" : draft.mode === "smart" ? "智能" : `${source}预设`;
    const phase = view.error ? view.error : !draft.enabled ? `${source} · 未接管（功耗由系统或其他软件决定）` : actual
      ? `${source} · ${using} · 上限 ${watts(actual.pl1)}／短时 ${watts(actual.pl2)}${typeof actual.package === "number" ? ` · 实际 ${watts(actual.package)}` : ""}`
      : `${source} · ${using}`;
    const manual = draft.mode === "manual", smart = draft.mode === "smart";
    const live = view.smart;
    const smartSlider = (key, label, field, min, description) => row(key, h(ui.SliderField, {
      label, description, min, max: limits.tdpMax, step: 1, validValues: "steps", minimumDpadGranularity: 1,
      notchTicksVisible: false, showValue: true, valueSuffix: " W", value: draft.smart[field],
      onChange: v => controller.edit({ smart: { ...draft.smart, [field]: Math.round(v) } }),
    }));
    const fpsText = live?.fps != null ? `真实 ${Math.round(live.fps)} 帧${live.presentedFps && live.presentedFps > live.fps * 1.3 ? `（含插帧显示 ${Math.round(live.presentedFps)} 帧）` : ""}` : "";
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
      draft.enabled && view.otherTool && row("other", h("div", { className: "claw-hint" },
        `体感助手仍在运行，它也会设置功耗。请在体感助手里关闭“自动设置 TDP”或退出体感助手，否则两边会来回改写。${view.reapplied ? `已发现 ${view.reapplied} 次被改回并重新写入。` : ""}`)),
      row("enabled", h(ui.ToggleField, { label: "接管功耗控制", description: "开启后按下面的设置写入功耗上限和 CPU 最高频率，并随插拔电源自动切换。",
        checked: draft.enabled, onChange: enabled => controller.edit({ enabled }, 0) })),
      row("mode", h(ui.DropdownItem, { label: "调节方式", selectedOption: draft.mode,
        rgOptions: [{ label: "智能：按帧率自动调", data: "smart" }, { label: "预设：插电／电池各一档", data: "auto" }, { label: "手动：固定一个值", data: "manual" }],
        onChange: o => controller.edit({ mode: o.data }, 0) })),
      smart ? h(React.Fragment, null,
        draft.enabled && live && row("smart-live", h("div", { className: "claw-hint" },
          [live.game ? `游戏 ${live.game}` : "未检测到游戏", fpsText, `当前上限 ${live.limit} W`, live.reason].filter(Boolean).join(" · "))),
        row("smart-target", h(ui.DropdownItem, { label: "目标帧率", description: "按游戏真实渲染的帧数计算，插帧（XeSS、小黄鸭）出来的帧不算。",
          rgOptions: (view.targets ?? [0, 30, 40, 45, 60, 90, 120]).map(v => ({ label: v ? `${v} 帧` : "不限（够用就不再加）", data: v })),
          selectedOption: draft.smart.target, onChange: o => controller.edit({ smart: { ...draft.smart, target: o.data } }, 0) })),
        smartSlider("smart-ac", "插电上限", "acMax", draft.smart.floor),
        smartSlider("smart-dc", "电池上限", "dcMax", draft.smart.floor),
        smartSlider("smart-floor", "最低功耗", "floor", limits.tdpMin, "画面静止或在菜单时降到这里。"),
        freq("smart-ac-freq", "插电 CPU 最高频率", "ac"),
        freq("smart-dc-freq", "电池 CPU 最高频率", "dc"),
        row("smart-hint", h("div", { className: "claw-hint" }, "帧率高于目标就降功耗，低于目标且功耗用满就升；多给功耗帧数不涨时停止加。")))
      : manual ? h(React.Fragment, null,
        slider("manual-tdp", "手动功耗", "manual", "手动时插电和电池预设都不生效。"),
        freq("manual-freq", "CPU 最高频率", "manual"))
      : h(React.Fragment, null,
        h(SectionLabel, null, view.charger ? "插电预设（当前使用）" : "插电预设"),
        slider("ac-tdp", "插电功耗", "ac"),
        freq("ac-freq", "插电 CPU 最高频率", "ac"),
        h(SectionLabel, null, view.charger ? "电池预设" : "电池预设（当前使用）"),
        slider("dc-tdp", "电池功耗", "dc"),
        freq("dc-freq", "电池 CPU 最高频率", "dc")),
      h(SectionLabel, null, "快捷档位"),
      row("quick", h("div", { className: "claw-action-grid", style: { gridTemplateColumns: "1fr 1fr 1fr" } },
        ...draft.quick.map(q => h(ui.ButtonItem, { key: q, layout: "below",
          onClick: () => controller.edit({ mode: "manual", manual: { ...draft.manual, tdp: q } }, 0) }, `${q} W`)))),
      row("quick-hint", h("div", { className: "claw-hint" }, "点快捷档位会切到手动并使用该功耗。")),
      h(SectionLabel, null, "高级"),
      row("boost", h(ui.SliderField, { label: "短时功耗加成", description: "短时功耗（PL2）＝功耗上限＋加成。体感助手原来用 1 W。",
        min: 0, max: limits.boostMax, step: 1, validValues: "steps", minimumDpadGranularity: 1, showValue: true, valueSuffix: " W",
        value: draft.boost, onChange: v => controller.edit({ boost: Math.round(v) }) })),
      view.ecore && row("ecore", h(ui.ToggleField, { label: "全部核心按 E 核调度",
        description: view.ecore.pending ? "已更改，重启掌机后生效。" : "和体感助手的“模拟全核心 E 核”相同，切换后重启生效。",
        checked: view.ecore.enabled, disabled: state.busy, onChange: enabled => controller.setEcore(enabled) })),
      actual?.freq && row("freq-actual", h("div", { className: "claw-hint" },
        `电源方案当前频率上限：插电 ${freqLabel(actual.freq.ac)}，电池 ${freqLabel(actual.freq.dc)}`)),
      row("restore", h(ui.ButtonItem, { layout: "below", disabled: state.busy || !view.backup, onClick: () => controller.restore() },
        "恢复原始功耗设置")),
      state.message && row("message", h("div", { className: "claw-hint" }, state.message)));
  };
}
