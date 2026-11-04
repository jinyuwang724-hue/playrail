// 界面音效合成（高清版）：48 kHz / 24 位立体声 WAV，三套音色 × 22 个事件，全部原创、确定性生成，不依赖第三方。
//
//   node tools/Compose-UiSounds.mjs [输出目录]     默认写到 frontend/library-native/ClawLibrary/Assets/Audio/Sfx
//
// 设计规则（每条都对应一个可听出来的效果）：
// - 一套音高语法：以 A5 (880 Hz) 为主音，往前 / 确认 / 打开 = 上行，返回 / 关闭 = 同一组音的镜像下行；
//   成功 = 大三和弦琶音，出错 = 低两个八度的小二度双击，到头 = 闷的低音加轻微下滑。听一次就能记住方向。
// - 能量放在 600 Hz–5 kHz：掌机小喇叭放不出 300 Hz 以下，低音只靠泛音"暗示"（缺失基频），所以在喇叭和耳机上听起来一样清楚；
//   8–14 kHz 只留很轻的空气感泛音，耳机上更细腻，喇叭上不刺耳。
// - 每个声音起音 1–4 ms 升余弦、尾巴按指数衰减到真正的 0，没有直流、没有爆音；最后 TPDF 抖动到 24 位。
// - 响度按 K 计权（ITU-R BS.1770）量，分档对齐：导航最轻，确认 / 开关中档，提示 / 出错 / 成功更醒目，开机最响；峰值 ≤ −1 dBFS。
// - 立体声只做很窄的空间（小房间早期反射 + 去相关），合成单声道不变色；控制中心从左边滑出，所以它的进出声相也从左到中。
// - 48 kHz 与 Windows 默认混音格式一致，引擎加载时不需要重采样。
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const RATE = 48000;
const here = dirname(fileURLToPath(import.meta.url));
const OUT = process.argv[2] ?? join(here, '..', 'frontend', 'library-native', 'ClawLibrary', 'Assets', 'Audio', 'Sfx');
const A5 = 880;
const st = n => A5 * 2 ** (n / 12);

// ---------------- 小工具 ----------------
let seed = 1;
const rand = () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 4294967296; };
const buf = sec => [new Float32Array(Math.ceil(sec * RATE)), new Float32Array(Math.ceil(sec * RATE))];

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

/** 起音升余弦 + 指数衰减，尾巴最后 8 ms 收到 0。 */
function env(t, attack, decay, length) {
  if (t < 0 || t >= length) return 0;
  const a = t < attack ? 0.5 - 0.5 * Math.cos(Math.PI * t / attack) : 1;
  const tail = length - t < 0.008 ? 0.5 - 0.5 * Math.cos(Math.PI * (length - t) / 0.008) : 1;
  return a * Math.exp(-Math.max(0, t - attack) / decay) * tail;
}

/**
 * 一个"敲击"：模态合成（若干泛音各自衰减）。timbre 决定泛音比和衰减；glide 让音高在 glideTime 内滑过 glide 个半音。
 * pan −1..1；at 起始秒。
 */
function strike(out, at, freq, gain, timbre, { pan = 0, glide = 0, glideTime = 0.06, decay = 1, length } = {}) {
  const parts = timbre.partials;
  const len = length ?? Math.min(2.5, timbre.decay * decay * 6);
  const n = Math.ceil(len * RATE), start = Math.round(at * RATE);
  const gl = Math.cos((pan + 1) * Math.PI / 4), gr = Math.sin((pan + 1) * Math.PI / 4);
  for (const [ratio, amp, dk, detune = 0] of parts) {
    const f0 = freq * ratio;
    if (f0 > 15000) continue;
    let ph = rand() * 0.2, ph2 = rand() * 0.2;
    const d = timbre.decay * decay * dk;
    for (let i = 0; i < n && start + i < out[0].length; i++) {
      const t = i / RATE;
      const bend = glide ? 2 ** (glide * Math.min(1, t / glideTime) * (2 - Math.min(1, t / glideTime)) / 12) : 1;
      const e = env(t, timbre.attack, d, len);
      if (e === 0 && t > timbre.attack) break;
      ph += 2 * Math.PI * f0 * bend / RATE;
      ph2 += 2 * Math.PI * f0 * bend * (1 + detune) / RATE;
      const v = (detune ? (Math.sin(ph) + Math.sin(ph2)) * 0.5 : Math.sin(ph)) * amp * gain * e;
      out[0][start + i] += v * gl; out[1][start + i] += v * gr;
    }
  }
  if (timbre.click) click(out, at, gain * timbre.click, timbre.clickBand, pan);
}

/** 触感：1–3 ms 带通噪声，给声音一个"按下去"的瞬间。 */
function click(out, at, gain, band = 4200, pan = 0, len = 0.004) {
  const f = biquad('bp', band, 1.4), n = Math.ceil(len * RATE), s = Math.round(at * RATE);
  const gl = Math.cos((pan + 1) * Math.PI / 4), gr = Math.sin((pan + 1) * Math.PI / 4);
  for (let i = 0; i < n + 400 && s + i < out[0].length; i++) {
    const t = i / RATE, x = i < n ? (rand() * 2 - 1) * (0.5 - 0.5 * Math.cos(2 * Math.PI * Math.min(1, t / len))) : 0;
    const v = f(x) * gain * 3;
    out[0][s + i] += v * gl; out[1][s + i] += v * gr;
  }
}

/** 空气声：带通噪声扫频（进出详情、控制中心）。from/to 频率，panFrom/panTo 声相。 */
function swish(out, at, len, from, to, gain, panFrom = 0, panTo = 0) {
  const n = Math.ceil(len * RATE), s = Math.round(at * RATE);
  let z1 = 0, z2 = 0;
  for (let i = 0; i < n && s + i < out[0].length; i++) {
    const t = i / RATE, p = t / len;
    const f = from * (to / from) ** p, w = 2 * Math.PI * f / RATE, q = 2.2, a = Math.sin(w) / (2 * q), c = Math.cos(w);
    const x = rand() * 2 - 1;
    // 状态变量带通（随频率实时改系数）
    const y = (a * x - (-2 * c) * z1 - (1 - a) * z2) / (1 + a);
    z2 = z1; z1 = y;
    const e = Math.sin(Math.PI * Math.min(1, p)) ** 1.6;
    const pan = panFrom + (panTo - panFrom) * p;
    const v = y * gain * e * 2.2;
    out[0][s + i] += v * Math.cos((pan + 1) * Math.PI / 4); out[1][s + i] += v * Math.sin((pan + 1) * Math.PI / 4);
  }
}

/** 小房间：8 条梳状 + 4 个全通（Freeverb 结构，左右错开），只加一点点湿声。 */
function room(out, wet, size = 0.5, damp = 0.4) {
  if (wet <= 0) return;
  const combs = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617].map(d => Math.round(d * RATE / 44100 * (0.55 + size * 0.45)));
  const aps = [556, 441, 341, 225].map(d => Math.round(d * RATE / 44100));
  for (let ch = 0; ch < 2; ch++) {
    const x = out[ch], spread = ch ? 23 : 0, y = new Float32Array(x.length);
    for (const d0 of combs) {
      const d = d0 + spread, line = new Float32Array(d); let idx = 0, store = 0;
      const fb = 0.70 + size * 0.12;
      for (let i = 0; i < x.length; i++) { const o = line[idx]; store = o * (1 - damp) + store * damp; line[idx] = x[i] * 0.015 + store * fb; idx = (idx + 1) % d; y[i] += o; }
    }
    for (const d0 of aps) {
      const d = d0 + spread, line = new Float32Array(d); let idx = 0;
      for (let i = 0; i < y.length; i++) { const b = line[idx]; const o = -y[i] + b; line[idx] = y[i] + b * 0.5; idx = (idx + 1) % d; y[i] = o; }
    }
    for (let i = 0; i < x.length; i++) x[i] += y[i] * wet;
  }
}

// ---------------- 响度（BS.1770 K 计权，取 400 ms 窗口里的最大瞬时响度） ----------------
function loudness(out) {
  // 两级 K 计权：高架 +4 dB @1.5 kHz、高通 38 Hz（按 48 kHz 系数）
  let sum = 0, best = -Infinity;
  const win = Math.round(0.4 * RATE), hop = Math.round(0.05 * RATE);
  const kw = out.map(ch => { const c = Float32Array.from(ch); filter(c, biquad('hs', 1681.97, 0.7072, 3.9998), biquad('hp', 38.13, 0.5003)); return c; });
  const len = kw[0].length;
  const sq = new Float64Array(len + 1);
  for (let i = 0; i < len; i++) sq[i + 1] = sq[i] + kw[0][i] ** 2 + kw[1][i] ** 2;
  for (let s = 0; s < Math.max(1, len - win + 1); s += hop) {
    const e = Math.min(len, s + win);
    const ms = (sq[e] - sq[s]) / win;
    best = Math.max(best, -0.691 + 10 * Math.log10(ms + 1e-20));
  }
  return best;
}
const peak = out => Math.max(...out.map(ch => ch.reduce((m, v) => Math.max(m, Math.abs(v)), 0)));

// ---------------- 音色 ----------------
// partials: [泛音比, 振幅, 衰减系数, 双振子失谐]
const TIMBRES = {
  // 轻触：毡头马林巴 —— 调过音的木条（1 : 3.93 : 9.0）、起音软、高泛音收得快，暖而不闷
  soft: {
    name: '轻触', attack: 0.0025, decay: 0.09, click: 0.10, clickBand: 2600, wet: 0.10, size: 0.35, damp: 0.55,
    partials: [[1, 1, 1], [2, 0.18, 0.6], [3.93, 0.32, 0.38], [9.0, 0.07, 0.16], [0.5, 0.20, 0.9]],
  },
  // 清透：玻璃 / 钟片 —— 非整数泛音 + 轻微拍频，余韵长、明亮
  crystal: {
    name: '清透', attack: 0.0012, decay: 0.16, click: 0.05, clickBand: 6000, wet: 0.16, size: 0.55, damp: 0.25,
    partials: [[1, 1, 1, 0.0018], [2.0, 0.30, 0.7], [2.76, 0.22, 0.55], [5.40, 0.12, 0.35], [8.93, 0.05, 0.22], [13.3, 0.018, 0.15]],
  },
  // 数码：干净的正弦 + 二、三次谐波，起音最快、几乎不留尾巴，节奏利落
  digital: {
    name: '数码', attack: 0.0010, decay: 0.055, click: 0.04, clickBand: 5200, wet: 0.05, size: 0.25, damp: 0.6,
    partials: [[1, 1, 1], [2, 0.22, 0.7], [3, 0.10, 0.5], [0.5, 0.12, 1.2]],
  },
};

// 响度档（LUFS，按瞬时最大值）：轻 → 醒目；整体和 0.1.67 那套持平，所以原来的音量设置听起来不变
const LEVEL = { tick: -38, whisper: -34, soft: -27, normal: -23.5, alert: -21, hero: -17 };

// ---------------- 事件 ----------------
// 每个函数 (out, T) 往缓冲里画声音；返回 [长度秒, 响度档]
const CUES = {
  // 导航：最短的单音（E6），轻到连按几十下也不累
  nav: [0.16, 'whisper', (o, T) => strike(o, 0, st(7), 0.8, T, { decay: 0.55 })],
  // 到头：低八度的闷音轻轻往下一沉，像碰到墙
  bump: [0.24, 'soft', (o, T) => {
    strike(o, 0, st(-12), 0.9, { ...T, partials: T.partials.slice(0, 3), click: 0 }, { glide: -1.2, glideTime: 0.08, decay: 0.8 });
    click(o, 0, 0.18, 900);
  }],
  // 确认：A5 → E6 上行五度，两下紧挨着
  activate: [0.32, 'normal', (o, T) => { strike(o, 0, st(0), 0.85, T, { decay: 0.7 }); strike(o, 0.045, st(7), 1, T); }],
  // 返回：确认的镜像，E6 → A5，稍轻
  back: [0.30, 'soft', (o, T) => { strike(o, 0, st(7), 0.85, T, { decay: 0.7 }); strike(o, 0.045, st(0), 0.95, T); }],
  // 进详情：往上的空气声 + 上滑到 C#6 的音
  detail_in: [0.42, 'normal', (o, T) => { swish(o, 0, 0.26, 900, 5200, 0.22); strike(o, 0.09, st(4), 1, T, { glide: 2, glideTime: 0.05, decay: 1.4 }); }],
  // 进详情的镜像
  detail_out: [0.40, 'soft', (o, T) => { swish(o, 0, 0.24, 5200, 900, 0.20); strike(o, 0.08, st(4), 0.95, T, { glide: -2, glideTime: 0.05, decay: 1.2 }); }],
  // 启动游戏：大和弦（加九）快速上行琶音 + 上扬的空气声，最有仪式感
  launch: [1.6, 'hero', (o, T) => {
    swish(o, 0, 0.7, 500, 7000, 0.16);
    [0, 4, 7, 11, 14, 19].forEach((n, i) => strike(o, 0.05 + i * 0.055, st(n - 5), 0.75 + i * 0.05, T, { decay: 3.2, pan: (i - 2.5) * 0.12 }));
  }],
  // 页签：单个 D6，加一个清楚的"咔"
  tab: [0.20, 'soft', (o, T) => { strike(o, 0, st(5), 0.9, T, { decay: 0.7 }); click(o, 0, 0.16, 3600); }],
  // 控制中心进：从左边滑进来（声相左 → 中），落在 F#6
  menu_in: [0.40, 'normal', (o, T) => { swish(o, 0, 0.22, 1200, 4800, 0.2, -0.8, 0); strike(o, 0.1, st(9), 0.9, T, { pan: -0.15, decay: 1.2 }); }],
  // 控制中心出：中 → 左，落回 A5
  menu_out: [0.36, 'soft', (o, T) => { swish(o, 0, 0.2, 4800, 1200, 0.18, 0, -0.8); strike(o, 0.08, st(2), 0.85, T, { pan: -0.3, decay: 1 }); }],
  // 弹层开：四度上行的柔和两音
  modal_show: [0.36, 'normal', (o, T) => { strike(o, 0, st(0), 0.8, T, { decay: 1.1 }); strike(o, 0.06, st(5), 0.95, T, { decay: 1.3 }); }],
  modal_hide: [0.32, 'soft', (o, T) => { strike(o, 0, st(5), 0.8, T, { decay: 1 }); strike(o, 0.06, st(0), 0.9, T, { decay: 1.1 }); }],
  // 开关开：大三度上行、亮；关：下行、低一点
  toggle_on: [0.26, 'normal', (o, T) => { click(o, 0, 0.2, 3000); strike(o, 0.012, st(4), 0.8, T, { decay: 0.6 }); strike(o, 0.05, st(8), 1, T, { decay: 0.8 }); }],
  toggle_off: [0.26, 'soft', (o, T) => { click(o, 0, 0.2, 2400); strike(o, 0.012, st(4), 0.85, T, { decay: 0.6 }); strike(o, 0.05, st(0), 0.9, T, { decay: 0.7 }); }],
  // 滑块：极短的刻度音，上高下低（引擎每格再按方向微调音高）
  slider_up: [0.11, 'whisper', (o, T) => strike(o, 0, st(12), 0.8, T, { decay: 0.35 })],
  slider_down: [0.11, 'whisper', (o, T) => strike(o, 0, st(7), 0.8, T, { decay: 0.35 })],
  // 提示：门铃式两音 E6 → A6，余音稍长，不打断操作
  toast: [0.75, 'alert', (o, T) => { strike(o, 0, st(7), 0.85, T, { decay: 2.2 }); strike(o, 0.12, st(12), 0.9, T, { decay: 2.6 }); }],
  // 成功：大三和弦琶音 A5-C#6-E6-A6
  positive: [0.85, 'alert', (o, T) => [0, 4, 7, 12].forEach((n, i) => strike(o, i * 0.065, st(n), 0.8 + i * 0.06, T, { decay: 2.4 }))],
  // 出错：低八度的小二度（A4 + A#4）双击、往下沉——不刺耳，但一听就知道不对
  error: [0.42, 'alert', (o, T) => {
    for (const at of [0, 0.13]) {
      strike(o, at, st(-12), 0.75, T, { glide: -0.8, glideTime: 0.08, decay: 0.9 });
      strike(o, at, st(-11), 0.6, T, { glide: -0.8, glideTime: 0.08, decay: 0.9 });
      strike(o, at, st(-5), 0.35, T, { decay: 0.6 });
    }
  }],
  // 打字：按键的"嗒"+ 很轻的高音，引擎随机微调音高
  typing: [0.07, 'tick', (o, T) => { click(o, 0, 0.5, 3200, 0, 0.003); strike(o, 0, st(19), 0.25, T, { decay: 0.25 }); }],
  // 开机：和弦从低往上铺开，像屏幕亮起
  startup: [2.2, 'hero', (o, T) => {
    swish(o, 0, 1.1, 300, 6000, 0.1);
    [-12, -5, 0, 4, 7, 11, 14].forEach((n, i) => strike(o, 0.08 + i * 0.11, st(n), 0.55 + i * 0.05, T, { decay: 5, pan: (i - 3) * 0.1 }));
  }],
  // 截图：两下机械快门（开 / 合）+ 一点镜身共鸣
  shutter: [0.30, 'normal', (o, T) => {
    click(o, 0, 0.9, 2200, -0.1, 0.006); click(o, 0.004, 0.5, 5500, 0.1, 0.004);
    click(o, 0.07, 0.7, 1800, 0.1, 0.006); click(o, 0.074, 0.4, 4800, -0.1, 0.004);
    strike(o, 0.0, st(-7), 0.15, { ...T, click: 0 }, { decay: 0.6 });
  }],
};

// ---------------- 输出 ----------------
function wav24(out) {
  const n = out[0].length, data = Buffer.alloc(n * 6);
  for (let i = 0; i < n; i++)
    for (let c = 0; c < 2; c++) {
      const tpdf = (rand() - rand()) / 8388608;
      const v = Math.max(-8388607, Math.min(8388607, Math.round((out[c][i] + tpdf) * 8388607)));
      data.writeIntLE(v, (i * 2 + c) * 3, 3);
    }
  const h = Buffer.alloc(44);
  h.write('RIFF', 0); h.writeUInt32LE(36 + data.length, 4); h.write('WAVE', 8); h.write('fmt ', 12);
  h.writeUInt32LE(16, 16); h.writeUInt16LE(1, 20); h.writeUInt16LE(2, 22); h.writeUInt32LE(RATE, 24);
  h.writeUInt32LE(RATE * 6, 28); h.writeUInt16LE(6, 32); h.writeUInt16LE(24, 34); h.write('data', 36); h.writeUInt32LE(data.length, 40);
  return Buffer.concat([h, data]);
}

const manifest = {};
for (const [theme, T] of Object.entries(TIMBRES)) {
  mkdirSync(join(OUT, theme), { recursive: true });
  for (const [cue, [seconds, level, draw]] of Object.entries(CUES)) {
    seed = [...theme + cue].reduce((h, ch) => (h * 31 + ch.charCodeAt(0)) >>> 0, 7);
    const out = buf(seconds + T.wet * 0.6);
    draw(out, T);
    room(out, T.wet, T.size, T.damp);
    // 低于 180 Hz 的能量小喇叭放不出来，只会让功放早早削波：切掉；16 kHz 以上也收一点
    for (const ch of out) filter(ch, biquad('hp', 180, 0.7), biquad('hp', 180, 0.7), biquad('lp', 16000, 0.7));
    // 响度对齐到档位，再保证峰值 ≤ −1 dBFS
    let g = 10 ** ((LEVEL[level] - loudness(out)) / 20);
    const p = peak(out) * g;
    if (p > 0.891) g *= 0.891 / p;
    for (const ch of out) for (let i = 0; i < ch.length; i++) ch[i] *= g;
    // 尾巴最后 10 ms 收到 0，去掉滤波器留下的残余
    for (const ch of out) for (let i = 0; i < 480; i++) ch[ch.length - 1 - i] *= i / 480;
    writeFileSync(join(OUT, theme, cue + '.wav'), wav24(out));
    manifest[`${theme}/${cue}`] = { seconds: +(out[0].length / RATE).toFixed(3), lufsMax: +loudness(out).toFixed(1), peakDb: +(20 * Math.log10(peak(out))).toFixed(1) };
  }
}
writeFileSync(join(OUT, 'sfx-manifest.json'), JSON.stringify({ format: '48 kHz / 24-bit stereo PCM', cues: manifest }, null, 1) + '\n');
console.log(`wrote ${Object.keys(manifest).length} cues to ${OUT}`);
