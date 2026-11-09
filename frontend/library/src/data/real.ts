// 真实游戏库：后端扫描出的清单（Steam 库 + 非 Steam 快捷方式 + 封面套件）转成墙上的条目。
import type { ArtKit, HomeTab, LibraryItem } from './mock';

export type ScanGame = {
    id: string;
    title: string;
    kicker: string;
    main: string;
    tail: string;
    /** 开发商 · 年份 */
    sub: string;
    platform: 'steam' | 'other';
    gal: boolean;
    series: string | null;
    /** 上次游玩（unix 秒），不知道就是 null。 */
    last: number | null;
    art: ArtKit;
};

const hueOf = (s: string) => [...s].reduce((sum, ch) => (sum * 31 + ch.charCodeAt(0)) % 360, 7);
const DAY = 86400;

export function fromScan(scan: ScanGame[]) {
    const now = Date.now() / 1000;
    const games: LibraryItem[] = scan.map(g => ({
        id: g.id,
        kind: 'game',
        title: g.title,
        kicker: g.kicker,
        main: g.main,
        tail: g.tail,
        subtitle: g.sub,
        badge: g.platform === 'steam' ? 'STEAM' : 'PC',
        hue: hueOf(g.title),
        platform: g.platform,
        genre: g.gal ? 'Galgame' : undefined,
        lastPlayed: g.last ?? undefined,
        lastPlayedDays: g.last ? Math.max(0, Math.floor((now - g.last) / DAY)) : undefined,
        art: g.art,
    }));
    const byGame = new Map(games.map(g => [g.id, g]));

    const recent = [...games].filter(g => g.lastPlayed).sort((a, b) => b.lastPlayed! - a.lastPlayed!);
    const rest = games.filter(g => !g.lastPlayed);
    const ordered = [...recent, ...rest];

    // 合集封面借一张成员的图，避开首页最前面那几张，免得同一张图在眼前出现两次
    const front = new Set(ordered.slice(0, 6).map(g => g.id));
    const collection = (id: string, title: string, kicker: string, children: LibraryItem[]): LibraryItem => ({
        id, kind: 'collection', title, main: title, kicker, subtitle: `${children.length} 款游戏`, badge: `合集 · ${children.length}`,
        hue: hueOf(title), children: children.map(c => c.id), art: (children.find(c => !front.has(c.id)) ?? children[0])?.art,
    });
    const series = new Map<string, LibraryItem[]>();
    scan.forEach(g => { if (g.series) series.set(g.series, [...(series.get(g.series) ?? []), byGame.get(g.id)!]); });
    const collections: LibraryItem[] = [
        collection('c-gal', 'Galgame', 'VISUAL NOVELS', ordered.filter(g => g.genre === 'Galgame')),
        ...[...series].filter(([, list]) => list.length >= 3).map(([name, list]) => collection(`c-${name}`, name, 'SERIES', list)),
    ];
    const byId = new Map<string, LibraryItem>([...games, ...collections].map(it => [it.id, it]));
    const HOME_TABS: HomeTab[] = [
        { id: 'all', label: '全部', items: () => [...ordered.slice(0, 6), ...collections, ...ordered.slice(6)] },
        { id: 'recent', label: '最近', items: () => recent },
        { id: 'steam', label: 'Steam', items: () => ordered.filter(g => g.platform === 'steam') },
        { id: 'other', label: '非 Steam', items: () => ordered.filter(g => g.platform === 'other') },
        { id: 'gal', label: 'Galgame', items: () => ordered.filter(g => g.genre === 'Galgame') },
    ];
    return { games, collections, byId, HOME_TABS };
}
