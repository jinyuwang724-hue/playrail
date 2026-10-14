// 数据入口：页面里有后端注入的 window.__LIBRARY__ 就用真实游戏库，否则用原型假数据。
import * as mock from './mock';
import { fromScan, type ScanGame } from './real';

const scan = (globalThis as { __LIBRARY__?: ScanGame[] }).__LIBRARY__;
const lib = scan?.length ? fromScan(scan) : mock;

export const { games, collections, byId, HOME_TABS } = lib;
export const isReal = !!scan?.length;
export type { ArtKit, HomeTab, LibraryItem } from './mock';
