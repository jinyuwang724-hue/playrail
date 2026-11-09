# Playhub 手柄反馈排查与修复

适用环境：Playhub 2.1.0；MSI Claw 8 EX AI+；内置手柄 `0DB0:1901`。

**当前部署已迁移：** 2026-10-07 按用户要求，将兼容震动输出移入 **Claw 掌机工具箱 0.3.0**。Playhub 的两个修改入口及内置包已恢复官方原版，不再需要启动时补写 Playhub 文件。当前使用方式见 [CLAW_TOOLBOX.md](CLAW_TOOLBOX.md)。下方为此前排查和文件修复历史；不要在工具箱启用时重新安装旧的 Playhub 补丁。

## 排查证据

- Playhub 导航反馈原本已开启，强度为 100%。Steam 和 Windows 均识别到内置手柄。
- 真实方向键、肩键输入会触发 Playhub `/dash/haptic` 请求，服务返回 `windows-gamepad`；原导航事件链路能够工作。
- 用户确认 Windows XInput 的 600 毫秒输出可以产生震动。Playhub 原有 Windows.Gaming.Input 路径在一次长脉冲测试中获得震动反馈，但后续相同参数又无震动反馈，输出在本机不能可靠地产生可感知反馈。
- 原生导航模式只有约 16 毫秒，确认模式约 21 毫秒。服务返回成功并不能证明这台机器上实际感知到了震动。
- 第一轮将导航延长到 90 毫秒、确认延长到 140 毫秒，服务正常接收，但用户反馈仍感觉不到。

## 当前修复

只针对硬件 ID 为 `0DB0:1901` 的手柄，将反馈通过 Playhub 现有 Decky 后端转交 Windows XInput。每次输出都用本机已核对的 XInput 扩展能力查询确认厂商与产品 ID；没有确切匹配、出现多个同型号手柄或系统不支持身份查询时拒绝输出，不凭 Steam 手柄序号猜测 XInput 插槽。

按用户 2026-10-07 的要求，震动时长恢复为 Playhub 2.1.0 官方原生服务实际采用的各动作首段时长：导航 16 毫秒、肩键 18 毫秒、滑块 12 毫秒、开关 13 毫秒、确认 21 毫秒、返回 18 毫秒、选项与菜单 11 毫秒、下拉框 12 毫秒、字母跳转 19 毫秒，默认 16 毫秒。官方 `ControllerHapticsService.PlayPattern` 当前只执行模式第一段，因此对应其实际行为。

随后按用户要求恢复官方马达调用：方向导航、肩键和滑块使用操作方向；上下导航为双马达；返回、选项、菜单和开启开关为左侧，关闭开关为右侧；确认、下拉框和字母跳转为双马达。单侧动作的另一侧输出由原修复的 10% 恢复为官方 4%。同时恢复官方基础强度曲线（包含 0.95 系数）及首段动作强度倍率，例如导航 0.86、返回 0.88、开关 0.42、菜单 0.48。保留 XInput 兼容输出。

其他型号沿用原来的动作模式。新输出核对 Windows 的 Steam 前台窗口和 Steam 的 RunningAppID；游戏运行或 Steam 失去前台时停止震动。每次脉冲结束、关闭反馈、插件卸载时都停止本模块启动的震动。只有震动期间存在最多 250 毫秒的停止检查，没有空闲轮询；未增加驱动、虚拟手柄或独立常驻后台。

XInput 修复已安装并获得用户“测试和导航都有震动”的明确确认；随后用户要求恢复官方时长及马达调用。两项调整均已安装并重载，软件检查覆盖全部动作的马达、强度和时长。2026-10-07 再次失效后完成以下启动同步修复，用户明确确认“已有震动”；当前官方短时长及马达调用也已获得实际导航震感确认。临时观察监听已移除。

## 2026-10-07 再次失效的原因及处理

本机 `C:\Software\Playhub\playhub_crash.txt` 记录 03:35:01 开始 `SyncDeckyPlugin`，03:35:03 完成插件更新。再次排查时，已安装 `dist/index.js` 和 `main.py` 的哈希精确等于原版备份，兼容模块仍在。手柄设备状态正常，XInput 扩展身份查询仍识别到唯一内置 `0DB0:1901` 手柄；反馈开关开启，用户当前强度设置为 50%。

Playhub 2.1.0 启动时，会将安装目录 `Assets\GamingModeDeckyPlugin\gaming-mode` 的插件内容同步到 `homebrew\plugins\gaming-mode`。此前仅修复了已安装插件，内置包仍是原版；修复改变了文件长度，触发启动更新检查，继而被覆盖。上次修复漏掉了内置包这一层。

本次同时修复内置包与已安装插件的三个文件，保留全部官方时长、马达选择、强度曲线及用户的 50% 强度，没有增加后台监视器。Playhub 后端已重载。即使启动检查因其他文件触发全包同步，复制来源也已包含兼容修复。

验证结果：

- 内置包与已安装插件的六个文件哈希均匹配当前修复构建。
- 五项前端与六项后端检查全部通过，涵盖官方参数、身份校验和自动停止。
- 实际导航观察记录 12 次手柄输入事件及 12 次震动请求；震动请求全部返回 `claw-xinput`，实际使用 16 或 18 毫秒官方短脉冲。
- 用户拿着掌机导航后明确确认“已有震动”。本次未重启电脑或 Playhub 桌面应用；启动同步的保留性通过复制来源和文件检查核对。

部署工具：`tools/Install-Playhub-HapticsPersistence.ps1`；当前版本重新安装修复应使用此工具。它先核对版本及文件哈希，再同时修改两处插件；写入失败时恢复此次写入前的文件。

本次内置包备份：`artifacts/playhub-haptics-persistence-backup/20261007-034145`。记录：`diagnostics/playhub-haptics-persistence.json`、`diagnostics/playhub-haptics-persistence-validation.json`、`diagnostics/playhub-haptics-recovery-runtime.json`。

`tools/Restore-Playhub-Haptics.ps1` 已同步更新，撤销时会恢复已安装插件及记录中的内置包，先检查文件未被新版替换。恢复后同样需要重载 Playhub 后端。

## 文件与验证

- 安装目标：`C:\Users\13304\homebrew\plugins\gaming-mode` 下的 `dist/index.js`、`main.py` 和 `claw_navigation_haptics.py`。
- 原始备份：`artifacts/playhub-haptics-backup/index.js`、`main.py`；此前长脉冲修复备份：`artifacts/playhub-haptics-long-pulses/index.js`；恢复官方马达前的备份：`artifacts/playhub-haptics-before-official-motors/index.js`、`claw_navigation_haptics.py`。
- 修复生成器：`tools/patch-playhub-haptics.mjs`；版本与哈希：`repairs/playhub-haptics/patch-info.json`。
- 检查：前端与后端语法检查、五项前端兼容及官方马达／强度／时长比对检查、六项后端身份校验／马达比例／输入校验／自动停止检查通过。
- 实机记录：`diagnostics/playhub-haptics-xinput-runtime.json`；安装校验：`diagnostics/playhub-haptics-install-validation.json`。
- 安装与恢复脚本：`tools/Install-Playhub-HapticsFix.ps1`、`tools/Restore-Playhub-Haptics.ps1`。脚本会核对哈希，拒绝覆盖后续更新的文件。复制后必须重载 Playhub 后端，例如通过现有 Decky 路由 `loader/reload_plugin` 指定 `Playhub`；恢复后同样重载后端。
- 临时观察工具会在一分钟后移除其输入观察和反馈请求观察；只记录手柄事件与反馈参数。

这是本机的前端兼容修复，Playhub 更新或重新安装插件可能覆盖它。不要把这个修复盲目应用到其他 Playhub 版本。

## 上游实现

- [导航反馈前端](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeDeckyPlugin/src/navigationHaptics.ts)
- [原生反馈模式](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Services/ControllerHapticPatterns.cs)
- [原生输出服务](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Services/ControllerHapticsService.cs)
- [启动时同步插件入口](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/Playhub/MainWindow.xaml.cs)
- [内置插件包检查及复制实现](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/Playhub/Services/GamingModeService.cs)

源码副本和有界实机测试脚本保存在 `diagnostics` 目录。
