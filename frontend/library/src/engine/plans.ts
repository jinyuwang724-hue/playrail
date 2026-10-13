// 换内容时的动画计划（纯计算，动画由舞台做）。
// - 翻牌：只翻内容变了的 slot，从起点向外按距离错开；视口外的、超过上限的直接换。
// - 整墙波次：先按「离视口左上角越远越先走」抬起淡出，全部走完后按「越近越先到」落回。
import { PITCH, overlaps, rectBounds, type Bounds, type Slot } from './geometry';
import { MOTION } from './motion';

export const MAX_ANIMATED = 400;

export type Change = { slot: Slot; from: string | null; to: string | null };
export type FlipOrigin = { kind: 'point'; x: number; y: number } | { kind: 'line'; x: number };

export type FlipStep = { key: string; delay: number; direction: -1 | 1 };
export type FlipPlan = { steps: FlipStep[]; instant: string[]; durationMs: number };

export const planFlip = (changes: readonly Change[], origin: FlipOrigin, visible: Bounds): FlipPlan => {
    const near: { slot: Slot; d: number }[] = [];
    const instant: string[] = [];
    for (const ch of changes) {
        if (ch.from === ch.to) continue;
        if (!overlaps(rectBounds(ch.slot), visible)) { instant.push(ch.slot.key); continue; }
        const d = origin.kind === 'point'
            ? Math.hypot(ch.slot.cx - origin.x, ch.slot.cy - origin.y)
            : Math.abs(ch.slot.cx - origin.x);
        near.push({ slot: ch.slot, d });
    }
    near.sort((a, b) => a.d - b.d);
    const { staggerPerCell, maxDelay, outMs, inMs } = MOTION.flip;
    let last = -1;
    const steps: FlipStep[] = [];
    near.forEach(({ slot, d }, i) => {
        if (i >= MAX_ANIMATED) { instant.push(slot.key); return; }
        const delay = Math.min(maxDelay, (d / PITCH) * staggerPerCell);
        last = Math.max(last, delay);
        steps.push({ key: slot.key, delay, direction: slot.cx < (origin.kind === 'point' ? origin.x : origin.x) ? -1 : 1 });
    });
    return { steps, instant, durationMs: steps.length ? last + outMs + inMs : 0 };
};

export type WaveStep = { key: string; outDelay: number; inDelay: number };
export type WavePlan = { steps: WaveStep[]; instant: string[]; durationMs: number };

export const waveStagger = (slot: { x: number; y: number }, corner: { left: number; top: number }) => {
    const cells = (Math.max(0, slot.x - corner.left) + Math.max(0, slot.y - corner.top)) / PITCH;
    return Math.min(MOTION.wave.maxDelay, cells * MOTION.wave.staggerPerCell);
};

export const planWave = (changes: readonly Change[], viewport: Bounds, visible: Bounds): WavePlan => {
    const { maxDelay, outMs, inMs } = MOTION.wave;
    const exitTotal = maxDelay + outMs;
    const list: { slot: Slot; s: number }[] = [];
    const instant: string[] = [];
    for (const ch of changes) {
        if (ch.from === ch.to) continue;
        if (!overlaps(rectBounds(ch.slot), visible)) { instant.push(ch.slot.key); continue; }
        list.push({ slot: ch.slot, s: waveStagger(ch.slot, viewport) });
    }
    list.sort((a, b) => a.s - b.s);
    list.slice(MAX_ANIMATED).forEach(e => instant.push(e.slot.key));
    const steps = list.slice(0, MAX_ANIMATED).map(({ slot, s }) => ({
        key: slot.key,
        outDelay: Math.round(maxDelay - s),
        inDelay: Math.round(exitTotal + s),
    }));
    const durationMs = steps.reduce((m, st) => Math.max(m, st.inDelay + inMs), 0);
    return { steps, instant, durationMs };
};
