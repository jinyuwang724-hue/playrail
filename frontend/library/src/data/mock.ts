// 原型用的假数据：游戏名是真的，封面是按色相生成的渐变（真机版换成 Steam / Xbox 本地缓存的封面）。
export type Platform = 'steam' | 'xbox' | 'other';

export type LibraryItem = {
    id: string;
    kind: 'game' | 'collection';
    title: string;
    /** 第二行：平台 · 时长，或集合的游戏数。 */
    subtitle: string;
    badge: string;
    hue: number;
    platform?: Platform;
    playtimeH?: number;
    lastPlayedDays?: number;
    genre?: string;
    /** 集合里的游戏 id。 */
    children?: string[];
    /** 排版标题：小字原名 / 主标题 / 副标题。没有时用 title。 */
    kicker?: string;
    main?: string;
    tail?: string;
    art?: ArtKit;
    lastPlayed?: number;
};

/** 封面套件（tools/art-kit.py 产出）：base 下的 v/n/h/s/x/p.webp，fx 是背景图的水平焦点，ha 是背景图宽高比，c 是主色。 */
export type ArtKit = { base: string; v?: boolean; n?: boolean; p?: boolean; h?: boolean; s?: boolean; x?: boolean; fx?: number; ha?: number; c?: string };

type Seed = [title: string, platform: Platform, genre: string, playtimeH: number, lastPlayedDays: number];

const GAMES: Seed[] = [
    ['鸣潮', 'other', '角色扮演', 312, 0],
    ['绝区零', 'other', '动作', 146, 1],
    ['艾尔登法环', 'steam', '角色扮演', 188, 6],
    ['黑神话：悟空', 'steam', '动作', 74, 12],
    ['博德之门 3', 'steam', '角色扮演', 131, 21],
    ['赛博朋克 2077', 'steam', '角色扮演', 96, 40],
    ['怪物猎人 荒野', 'steam', '动作', 58, 3],
    ['只狼：影逝二度', 'steam', '动作', 63, 90],
    ['空洞骑士', 'steam', '独立', 41, 120],
    ['哈迪斯 II', 'steam', '独立', 37, 9],
    ['星露谷物语', 'steam', '独立', 210, 30],
    ['戴森球计划', 'steam', '策略', 88, 55],
    ['文明 VI', 'steam', '策略', 154, 70],
    ['女神异闻录 5 皇家版', 'steam', '角色扮演', 102, 150],
    ['最终幻想 VII 重生', 'steam', '角色扮演', 79, 18],
    ['生化危机 4', 'steam', '动作', 29, 200],
    ['死亡细胞', 'steam', '独立', 52, 80],
    ['杀戮尖塔', 'steam', '独立', 140, 14],
    ['蔚蓝', 'steam', '独立', 22, 300],
    ['双人成行', 'steam', '动作', 18, 240],
    ['极限竞速：地平线 5', 'xbox', '竞速', 67, 5],
    ['光环：士官长合集', 'xbox', '射击', 44, 60],
    ['星空', 'xbox', '角色扮演', 35, 110],
    ['微软模拟飞行 2024', 'xbox', '模拟', 12, 33],
    ['战争机器 5', 'xbox', '射击', 21, 180],
    ['盗贼之海', 'xbox', '冒险', 26, 45],
    ['奥日与萤火意志', 'xbox', '独立', 19, 260],
    ['帝国时代 IV', 'xbox', '策略', 48, 95],
    ['白色相簿 2', 'other', 'Galgame', 64, 7],
    ['千恋＊万花', 'other', 'Galgame', 38, 2],
    ['魔女的夜宴', 'other', 'Galgame', 33, 25],
    ['星光咖啡馆与死神之蝶', 'other', 'Galgame', 27, 16],
    ['美好的每一天 ～不连续的存在～', 'other', 'Galgame', 41, 130],
    ['命运石之门', 'steam', 'Galgame', 36, 210],
    ['樱之诗', 'other', 'Galgame', 52, 48],
    ['原神', 'other', '角色扮演', 420, 4],
    ['崩坏：星穹铁道', 'other', '角色扮演', 230, 2],
    ['明日方舟：终末地', 'other', '策略', 15, 1],
    ['街头霸王 6', 'steam', '格斗', 31, 28],
    ['铁拳 8', 'steam', '格斗', 14, 75],
    ['荒野大镖客：救赎 2', 'steam', '冒险', 85, 160],
    ['巫师 3：狂猎', 'steam', '角色扮演', 120, 400],
    ['传说之下', 'steam', '独立', 9, 500],
    ['泰拉瑞亚', 'steam', '独立', 160, 66],
];

const PLATFORM_LABEL: Record<Platform, string> = { steam: 'STEAM', xbox: 'XBOX', other: 'PC' };

const hueOf = (s: string) => [...s].reduce((sum, ch) => (sum * 31 + ch.charCodeAt(0)) % 360, 7);

export const games: LibraryItem[] = GAMES.map(([title, platform, genre, playtimeH, lastPlayedDays], i) => ({
    id: `g${i}`,
    kind: 'game',
    title,
    subtitle: `${genre} · ${playtimeH} 小时`,
    badge: PLATFORM_LABEL[platform],
    hue: hueOf(title),
    platform,
    playtimeH,
    lastPlayedDays,
    genre,
}));

const collection = (id: string, title: string, pick: (g: LibraryItem) => boolean): LibraryItem => {
    const children = games.filter(pick).map(g => g.id);
    return { id, kind: 'collection', title, subtitle: `${children.length} 款游戏`, badge: `合集 · ${children.length}`, hue: hueOf(title), children };
};

export const collections: LibraryItem[] = [
    collection('c-gal', 'Galgame', g => g.genre === 'Galgame'),
    collection('c-rpg', '角色扮演', g => g.genre === '角色扮演'),
    collection('c-act', '动作', g => g.genre === '动作' || g.genre === '格斗'),
    collection('c-indie', '独立游戏', g => g.genre === '独立'),
];

export const byId = new Map<string, LibraryItem>([...games, ...collections].map(it => [it.id, it]));

export type HomeTab = { id: string; label: string; items: () => LibraryItem[] };

const recent = () => [...games].sort((a, b) => (a.lastPlayedDays ?? 0) - (b.lastPlayedDays ?? 0));

export const HOME_TABS: HomeTab[] = [
    { id: 'all', label: '全部', items: () => [...recent().slice(0, 6), ...collections, ...recent().slice(6)] },
    { id: 'recent', label: '最近', items: () => recent().slice(0, 16) },
    { id: 'steam', label: 'Steam', items: () => recent().filter(g => g.platform === 'steam') },
    { id: 'xbox', label: 'Xbox', items: () => recent().filter(g => g.platform === 'xbox') },
    { id: 'other', label: '其他', items: () => recent().filter(g => g.platform === 'other') },
];
