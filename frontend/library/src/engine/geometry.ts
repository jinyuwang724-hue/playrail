// 墙面几何：格、块、slot。世界坐标的单位是「世界 px」，相机再整体缩放到屏幕。
// 一个块 = 12×8 格、正好 12 张卡；块的样式（4 套模板 × 4 种镜像）只由块坐标决定，不存状态。
import { REFLOWS, TEMPLATES, type RawSlot } from './blockData';

export const CELL = 128;
export const GAP = 8;
export const PITCH = CELL + GAP;
export const BLOCK_COLS = 12;
export const BLOCK_ROWS = 8;
export const SLOTS_PER_BLOCK = 12;
export const BLOCK_W = BLOCK_COLS * PITCH;
export const BLOCK_H = BLOCK_ROWS * PITCH;

export type Rect = { x: number; y: number; width: number; height: number };
export type Bounds = { left: number; right: number; top: number; bottom: number };

export type Slot = Rect & {
    /** `列,行,序号`：跨层、跨过滤都不变的身份，内容是「翻」到它上面的。 */
    key: string;
    column: number;
    row: number;
    index: number;
    cols: number;
    rows: number;
    cx: number;
    cy: number;
};

export const overlaps = (a: Bounds, b: Bounds) => a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
export const rectBounds = (r: Rect): Bounds => ({ left: r.x, right: r.x + r.width, top: r.y, bottom: r.y + r.height });

type Mirror = (s: RawSlot) => RawSlot;
const MIRRORS: Mirror[] = [
    s => s,
    ([x, y, c, r]) => [BLOCK_COLS - x - c, y, c, r],
    ([x, y, c, r]) => [x, BLOCK_ROWS - y - r, c, r],
    ([x, y, c, r]) => [BLOCK_COLS - x - c, BLOCK_ROWS - y - r, c, r],
];

/** 块坐标的整数哈希（只用来挑镜像，让整面墙看不出周期）。 */
const hashBlock = (column: number, row: number) => {
    let h = (Math.imul(column, 0x27d4eb2d) + Math.imul(row, 0x165667b1)) | 0;
    h = Math.imul(h ^ (h >>> 16), 0x7feb352d);
    h = Math.imul(h ^ (h >>> 15), 0x846ca68b);
    return (h ^ (h >>> 16)) >>> 0;
};

const mod = (v: number, n: number) => ((v % n) + n) % n;

/** 模板按 (列 + 2×行) 轮换，相邻块永远不同；镜像来自哈希。 */
export const blockOrientation = (column: number, row: number) => ({
    template: mod(column + row * 2, TEMPLATES.length),
    mirror: hashBlock(column, row) % MIRRORS.length,
});

const toSlot = (column: number, row: number, index: number, raw: RawSlot): Slot => {
    const [gx, gy, cols, rows] = raw;
    const x = column * BLOCK_W + gx * PITCH;
    const y = row * BLOCK_H + gy * PITCH;
    const width = cols * PITCH - GAP;
    const height = rows * PITCH - GAP;
    return { key: `${column},${row},${index}`, column, row, index, cols, rows, x, y, width, height, cx: x + width / 2, cy: y + height / 2 };
};

const blockCache = new Map<string, Slot[]>();

export const blockSlots = (column: number, row: number): Slot[] => {
    const cacheKey = `${column},${row}`;
    let slots = blockCache.get(cacheKey);
    if (!slots) {
        const { template, mirror } = blockOrientation(column, row);
        slots = TEMPLATES[template].map((raw, index) => toSlot(column, row, index, MIRRORS[mirror](raw)));
        if (blockCache.size > 4096) blockCache.clear();
        blockCache.set(cacheKey, slots);
    }
    return slots;
};

export const slotByKey = (key: string): Slot | null => {
    const [c, r, i] = key.split(',').map(Number);
    if (![c, r, i].every(Number.isInteger) || i < 0 || i >= SLOTS_PER_BLOCK) return null;
    return blockSlots(c, r)[i];
};

/** `expanded` 展开成 6×6 时本块 12 张卡的位置（键不变，只有矩形变）。块外一张不动。 */
export const focusedBlockSlots = (column: number, row: number, expanded: number): Slot[] => {
    const { template, mirror } = blockOrientation(column, row);
    return REFLOWS[template][expanded].map((raw, index) => toSlot(column, row, index, MIRRORS[mirror](raw)));
};

/** 与世界矩形相交的所有 slot，按块、按序号。 */
export const slotsIn = (b: Bounds, limit = 400): Slot[] => {
    const out: Slot[] = [];
    const c0 = Math.floor(b.left / BLOCK_W), c1 = Math.floor(b.right / BLOCK_W);
    const r0 = Math.floor(b.top / BLOCK_H), r1 = Math.floor(b.bottom / BLOCK_H);
    for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) {
        for (const s of blockSlots(c, r)) {
            if (!overlaps(rectBounds(s), b)) continue;
            out.push(s);
            if (out.length >= limit) return out;
        }
    }
    return out;
};

/** 相机缩放：按视口宽分三档。 */
export const scaleFor = (viewportWidth: number) => (viewportWidth < 640 ? 0.52 : viewportWidth < 1100 ? 0.64 : 0.76);
