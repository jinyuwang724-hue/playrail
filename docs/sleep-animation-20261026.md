# 电源键休眠动画：已安装与验证

## 2026-10-27：S0 睡眠动画和唤醒顺序修复

- 用户反馈按电源键睡眠时没有进入动画，唤醒后先显示桌面，再显示唤醒动画。诊断发现进入动画设置已为 false，唤醒动画为 true；此前一次 S0 恢复后看护记录 `owner-unresponsive`，随后控制器关闭进入动画。该日志支持看护误判的可能，但不能证明所有长时间恢复都会被误判。
- 看护增加睡眠恢复和显示亮起后的 30 秒宽限，前台可见时的心跳超时为 15 秒；进程死亡仍立即触发恢复。保留原有策略恢复和所有权检查。单独唤醒动画现在先显示黑色遮罩，再进行 Game Bar 检查；通过实体电源键进入睡眠时继续保留入睡前遮罩到唤醒。
- 已发布 motion-v11，重新启用进入动画，睡眠和唤醒开关均开启，电源键实际选择仍为 S0 睡眠。180 项状态检查通过；安装验证了正常退出和进程丢失恢复，实际暂停控制器也验证了看护恢复和重新启用。
- 11:46 实机：用户确认进入、唤醒两段动画正常，唤醒未先露桌面。日志记录 11:46:40 开始进入动画、11:46:41 请求睡眠、11:46:56 电源键唤醒并播放动画、11:46:57 完成；两项开关和接管仍有效。此次是约 15 秒的短测，不能证明长时间待机后所有设备及显示驱动恢复均无延迟。
- 证据：`artifacts/sleep-animation/motion-v11/installation.json`、`runtime-verification.json`，以及受保护状态目录下的 `animation.jsonl`。

当前使用双页开合动画（v5，无文字，唤醒背景整体淡出），最新视觉、时长、部署与开关机范围见 [新版动效记录](sleep-animation-motion-20261026.md)。下文保留前两版接管方案及实际按键验证的历史记录。

2026-10-26，Asia/Shanghai。MSI Claw 8 EX AI+ CG3EM Launch Pack，Windows 11 build 26200。

## 现在的行为

短按实体电源键，显示黑色遮罩和现有 Claw 叠页图标，图标淡出后进入 **Windows 休眠 S4**。v2 在恢复画面后还会播放约 0.52 秒的图标淡入和遮罩淡出。当前电源键仍为 S4，没有改成 S0 待机。

游戏库 **控制中心 → 系统 → 电池与电源** 新增两个独立开关：**睡眠／休眠动画**、**唤醒动画**。均默认开启，保存到受保护状态目录的 `settings.json`。关闭进入动画后立即恢复原生电源键操作；可以保留唤醒动画单独开启。普通用户的设置页通过当前会话的固定命名管道访问后台，不需要每次弹出管理员确认。

组件已安装、正在运行，并设置为当前用户登录后自动启动。安装位置在 `C:\Program Files\ClawToolbox Sleep Animation\releases\`，状态与恢复记录在 `C:\ProgramData\ClawToolbox\SleepAnimation\<当前用户 SID>\`。它独立于现有按键路由、功耗服务、Game Bar 和游戏库，不需要保持 Codex 打开。

自启动任务：

- `\ClawToolbox\SleepAnimation`：当前用户的交互式后台，使用管理员权限接收系统电源事件。
- `\ClawToolbox\SleepAnimationRecovery`：开机时恢复上次异常遗留的电源键设置。此任务只执行受保护的 Program Files 副本，读取受保护的 ProgramData 状态。

## 实现与设置含义

这台设备的普通 MSI / Raw Input 监听没有收到独立的电源键通知；在休眠广播中抢播动画也失败，实机显示器比广播早约一秒熄灭。

最终使用已实测的 `Microsoft-Windows-Kernel-Power` 事件 555 / 版本 1。在后台健康运行期间，将原方案的 AC/DC `PBUTTONACTION` 从休眠（2）暂时设为不操作（0），程序收到按键信号后完成动画，再调用 `SetSuspendState(true, false, false)`。

因此，**Windows 原生电源设置在接管期间会显示“不采取任何操作”**，实际的按键操作由组件完成。关闭进入动画或出现异常时，会恢复原来的动作。工具箱显示的是有效的睡眠／休眠选择，不把运行时的 0 显示成按键失效。在工具箱中选择睡眠或休眠会先释放旧策略，再按新选择接管；选择关机则释放接管。若在 Windows 外部主动更改策略，后台停止接管并保留新值。

匹配采用本机记录到的完整事件特征，而不是任意电源事件：`Reason=0`、`TriggerFlags=131`、`PowerAction=0`、`MinState=4`、`SubstitutionPolicy=1`、`RequesterName=System`，以及其余已核对的标志。当前仅在已验证的 Windows build 26200 启用，其他系统 build 会保留原操作。

按键送达使用 QPC 单调时钟判断，避免 S4 唤醒后 ETW 的墙钟换算偏移。动画进行期间拒绝重复事件，唤醒后保留三秒保护，避免缓冲事件导致立即再次休眠。ETW 仅订阅事件 555，正常运行不记录普通键盘输入，不安装驱动。

## 恢复保护

修改按键设置之前，先原子保存原始值、进程身份和恢复记录，并确认独立看护进程已就绪。

- 正常退出：后台主动恢复。
- 后台被强制结束：看护进程恢复。
- 后台无响应：看护进程在心跳超时后恢复；实际暂停后台的测试已通过。
- 后台和看护同时丢失：开机恢复任务读取持久记录，还原原来的按键设置。
- 正常注销或关机：在会话结束消息中恢复。
- 电源方案或用户设置发生变化：停止接管，只还原仍属于本组件的 0 值，不覆盖用户后来主动选择的操作。

每轮运行具有独立标识；旧看护不会还原或清除新一轮运行的设置。程序、任务和状态的权限已设置，SYSTEM 恢复任务不执行普通用户可写的程序。

## 验证结果

| 检查 | 结果 |
| --- | --- |
| 实体电源键 → 完整动画 → 休眠 → 唤醒动画 → 恢复画面 | v2 用户确认全部成功 |
| 实机按键信号送达 | 约 53 ms |
| 动画开始到发起休眠 | 约 0.918 秒 |
| 信号解析、完整特征匹配、重复/过期/唤醒保护、独立设置 | 71 项通过 |
| 正常退出还原 AC/DC 2 | 通过 |
| 强制结束主程序后还原 | 通过 |
| 暂停主程序，模拟卡住后的还原 | 通过 |
| 同时结束主程序和看护，运行实际开机恢复任务 | 通过，任务结果 0 |
| 恢复后重新启动并接管 | 通过 |
| 登录任务、恢复任务、受保护的执行路径 | 已核对 |
| 空闲 CPU | v2 的 12 秒样本，两进程合计约 0.195%（按全部逻辑核心计） |
| 内存工作集 | v2 两进程合计约 165 MiB |
| 旧诊断 ETW 会话 | 已停止，最终仅一个正式会话 |

没有为了验证而实际重启或注销整机。已实测当前桌面环境；独占全屏游戏中的覆盖显示尚未实测。开机恢复任务已实际手动触发验证，其自动触发器已配置。

证据在工作区：

- `artifacts/sleep-animation/animated-run-20261026/animation.jsonl`：成功的实体按键休眠与唤醒。
- `artifacts/sleep-animation/installation.json`：安装位置、任务和八项部署检查。
- `artifacts/sleep-animation/runtime-verification.json`：无响应恢复、自启动配置与空闲资源样本。

## 构建、安装与关闭

需要 .NET 9 SDK 构建，机器已有 Microsoft.WindowsDesktop.App 9.0 运行时。TraceEvent 库固定为 Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8（MIT），由 NuGet 恢复。

```powershell
.\tools\Build-SleepAnimation.ps1 -OutputDirectory .\artifacts\sleep-animation\verified-app
dotnet run --project tests/SleepAnimation/SleepAnimation.csproj -c Release
```

在管理员 PowerShell 中安装或更新：

```powershell
.\tools\Install-SleepAnimation.ps1 -Verify
```

安装会保留已有发布目录，核对副本哈希，并在启用前验证恢复路径。更新时只停止本组件。

关闭并移除自启动，恢复正常电源键操作：

```powershell
.\tools\Install-SleepAnimation.ps1 -Remove
```

这不会递归删除安装文件和日志，便于复查或恢复。原始电源方案为 `50e23985-8a76-4170-881b-c380b06e6cdd`，安装前 AC/DC 电源键均为休眠（2）；其他电源设置未更改。

## 源码入口

- `native/SleepAnimation/PowerTrace.cs`、`PowerButtonEvent.cs`：经过验证的信号与状态保护。
- `ButtonMonitor.cs`：常驻生命周期和动画/休眠流程。
- `PowerLease.cs`、`PowerPolicy.cs`：策略所有权、看护与开机恢复。
- `SleepOverlay.cs`：现有品牌图标的 WPF 动画。
- `Hibernate.cs`：受控启用并恢复休眠调用所需的权限。
- `ButtonTrial.cs`、`SuspendAnimation.cs`、`tools/SleepButtonProbe.cs`：诊断与实验入口，不作为正式自启动组件运行。
- `tools/Install-SleepAnimation.ps1`、`Verify-SleepAnimation.ps1`：安装与本机恢复验证。

参考：[Windows 电源键操作](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/power-button-and-lid-settings-power-button-action)、[休眠前通知时间限制](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmsuspend)、[Microsoft TraceEvent](https://github.com/microsoft/perfview)。

## v2 设置与唤醒（2026-10-26）

游戏库已从 0.1.110 升级到 0.1.111，保留 LocalState。后端修改以实际安装的模块为基线，页面修改以 0.1.110 的构建源为基线，避免把当前工作区里较旧的其他功能覆盖回去。升级前备份保留在 `%LOCALAPPDATA%\ClawToolbox\repair-backups\sleep-animation-20261026-103906`。

后台控制接口仅允许读取状态、设置两个布尔开关和选择原有的睡眠／休眠／关机动作；参数限制和长度限制均在后台执行。管道只向当前用户、管理员和 SYSTEM 开放，当前用户不具有创建服务端实例的权限。关掉进入动画时仍保留设置控制器，可以从界面重新开启。发生接管故障时恢复原按键动作、关闭进入动画开关并显示错误，不在故障后反复自动接管。

看护现在区分屏幕关闭与主程序卡住：Modern Standby 中桌面进程可能一起暂停，不能仅因心跳停止就判定故障；恢复亮屏后有六秒宽限。已在亮屏时实际暂停主程序，验证看护恢复策略、控制器继续响应、通过设置重新启用成功。

命令行管理（使用已安装的受保护 exe；这不是供设置页执行任意命令的接口）：

```powershell
ClawSleepAnimation.exe --set-suspend false --state-dir "受保护状态目录"
ClawSleepAnimation.exe --set-resume true --state-dir "受保护状态目录"
ClawSleepAnimation.exe --set-action hibernate --state-dir "受保护状态目录"
```

唤醒动画等待显示恢复后播放，处理重复恢复通知并保留按键保护。它运行在当前用户桌面，不能覆盖固件开机画面、Windows 恢复界面或登录安全桌面；实际显示时机取决于 Windows 和显卡恢复。

睡眠路径也已实现：当用户选择“睡眠”，进入动画后通过现有原生游戏库相同的关屏方式交给 Modern Standby，亮屏后播放唤醒动画。**本轮实际硬件流程验证以 S4 为主，S0 完整休眠与唤醒动画尚未由用户实测**；不能把“屏幕熄灭”直接当成已进入低功耗状态，也不能靠动画解决异常唤醒。

最新证据：

- `artifacts/sleep-animation/settings-v2/ui-deployment.json`：0.1.111 安装、LocalState 保留。
- `artifacts/sleep-animation/settings-v2/backend-deployment.json`：安装模块与配置保留检查。
- `artifacts/sleep-animation/settings-v2/control-verification.json`：普通用户开关回读、关闭恢复 S4、开启重新接管、独立唤醒开关和非法参数检查。
- `artifacts/sleep-animation/settings-v2/installation.json`：新版八项安装与恢复检查。
- `artifacts/sleep-animation/settings-v2/runtime-verification.json`：新版实际暂停、恢复、重新启用与资源样本。
- `artifacts/sleep-animation/settings-v2/user-verification.json`：用户确认进入与唤醒动画均可见，休眠唤醒正常；对应 11:29:53 进入动画、11:29:53.938 请求 S4、11:30:37.090 返回、11:30:37.660 完成整个流程（北京时间）。日志无该轮错误，恢复后接管和两个开关仍开启。
- 已通过实际窗口检查：两个开关可见；点击关闭、再开启“唤醒动画”后，受保护配置同步为 false、true。界面仍显示电源键“休眠”。

## 本机睡眠诊断

本轮读取 `powercfg /a`、设备唤醒权限、唤醒计时器、执行请求、系统事件和 28 天 SleepStudy，报告保留在 `artifacts/sleep-animation/sleep-diagnostics-20261026/`。未为了诊断切到 S0、停止 Sunshine 或更改驱动／固件／系统安全功能。

- 本机支持 S0 Modern Standby（断网）和 S4，不支持传统 S3。
- 当前设备唤醒授权列表为空；设置里的“只允许电源键唤醒”已开启。这是设备授权列表的限制，不代表能阻断所有固件或输入唤醒路径。
- 睡眠转休眠 AC/DC 都是 3600 秒。10 月 23 日电池供电的一次睡眠持续约 60 分钟后按固定超时转 S4；另一次分成 43.37 和 16.63 分钟两段，再转 S4，兜底确实工作。
- 上述主要 S0 段有约 94～98% 的硬件低功耗驻留。约一小时的一段消耗 358 mWh，说明进入低功耗后本身耗电不高。
- 预算超限退出睡眠不等于亮屏。10 月 23 日 18:15 的预算退出后进入 Screen Off，再回 Sleep，不能把这一条单独认定为误唤醒亮屏。
- 历史 `SLEEP_DRAIN_20261011.md` 曾捕获内部 MSI USB `0DB0:1901` 重置／重新枚举，随后一秒内出现 `Input Mouse` 亮屏，屏幕长时间未灭。输入来源尚未唯一确定。
- 当前 Sunshine 存在 DISPLAY 请求，可能妨碍自动关屏／入睡，但这次快照不能证明它就是历史亮屏原因。未停止或覆盖其请求。

建议保留当前按键 S4；如果用户希望短暂放下时用 S0，可自行将“睡眠多久后转休眠”从一小时缩到三十分钟，以缩短意外亮屏影响的窗口。不能宣称已修复 S0 自行亮屏。下一次复现应对齐 SleepStudy 的屏幕切换、Kernel-Power 的输入原因及 USB 重新枚举时间，再决定具体设备或服务的修复。

微软区分“唤醒处理器”和“点亮屏幕”：见 [Modern Standby wake sources](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby-wake-sources)。

## SteamOS 机制核对

本机 Valve Steam 客户端 `C:\software\steamui\sp.js` 中包含独立的 suspend/resume 通知和界面状态：`RegisterForNotifyRequestSuspend`、`PrepareForSystemSuspend`、`RegisterForNotifyResumeFromSuspend`、`OnSystemResumedFromSuspend`、`m_bShowResumeUI`、`ResumeSuspendedGames`。另有 Deck/OLED 的 suspend WebM 资源。

因此可确认动画位于真正挂起前和图形／客户端恢复后，系统保持挂起时不会继续播放。物理电源键需要系统与 Steam 的流程衔接；不能简单在系统已熄屏后的通知里抢播。Valve 仓库的 [历史问题 #11629](https://github.com/ValveSoftware/steam-for-linux/issues/11629) 也记录了直接由系统挂起会绕过动画、而从 Steam 电源菜单进入会显示动画的差异。该问题已关闭，不能据此把旧的第三方守护进程当作当前 SteamOS 的实现。
