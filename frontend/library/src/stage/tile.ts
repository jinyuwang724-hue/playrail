// 墙上的一张卡：外框（.tile）只管位置与尺寸，永远不转；内容层（.face）翻牌时绕 Y 轴转。
// 内容只在转到 90° 那一刻重画一次，其余时间只改 transform / 尺寸。
import { GAP, type Rect, type Slot } from '../engine/geometry';
import { MOTION } from '../engine/motion';
import type { ArtKit, LibraryItem } from '../data';

const esc = (s: string) => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));

const CJK = /[\u3040-\u30ff\u3400-\u9fff\uff00-\uffef]/;

type ArtKind = 'p' | 't' | 'h' | 's' | 'x';

/** 按卡片宽高比挑封面套件里的哪张图：细长竖卡 t、竖卡 p、方卡 s / x、横卡 h；缺哪张就退到有的那张。 */
export function pickArt(art: ArtKit, w: number, h: number, expanded: boolean): ArtKind | null {
    const r = w / Math.max(h, 1);
    const want: ArtKind[] = expanded ? ['x', 'h', 'p']
        : r < 0.6 ? ['t', 'p', 'x', 'h']
        : r < 0.9 ? ['p', 't', 'x', 'h']
        : r <= 1.12 ? (Math.max(w, h) >= 500 ? ['x', 's', 'h', 'p'] : ['s', 'x', 'h', 'p'])
        : ['h', 'x', 'p'];
    return want.find(k => art[k]) ?? null;
}

/** 横图在卡里按焦点取景：算出 object-position 的横向百分比。 */
function objectX(art: ArtKit, kind: ArtKind, w: number, h: number) {
    if (kind !== 'h' || !art.ha || art.fx === undefined) return 50;
    const visible = (w / Math.max(h, 1)) / art.ha;
    if (visible >= 1) return 50;
    const left = Math.min(Math.max(art.fx - visible / 2, 0), 1 - visible);
    return Math.round((left / (1 - visible)) * 1000) / 10;
}

const rot = (deg: number) => `perspective(${MOTION.flip.perspective}px) rotateY(${deg}deg)`;
const LIFTED: Keyframe = { transform: `translate3d(0,-${MOTION.wave.lift}px,0) scale(${MOTION.wave.scale})`, opacity: 0 };
const SETTLED: Keyframe = { transform: 'none', opacity: 1 };
const FADED: Keyframe = { opacity: 0 };
const SHOWN: Keyframe = { opacity: 1 };

export class Tile {
    readonly el: HTMLDivElement;
    readonly face: HTMLDivElement;
    slot: Slot;
    rect: Rect;
    /** 此刻画着的内容（id），以及目标内容。 */
    shown: string | null = null;
    target: string | null = null;
    expanded = false;
    private artKind: ArtKind | null = null;
    private img: HTMLImageElement | null = null;
    private anims: Animation[] = [];

    constructor(slot: Slot, private readonly lookup: (id: string) => LibraryItem | undefined) {
        this.slot = slot;
        this.rect = { x: slot.x, y: slot.y, width: slot.width, height: slot.height };
        this.el = document.createElement('div');
        this.el.className = 'tile';
        this.el.dataset.slot = slot.key;
        this.face = document.createElement('div');
        this.face.className = 'face';
        this.el.appendChild(this.face);
        this.place(this.rect);
    }

    place(r: Rect) {
        this.rect = r;
        const s = this.el.style;
        s.transform = `translate(${r.x}px,${r.y}px)`;
        s.width = `${r.width}px`;
        s.height = `${r.height}px`;
        // 悬停 / 焦点放大：四边各外扩一个 gap
        s.setProperty('--pop-x', ((r.width + GAP * 2) / Math.max(r.width, 1)).toFixed(4));
        s.setProperty('--pop-y', ((r.height + GAP * 2) / Math.max(r.height, 1)).toFixed(4));
    }

    /** 立即画成目标内容（不动画）。 */
    show(id: string | null) {
        this.cancel();
        this.target = id;
        this.draw(id);
    }

    setExpanded(on: boolean) {
        if (this.expanded === on) return;
        this.expanded = on;
        this.el.classList.toggle('is-expanded', on);
        this.face.classList.toggle('is-expanded', on);
        this.draw(this.shown);
    }

    private draw(id: string | null) {
        this.shown = id;
        const item = id ? this.lookup(id) : undefined;
        const f = this.face;
        this.img = null;
        this.artKind = null;
        if (!item) {
            f.className = 'face is-wall';
            f.innerHTML = '';
            return;
        }
        const art = item.art;
        const kind = art ? pickArt(art, this.rect.width, this.rect.height, this.expanded) : null;
        f.className = `face kind-${item.kind}${this.expanded ? ' is-expanded' : ''}${kind ? ` has-art art-${kind}` : ''}`;
        f.style.setProperty('--h', String(item.hue));
        if (art?.c) f.style.setProperty('--c', art.c);
        const main = item.main ?? item.title;
        const latin = !CJK.test(main);
        const meta = this.expanded
            ? `<div class="focus-meta">${esc(item.subtitle)}${item.lastPlayedDays !== undefined ? ` <i>·</i> ${item.lastPlayedDays === 0 ? '今天玩过' : `${item.lastPlayedDays} 天前玩过`}` : ''}</div>
               <div class="focus-actions"><span class="btn primary"><b>A</b>开始游戏</span><span class="btn"><b>Y</b>游戏设置</span><span class="btn"><b>B</b>收起</span></div>`
            : `<small>${esc(item.subtitle)}</small>`;
        const cover = kind
            ? `<img class="art-img" alt="" draggable="false" decoding="async" src="${art!.base}${kind}.webp" style="object-position:${objectX(art!, kind, this.rect.width, this.rect.height)}% 50%">`
            : `<div class="art"></div><div class="glyph" style="font-size:${Math.round(Math.min(this.slot.width, this.slot.height) * (this.expanded ? 0.8 : 0.92))}px">${esc([...item.title][0])}</div>`;
        f.innerHTML = cover + `<div class="shade"></div><div class="tint"></div><span class="badge">${esc(item.badge)}</span>`
            + `<div class="copy">${item.kicker ? `<span class="kicker">${esc(item.kicker)}</span>` : ''}`
            + `<strong class="${latin ? 'latin' : 'cjk'}">${esc(main)}</strong>${item.tail ? `<em class="tail">${esc(item.tail)}</em>` : ''}${meta}</div>`;
        this.img = kind ? f.querySelector('img') : null;
        this.artKind = kind;
    }

    /** 尺寸定下来之后（让位动画结束）按新宽高比换图；同一张图只更新取景。 */
    fitArt() {
        const item = this.shown ? this.lookup(this.shown) : undefined;
        if (!item?.art || !this.img) return;
        const { width: w, height: h } = this.rect;
        const kind = pickArt(item.art, w, h, this.expanded);
        if (!kind) return;
        if (kind !== this.artKind) {
            this.face.classList.replace(`art-${this.artKind}`, `art-${kind}`);
            this.artKind = kind;
            this.img.src = `${item.art.base}${kind}.webp`;
        }
        this.img.style.objectPosition = `${objectX(item.art, kind, w, h)}% 50%`;
    }

    cancel() {
        for (const a of this.anims) a.cancel();
        this.anims = [];
    }

    /** 翻牌：转到 90° → 换内容 → 从另一侧转回来。 */
    flip(to: string | null, delay: number, direction: -1 | 1, reduced: boolean) {
        this.cancel();
        this.target = to;
        const f = MOTION.flip;
        const out = reduced
            ? this.face.animate([SHOWN, FADED], { duration: MOTION.reducedFadeMs / 2, easing: 'ease-in', fill: 'forwards' })
            : this.face.animate([{ transform: rot(0) }, { transform: rot(90 * direction) }], { duration: f.outMs, delay, easing: f.outEase, fill: 'forwards' });
        this.anims.push(out);
        out.onfinish = () => {
            if (this.target !== to) return;
            this.draw(to);
            const back = reduced
                ? this.face.animate([FADED, SHOWN], { duration: MOTION.reducedFadeMs / 2, easing: 'ease-out' })
                : this.face.animate([{ transform: rot(-90 * direction) }, { transform: rot(0) }], { duration: f.inMs, easing: f.inEase });
            out.cancel();
            this.anims = [back];
        };
    }

    /** 整墙波次：抬起淡出 → 换内容 → 等到自己的时刻落回。 */
    wave(to: string | null, outDelay: number, inDelay: number, reduced: boolean) {
        if (reduced) { this.flip(to, 0, 1, true); return; }
        this.cancel();
        this.target = to;
        const w = MOTION.wave;
        const out = this.face.animate([SETTLED, LIFTED], { duration: w.outMs, delay: outDelay, easing: w.outEase, fill: 'forwards' });
        this.anims.push(out);
        out.onfinish = () => {
            if (this.target !== to) return;
            this.draw(to);
            const back = this.face.animate([LIFTED, SETTLED], {
                duration: w.inMs, delay: Math.max(0, inDelay - outDelay - w.outMs), easing: w.inEase, fill: 'backwards',
            });
            out.cancel();
            this.anims = [back];
        };
    }

    /** 新挂上的卡在整墙入场期间也要落回（保持抬起到自己的时刻）。 */
    land(delay: number) {
        this.cancel();
        const w = MOTION.wave;
        this.anims.push(this.face.animate([LIFTED, SETTLED], { duration: w.inMs, delay, easing: w.inEase, fill: 'backwards' }));
    }
}
