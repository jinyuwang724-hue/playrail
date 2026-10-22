// 游戏库界面音效：照 Steam 大屏幕的事件清单（导航、到头、确认、返回、进出详情、启动游戏、页签、侧边菜单、弹层、开关、
// 滑块、提示、成功、出错、打字、开机、截图）自己合成的原创短音，不用任何外部采样。确定性生成：
//   node tools/Compose-UiSounds.mjs
// 三套风格同一组事件：轻触（暖、圆）、清透（钟片、泛音亮）、数码（三角 / 方波、干脆）。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.join(root, 'frontend/library-native/ClawLibrary/Assets/Audio/Sfx');
const RATE = 44100, TAU = 2 * Math.PI;
const hz = midi => 440 * 2 ** ((midi - 69) / 12);
let seed = 90211;
const rnd = () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 4294967296 * 2 - 1; };

class Buf {
  constructor(seconds) { this.d = new Float64Array(Math.ceil(seconds * RATE)); }
  add(i, v) { if (i >= 0 && i < this.d.length) this.d[i] += v; }
}

const THEMES = {
  soft: { trans: -5, bright: 0.55, wave: 'sine', click: 0.6 },
  crystal: { trans: 2, bright: 1, wave: 'bell', click: 0.45 },
  digital: { trans: -2, bright: 0.8, wave: 'tri', click: 0.8 },
};

/** 一个音：起音 / 指数衰减 / 可滑音；音色按风格。 */
function tone(b, th, { at = 0, midi, to = null, dur = 0.1, amp = 0.5, attack = 0.002, decay = null, release = 0.012 }) {
  const f0 = hz(midi + th.trans), f1 = to == null ? f0 : hz(to + th.trans);
  const n = Math.round(dur * RATE), off = Math.round(at * RATE), tau = decay ?? dur / 4;
  let phase = 0;
  for (let i = 0; i < n; i++) {
    const t = i / RATE, u = i / n;
    const f = f0 * (f1 / f0) ** Math.min(1, u * 1.6);
    phase += TAU * f / RATE;
    const env = (1 - Math.exp(-t / attack)) * Math.exp(-t / tau) * Math.min(1, (dur - t) / release);
    let w;
    if (th.wave === 'sine') w = Math.sin(phase) + 0.12 * Math.sin(2 * phase) * Math.exp(-t / 0.03);
    else if (th.wave === 'bell') w = 0.78 * Math.sin(phase + 0.9 * Math.sin(phase * 3.5) * Math.exp(-t / 0.05)) + 0.16 * Math.sin(phase * 2.76) * Math.exp(-t / 0.08);
    else { const p = (phase / TAU) % 1; w = 0.75 * (4 * Math.abs(p - 0.5) - 1) + 0.18 * Math.sin(phase * 3) * Math.exp(-t / 0.02); }
    b.add(off + i, w * env * amp);
  }
}

/** 噪声扫频（带通，中心频率从 from 走到 to）：「嗖」的一下。 */
function sweep(b, th, { at = 0, from, to, dur, amp = 0.3, q = 2.2 }) {
  const n = Math.round(dur * RATE), off = Math.round(at * RATE);
  let low = 0, band = 0;
  for (let i = 0; i < n; i++) {
    const u = i / n, fc = from * (to / from) ** u * (0.7 + 0.3 * th.bright);
    const f = 2 * Math.sin(Math.PI * Math.min(fc, RATE / 6) / RATE);
    const x = rnd();
    low += f * band; const high = x - low - band / q; band += f * high;
    const env = Math.sin(Math.PI * Math.min(1, u * 1.15)) ** 1.6;
    b.add(off + i, band * env * amp);
  }
}

/** 很短的一下「嗒」（高通噪声 + 快速衰减），给按键手感。 */
function click(b, th, { at = 0, amp = 0.4, dur = 0.006 }) {
  const n = Math.round(dur * RATE), off = Math.round(at * RATE);
  let prev = 0;
  for (let i = 0; i < n; i++) { const x = rnd(); b.add(off + i, (x - prev) * Math.exp(-i / RATE / (dur / 4)) * amp * th.click); prev = x; }
}

/** 低沉的「咚」（到头）：正弦往下滑 + 低通噪声。 */
function thud(b, th, { at = 0, amp = 0.6 }) {
  tone(b, { ...th, wave: 'sine' }, { at, midi: 45, to: 38, dur: 0.11, amp, decay: 0.035 });
  let low = 0; const off = Math.round(at * RATE);
  for (let i = 0; i < 0.03 * RATE; i++) { low += 0.08 * (rnd() - low); b.add(off + i, low * Math.exp(-i / RATE / 0.008) * amp * 1.4); }
}

/** 短小的空间感：几道衰减的回声（导航类不加，免得糊）。 */
function room(b, amount = 0.18, delays = [0.023, 0.041, 0.067, 0.097]) {
  const d = b.d, src = Float64Array.from(d);
  delays.forEach((s, k) => { const o = Math.round(s * RATE), g = amount * 0.72 ** k; for (let i = o; i < d.length; i++) d[i] += src[i - o] * g; });
}

function save(file, b, peak) {
  const d = b.d;
  let max = 0; for (const v of d) max = Math.max(max, Math.abs(v));
  const g = peak / Math.max(max, 1e-9);
  const fade = Math.round(0.004 * RATE);   // 尾巴淡到 0，不留爆音
  const buf = Buffer.alloc(44 + d.length * 2);
  buf.write('RIFF', 0); buf.writeUInt32LE(buf.length - 8, 4); buf.write('WAVEfmt ', 8);
  buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22);
  buf.writeUInt32LE(RATE, 24); buf.writeUInt32LE(RATE * 2, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34);
  buf.write('data', 36); buf.writeUInt32LE(d.length * 2, 40);
  for (let i = 0; i < d.length; i++) {
    const tail = i > d.length - fade ? (d.length - i) / fade : 1;
    buf.writeInt16LE(Math.round(Math.max(-1, Math.min(1, d[i] * g * tail)) * 32767), 44 + i * 2);
  }
  fs.writeFileSync(file, buf);
  return { seconds: +(d.length / RATE).toFixed(3), peak };
}

// 每个事件：时长、峰值（相对响度：导航最轻，启动、开机最响）、怎么画
const CUES = {
  nav: [0.05, 0.16, (b, th) => { click(b, th, { amp: 0.5 }); tone(b, th, { midi: 86, dur: 0.045, amp: 0.5, decay: 0.012 }); }],
  bump: [0.13, 0.26, (b, th) => { thud(b, th, {}); }],
  activate: [0.2, 0.3, (b, th) => { click(b, th, {}); tone(b, th, { midi: 76, dur: 0.11, amp: 0.55 }); tone(b, th, { at: 0.035, midi: 83, dur: 0.14, amp: 0.5 }); room(b, 0.12); }],
  back: [0.18, 0.26, (b, th) => { click(b, th, { amp: 0.3 }); tone(b, th, { midi: 81, dur: 0.1, amp: 0.5 }); tone(b, th, { at: 0.035, midi: 74, dur: 0.12, amp: 0.45 }); room(b, 0.1); }],
  detail_in: [0.32, 0.28, (b, th) => { sweep(b, th, { from: 500, to: 3800, dur: 0.2, amp: 0.5 }); tone(b, th, { at: 0.08, midi: 83, dur: 0.18, amp: 0.3 }); tone(b, th, { at: 0.11, midi: 90, dur: 0.18, amp: 0.22 }); room(b, 0.15); }],
  detail_out: [0.28, 0.25, (b, th) => { sweep(b, th, { from: 3600, to: 450, dur: 0.18, amp: 0.5 }); tone(b, th, { at: 0.05, midi: 86, to: 79, dur: 0.16, amp: 0.25 }); room(b, 0.12); }],
  launch: [1.5, 0.42, (b, th) => {
    sweep(b, th, { from: 300, to: 6500, dur: 0.55, amp: 0.45, q: 3 });
    [64, 71, 76, 83, 88].forEach((m, k) => tone(b, th, { at: 0.06 * k, midi: m, dur: 0.5, amp: 0.42 - k * 0.03, decay: 0.18 }));
    [76, 83, 88, 95].forEach(m => tone(b, th, { at: 0.36, midi: m, dur: 1.05, amp: 0.2, attack: 0.03, decay: 0.38 }));
    room(b, 0.3, [0.031, 0.053, 0.079, 0.113, 0.157, 0.211]);
  }],
  tab: [0.09, 0.22, (b, th) => { click(b, th, {}); tone(b, th, { midi: 84, dur: 0.03, amp: 0.4, decay: 0.01 }); click(b, th, { at: 0.026, amp: 0.3 }); tone(b, th, { at: 0.026, midi: 91, dur: 0.035, amp: 0.35, decay: 0.012 }); }],
  menu_in: [0.36, 0.27, (b, th) => { sweep(b, th, { from: 280, to: 2600, dur: 0.24, amp: 0.45 }); tone(b, th, { at: 0.04, midi: 69, dur: 0.28, amp: 0.3, attack: 0.03, decay: 0.12 }); tone(b, th, { at: 0.07, midi: 76, dur: 0.26, amp: 0.26, attack: 0.03, decay: 0.12 }); room(b, 0.16); }],
  menu_out: [0.32, 0.24, (b, th) => { sweep(b, th, { from: 2600, to: 280, dur: 0.22, amp: 0.45 }); tone(b, th, { midi: 76, dur: 0.22, amp: 0.26, attack: 0.02, decay: 0.09 }); tone(b, th, { at: 0.04, midi: 69, dur: 0.24, amp: 0.26, attack: 0.02, decay: 0.1 }); room(b, 0.14); }],
  modal_show: [0.22, 0.27, (b, th) => { click(b, th, { amp: 0.35 }); tone(b, th, { midi: 72, to: 79, dur: 0.12, amp: 0.5, decay: 0.05 }); sweep(b, th, { from: 900, to: 2600, dur: 0.1, amp: 0.2 }); room(b, 0.14); }],
  modal_hide: [0.2, 0.23, (b, th) => { tone(b, th, { midi: 79, to: 71, dur: 0.11, amp: 0.45, decay: 0.045 }); sweep(b, th, { from: 2400, to: 800, dur: 0.09, amp: 0.18 }); room(b, 0.12); }],
  toggle_on: [0.12, 0.25, (b, th) => { click(b, th, {}); tone(b, th, { midi: 79, dur: 0.045, amp: 0.45, decay: 0.015 }); tone(b, th, { at: 0.03, midi: 86, dur: 0.07, amp: 0.5, decay: 0.022 }); }],
  toggle_off: [0.12, 0.22, (b, th) => { click(b, th, {}); tone(b, th, { midi: 84, dur: 0.045, amp: 0.45, decay: 0.015 }); tone(b, th, { at: 0.03, midi: 77, dur: 0.07, amp: 0.45, decay: 0.022 }); }],
  slider_up: [0.05, 0.17, (b, th) => { click(b, th, { amp: 0.35 }); tone(b, th, { midi: 88, to: 90, dur: 0.035, amp: 0.45, decay: 0.01 }); }],
  slider_down: [0.05, 0.16, (b, th) => { click(b, th, { amp: 0.35 }); tone(b, th, { midi: 83, to: 81, dur: 0.035, amp: 0.45, decay: 0.01 }); }],
  toast: [0.65, 0.27, (b, th) => { tone(b, th, { midi: 81, dur: 0.4, amp: 0.4, decay: 0.12 }); tone(b, th, { at: 0.09, midi: 88, dur: 0.5, amp: 0.38, decay: 0.16 }); room(b, 0.2); }],
  positive: [0.5, 0.3, (b, th) => { [72, 76, 79, 84].forEach((m, k) => tone(b, th, { at: 0.045 * k, midi: m, dur: 0.3, amp: 0.4, decay: 0.09 })); room(b, 0.18); }],
  error: [0.32, 0.27, (b, th) => { [0, 0.11].forEach(at => { tone(b, { ...th, wave: th.wave === 'bell' ? 'sine' : th.wave }, { at, midi: 58, dur: 0.1, amp: 0.4, decay: 0.05 }); tone(b, th, { at, midi: 59, dur: 0.1, amp: 0.3, decay: 0.05 }); }); room(b, 0.08); }],
  typing: [0.03, 0.12, (b, th) => { click(b, th, { amp: 0.7, dur: 0.004 }); tone(b, th, { midi: 96, dur: 0.018, amp: 0.25, decay: 0.005 }); }],
  startup: [2.2, 0.38, (b, th) => {
    sweep(b, th, { from: 200, to: 3000, dur: 1.2, amp: 0.25, q: 4 });
    [60, 67, 72, 76, 79].forEach((m, k) => tone(b, th, { at: 0.12 * k, midi: m, dur: 1.9 - 0.12 * k, amp: 0.3, attack: 0.25, decay: 0.7 }));
    tone(b, th, { at: 0.7, midi: 91, dur: 1.2, amp: 0.12, attack: 0.05, decay: 0.4 });
    room(b, 0.3, [0.037, 0.061, 0.089, 0.131, 0.181, 0.24]);
  }],
  shutter: [0.16, 0.28, (b, th) => { sweep(b, th, { from: 2500, to: 1800, dur: 0.025, amp: 1.2, q: 1.2 }); sweep(b, th, { at: 0.07, from: 1800, to: 2600, dur: 0.03, amp: 1, q: 1.2 }); click(b, th, { amp: 0.6 }); click(b, th, { at: 0.07, amp: 0.5 }); }],
};

const report = {};
for (const [name, th] of Object.entries(THEMES)) {
  fs.mkdirSync(path.join(out, name), { recursive: true });
  for (const f of fs.readdirSync(path.join(out, name))) if (f.endsWith('.wav')) fs.unlinkSync(path.join(out, name, f));
  for (const [cue, [seconds, peak, draw]] of Object.entries(CUES)) {
    seed = 90211 + cue.length * 131;   // 每个事件的噪声固定，重跑结果一样
    const b = new Buf(seconds);
    draw(b, th);
    report[`${name}/${cue}`] = save(path.join(out, name, `${cue}.wav`), b, peak);
  }
}
fs.writeFileSync(path.join(out, 'sfx-manifest.json'), JSON.stringify(report, null, 1));
console.log(`${Object.keys(report).length} cues written to ${out}`);
