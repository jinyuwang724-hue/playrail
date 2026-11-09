// 视口 ↔ 世界的换算，缝（信息条 / 书脊）的几何，以及「最小移动把一张卡露出来」。
// 相机的真源是「视图中心」：缝基准线（屏幕上 lineX、半高处）下面的那个世界点。
// 缝只开在块边界上（没有卡跨过两块之间的缝隙），墙按缝分成左右两半，各让出半个开口 + 半个 gap。
import { BLOCK_W, GAP, type Bounds, type Rect } from './geometry';

export type View = { width: number; height: number; scale: number; lineX: number };
export type Center = { x: number; y: number };

/** 开口离视口边缘保留的屏幕 px。 */
export const SEAM_EDGE = 14;
/** 选块边界时「离基准线的距离」相对「相机要让的距离」的权重。 */
const SEAM_LINE_WEIGHT = 0.15;

export const SEAM_WIDTH = { home: 120, full: 300, spine: 64 } as const;

/** 块 `column` 左边那道缝隙的中线（世界 x）。 */
export const boundaryX = (column: number) => column * BLOCK_W - GAP / 2;
export const boundaryColumn = (x: number) => Math.round((x + GAP / 2) / BLOCK_W);

/** 两半各自的屏幕偏移：半个开口 + 半个 gap（开口很窄时 gap 也跟着收）。 */
export const splitOffsets = (width: number, scale: number) => {
    const pad = (GAP / 2) * scale * Math.min(1, width / 16);
    return { left: -width / 2 - pad, right: width / 2 + pad };
};

/** 选缝开在哪：相机附近的三条块边界，各自让相机挪最少的距离把整个开口放进屏幕，分数最低的胜出。 */
export const planSeam = (center: Center, openWidth: number, view: View, prefer?: number) => {
    const need = openWidth / 2 + SEAM_EDGE;
    const base = boundaryColumn(center.x);
    const candidates = prefer !== undefined ? [prefer] : [base - 1, base, base + 1].map(boundaryX);
    let best = { anchorX: candidates[0], centerX: center.x }, bestScore = Infinity;
    for (const x of candidates) {
        const screenX = view.lineX + (x - center.x) * view.scale;
        const fitX = Math.max(need, Math.min(view.width - need, screenX));
        const nextCenterX = x - (fitX - view.lineX) / view.scale;
        const score = Math.abs(nextCenterX - center.x) * view.scale + Math.abs(fitX - view.lineX) * SEAM_LINE_WEIGHT;
        if (score < bestScore) { bestScore = score; best = { anchorX: x, centerX: nextCenterX }; }
    }
    return best;
};

/** 缝跟着世界走，靠近视口边时开口收窄，出屏就合上（collapsed = 本该开着却被挤没了，显示边缘标签）。 */
export const seamGeometry = (anchorX: number, center: Center, openWidth: number, view: View) => {
    const screenX = view.lineX + (anchorX - center.x) * view.scale;
    const room = Math.min(screenX, view.width - screenX) - SEAM_EDGE;
    const width = Math.max(0, Math.min(openWidth, room * 2));
    return { screenX, width, collapsed: openWidth > 1 && width < 2, side: screenX < view.lineX ? 'left' as const : 'right' as const };
};

/** 此刻看得见的世界范围（缝张开时左右各放宽一个开口）。 */
export const visibleWorld = (center: Center, view: View, seamWidth = 0, overscan = 0): Bounds => ({
    left: center.x - (view.lineX + seamWidth) / view.scale - overscan,
    right: center.x + (view.width - view.lineX + seamWidth) / view.scale + overscan,
    top: center.y - view.height / 2 / view.scale - overscan,
    bottom: center.y + view.height / 2 / view.scale + overscan,
});

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

/**
 * 把一个世界矩形完整露出来的最小相机移动（屏幕留 pad）；缝能同时留在屏上时优先留着，两者冲突时矩形优先。
 */
export const revealCenter = (
    rect: Rect,
    center: Center,
    view: View,
    anchorX: number | null,
    seamWidth: number,
    pad = 24,
    bottomInset = pad,
): Center => {
    const s = view.scale;
    const hasSeam = anchorX !== null && seamWidth > 1;
    const offsets = hasSeam ? splitOffsets(seamWidth, s) : { left: 0, right: 0 };
    const offset = anchorX !== null && rect.x + rect.width / 2 < anchorX ? offsets.left : offsets.right;
    const fitLow = rect.x + rect.width - (view.width - pad - view.lineX - offset) / s;
    const fitHigh = rect.x - (pad - view.lineX - offset) / s;
    let x: number;
    const lo = Math.min(fitLow, fitHigh), hi = Math.max(fitLow, fitHigh);
    if (hasSeam) {
        const need = seamWidth / 2 + SEAM_EDGE;
        const seamLow = anchorX! - (view.width - need - view.lineX) / s;
        const seamHigh = anchorX! + (view.lineX - need) / s;
        const a = Math.max(fitLow, seamLow), b = Math.min(fitHigh, seamHigh);
        x = a <= b ? clamp(center.x, a, b) : clamp(center.x, lo, hi);
    } else {
        x = clamp(center.x, lo, hi);
    }
    const yLow = rect.y + rect.height - (view.height / 2 - bottomInset) / s;
    const yHigh = rect.y - (pad - view.height / 2) / s;
    const y = yLow <= yHigh ? clamp(center.y, yLow, yHigh) : rect.y + rect.height / 2;
    return { x, y };
};

/** 世界层的 transform，平移对齐到整设备像素：亚像素变化会让 Chromium 每帧重新栅格化整面墙。 */
export const worldTransform = (x: number, y: number, scale: number) => {
    const dpr = window.devicePixelRatio || 1;
    const snap = (v: number) => Math.round(v * dpr) / dpr;
    return `translate3d(${snap(x)}px,${snap(y)}px,0) scale(${scale})`;
};
