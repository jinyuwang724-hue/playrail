// 输入：手柄（标准映射，WebView2 下就是 XInput）、键盘、鼠标拖动。
// 十字键 / 左摇杆 = 方向导航（按住按间隔连走），右摇杆 = 平移相机，A 打开，B 返回，LB / RB 换页签。
import type { Direction } from './engine/nav';
import { MOTION } from './engine/motion';
import type { Stage } from './stage/stage';

const REPEAT_FIRST_MS = 320;
const REPEAT_MS = 110;
const STICK_DEAD = 0.35;
const PAN_DEAD = 0.14;
/** 右摇杆推满时的平移速度（屏幕 px/s）。 */
const PAN_SPEED = 1700;

export const attachInput = (stage: Stage, onToggleHud: () => void) => {
    // ---------- 键盘 ----------
    const keyDir: Record<string, Direction> = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' };
    window.addEventListener('keydown', e => {
        const dir = keyDir[e.key];
        if (dir) { stage.move(dir); e.preventDefault(); return; }
        if (e.repeat) return;
        if (e.key === 'Enter' || e.key === ' ') stage.activate();
        else if (e.key === 'Escape' || e.key === 'Backspace') stage.back();
        else if (e.key === 'q' || e.key === 'Q' || e.key === 'PageUp') stage.switchTab(-1);
        else if (e.key === 'e' || e.key === 'E' || e.key === 'PageDown') stage.switchTab(1);
        else if (e.key === 'f' || e.key === 'F') onToggleHud();
        else if (e.key === 't' || e.key === 'T') toggleTitleStyle(stage);
        else if (e.key === 'r' || e.key === 'R') {
            const root = document.documentElement;
            root.dataset.reduceMotion = root.dataset.reduceMotion === '1' ? '' : '1';
            stage.toast(root.dataset.reduceMotion === '1' ? '已降低动态效果' : '已恢复完整动效');
        }
    });

    // ---------- 鼠标 / 触屏拖动（松手带惯性） ----------
    const field = document.querySelector<HTMLElement>('.field')!;
    let drag: { x: number; y: number; t: number; vx: number; vy: number; moved: number; id: number } | null = null;
    field.addEventListener('pointerdown', e => {
        drag = { x: e.clientX, y: e.clientY, t: e.timeStamp, vx: 0, vy: 0, moved: 0, id: e.pointerId };
        field.setPointerCapture(e.pointerId);
    });
    field.addEventListener('pointermove', e => {
        if (!drag || e.pointerId !== drag.id) return;
        const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
        const dt = Math.max(1, e.timeStamp - drag.t);
        drag.vx = (dx / dt) * 1000; drag.vy = (dy / dt) * 1000;
        drag.x = e.clientX; drag.y = e.clientY; drag.t = e.timeStamp;
        drag.moved += Math.abs(dx) + Math.abs(dy);
        if (drag.moved > 4) { field.classList.add('dragging'); stage.panBy(dx, dy); }
    });
    const release = (e: PointerEvent) => {
        if (!drag || e.pointerId !== drag.id) return;
        const d = drag; drag = null;
        field.classList.remove('dragging');
        if (d.moved <= 4) { stage.clickAt(e.target); return; }
        if (e.timeStamp - d.t > 80) return;
        // 惯性：滑行距离 = 松手速度 × 0.3（power），按时间常数 280ms 指数衰减；速度上限 4000 px/s
        const speed = Math.hypot(d.vx, d.vy);
        if (speed < 40) return;
        const k = (Math.min(speed, 4000) / speed) * (0.3 / 0.28);
        let vx = d.vx * k, vy = d.vy * k;
        let last = performance.now();
        const glide = (now: number) => {
            const dt = now - last; last = now;
            const decay = Math.exp(-dt / 280);
            stage.panBy(vx * dt / 1000, vy * dt / 1000);
            vx *= decay; vy *= decay;
            if (Math.hypot(vx, vy) > 8 && !drag) requestAnimationFrame(glide);
        };
        requestAnimationFrame(glide);
    };
    field.addEventListener('pointerup', release);
    field.addEventListener('pointercancel', release);
    field.addEventListener('wheel', e => { e.preventDefault(); stage.panBy(-e.deltaX, -e.deltaY); }, { passive: false });

    // ---------- 手柄 ----------
    const held = new Map<string, number>();
    const pressedBefore = new Set<number>();
    let smoothPan = { x: 0, y: 0 };

    const repeatDir = (name: Direction, active: boolean, now: number) => {
        const key = `dir:${name}`;
        if (!active) { held.delete(key); return; }
        const next = held.get(key);
        if (next === undefined) { stage.move(name); held.set(key, now + REPEAT_FIRST_MS); }
        else if (now >= next) { stage.move(name); held.set(key, now + REPEAT_MS); }
    };

    stage.onFrame = (dt, now) => {
        const pads = navigator.getGamepads?.() ?? [];
        const pad = [...pads].find(p => p && p.connected);
        if (!pad) return;
        const b = (i: number) => Boolean(pad.buttons[i]?.pressed);
        const edge = (i: number) => { const on = b(i); const was = pressedBefore.has(i); if (on) pressedBefore.add(i); else pressedBefore.delete(i); return on && !was; };
        const [lx = 0, ly = 0, rx = 0, ry = 0] = pad.axes;
        repeatDir('up', b(12) || ly < -STICK_DEAD, now);
        repeatDir('down', b(13) || ly > STICK_DEAD, now);
        repeatDir('left', b(14) || lx < -STICK_DEAD, now);
        repeatDir('right', b(15) || lx > STICK_DEAD, now);
        if (edge(0)) stage.activate();
        if (edge(1)) stage.back();
        if (edge(4)) stage.switchTab(-1);
        if (edge(5)) stage.switchTab(1);
        if (edge(3)) stage.toast('游戏设置（原型里还没有）');
        if (edge(2)) toggleTitleStyle(stage);
        if (edge(8)) onToggleHud();
        // 右摇杆：死区外按平方曲线给速度，再用与滚轮同一类的指数平滑追上去
        const shape = (v: number) => (Math.abs(v) < PAN_DEAD ? 0 : Math.sign(v) * ((Math.abs(v) - PAN_DEAD) / (1 - PAN_DEAD)) ** 2);
        const want = { x: shape(rx) * PAN_SPEED, y: shape(ry) * PAN_SPEED };
        const f = 1 - Math.exp(-dt / MOTION.panTimeConstantMs);
        smoothPan = { x: smoothPan.x + (want.x - smoothPan.x) * f, y: smoothPan.y + (want.y - smoothPan.y) * f };
        if (Math.hypot(smoothPan.x, smoothPan.y) < 4 && !want.x && !want.y) smoothPan = { x: 0, y: 0 };
        stage.panVelocity = smoothPan;
    };

    const syncAlive = () => {
        stage.keepAlive = [...(navigator.getGamepads?.() ?? [])].some(p => p && p.connected);
        stage.request();
    };
    window.addEventListener('gamepadconnected', syncAlive);
    window.addEventListener('gamepaddisconnected', syncAlive);
    syncAlive();
};

/** 标题样式：默认 D「电影字幕」，T 键 / 手柄 X 切到备选 B「极简黑体」再切回。 */
function toggleTitleStyle(stage: Stage) {
    const root = document.documentElement;
    root.dataset.title = root.dataset.title === 'b' ? '' : 'b';
    stage.toast(root.dataset.title === 'b' ? '标题样式：极简黑体' : '标题样式：电影字幕');
}
