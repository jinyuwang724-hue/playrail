# 快设与游戏库：保留游戏前台、隔离输入、OSD 让位

接续 Claude 的已安装 0.1.190 源码，0.1.196 已安装。2026-11-01 完成验收；工作目录中其他并行改动未覆盖。

用户明确：左侧快设属于 Claw 游戏模式；桌面模式的快设键继续调用 Game Bar。验证必须在游戏模式进行，不能用桌面模式下的内部路由调用代替。

## 实现

- 游戏模式中，从游戏呼出快设或游戏库时使用不激活的置顶窗口。游戏继续拥有前台；已有 XInput 轮询读取被 HidHide 放行的真实手柄。
- 显示窗口前，后台确认 HidHide 已隐藏实体设备、虚拟设备已连接、实际虚拟报告已经归零。归零与前台进程名无关，也覆盖键盘鼠标映射。收起动画结束后释放占用，关闭所用的按键、扳机和摇杆松开后才恢复游戏输出。
- 固定 OSD 在占用期间关闭，释放后由原有更新循环恢复。没有修改 OSD 档位、位置或用户开关。
- 占用由独立 RPC 连接持有；断线立即释放，8 秒未续期也释放。旧界面的断开不能释放新界面的占用。
- 游戏库再次收到 Claw 键的 `restore-library` 路由时，能识别已经显示的叠加界面并收起。快速连按会取消过期的延迟显示回调。
- 文字输入显式取得键盘焦点。此时会触发游戏自身的失焦行为；普通手柄导航和快设不需要取得键盘焦点。
- 快设打开期间不进入游戏库屏保。
- 收起时不把已最小化的窗口切回全屏 presenter，避免 WinUI 重新激活游戏库；普通前台唤出时再恢复该样式。

窗口定位保留 `SWP_NOACTIVATE`，并使用 `SWP_NOOWNERZORDER`。本机缺少后者时，调用返回成功但置顶位没有生效；加入后实测置顶位生效且游戏前台未变化。API 定义见 [Microsoft SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) 和 [扩展窗口样式](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)。

## 源码和交付物

完整候选源码、MSIX、运行记录位于 `artifacts/overlay-focus-fix-191/`（目录保留最初开发批次名，正式版本以清单为准）。

- 游戏库基线：`.claude/worktrees/bold-jennings-a19012/artifacts/qp/src/frontend/library-native/ClawLibrary`。其编译产物与开始时安装的 0.1.190 可执行文件 SHA256 一致。
- 输入层基线：`artifacts/enhanced-sleep/final/src/native/InputLayer`。保留桌面翻译、通知、升级后白名单刷新和增强睡眠支持。
- 后台基线备份：`artifacts/overlay-focus-fix-191/base/plugin`；现场部署备份位于 `%LOCALAPPDATA%/ClawToolbox/repair-backups/`，路径记录在 `agent-deployment.json`。
- 可审查补丁：`tests/OverlayFocus/overlay-focus.patch`。`integration.json` 记录基线和候选哈希。
- 版本化输入程序：`ClawInput-0.17.58-overlay-r2.exe`，不覆盖运行中的旧程序。
- `Payload/agent/agent.zip` 同步后台和新输入程序，其他载荷条目保留。

准备、编译、安装、验证入口分别是 `tools/Prepare-OverlayFocus.py`、`Build-OverlayFocus.ps1`、`Install-OverlayFocus.ps1`、`Verify-OverlayFocus.py`。

安装前验证当前程序哈希和包签名，原位升级并核对用户 INI。游戏模式守护进程没有升级暂停接口，直接替换包会误触发崩溃兜底；安装脚本先经正式入口临时切桌面，升级后恢复原模式和默认启动方式。

## 验证边界

- 9 项输入释放检查、5 项连接占用检查通过。
- 候选实际 `GamepadPoller` 的 2167 项设备选择与调度回归检查通过。
- 真实输入引擎的 A/B/LB 合成序列在占用及中途释放时没有生成手柄或键盘输出。
- 补丁可重放为候选源码；OOBE 载荷与后台一致，其他载荷条目保留；安装过程保留用户 INI。
- 真实游戏为本轮重新启动的 P3R，使用正式路由 RPC 和 PresentMon 核对。快设期间 60.16–60.27 FPS，帧计数 2226→2344；游戏库期间 59.75–59.84 FPS，帧计数 2406→2587。游戏 HWND 始终为前台，叠加窗口置顶且不激活，虚拟手柄保持占用。关闭、连续快速开关 10 次后，前台仍是游戏，输入占用解除，OSD 重新绘制。结果在 `runtime-verification.json`。
- Computer Use 在切换游戏模式后报告 `CreateForMonitor ... 0x80070057`，因此没有把截图当作这轮画面验证证据。实体按键和触屏不由 RPC 测试替代。

安装期间输入层曾重启。HidHide 不能撤销程序已打开的实体手柄句柄；更新前已经运行的游戏应重新启动。本轮最终测试的 P3R 在输入层稳定运行后重新启动。

验收后经正常窗口关闭请求退出本轮启动的测试游戏，并恢复验收前的桌面模式和默认桌面启动设置，记录在 `test-cleanup.json`。
