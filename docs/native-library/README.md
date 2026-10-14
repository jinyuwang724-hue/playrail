# 游戏库原生版：对照与实测（2026-10-14，MSI Claw 8，1920×1200 @150%，60 Hz）

代码在 `frontend/library-native/`。WinUI 3 只做窗口，墙面全部画在 Microsoft.UI.Composition 视觉层上，
相机 / 缝 / 让位 / 翻牌 / 波次动画都在合成线程跑；NativeAOT，自包含 MSIX，用 CN=ClawToolbox 签名。

## 截图（上 = 网页版，下 = 原生版；同一份数据、同一套封面）

- `cmp-expand.jpg`：A 展开 6×6 聚焦卡 + 块内让位
- `cmp-collection2.jpg`：进入「Galgame」合集（缝变成信息条，墙翻牌）
- `cmp-styleb.jpg`：X 切到标题样式 B「极简黑体」
- `cmp-home-titles.jpg`：首页标题排版放大对照
- `web-home.jpg` / `native-home.jpg`：首页整屏

网页版截图用 `tools/web-preview.mjs`（Node 自带去类型直接跑 `frontend/library/src`）+ `tools/web-shot.mjs`（无头 Edge，1280×800 @1.5）。

## 实测

- 冷启动（AOT MSIX，封面缓存已生成）：进程启动 → 第一帧 483–640 ms（中位约 525 ms），屏上封面全部解码约 0.75–1.0 s
- 首次启动另需约 10–11 s 在后台从 Steam 原图生成封面套件（期间墙正常显示，封面逐张出现）
- 帧间隔（`bench.json`，DWM 合成时间戳）：右摇杆平移、十字键走位、展开 / 收起、换页签波次、进出合集全部 p99 = 16.67 ms（60 Hz 每帧都到）；整轮只有 1 个 33 ms 帧
- CPU：静止 0.05–0.2 %，动画中 1–2.3 %（占全部核心）；GPU 3D：静止 0 %，动画中 2–5 %
- 内存：启动后工作集约 210 MB（私有 155 MB），来回翻几个页签后约 340 MB（封面缓存）

## 与网页版的差别

- 集合信息条的「共 N 小时」读的是本机 Steam 时长（网页版没读，显示 0）
- 「Narcissu 1st & 2nd」显示 &（网页版把 `&amp;` 原样显示了）
- 新的让位不会把上一次没走完的卡冻在半路（网页版快速连按时会）
- 颗粒噪点是近似（网页是 soft-light 混合）；边缘标签没有滑入动画；鼠标悬停效果没做
- 启动游戏仍是占位提示（与网页版相同）
