// Claw 界面声音：一套签名音色，22 个事件，全部原创、确定性生成，48 kHz / 24 位立体声 WAV，不依赖第三方。
//
//   node tools/Compose-UiSounds.mjs [输出目录]     默认写到 frontend/library-native/ClawLibrary/Assets/Audio/Sfx/claw
//
// 声音只用三种材料，每种材料只代表一类意思，听几次就记住：
//   触 —— 干、短、有质感的"嗒"（一下瞬态 + 一个高 Q 共鸣），只用在"移动"：导航、页签、滑块、打字、到头、快门。
//   光 —— 纯净的 FM 音，会滑音：往上滑 = 确认 / 打开 / 开，往下滑 = 返回 / 关闭 / 关。只用在"状态变了"。
//   气 —— 带共振峰的气流，带声相：页面浮上来是吸气往上，退下去是呼气往下；控制中心从左边出入。只用在"空间变了"。
// 另有两种只给结果用的专属音色：玻璃铃（提示 / 成功）和闷的嗡声（出错），整套里不会在别处出现。
// 调式：D 利底亚（D E F# G# A C#），明亮但不俗套；主音 D6 = 1175 Hz，正好落在人耳最敏感、掌机喇叭也放得出的区间。
//
// 工程：起音升余弦、尾巴收到 0；150 Hz 以下切掉（小喇叭放不出，只会让功放提前削波）；K 计权响度六档、峰值 ≤ −1 dBFS；
// 小板混响 + 窄立体声，合成单声道不变色；TPDF 抖动到 24 位；48 kHz 与 Windows 默认混音格式一致，加载不重采样。
// 空间位置和数值音高由播放端决定：导航按焦点在屏幕上的横向位置摆声相，滑块按数值在区间里的位置变调。
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const RATE = 48000;
const here = dirname(fileURLToPath(import.meta.url));
const OUT = process.argv[2] ?? join(here, '..', 'frontend', 'library-native', 'ClawLibrary', 'Assets', 'Audio', 'Sfx', 'claw');
const D6 = 1174.66;
// D 利底亚的音级（相对 D6 的半音）：D 0、E 2、F# 4、G# 6、A 7、C# 11
const st = n => D6 * 2 ** (n / 12);

// ---------------- 基础 ----------------
let seed = 1;
const rand = () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 4294967296; };
const noise = () => rand() * 2 - 1;
const panGains = p => [Math.cos((p + 1) * Math.PI / 4), Math.sin((p + 1) * Math.PI / 4)];

function biquad(type, f, q = 0.707, gainDb = 0) {
  const w = 2 * Math.PI * f / RATE, c = Math.cos(w), s = Math.sin(w), a = s / (2 * q), A = 10 ** (gainDb / 40);
  let b0, b1, b2, a0, a1, a2;
  if (type === 'lp') { b0 = (1 - c) / 2; b1 = 1 - c; b2 = b0; a0 = 1 + a; a1 = -2 * c; a2 = 1 - a; }
  else if (type === 'hp') { b0 = (1 + c) / 2; b1 = -(1 + c); b2 = b0; a0 = 1 + a; a1 = -2 * c; a2 = 1 - a; }
  else if (type === 'bp') { b0 = a; b1 = 0; b2 = -a; a0 = 1 + a; a1 = -2 * c; a2 = 1 - a; }
  else if (type === 'hs') { const r = 2 * Math.sqrt(A) * a; b0 = A * ((A + 1) + (A - 1) * c + r); b1 = -2 * A * ((A - 1) + (A + 1) * c); b2 = A * ((A + 1) + (A - 1) * c - r); a0 = (A + 1) - (A - 1) * c + r; a1 = 2 * ((A - 1) - (A + 1) * c); a2 = (A + 1) - (A - 1) * c - r; }
  else throw new Error(type);
  const k = [b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0];
  let x1 = 0, x2 = 0, y1 = 0, y2 = 0;
  return x => { const y = k[0] * x + k[1] * x1 + k[2] * x2 - k[3] * y1 - k[4] * y2; x2 = x1; x1 = x; y2 = y1; y1 = y; return y; };
}
const filter = (ch, ...fs) => { for (const f of fs) for (let i = 0; i < ch.length; i++) ch[i] = f(ch[i]); };
const smooth = x => x * x * (3 - 2 * x);

function add(out, start, i, v, pan) {
  const j = start + i;
  if (j < 0 || j >= out[0].length) return;
  const [l, r] = panGains(pan);
  out[0][j] += v * l; out[1][j] += v * r;
}

// ---------------- 材料 ----------------

/** 触：1–2 ms 的瞬态激励一个高 Q 共鸣（像高级按键的"嗒"），外加一点点同频正弦给音高感。 */
function tock(out, at, gain, { ring = 2200, q = 14, decay = 0.018, body = 0.25, pan = 0, bend = 0 } = {}) {
  const s = Math.round(at * RATE), n = Math.round((decay * 7 + 0.004) * RATE);
  // 共鸣的衰减时间 τ = Q / (π f)：按想要的 decay 算 Q；q 只是下限（让很短的触也有一点音色）
  const rq = Math.max(q, decay * Math.PI * ring);
  const res = biquad('bp', ring, rq), air = biquad('hp', 3500, 0.7), resGain = 6 * Math.sqrt(rq / 14);
  let ph = 0;
  for (let i = 0; i < n; i++) {
    const t = i / RATE;
    const ex = t < 0.0015 ? noise() * (1 - t / 0.0015) : 0;
    const f = ring * 2 ** (bend * Math.min(1, t / 0.03) / 12);
    ph += 2 * Math.PI * f / RATE;
    const e = Math.exp(-t / decay);
    const v = res(ex) * resGain + air(ex) * 0.25 + Math.sin(ph) * body * e * (t < 0.001 ? t / 0.001 : 1);
    add(out, s, i, v * gain, pan);
  }
}

/**
 * 光：FM 音（载波 + 一个调制器，调制指数随时间收小 —— 起音亮、尾巴纯），频率从 f0 滑到 f1。
 * 滑音曲线是缓入缓出的 S 形，听起来像"浮上去 / 落下来"而不是警笛。
 */
function light(out, at, len, f0, f1, gain, { glide = 0.06, attack = 0.004, decay = 0.25, ratio = 2, index = 1.2, indexDecay = 0.05, pan = 0, vibrato = 0, partial2 = 0.12 } = {}) {
  const s = Math.round(at * RATE), n = Math.round(len * RATE);
  let pc = 0, pm = 0, p2 = 0;
  for (let i = 0; i < n; i++) {
    const t = i / RATE;
    const g = Math.min(1, t / glide);
    const f = f0 * (f1 / f0) ** smooth(g) * (1 + vibrato * Math.sin(2 * Math.PI * 5.5 * t) * Math.min(1, t / 0.15));
    pm += 2 * Math.PI * f * ratio / RATE;
    const idx = index * Math.exp(-t / indexDecay);
    pc += 2 * Math.PI * f / RATE + 0; p2 += 2 * Math.PI * f * 2 / RATE;
    const a = t < attack ? 0.5 - 0.5 * Math.cos(Math.PI * t / attack) : 1;
    const tail = len - t < 0.01 ? (len - t) / 0.01 : 1;
    const e = a * Math.exp(-Math.max(0, t - attack) / decay) * tail;
    const v = (Math.sin(pc + idx * Math.sin(pm)) + partial2 * Math.sin(p2)) * e;
    add(out, s, i, v * gain, pan);
  }
}

/** 气：噪声过两个共振峰（像元音），共振峰频率从 from 滑到 to，声相从 panFrom 走到 panTo。 */
function air(out, at, len, from, to, gain, { panFrom = 0, panTo = 0, shape = 1.4 } = {}) {
  const s = Math.round(at * RATE), n = Math.round(len * RATE);
  const st1 = { z1: 0, z2: 0 }, st2 = { z1: 0, z2: 0 };
  const svf = (state, x, f, q) => {
    const w = 2 * Math.PI * Math.min(f, 18000) / RATE, a = Math.sin(w) / (2 * q), c = Math.cos(w);
    const y = (a * x + 2 * c * state.z1 - (1 - a) * state.z2) / (1 + a);
    state.z2 = state.z1; state.z1 = y; return y;
  };
  for (let i = 0; i < n; i++) {
    const p = i / n, f = from * (to / from) ** smooth(p);
    const x = noise();
    const y = svf(st1, x, f, 3) + 0.6 * svf(st2, x, f * 2.3, 4);
    const e = Math.sin(Math.PI * p) ** shape;
    add(out, s, i, y * e * gain * 2.4, panFrom + (panTo - panFrom) * smooth(p));
  }
}

/** 玻璃铃：非整数泛音（1 : 2.76 : 5.40 : 8.93）+ 轻微拍频，只给提示和成功用。 */
function bell(out, at, f, gain, { decay = 0.5, pan = 0 } = {}) {
  const s = Math.round(at * RATE), len = decay * 5, n = Math.round(len * RATE);
  const parts = [[1, 1, 1, 0.0015], [2.76, 0.32, 0.5, 0], [5.40, 0.14, 0.3, 0], [8.93, 0.05, 0.18, 0]];
  const ph = parts.map(() => [rand(), rand()]);
  for (let i = 0; i < n; i++) {
    const t = i / RATE, a = t < 0.0015 ? t / 0.0015 : 1, tail = len - t < 0.01 ? (len - t) / 0.01 : 1;
    let v = 0;
    parts.forEach(([r, amp, dk, det], k) => {
      ph[k][0] += 2 * Math.PI * f * r / RATE; ph[k][1] += 2 * Math.PI * f * r * (1 + det) / RATE;
      v += (det ? (Math.sin(ph[k][0]) + Math.sin(ph[k][1])) / 2 : Math.sin(ph[k][0])) * amp * Math.exp(-t / (decay * dk));
    });
    add(out, s, i, v * a * tail * gain, pan);
  }
}

/** 嗡：带 32 Hz 粗糙度的闷音（锯齿过低通），短、往下沉，只给出错用。 */
function buzz(out, at, len, f0, f1, gain, { pan = 0 } = {}) {
  const s = Math.round(at * RATE), n = Math.round(len * RATE), lp = biquad('lp', 1400, 0.9), lp2 = biquad('lp', 1400, 0.9);
  let ph = 0;
  for (let i = 0; i < n; i++) {
    const t = i / RATE, p = i / n, f = f0 * (f1 / f0) ** p;
    ph = (ph + f / RATE) % 1;
    const saw = 2 * ph - 1;
    const rough = 0.75 + 0.25 * Math.sin(2 * Math.PI * 32 * t);
    const e = (t < 0.004 ? t / 0.004 : 1) * Math.exp(-t / (len * 0.45)) * (len - t < 0.012 ? (len - t) / 0.012 : 1);
    add(out, s, i, lp2(lp(saw)) * rough * e * gain, pan);
  }
}

/** 小板混响：8 梳 + 4 全通，左右错开；只加一点湿声。 */
function plate(out, wet, size = 0.45, damp = 0.35) {
  const combs = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617].map(d => Math.round(d * RATE / 44100 * (0.5 + size * 0.5)));
  const aps = [556, 441, 341, 225].map(d => Math.round(d * RATE / 44100));
  for (let ch = 0; ch < 2; ch++) {
    const x = out[ch], spread = ch ? 23 : 0, y = new Float32Array(x.length), pre = biquad('hp', 400, 0.7);
    const src = x.map(v => pre(v));
    for (const d0 of combs) {
      const d = d0 + spread, line = new Float32Array(d); let idx = 0, store = 0;
      for (let i = 0; i < x.length; i++) { const o = line[idx]; store = o * (1 - damp) + store * damp; line[idx] = src[i] * 0.015 + store * (0.7 + size * 0.12); idx = (idx + 1) % d; y[i] += o; }
    }
    for (const d0 of aps) {
      const d = d0 + spread, line = new Float32Array(d); let idx = 0;
      for (let i = 0; i < y.length; i++) { const b = line[idx]; const o = -y[i] + b; line[idx] = y[i] + b * 0.5; idx = (idx + 1) % d; y[i] = o; }
    }
    for (let i = 0; i < x.length; i++) x[i] += y[i] * wet;
  }
}

// ---------------- 响度（BS.1770 K 计权，400 ms 窗口的最大瞬时响度） ----------------
function loudness(out) {
  const win = Math.round(0.4 * RATE), hop = Math.round(0.05 * RATE);
  const kw = out.map(ch => { const c = Float32Array.from(ch); filter(c, biquad('hs', 1681.97, 0.7072, 3.9998), biquad('hp', 38.13, 0.5003)); return c; });
  const len = kw[0].length, sq = new Float64Array(len + 1);
  for (let i = 0; i < len; i++) sq[i + 1] = sq[i] + kw[0][i] ** 2 + kw[1][i] ** 2;
  let best = -Infinity;
  for (let s = 0; s < Math.max(1, len - win + 1); s += hop) best = Math.max(best, -0.691 + 10 * Math.log10((sq[Math.min(len, s + win)] - sq[s]) / win + 1e-20));
  return best;
}
const peak = out => Math.max(...out.map(ch => ch.reduce((m, v) => Math.max(m, Math.abs(v)), 0)));

// 响度档：打字 < 移动 < 次要 < 状态变化 < 结果 < 时刻
const LEVEL = { tick: -38, move: -34, minor: -28, state: -24, result: -21.5, moment: -18 };

// ---------------- 事件：[长度秒, 响度档, 混响, 画法] ----------------
const CUES = {
  // —— 移动（触）——
  nav: [0.12, 'move', 0.04, o => tock(o, 0, 1, { ring: 2350, decay: 0.014, body: 0.3 })],
  tab: [0.16, 'minor', 0.05, o => { tock(o, 0, 0.8, { ring: 2000 }); tock(o, 0.022, 1, { ring: 2640 }); }],
  slider_up: [0.10, 'move', 0.03, o => tock(o, 0, 1, { ring: 3100, q: 18, decay: 0.012, body: 0.45 })],
  slider_down: [0.10, 'move', 0.03, o => tock(o, 0, 1, { ring: 2350, q: 18, decay: 0.012, body: 0.45 })],
  typing: [0.07, 'tick', 0.02, o => tock(o, 0, 1, { ring: 1700, q: 8, decay: 0.008, body: 0.1 })],
  // 到头：低、钝、往下一沉 —— 没有"光"，因为什么都没发生
  bump: [0.18, 'minor', 0.03, o => { tock(o, 0, 1, { ring: 720, q: 6, decay: 0.03, body: 0.6, bend: -3 }); tock(o, 0.004, 0.25, { ring: 1500, q: 4, decay: 0.01, body: 0 }); }],
  // 快门：两下锐利的"触"（帘幕开、合）+ 一口短气
  shutter: [0.26, 'state', 0.06, o => {
    tock(o, 0, 1, { ring: 3300, q: 5, decay: 0.01, body: 0, pan: -0.15 }); air(o, 0.002, 0.05, 6000, 3000, 0.25);
    tock(o, 0.075, 0.85, { ring: 2600, q: 5, decay: 0.012, body: 0, pan: 0.15 }); air(o, 0.077, 0.05, 5000, 2500, 0.2);
  }],

  // —— 状态变化（触 + 光）——
  // 确认：按下去（触），然后亮起来（光从 A5 滑到 D6）
  activate: [0.38, 'state', 0.10, o => { tock(o, 0, 0.55, { ring: 2350 }); light(o, 0.008, 0.36, st(-5), st(0), 0.9, { glide: 0.05, decay: 0.12 }); }],
  // 返回：一口短的呼气，光从 D6 落回 A5，更轻
  back: [0.34, 'minor', 0.08, o => { air(o, 0, 0.09, 3800, 1800, 0.35); light(o, 0.012, 0.32, st(0), st(-5), 0.85, { glide: 0.06, decay: 0.1, index: 0.8 }); }],
  // 开：触 + 往上滑一个大三度、末尾一下"卡住"的高触；关：往下滑、尾巴变暗
  toggle_on: [0.30, 'state', 0.07, o => { tock(o, 0, 0.6, { ring: 2000 }); light(o, 0.006, 0.26, st(0), st(4), 0.8, { glide: 0.045, decay: 0.08 }); tock(o, 0.05, 0.45, { ring: 3300, q: 20, decay: 0.008 }); }],
  toggle_off: [0.30, 'minor', 0.06, o => { tock(o, 0, 0.6, { ring: 1700 }); light(o, 0.006, 0.26, st(4), st(0), 0.75, { glide: 0.05, decay: 0.07, index: 0.5 }); }],

  // —— 空间变化（气 + 光）——
  // 进详情：吸气往上，光浮上去一个四度
  detail_in: [0.46, 'state', 0.14, o => { air(o, 0, 0.3, 900, 4200, 0.4); light(o, 0.11, 0.34, st(-5), st(0), 0.75, { glide: 0.09, decay: 0.12 }); }],
  detail_out: [0.42, 'minor', 0.12, o => { air(o, 0, 0.28, 4200, 900, 0.38); light(o, 0.09, 0.3, st(0), st(-5), 0.65, { glide: 0.09, decay: 0.1, index: 0.7 }); }],
  // 控制中心：从左边浮进来 / 退回左边
  menu_in: [0.46, 'state', 0.14, o => { air(o, 0, 0.3, 1100, 4600, 0.42, { panFrom: -0.9, panTo: -0.25 }); light(o, 0.12, 0.32, st(2), st(7), 0.7, { glide: 0.08, decay: 0.11, pan: -0.3 }); }],
  menu_out: [0.42, 'minor', 0.12, o => { air(o, 0, 0.28, 4600, 1100, 0.4, { panFrom: -0.25, panTo: -0.9 }); light(o, 0.08, 0.28, st(7), st(2), 0.6, { glide: 0.08, decay: 0.09, pan: -0.5, index: 0.7 }); }],
  // 弹层：短气 + 一对五度光一起浮起 / 落下
  modal_show: [0.42, 'state', 0.14, o => { air(o, 0, 0.18, 1500, 4000, 0.3); light(o, 0.06, 0.36, st(-7), st(0), 0.55, { glide: 0.07, decay: 0.14, pan: -0.2 }); light(o, 0.06, 0.36, st(0), st(7), 0.5, { glide: 0.07, decay: 0.14, pan: 0.2 }); }],
  modal_hide: [0.36, 'minor', 0.1, o => { air(o, 0, 0.16, 4000, 1500, 0.28); light(o, 0.04, 0.3, st(0), st(-7), 0.5, { glide: 0.07, decay: 0.1, pan: -0.2, index: 0.7 }); light(o, 0.04, 0.3, st(7), st(0), 0.45, { glide: 0.07, decay: 0.1, pan: 0.2, index: 0.7 }); }],

  // —— 结果（铃 / 嗡）——
  // 提示：两下玻璃铃（A6 → D7），余音长但很轻，不打断操作
  toast: [1.1, 'result', 0.16, o => { bell(o, 0, st(7), 0.8, { decay: 0.32, pan: -0.1 }); bell(o, 0.11, st(12), 0.75, { decay: 0.4, pan: 0.1 }); }],
  // 成功：利底亚三音铃 D – F# – A 往上 + 光的余晖
  positive: [1.2, 'result', 0.18, o => {
    [0, 4, 7].forEach((n, i) => bell(o, i * 0.07, st(n), 0.7 + i * 0.08, { decay: 0.35 + i * 0.06, pan: (i - 1) * 0.2 }));
    light(o, 0.14, 0.9, st(12), st(12), 0.25, { decay: 0.4, index: 0.4, vibrato: 0.003 });
  }],
  // 出错：两下闷嗡往下沉（A4 → F4），粗糙但不刺耳
  error: [0.44, 'result', 0.06, o => { buzz(o, 0, 0.13, 440, 400, 1); buzz(o, 0.16, 0.18, 415, 349, 1); }],

  // —— 时刻（气 + 光的签名动机 D–A–E）——
  // 启动游戏：长吸气往上推，签名动机三个光音依次浮起，最后一声高八度的光收尾
  launch: [1.7, 'moment', 0.2, o => {
    air(o, 0, 0.8, 500, 6500, 0.35);
    [[0, -12], [7, -5], [14, 2]].forEach(([n, from], i) => light(o, 0.12 + i * 0.12, 1.3 - i * 0.12, st(from), st(n), 0.55, { glide: 0.12, decay: 0.45, index: 0.9, pan: (i - 1) * 0.3 }));
    bell(o, 0.5, st(19), 0.35, { decay: 0.45 });
  }],
  // 开机：同一个动机放慢、铺开，像屏幕亮起来
  startup: [2.3, 'moment', 0.22, o => {
    air(o, 0, 1.2, 300, 5000, 0.28, { shape: 2 });
    [[0, -17], [7, -12], [14, -5], [19, 0]].forEach(([n, from], i) => light(o, 0.2 + i * 0.22, 2 - i * 0.2, st(from), st(n - 12), 0.55, { glide: 0.2, decay: 0.7, index: 0.8, pan: (i - 1.5) * 0.25, vibrato: 0.002 }));
    bell(o, 1.0, st(12), 0.3, { decay: 0.6 });
  }],
};

// ---------------- 输出 ----------------
function wav24(out) {
  const n = out[0].length, data = Buffer.alloc(n * 6);
  for (let i = 0; i < n; i++)
    for (let c = 0; c < 2; c++) {
      const v = Math.max(-8388607, Math.min(8388607, Math.round((out[c][i] + (rand() - rand()) / 8388608) * 8388607)));
      data.writeIntLE(v, (i * 2 + c) * 3, 3);
    }
  const h = Buffer.alloc(44);
  h.write('RIFF', 0); h.writeUInt32LE(36 + data.length, 4); h.write('WAVE', 8); h.write('fmt ', 12);
  h.writeUInt32LE(16, 16); h.writeUInt16LE(1, 20); h.writeUInt16LE(2, 22); h.writeUInt32LE(RATE, 24);
  h.writeUInt32LE(RATE * 6, 28); h.writeUInt16LE(6, 32); h.writeUInt16LE(24, 34); h.write('data', 36); h.writeUInt32LE(data.length, 40);
  return Buffer.concat([h, data]);
}

mkdirSync(OUT, { recursive: true });
const manifest = {};
for (const [cue, [seconds, level, wet, draw]] of Object.entries(CUES)) {
  seed = [...cue].reduce((h, ch) => (h * 31 + ch.charCodeAt(0)) >>> 0, 11);
  const out = [new Float32Array(Math.ceil((seconds + wet * 1.2) * RATE)), new Float32Array(Math.ceil((seconds + wet * 1.2) * RATE))];
  draw(out);
  plate(out, wet);
  for (const ch of out) filter(ch, biquad('hp', 150, 0.7), biquad('hp', 150, 0.7), biquad('lp', 17000, 0.7));
  let g = 10 ** ((LEVEL[level] - loudness(out)) / 20);
  if (peak(out) * g > 0.891) g = 0.891 / peak(out);
  for (const ch of out) for (let i = 0; i < ch.length; i++) ch[i] *= g;
  for (const ch of out) for (let i = 0; i < 480; i++) ch[ch.length - 1 - i] *= i / 480;
  writeFileSync(join(OUT, cue + '.wav'), wav24(out));
  manifest[cue] = { seconds: +(out[0].length / RATE).toFixed(3), lufsMax: +loudness(out).toFixed(1), peakDb: +(20 * Math.log10(peak(out))).toFixed(1) };
}
writeFileSync(join(OUT, 'manifest.json'), JSON.stringify({ format: '48 kHz / 24-bit stereo PCM', cues: manifest }, null, 1) + '\n');
console.log(`wrote ${Object.keys(manifest).length} cues to ${OUT}`);
