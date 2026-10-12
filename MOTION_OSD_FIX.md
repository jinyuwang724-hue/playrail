# MotionAssistant OSD 修复

日期：2026-10-06，Asia/Shanghai。

## 原因与本机证据

体感助手 1.2.1.0 的 `OSDEnabled=True`，RTSS 7.3.5.28314 的 OSD 显示开关也已开启，但 RTSS 全局配置的 `EnableHooking=0`（Application detection level 为 None），导致游戏未被检测。最初的 RTSS 运行记录中没有鸣潮进程。

直接修改全局检测开关后，设置又回到 None；用户也确认手动点 Low 会跳回 None。检查体感助手的内置代码可见，OSD 设置和帧率控制路径会调用 `SaveProfile("")` 保存全局配置。当前共存环境中，依靠修改 Global 无法持久恢复游戏检测。

两种 PlayHub 模式并非本次故障的唯一触发条件：初始 Desktop 下也没有游戏检测。登录计划任务已为 MotionAssistant 和 RTSS 配置最高权限启动；PlayHub 的 Gaming 启动项也已经包含这两个程序，且日志确认运行时会跳过重复启动。

## 生效的修复

为现有游戏库中 54 个主程序名称创建独立 RTSS 配置，包含：

```ini
[Hooking]
EnableHooking=1
[OSD]
EnableOSD=1
```

游戏专用配置保持 Low 检测和 OSD 开启，其余设置继承全局配置；没有锁定新的帧率上限。体感助手继续提供原有 OSD 文本、硬件监控、功耗、风扇和小黄鸭帧数功能。配置适用于两种 PlayHub 模式。

目标列表保存在 `diagnostics/motion-osd-game-targets-20261006.json`，持久性检查确认 54 个配置全部保持开启。辅助启动器、安装器、崩溃报告程序等不包含在本轮配置中。

新增配置通过 `tools/Set-Game-OSDProfiles.ps1` 应用；今后新增游戏可以使用该脚本的 `GameExecutableNames` 参数添加实际游戏主程序名称。这一方案按已安装游戏配置，不自动覆盖未来新安装的游戏。

## 验证范围

鸣潮重新启动后的游戏进程为 21112。用户明确确认 Desktop 下“已显示 OSD 和帧数”。RTSS 重新记录到游戏帧数，MotionAssistant 的 OSD 文本包含 FPS、帧时间、CPU、GPU、功耗、内存、风扇和时间。

随后通过 PlayHub 本地接口短暂切换 Gaming 和 Desktop，读取同一游戏进程的帧数、OSD 文本和游戏自身的 OSD 渲染帧编号。两种模式的结果保存在 `diagnostics/motion-osd-wuwa-gaming-20261006.json` 和 `diagnostics/motion-osd-wuwa-desktop-20261006.json`。Gaming 模式本轮验证依据为运行数据和渲染刷新，没有继续自动操作窗口；用户用 Escape 停止了 Computer Use。

Gaming 样本为 25.2 FPS，游戏 OSD 渲染帧编号 8104；Desktop 样本为 25.6 FPS，渲染帧编号 8351。这些是核对链路时的瞬时后台样本，不是性能测评结果。验证结束恢复原来的 Desktop 模式，保留游戏运行。诊断期间为打开 RTSS 设置窗口而临时修改的 `StartMinimized` 已恢复为 1。

绝区零和其他已安装游戏的配置已应用并验证保持开启，没有逐个启动这些游戏验证画面。支持情况仍受每款游戏自身的图形接口和覆盖兼容性影响。

## 恢复

原配置和新增文件清单保存在 `diagnostics/motion-osd-backup-20261006`。`tools/Restore-Game-OSDProfiles.ps1` 只恢复本轮游戏专用配置；恢复后重新启动 RTSS 和游戏生效。体感助手的功耗和风扇配置未改写。
