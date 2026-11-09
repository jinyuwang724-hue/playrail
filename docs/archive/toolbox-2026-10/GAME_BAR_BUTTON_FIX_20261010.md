# Xbox 键同时打开 Steam 和 Game Bar：0.12.4

用户在 Playhub Desktop 下打开 Steam 大屏幕，Xbox 键同时打开 Steam 左侧栏和 Xbox Game Bar。0.12.2 已移除旧快捷键回放，但用户确认仍同时出现两个界面；不能把快捷键过滤日志当作 Game Bar 已阻止的证据。

## 本机证据

Windows `HKCU\Software\Microsoft\GameBar\UseNexusForGameBarEnabled` 已为 0，因此仅关闭“允许手柄打开 Game Bar”不足以消除本次问题。这个开关的含义可对照 [Microsoft Windows 设置说明](https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-windows-11)。

用户配合的有限观察记录在 `artifacts/xbox-double-menu-observation.jsonl`。只记录 G、Windows 键及 MSI 前置键事件，不记录其他文字。旧版本捕获到两次 WMI 88；一次缺少可用的原始设备确认，25 毫秒到期后 G 按下、G 松开和 Windows 松开被带本插件标记 0x434C4157 重新发送。用户确认仍同时出现 Game Bar。

0.12.2 的四次实体 Xbox 按压都记录了快捷键抑制，Steam 菜单也均确认成功，用户仍看到 Game Bar。单次结束 Game Bar 后台后用户反馈“Game Bar 又重新出现了”。本机可见问题并非仅由旧版快捷键回放解释。

## 修复

接管期间直接屏蔽 Win+G，不再等待原始设备确认，也不在确认缺失时回放这组快捷键。左右 Windows 修饰键、不同时间戳、合成输入和按键重复均覆盖；G 松开成对处理，普通 G 输入及其他快捷键放行。

0.12.4 增加 Game Bar 窗口保护：接管时关闭已有 Game Bar 后台，在窗口创建或显示通知中关闭重新出现的 Game Bar。只处理当前 Windows 会话中、包身份为 `Microsoft.XboxGamingOverlay_8wekyb3d8bbwe` 的 `GameBar.exe` 和 `GameBarFTServer.exe`。从身份核对到结束进程使用同一个进程句柄，避免 PID 复用误关；不处理 ClawTweaks 硬件后台，不做空闲进程扫描。

该保护随 Steam 大屏幕接管启用，退出大屏幕、关闭接管、维护、睡眠和卸载时停止。Game Bar 保留安装；桌面主页修复和 ClawTweaks 组件继续保留。保护所需通知不可用时不开始接管。

开发中的 WMI 进程启动通知在当前非管理员后台被 Windows 拒绝访问，已撤回该方案。最终版本使用普通用户可用的 `SetWinEventHook` 窗口通知，不要求额外权限。

## 验证

15 项快捷键边界检查及 20 项接管／目标身份检查通过。0.12.4 已安装并重载，ready=true、released=true、gameBarGuardActive=true。

真实本机验证：接管时关闭已有 Game Bar 与 FTServer 两个进程；使用 Computer Use 启动已安装的 Game Bar，实际新进程 PID 15988 被窗口通知关闭，`events.jsonl` 记录 `gamebar-closed`，之后没有残留 Game Bar 进程。应用启动工具因为窗口被关闭而返回“未暴露可操作窗口”，该提示不是成功依据，实际结束记录和进程检查才是依据。

退出大屏幕后 guardActive=false、released=false、helperRunning=true、restorePending=false；Game Bar PID 2016 和 FTServer PID 22000 在原 ClawTweaks 恢复时启动并保持运行，证明保护解除后可恢复原入口。重新打开大屏幕后保护再次开启。

0.12.4 实体 Xbox 键复测通过：用户明确反馈“只出现 Steam 左侧栏”。日志记录四次 Xbox 输入及四个重新启动的 Game Bar 进程被关闭，Steam 菜单均确认成功；最后状态 gameBarClosed=9、gameBarCloseFailures=0，无残留 Game Bar。窗口通知在应用开始创建／显示窗口后处理，当前本机复测未报告闪现；不能视为 Windows 激活之前的阻断。旧有限输入观察已自动退出；不再运行输入观察进程。
