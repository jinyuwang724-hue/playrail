# Claw 掌机工具箱：Claude 开发交接

更新：2026-10-10，北京时间。当前已安装版本：**0.12.31**（见 2.5 节；下文 0.12.4 记录仍有效）。

工作目录：`C:\Users\13304\Desktop\Claw-Playhub-ButtonRouter-Handoff`。

这是给 Claude 的最新接续入口。请先完整阅读本文，再根据用户的新任务查看对应源码和验证报告。用户当前只要求整理交接，没有指定下一项开发任务；不要仅根据历史待办自动改动系统或启动开发。

## 1. 项目目标与需求优先级

这是 Windows 掌机上的 Decky 插件，界面名称 **Claw 掌机工具箱**，安装目录名称仍为 `claw-button-router`。目标是在 Steam 大屏幕／游戏中完成常用操作，减少切回桌面。

当前范围包括缩放与锐化、添加非 Steam 游戏、网络代理、按键映射、导航反馈、兼容性修复。

以下最新决定覆盖旧资料：

- **按键接管只看 Steam 大屏幕是否存在**，不再区分 Playhub Desktop／Gaming。最小化、失焦、启动或退出游戏均保持接管；真正退出大屏幕才恢复原映射。
- 当前默认动作是 **MSI → 右侧快捷面板；Xbox → 左侧主菜单**，自动接管开启。不要被旧文档中的反向映射误导。
- 用户已取消 SteamOS 五种缩放模式和四种过滤器的复刻。当前画面需求是**保持宽高比铺满屏幕，允许居中裁掉边缘；通过 Magpie 处理锐化和效果**。
- 用户明确不希望用 Lossless Scaling／小黄鸭代替这套画面功能。原有 ClawTweaks 的相关功能可以保留，不纳入本插件的新画面处理链。
- 功耗、RTSS 控制、全核心模拟 E 核已列入清单，但仍暂缓，不能擅自启用或改系统性能设置。

资料出现冲突时，以用户的新指令、本文和对应最新实测报告为准。

`CODEX_HANDOFF.md` 后半段主要是开发前的历史目标，仍包含已经过时的 Playhub 双模式设计。`FEATURE_BACKLOG.md` 页首基线仍写 0.11.0，其中画面功能范围可以参考，但不代表当前安装版本。`manifest.json` 也保留了一些旧验证标记，不能仅凭单个布尔字段推翻最新报告。

## 2. 当前版本和最后观察到的状态

### 版本与交付物

- 工作区及已安装插件版本：`0.12.4`。
- 安装目录：`C:\Users\13304\homebrew\plugins\claw-button-router`。
- 插件设置目录：`C:\Users\13304\homebrew\settings\claw-button-router`。
- 最新安装包：`artifacts/claw-button-router-0.12.4.zip`。
- 包 SHA256：`c9814593ea51f17ef610eb431a8fe7e193a12e93ef2f5e20f5dfae3bf031b1e3`。
- `artifacts/release-validation-0.12.4.json` 确认 193 个分发文件与工作区、安装目录一致，路由程序及 Magpie 对应源码检查通过。
- 本工作目录**不是 Git 仓库**，没有可依赖的分支或提交记录；恢复主要依靠安装备份、历史安装包和记录。

### 本次交接的只读检查

读取时最新状态报告时间为北京时间 **2026-10-10 20:13:23**：

```text
enabled=true
mode=SteamClosed
ready=true
released=false
gameBarGuardActive=false
helperRunning=true
restorePending=false
error=null
```

检测到一个 `ClawRouter-0.12.4.exe`，以及恢复后的 ClawTweaks helper、Game Bar 和 GameBarFTServer。没有检测到测试游戏 `hamidashi.exe`。

此时大屏幕已关闭，Game Bar 恢复运行符合设计，不能误判成双弹窗修复失效。此前测试后曾把游戏留在标题画面，那是当时的测试结束状态，不是当前仍在运行的保证。进程 PID、窗口 HWND、运行计数及截图都具有时效性，后续操作前重新检查。

## 2.5 0.12.5：全屏游戏侧栏卡顿补帧

《甜蜜女友2》（CatSystem2，D3D9 独占全屏）标题画面约 0.4 FPS，Steam 全屏覆盖只能随游戏帧刷新，导致侧栏卡顿。0.12.5 新增 `plugin/claw_overlay_frames.py`：覆盖可见且前台游戏窗口铺满显示器时以约 60 Hz `InvalidateRect` 让游戏补帧，菜单关闭即停。原生源码未改，仅按版本生成 `ClawRouter-0.12.5.exe`。详见 [FULLSCREEN_OVERLAY_FRAMES_20261010.md](FULLSCREEN_OVERLAY_FRAMES_20261010.md)；实体键复测结果以该报告更新为准。

## 2.6 0.13.0：性能与功耗（替代体感助手，第 1 批）

用户 2026-10-11 决定把体感助手的功能分批并入插件（功耗/限频 → OSD → 风扇曲线），这覆盖了上文"功耗暂缓"。第 1 批：

- `native/ClawPower.cs` → `bin/ClawPower-<版本>.exe`：普通权限 stdio 桥 + 提权执行端（MagpieLauncher 同款管道身份核对）。经 PawnIO（`C:\Program Files\PawnIO`）与 `bin/pawnio/IntelMSR.bin`（取自体感助手内嵌的同一签名模块）读写 MSR 0x610，只改 PL1/PL2 功率字段和使能位，保留时间窗；电源方案写 PROCFREQMAX/PROCFREQMAX1。
- `plugin/claw_power.py`：插电/电池预设（1 W 步进，5–40 W）、手动模式（预设不生效）、快捷档位、PL2=PL1+加成（默认 1 W，同体感助手）、每 3 秒回读并纠正、插拔电源切换。首次写入前把原值存到 `settings/claw-button-router/power-backup.json`，"恢复原始功耗设置"写回并关闭接管。默认不接管。
- `plugin/power.js`：界面模块"性能与功耗"。
- 体感助手的写法来自静态 IL 分析（未运行其代码）：`IntelMSR.ChangeTDP` 写 0x610，`IntelPL2Plus` 加在 PL2；限频还会改 PROCTHROTTLE 和 personality，本插件不改这些。证据和改前值：`diagnostics/power-backup-20261011/`。
- 实测：设 12 W 后封装功耗由约 24 W 降到约 11.5 W，写回 35/36 W 后恢复。体感助手仍在运行时两边会互相改写，界面会提示。

### 0.13.1：智能模式（按帧率自动调功耗）

- 用户不要按游戏分档，要按目标帧率自动给功耗；上限插电 35 W、电池 25 W，下限 8 W，目标默认 60（可选 30/40/45/60/90/120/不限）。
- 帧率来自 Intel PresentMonSharedService 2.5.1 的 `PresentMonAPI2.dll`（在提权的 ClawPower 里，`frames` 命令）。用 Application FPS（指标 62），会排除 XeSS 插帧（帧类型 INTEL_XEFG）；动态查询窗口 1000 ms、偏移 1000 ms。同一命令返回自上次调用以来的封装功耗（0x611 差值）。
- `SmartGovernor`（`claw_power.py`）：高于目标 7% 降功耗；低于目标 5% 且功耗用满（实际≥上限−1.5 W）才升；没用满说明瓶颈不在功耗，往下收；多给 2 W 帧数涨不到 3% 就记住上限 60 秒；没有帧率（菜单、galgame 静止画面）按实际耗电降。`tests/power_backend_test.py` 里有 7 个模拟场景。
- **未验证：** 本机桌面测试时（WPF 动画、Edge 动画）PresentMon 返回的都是 0 帧，原因未确认（可能屏幕没亮或窗口被大屏幕遮住）。需要在真实游戏里看界面上的"真实 N 帧"是否正常，以及 XeSS 插帧游戏是否显示"含插帧显示 M 帧"。如果 API 读不到，备选方案是 RTSS 共享内存（但它包含插出来的帧）。

### 0.14.0：OSD（第 2 批）与鸣潮崩溃

- 鸣潮 3.7.13 在 RTSS 注入、但没有任何 OSD 客户端写内容时，进世界前必崩（游戏代码同一处空指针 +0x168）。体感助手开着（它是 RTSS OSD 客户端）或 RTSS 关掉都正常；用一个只写一行字的测试客户端也不崩（用户 2026-10-11 实测）。
- 因此插件在 ClawPower 里做了 RTSS OSD 客户端（`osd` 命令，slot 所有者 `ClawToolbox`，ANSI 编码，退出时清空），默认开启，与功耗接管独立：只开 OSD 时提权后台也会运行。内容一行：时间、`<FR>` 帧率（插帧时附"真实 N"）、封装功耗、CPU 温度（MSR 0x1A2/0x1B1）、电量。
- 体感助手和插件同时开会出现两行 OSD。
- 0.14.1：OSD 分 4 档（`osd_groups`），单行、颜色区分（RTSS `<Cn=RRGGBB>` 调色板）、单位用 `<S0=50>` 小号；字号小/中/大 = RTSS ZoomRatio 2/3/4，通过 RTSSHooks64 的 Load/Set/SaveProfile 写到 Global 和所有已有游戏配置（原值存在 `settings/claw-button-router/osd-zoom-backup.json`，原来都是 2）。3 档以上数据来自 ClawPower `stats`（性能计数器、BatteryStatus WMI）和 PresentMon 的 1% 低帧、GPU 频率温度功耗查询（真实游戏里能否读到仍待确认）。帧时间曲线未做。

### 0.14.5 / 0.15.0：完整 OSD 恢复，风扇（第 3 批）

- 鸣潮崩溃真因是 Steam 启动参数 `-krqlv=uhd`（见记忆 wuwa-uhd-crash），与 RTSS/OSD/性能计数器无关；0.14.2–0.14.4 的精简已在 0.14.5 撤回（彩色、帧时间、GPU/电池计数器、PresentMon 1% 低帧和 GPU 遥测）。上文 0.14.0 段落里"无 OSD 必崩"的结论作废。
- 风扇走 MSI 官方 WMI `MSI_ACPI`（`ACPI\PNP0C14\0_0`），与 ClawTweaks 相同，不碰 EC 端口：`Get_Fan(0)` 转速、`Get_Fan(1/2)` CPU/GPU 表（[0] 当前占空比，[1..6] 六点，[7] 保留）、`Get_Temperature(1/2)` 温度轴、`Get_AP(1)` bit7 = 软件曲线（`Set_Data 0xD4`）、`Get_Data(0x98)` bit7 = 全速。回复首字节是状态。实测：曲线写 100% 后转速 5160→6000，恢复后交还固件。
- 模式：固件自动（默认）/静音/均衡/性能/自定义 6 点 + 全速。首次切换前备份原表到 `settings/claw-button-router/fan-backup.json`，选回固件时写回并清 bit7。每 9 秒校验，被改回会重写。OSD 3/4 档显示转速和占空比。

## 3. 最新修复：Xbox 键同时打开两个菜单

用户在桌面打开 Steam 大屏幕后，Xbox 键会同时呼出 Steam 左菜单和 Xbox Game Bar。

### 失败路径及最终处理

1. 旧固件过滤器在缺少原始设备确认时回放 Win+G，可能重新打开 Game Bar。
2. 0.12.2 移除回放，接管期间直接屏蔽 Win+G。日志确认四次抑制，但用户仍看到双弹窗，因此**低层键盘过滤成功不等于 Game Bar 已被阻止**。
3. 本机 `UseNexusForGameBarEnabled` 已为 0；单次关闭 Game Bar 后台也无法阻止再次呼出。
4. 开发中尝试的 WMI `Win32_ProcessStartTrace` 在普通权限后台被拒绝访问，已撤回。
5. **0.12.4 使用窗口创建／显示通知处理 Game Bar**：接管时关闭已有后台，重新出现时再次关闭；退出接管停止处理。

实现位置：

- `native/FirmwareShortcutFilter.cs`：接管期间过滤 Win+G，不再回放；普通 G 及其他快捷键放行。
- `native/GameBarGuard.cs`：`SetWinEventHook`、当前会话和应用身份核对、关闭目标进程。
- `native/ClawRouter.cs`：保护随接管、恢复、维护、睡眠和卸载生命周期切换。

只允许处理当前 Windows 会话中，包身份为 `Microsoft.XboxGamingOverlay_8wekyb3d8bbwe` 的 `GameBar.exe`／`GameBarFTServer.exe`。核对及结束目标使用同一进程句柄，避免 PID 复用误关。不按文件名直接结束任意进程；不卸载 Game Bar；不设置定时杀进程循环。

接管期间 Game Bar 本身及其组件入口会被关闭；退出大屏幕／关闭自动接管后恢复原入口。不要为了抑制 Game Bar 强杀 ClawTweaks 的硬件 helper。

### 验证结论

- 20 项接管／目标身份检查和 15 项快捷键检查通过。
- 真实启动本机 Game Bar，观察到新进程被关闭，未用模拟窗口代替。
- 退出大屏幕后，保护关闭、原 helper 恢复，Game Bar 重新运行；重开大屏幕再次接管。
- 用户实体 Xbox 键复测明确反馈：**“只出现 Steam 左侧栏”**。
- 窗口通知发生在应用创建／显示窗口之后，不能说它从 Windows 激活之前阻断。当前本机复测没有反馈闪现，但其他游戏／机器仍需实测。

证据：[GAME_BAR_BUTTON_FIX_20261010.md](GAME_BAR_BUTTON_FIX_20261010.md)、`artifacts/gamebar-guard-verification-0.12.4.json`。

## 4. 最新真实游戏菜单层级测试

测试游戏：《常轨脱离 Creative - Ver1.06》，Steam App ID `1604380`。

```text
D:\SteamLibrary\steamapps\common\常轨脱离Creative\hamidashi.exe
```

通过 Steam 启动，停留在标题画面；最大化窗口和游戏全屏分别测试左右菜单。

| 检查 | 结果 |
| --- | --- |
| 最大化窗口左菜单／右快捷面板 | 实际显示在游戏画面上方 |
| 游戏全屏左菜单／右快捷面板 | 实际显示在游戏画面上方 |
| 关闭菜单 | 正常返回游戏，菜单及覆盖合成状态均归零 |
| 前台窗口核对 | 全屏菜单显示时前台仍为真实游戏窗口 |
| 顶部 RTSS 条 | 仍然可见；未承诺 Steam 高于所有第三方监控条 |

**测试边界：**本轮调用已安装插件唯一的 `router_event` 监听函数，走生产版本菜单处理流程；没有创建第二个路由器、替换 Steam 回调或模拟实体 Xbox／MSI 输入。主页的实体键双弹窗复测已由用户确认；**0.12.4 游戏内实体键没有另做单独复测**。

测试时 Magpie 关闭，未验证本轮 Magpie 开启后的所有层级组合，也未覆盖没有 Steam 原生覆盖支持的其他游戏。游戏全屏不等于已证明独占全屏。

报告：[INGAME_MENU_VERIFICATION_20261010.md](INGAME_MENU_VERIFICATION_20261010.md)。

## 5. 各功能当前范围

### Magpie／等比铺满与锐化

- 已从 Magpie **v0.12.1** 修改源码并编译，运行引擎标识 `0.12.1-claw`，不是仅打开外部 Magpie 界面的链接按钮。
- 本地上游源码：`native/upstream/Magpie-0.12.1`；运行文件：`plugin/bin/magpie`。
- 接入原版 134 个效果、七个默认方案、自定义效果链和参数、四种捕获方式、帧率上限、3D 模式、鼠标速度调整、手动黑边裁剪。
- 效果链最后统一适配屏幕，整数／固定倍数效果也经过最终适配，不能声称与原版严格整数倍率完全相同。
- 游戏权限高于插件时，通过经过双方身份核对的命名管道和原生提权桥启动后台；系统授权提示由用户处理，不修改 UAC。
- 已在《常轨脱离》普通和管理员窗口实测铺满、RCAS、FSR、Anime4K；打开 Steam 菜单暂停，关闭返回游戏后同一后台恢复。
- 134 个效果可选不代表 134 个都逐项实测。HDR、各捕获方式的广泛兼容、触控和其他游戏仍有验证边界。
- 触控辅助、按游戏保存配置、方案导入导出、原版工具栏和开发调试选项尚未全部接入。
- 旧 `ClawScaling`、SteamOS 五模式／四过滤器源码只作历史保留，不重新纳入入口、构建或分发。

先读 [MAGPIE_REAL_GAME_20261010.md](MAGPIE_REAL_GAME_20261010.md)，再按需要读 [MAGPIE_IMPLEMENTATION_20261009.md](MAGPIE_IMPLEMENTATION_20261009.md)。

### 添加非 Steam 游戏

- 展开模块后直接显示完整表单，无额外“添加游戏”展开按钮或表单返回按钮。
- 支持跨 C／D 等磁盘选择 exe／lnk、确认游戏名称及启动文件、选择现有或新建分类。
- 提交后按用户名称调用 Steam 原生改名并等待生效；相同目标可复用入口，失败保留 AppID 继续，不重复添加。
- 分类保存后固定进入**非 Steam 游戏标签页** `/library/tab/DesktopApps`，清除旧合集上下文，避免跳回“旮旯game”合集。
- 名称和分类导航已有真实 Steam API／持久化测试，但旧资料中个别实际面板手动验证仍待用户确认，不一概标为全部通过。
- 历史用户测试路径：`D:\gal\(v60562)(JA)(PC)サクラノ詩 10th\sakuranouta10th.exe`、`D:\gal\ＰＲＥＴＴＹ×Ｃ∧ＴＩＯＮ\ＰＲＥＴＴＹ×Ｃ∧ＴＩＯＮ.exe`。测试新建入口应清理，保留原游戏及分类成员。

### 网络代理／Clash Verge

- 顺序为**网络设置 → 节点选择 → 订阅管理**；只保留一个节点选择入口。
- 规则／全局／直连、系统代理、TUN；底部订阅 URL 输入框、添加订阅、刷新订阅及用户手动验证按钮。
- 使用本机 Mihomo 管道和按需准备的 Clash Verge 原生会话；第一次原生订阅操作可能短暂重启已识别的 Verge，不在打开面板时启动该流程。
- 订阅流程需支持先导入链接、借助可用节点和系统代理、再刷新获取完整节点，失败保留可恢复信息。
- **用户明确要求代理由自己手动验证。** 后续不可为验证文档／界面或无关功能主动切代理、节点、TUN、系统代理、刷新／导入订阅或进行代理联网测试。
- 保留“验证测试”按钮；订阅 URL 可能带令牌，不在日志、交接或提示中输出完整链接。

### UI、导航反馈和兼容性

- UI 使用可扩展的纵向模块列表，避免固定两列／固定位置；状态用简洁文字，用户不喜欢左侧竖线状态卡片。
- 打开选项、未修改退出、重挂载或重复读取不得再次应用设置或重放整组开关动画。模块选择、表单和草稿应保留。
- 导航反馈只在可见导航界面输出；退出／卸载／返回桌面及时停振，避免旧请求重新震动。不因为按键接管忽略前台而让导航震动也常驻。
- 保留原 MotionAssistant 功耗控制、ClawTweaks 手柄／桌面鼠标、RGB、背键等现有配置；不安装第二套虚拟手柄或完整硬件管理器。
- “兼容性修复”分别管理 Steam 启动和 Game Bar 主页修复，具有原值备份和维护交接。不要看到 Game Bar 主页修复开启就误认为它应在 Steam 接管期间显示。

使用说明：[CLAW_TOOLBOX.md](CLAW_TOOLBOX.md)；兼容性说明：[COMPATIBILITY_REPAIR.md](COMPATIBILITY_REPAIR.md)。

## 6. 核心源码地图

| 文件／目录 | 职责 |
| --- | --- |
| `plugin/main.py` | Decky 后端、版本化原生进程、设置、状态与前端会话校验 |
| `plugin/frontend.js` | 插件界面、生产事件监听、各模块组合 |
| `plugin/steam-context.js` | Steam 大屏幕存在状态、MobX 订阅、两秒报告及版本控制 |
| `plugin/menu-router.js` | 主页／真实游戏覆盖菜单、身份确认、无覆盖时的 Playhub 焦点事务 |
| `native/ClawRouter.cs` | MSI WMI 输入、接管、正常停止及恢复 ClawTweaks、动作确认和失败恢复 |
| `native/FirmwareShortcutFilter.cs` | Win+G 接管期间过滤及按键状态配对 |
| `native/GameBarGuard.cs` | Game Bar 窗口事件处理、包身份及会话核对 |
| `plugin/toolbox-ui.js`、`plugin/panel-lifecycle.js` | 模块布局、面板引用和重挂载行为 |
| `plugin/game-add.js` | 游戏表单、名称／分类、成功跳转 |
| `plugin/claw_clash.py`、`plugin/claw_subscriptions.py` | 代理和订阅后台 |
| `native/ClashBridge.cs`、`native/VergeSession.cs` | Mihomo 管道和 Verge 本机原生会话 |
| `plugin/magpie.js`、`plugin/claw_magpie.py` | 画面草稿、效果控制、状态、暂停／恢复 |
| `native/MagpieLauncher.cs`、`native/upstream/Magpie-0.12.1` | 提权桥及修改后的真实画面引擎 |
| `plugin/claw_compatibility.py`、`native/CompatibilityUtility.cs` | 可恢复的本机兼容性修复 |
| `plugin/navigation-haptics.js`、`plugin/claw_navigation_haptics.py` | 导航反馈和停止／恢复 |
| `tests`、`tools`、`diagnostics`、`artifacts` | 检查、开发工具、诊断及历史验证／备份 |

编辑源码文件，`plugin/dist/index.js` 由构建生成，不作为唯一修改入口。

## 7. 接管和恢复必须保留的行为

- 大屏幕判断来自 Steam 主实例为 GamepadUI 且存在大屏幕主窗口；不能仅用前台、可见性、运行游戏或残留 overlay 判断。
- 前端每两秒报告，15 秒失联保护恢复原映射；拒绝旧前端标识和旧递增版本。
- 原生一秒 watchdog 只读内存状态并在状态变化时交接，不改回空闲 HTTP／进程扫描。
- Playhub 端口默认 47991，实际从 `%APPDATA%\GamingMode\config.json` 的 `safety.apiPort` 读取，仅用于原有无覆盖窗口菜单焦点服务。
- ClawTweaks 使用 `shutdown-request.json`／`shutdown-ack.json` 协议正常退出，原计划任务 `\ClawTweaks\ClawTweaksHelper` 恢复。
- `settings\claw-button-router\restore.json` 是恢复记录，不因为它存在就直接删除。核对当前含义和 `restorePending`。
- 关闭接管／退出大屏幕／失联／菜单失败／睡眠／卸载须恢复原后台，禁止为解决锁文件强杀硬件 helper。
- 真实游戏优先使用匹配 appID、PID、browserID、完整 gameID 的 Steam 原生覆盖窗口；无覆盖时走已有 Playhub 焦点事务，不能盲目把主页窗口放到游戏前面。
- 不替换 Steam 自己占用的 `RegisterOverlayBrowserInfoChanged` 回调槽；订阅已有 fan-out。
- 不重复执行 Decky `connection.connect()`，它可能替换生产监听。调试用现有连接。

详细生命周期验证：[STEAM_OWNERSHIP_20261010.md](STEAM_OWNERSHIP_20261010.md)。

## 8. 构建、安装、重载和验证

### 本机工具

- Shell：PowerShell；原生路由用 Windows .NET Framework C# 编译器。
- 编译器：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`。
- Magpie 已安装 VS2022 Build Tools／v143、UWP C++、Windows SDK 26100、Conan、CMake；MSBuild 在 `C:\BuildTools2022\MSBuild\Current\Bin\MSBuild.exe`。不要重复安装环境。
- 上次使用的 Node／Python 路径如下；这些属于 Codex 提供的本机运行时，Claude 启动时检查是否仍可用，也可使用兼容的独立 Node／Python。

```powershell
$clawNode = 'C:\Users\13304\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
$clawPython = 'C:\Users\13304\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
```

### 修改后的正常流程

1. 先读取实际版本和运行状态，保留原设置和相关备份。
2. **原生程序修改必须用新的版本化文件名。** 同步 `plugin/plugin.json`、`plugin/package.json` 和 `manifest.json` 的版本信息后构建，不覆盖正在运行的 `ClawRouter-0.12.4.exe`。
3. 只改路由／前端时优先 `build.ps1 -RouterOnly`，避免重建被其他组件使用的二进制；单纯前端修改可 `-FrontendOnly`。
4. 做与修改相关的检查，安装后使用现有 Decky 连接正常重载，让旧后台完成恢复和退出。
5. 核对新版本、仅一个原生后台、ready／released／helper 状态和实际 UI，再打包检查。

```powershell
.\build.ps1 -RouterOnly
.\install.ps1
& $clawNode .\tools\steam-probe.mjs 'window.DeckyBackend.call("loader/reload_plugin","Claw 掌机工具箱")'
& $clawNode .\tools\steam-probe.mjs --plugin-state
.\package.ps1
& $clawPython .\tools\verify-magpie-release.py
```

`-FrontendOnly` 不生成新版本原生文件。如果同时升版本，必须准备相应的 `ClawRouter-<version>.exe`；不要让安装脚本引用不存在的版本。

`install.ps1` 在 `artifacts/plugin-backup-*` 保存被替换文件，跳过哈希相同的文件。不要绕开版本化机制去覆盖运行中的文件，也不要盲目恢复整套旧包覆盖最新 Magpie 功能。

### 检查入口

- `test.ps1`：编译并运行原生策略／快捷键检查，随后执行一组 Node 检查；要求 `node` 在当前 shell PATH 中。部分旧 scaling 检查是历史保留，按修改范围选择。
- `tests/backend_test.py`：桥接和前端会话。
- `tests/magpie_backend_test.py`、`tests/magpie.test.mjs`：画面效果、参数和草稿。
- `tests/game-add.test.mjs`：游戏表单／名称／分类／导航。
- `tests/menu-router.test.mjs`、`tests/steam-context.test.mjs`、`tests/panel-lifecycle.test.mjs`：菜单、接管与面板。
- `tools/verify-steam-ownership.mjs`：真实大屏幕生命周期，**会最小化、退出并重开大屏幕**，并非纯只读；不要在用户游戏中随手执行。
- `tools/verify-game-add-name.mjs`：真实测试入库及清理；会写入 Steam 库，应按任务选择。
- `tools/verify-magpie-game.mjs`：`state` 为读取，`apply`／`pause`／`resume`／`stop` 会改变真实画面状态。
- `tools/verify-magpie-release.py`：比较 ZIP、工作区、安装文件和 GPL 对应源码。

不要为编写文档重新跑整套实机动作。测试窗口、自动状态交接、真实菜单显示与实体键按压要分别说明，不能相互替代。

### 修改 Magpie 源码时

```powershell
.\build-magpie.ps1
& $clawPython .\tools\magpie-catalog.py
& $clawPython .\tools\package-magpie-source.py
.\build.ps1 -FrontendOnly
```

先通过插件正常停止自己的 Magpie 实例，再更新运行文件。重新分发时保留 GPL 许可、NOTICE 和对应修改后源码；效果目录和 source.zip 不能遗失。未改 Magpie 时不要为了升级按键路由重编整个画面引擎。

## 9. 本机诊断与 Claude 工具差异

Steam 已有调试端点曾为 `http://127.0.0.1:8080/json/list`，Decky 前端服务使用 1337。`tools/steam-probe.mjs` 默认选择 `SharedJSContext`，通过现有 `DeckyBackend` 调用，不创建新连接。

运行日志：`C:\Users\13304\homebrew\settings\claw-button-router\events.jsonl`；历史自动验证主要在 `artifacts` 和 `diagnostics`。`get_state` 中部分计数来自最近一次状态消息，可能不是即时刷新，需结合日志和当前进程判断。

Codex 此前使用了它的 Computer Use 工具抓取真实游戏画面。Claude 不一定拥有这些工具；普通源码、PowerShell、Node 和 Python 流程不依赖 Codex 工具，但不能照抄 `tools.*`、`sky`、Codex 的内部权限或截图句柄，假定在 Claude 中同样可用。缺少界面操作工具时，先做可完成的检查，再让用户完成必要的实体输入；不能把模拟窗口截图当作游戏验证。

## 10. 尚未推进的事项及协作偏好

- 按用户下一条任务决定继续方向。可供后续确认的边界包括游戏内实体键复测、没有 Steam 覆盖支持的游戏、Magpie 触控／自动配置／导入导出；这些不是本次交接授权启动的新任务。
- 功耗／RTSS／全核心模拟 E 核只在清单中，暂不开发。
- 曾调查睡眠在包里被唤醒发热。用户**不想改为休眠**，希望仅电源键唤醒；仅供电拓展坞插拔能唤醒，而当次包里事故没有外接设备。原因不能直接归因于键盘或拓展坞；调查暂缓，别顺手调整电源策略。
- 非代理任务可自行做必要验证；代理实机动作仍由用户手动验证。
- 用户希望直接完成已明确授权的工作，减少反复确认；同时如实说清楚验证范围，不把编译成功、日志成功或理论可行描述成实体测试通过。
- 实际游戏测试优先停在标题画面，保留存档、Steam 库、合集成员和原有软件设置。

## 11. 给 Claude 的起始提示

可以把下面这段发给 Claude，并让它打开整个工作目录：

> 请先完整读取项目根目录 CLAUDE_HANDOFF.md，再根据我接下来给出的任务继续 Claw 掌机工具箱开发。当前基线为已安装的 0.12.4。保留 Steam 大屏幕存在即接管的逻辑、Game Bar 窗口保护和现有 Magpie 集成。旧 CODEX_HANDOFF.md 后半段是历史需求，不要恢复 Playhub 双模式或已取消的 SteamOS 缩放复刻。先核对本机状态，按任务修改并完成必要检查；代理相关实机动作由我手动验证。明确区分实际游戏菜单显示与实体按键复测。

如果只移交插件 ZIP，Claude 将拿不到完整开发源码、工具及历史证据；继续开发应打开／保留这个完整工作目录。
