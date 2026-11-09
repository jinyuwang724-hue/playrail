# 实际重启后的启动与电源菜单动画修正

2026-10-27，北京时间。用户实际从 Game Bar 的 Claw 小组件点击重启后，没有看到关机或启动动画，流程是微星 Logo／转圈 → Windows 登录 → 游戏库。

## 查明的问题

- 旧小组件 `power_action` 直接调用系统助手，没有动画步骤。此前只实现了实体电源键的睡眠／休眠动画及登录后加载窗口，不能把它描述成已支持所有关机入口。
- 最近真实启动记录中，旧外置动画进程确实于 10 月 26 日 17:01:08 开始、约四秒后结束，但结束时 `foreground=false`。桌面内模拟成功未证明登录切换阶段可见。
- Windows 欢迎与验证属于 Winlogon 桌面；普通用户窗口不覆盖此阶段。本机家庭版也不支持微软 Custom Logon。此次仍不修改凭据、登录、Shell 或引导配置。

## 新的启动衔接

启动遮罩移入 Claw 游戏库自身的 Composition 层，不再依赖另一个普通窗口覆盖全屏游戏主页。旧 `\ClawToolbox\StartupAnimation` 任务已禁用并保留 XML 备份。

新的库内图层仍为黑底双页、冷白灰色、无文字，使用合成线程的 2.2 秒缩放和明暗循环。只有以下条件同时满足时才计算可见时间：

- 游戏库窗口可见、未最小化、未被 DWM cloak。
- 游戏库是实际前台根窗口。
- 当前输入桌面与游戏库线程所在桌面一致；只读取桌面信息，不调用 SwitchDesktop。

在这些条件满足后至少显示 1.1 秒，且等首帧、入场和封面就绪再用 260 ms 整体淡出。登录界面后面的时间、或库被其他程序遮住的时间，不消耗这段最短可见时间。资源解码仍有八秒兜底；真正可见而资源一直未就绪时，45 秒后允许退出遮罩。

默认启动并以 Claw 为原有全屏主页时启用。协议恢复、热键唤出等现有操作不重新播放。原主页选择、Steam 跳转偏好、`ui.ini` 均保留。

## Game Bar 与游戏库的关机、重启

Game Bar 现有 `power_action` 调用改为请求动画控制器：控制器先完整显示无文字合页动画，确认末帧纯黑，然后才执行固定的 `shutdown.exe /s /t 0` 或 `/r /t 0`。不使用 `/f`，不跳过 Windows 的未保存应用处理。若进入动画开关关闭或控制器不可用，保留原系统电源路径。

游戏库电源菜单的关机、重启也改为同一后台入口。没有拦截 Windows 开始菜单、系统更新、长按断电等其他来源，也不能把用户会话结束后的系统界面替换掉。

新增的管道命令只接受 restart／shutdown 两个固定值；`preview_exit` 只播动画，不执行电源请求。动画期间拒绝重复操作。回复不明确时不会在后端另发一次重启，避免重复请求。异常或系统未完成退出时，遮罩有清理上限。会话结束的查询消息只确认，不提前关闭控制器，等 Windows 确认会话实际结束后才恢复并退出。

现有动画设置和用户最新的电源键选择保留。本轮回读的有效电源键操作为 sleep；没有强行改回以前的 hibernate。

## 部署与验证

- 以实际安装的 0.1.117 EXE 与对应构建产物哈希一致为前提，复制完整基线后原位升级为 0.1.118；保留托盘、现有功能和资源。`ui.ini` 哈希前后一致。
- 后端只把 `power_action` 方法合并进实际已安装的 `claw_service.py`，并扩展实际已安装的动画客户端；没有把工作区较旧服务文件整体覆盖回去。
- 12 项可见性门控检查通过，包含登录桌面遮挡两分钟、资源已经就绪却仍不可见、前台切换、实际可见后才计时与超时。
- 11 项电源路由检查通过，包含动画开启、关闭、后端不可用、sleep／lock 不受影响、非法动作拒绝，以及请求回复丢失时不重复重启。测试使用模拟系统助手，没有执行操作系统电源动作。
- 实机游戏库在后台等待约 98 秒时没有提前结束遮罩。04:02:47.378 被置前后记录可见，04:02:48.881 才发布就绪并移除遮罩。已用 computer-use 查看最终库画面正常。
- 两种关机动画通过 `preview_exit` 进行安全预览，日志确认完成、窗口未被 DWM cloak；最近一轮 `foreground=true`。没有发送 `power-exit-requested`，操作系统电源请求数为 0。用户前一轮未留意预览；Game Bar 小组件在前台时的直接观察仍待确认。
- 此次没有代用户再次重启。真正从 Game Bar 重启并在下一次登录后观察两段动画的结果仍需用户实测；Windows 登录界面仍会出现。

证据：`artifacts/startup-shield/installation.json`、`backend-deployment.json`、`exit-preview.json`、`runtime-verification.json`；构建为 `build-fixed.log`。旧任务 XML 和旧 MSIX、受保护原生发布目录均保留。

参考：[微软输入桌面说明](https://learn.microsoft.com/en-us/windows/win32/winstation/desktops)、[OpenInputDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop)、[会话结束查询应如何响应](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession)。

## 动画期间的实体快捷设置键

用户随后确认，普通画面上的预览可见，但动画播放时仍能用掌机实体快捷设置键呼出 Game Bar；当时 Game Bar 没有预先打开，不能将这次反馈当作已有 Game Bar 前台覆盖测试通过。

本机 QS 当前映射仍为 `system/gamebar`。固件直接发出的 Win+G 在 MSI_Event 通知前到达，普通全屏遮罩不会禁止此快捷键。v8 在 WPF 的短时进入、唤醒动画范围内临时安装低级键盘过滤，动画结束立即注销，不改按键映射，也不在登录呼吸循环、黑屏等待或普通空闲状态监听。

为防止拦下 G 后松开 Win 反而弹出开始菜单，过滤范围包含该短时阶段的新 Win 按下、相应组合键与释放。因此其他 Windows 键组合在这一小段动效期间也暂不传递；不含 Win 的普通键正常传递。对于动画开始前已按下的 Win，保留其正常释放，避免留下按键状态。没有按键重放，没有记录普通输入内容，日志仅保留拦截的 Windows 按下次数。

新增状态检查覆盖固件 Win+G、左右 Win、不同释放顺序、普通 G、Ctrl/Alt、预先持有的修饰键和结束后恢复；连同原检查共 84 项通过。v8 已部署，现有设置与 sleep 电源键动作保持原样。实体按键在短动画内被阻止、结束后恢复的最终实机结果仍待用户验证。

源码为 `native/SleepAnimation/TransitionShortcutState.cs`、`TransitionShortcutFilter.cs`，作用域由 `SleepOverlay.AnimateAsync` 管理。参考：[微软低级键盘回调说明](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)。

## 已打开的 Game Bar：改用宿主交接

用户在后续测试中明确指出，预览前已经打开 Game Bar 时，动画无法盖在 Game Bar 上面。04:38 的预览中临时键盘过滤记录为零次，不能当作物理键过滤有效或已有 Game Bar 遮挡问题已经解决的证据。普通桌面窗口的置顶、前台和未 cloak 状态也不能证明它盖住了 Game Bar。

Game Bar 小组件更新为 0.4.43.0：关机、重启先通过 `XboxGameBarWidget.LaunchUriAsync` 打开现有 `claw-library://restore-library` 路由，再调用 `MinimizeAsync`，等待宿主的 `GameBarDisplayMode` 离开 Foreground，另等 200 ms 后才发送原动画电源请求。两秒内未退出前台、启动库失败或准备阶段再次进入前台时取消，不继续执行电源动作。没有使用会关闭最后一个窗口的 `Close()`，也不修改快捷设置键的长期映射。

新增电源菜单「动画预览」，走相同宿主交接，但最后只调用无参数的 `preview_power_animation`。该入口固定传递 `preview=True` 到 `preview_exit`，不提供操作系统电源兜底路径。参数误传、动画控制器不可用时均不转成真实重启。现有 11 项路由检查加 3 项预览安全检查，共 14 项通过，未执行操作系统电源动作。

构建基线来自当前安装的 0.4.42 对应源码，其构建 EXE 与已安装 EXE 的 SHA-256 一致；保留现有 OSD、小核心与手柄模式等功能。后端仅把预览方法及允许列表合并进当前安装的模块，正常重启后台。小组件原位升级，保留 LocalSettings；`gamebar-layout.json` 是宿主会重新生成的几何测量报告，不能把它的变化当作用户设置变化。安装后 EXE 与新构建匹配，旧签名包保留用于恢复。

本次范围为从小组件菜单发起的操作；直接从外部触发的动画预览、已打开 Game Bar 时按实体电源键的情况尚未改为此宿主交接。Game Bar 固定的其他小组件也需单独观察。实际视觉结果正在通过新增菜单由用户验证，不能提前认定通过。

04:51 的真实菜单预览已记录：`libraryLaunched=true`、`gameBarForeground=false`、`phase=ready-to-animate`。原生播放 72 帧，用时 1156 ms、最大帧间隔 19.6 ms，随后到达纯黑并清理；操作系统电源请求为零，控制器仍正常接管原 sleep 动作。此记录只证明程序交接和播放完成，用户对实际无遮挡效果的反馈仍待确认。

证据：`artifacts/gamebar-power-handoff/installation.json`、`backend-deployment.json`、`base.json`、`build.log`、`live-preview.json`。实际点击后，宿主交接诊断写入小组件 LocalState 的 `power-transition.json`；原生 `animation.jsonl` 继续记录预览及真实退出请求，二者分开判定。

参考：[微软 XboxGameBarWidget 的启动、最小化与前台状态接口](https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget)。

## 0.4.44 / motion-v9：在当前画面收起 Game Bar

用户确认 0.4.43 的菜单预览能够收起 Game Bar 并看到动画，但有两次游戏库唤起／消失的多余过场；实体电源键进入和唤醒时，Game Bar 仍在动画上方。上一版的菜单交接不能作为实体电源键已修复的证据。

0.4.44 删除菜单预览和关机／重启前的 `claw-library://restore-library` 激活及最小化步骤。motion-v9 也不再激活动画窗口，沿用实体电源键已验证过的非激活全屏遮罩，避免切换应用造成额外过场。

Game Bar 小组件在宿主显示模式或可见性变化时原子写出 `power-visibility.json`，只有内容变化才写入，普通轮询只作补充。原生动画程序在进入动画前、保留黑屏后的唤醒动画前，以及独立唤醒动画前读取同一报告。报告须为 Foreground 且 Visible，报告进程须仍为同一会话的 ClawWidget，启动时间与报告匹配，并且该进程确有可见、未被 DWM cloak 的窗口，才允许尝试 Escape 在原画面收起 Game Bar。没有 Win+G 切换、游戏库协议启动或强制窗口激活。

每次输入前重新核对可见性；用户按住 Win、Ctrl、Alt 或 Escape 时不干扰。Escape 的按下和释放成对发送，部分输入失败也补释放。最多尝试三次，宿主已退出则立即停止，避免将下一次 Escape 发到游戏；输入失败不重试。关闭动作在短时 Windows 快捷键过滤之前完成。此版本仍需实测确认 Game Bar 的实际收起效果；失败时不会阻止原电源动作，可能仍出现旧的遮挡现象。

新增 7 项非交互检查覆盖无覆盖层时不发送输入、一次关闭、菜单先消费一次 Escape、修饰键保留、发送前可见性变化、输入失败以及重试上限。与原有检查合计 91 项通过；使用模拟输入，没有对桌面发出按键。构建、签名及安装完成；设置与有效 sleep 电源键动作保留。没有代用户执行关机、重启或睡眠，菜单预览与保持 Game Bar 打开的实体电源键循环正在等待实测反馈。

证据：`artifacts/gamebar-power-in-place/installation.json`、`base.json`、`build.log`，以及 `artifacts/sleep-animation/motion-v9/update-verification.json`。原生日志新增 `gamebar-dismiss`，分别记录进入／唤醒方向、Escape 次数及是否仍可见。0.4.43 的旧报告和签名恢复包保留，不将其与本轮验证混用。

参考：[微软 SendInput 的成对输入与键盘状态限制](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)。

## motion-v10：用系统输入归属修复误判

用户确认 v9 已消除游戏库反复出现／消失，但菜单预览和实体按键仍不收起 Game Bar。对应日志中所有关闭尝试的 `presses` 均为零；不能将其解释为 Escape 被发送后无效。

在用户明确保持 Game Bar 打开的现场，只读检查得到小组件宿主报告 `foreground=true, visible=true`，进程身份匹配，但 EnumWindows 和另一个有界 FindWindowEx 遍历都未发现该进程的普通可见窗口。v9 的额外窗口检查将真实覆盖层错误判为不存在。微软说明 Windows 8 以后的 EnumWindows 只枚举桌面应用的顶层窗口，不能将这一检查作为 Game Bar 宿主表面的必要条件。

进一步在本机读取系统 Game Bar API，实测 `Windows.Gaming.UI.GameBar.Visible=false`，但 `IsInputRedirected=true`。后者表示输入正在由 Game Bar 接管，正是关闭按键应送给 Game Bar 的状态；不能用本机这个场景的 Visible=false 判断它没有打开。普通权限和与正式动画相同的高权限只读探测均已验证输入归属为 true，没有发送任何按键或电源操作。

v10 删除了用户窗口枚举和小组件状态文件这两项关闭前提。每次发送 Escape 前及等待期间，直接读取系统 `GameBar.IsInputRedirected`，输入交还就立即停止；API 异常时不发送。保留成对输入、修饰键保护、三次上限，以及进入和唤醒共用的原地流程。不唤起游戏库、不激活动画窗口、不发送 Win+G。应用目标框架使用 Windows 10 SDK 投影以调用该正式系统 API，仍为 .NET 9 自包含发布。

v10 已安装到受保护目录 `20261027-051436`，现有两个开关和 sleep 动作保留。Game Bar 小组件仍为 0.4.44、游戏库仍为 0.1.118，不需升级它们。实际收起和视觉效果通过菜单的无电源预览验证中，尚不能提前认定已通过；暂不要求再做一次睡眠循环。

证据：`artifacts/gamebar-dismiss-diagnosis/v9-open-state.json`、`input-api-elevated/gamebar-inspection.json`、`artifacts/sleep-animation/motion-v10/update-verification.json`。新增的 `--inspect-gamebar` 是只读探测，不创建动画窗口，不发送输入。91 项既有状态和输入边界检查的逻辑不变；本次新增验证为真实运行权限下的 API 读取、构建和安装回读。

参考：[EnumWindows 的应用类型范围](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows)、[GameBar.IsInputRedirected](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.ui.gamebar.isinputredirected?view=winrt-26100)。

05:19 实机菜单预览通过：用户明确反馈「Game Bar 收起，动画完整显示」。最近两次记录均为输入由 Game Bar 接管、发送一次 Escape、`remaining=false`，随后动画完成并清理，操作系统电源请求为零。最后一次播放 72 帧、1159.2 ms，最大帧间隔 22.5 ms。证据保存为 `artifacts/gamebar-dismiss-diagnosis/v10-live-verification.json`。保持 Game Bar 打开的实体电源键进入／唤醒循环仍需分别验证，不能由菜单预览结果代替。

05:21 实体电源键实测也通过：用户明确反馈「Game Bar 收起，进入和唤醒动画都完整可见」。对应最后一轮进入时输入归属为 Game Bar，发送一次 Escape 后交还；进入动画完成 72 帧、1161 ms，然后执行原 sleep 动作。唤醒时 Game Bar 没有重新接管输入，因此零次发送，唤醒动画完成 60 帧、954.5 ms，最终正常清理。预览、实体按键进入和唤醒三项视觉结果均已确认，当前保留 Game Bar 0.4.44 / 原生 motion-v10 / 游戏库 0.1.118，没有再修改电源设置。

本轮完成的是当前用户会话中 Game Bar 遮挡与额外游戏库过场的修复；不将这些结果写成真实关机／重启已验证，也不改变 Windows 登录桌面的既有限制。较早章节的「待验证」为当时状态，本段与 `v10-live-verification.json` 为当前最终结果。
