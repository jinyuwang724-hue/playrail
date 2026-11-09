// 动效参数与缓动函数（数值照 folia Bravais 的设计）。只依赖浏览器自带的 WAAPI / CSS，不用动画库。

/** 三次贝塞尔缓动（与 CSS cubic-bezier 相同的定义），返回 t → 进度。 */
export const cubicBezier = (x1: number, y1: number, x2: number, y2: number) => {
    const cx = 3 * x1, bx = 3 * (x2 - x1) - cx, ax = 1 - cx - bx;
    const cy = 3 * y1, by = 3 * (y2 - y1) - cy, ay = 1 - cy - by;
    const sx = (t: number) => ((ax * t + bx) * t + cx) * t;
    const sy = (t: number) => ((ay * t + by) * t + cy) * t;
    const dx = (t: number) => (3 * ax * t + 2 * bx) * t + cx;
    const solve = (x: number) => {
        let t = x;
        for (let i = 0; i < 8; i++) {
            const e = sx(t) - x;
            if (Math.abs(e) < 1e-6) return t;
            const d = dx(t);
            if (Math.abs(d) < 1e-6) break;
            t -= e / d;
        }
        let lo = 0, hi = 1;
        t = x;
        while (hi - lo > 1e-6) { if (sx(t) < x) lo = t; else hi = t; t = (lo + hi) / 2; }
        return t;
    };
    return (x: number) => (x <= 0 ? 0 : x >= 1 ? 1 : sy(solve(x)));
};

export const MOTION = {
    /** 相机补间（焦点露出、换层定位）。 */
    camera: { ms: 420, ease: cubicBezier(0.22, 1, 0.36, 1) },
    /** 缝开口补间。 */
    seam: { ms: 360, ease: cubicBezier(0.65, 0, 0.35, 1) },
    /** 翻牌：转到 90° 换内容，再转回来。 */
    flip: {
        outMs: 150, outEase: 'cubic-bezier(.55,0,.9,.45)',
        inMs: 210, inEase: 'cubic-bezier(.2,.7,.25,1)',
        staggerPerCell: 18, maxDelay: 420, perspective: 1400,
    },
    /** 整墙出场 → 入场（换页签）。 */
    wave: {
        lift: 90, scale: 0.88,
        outMs: 280, outEase: 'cubic-bezier(.4,0,1,1)',
        inMs: 420, inEase: 'cubic-bezier(.2,.9,.3,1.04)',
        staggerPerCell: 30, maxDelay: 340,
    },
    /** 缝内容换层时的翻转。 */
    seamFlip: { outMs: 160, outEase: 'ease-in', inMs: 240, inEase: 'cubic-bezier(.2,.7,.25,1)' },
    /** 聚焦卡按钮排：卡起步后多久出现、多长、从下方多少 px。 */
    focusActions: { delayMs: 140, ms: 240, rise: 12 },
    /** 降低动态效果时，翻牌与波次换成的淡出淡入总时长。 */
    reducedFadeMs: 180,
    /** 右摇杆平移的指数平滑（与滚轮同一类曲线）。 */
    panTimeConstantMs: 80,
} as const;

/**
 * 让位弹簧（stiffness 300、damping 34、mass 1，阻尼比约 0.98，不过冲）。
 * 线性系统：位移多大进度曲线都一样，所以只算一条归一化曲线，时长取「一整块宽度的位移」走到静止的时刻。
 */
const springCurve = (() => {
    const k = 300, c = 34, distance = 12 * 136 - 8;
    const dt = 1 / 1000;
    let x = 0, v = 0, t = 0;
    const samples: number[] = [0];
    // 静止判据：剩余 ≤ 0.5 世界 px 且速度 ≤ 2 px/s
    for (;;) {
        const a = -k * (x - distance) - c * v;
        v += a * dt; x += v * dt; t += dt;
        samples.push(x / distance);
        if (Math.abs(distance - x) <= 0.5 && Math.abs(v) <= 2) break;
        if (t > 3) break;
    }
    samples[samples.length - 1] = 1;
    return { durationMs: Math.round(t * 1000), samples };
})();

export const REFLOW_MS = springCurve.durationMs;

/** 让位进度：t ∈ [0,1] 是时间比例。 */
export const reflowEase = (t: number) => {
    if (t <= 0) return 0;
    if (t >= 1) return 1;
    const s = springCurve.samples;
    const p = t * (s.length - 1);
    const i = Math.floor(p);
    return s[i] + (s[i + 1] - s[i]) * (p - i);
};

/** 降低动态效果：系统设置或本地开关。 */
export const prefersReducedMotion = () =>
    document.documentElement.dataset.reduceMotion === '1' || matchMedia('(prefers-reduced-motion: reduce)').matches;
