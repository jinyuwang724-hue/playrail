// 舞台：一面常驻的墙 + 一道缝。相机、缝、让位每帧直接写 DOM；只有裁剪范围变化和换内容时才增删 / 重画卡片。
import { byId, HOME_TABS, type LibraryItem } from '../data/mock';
import { infiniteLayout, itemAt, type WallLayout } from '../engine/content';
import {
    BLOCK_W, focusedBlockSlots, scaleFor, slotByKey, slotsIn, overlaps, rectBounds,
    type Bounds, type Rect, type Slot,
} from '../engine/geometry';
import { MOTION, REFLOW_MS, prefersReducedMotion, reflowEase } from '../engine/motion';
import { findAdjacentSlot, nearestTo, type Direction } from '../engine/nav';
import { planFlip, planWave, waveStagger, type Change, type FlipOrigin } from '../engine/plans';
import {
    SEAM_WIDTH, boundaryColumn, planSeam, revealCenter, seamGeometry, splitOffsets, visibleWorld, worldTransform,
    type Center, type View,
} from '../engine/view';
import { renderSeam, type SeamModel } from './seam';
import { Tile } from './tile';

/** 视口外多渲染的世界距离；离已渲染范围边缘还剩这么多时重新裁剪。 */
const OVERSCAN = 420;
const CULL_MARGIN = 160;
const MAX_TILES = 400;

type Layer = {
    id: string;
    title: string;
    items: LibraryItem[];
    layout: WallLayout;
    seam: 'home' | 'full';
    /** 返回时恢复用。 */
    saved?: { center: Center; focus: string | null };
};

type Tween = { from: number; to: number; start: number; ms: number; ease: (t: number) => number };

export class Stage {
    private readonly root: HTMLElement;
    private readonly field: HTMLDivElement;
    private readonly worlds: [HTMLDivElement, HTMLDivElement];
    private readonly seamEl: HTMLDivElement;
    private readonly seamInner: HTMLDivElement;
    private readonly edgeTab: HTMLDivElement;
    private readonly toastEl: HTMLDivElement;

    view: View = { width: 1, height: 1, scale: 0.76, lineX: 0 };
    center: Center = { x: 0, y: 0 };
    private anchorX = 0;
    private openWidth = 0;
    private contentWidth: number = SEAM_WIDTH.home;

    private stack: Layer[] = [];
    private tabIndex = 0;
    private tiles = new Map<string, Tile>();
    private culled: Bounds | null = null;

    focusKey: string | null = null;
    private expandedKey: string | null = null;
    /** 让位中（或已展开）那一块的实际矩形；块外的卡用模板矩形。 */
    private drawnOverride = new Map<string, Rect>();
    private reflow: { from: Map<string, Rect>; to: Map<string, Rect>; start: number; clear: boolean } | null = null;

    private camX: Tween | null = null;
    private camY: Tween | null = null;
    private seamTween: Tween | null = null;
    /** 右摇杆 / 拖动的平移速度（屏幕 px/s）。 */
    panVelocity = { x: 0, y: 0 };
    private entrance: { start: number; corner: { left: number; top: number }; until: number } | null = null;

    private frameRequested = false;
    private lastFrame = 0;
    onFrame?: (dt: number, now: number) => void;
    /** 有手柄连着时一直跑帧（要轮询按键）。 */
    keepAlive = false;

    constructor(root: HTMLElement) {
        this.root = root;
        root.classList.add('wall-root');
        root.innerHTML = `<div class="field"><div class="world"></div><div class="world"></div></div>
            <div class="seam"><div class="seam-inner"></div></div><div class="edge-tab"></div><div class="vignette"></div><div class="toast"></div>`;
        this.field = root.querySelector('.field')!;
        const w = root.querySelectorAll<HTMLDivElement>('.world');
        this.worlds = [w[0], w[1]];
        this.seamEl = root.querySelector('.seam')!;
        this.seamInner = root.querySelector('.seam-inner')!;
        this.edgeTab = root.querySelector('.edge-tab')!;
        this.toastEl = root.querySelector('.toast')!;
        this.measure();
        window.addEventListener('resize', () => { this.measure(); this.recull(true); this.request(); });
    }

    get reduced() { return prefersReducedMotion(); }
    get layer() { return this.stack[this.stack.length - 1]; }
    get tileCount() { return this.tiles.size; }

    private measure() {
        const r = this.root.getBoundingClientRect();
        this.view = { width: r.width, height: r.height, scale: scaleFor(r.width), lineX: r.width / 2 };
    }

    // ---------------- 启动 ----------------

    start() {
        const tab = HOME_TABS[this.tabIndex];
        const items = tab.items();
        // 墙从世界原点附近开始：缝开在第 0 列块的左边界，相机让开口完整落在屏幕里。
        this.center = { x: BLOCK_W * 0.35, y: 0 };
        const plan = planSeam(this.center, SEAM_WIDTH.home, this.view);
        this.anchorX = plan.anchorX;
        this.center.x = plan.centerX;
        const start = this.slotNearSeam();
        this.stack = [{ id: `home:${tab.id}`, title: '游戏库', items, layout: infiniteLayout(items.length, start), seam: 'home' }];
        this.contentWidth = SEAM_WIDTH.home;
        this.renderSeamContent();
        this.recull(true);
        // 首次进入：整墙按对角线落下，缝同时张开
        const vis = visibleWorld(this.center, this.view, 0, 0);
        this.entrance = { start: performance.now(), corner: { left: vis.left, top: vis.top }, until: performance.now() + MOTION.wave.maxDelay + MOTION.wave.inMs };
        if (!this.reduced) for (const t of this.tiles.values()) t.land(waveStagger(t.slot, this.entrance.corner));
        this.tweenSeam(SEAM_WIDTH.home, 0);
        this.focus(start?.key ?? null, false);
        this.request();
    }

    /** 缝右边离缝最近的一张卡（新层的起点）。 */
    private slotNearSeam(): Slot | null {
        const vis = visibleWorld(this.center, this.view, 0, 0);
        const right = slotsIn(vis).filter(s => s.x > this.anchorX);
        return nearestTo(right, this.anchorX + 200, this.center.y);
    }

    // ---------------- 帧 ----------------

    request() {
        if (this.frameRequested) return;
        this.frameRequested = true;
        requestAnimationFrame(t => this.tick(t));
    }

    private animating(now: number) {
        return Boolean(this.camX || this.camY || this.seamTween || this.reflow || this.panVelocity.x || this.panVelocity.y
            || (this.entrance && now < this.entrance.until));
    }

    /** 最近一帧舞台自己花的脚本时间（ms），给性能读数用。 */
    lastWorkMs = 0;

    private tick(now: number) {
        const workStart = performance.now();
        this.frameRequested = false;
        const dt = this.lastFrame ? Math.min(50, now - this.lastFrame) : 16;
        this.lastFrame = now;
        this.onFrame?.(dt, now);

        if (this.panVelocity.x || this.panVelocity.y) {
            this.camX = this.camY = null;
            this.center = {
                x: this.center.x + (this.panVelocity.x * dt) / 1000 / this.view.scale,
                y: this.center.y + (this.panVelocity.y * dt) / 1000 / this.view.scale,
            };
        }
        const step = (tw: Tween | null): [number | null, Tween | null] => {
            if (!tw) return [null, null];
            const t = Math.min(1, (now - tw.start) / tw.ms);
            const v = tw.from + (tw.to - tw.from) * tw.ease(t);
            return [v, t >= 1 ? null : tw];
        };
        let v: number | null;
        [v, this.camX] = step(this.camX); if (v !== null) this.center.x = v;
        [v, this.camY] = step(this.camY); if (v !== null) this.center.y = v;
        [v, this.seamTween] = step(this.seamTween); if (v !== null) this.openWidth = v;
        if (this.reflow) this.stepReflow(now);

        this.renderFrame();
        this.recull(false);
        if (this.entrance && now >= this.entrance.until) this.entrance = null;
        if (this.animating(now) || this.keepAlive) this.request();
        else this.lastFrame = 0;
        this.lastWorkMs = performance.now() - workStart;
    }

    private renderFrame() {
        const { view, center } = this;
        const geo = seamGeometry(this.anchorX, center, this.openWidth, view);
        const off = splitOffsets(geo.width, view.scale);
        const y = view.height / 2 - center.y * view.scale;
        const baseX = view.lineX - center.x * view.scale;
        this.worlds[0].style.transform = worldTransform(baseX + off.left, y, view.scale);
        this.worlds[1].style.transform = worldTransform(baseX + off.right, y, view.scale);
        const s = this.seamEl.style;
        s.transform = `translate3d(${Math.round(geo.screenX - geo.width / 2)}px,0,0)`;
        s.width = `${Math.max(0, geo.width)}px`;
        s.visibility = geo.width < 1 ? 'hidden' : 'visible';
        const ratio = Math.min(1, geo.width / Math.max(1, this.contentWidth));
        this.seamInner.style.opacity = String(ratio < 0.6 ? 0 : (ratio - 0.6) / 0.4);
        this.seamInner.style.width = `${this.contentWidth}px`;
        this.edgeTab.classList.toggle('show', geo.collapsed);
        this.edgeTab.dataset.side = geo.side;
    }

    // ---------------- 裁剪 ----------------

    private visible(): Bounds {
        return visibleWorld(this.center, this.view, this.openWidth, 0);
    }

    private recull(force: boolean, extra?: Bounds) {
        const vis = this.visible();
        const c = this.culled;
        const pad = OVERSCAN - CULL_MARGIN;
        if (!force && c && vis.left >= c.left - pad && vis.right <= c.right + pad && vis.top >= c.top - pad && vis.bottom <= c.bottom + pad) return;
        let want: Bounds = { left: vis.left - OVERSCAN, right: vis.right + OVERSCAN, top: vis.top - OVERSCAN, bottom: vis.bottom + OVERSCAN };
        if (extra) want = { left: Math.min(want.left, extra.left), right: Math.max(want.right, extra.right), top: Math.min(want.top, extra.top), bottom: Math.max(want.bottom, extra.bottom) };
        this.culled = vis;
        const slots = slotsIn(want, MAX_TILES);
        const keep = new Set<string>();
        const anchorCol = boundaryColumn(this.anchorX);
        const now = performance.now();
        for (const slot of slots) {
            keep.add(slot.key);
            let tile = this.tiles.get(slot.key);
            const side = slot.column < anchorCol ? 0 : 1;
            if (!tile) {
                tile = new Tile(slot, id => byId.get(id));
                const override = this.drawnOverride.get(slot.key);
                if (override) tile.place(override);
                tile.show(this.itemFor(slot));
                if (slot.key === this.focusKey) tile.el.classList.add('is-focused');
                if (slot.key === this.expandedKey) tile.setExpanded(true);
                this.tiles.set(slot.key, tile);
                if (this.entrance && now < this.entrance.until && !this.reduced) {
                    tile.land(Math.max(0, this.entrance.start + waveStagger(slot, this.entrance.corner) - now));
                }
            }
            if (tile.el.parentElement !== this.worlds[side]) this.worlds[side].appendChild(tile.el);
        }
        for (const [key, tile] of this.tiles) {
            if (keep.has(key) || key === this.focusKey || this.drawnOverride.has(key)) continue;
            tile.cancel();
            tile.el.remove();
            this.tiles.delete(key);
        }
    }

    private itemFor(slot: Slot): string | null {
        const idx = itemAt(this.layer.layout, slot);
        return idx === null ? null : this.layer.items[idx]?.id ?? null;
    }

    // ---------------- 相机 ----------------

    tweenCamera(to: Center, ms: number = MOTION.camera.ms) {
        this.panVelocity = { x: 0, y: 0 };
        if (Math.abs(to.x - this.center.x) < 0.5 && Math.abs(to.y - this.center.y) < 0.5) return;
        // 先把目的地并进裁剪范围，飞行途中墙不会空
        this.recull(true, visibleWorld(to, this.view, this.openWidth, 0));
        if (this.reduced) { this.center = { ...to }; this.camX = this.camY = null; this.request(); return; }
        const now = performance.now();
        this.camX = { from: this.center.x, to: to.x, start: now, ms, ease: MOTION.camera.ease };
        this.camY = { from: this.center.y, to: to.y, start: now, ms, ease: MOTION.camera.ease };
        this.request();
    }

    private tweenSeam(to: number, from = this.openWidth) {
        if (this.reduced || Math.abs(to - from) < 0.5) { this.openWidth = to; this.seamTween = null; this.request(); return; }
        this.seamTween = { from, to, start: performance.now(), ms: MOTION.seam.ms, ease: MOTION.seam.ease };
        this.request();
    }

    private drawn(slot: Slot): Rect {
        return this.drawnOverride.get(slot.key) ?? slot;
    }

    private reveal(key: string) {
        const slot = slotByKey(key);
        if (!slot) return;
        const target = revealCenter(this.drawn(slot), this.center, this.view, this.anchorX, SEAM_WIDTH[this.layer.seam]);
        this.tweenCamera(target);
    }

    // ---------------- 焦点 ----------------

    focus(key: string | null, reveal = true) {
        if (this.focusKey) this.tiles.get(this.focusKey)?.el.classList.remove('is-focused');
        this.focusKey = key;
        if (key) {
            this.tiles.get(key)?.el.classList.add('is-focused');
            if (reveal) this.reveal(key);
        }
    }

    private hasContent = (slot: Slot) => this.itemFor(slot) !== null;

    /** 焦点不在屏上（摇杆平移走了）时，从视口中央选最近的一张。 */
    private ensureFocusOnScreen(): boolean {
        const vis = this.visible();
        const cur = this.focusKey ? slotByKey(this.focusKey) : null;
        if (cur && overlaps(rectBounds(this.drawn(cur)), vis)) return false;
        const pick = nearestTo(slotsIn(vis).filter(this.hasContent), this.center.x, this.center.y);
        this.focus(pick?.key ?? null);
        return true;
    }

    move(dir: Direction) {
        if (this.ensureFocusOnScreen()) return;
        const from = slotByKey(this.focusKey!);
        if (!from) return;
        const next = findAdjacentSlot(from, dir, this.hasContent, s => this.drawn(s));
        if (!next) return;
        if (this.expandedKey && next.key !== this.expandedKey) this.collapse(false);
        this.focus(next.key);
    }

    /** A：游戏卡展开聚焦卡；聚焦卡上再按 = 启动；集合卡 = 进入。 */
    activate() {
        if (this.ensureFocusOnScreen()) return;
        const slot = slotByKey(this.focusKey!);
        const id = slot && this.itemFor(slot);
        const item = id ? byId.get(id) : undefined;
        if (!slot || !item) return;
        if (item.kind === 'collection') { this.push(slot, item); return; }
        if (this.expandedKey === slot.key) { this.toast(`启动「${item.title}」（原型里不会真的启动）`); return; }
        this.expand(slot);
    }

    /** B：收起聚焦卡 → 返回上一层。 */
    back() {
        if (this.expandedKey) { this.collapse(true); return; }
        if (this.stack.length > 1) this.pop();
    }

    // ---------------- 聚焦卡（块内让位） ----------------

    private expand(slot: Slot) {
        if (this.expandedKey) this.collapse(false);
        const to = new Map(focusedBlockSlots(slot.column, slot.row, slot.index).map(s => [s.key, s as Rect]));
        this.startReflow(to);
        this.expandedKey = slot.key;
        this.tiles.get(slot.key)?.setExpanded(true);
        const target = revealCenter(to.get(slot.key)!, this.center, this.view, this.anchorX, SEAM_WIDTH[this.layer.seam]);
        this.tweenCamera(target);
    }

    private collapse(reveal: boolean) {
        const key = this.expandedKey;
        if (!key) return;
        const slot = slotByKey(key)!;
        this.tiles.get(key)?.setExpanded(false);
        this.expandedKey = null;
        const base = new Map<string, Rect>();
        for (let i = 0; i < 12; i++) {
            const s = slotByKey(`${slot.column},${slot.row},${i}`)!;
            base.set(s.key, s);
        }
        this.startReflow(base, true);
        if (reveal) {
            const target = revealCenter(slot, this.center, this.view, this.anchorX, SEAM_WIDTH[this.layer.seam]);
            this.tweenCamera(target);
        }
    }

    private startReflow(to: Map<string, Rect>, clearAfter = false) {
        const from = new Map<string, Rect>();
        for (const key of to.keys()) {
            const s = slotByKey(key)!;
            from.set(key, this.tiles.get(key)?.rect ?? this.drawn(s));
        }
        if (this.reduced) {
            for (const [key, r] of to) this.tiles.get(key)?.place(r);
            if (clearAfter) for (const key of to.keys()) this.drawnOverride.delete(key);
            else for (const [key, r] of to) this.drawnOverride.set(key, r);
            this.reflow = null;
            return;
        }
        for (const [key, r] of to) this.drawnOverride.set(key, r);
        this.reflow = { from, to, start: performance.now(), clear: clearAfter };
        this.root.classList.add('is-reflowing');
        this.request();
    }

    private stepReflow(now: number) {
        const rf = this.reflow!;
        const t = Math.min(1, (now - rf.start) / REFLOW_MS);
        const e = reflowEase(t);
        for (const [key, b] of rf.to) {
            const a = rf.from.get(key)!;
            this.tiles.get(key)?.place({
                x: a.x + (b.x - a.x) * e, y: a.y + (b.y - a.y) * e,
                width: a.width + (b.width - a.width) * e, height: a.height + (b.height - a.height) * e,
            });
        }
        if (t >= 1) {
            if (rf.clear) for (const key of rf.to.keys()) this.drawnOverride.delete(key);
            this.reflow = null;
            this.root.classList.remove('is-reflowing');
        }
    }

    // ---------------- 换层 ----------------

    /** 把当前所有卡换到新布局：翻牌（从 origin 向外）或整墙波次。 */
    private swapContent(mode: { kind: 'flip'; origin: FlipOrigin } | { kind: 'wave' }) {
        const changes: Change[] = [];
        for (const tile of this.tiles.values()) changes.push({ slot: tile.slot, from: tile.target, to: this.itemFor(tile.slot) });
        const visible = visibleWorld(this.center, this.view, this.openWidth, OVERSCAN);
        const reduced = this.reduced;
        if (mode.kind === 'flip') {
            const plan = planFlip(changes, mode.origin, visible);
            for (const key of plan.instant) { const t = this.tiles.get(key)!; t.show(this.itemFor(t.slot)); }
            for (const st of plan.steps) { const t = this.tiles.get(st.key)!; t.flip(this.itemFor(t.slot), reduced ? 0 : st.delay, st.direction, reduced); }
        } else {
            const viewport = visibleWorld(this.center, this.view, this.openWidth, 0);
            const plan = planWave(changes, viewport, visible);
            for (const key of plan.instant) { const t = this.tiles.get(key)!; t.show(this.itemFor(t.slot)); }
            for (const st of plan.steps) { const t = this.tiles.get(st.key)!; t.wave(this.itemFor(t.slot), st.outDelay, st.inDelay, reduced); }
        }
    }

    private flipSeam(width: number) {
        const el = this.seamInner;
        const f = MOTION.seamFlip;
        if (this.reduced) { this.contentWidth = width; this.renderSeamContent(); return; }
        const out = el.animate([{ transform: 'perspective(900px) rotateY(0)' }, { transform: 'perspective(900px) rotateY(90deg)' }], { duration: f.outMs, easing: f.outEase, fill: 'forwards' });
        out.onfinish = () => {
            this.contentWidth = width;
            this.renderSeamContent();
            this.renderFrame();
            el.animate([{ transform: 'perspective(900px) rotateY(-90deg)' }, { transform: 'perspective(900px) rotateY(0)' }], { duration: f.inMs, easing: f.inEase });
            out.cancel();
        };
    }

    /** 缝被摇杆推出了屏幕：换层时在视野里重新裂开（此时开口是 0，换锚点不会跳）。 */
    private reopenSeamIfGone(width: number) {
        const geo = seamGeometry(this.anchorX, this.center, this.openWidth, this.view);
        if (!geo.collapsed && geo.width > 1) return this.center;
        const plan = planSeam(this.center, width, this.view);
        this.anchorX = plan.anchorX;
        this.openWidth = 0;
        this.recull(true);
        return { x: plan.centerX, y: this.center.y };
    }

    private push(slot: Slot, item: LibraryItem) {
        if (this.expandedKey) this.collapse(false);
        const cur = this.layer;
        cur.saved = { center: { ...this.center }, focus: this.focusKey };
        const items = (item.children ?? []).map(id => byId.get(id)!).filter(Boolean);
        this.stack.push({ id: item.id, title: item.title, items, layout: infiniteLayout(items.length, slot), seam: 'full' });
        let target = this.reopenSeamIfGone(SEAM_WIDTH.full);
        const plan = planSeam(target, SEAM_WIDTH.full, this.view, this.anchorX);
        target = { x: plan.centerX, y: target.y };
        this.tweenSeam(SEAM_WIDTH.full);
        this.flipSeam(SEAM_WIDTH.full);
        this.swapContent({ kind: 'flip', origin: { kind: 'point', x: slot.cx, y: slot.cy } });
        this.focus(slot.key, false);
        this.tweenCamera(revealCenter(slot, target, this.view, this.anchorX, SEAM_WIDTH.full));
    }

    private pop() {
        if (this.expandedKey) this.collapse(false);
        this.stack.pop();
        const layer = this.layer;
        const saved = layer.saved;
        this.reopenSeamIfGone(SEAM_WIDTH.home);
        this.tweenSeam(SEAM_WIDTH.home);
        this.flipSeam(SEAM_WIDTH.home);
        this.swapContent({ kind: 'flip', origin: { kind: 'line', x: this.anchorX } });
        if (saved) {
            this.focus(saved.focus, false);
            this.tweenCamera(saved.center);
        }
    }

    /** LB / RB：首页换页签，整墙出场 → 入场。 */
    switchTab(delta: number) {
        if (this.stack.length > 1) return;
        if (this.expandedKey) this.collapse(false);
        this.tabIndex = (this.tabIndex + delta + HOME_TABS.length) % HOME_TABS.length;
        const tab = HOME_TABS[this.tabIndex];
        const items = tab.items();
        const start = this.slotNearSeam();
        this.stack[0] = { id: `home:${tab.id}`, title: '游戏库', items, layout: infiniteLayout(items.length, start), seam: 'home' };
        this.renderSeamContent();
        this.swapContent({ kind: 'wave' });
        this.focus(start?.key ?? null, false);
    }

    // ---------------- 缝的内容 ----------------

    private renderSeamContent() {
        const layer = this.layer;
        const model: SeamModel = layer.seam === 'home'
            ? { kind: 'home', title: '游戏库', tabs: HOME_TABS.map(t => t.label), active: this.tabIndex }
            : {
                kind: 'full', title: layer.title, crumbs: ['游戏库'],
                count: layer.items.length,
                hours: layer.items.reduce((s, it) => s + (it.playtimeH ?? 0), 0),
            };
        // 换层翻转期间缝内容按目标宽度排版；没在翻转时宽度也同步
        if (layer.seam === 'home' && this.contentWidth !== SEAM_WIDTH.home && !this.seamTween) this.contentWidth = SEAM_WIDTH.home;
        renderSeam(this.seamInner, model);
        this.edgeTab.textContent = layer.seam === 'home' ? `游戏库 · ${HOME_TABS[this.tabIndex].label}` : layer.title;
    }

    // ---------------- 其他 ----------------

    private toastTimer = 0;
    toast(text: string) {
        this.toastEl.textContent = text;
        this.toastEl.classList.add('show');
        clearTimeout(this.toastTimer);
        this.toastTimer = window.setTimeout(() => this.toastEl.classList.remove('show'), 2200);
    }

    /** 鼠标：点卡 = 聚焦并激活。 */
    clickAt(target: EventTarget | null) {
        const el = (target as HTMLElement | null)?.closest<HTMLElement>('.tile');
        if (!el?.dataset.slot) return;
        const key = el.dataset.slot;
        const slot = slotByKey(key);
        if (!slot || !this.hasContent(slot)) return;
        if (this.focusKey !== key) {
            if (this.expandedKey && this.expandedKey !== key) this.collapse(false);
            this.focus(key);
        }
        this.activate();
    }

    /** 拖动：直接平移相机（屏幕 px）。 */
    panBy(dx: number, dy: number) {
        this.camX = this.camY = null;
        this.center = { x: this.center.x - dx / this.view.scale, y: this.center.y - dy / this.view.scale };
        this.request();
    }
}
