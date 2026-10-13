// 缝里的内容：首页窄缝（竖排标题 + 竖排页签），集合页完整信息条（面包屑、竖排大标题、统计、按键提示）。
export type SeamModel =
    | { kind: 'home'; title: string; tabs: string[]; active: number }
    | { kind: 'full'; title: string; crumbs: string[]; count: number; hours: number };

const esc = (s: string) => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));

export const renderSeam = (el: HTMLElement, m: SeamModel) => {
    if (m.kind === 'home') {
        el.className = 'seam-inner narrow';
        el.innerHTML = `<div class="vtitle">${esc(m.title)}</div>
            <div class="hint-key">LB</div>
            <div class="vtabs">${m.tabs.map((t, i) => `<span class="${i === m.active ? 'on' : ''}">${esc(t)}</span>`).join('')}</div>
            <div class="hint-key">RB</div>`;
        return;
    }
    el.className = 'seam-inner full';
    el.innerHTML = `<div class="crumbs"><span class="back">‹</span>${m.crumbs.map(esc).join(' › ')}</div>
        <div class="vtitle-wrap"><div class="quote">“</div><div class="vtitle">${esc(m.title)}</div></div>
        <div class="rule"></div>
        <div class="meta"><strong>${m.count}</strong> 款游戏 · 共 <strong>${m.hours}</strong> 小时</div>
        <div class="keys"><span><b>A</b>打开</span><span><b>B</b>返回</span></div>`;
};
