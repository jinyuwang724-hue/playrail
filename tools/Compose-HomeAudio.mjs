// Original Claw menu sounds and circular ambient compositions; no external samples.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.join(root, 'frontend/library-native/ClawLibrary/Assets/Audio');
fs.mkdirSync(out, { recursive: true });
const hz = midi => 440 * 2 ** ((midi - 69) / 12);
const tau = 2 * Math.PI;
let seed = 72317;
const random = () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 4294967296 * 2 - 1; };
const report = [];
function wav(file, channels, rate, samples, peakTarget) {
  let peak = 0, energy = 0;
  for (const c of channels) for (let i = 0; i < samples; i++) peak = Math.max(peak, Math.abs(c[i]));
  const gain = peakTarget / Math.max(peak, 1e-9), count = channels.length;
  const data = Buffer.alloc(44 + samples * count * 2);
  data.write('RIFF', 0); data.writeUInt32LE(data.length - 8, 4); data.write('WAVEfmt ', 8);
  data.writeUInt32LE(16, 16); data.writeUInt16LE(1, 20); data.writeUInt16LE(count, 22);
  data.writeUInt32LE(rate, 24); data.writeUInt32LE(rate * count * 2, 28);
  data.writeUInt16LE(count * 2, 32); data.writeUInt16LE(16, 34); data.write('data', 36); data.writeUInt32LE(data.length - 44, 40);
  for (let i = 0; i < samples; i++) for (let c = 0; c < count; c++) {
    const value = channels[c][i] * gain;
    energy += value * value;
    data.writeInt16LE(Math.round(Math.max(-1, Math.min(1, value)) * 32767), 44 + (i * count + c) * 2);
  }
  fs.writeFileSync(path.join(out, file), data);
  report.push({ file, seconds: samples / rate, channels: count, rate, peak: peakTarget,
    rms: Math.sqrt(energy / (samples * count)), boundaryStep: Math.max(...channels.map(c => Math.abs(c[0] - c[samples - 1]) * gain)) });
}
const cues = {
  move: [[0, 78, .038, .52]], confirm: [[0, 69, .13, .7], [.045, 76, .13, .55]],
  back: [[0, 74, .11, .65], [.04, 67, .1, .5]], switch: [[0, 72, .075, .6], [.022, 79, .08, .28]],
  open: [[0, 62, .18, .5], [.055, 69, .17, .52], [.095, 76, .18, .42]],
  toggle: [[0, 76, .065, .6], [.028, 79, .07, .3]],
  launch: [[0, 57, .32, .48], [.065, 64, .3, .42], [.13, 69, .3, .4], [.19, 76, .3, .32]],
  error: [[0, 62, .1, .45], [.085, 60, .12, .4]]
};
for (const [theme, trans] of [['soft', -5], ['crystal', 2], ['digital', -2]]) {
  fs.mkdirSync(path.join(out, 'Sfx', theme), { recursive: true });
  for (const [kind, notes] of Object.entries(cues)) {
    const rate = 44100, seconds = Math.max(...notes.map(n => n[0] + n[2])) + .045;
    const count = Math.ceil(rate * seconds), sound = new Float64Array(count);
    for (const [start, midi, length, amplitude] of notes) {
      const f = hz(midi + trans), offset = Math.round(start * rate), n = Math.ceil(length * rate);
      let previous = 0;
      for (let i = 0; i < n && i + offset < count; i++) {
        const t = i / rate, phase = tau * f * t;
        const env = (1 - Math.exp(-t / .0025)) * Math.exp(-t / (length / 4)) * Math.min(1, (length - t) / .008);
        const noise = random(), tap = (noise - previous) * Math.exp(-t / .004); previous = noise;
        let wave = Math.sin(phase);
        if (theme === 'soft') wave = .88 * wave + .1 * Math.sin(phase * 2.003) + .025 * tap;
        if (theme === 'crystal') wave = .8 * Math.sin(phase + .35 * Math.sin(phase * 2.001) * Math.exp(-t / .025)) + .13 * Math.sin(phase * 3.01);
        if (theme === 'digital') wave = .84 * wave + .1 * Math.sin(phase * 3) / 3 + .06 * Math.sin(phase * 5) / 5;
        sound[offset + i] += wave * env * amplitude;
      }
    }
    // A short, quiet reflection adds space without making navigation ring out.
    for (let i = count - 1; i >= 0; i--) if (i >= 573) sound[i] += .07 * sound[i - 573];
    wav(`Sfx/${theme}/${kind}.wav`, [sound], rate, count, kind === 'move' ? .19 : kind === 'launch' ? .42 : .32);
  }
}

// Sixteen bars with extended voicings, sparse motifs and tails wrapped onto the start.
const pieces = [
  { id: 'daylight', name: '浮光', bpm: 72, chords: [[50,57,61,64,69],[47,54,57,62,66],[43,50,57,59,66],[45,52,57,59,64]], melody: [81,78,76,73,76,78,85,81], brightness: .28 },
  { id: 'starport', name: '星港', bpm: 66, chords: [[48,55,59,62,67],[45,52,55,59,64],[41,48,55,57,64],[43,50,57,59,62]], melody: [79,74,76,71,74,79,83,81], brightness: .17 },
  { id: 'night', name: '夜航', bpm: 60, chords: [[45,52,55,59,64],[41,48,52,57,60],[48,55,59,62,67],[43,50,55,57,62]], melody: [76,71,67,74,72,67,71,64], brightness: .1 }
];
for (const piece of pieces) {
  const rate = 32000, beat = 60 / piece.bpm, bar = beat * 4, duration = bar * 16, size = Math.round(rate * duration);
  const left = new Float64Array(size), right = new Float64Array(size);
  function voice(start, length, midi, amplitude, pan, type = 'pad') {
    const f = hz(midi), offset = Math.round(start * rate), frames = Math.round(length * rate);
    const gl = Math.sqrt((1 - pan) / 2), gr = Math.sqrt((1 + pan) / 2);
    for (let i = 0; i < frames; i++) {
      const t = i / rate, u = t / length, p = tau * f * t;
      let env, tone;
      if (type === 'pad') {
        env = Math.sin(Math.PI * u) ** 2;
        tone = .63 * Math.sin(p) + .22 * Math.sin(p * 1.0019 + .5) + .1 * Math.sin(2*p) + .035 * Math.sin(3*p);
      } else {
        env = (1 - Math.exp(-t / .008)) * Math.exp(-t / (type === 'bell' ? 1.15 : .55)) * Math.min(1, (length - t) / .06);
        tone = Math.sin(p + piece.brightness * Math.sin(p * 2.003) * Math.exp(-t / .4)) + .06 * Math.sin(3*p);
      }
      const index = (offset + i) % size, value = amplitude * env * tone;
      left[index] += value * gl; right[index] += value * gr;
    }
  }
  for (let b = 0; b < 16; b++) {
    const chord = piece.chords[Math.floor(b / 2) % 4];
    if (b % 2 === 0) {
      chord.forEach((note, j) => voice(b * bar, bar * 2.9, note, j ? .115 : .13, (j - 2) * .24));
      voice(b * bar, bar * 1.85, chord[0] - 12, .09, 0);
    }
    // A lightly syncopated pentatonic figure; alternate bars leave space for the interface.
    for (let k = 0; k < (b % 2 ? 2 : 3); k++) {
      const note = chord[2 + k % 3] + 12;
      voice(b * bar + (k * 1.25 + .5) * beat, beat * 2.8, note, .055, (k - 1) * .5, 'pluck');
    }
    if (b % 2 === 0) voice(b * bar + beat * 1.5, beat * 4, piece.melody[b / 2], .065, (b % 4 ? -.23 : .23), 'bell');
  }
  // Circular stereo reflections: the sample before/after the loop belongs to the same continuous texture.
  const dryL = left.slice(), dryR = right.slice();
  for (const [seconds, gain] of [[beat*.75,.16],[beat*1.5,.085],[beat*2.25,.04]]) {
    const delay = Math.round(seconds * rate);
    for (let i = 0; i < size; i++) { const j = (i - delay + size) % size; left[i] += dryR[j] * gain; right[i] += dryL[j] * gain; }
  }
  wav(`${piece.id}.wav`, [left, right], rate, size, .54);
}
fs.writeFileSync(path.join(out, 'audio-manifest.json'), JSON.stringify({ original: true, externalSamples: false, pieces, files: report }, null, 2));
console.log(JSON.stringify(report));
