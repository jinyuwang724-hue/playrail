# 交给 Codex：游戏模式剩下的两个问题（2026-11-06）

基线：游戏库 0.1.243（main），外壳 ClawShell 1.0.9（`native/ClawShell`），游戏模式 =「保留 explorer」版
（`%LOCALAPPDATA%\ClawToolbox\shell\keep-explorer.flag` 打开，state.json 里 `covered:true`：explorer 在、任务栏隐藏）。
构建 / 安装：在 main 主目录 `frontend\library-native\tools\Build-Msix.ps1`，升级安装（`Add-AppxPackage` 新版本，绝不先卸载），
从 Claude / Codex 桌面应用里操作时安装、读 `%LOCALAPPDATA%` 都要经 `tools\Invoke-OutsideApp.ps1`。

---

## 问题 1：游戏模式下仍能用系统边缘手势叫出开始菜单（要在系统层面屏蔽）

**现象**：游戏模式下，从屏幕底边上滑（以及 Ctrl+Esc 等）还能叫出开始菜单 / 搜索；在系统设置等所有非游戏库页面都能叫出。
开始菜单一开着就攥着前台，别的窗口拿不到前台（0.1.241 时窗口切换因此失败并留下空屏）。

**现在的状态（0.1.242 起，只是兜底）**：
- `frontend/library-native/ClawLibrary/Data/ShellSurfaces.cs`：按进程名认出 `StartMenuExperienceHost` / `SearchHost` / `SearchApp`，
  在前台时发一次 Esc 收起。
- `MainWindow.WindowManager.cs` 的 `EVENT_SYSTEM_FOREGROUND` 回调里：游戏模式下它们一到前台就收起；`MainWindow.Overlay.cs`
  `SwitchToWindow` 切换前也先收起。实测（11-06）开始菜单被打开 19 次，每次都收起，切换 120/120 成功。
- 缺点：开始菜单会先出现再收起（闪一下）；手势本身没被拦住；通知中心（右边缘）/ 小组件（左边缘）没管。
- 以前的 `Data/EdgeGestures.cs`（窗口属性 `System.EdgeGesture.DisableTouchWhenFullscreen`）只对「全屏」的窗口生效，
  被窗口管理铺满的应用窗口（工作区底下留一截）和系统设置这种 UWP 宿主窗口不算全屏，所以挡不住。

**要做的**：
1. 进入游戏模式时在系统层面关掉边缘滑动：策略 `HKLM\SOFTWARE\Policies\Microsoft\Windows\EdgeUI` 的 `AllowEdgeSwipe`（DWORD）= 0，
   回桌面模式时恢复原值（先备份：原来有没有这个值、是多少；没有就删掉）。HKLM 要管理员：走已有的提权路径
   （后台 ClawPower 提权执行端 / 管理员计划任务，参照 `native/ClawPower.cs` 的 stdio 桥和管道身份核对），不要新开 UAC 弹窗。
   在哪里切：外壳 `ClawShell to-game / to-desktop`（`native/ClawShell/WindowsShellSystem.Covered.cs`）或后台收到游戏模式状态变化时，选一处，
   崩溃 / 掉电后也要能恢复（开机外壳检查时按 state.json 纠正）。
2. 核实这条策略在本机 Win11 25H2 上是否立即生效、是否也挡住底边上滑开始菜单（不同版本行为不一样）；不生效就调研别的办法
   （例如 `HKLM\SOFTWARE\Microsoft\PolicyManager\default\LockDown\AllowEdgeSwipe`、Shell 的 edge UI 设置），把结论写进报告。
3. `ShellSurfaces` 的「出现就收起」保留作兜底。

**验收**：游戏模式下在游戏库、系统设置、Claude、文件管理器、窗口化游戏前台时各从底边 / 左右边缘上滑 10 次，
开始菜单 / 搜索 / 通知中心 / 小组件都不出现（不是出现后收起）；回桌面模式后边缘手势恢复；写明哪些是手指实测、哪些是注入触控。
注入触控的测试工具：`artifacts/edge/Swipe.exe`（以前验证过）。

---

## 问题 2：窗口切换器改成常驻、不激活的独立置顶窗口

**现象 / 背景**：长按快设键的窗口切换器是控制中心的「窗口」页，画在游戏库主窗口里：每次唤出都要把游戏库主窗口以置顶、不激活的方式
盖到前台程序上（`MainWindow.Shortcut.cs` `ShowShortcutWindow` → `LibraryWindow.ShowOverlay`），收起时再把它停回游戏后面
（`ParkGameShell`）。这条路依赖主窗口的显示 / 停放状态，用户在全屏游戏 → Claude 切换时遇到过失败（11-06 下午，日志里
`switch to claude … foreground=False`）。快捷面板已经改成独立窗口（`QamWindow.cs`：启动时建好、`WS_EX_NOACTIVATE | TOOLWINDOW | TOPMOST`、
透明背景 + `DwmExtendFrameIntoClientArea`、收起时停在屏幕外 -30000 而不是隐藏），按键到首帧约 130 ms，稳定。

**要做的**：
1. 把切换器（`Stage/WallStage.Switcher.cs` 的卡片、DWM 缩略图、焦点、A / X / B 处理）挪到一个和 `QamWindow` 同样做法的常驻独立窗口里；
   DWM 缩略图的目标窗口改成这个新窗口（`DwmThumb.Register(dest, source)` 的 dest）。游戏库自己那张卡现在用合成层 VisualSurface
   拿墙的画面，跨窗口要换办法（例如截一帧或用游戏库主窗口的 DWM 缩略图）。
2. 唤出 / 收起不再显示、停放游戏库主窗口；切换走 `MainWindow.Overlay.cs` 的 `SwitchToWindow`（已带：先收起开始菜单、AttachThreadInput、
   重试 3 次带 Alt 解锁、失败退回游戏库，绝不留空屏）。
3. 保持用户定下的外观和动效（控制中心「窗口」页的卡片样式、进出场），不要换设计。

**验收**：用游戏库里的自测入口跑切换压力矩阵（游戏模式下）：注册消息 `ClawToolbox.Library.Activation.v1`，wp = 7 打开切换器（lp = 当前前台），
wp = 112 把焦点放到 lp 指定窗口的卡上，wp = 104 = A，wp = 105 = B；先在日志里确认 `window switcher open` 再发按键
（否则按键会落到游戏库卡盒上、误启动游戏）。矩阵：窗口化游戏、**独占全屏游戏**、管理员权限游戏、系统设置、**文件管理器**、Claude、
开着开始菜单、开着快设面板，两两来回切各 30 次，记成功率、按 A 到目标前台的耗时、失败日志，真速录屏取证。
11-06 在 0.1.243 上的基线（窗口化 + 管理员权限的常轨脱离、Claude、系统设置、开始菜单、快设面板）：120/120 成功，平均 236–259 ms；
独占全屏游戏和文件管理器还没测过。
