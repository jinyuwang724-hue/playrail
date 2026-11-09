# 鸣潮：Steam 运行状态验证与修复

实机验证日期：2026-10-06，Asia/Shanghai。Steam 构建 1791249696。

## 原问题复现

Steam 已按前一轮设置以管理员身份运行（PID 19136），但鸣潮游戏根目录仍有 `steam_appid.txt`，内容为 `14637647\n\0`。直接启动入口的正确 GameID 是 18293439506784714752，Steam 却把实际运行的游戏登记为 14637647；快捷方式退出运行列表，未建立覆盖窗口。因此默认管理员设置不会自动清理此前遗留的错误编号文件。

## 已完成的修复

将错误文件原样移到 `C:\Game\Wuthering Waves Game\steam_appid.txt.codex-backup-20261006`，保留恢复副本。

库里有两个同名 Wuthering Waves 入口。第二个入口原先通过 `Playhub.GameSession.exe` 启动游戏，独立重试两次均在加载阶段以退出码 3 结束，Steam 正确记录了退出。这与游戏继续运行却被 Steam 记错编号是不同的现象。本次没有将退出码单独归因于某一个参数，而是把第二入口的目标、工作目录和启动参数统一为第一入口已验证成功的配置。

第二入口修改前配置保存在 `diagnostics/wuwa-second-shortcut-before-20261006.json`；完整快捷方式文件备份为 `diagnostics/steam-shortcuts-before-wuwa-fix-20261006.vdf`。`tools/restore-wuwa-second-shortcut.js` 可经已有 Steam 调试工具恢复这个入口的三个原始字段，恢复时需要先退出正在运行的游戏。

两个入口现均使用：

- 目标：`C:\Game\Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe`
- 工作目录：`C:\Game\Wuthering Waves Game`
- 参数：`-krqlv=uhd`
- Steam Overlay：开启

| 入口 | 保留的 AppID | 保留的 GameID |
|---|---|---|
| 原直接启动入口 | 4259273295 | 18293439506784714752 |
| 原 Playhub 包装入口，已改为直接启动 | 3941414661 | 16928247069003481088 |

名称、游戏编号和图标路径保留。

## 已验证结果

原直接入口于 15:09:56 启动，游戏 PID 9828，Steam 持续跟踪到 15:12:59 结束该轮测试；覆盖进程 PID 25612 对应正确 GameID。在游戏开始画面上观察了左右侧栏，右侧栏在正常缩放下显示。

修正后的第二入口于 15:18:27 启动，游戏 PID 11668，覆盖进程 PID 12172。该入口持续保留在 Steam 运行列表中（`display_status=4`），覆盖窗口对应 AppID 3941414661 和正确 GameID。左右侧栏的菜单状态分别为 1、2，覆盖组合状态均为 2；随后关闭侧栏，游戏继续运行。两个可能的工作目录均没有重新生成错误编号文件。

此前两个入口的覆盖界面均出现过缩放偏小。后续的 GamingMode 对照测试与处理见下节。

本次测试到游戏开始画面，没有进入实际战斗，没有进行用户认证操作，也没有验证 Gaming 模式下的实体按键。

## GamingMode 下的侧栏缩放，2026-10-06 后续处理

16:20 的桌面启动中，覆盖界面先读取 DPI 1.00，随后更新到 3.00，侧栏才恢复正常。16:34 在 GamingMode 内重新启动时，覆盖进程 PID 27596 持续读取 DPI 1.00，逻辑画布为 4042×2274，显示比例 0.948683；同一显示器的大屏幕主页实际比例约为 2.85。用户反馈侧栏很小。

这一轮临时尝试了界面缩放补偿。用户反馈菜单位置异常，因此已经撤销，未安装或发布到插件。Steam 的界面比例与覆盖界面设置均保留原值，安装的 Claw 按键切换仍为 0.2.4，工作区与安装目录的前端构建哈希相同。

Playhub 日志显示加载期间反复执行 `Applied borderless fullscreen to Client-Win64-Shipping`。对照测试仅关闭 `gaming.borderlessFullscreenWindowsInGamingMode`，保留游戏自身的全屏方式。该项位于 `C:\Users\13304\AppData\Roaming\GamingMode\config.json`，原配置备份为 `diagnostics/gaming-config-before-wuwa-display-20261006.json`。

关闭强制全屏后，在 GamingMode 重新启动鸣潮：游戏 PID 24640，覆盖进程 PID 23472。16:43:47 的覆盖创建日志第一次就读取 DPI 3.00；原生覆盖画布为 1347×758，显示比例 2.846050。游戏窗口与显示器物理边界均为 0,0–3840,2160，Windows 报告窗口 DPI 288（300%）。右菜单状态为 2，覆盖组合状态为 2，根元素 CSS zoom 为 1，没有临时样式。观测记录见 `diagnostics/wuwa-overlay-after-fullscreen-fix-20261006.json`。

用户随后通过 GamingMode 前置键复核，明确反馈“左右侧栏大小和位置都正常”。本轮实际显示与前置键打开两个菜单已获得用户确认；没有进入战斗测试。关闭强制全屏的配置保留生效，测试完成后返回原来的 Desktop 模式，保留游戏运行。

此配置是 Playhub 的全局选项，其他游戏也会使用自身的窗口/全屏方式。`tools/Restore-Playhub-AutoFullscreen.ps1` 只恢复这一项为 true，不覆盖其他配置；设置在重新切换 GamingMode 后应用。
