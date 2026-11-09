# 登录后进入游戏库的呼吸加载动画

旧版外置加载窗口已由 2026-10-27 的库内启动图层取代，旧启动任务已禁用。最新范围与实际重启反馈见 [修正记录](../system/startup-power-animation-fix-20261027.md)。下文保留初版方案及其验证边界。

2026-10-26，北京时间。用户希望用自定义动画衔接开机登录与 Claw 游戏库，加载较长时循环呼吸，并沿用“不显示文字”和“不使用圆形背景扩散”的要求。

## 已实现的范围

用户登录后启动一个普通用户权限的短时动画进程，在 Default 用户桌面显示黑底双页。双页先展开，再以 2.2 秒周期轻微缩放和明暗呼吸。游戏库可用后，整个动画层在约 260 ms 内平滑淡出。没有字标、状态文字、圆形揭幕或持续的后台绘制。

它不是 Windows 登录界面的替换。本机 `EditionID=CoreCountrySpecific`，属于家庭版；微软 Custom Logon 的支持版本没有家庭版。Windows 验证、欢迎和安全桌面阶段仍由系统显示，普通用户窗口不能覆盖。登录任务和全屏体验启动之间是否仍会闪现桌面，需要实际重新登录观察；不能把桌面内的成功测试当成整机登录全程无缝的证明。

微软资料：[Custom Logon 支持版本与范围](https://learn.microsoft.com/en-us/windows/configuration/custom-logon/)、[Winlogon 与 Default 桌面的区别](https://learn.microsoft.com/en-us/windows/win32/winstation/desktops)。未修改凭据、自动登录、登录安全桌面、Windows 引导或系统 Shell。

## 启动与退出条件

- `\ClawToolbox\StartupAnimation` 在当前用户登录时触发，以 Limited 权限执行 Program Files 中受保护的 `ClawSleepAnimation.exe --boot-loading`。任务最多运行一分钟，动画本身等待上限 45 秒；失败或超时会移除遮罩。
- 仅在原有“开机进入全屏体验”开启、主页是 Claw 游戏库、且未选择跳转 Steam 大屏幕时播放。读取现有设置，不改变其值。
- 已经存在可用游戏库窗口时跳过，避免晚到的启动任务再次把画面盖黑。此任务不注册解锁、恢复或定时触发，不与普通睡眠／休眠动画绑定。
- 通过现有 `claw-library://library` 协议唤起游戏库。启动请求放在线程池中，避免 Shell 激活等待阻塞呼吸动画。
- 游戏库在首帧、初始入场动作与封面解码完成后，再经过两次合成帧通知设置窗口属性 `ClawToolbox.Library.Ready.v1`。图片解码等待最多八秒，此后保留可用界面和占位卡，不因一个资源永久等待。
- 加载进程同时核对窗口可见、未最小化、未被 DWM cloak、具有就绪标记、所属进程包家族是 Claw 游戏库。不会仅按进程名或窗口存在就认定可显示。
- 确认库就绪后尝试将库置前，再淡出遮罩。遮罩在等待期间只维持自身层级，不修改系统前台策略。按 Escape 可在动画窗口接收键盘时取消，45 秒超时保证另一个退出路径。

## 部署与保留

游戏库从 0.1.116 原位升级为 0.1.117，使用与已安装 0.1.116 EXE 哈希匹配的 `artifacts/tray-hide/src/frontend/library-native` 作为基线，只加就绪通知。没有用主工作区较旧版本覆盖新功能。

睡眠动画组件更新到 `C:\Program Files\ClawToolbox Sleep Animation\releases\20261026-165047`，休眠、唤醒的无文字样式与两个开关保持原样。设置页、托盘、游戏库资源、后台模块、功耗与按键配置保留。新库的 `ui.ini` 哈希在升级前后相同。旧发布目录和 0.1.116 签名包保留。

源码入口：

- `native/SleepAnimation/StartupLoading.cs`：现有主页条件、真实游戏库窗口核验、短时启动流程。
- `SleepOverlay.cs`、`TransitionScene.cs`：呼吸循环、统一淡出、有界等待。
- `frontend/library-native/ClawLibrary/Stage/WallStage.Startup.cs`：首帧、封面及呈现就绪标记。
- `tools/Prepare-StartupTransition.py`：匹配已安装版本的独立集成源。
- `tools/Install-StartupTransition.ps1`：签名核验、原位升级、普通用户登录任务。

## 验证

- 两个原生项目构建成功。游戏库保留基线已有的裁剪、未使用字段、符号包警告；新增代码无编译错误。
- 共用运行时绘制器导出 48 帧呼吸动画：每帧背景均为不透明黑色，存在超过 20 个不同帧，亮度按周期变化。人工查看图形，未出现文字。
- 实际触发新登录任务，以未运行的 0.1.117 游戏库完成冷启动：16:52:09 开始遮罩，16:52:14.103 库标记就绪且 `pending=0`，16:52:14.211 动画进程确认真实库及前台，16:52:14.498 移除遮罩，总约 4.555 秒。任务结果 0。
- 随后通过 computer-use 查看真实游戏库窗口，封面、侧栏和选择框显示正常，遮罩已经退出。
- 再次触发任务，记录 `startup-overlay-skipped-already-ready`，没有重播。
- 生产相同等待逻辑的三秒超时实验约 3.283 秒后正常清除窗口，记录 `cancelled-or-timeout`，没有请求任何电源操作。
- 更新后睡眠动画控制回读仍为 active=true、两个开关 true、AC/DC 有效动作 hibernate、error=null。
- 未自动重启或注销 Windows。真实重新登录后任务与 Windows 欢迎界面的接续视觉效果待用户验证。

证据与预览在 `artifacts/startup-transition/`：`installation.json`、`integration.json`、`preview-verification.json`、`claw-startup-loading.gif`、`timeout-test/animation.jsonl`、`runtime-verification.json`。实际运行日志在 `%LOCALAPPDATA%\ClawToolbox\startup-animation\animation.jsonl`。
