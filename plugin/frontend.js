import { createMenuRouter } from "./menu-router.js";
import { installNavigationHaptics } from "./navigation-haptics.js";
import { createGameAdder } from "./game-add.js";
import { syncPluginPanel } from "./panel-lifecycle.js";
import { createModuleSelection, createToolboxUI, toolboxModules, toolboxStyles } from "./toolbox-ui.js";
import { createMagpieController, createMagpiePanel } from "./magpie.js";
import { syncOverlayInset, syncQuickAccessSize } from "./magpie.js";
import { installSteamContext } from "./steam-context.js";
import { createPowerController, createPowerPanel, powerSummaries, powerTiles } from "./power.js";
import { createRumbleController, createRumbleRows } from "./rumble.js";
const manifest = { name: "Claw 掌机工具箱" };
const connection = window.__DECKY_SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED_deckyLoaderAPIInit;
if (!connection) throw new Error("Decky 接口不可用");
let api;
try { api = connection.connect(2, manifest.name); }
catch { api = connection.connect(1, manifest.name); }
const R = SP_REACT, UI = DFL, h = R.createElement;
const GameAdder = createGameAdder(window, UI, api, R);
const moduleSelection = createModuleSelection();
const { Home, SubPage, TitleBar, SectionLabel, StatusText } = createToolboxUI(R, UI);
// Side-panel title: the selected Intel policy, or the toolbox name.
function Title() {
  const [value, setValue] = R.useState(() => power.value);
  R.useEffect(() => power.subscribe(setValue), []);
  const auto = value.draft?.enabled && value.draft?.mode === "autotdp" ? value.view?.autotdp : null;
  const smart = value.draft?.enabled && value.draft?.mode === "smart" ? value.view?.smart : null;
  return h(TitleBar, { game: auto ? { name: "Auto 自动功耗", detail: `目标 ${auto.target} 帧` } : smart?.provider === "intel" ? { name: "Intel 智能功耗",
    detail: ({ performance: "性能", balanced: "均衡", battery: "续航" })[smart.profile] ?? "自动调节" } : null });
}
const magpie = createMagpieController(api);
const MagpiePanel = createMagpiePanel(R, UI, magpie, StatusText);
const power = createPowerController(api);
const PowerPanel = createPowerPanel(R, UI, power, StatusText, SectionLabel);
const rumble = createRumbleController(api);
const RumbleRows = createRumbleRows(R, UI, rumble, StatusText);
// While Steam's side menus are open, keep the quick access panel at its on-screen
// size (Steam rebuilds it on its own). The menus go through Magpie's effect chain
// with the game. Runs only while an overlay is visible.
let plainTimer;
const trackPlain = visible => {
  clearInterval(plainTimer); plainTimer = undefined;
  syncQuickAccessSize(window, magpie.value);
  if (visible) plainTimer = setInterval(() => syncQuickAccessSize(window, magpie.value), 120);
};
// Re-apply the menu inset just before the overlay shows, in case Steam rebuilt its window.
const routerOptions = () => ({ port: () => state.status.playhubPort ?? 47991,
  onMenuVisibility: (source, visible) => {
    if (source === "overlay") { if (visible) syncOverlayInset(window, magpie.value); trackPlain(visible); }
    return magpie.pause(source, visible);
  },
  onGameStarted: () => magpie.gameStarted() });
const actions = [
  { label: "左侧主菜单", data: "MainMenu" },
  { label: "右侧快捷面板", data: "QuickAccess" },
  { label: "超分铺满开关", data: "MagpieToggle" },
  { label: "不执行动作", data: "None" },
];
const proxyModes = [{ label: "规则", data: "rule" }, { label: "全局", data: "global" }, { label: "直连", data: "direct" }];
let state = { settings: { enabled: false, msiAction: "QuickAccess", qamAction: "MainMenu", hapticsEnabled: true },
  status: { mode: "Unknown", phase: "正在连接" }, haptics: { phase: "等待导航" }, recent: [] };
const subscribers = new Set();
function update(next) { state = next; subscribers.forEach(fn => fn(state)); }
async function refresh() { update(await api.call("get_state")); }
let compatibilityBusy = false;
async function compatibilityAction(action, feature = null, enabled = null) {
  if (compatibilityBusy) return;
  compatibilityBusy = true;
  update({ ...state, compatibility: { ...state.compatibility, busy: true, phase: "正在处理，请稍候…" } });
  try { update({ ...state, compatibility: await api.call("manage_compatibility", action, feature, enabled) }); }
  catch (error) { update({ ...state, compatibility: { ...state.compatibility, busy: false, phase: String(error) } }); }
  finally { compatibilityBusy = false; }
}
// Steam can remount the panel when a dropdown or keyboard closes. Keep its last real
// state and pending operation outside Content so returning never resets switches to off.
let proxyState = { clash: { available: false, phase: "正在读取 Clash Verge", groups: [] },
  busy: false, message: "", operation: "", link: "" };
const proxySubscribers = new Set();
let proxyRead;
function updateProxy(patch) {
  proxyState = { ...proxyState, ...patch };
  proxySubscribers.forEach(fn => fn(proxyState));
}
async function refreshProxy() {
  if (!proxyRead) {
    proxyRead = api.call("get_clash_state").then(clash => {
      // An unchanged read needs no React update or new dropdown options.
      if (JSON.stringify(clash) !== JSON.stringify(proxyState.clash)) updateProxy({ clash });
      return clash;
    }).finally(() => { proxyRead = null; });
  }
  return proxyRead;
}
function proxyChoiceUnchanged(action, choice, clash) {
  const subscription = clash.subscriptions ?? {};
  if (action === "subscription_mode") return choice === clash.mode;
  if (action === "subscription_tun") return typeof clash.tun === "boolean" && choice === clash.tun;
  if (action === "subscription_system_proxy") {
    const actual = clash.systemProxy ?? subscription.systemProxy;
    return typeof actual === "boolean" && choice === actual;
  }
  if (action === "subscription_profile") return choice === subscription.current;
  if (action === "group") return choice === clash.group;
  if (action === "node") return choice?.group === clash.group && choice?.node === clash.current;
  return false;
}
const routers = new WeakMap();
export function executeMenu(action, env = window, ui = UI) {
  if (!routers.has(env)) routers.set(env, createMenuRouter(env, ui, routerOptions()));
  return routers.get(env).execute(action);
}
export function Content() {
  const [activeModule, setActiveModule] = R.useState(() => moduleSelection.value);
  const [value, setValue] = R.useState(state);
  const [busy, setBusy] = R.useState(false);
  const [message, setMessage] = R.useState("");
  const [proxy, setProxy] = R.useState(() => proxyState);
  // The home page shows power and picture state without opening those modules.
  const [powerValue, setPowerValue] = R.useState(() => power.value);
  const [magpieValue, setMagpieValue] = R.useState(() => magpie.value);
  const { clash, busy: proxyBusy, message: proxyMessage, operation: proxyOperation, link: subscriptionLink } = proxy;
  R.useEffect(() => {
    let mounted = true;
    const unsubscribeModule = moduleSelection.subscribe(setActiveModule);
    subscribers.add(setValue);
    proxySubscribers.add(setProxy);
    setProxy(proxyState);
    const removePower = power.subscribe(setPowerValue), removeMagpie = magpie.subscribe(setMagpieValue);
    // Read-only: neither read applies a setting.
    if (!power.value.view) power.refresh(false);
    api.call("get_magpie_state").then(value => { if (mounted) magpie.accept(value); }).catch(() => {});
    refresh().catch(e => { if (mounted) setMessage(String(e)); });
    if (!proxyState.busy) refreshProxy().catch(() => {
      updateProxy({ message: "暂时无法读取 Clash Verge 状态，请确认它正在运行" });
    });
    return () => { mounted = false; unsubscribeModule(); subscribers.delete(setValue); proxySubscribers.delete(setProxy); removePower(); removeMagpie(); };
  }, []);
  // Live readings on the home page; module pages keep their own refresh.
  R.useEffect(() => {
    if (activeModule !== null) return undefined;
    // Sampled reads: an unsampled read carries no package power.
    const poll = setInterval(() => power.refresh(true), 3000);
    return () => clearInterval(poll);
  }, [activeModule]);
  const proxyAction = async (action, choice) => {
    if (proxyState.busy || proxyChoiceUnchanged(action, choice, clash)) return;
    if (proxyRead) { try { await proxyRead; } catch {} }
    if (proxyState.busy || proxyChoiceUnchanged(action, choice, proxyState.clash)) return;
    updateProxy({ busy: true, message: "", operation: ({ subscription_import: "正在添加订阅，请稍候…", subscription_update: "正在刷新订阅，请稍候…", subscription_mode: "正在切换代理模式…", subscription_tun: "正在切换 TUN 模式…", subscription_system_proxy: "正在切换系统代理…", test: "正在验证当前节点…" })[action] ?? "正在处理，请稍候…" });
    try {
      const next = await api.call("set_clash", action, choice);
      updateProxy({ clash: next, ...(action === "subscription_import" ? { link: "" } : {}) });
      if (action === "subscription_import" || action === "subscription_update") {
        api.toaster?.toast?.({ title: action === "subscription_import" ? "订阅已添加" : "订阅已刷新", body: next.subscriptions?.message ?? "操作完成" });
      }
    }
    catch (e) {
      updateProxy({ message: String(e) });
      try { await refreshProxy(); } catch { /* Keep the last known switches instead of displaying a false off state. */ }
    }
    finally { updateProxy({ busy: false, operation: "" }); }
  };
  const proxyGroup = clash.groups?.find(group => group.name === clash.group);
  const subscription = clash.subscriptions ?? { connected: false, items: [] };
  const subscriptionItem = subscription.items?.find(item => item.uid === subscription.current);
  const systemProxy = clash.systemProxy ?? subscription.systemProxy;
  const subscriptionOptions = R.useMemo(() => (subscription.items ?? []).map(item => ({ label: item.name, data: item.uid })), [subscription.items]);
  const groupOptions = R.useMemo(() => (clash.groups ?? []).filter(group => group.name !== "GLOBAL").map(group => ({ label: group.name, data: group.name })), [clash.groups]);
  const nodeOptions = R.useMemo(() => (proxyGroup?.nodes ?? []).map(node => ({ label: node, data: node })), [proxyGroup?.nodes]);
  const save = async patch => {
    if (busy || Object.entries(patch).every(([key, next]) => next === state.settings[key])) return;
    setBusy(true); setMessage("");
    try { update(await api.call("save_settings", { ...state.settings, ...patch })); void window.__clawToolboxHaptics?.sync?.(true); }
    catch (e) { setMessage(String(e)); }
    finally { setBusy(false); }
  };
  const field = (label, content) => h(UI.PanelSectionRow, { key: label }, h("div", { className: "claw-field" }, h("span", { className: "claw-field-label" }, label), h("div", { className: "claw-field-value" }, content)));
  const compatibility = value.compatibility ?? { enabled: {}, items: {}, busy: true, phase: "正在读取修复状态" };
  const contents = {
    magpie: h(MagpiePanel, { key: "magpie" }),
    power: h(PowerPanel, { key: "power", section: "power" }),
    osd: h(PowerPanel, { key: "osd", section: "osd" }),
    fan: h(PowerPanel, { key: "fan", section: "fan" }),
    compatibility: h(UI.PanelSection, { key: "compatibility" },
      h(StatusText, null, compatibility.phase),
      h(UI.PanelSectionRow, null, h(UI.ToggleField, { label: "Steam 启动修复", description: "避免重启后普通权限 Steam 抢先启动，保持游戏运行监控。关闭时恢复原设置。", checked: !!compatibility.enabled?.steam, disabled: compatibility.busy || compatibilityBusy, onChange: enabled => compatibilityAction("set", "steam", enabled) })),
      compatibility.items?.steam && field("Steam 状态", compatibility.items.steam),
      h(UI.PanelSectionRow, null, h(UI.ToggleField, { label: "Game Bar 主页修复", description: "桌面模式按 Xbox 键打开主页，保留 MSI 快捷栏。关闭时恢复原设置。", checked: !!compatibility.enabled?.gamebar, disabled: compatibility.busy || compatibilityBusy, onChange: enabled => compatibilityAction("set", "gamebar", enabled) })),
      compatibility.items?.gamebar && field("Game Bar 状态", compatibility.items.gamebar),
      h(UI.PanelSectionRow, null, h(UI.ButtonItem, { layout: "below", disabled: compatibility.busy || compatibilityBusy, onClick: () => compatibilityAction("check") }, "检查并修复")),
      h(UI.PanelSectionRow, null, h(UI.ButtonItem, { layout: "below", disabled: compatibility.busy || compatibilityBusy || (!compatibility.enabled?.steam && !compatibility.enabled?.gamebar), onClick: () => compatibilityAction("restore") }, "关闭修复并恢复原设置"))),
    mapping: h(UI.PanelSection, { key: "mapping" },
      h(StatusText, null, value.status.phase),
      h(UI.PanelSectionRow, { key: "mapping-enabled" }, h(UI.ToggleField, { label: "自动接管", description: "大屏幕打开时接管并屏蔽 Game Bar 快捷键，退出后恢复。最小化或运行游戏时仍生效。", checked: value.settings.enabled, disabled: busy, onChange: enabled => save({ enabled }) })),
      h(SectionLabel, null, "快捷键动作"),
      h(UI.PanelSectionRow, { key: "mapping-msi" }, h(UI.DropdownItem, { label: "MSI 键", rgOptions: actions, selectedOption: value.settings.msiAction, disabled: busy, onChange: option => save({ msiAction: option.data }) })),
      h(UI.PanelSectionRow, { key: "mapping-xbox" }, h(UI.DropdownItem, { label: "Xbox 键", rgOptions: actions, selectedOption: value.settings.qamAction, disabled: busy, onChange: option => save({ qamAction: option.data }) })),
      (value.status.error || message) && field("提示", value.status.error || message)),
    haptics: h(UI.PanelSection, { key: "haptics" },
      h(RumbleRows, { key: "rumble" }),
      h(SectionLabel, null, "界面导航震动"),
      h(StatusText, null, value.haptics?.phase ?? "等待导航"),
      h(UI.PanelSectionRow, { key: "haptics-enabled" }, h(UI.ToggleField, { label: "手柄反馈修复", checked: value.settings.hapticsEnabled, disabled: busy, onChange: hapticsEnabled => save({ hapticsEnabled }) }))),
    games: h(GameAdder, { key: "game-add", embedded: true }),
    proxy: h(UI.PanelSection, { key: "proxy" },
      h(StatusText, null, proxyBusy ? proxyOperation : clash.phase),
      h(SectionLabel, null, "网络设置"),
      !clash.available && h(UI.PanelSectionRow, { key: "proxy-launch" }, h(UI.ButtonItem, { layout: "below", disabled: proxyBusy, onClick: () => proxyAction("launch", null) }, "启动 Clash Verge")),
      typeof systemProxy === "boolean" ? h(UI.PanelSectionRow, { key: "proxy-system" }, h(UI.ToggleField, { label: "系统代理", description: "让浏览器等应用使用 Clash 代理。", checked: systemProxy, disabled: proxyBusy, onChange: enabled => proxyAction("subscription_system_proxy", enabled) })) : field("系统代理", "等待读取状态"),
      h(UI.PanelSectionRow, { key: "proxy-mode" }, h(UI.DropdownItem, { label: "代理模式", description: clash.mode === "global" ? "所有流量使用全局节点，订阅中的直连分流规则不生效。" : "规则模式按订阅分流；直连模式直接连接。", rgOptions: proxyModes, selectedOption: clash.mode, disabled: proxyBusy || !clash.available, onChange: option => proxyAction("subscription_mode", option.data) })),
      typeof clash.tun === "boolean" ? h(UI.PanelSectionRow, { key: "proxy-tun" }, h(UI.ToggleField, { label: "TUN 模式", description: "接管不使用系统代理的应用，需 Clash 服务支持。", checked: clash.tun, disabled: proxyBusy, onChange: enabled => proxyAction("subscription_tun", enabled) })) : field("TUN 模式", "等待读取状态"),
      h(SectionLabel, null, "节点选择"),
      clash.available && clash.mode !== "global" && h(UI.PanelSectionRow, { key: "proxy-group" }, h(UI.DropdownItem, { label: "代理组", rgOptions: groupOptions, selectedOption: clash.group, disabled: proxyBusy, onChange: option => proxyAction("group", option.data) })),
      proxyGroup && h(UI.PanelSectionRow, { key: "proxy-node" }, h(UI.DropdownItem, { label: clash.mode === "global" ? "全局节点" : "当前节点", rgOptions: nodeOptions, selectedOption: clash.current, disabled: proxyBusy, onChange: option => proxyAction("node", { group: clash.group, node: option.data }) })),
      clash.resolved && clash.resolved !== clash.current && field("实际节点", clash.resolved),
      Number.isInteger(clash.nodeCount) && h(UI.PanelSectionRow, null, h("div", { className: "claw-hint" }, `已载入 ${clash.nodeCount} 个节点`)),
      h(UI.PanelSectionRow, { key: "proxy-test" }, h(UI.ButtonItem, { layout: "below", disabled: proxyBusy || !clash.available || !proxyGroup, onClick: () => proxyAction("test", null) }, "验证测试")),
      clash.test && field(clash.test.ok ? "验证通过" : "验证未通过", h(R.Fragment, null,
        h("div", null, clash.test.message + (clash.test.delay ? ` · ${clash.test.delay} 毫秒` : "")),
        h("div", { style: { fontSize: "12px", opacity: .75, overflowWrap: "anywhere" } }, clash.test.node))),
      h(SectionLabel, null, "订阅管理"),
      h(UI.PanelSectionRow, { key: "subscription-link" }, h(UI.TextField, { label: "订阅链接", placeholder: "在这里粘贴 Clash 订阅链接", type: "password", value: subscriptionLink, disabled: proxyBusy, onChange: event => updateProxy({ link: event.target?.value ?? "" }) })),
      h(UI.PanelSectionRow, { key: "subscription-add" }, h(UI.ButtonItem, { layout: "below", disabled: proxyBusy || !subscriptionLink.trim(), onClick: () => proxyAction("subscription_import", subscriptionLink.trim()) }, "添加订阅")),
      subscription.items?.length > 0 && h(UI.PanelSectionRow, { key: "subscription-current" }, h(UI.DropdownItem, { label: "当前订阅", rgOptions: subscriptionOptions, selectedOption: subscription.current, disabled: proxyBusy, onChange: option => proxyAction("subscription_profile", option.data) })),
      h(UI.PanelSectionRow, { key: "subscription-refresh" }, h(UI.ButtonItem, { layout: "below", disabled: proxyBusy || (subscription.connected && subscriptionItem?.remote === false), onClick: () => proxyAction("subscription_update", null) }, "刷新订阅")),
      subscriptionItem && field("订阅信息", h(R.Fragment, null,
        h("div", null, subscriptionItem.source),
        subscriptionItem.updated && h("div", { style: { fontSize: "12px", opacity: .75 } }, `更新时间：${new Date(subscriptionItem.updated * 1000).toLocaleString("zh-CN", { timeZone: "Asia/Shanghai", hour12: false })}`))),
      proxyMessage && field("提示", proxyMessage)),
  };
  const proxyModeLabel = proxyModes.find(item => item.data === clash.mode)?.label;
  const states = {
    ...powerSummaries(powerValue),
    magpie: magpieValue.enabled ? { text: magpieValue.active ? "铺满中" : "已开启" } : { text: "关", off: true },
    mapping: value.settings.enabled ? { text: "接管中" } : { text: "关", off: true },
    haptics: value.settings.hapticsEnabled ? { text: "开" } : { text: "关", off: true },
    proxy: !clash.available ? { text: "未运行", off: true } : proxyModeLabel ? { text: proxyModeLabel } : undefined,
    compatibility: compatibility.enabled?.steam || compatibility.enabled?.gamebar ? { text: "已启用" } : undefined,
  };
  const open = id => moduleSelection.select(id);
  const page = toolboxModules.find(module => module.id === activeModule);
  let body;
  if (page) body = h(SubPage, { module: page, state: states[page.id], onBack: () => open(null) }, contents[page.id]);
  else {
    const view = powerValue.view, draft = powerValue.draft, live = view?.live ?? {}, smart = draft?.mode === "autotdp" ? view?.autotdp : view?.smart;
    const managed = !!draft?.enabled;
    const round = (v, digits = 0) => typeof v === "number" ? Math.round(v * 10 ** digits) / 10 ** digits : null;
    const fps = round(smart?.fps ?? live.fps), target = managed ? smart?.target : null;
    const toggleMagpie = async () => {
      if (magpieValue.busy) return;
      const result = await magpie.toggle();
      api.toaster?.toast?.({ title: "超分铺满", body: result.ok ? result.message : result.reason });
    };
    const [powerTile, ...otherTiles] = powerTiles(powerValue, power, open);
    body = h(Home, {
      metrics: [
        { label: "功耗", value: managed ? round(view?.actual?.package, 1) : null, unit: "W" },
        { label: "CPU", value: round(live.cpuTemp), unit: "°C" },
        { label: "帧率", value: fps },
        { label: view ? (view.charger ? "插电" : "电池") : "电量", value: live.battery ?? null, unit: "%" },
      ],
      banner: managed && view?.otherTool ? "体感助手也在调功耗，两边会互相改写。请在体感助手里关掉“自动设置 TDP”，或退出体感助手。" : null,
      tiles: [powerTile,
        { id: "magpie", label: "超分铺满", icon: "magpie", on: !!magpieValue.enabled, steps: 1, filled: magpieValue.enabled ? 1 : 0,
          value: magpieValue.enabled ? (magpieValue.active ? "铺满中" : "已开启") : "关", disabled: !!magpieValue.busy, onPress: toggleMagpie },
        ...otherTiles].filter(Boolean),
      onOpen: open,
    });
  }
  return h("div", { className: "claw-toolbox-ui" }, h("style", null, toolboxStyles), body);
}
export default function plugin() {
  let disposed = false;
  const disposePanel = syncPluginPanel(window, manifest.name, Content);
  const frontendId = `claw-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  let frontendReady;
  const haptics = installNavigationHaptics(window, (method, ...args) => api.call(method, ...args), {
    frontendId, ready: () => frontendReady,
    enabled: () => !disposed && state.settings.hapticsEnabled,
    port: () => state.status.playhubPort ?? 47991,
    onError: () => update({ ...state, haptics: { ...state.haptics, phase: "连接暂不可用" } }),
  });
  // Register Steam signals during plugin load, before the first physical press.
  if (!routers.has(window)) routers.set(window, createMenuRouter(window, UI, routerOptions()));
  const listener = async event => {
    if (disposed) return;
    if (event.type === "status") {
      routers.get(window)?.modeChanged(event.enabled && event.ready ? event.mode : "Unknown");
      update({ ...state, status: event, settings: { ...state.settings, enabled: event.enabled } });
    }
    if (event.type === "button") update({ ...state, recent: [...state.recent, event].slice(-8) });
    if (event.type === "action") {
      const start = performance.now();
      const bridgeMs = Math.max(0, Date.now() - Date.parse(event.at));
      // The Magpie toggle is a backend action; menus go through the Steam router.
      const toggle = event.action === "MagpieToggle";
      const result = toggle ? await magpie.toggle() : await executeMenu(event.action);
      const menuMs = performance.now() - start;
      // A toggle that finds no game is reported here, not as a menu failure that
      // would count toward releasing the button mapping.
      await api.call("action_result", { id: event.id, ok: toggle || result.ok, path: result.path, reason: result.reason,
        timings: { nativeMs: event.nativeMs, bridgeMs, menuMs } });
      if (toggle) api.toaster?.toast?.({ title: "超分铺满", body: result.ok ? result.message : result.reason });
      else if (!result.ok) api.toaster?.toast?.({ title: "Steam 菜单未打开", body: result.reason });
    }
  };
  api.addEventListener("router_event", listener);
  const hapticListener = haptics => { if (!disposed) update({ ...state, haptics }); };
  api.addEventListener("haptic_status", hapticListener);
  const magpieListener = value => { if (!disposed) magpie.accept(value); };
  const removeInset = magpie.subscribe(value => { syncOverlayInset(window, value); syncQuickAccessSize(window, value); });
  api.addEventListener("magpie_status", magpieListener);
  const powerListener = value => { if (!disposed) power.accept(value); };
  api.addEventListener("power_status", powerListener);
  frontendReady = api.call("frontend_ready", true, frontendId);
  const steamContext = installSteamContext(window, UI, (method, ...args) => api.call(method, ...args), { frontendId, ready: frontendReady,
    onError: error => console.warn("[Claw] Steam context unavailable", error) });
  frontendReady.then(value => { update(value); void haptics.sync(); }).catch(error => update({ ...state, status: { ...state.status, error: String(error) } }));
  return {
    name: manifest.name, title: h(Title), content: h(Content),
    icon: UI.IconsModule?.Controller ? h(UI.IconsModule.Controller, { width: 20, height: 20, style: { width: 20, height: 20 } }) : null,
    onDismount() { disposed = true; clearInterval(plainTimer); removeInset(); syncOverlayInset(window, null); syncQuickAccessSize(window, null); steamContext.dispose(); disposePanel(); haptics.dispose(); routers.get(window)?.dispose(); routers.delete(window); magpie.dispose(); power.dispose(); rumble.dispose(); api.removeEventListener("power_status", powerListener); api.removeEventListener("magpie_status", magpieListener); api.removeEventListener("router_event", listener); api.removeEventListener("haptic_status", hapticListener); api.call("frontend_ready", false, frontendId).catch(() => {}); },
  };
}
