// 生成墙面的块模板与聚焦让位表（frontend/library/src/engine/blockData.ts）。
// 块 = 12×8 格、12 张卡、无空洞。模板随机生成后按「尺寸种类多、恰好一张主海报」挑 4 套；
// 让位表：每套模板的每个 slot 展开成 6×6 时，其余 11 张换成更小的尺寸重新铺满本块，
// 在所有铺法里选「卡片移动最少」的那一种（匈牙利算法做卡片与新位置的匹配）。
// 用法：node tools/gen-blocks.mjs [seed]
import { writeFileSync } from 'node:fs';

const W = 12, H = 8, N = 12;
let seed = Number(process.argv[2] ?? 20261013) >>> 0;
const rand = () => { seed = (seed + 0x6d2b79f5) >>> 0; let t = seed; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
const shuffle = a => { for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(rand() * (i + 1)); [a[i], a[j]] = [a[j], a[i]]; } return a; };

// 每套模板用哪 12 种尺寸：与 folia Bravais 的 4 套模板逐套相同（大小搭配一致），卡片位置由这里自己随机排。
const TEMPLATE_SETS = [
    ['6x4', '4x4', '3x4', '3x2', '3x2', '3x2', '3x2', '2x2', '2x2', '2x2', '2x2', '2x2'],
    ['6x4', '6x2', '2x4', '2x4', '2x3', '2x3', '2x3', '2x3', '2x3', '2x3', '2x2', '2x2'],
    ['6x4', '6x3', '4x3', '4x2', '2x3', '2x2', '2x2', '2x2', '2x2', '2x2', '2x2', '2x2'],
    ['4x4', '4x3', '4x3', '6x2', '2x4', '2x4', '2x3', '2x3', '2x2', '2x2', '2x2', '2x2'],
].map(set => set.map(k => k.split('x').map(Number)));
const REFLOW_SIZES = [[2, 2], [2, 3], [3, 2], [2, 4], [4, 2], [3, 4], [4, 3], [4, 4], [6, 2], [2, 6]];

const fits = (g, x, y, w, h) => {
    if (x + w > W || y + h > H) return false;
    for (let yy = y; yy < y + h; yy++) for (let xx = x; xx < x + w; xx++) if (g[yy * W + xx]) return false;
    return true;
};
const fill = (g, x, y, w, h, v) => { for (let yy = y; yy < y + h; yy++) for (let xx = x; xx < x + w; xx++) g[yy * W + xx] = v; };

// ---------- 模板 ----------
/** 用给定的一组尺寸随机铺满 12×8（每种尺寸可横可竖不换向：尺寸就是 folia 的原样）。 */
const randomTemplate = sizes => {
    const g = new Uint8Array(W * H);
    const left = sizes.map(() => true);
    const out = [];
    let budget = 200000;
    const dfs = () => {
        if (--budget < 0) return false;
        const i = g.indexOf(0);
        if (i < 0) return out.length === N;
        const x = i % W, y = (i / W) | 0;
        const tried = new Set();
        for (const j of shuffle(sizes.map((_, k) => k))) {
            if (!left[j]) continue;
            const [w, h] = sizes[j];
            if (tried.has(`${w}x${h}`) || !fits(g, x, y, w, h)) continue;
            tried.add(`${w}x${h}`);
            left[j] = false; fill(g, x, y, w, h, 1); out.push({ x, y, cols: w, rows: h, j });
            if (dfs()) return true;
            left[j] = true; fill(g, x, y, w, h, 0); out.pop();
        }
        return false;
    };
    return dfs() ? out : null;
};
/** 块内贯穿整块的直线越少越好（读起来不像表格）；大卡离块中心远一点，相邻块拼起来不挤在一起。 */
const templateScore = t => {
    let lines = 0;
    for (let x = 1; x < W; x++) if (t.every(s => s.x >= x || s.x + s.cols <= x)) lines++;
    for (let y = 1; y < H; y++) if (t.every(s => s.y >= y || s.y + s.rows <= y)) lines++;
    return -lines * 10 + rand();
};

const picked = TEMPLATE_SETS.map((sizes, ti) => {
    let best = null, bestScore = -Infinity;
    for (let tries = 0; tries < 400; tries++) {
        const t = randomTemplate(sizes);
        if (!t) continue;
        const score = templateScore(t);
        if (score > bestScore) { best = t; bestScore = score; }
    }
    if (!best) throw new Error(`template ${ti}: no cover`);
    // slot 序号按位置排（行优先），与 folia 一样
    return best.sort((a, b) => a.y - b.y || a.x - b.x).map(({ x, y, cols, rows }) => ({ x, y, cols, rows }));
});

// ---------- 让位 ----------
const center = s => ({ x: s.x + s.cols / 2, y: s.y + s.rows / 2 });
const moveCost = (from, to) => {
    const a = center(from), b = center(to);
    return Math.abs(a.x - b.x) + Math.abs(a.y - b.y) + 0.5 * (Math.abs(from.cols - to.cols) + Math.abs(from.rows - to.rows));
};
// 匈牙利算法（n×n，最小化）
const hungarian = cost => {
    const n = cost.length, INF = 1e18;
    const u = new Array(n + 1).fill(0), v = new Array(n + 1).fill(0), p = new Array(n + 1).fill(0), way = new Array(n + 1).fill(0);
    for (let i = 1; i <= n; i++) {
        p[0] = i; let j0 = 0;
        const minv = new Array(n + 1).fill(INF), used = new Array(n + 1).fill(false);
        do {
            used[j0] = true; const i0 = p[j0]; let delta = INF, j1 = 0;
            for (let j = 1; j <= n; j++) if (!used[j]) {
                const cur = cost[i0 - 1][j - 1] - u[i0] - v[j];
                if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                if (minv[j] < delta) { delta = minv[j]; j1 = j; }
            }
            for (let j = 0; j <= n; j++) { if (used[j]) { u[p[j]] += delta; v[j] -= delta; } else minv[j] -= delta; }
            j0 = j1;
        } while (p[j0] !== 0);
        do { const j1 = way[j0]; p[j0] = p[j1]; j0 = j1; } while (j0);
    }
    const assign = new Array(n);
    for (let j = 1; j <= n; j++) assign[p[j] - 1] = j - 1;
    let total = 0; for (let i = 0; i < n; i++) total += cost[i][assign[i]];
    return { assign, total };
};

const EXP = 6;
const reflowFor = (template, k) => {
    const others = template.map((s, i) => ({ s, i })).filter(o => o.i !== k);
    let best = null;
    for (const fx of [0, 2, 3, 4, 6]) for (const fy of [0, 2]) {
        const expanded = { x: fx, y: fy, cols: EXP, rows: EXP };
        const expCost = moveCost(template[k], expanded) * 1.5;
        if (best && expCost >= best.total) continue;
        const g = new Uint8Array(W * H); fill(g, fx, fy, EXP, EXP, 1);
        const rects = [];
        const dfs = () => {
            const i = g.indexOf(0);
            if (i < 0) {
                if (rects.length !== N - 1) return;
                const cost = others.map(o => rects.map(r => moveCost(o.s, r)));
                const { assign, total } = hungarian(cost);
                const sum = total + expCost;
                if (!best || sum < best.total - 1e-9) {
                    const layout = new Array(N);
                    layout[k] = expanded;
                    others.forEach((o, idx) => { layout[o.i] = { ...rects[assign[idx]] }; });
                    best = { total: sum, layout };
                }
                return;
            }
            if (rects.length >= N - 1) return;
            const x = i % W, y = (i / W) | 0;
            for (const [w, h] of REFLOW_SIZES) {
                if (!fits(g, x, y, w, h)) continue;
                fill(g, x, y, w, h, 1); rects.push({ x, y, cols: w, rows: h });
                dfs();
                fill(g, x, y, w, h, 0); rects.pop();
            }
        };
        dfs();
    }
    if (!best) throw new Error(`no reflow for slot ${k}`);
    return best.layout;
};

const reflows = picked.map((t, ti) => t.map((_, k) => { process.stderr.write(`template ${ti} slot ${k}\r`); return reflowFor(t, k); }));
process.stderr.write('\n');

// 校验：每个布局正好铺满 12×8
const check = layout => { const g = new Uint8Array(W * H); for (const s of layout) for (let y = s.y; y < s.y + s.rows; y++) for (let x = s.x; x < s.x + s.cols; x++) { if (g[y * W + x]) throw new Error('overlap'); g[y * W + x] = 1; } if (g.indexOf(0) >= 0) throw new Error('hole'); };
picked.forEach(check); reflows.flat().forEach(check);

const fmt = s => `[${s.x},${s.y},${s.cols},${s.rows}]`;
const body = `// 由 tools/gen-blocks.mjs 生成，不要手改。每个 slot 是 [x, y, cols, rows]（单位：格）。
// TEMPLATES[t][slot]：基础模板；REFLOWS[t][expanded][slot]：slot \`expanded\` 展开成 6×6 时本块 12 张卡的位置。
export type RawSlot = readonly [number, number, number, number];
export const TEMPLATES: readonly (readonly RawSlot[])[] = [
${picked.map(t => `    [${t.map(fmt).join(', ')}],`).join('\n')}
];
export const REFLOWS: readonly (readonly (readonly RawSlot[])[])[] = [
${reflows.map(r => `    [\n${r.map(l => `        [${l.map(fmt).join(', ')}],`).join('\n')}\n    ],`).join('\n')}
];
`;
writeFileSync(new URL('../src/engine/blockData.ts', import.meta.url), body);
console.log(picked.map(t => t.map(s => `${s.cols}x${s.rows}`).join(' ')).join('\n'));
