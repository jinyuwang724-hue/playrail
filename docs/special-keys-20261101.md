# 快设、窗口切换器与桌面切换：2026-11-01

当前安装游戏库 **0.1.199.0**，后台仍为 0.17.58，路由使用独立文件 `ClawRouter-0.17.58-specialkeys.exe`。没有降级或清空用户配置。完整集成源码在 `artifacts/special-keys-197/src`，基线为已经安装的 0.1.196；原工作区游戏库源码更旧，未用它覆盖本机新版本。

## 已完成与验证

- 实体短按实际发送 Win+G，约 300ms 后发送 MSI 88；长按发送 Win+Tab，随后发送 MSI 42。旧代码漏掉 42。现在优先处理键盘报告，并逐个关联后续固件报告，避免双开关；短按与长按按当前 Claw 模式固定分流。
- 游戏模式短按直接打开左栏，长按直接打开独立顶层窗口切换器，不等待控制中心形变。它们优先接收手柄、触摸输入，保留被覆盖的页面；窗口图标后台读取，选择目标时先撤下界面和输入隔离，再切换目标。
- 用户实机确认“左栏和切换器均出现，手柄可直接操作”。生产日志确认长按只发出一次窗口切换路由，并由手柄 A 成功切回 ChatGPT。证据为 `artifacts/special-keys-197/physical-events.jsonl` 和游戏库日志。
- 统一使用无边框窗口，明确处理客户区边界。198 与 199 的窗口和客户区均精确覆盖显示器 `[0,0,1280,800]`，修掉原有四边 2px 缩进。
- 退出游戏模式显示 Windows 桌面，不再恢复之前记住的 Steam/Xbox 窗口。实际前台类名为 `Progman`，Explorer 已运行。后台也会补做旧登录外壳进程的退出交接，不需要强杀当前登录外壳。
- 桌面短按只走固件原生 Game Bar，长按不打开 Claw 切换器。2026-11-02 用户确认重启后手柄导航恢复；撤销诊断开关、恢复 HidHide 和虚拟手柄后，方向/A/B 及连续开关两次仍正常。

## 桌面 Game Bar 手柄导航排查与重启恢复

此前 `docs/gamebar-controller-repair-20261018.md` 已记录同机顶栏及小组件均不响应手柄。此次没有把“窗口能打开”当成导航已经修好。

1. 停用软件输入层、移除虚拟手柄后，用户仍确认不能操作。
2. 官方 HidHideCLI 经管理员权限直接读取：`--cloak-off`，隐藏列表为空。对应记录 `hidhide-off-state.json`，不是只根据输入层 UI 判断。
3. Game Bar 通过输入委托运行时，普通前台 HWND 仍可能是 ChatGPT；控制器交接现改读组件的实际可见状态，避免过早恢复桌面手柄模式。
4. 实验性的 XInput 转键盘导航读取了数百次，方向/A/B 全部为零，用户也确认无效。该实验已经从运行后台及最终安装包移除。
5. 在隐藏关闭时重启 Game Bar、GameInput 两个服务。`GameInputRedistService` 曾长时间卡在 `Stop Pending`，立即恢复尝试失败，控制器重连也提示需要重启。随后该服务延迟完成恢复，最终两个服务均为 `Running`；API 只读探测可以读取手柄和导航设备。`gamebar-input-restart.json`、`final-diagnostic-state.json` 保存了最终状态，不能只看较早的 `gameinput-recovery.json` 失败快照。

排查期间曾用 `%LOCALAPPDATA%\ClawToolbox\data\input-layer\hidhide-diagnostic.json` 保留 24 小时的桌面对照窗口。游戏模式不受该诊断开关影响。该文件已于 2026-11-02 撤销并留档，自动输入规则已经恢复：`override=null`、`wanted=true`、输入层 active、HidHide cloak=true、实体手柄与虚拟手柄均连接。仍留在桌面模式，开机默认仍为桌面；服务启动类型、按键布局、风扇及功耗设置均未改变。

2026-11-02 重启后，用户报告已经可以正常选择。实际状态为重分发 GameInput 服务 Running、Windows 自带 GameInput 服务 Manual/Stopped，保持系统自动管理，没有强制把两者同时启动。尚不能据此确认故障究竟来自设备重新枚举、服务状态或驱动兼容性。

**正常配置验收已通过**：用户确认恢复 HidHide 和虚拟手柄后，短按打开 Game Bar，方向/A/B 可用，连续开关两次仍正常。最终只读核对：诊断文件不存在、override=null、输入层 active、cloak=true、实体与虚拟手柄均连接、overlayHeld=false、无输入错误，当前及开机默认均为桌面。记录为 `artifacts/special-keys-197/post-reboot-acceptance.json`。自动规则恢复记录为 `auto-input-restored.json`，原诊断文件留档为 `hidhide-diagnostic-before-restore.json`。

本轮五项改动与约定的实机验收已完成，安装版本为 0.1.199.0。Game Bar 恢复的具体系统根因仍未确定；不能把正常配置下已恢复的结果写成 HidHide 驱动故障，也不需要保留停用隐藏的临时方案。

## 构建与检查

固件快捷键原有检查通过；新增 23 项状态/关联检查通过；ClawShell 原有 42 项检查通过；后台 5 项检查通过，包括过期模式事件、连续 20 次快捷键、诊断开关绝不关闭游戏模式隔离。

NativeAOT/MSIX 构建、签名、原位安装及配置哈希检查通过。`tools/Verify-SpecialKeys.py` 已验证差异可从 196 基线完整重放、运行后台与包内后台一致、其他安装负载条目未被替换。补丁为 `tests/SpecialKeys197/special-keys.patch`。根目录中本次涉及的 6 个原生源文件亦已在核对基线后同步，`build.ps1` 已补齐新增依赖。

管理员诊断记录均在 `artifacts/special-keys-197/`，包括服务恢复、控制器重连和最终隐藏状态。后台更新使用正常退出协议，备份路径见 `agent-deployment.json` 及 `navigation-update/agent-deployment.json`；没有重启或更新风扇服务。
