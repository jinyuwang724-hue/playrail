// Claw toolbox Game Bar widget. Talks to the toolbox agent over a local WebSocket (endpoint and
// token are published into this app's LocalState, mapped to https://state.claw/agent.json).
// Layout and states follow the approved design ("UI design" folder).
"use strict";

const ICONS = {
  power: '<path d="M13 2.5 4.5 14H11l-1 7.5L18.5 10H12z"/>',
  fan: '<circle cx="12" cy="12" r="2"/><path d="M12 10c-1-4 0-7 3-7s3 4-1 7M14 12c4-1 7 0 7 3s-4 3-7-1M12 14c1 4 0 7-3 7s-3-4 1-7M10 12c-4 1-7 0-7-3s4-3 7 1"/>',
  fps: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 15l3-4 3 3 4-5"/>',
  refresh: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
  osd: '<path d="M4 17a8 8 0 1 1 16 0"/><path d="M12 17l4-5"/>',
  magpie: '<path d="M4 10V4h6M20 14v6h-6M4 4l6 6M20 20l-6-6"/>',
  rumble: '<rect x="8" y="3" width="8" height="18" rx="2"/><path d="M4 8v8M20 8v8"/>',
  keyboard: '<rect x="2.5" y="6" width="19" height="12" rx="2"/><path d="M6 10h.01M9 10h.01M12 10h.01M15 10h.01M18 10h.01M8 14h8"/>',
  close: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9.5 9.5l5 5M14.5 9.5l-5 5"/>',
  target: '<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="3"/>',
  cpu: '<rect x="6" y="6" width="12" height="12" rx="2"/><path d="M9 3v3M15 3v3M9 18v3M15 18v3M3 9h3M3 15h3M18 9h3M18 15h3"/>',
  fanfull: '<path d="M4 12h3l2-5 3 10 2-5h6"/>',
  ecore: '<path d="M7 7h10M7 12h7M7 17h10M7 7v10"/>',
  sharpen: '<path d="M12 3 4 20h16z"/>',
  screenshot: '<path d="M4 8V6a2 2 0 0 1 2-2h2M16 4h2a2 2 0 0 1 2 2v2M20 16v2a2 2 0 0 1-2 2h-2M8 20H6a2 2 0 0 1-2-2v-2"/><circle cx="12" cy="12" r="3"/>',
  edit: '<path d="M4 20h4L19 9l-4-4L4 16z"/><path d="M13.5 6.5l4 4"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2.5v2M12 19.5v2M4.6 4.6l1.4 1.4M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4"/>',
  speaker: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12"/>',
  muted: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M17 9l5 6M22 9l-5 6"/>',
  shutdown: '<path d="M12 3v8"/><path d="M6.3 6.8a8 8 0 1 0 11.4 0"/>',
  sleep: '<path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/>',
  restart: '<path d="M20 12a8 8 0 1 1-2.3-5.7"/><path d="M20 4v4h-4"/>',
  lock: '<rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
  minus: '<path d="M6 12h12"/>',
  plus: '<path d="M12 6v12M6 12h12"/>',
  move: '<path d="M12 4v16M4 12h16M12 4l-3 3M12 4l3 3M12 20l-3-3M12 20l3-3M4 12l3-3M4 12l3 3M20 12l-3-3M20 12l-3 3"/>',
};
const icon = name => `<svg class="ic" viewBox="0 0 24 24" aria-hidden="true">${ICONS[name]}</svg>`;
const esc = text => String(text ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const ghz = mhz => `${+(mhz / 1000).toFixed(2)} GHz`;

// ---- agent connection ---------------------------------------------------------------------
const agent = {
  ws: null, ready: false, seq: 0, pending: new Map(), listeners: new Set(),
  async connect() {
    this.close();
    // ?ws=<port>&token=<token> lets the page be previewed in an ordinary browser.
    const query = new URLSearchParams(location.search);
    const endpoint = query.has("ws") ? { wsPort: query.get("ws"), token: query.get("token") }
      : await (await fetch("https://state.claw/agent.json", { cache: "no-store" })).json();
    await new Promise((resolve, reject) => {
      const ws = new WebSocket(`ws://127.0.0.1:${endpoint.wsPort}`);
      this.ws = ws;
      const timer = setTimeout(() => { ws.close(); reject(new Error("timeout")); }, 4000);
      ws.onopen = () => ws.send(JSON.stringify({ hello: endpoint.token, client: "gamebar" }));
      ws.onmessage = event => {
        const message = JSON.parse(event.data);
        if (message.hello === "ok") { clearTimeout(timer); this.ready = true; resolve(); return; }
        if (!this.ready) return;
        if (message.event) { this.listeners.forEach(fn => fn(message.event, message.value)); return; }
        const waiter = this.pending.get(message.id);
        if (!waiter) return;
        this.pending.delete(message.id);
        message.error ? waiter.reject(new Error(message.error)) : waiter.resolve(message.result);
      };
      ws.onclose = () => {
        clearTimeout(timer);
        const was = this.ready;
        this.ready = false;
        this.pending.forEach(w => w.reject(new Error("工具箱后台连接中断")));
        this.pending.clear();
        if (was) this.listeners.forEach(fn => fn("closed"));
        reject(new Error("closed"));
      };
    });
  },
  async call(method, ...args) {
    for (let i = 0; !this.ready && i < 40; i++) await new Promise(r => setTimeout(r, 100));
    if (!this.ready) throw new Error("工具箱后台未连接");
    const id = ++this.seq;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.ws.send(JSON.stringify({ id, method, args }));
    });
  },
  close() { if (this.ws) { this.ws.onclose = null; this.ws.close(); } this.ws = null; this.ready = false; },
};

// ---- state -----------------------------------------------------------------------------------
const DEFAULT_TILES = ["power", "fan", "fps", "refresh", "osd", "magpie", "rumble", "keyboard", "close"];
const POOL = ["target", "cpu", "fanfull", "ecore", "sharpen", "screenshot"];
let tiles = load("claw.tiles", DEFAULT_TILES).filter(id => DEFAULT_TILES.includes(id) || POOL.includes(id));
// The widget opens on the last readings it saw and connects quietly behind them: Game Bar starts
// it cold most times, and a "connecting" screen in front of every open is what the user rejected.
const cached = load("claw.state", null);
const S = {
  conn: cached ? "online" : "waiting", data: cached, draft: cached ? structuredClone(cached.power.settings) : null,
  message: "", menu: false, edit: false, moving: null, arm: null, focus: null, failedSince: 0,
};

function load(key, fallback) { try { return JSON.parse(localStorage.getItem(key)) || fallback; } catch { return fallback; } }
function save(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* storage unavailable */ } }

let connecting = null;
function connect() {
  if (connecting) return connecting;
  connecting = (async () => {
    try {
      await agent.connect();
      S.failedSince = 0;
      await refresh();
    } catch {
      // Only after a few seconds without the agent is it worth telling the user.
      S.failedSince ||= Date.now();
      if (Date.now() - S.failedSince > 4000 || !S.data) S.conn = "offline";
      render();
    } finally { connecting = null; }
  })();
  return connecting;
}

// Values the user just changed win over readings for a moment: the agent and the hardware lag a
// press by up to a second, and a reading taken before the change must not pull the tile back.
const held = new Map();
function getPath(object, path) { return path.split(".").reduce((o, k) => o?.[k], object); }
function setPath(object, path, value) {
  const keys = path.split("."), last = keys.pop();
  const target = keys.reduce((o, k) => (o[k] ??= {}), object);
  target[last] = value;
}
// A hold lasts while its request is in flight and a moment after (see send), at least `ms`.
function hold(path, value, ms = 3000, key = null) {
  setPath(S.data, path, value);
  held.set(path, { value, key, until: key ? Infinity : Date.now() + ms });
}
function release(key) {
  for (const h of held.values()) if (h.key === key) h.until = Date.now() + 1500;
  if (key === "power") S.draftHeld = Date.now() + 1500;
}
function applyHolds(data) {
  for (const [path, h] of held) {
    if (h.until > Date.now()) setPath(data, path, h.value);
    else held.delete(path);
  }
  return data;
}

let lastSaved = 0;
function accept(data) {
  S.data = applyHolds(data);
  if (!S.saveTimer && !(S.draftHeld > Date.now())) S.draft = structuredClone(S.data.power.settings);
  S.conn = "online";
  if (Date.now() - lastSaved > 5000) { lastSaved = Date.now(); save("claw.state", S.data); }
}

async function refresh() {
  if (!agent.ready) return;
  try { accept(await agent.call("widget_state")); } catch (error) { if (agent.ready) flash(error.message); }
  render();
}

agent.listeners.add((event, value) => {
  if (event === "closed") { connect(); return; }
  if (!S.data) return;
  if (event === "power_status") { accept({ ...S.data, power: value }); render(); }
  if (event === "magpie_status") { accept({ ...S.data, magpie: value }); render(); }
});

// One request per control in flight; presses made meanwhile collapse into the latest value.
const sending = new Map();
async function send(key, method, ...args) {
  if (sending.has(key)) { sending.set(key, { method, args }); return; }
  sending.set(key, null);
  let next = { method, args };
  while (next) {
    try { await agent.call(next.method, ...next.args); } catch (error) { flash(error.message || String(error)); }
    next = sending.get(key);
    sending.set(key, null);
  }
  sending.delete(key);
  release(key);
}

function flash(text) {
  S.message = text;
  clearTimeout(S.flashTimer);
  S.flashTimer = setTimeout(() => { S.message = ""; render(); }, 4000);
  render();
}

async function run(promise) {
  try { return await promise; } catch (error) { flash(error.message || String(error)); }
}

// Power settings edits: the draft changes at once; the agent gets the latest draft shortly after
// (a run of presses sends once), and readings do not replace the draft until it has settled.
function editPower(patch, delay = 200) {
  if (!S.draft) return;
  S.draft = { ...S.draft, ...patch };
  S.draftHeld = Infinity;
  clearTimeout(S.saveTimer);
  S.saveTimer = setTimeout(() => {
    S.saveTimer = null;
    send("power", "save_power", structuredClone(S.draft));
  }, delay);
  render();
}

// ---- tiles ------------------------------------------------------------------------------------
// Each tile: {name, icon, levels(): {index, count, value} | {value}, press()}.
const FPS_STEPS = [0, 30, 40, 45, 60, 90, 120];
const RUMBLE_STEPS = [[0, "关"], [35, "弱"], [70, "中"], [100, "强"]];
const FAN_STEPS = [["firmware", "固件"], ["quiet", "静音"], ["balanced", "均衡"], ["performance", "性能"]];
const nearest = (list, value) => list.reduce((best, v, i) => Math.abs(v - value) < Math.abs(list[best] - value) ? i : best, 0);

const TILES = {
  power: {
    name: "功耗", icon: "power",
    levels() {
      const d = S.draft, quick = d.quick;
      const index = !d.enabled ? 0 : d.mode === "smart" ? 1 : d.mode === "manual" && quick.includes(d.manual.tdp) ? 2 + quick.indexOf(d.manual.tdp) : 1;
      const value = !d.enabled ? "系统控制" : d.mode === "smart" ? `智能 · ${S.data.power.smart?.limit ?? S.data.power.target?.pl1 ?? "--"} W`
        : d.mode === "manual" ? `${d.manual.tdp} W` : `预设 · ${S.data.power.target?.pl1} W`;
      return { index, count: quick.length + 2, value };
    },
    press() {
      const d = S.draft, { index, count } = this.levels(), next = (index + 1) % count;
      if (next === 0) editPower({ enabled: false });
      else if (next === 1) editPower({ enabled: true, mode: "smart" });
      else editPower({ enabled: true, mode: "manual", manual: { ...d.manual, tdp: d.quick[next - 2] } });
    },
  },
  fan: {
    name: "风扇", icon: "fan",
    levels() { const i = Math.max(0, FAN_STEPS.findIndex(([m]) => m === S.draft.fan.mode)); return { index: i, count: 4, value: FAN_STEPS[i][1] }; },
    press() { const { index } = this.levels(); editPower({ fan: { ...S.draft.fan, mode: FAN_STEPS[(index + 1) % 4][0] } }); },
  },
  fps: {
    name: "限帧", icon: "fps",
    levels() { const v = S.data.system.fpsLimit ?? 0, i = nearest(FPS_STEPS, v); return { index: i, count: FPS_STEPS.length, value: v ? `${v} 帧` : "不限" }; },
    press() { const { index } = this.levels(), v = FPS_STEPS[(index + 1) % FPS_STEPS.length]; hold("system.fpsLimit", v, 0, "fps"); send("fps", "set_fps_limit", v); },
    disabled: () => S.data.system.fpsLimit == null,
  },
  refresh: {
    name: "刷新率", icon: "refresh",
    rates: () => (S.data.system.refreshRates || []).filter(r => r >= 60),
    levels() { const rates = this.rates(), v = S.data.system.refresh, i = Math.max(0, rates.indexOf(v)); return { index: i, count: rates.length || 1, value: v ? `${v} Hz` : "--" }; },
    press() { const rates = this.rates(); if (rates.length < 2) return; const { index } = this.levels(), v = rates[(index + 1) % rates.length]; hold("system.refresh", v, 0, "refresh"); send("refresh", "set_refresh_rate", v); },
  },
  osd: {
    name: "性能显示", icon: "osd",
    levels() { const o = S.draft.osd, i = o.enabled ? o.level : 0; return { index: i, count: 5, value: i ? `${i} 档` : "关" }; },
    press() { const o = S.draft.osd, next = ((o.enabled ? o.level : 0) + 1) % 5; editPower({ osd: { ...o, enabled: next > 0, level: next || o.level } }); },
  },
  magpie: {
    name: "画面缩放", icon: "magpie",
    levels() { const on = !!S.data.magpie.enabled; return { index: on ? 1 : 0, count: 2, value: on ? "开" : "关" }; },
    press() { hold("magpie.enabled", !S.data.magpie.enabled, 0, "magpie"); send("magpie", "toggle_magpie"); },
    disabled: () => !S.data.magpie.available,
  },
  rumble: {
    name: "震动", icon: "rumble",
    levels() { const v = S.data.rumble.strength ?? 100, i = nearest(RUMBLE_STEPS.map(s => s[0]), v); return { index: i, count: 4, value: RUMBLE_STEPS[i][1] }; },
    press() { const { index } = this.levels(), v = RUMBLE_STEPS[(index + 1) % 4][0]; hold("rumble.strength", v, 0, "rumble"); send("rumble", "set_rumble_strength", v); },
  },
  keyboard: {
    name: "显示键盘", icon: "keyboard",
    levels: () => ({ value: "打开" }),
    press() { window.chrome?.webview?.postMessage({ keyboard: true }); send("keyboard", "toggle_keyboard"); },
  },
  close: {
    name: "关闭程序", icon: "close",
    levels: () => ({ value: S.data.system.game?.title ?? "没有游戏" }),
    disabled: () => !S.data.system.game,
    press() {
      const game = S.data.system.game;
      if (!game) return;
      if (S.arm && S.arm.pid === game.pid) { clearArm(); return run(agent.call("close_game", game.pid).then(refresh)); }
      armClose(game);
    },
  },
  target: {
    name: "目标帧率", icon: "target",
    levels() { const v = S.draft.smart.target, i = Math.max(0, FPS_STEPS.indexOf(v)); return { index: i, count: FPS_STEPS.length, value: v ? `${v} 帧` : "不限" }; },
    press() { const { index } = this.levels(); editPower({ smart: { ...S.draft.smart, target: FPS_STEPS[(index + 1) % FPS_STEPS.length] } }); },
    hint: "智能模式用",
  },
  cpu: {
    name: "CPU 频率", icon: "cpu",
    profile() { const d = S.draft; return d.mode === "manual" ? "manual" : S.data.power.charger ? "ac" : "dc"; },
    levels() {
      const choices = S.data.power.limits.freq, v = S.draft[this.profile()].freq, i = Math.max(0, choices.indexOf(v));
      return { index: i, count: choices.length, value: v ? ghz(v) : "最高" };
    },
    press() {
      const choices = S.data.power.limits.freq, p = this.profile(), { index } = this.levels();
      editPower({ [p]: { ...S.draft[p], freq: choices[(index + 1) % choices.length] } });
    },
    hint: "最高 / 限频",
  },
  fanfull: {
    name: "风扇全速", icon: "fanfull",
    levels() { const on = !!S.draft.fan.full; return { index: on ? 1 : 0, count: 2, value: on ? "开" : "关" }; },
    press() { editPower({ fan: { ...S.draft.fan, full: !S.draft.fan.full } }, 0); },
    disabled: () => S.draft.fan.mode === "firmware",
    hint: "开 / 关",
  },
  ecore: {
    name: "E 核调度", icon: "ecore",
    levels() { const e = S.data.power.ecore || {}; return { index: e.enabled ? 1 : 0, count: 2, value: (e.enabled ? "开" : "关") + (e.pending ? " · 重启后生效" : "") }; },
    press() { const v = !S.data.power.ecore?.enabled; hold("power.ecore.enabled", v, 0, "ecore"); send("ecore", "set_ecore", v); },
    hint: "重启后生效",
  },
  sharpen: {
    name: "画面锐化", icon: "sharpen",
    levels() { const on = !!S.data.magpie.preferences?.sharpen; return { index: on ? 1 : 0, count: 2, value: on ? "开" : "关" }; },
    disabled: () => !S.data.magpie.enabled,
    press() {
      const m = S.data.magpie;
      return run(agent.call("apply_magpie", { ...m.preferences, target: m.target, sharpen: !m.preferences.sharpen }));
    },
    hint: "Magpie",
  },
  screenshot: {
    name: "截图", icon: "screenshot",
    levels: () => ({ value: "保存到相册" }),
    press() { send("screenshot", "screenshot"); },
    hint: "保存到相册",
  },
};

function armClose(game) {
  clearArm();
  S.arm = { pid: game.pid, left: 3 };
  S.arm.timer = setInterval(() => {
    S.arm.left -= 1;
    if (S.arm.left <= 0) clearArm();
    render();
  }, 1000);
  render();
}
function clearArm() { if (S.arm) clearInterval(S.arm.timer); S.arm = null; render(); }

// ---- rendering --------------------------------------------------------------------------------
const root = document.getElementById("app");

function dots(level) {
  if (level.count == null || level.count < 2) return "";
  return `<span class="dots" aria-label="第 ${level.index + 1} 档，共 ${level.count} 档">${Array.from({ length: level.count }, (_, i) => `<i${i === level.index ? ' class="c"' : ""}></i>`).join("")}</span>`;
}

function tileHtml(id) {
  const t = TILES[id], online = S.conn === "online" && S.data;
  if (!online) return `<button class="tl" disabled data-nav="tile:${id}"><span class="tt">${icon(t.icon)}</span><span class="tn">${t.name}</span><span>--</span></button>`;
  const level = t.levels(), disabled = t.disabled?.();
  if (id === "close" && S.arm) {
    return `<button class="tl warn" data-nav="tile:${id}" aria-label="再按一次关闭${esc(level.value)}，${S.arm.left} 秒后取消"><span class="tt"><span class="num" style="font-weight:600">${S.arm.left}</span></span><span class="tn">再按一次</span><span class="tv">关闭${esc(level.value)}</span><span class="cnt" style="width:${S.arm.left / 3 * 100}%"></span></button>`;
  }
  return `<button class="tl" data-nav="tile:${id}"${disabled ? " disabled" : ""}><span class="tt">${icon(t.icon)}${dots(level)}</span><span class="tn">${t.name}</span><span class="tv num">${esc(level.value)}</span></button>`;
}

function sideHtml() {
  const sys = S.data?.system, online = S.conn === "online" && sys;
  if (S.edit) {
    return `<aside class="card side" aria-label="侧栏"><button class="sbtn on" data-nav="edit" aria-pressed="true">${icon("edit")}</button><span class="sdv"></span>
      <div class="dim" aria-hidden="true">${["brightness", "volume"].map((k, i) => `<div class="vsec"><span class="num">${sys?.[k] ?? "--"}</span><span class="vtrk"><span class="vfill" style="height:${sys?.[k] ?? 0}%"></span><span class="vth" style="bottom:${sys?.[k] ?? 0}%"></span></span><span class="vic">${icon(i ? "speaker" : "sun")}</span></div><span class="sdv"></span>`).join("")}<span class="pwr">${icon("shutdown")}</span></div></aside>`;
  }
  const slider = (key, label, value, extra) => `<div class="vsec"><span class="vval num">${value ?? "--"}</span>
    <button class="vtrk" role="slider" data-nav="slider:${key}" data-key="${key}" aria-label="${label}" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${value ?? 0}" aria-orientation="vertical"${online && value != null ? "" : " disabled"}>
      <span class="vfill" style="height:${value ?? 0}%"></span><span class="vth" style="bottom:${value ?? 0}%"></span></button>${extra}</div>`;
  const muted = sys?.muted;
  return `<aside class="card side${online ? "" : " off"}" aria-label="编辑、亮度、音量与电源">
    <button class="sbtn" data-nav="edit" aria-label="编辑磁贴"${online ? "" : " disabled"}>${icon("edit")}</button><span class="sdv"></span>
    ${slider("brightness", "亮度", sys?.brightness, `<button class="vic" disabled aria-label="自动亮度">${icon("sun")}</button>`)}
    <span class="sdv"></span>
    ${slider("volume", "音量", muted ? 0 : sys?.volume, `<button class="vic${muted ? " on" : ""}" data-nav="mute" aria-label="静音"${online ? "" : " disabled"}>${icon(muted ? "muted" : "speaker")}</button>`)}
    <span class="sdv"></span>
    <button class="pwr" data-nav="power" aria-label="电源" aria-haspopup="menu" aria-expanded="${S.menu}"${online ? "" : " disabled"}>${icon("shutdown")}</button>
  </aside>`;
}

function headerHtml() {
  const narrow = window.innerWidth < 480;
  if (S.conn !== "online" || !S.data) {
    const sub = S.conn === "offline" ? `<div class="t2">数据和设置暂不可用</div>` : "";
    const stat = name => `<div class="stat"><span class="t2">${name}</span><span class="num sv dis">--</span></div>`;
    return `<header class="card hdr"><div class="head"><div class="gname">Claw 掌机工具箱</div>${sub}</div><div class="stats">${stat("功耗")}${stat("CPU")}${stat("电量")}</div></header>`;
  }
  const p = S.data.power, live = p.live || {}, game = S.data.system.game;
  const fps = live.fps != null ? `<div class="num fps">${Math.round(live.fps)}<small>真实帧率</small></div>` : `<div class="t2">${game ? "等待帧率数据" : "未在游戏中"}</div>`;
  const value = (v, unit, digits = 0) => v == null ? `<span class="num sv dis">--</span>` : `<span class="num sv">${digits ? v.toFixed(digits) : Math.round(v)}<small>${unit}</small></span>`;
  const head = narrow ? `<div class="head"><div class="gname">${esc(game?.title ?? "Claw 掌机工具箱")}</div>${fps}</div>` : `<div class="head" style="min-width:0"><div class="gname">${esc(game?.title ?? "Claw 掌机工具箱")}</div>${fps}</div>`;
  return `<header class="card hdr">${head}<div class="stats">
    <div class="stat"><span class="t2">功耗</span>${value(p.actual?.package, "W", 1)}</div>
    <div class="stat"><span class="t2">CPU</span>${value(live.cpuTemp, "°C")}</div>
    <div class="stat"><span class="t2">电量</span>${value(live.battery, S.data.system.charging ? "% ⚡" : "%")}</div></div></header>`;
}

function bannersHtml() {
  let html = "";
  if (S.conn === "offline") html += `<div class="ib err" role="alert"><span class="sev" aria-hidden="true">${icon("minus").replace('class="ic"', 'class="ic" style="width:12px;height:12px;stroke-width:2.5"')}</span><div><strong style="font-weight:600;margin-right:8px">未连接</strong>工具箱后台没有运行</div><button class="accb" data-nav="reconnect">重新连接</button></div>`;
  if (S.conn === "online" && S.data?.power.otherTool && S.draft?.enabled) html += `<div class="ib" role="status"><span class="sev" aria-hidden="true">!</span><div><strong style="font-weight:600;margin-right:8px">功耗冲突</strong>体感助手也在设置功耗，两边会互相改写</div></div>`;
  if (S.message) html += `<div class="ib" role="status"><span class="sev" aria-hidden="true">!</span><div>${esc(S.message)}</div></div>`;
  return html;
}

function menuHtml() {
  if (!S.menu) return "";
  const item = (action, name, label) => `<button class="mi" role="menuitem" data-nav="pa:${action}">${icon(name)}${label}</button>`;
  return `<div class="smoke" data-nav-none></div><div class="menu" role="menu" aria-label="电源">${item("sleep", "sleep", "睡眠")}${item("shutdown", "shutdown", "关机")}${item("restart", "restart", "重启")}<div class="msep"></div>${item("lock", "lock", "锁定")}</div>`;
}

function editHtml() {
  const card = (id, pinned) => {
    const t = TILES[id], moving = S.moving === id;
    const level = S.data ? t.levels() : { value: "" };
    return `<button class="tl${pinned ? (moving ? " mv" : "") : " ghost"}" data-nav="${pinned ? "pin" : "add"}:${id}" aria-label="${pinned ? (moving ? "正在移动 " : "取消固定 ") : "添加 "}${t.name}">
      <span class="bdg" aria-hidden="true">${icon(pinned ? (moving ? "move" : "minus") : "plus")}</span>${icon(t.icon)}<span class="tn">${t.name}</span><span${pinned ? ' class="tv num"' : ""}>${esc(pinned ? level.value : (t.hint || ""))}</span></button>`;
  };
  const available = [...DEFAULT_TILES, ...POOL].filter(id => !tiles.includes(id));
  return `<div class="right edit"><div class="top"><div><div class="ttl">编辑快捷磁贴</div><div class="t2">X 拿起 · 方向键移动 · X 放下</div></div><button class="accb" data-nav="done">完成</button></div>
    <div class="h2">已固定<span>${tiles.length} 个</span></div><section class="grid" aria-label="已固定的磁贴">${tiles.map(id => card(id, true)).join("")}</section>
    ${available.length ? `<div class="h2">可添加<span>按 A 固定</span></div><section class="grid" aria-label="可添加的磁贴">${available.map(id => card(id, false)).join("")}</section>` : ""}</div>`;
}

function render() {
  root.className = "r";
  root.innerHTML = sideHtml() + (S.edit ? editHtml()
    : `<div class="right">${S.conn === "offline" ? bannersHtml() + headerHtml() : headerHtml() + bannersHtml()}<section class="grid" aria-label="快捷磁贴">${tiles.map(tileHtml).join("")}</section></div>`) + menuHtml();
  restoreFocus();
  reportBack();
}

// ---- input: touch, keyboard and controller ----------------------------------------------------
function focusables() {
  const scope = S.menu ? root.querySelector(".menu") : root;
  return [...scope.querySelectorAll("[data-nav]")].filter(el => !el.disabled && el.offsetParent !== null && !el.hasAttribute("data-nav-none"));
}

function setFocus(el) {
  root.querySelectorAll(".f").forEach(e => e.classList.remove("f"));
  if (!el) return;
  S.focus = el.dataset.nav;
  el.classList.add("f");
  el.scrollIntoView({ block: "nearest" });
}

function restoreFocus() {
  const list = focusables();
  const el = list.find(e => e.dataset.nav === S.focus) || (S.menu ? list[0] : list.find(e => e.dataset.nav.startsWith("tile:") || e.dataset.nav.startsWith("pin:")) || list[0]);
  if (el) setFocus(el);
}

function current() { return root.querySelector(".f") || null; }

// Spatial navigation: nearest focusable whose centre lies in the pressed direction.
function move(dx, dy) {
  const from = current(), list = focusables();
  if (!from) return setFocus(list[0]);
  const a = from.getBoundingClientRect(), ax = a.left + a.width / 2, ay = a.top + a.height / 2;
  let best = null, score = Infinity;
  for (const el of list) {
    if (el === from) continue;
    const b = el.getBoundingClientRect(), bx = b.left + b.width / 2, by = b.top + b.height / 2;
    const along = dx ? (bx - ax) * dx : (by - ay) * dy, across = dx ? Math.abs(by - ay) : Math.abs(bx - ax);
    if (along <= 4) continue;
    const s = along + across * 2.5;
    if (s < score) { score = s; best = el; }
  }
  if (best) setFocus(best);
}

function moveTile(dx, dy) {
  const i = tiles.indexOf(S.moving), cols = window.innerWidth < 480 ? 3 : 3;
  const j = Math.max(0, Math.min(tiles.length - 1, i + dx + dy * cols));
  if (j === i) return;
  tiles.splice(j, 0, tiles.splice(i, 1)[0]);
  save("claw.tiles", tiles);
  render();
}

function adjustSlider(key, delta) {
  const sys = S.data?.system;
  if (!sys) return;
  const base = key === "volume" && sys.muted ? 0 : sys[key] ?? 0;
  const value = Math.max(0, Math.min(100, base + delta));
  hold(`system.${key}`, value, 0, key);
  if (key === "volume") hold("system.muted", false, 0, key);
  render();
  send(key, key === "volume" ? "set_volume" : "set_brightness", value);
}

function activate(el) {
  const nav = el?.dataset.nav;
  if (!nav) return;
  const [kind, id] = nav.split(":");
  if (kind === "tile") { TILES[id].press(); return render(); }
  if (kind === "edit") { S.edit = !S.edit; S.moving = null; S.focus = S.edit ? null : "edit"; return render(); }
  if (kind === "done") { S.edit = false; S.moving = null; S.focus = "edit"; return render(); }
  if (kind === "pin") { if (S.moving) { S.moving = null; return render(); } tiles = tiles.filter(t => t !== id); save("claw.tiles", tiles); return render(); }
  if (kind === "add") { tiles.push(id); save("claw.tiles", tiles); S.focus = `pin:${id}`; return render(); }
  if (kind === "mute") { const v = !S.data.system.muted; hold("system.muted", v, 0, "mute"); render(); return send("mute", "set_mute", v); }
  if (kind === "power") { S.menu = true; S.focus = null; return render(); }
  if (kind === "pa") { S.menu = false; S.focus = "power"; render(); return send("power_action", "power_action", id); }
  if (kind === "reconnect") { S.failedSince = 0; return connect(); }
}

function back() {
  if (S.menu) { S.menu = false; S.focus = "power"; return render(); }
  if (S.moving) { S.moving = null; return render(); }
  if (S.edit) { S.edit = false; S.focus = "edit"; return render(); }
  if (S.arm) return clearArm();
}

function pick() {
  const nav = current()?.dataset.nav || "";
  if (!S.edit || !nav.startsWith("pin:")) return;
  S.moving = S.moving ? null : nav.slice(4);
  render();
}

function direction(dx, dy) {
  const nav = current()?.dataset.nav || "";
  if (S.moving) return moveTile(dx, dy);
  if (nav.startsWith("slider:") && dy) return adjustSlider(nav.slice(7), -dy * 5);
  move(dx, dy);
}

document.addEventListener("keydown", event => {
  if (event.repeat && !event.key.startsWith("Arrow") && !event.key.startsWith("GamepadDPad")) return;
  const keys = { ArrowUp: [0, -1], ArrowDown: [0, 1], ArrowLeft: [-1, 0], ArrowRight: [1, 0],
    GamepadDPadUp: [0, -1], GamepadDPadDown: [0, 1], GamepadDPadLeft: [-1, 0], GamepadDPadRight: [1, 0] };
  if (keys[event.key]) { direction(...keys[event.key]); event.preventDefault(); return; }
  if (event.key === "Enter" || event.key === " " || event.key === "GamepadA") { activate(current()); event.preventDefault(); }
  else if (event.key === "Escape" || event.key === "GamepadB") { back(); event.preventDefault(); }
  else if (event.key === "x" || event.key === "GamepadX") { pick(); event.preventDefault(); }
});

root.addEventListener("click", event => {
  const el = event.target.closest("[data-nav]");
  if (event.target.classList.contains("smoke")) return back();
  if (!el || el.disabled || el.dataset.nav.startsWith("slider:")) return;
  setFocus(el);
  activate(el);
});

// Touch / mouse drag on the vertical sliders.
root.addEventListener("pointerdown", event => {
  const track = event.target.closest(".vtrk[data-key]");
  if (!track || track.disabled) return;
  const key = track.dataset.key;
  const apply = e => {
    // Look the track up again: a refresh may have redrawn it while the finger moves.
    const now = root.querySelector(`.vtrk[data-key="${key}"]`) || track;
    const r = now.getBoundingClientRect(), value = Math.round(Math.max(0, Math.min(1, (r.bottom - 9 - e.clientY) / (r.height - 18))) * 100);
    adjustSlider(key, value - (key === "volume" && S.data.system.muted ? 0 : S.data.system[key] ?? 0));
  };
  apply(event);
  const up = () => { window.removeEventListener("pointermove", apply); window.removeEventListener("pointerup", up); };
  window.addEventListener("pointermove", apply);
  window.addEventListener("pointerup", up);
});

// Controller through the Gamepad API (Game Bar hands it to the focused widget).
// Only while this widget has focus, and only after every button has been let go once since it got
// focus: the A press that picks this widget in Game Bar (or anything pressed while another widget
// or the game had focus) must not reach a tile. That stray A was opening the touch keyboard.
const pad = { last: {}, repeatAt: 0, armed: false };
window.addEventListener("blur", () => { pad.armed = false; });
window.addEventListener("focus", () => { pad.armed = false; });
function pollPad(now) {
  const gp = [...(navigator.getGamepads?.() || [])].find(Boolean);
  if (gp && !document.hasFocus()) pad.armed = false;
  else if (gp) {
    const pressed = {
      a: gp.buttons[0]?.pressed, b: gp.buttons[1]?.pressed, x: gp.buttons[2]?.pressed,
      up: gp.buttons[12]?.pressed || gp.axes[1] < -0.6, down: gp.buttons[13]?.pressed || gp.axes[1] > 0.6,
      left: gp.buttons[14]?.pressed || gp.axes[0] < -0.6, right: gp.buttons[15]?.pressed || gp.axes[0] > 0.6,
    };
    if (!pad.armed) {
      pad.armed = !Object.values(pressed).some(Boolean) && !gp.buttons.some(b => b.pressed);
      pad.last = pressed;
      requestAnimationFrame(pollPad);
      return;
    }
    const edge = k => pressed[k] && !pad.last[k];
    if (edge("a")) activate(current());
    if (edge("b")) back();
    if (edge("x")) pick();
    const dirs = { up: [0, -1], down: [0, 1], left: [-1, 0], right: [1, 0] };
    for (const [k, d] of Object.entries(dirs)) {
      if (edge(k)) { direction(...d); pad.repeatAt = now + 400; }
      else if (pressed[k] && now >= pad.repeatAt) { direction(...d); pad.repeatAt = now + 110; }
    }
    pad.last = pressed;
  }
  requestAnimationFrame(pollPad);
}
requestAnimationFrame(pollPad);

// Game Bar tells the host when it opens; poll the agent only while the widget can be seen.
// In Game Bar the host owns the controller (controller focus inside the WebView2 makes Windows
// show the touch keyboard) and forwards each press as {"nav": ...}.
const NAV = { up: [0, -1], down: [0, 1], left: [-1, 0], right: [1, 0] };
window.chrome?.webview?.addEventListener("message", event => {
  const data = event.data || {};
  if (data.gamebar === "foreground") refresh();
  if (NAV[data.nav]) direction(...NAV[data.nav]);
  else if (data.nav === "a") activate(current());
  else if (data.nav === "b") back();
  else if (data.nav === "x") pick();
});
let canBack = null;
function reportBack() {
  const now = !!(S.menu || S.edit || S.moving || S.arm);
  if (now !== canBack) { canBack = now; window.chrome?.webview?.postMessage({ canBack: now }); }
}
document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
setInterval(() => { if (!document.hidden && agent.ready && !S.saveTimer) refresh(); }, 1500);
setInterval(() => { if (!agent.ready) connect(); }, 2000);
window.addEventListener("resize", render);

render();
connect();
