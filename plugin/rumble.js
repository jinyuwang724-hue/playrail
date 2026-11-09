// Firmware rumble strength. The pad stores it in ROM, so slider drags are
// debounced into one write after the value settles.
export const RUMBLE_WRITE_DELAY_MS = 700;

export function createRumbleController(api, timers = globalThis) {
  let value = { strength: null, error: null, pending: null };
  let timer;
  const subscribers = new Set();
  const set = next => { value = { ...value, ...next }; subscribers.forEach(fn => fn(value)); };
  const accept = view => set({ strength: view.strength, error: view.error ?? null });
  return {
    get value() { return value; },
    subscribe(fn) { subscribers.add(fn); return () => subscribers.delete(fn); },
    async refresh() {
      try { accept(await api.call("get_rumble_state")); }
      catch (error) { set({ error: String(error) }); }
    },
    edit(strength) {
      set({ pending: strength });
      timers.clearTimeout(timer);
      timer = timers.setTimeout(async () => {
        try { accept(await api.call("set_rumble_strength", strength)); }
        catch (error) { set({ error: String(error) }); }
        finally { if (value.pending === strength) set({ pending: null }); }
      }, RUMBLE_WRITE_DELAY_MS);
    },
    async test() {
      try {
        const result = await api.call("test_rumble");
        // The test pulse never overrides a running game's own rumble.
        set({ error: result?.path === "steam-ui-only" ? "游戏运行时无法测试，可直接在游戏里感受" : null });
      } catch (error) { set({ error: String(error) }); }
    },
    dispose() { timers.clearTimeout(timer); },
  };
}

export function createRumbleRows(R, UI, controller, StatusText) {
  const h = R.createElement;
  return function RumbleRows() {
    const [value, setValue] = R.useState(() => controller.value);
    R.useEffect(() => { const remove = controller.subscribe(setValue); void controller.refresh(); return remove; }, []);
    const shown = value.pending ?? value.strength;
    return h(R.Fragment, null,
      typeof shown === "number" && h(UI.PanelSectionRow, { key: "rumble-strength" }, h(UI.SliderField, {
        label: "震动强度", min: 0, max: 100, step: 5, validValues: "steps", minimumDpadGranularity: 5,
        notchTicksVisible: false, showValue: true, valueSuffix: "%", value: shown,
        onChange: v => controller.edit(Math.round(v / 5) * 5),
      })),
      typeof shown === "number" && h(UI.PanelSectionRow, { key: "rumble-test" },
        h(UI.ButtonItem, { layout: "below", onClick: () => controller.test() }, "测试震动")),
      value.error && h(StatusText, null, value.error));
  };
}
