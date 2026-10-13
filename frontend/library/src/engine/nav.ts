// 方向导航：从当前卡往某个方向走到哪一张。候选必须整张在前进方向前方；
// 分数 = 前进距离 + （横向没有重叠时）2×错开距离 + 一个步距的惩罚，同分取横向偏移小的。
// 所以十字键会沿一行走，不会斜着抄近路。
import { PITCH, blockSlots, type Rect, type Slot } from './geometry';

export type Direction = 'up' | 'down' | 'left' | 'right';

const AXES = {
    right: { a: 'x', c: 'y', as: 'width', cs: 'height', sign: 1 },
    left: { a: 'x', c: 'y', as: 'width', cs: 'height', sign: -1 },
    down: { a: 'y', c: 'x', as: 'height', cs: 'width', sign: 1 },
    up: { a: 'y', c: 'x', as: 'height', cs: 'width', sign: -1 },
} as const;

export const pickAdjacent = <T>(from: Rect, candidates: Iterable<{ value: T; rect: Rect }>, dir: Direction): T | null => {
    const ax = AXES[dir];
    let best: T | null = null, bestScore = Infinity, bestCross = Infinity;
    for (const { value, rect } of candidates) {
        const step = ax.sign === 1
            ? rect[ax.a] - (from[ax.a] + from[ax.as])
            : from[ax.a] - (rect[ax.a] + rect[ax.as]);
        if (step < 0) continue;
        const overlap = Math.min(from[ax.c] + from[ax.cs], rect[ax.c] + rect[ax.cs]) - Math.max(from[ax.c], rect[ax.c]);
        const score = step + (overlap > 0 ? 0 : -overlap * 2 + PITCH);
        const cross = Math.abs(rect[ax.c] + rect[ax.cs] / 2 - (from[ax.c] + from[ax.cs] / 2));
        if (score < bestScore || (score === bestScore && cross < bestCross)) {
            best = value; bestScore = score; bestCross = cross;
        }
    }
    return best;
};

/** 在周围块里找下一张（有内容的）卡；一圈找不到再向外扩，最多三圈。`drawn` 给出让位后的实际矩形。 */
export const findAdjacentSlot = (
    from: Slot,
    dir: Direction,
    hasContent: (s: Slot) => boolean,
    drawn: (s: Slot) => Rect = s => s,
): Slot | null => {
    const source = drawn(from);
    for (let radius = 1; radius <= 3; radius++) {
        const candidates: { value: Slot; rect: Rect }[] = [];
        for (let r = from.row - radius; r <= from.row + radius; r++) {
            for (let c = from.column - radius; c <= from.column + radius; c++) {
                for (const s of blockSlots(c, r)) {
                    if (s.key === from.key || !hasContent(s)) continue;
                    candidates.push({ value: s, rect: drawn(s) });
                }
            }
        }
        const next = pickAdjacent(source, candidates, dir);
        if (next) return next;
    }
    return null;
};

export const nearestTo = <T extends Rect>(items: readonly T[], x: number, y: number): T | null => {
    let best: T | null = null, d = Infinity;
    for (const it of items) {
        const dx = it.x + it.width / 2 - x, dy = it.y + it.height / 2 - y;
        const v = dx * dx + dy * dy;
        if (v < d) { d = v; best = it; }
    }
    return best;
};
