// 哪个 slot 显示哪一项。
// - 无限墙：内容按一个横向周期（若干整块）重复铺满整个平面；偏移量让「起点」那一格显示第 0 项。
// - 有限墙（过滤后）：每个 slot 打分，第 0、1、2… 名按分数排，好的挨着起点、向外螺旋。
import { BLOCK_COLS, BLOCK_ROWS, PITCH, SLOTS_PER_BLOCK, type Slot } from './geometry';

const mod = (v: number, n: number) => ((v % n) + n) % n;

/** 周期的横纵比：低于 1 时竖向重复得比横向快，看上去像条纹。 */
const FIELD_ASPECT = 2.2;

export type Period = { across: number; down: number };

export const periodFor = (itemCount: number): Period => {
    const blocks = Math.max(1, Math.ceil(itemCount / SLOTS_PER_BLOCK));
    const across = blocks <= 1 ? 1 : Math.max(1, Math.round(Math.sqrt(FIELD_ASPECT * blocks * BLOCK_ROWS / BLOCK_COLS)));
    return { across, down: Math.ceil(blocks / across) };
};

/** 一个 slot 在周期序列里的位置（加偏移之前）。 */
const wrapIndex = (slot: Pick<Slot, 'column' | 'row' | 'index'>, itemCount: number, period: Period) => {
    const block = mod(slot.row, period.down) * period.across + mod(slot.column, period.across);
    return (block * SLOTS_PER_BLOCK + slot.index) % itemCount;
};

export type InfiniteLayout = { kind: 'infinite'; count: number; period: Period; offset: number };
export type FiniteLayout = { kind: 'finite'; count: number; ranks: Map<string, number> };
export type WallLayout = InfiniteLayout | FiniteLayout;

export const infiniteLayout = (count: number, start: Slot | null): InfiniteLayout => {
    const period = periodFor(count);
    const offset = start && count > 0 ? mod(-wrapIndex(start, count, period), count) : 0;
    return { kind: 'infinite', count, period, offset };
};

/** 第几项（没有内容时为 null）。 */
export const itemAt = (layout: WallLayout, slot: Slot): number | null => {
    if (layout.count <= 0) return null;
    if (layout.kind === 'infinite') return mod(wrapIndex(slot, layout.count, layout.period) + layout.offset, layout.count);
    const rank = layout.ranks.get(slot.key);
    return rank !== undefined && rank < layout.count ? rank : null;
};

// ---------- 有限墙的排名 ----------

/** 竖向距离打九折（墙是横的）；大卡按 √面积 往前拉，头几名落在大海报上。 */
export const RANK_VERTICAL = 0.9;
export const RANK_AREA = 0.35;

export const rankScore = (s: Slot, ox: number, oy: number) =>
    Math.abs(s.cx - ox) + Math.abs(s.cy - oy) * RANK_VERTICAL - Math.sqrt(s.cols * s.rows) * PITCH * RANK_AREA;

/** 候选 slot 排序：起点（若给）排第 0，其余按分数，同分按世界位置，结果稳定。 */
export const rankSlots = (candidates: readonly Slot[], ox: number, oy: number, startKey?: string | null): Slot[] => {
    const scored = candidates.map(s => ({ s, v: rankScore(s, ox, oy) }));
    scored.sort((a, b) => a.v - b.v || a.s.row - b.s.row || a.s.column - b.s.column || a.s.index - b.s.index);
    const order = scored.map(e => e.s);
    const i = startKey ? order.findIndex(s => s.key === startKey) : -1;
    if (i > 0) order.unshift(...order.splice(i, 1));
    return order;
};

export const finiteLayout = (candidates: readonly Slot[], count: number, ox: number, oy: number, startKey?: string | null): FiniteLayout => {
    const order = rankSlots(candidates, ox, oy, startKey).slice(0, count);
    return { kind: 'finite', count, ranks: new Map(order.map((s, i) => [s.key, i])) };
};
