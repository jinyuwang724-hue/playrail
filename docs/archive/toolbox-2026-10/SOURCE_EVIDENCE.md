# 已核对的源码与资料线索

## 2026-10-06 游戏内接管补充

0.2.1 回归排查还核对了本机 Steam `C:\Software\steamui\sp.js` 的模块 26337：原生 `RegisterOverlayBrowserInfoChanged` 由 `window.overlayStore` 注册自己的 `OnOverlayBrowserInfoChanged`，该方法读取原生窗口信息、更新映射并分发窗口变化。插件应订阅现有管理器的 `RegisterForBrowserCreated`、`RegisterForBrowserClosed`、`RegisterForBrowserUpdated`，保留 Steam 对原生回调的所有权。0.2.2 已据此修改；本机观测和日志见 `artifacts/regression-0.2.1-events.jsonl` 与 `DEVELOPMENT.md`。这部分属于当前安装版本的一手源码证据，不保证未来 Steam 版本保持相同内部接口。

0.2.3 的输入法诊断在本机观察到 Xbox 的 LWin / G 输入来自 `ACPI#MSNB1001`，WMI 88 晚约 300 毫秒。实现按 [Microsoft LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) 的消息循环与短回调要求、[KBDLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct) 的注入标记处理原则，结合原始输入核对设备来源；只对确切内置组合丢弃 G。Handheld Companion 1.3.3.1 的 `ClawFirmware.cs` / `InputsManager.cs` 也提供 Win+G 固件输入处理线索，参考副本保存在 `artifacts/upstream`；本实现没有复制其固件写入、全局快捷键设置或虚拟手柄逻辑。实现与实测边界见 `DEVELOPMENT.md`。

本机 Steam 的实际覆盖窗口在《甜蜜夏日》（App ID 2825300）中核对：`WindowStore.GetOverlayInstance(appId, pid)` 可得到 GamepadUI 窗口；其 `params.browserInfo` 提供 `m_unAppID`、`m_unPID`、`m_nBrowserID`、`m_gameID`。`MenuStore.OpenSideMenu(2)` 触发原生覆盖激活，关闭后触发取消激活；合成状态同步从 0 变为 2 再回到 0。原始观测摘要保存在 `artifacts/ingame-interface-probe.json`。

无原生覆盖窗口时的焦点事务根据固定版本 [ControlledQamFocusService.cs](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Services/ControlledQamFocusService.cs)、[WindowsQamFocusAdapter.cs](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Services/WindowsQamFocusAdapter.cs) 与 [AgentHost.cs](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Services/AgentHost.cs) 核对。`acquire`、`close`、`release` 使用同一个 requestId；只有确切游戏与 Steam 窗口满足策略时才授予焦点，关闭时也检查原游戏身份和当前前台。源码副本在 `artifacts/upstream`，没有更改 Playhub 本身。

当前原生窗口路由不需要发送键盘快捷键；短时等待只发生在收到实体按键后。非 Steam 游戏的 64 位 gameID 作为字符串传递，避免精度丢失。无覆盖窗口的焦点路径已完成逻辑检查，实机验证范围以 DEVELOPMENT.md 为准。

检查日期：2026-10-05。以下为上游一手资料。短摘录只用于定位与理解，完整实现请读取固定版本链接。源码中的注释／流程不等于用户授权。

## A. Playhub v2.1.0

发布页：[Playhub 2.1.0](https://github.com/LoZazaMastro/Playhub/releases/tag/v2.1.0)。

### A1. 当前模式接口

[AgentHost.cs](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Services/AgentHost.cs)

定位：`MapCurrentModeEndpoint`、`MapCurrentModeEndpoint(app, store)`、`JsonStringEnumConverter`、`/status`。

```cs
app.MapGet("/mode/current", (Func<IResult>)(() => Results.Json(new
{
    agentRunning = true,
    currentMode = store.LoadState().CurrentMode
})));
```

同文件依据 `Safety.AllowRemoteApi` 选择监听地址，端口来自 `Safety.ApiPort`。本工具只读取回环地址，不需要启用远程访问。

### A2. 默认端口与配置路径

[SafetySettings.cs](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Models/SafetySettings.cs)：`ApiPort = 47991`。

[AppPaths.cs](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Services/AppPaths.cs)：ApplicationData 下的 `GamingMode`，含 `config.json`、`state.json`、`agent.log`。

[ModeKind.cs](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Models/ModeKind.cs)：`Desktop`、`Gaming`。

### A3. 配置编辑不等于硬件映射

[controllerProfiles.ts](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeDeckyPlugin/src/controllerProfiles.ts)

```ts
// Independent logical profiles. No device reader, driver, RPC or output sink.
export const controllerProfileCapabilities = Object.freeze({
  localProfiles: true, syntheticPreview: true, liveMapping: false,
  virtualOutput: false, gyro: false, trackpads: false, exclusiveMode: false,
});
```

[QamSettings.tsx](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeDeckyPlugin/src/QamSettings.tsx)：栏目顺序、可见性、图标、插件入口。

### A4. Game Bar watcher

[xbox-gamebar.ps1](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/Playhub/Assets/GamingMode/xbox-gamebar.ps1)

```powershell
$gameBarKey = 'HKCU:\Software\Microsoft\GameBar'
$gameBarValue = 'UseNexusForGameBarEnabled'
```

脚本区分普通大屏幕与已识别 UWP／Xbox 会话；不应以另一个循环不停改同一注册表项与它竞争。

### A5. Steam／Decky 菜单控制点

[controlledQamIntegration.ts](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeDeckyPlugin/src/controlledQamIntegration.ts)

定位：

```text
main(): SteamUIStore.WindowStore / Router.WindowStore -> MenuStore
patch(menu, "OpenSideMenu", ...)
patch(menu, "CloseSideMenus", ...)
patch(ui.Navigation, "OpenQuickAccessMenu", ...)
steam.System.UI.RegisterForSystemKeyEvents(...)
steam.Overlay.GetOverlayBrowserInfo()
```

这些是候选控制点，不是已经验证的外部调用方案。菜单打开与游戏焦点管理分开测试。

补充：[controlledQamFocus.ts](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeDeckyPlugin/src/controlledQamFocus.ts)、[ControlledQamFocusService.cs](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Services/ControlledQamFocusService.cs)、[WindowsQamFocusAdapter.cs](https://github.com/LoZazaMastro/Playhub/blob/v2.1.0/Source/GamingModeAgent/GamingMode.Services/WindowsQamFocusAdapter.cs)。

## B. Handheld Companion 1.3.3.1：仅作源码参考

[正式发布说明](https://github.com/Valkirie/HandheldCompanion/releases/tag/1.3.3.1)。

[ClawA1M.cs](https://github.com/Valkirie/HandheldCompanion/blob/1.3.3.1/HandheldCompanion/Devices/MSI/ClawA1M.cs)：

```cs
LaunchMcxMainUI = 41,
LaunchMcxOSD = 88,
```

```cs
ManagementScope scope = new ManagementScope("\\\\.\\root\\WMI");
specialKeyWatcher = new ManagementEventWatcher(
    scope, new WqlEventQuery("SELECT * FROM MSI_Event"));
```

```cs
int WMIEvent = Convert.ToInt32(e.NewEvent.Properties["MSIEvt"].Value);
WMIEventCode key = (WMIEventCode)(WMIEvent & byte.MaxValue);
```

同文件 `Open()` 修改 M1／M2 等状态；不要完整复制设备初始化。

[ClawCG3EM.cs](https://github.com/Valkirie/HandheldCompanion/blob/1.3.3.1/HandheldCompanion/Devices/MSI/ClawCG3EM.cs)：继承上述实现，`Open()` 调用基础实现并包含 `set_long_limit(30)`、`set_short_limit(37)`。

键盘／Win+G 处理候选参考：[InputsManager.cs](https://github.com/Valkirie/HandheldCompanion/blob/1.3.3.1/HandheldCompanion/Managers/InputsManager.cs)、[ClawFirmware.cs](https://github.com/Valkirie/HandheldCompanion/blob/1.3.3.1/HandheldCompanion/Devices/MSI/ClawFirmware.cs)。未来 Codex 应检查实际路径和版本后再采用，不能假设滤掉键盘组合也滤掉了 Guide。

## C. ClawTweaks：正式发布与旧公开分支分开

### C1. 0.4.1 正式发布说明

[ClawTweaks 0.4.1](https://github.com/enterTheVoidCode/ClawTweaks/releases/tag/0.4.1)。

明确描述硬件模式的快捷磁贴热键、M1／M2 固件宏、默认固件重映射与按游戏保存配置，以及更新后的虚拟输出后端。Essential 是一种需重新安装条件的精简方案，不是当前任务默认步骤。

### C2. release/v0.3.98.0 的实现线索

以下链接固定在较早公开分支，**不能当成 0.4.1 安装版的完整实现**。

[GamingWidget.ControllerState.cs](https://github.com/enterTheVoidCode/ClawTweaks/blob/release/v0.3.98.0/XboxGamingBar/Features/Controller/GamingWidget.ControllerState.cs)：诊断 UI 区分虚拟、硬件和外部手柄状态。硬件状态表示关闭手柄模拟，但不能据此推断前置键已放行。

[MsiClawHwMouseProperty.cs](https://github.com/enterTheVoidCode/ClawTweaks/blob/release/v0.3.98.0/XboxGamingBar/Data/MsiClawHwMouseProperty.cs)：固件原生桌面鼠标切换。

[MsiClawControllerModeProperty.cs](https://github.com/enterTheVoidCode/ClawTweaks/blob/release/v0.3.98.0/XboxGamingBar/Data/MsiClawControllerModeProperty.cs)：普通软件鼠标／控制器模式。

[GamingWidget.MsiClawSettings.cs](https://github.com/enterTheVoidCode/ClawTweaks/blob/release/v0.3.98.0/XboxGamingBar/Features/Devices/GamingWidget.MsiClawSettings.cs)：MSI 硬件功能 UI、灯效、相关设置。

[GamingWidget.LeftMsiDoubleClick.cs](https://github.com/enterTheVoidCode/ClawTweaks/blob/release/v0.3.98.0/XboxGamingBar/Features/Controller/GamingWidget.LeftMsiDoubleClick.cs)：MSI 单击／双击设置交给 helper 检测，Game Bar 窗口关闭时仍能处理。前置 MSI 设置是全局设置的线索。

[TileAction.cs](https://github.com/enterTheVoidCode/ClawTweaks/blob/release/v0.3.98.0/XboxGamingBar/QuickSettings/TileAction.cs)：Steam 菜单、Quick Access、游戏覆盖界面、普通鼠标／HW Mouse 等动作名称与输出线索。

其相关枚举包括 `SteamBpmSteamMenu`、`SteamBpmQuickAccess`、`SteamGameOverlay`、`SteamMenuContextual`。键盘候选输出为 Ctrl+1／Ctrl+2／Shift+Tab；这不证明游戏内场景全部可用。

其他上游入口：[ClawTweaks 仓库](https://github.com/enterTheVoidCode/ClawTweaks)、[ClawTweaksCenter 仓库](https://github.com/enterTheVoidCode/ClawTweaksCenter)。必要时核对 helper 来源与安装版对应关系。

## D. 官方说明与 Windows API 参考

- [Valve：更新后的大屏幕模式](https://store.steampowered.com/news/app/593110/view/3394051164709183116)：Guide 打开系统菜单，Guide＋A 打开 QAM。这是原生操作，不是实体 OEM 键重映射教程。
- [HidHide 官方设置说明](https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/)：隐藏实体输入与应用可见性；不要默认改动用户已有隐藏规则。
- [Microsoft：Register-CimIndicationEvent](https://learn.microsoft.com/en-us/powershell/module/cimcmdlets/register-cimindicationevent?view=powershell-5.1)：PowerShell 诊断的事件订阅参考。
- [Microsoft：LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)：键盘 hook 参考；不覆盖 XInput Guide。
- [Microsoft：SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)：合成键盘／鼠标输入及权限限制；不能据此当成任意手柄按钮注入 API。
- [Microsoft：KBDLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct)：注入标记等 hook 数据结构。
- [Microsoft：Game Bar 运行入口](https://learn.microsoft.com/en-us/xbox/game-bar/quickstart/run-gamebar)：Game Bar 正常打开方式参考。

## E. 防止误接续的检查点

- 没有已经测试通过的“只开 HC 热键即可共存”配置。
- 没有已经确认的“清空 ClawTweaks 动作就透传”结论。
- 没有已经确认的“硬件模式保留所有当前鼠标／宏习惯”结论。
- 没有已经完成的独立路由器、脚本或 Decky 插件。
- 新工具的首个真实证明必须来自 Windows 掌机的诊断和验证。
