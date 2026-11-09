# 自制掌机前端，取代 ClawTweaks：方案

2026-10-12 · 只读调查，设备上没有改动任何东西。

## 1. 我对需求的理解

你说的“掌机前端”和“Xbox 组件”，在本机上正好对应 ClawTweaks 现在装的两个包（只读查看 `AppxManifest.xml` 得到）：

| ClawTweaks 的包 | 实际是什么 | 对应你说的 |
|---|---|---|
| `MSIClaw.ClawTweaksCenterFSE`（“ClawTweaks Library”） | 注册为 `windows.gamingApp` 扩展、带 `gamingHome` 自定义能力的身份包，指向 `CTW_Center.exe`。也就是 **Xbox 全屏体验（FSE）里可选的“主页应用”**：开机/按 Xbox 键进入的全屏游戏库 | 掌机前端 |
| `MSIClaw.ClawTweaks` | **Xbox Game Bar 小组件**（`microsoft.gameBarUIExtension`，`XboxGamingBar.exe` + 全信任 `XboxGamingBarHelper.exe`），游戏里按 Xbox 键在 Game Bar 里调功耗、手柄等 | Xbox 组件 |
| ClawTweaks Core（Win32） | 后台：MSI 键、背键、桌面鼠标、RGB、陀螺仪、按游戏切换手柄配置、性能模式；顺带装了 ViGEmBus 1.22 和 HidHide 1.5 | 要一起接手的手柄功能 |

所以我按这个理解来规划：

1. **掌机前端**：自己的全屏主页应用，可以设为 Xbox 全屏体验的主页（替代 Xbox App / ClawTweaks Library），用手柄浏览和启动游戏（Steam、Xbox/Game Pass、其他启动器、你手动加的游戏），并能进入工具箱设置。
2. **Xbox 组件**：自己的 Game Bar 小组件，游戏里按 Xbox 键就能调现在插件里的那些功能（功耗、风扇、OSD、震动、Magpie、手柄设置），不再依赖 Steam 大屏幕。
3. **后台服务**：一个开机自启、不依赖 Decky 的常驻进程，承载现有的功耗/风扇/OSD/震动/Magpie 逻辑，并接手 ClawTweaks Core 还在做的手柄功能。

现有 Decky 插件保留，改成这个后台服务的一个“皮肤”，你在 Steam 大屏幕里用起来不变。

本机条件都满足：开发者模式已开（`AllowDevelopmentWithoutDevLicense=1`），ClawTweaks 两个包都是开发者签名侧载，FSE 的自定义能力用的是 `AllowAny="true"` 的 SCCD，在开发者模式下自己签名也能装。系统是 25H2，FSE 和 Game Bar 都在。

> 另一种理解是只要“一个在桌面也能呼出的快捷面板”，不做游戏库。这个是本方案的子集（第 2 阶段就能得到），如果你只要这个，后面的游戏库阶段可以不做。

## 2. 与 ClawTweaks 的功能差距

来源：依赖核查线程（只读）、项目记忆、本次只读检查。

| 功能 | 现在谁在管 | 插件已有？ | 实现方式 | 需要虚拟手柄？ |
|---|---|---|---|---|
| 功耗 PL1/PL2、CPU 频率、智能模式 | 插件 | 有 | PawnIO 写 MSR 0x610（`ClawPower.cs`） | 否 |
| 风扇曲线 | 插件 | 有 | MSI_ACPI WMI | 否 |
| OSD | 插件 | 有 | RTSS | 否 |
| 震动强度 | 插件 | 有 | 手柄固件 HID 0x22/0x23 | 否 |
| Magpie / 缩放 | 插件 | 有 | 自带 | 否 |
| MSI 键、Xbox 键（大屏幕内） | 插件 | 有 | MSI_Event WMI（`ClawRouter.cs`） | 否 |
| MSI 键单击/双击（大屏幕外） | ClawTweaks | 无 | 同上，去掉“只在大屏幕路由”的限制 | 否 |
| Game Bar 主页兼容修复 | 插件，但靠修补 ClawTweaks helper | 半有 | 改成自己的实现（`GameBarHomePreference.cs` 已有雏形），不再修补 ClawTweaks | 否 |
| 性能模式（现在 Balanced） | ClawTweaks | 无 | MSI_ACPI WMI 设一个固定模式，功耗交给插件 | 否 |
| RGB 灯光（白色 50%） | ClawTweaks | 无 | 手柄固件 HID（Linux hid-msi 有协议） | 否 |
| 背键 M1/M2 | ClawTweaks | 无 | 优先用固件重映射（hid-msi 支持把 M 键映射成按键/手柄键）；复杂宏才需要软件 | 多数情况否 |
| 桌面鼠标模式 | ClawTweaks | 无 | 固件有桌面模式（键鼠）切换；或软件把摇杆转鼠标 | 固件方式否 |
| 摇杆死区 | ClawTweaks | 无 | 要先确认 ClawTweaks 是写固件还是软件处理；软件处理就需要虚拟手柄 | 待确认 |
| 陀螺仪（体感转摇杆/鼠标） | ClawTweaks | 无 | 读 IMU，经虚拟手柄输出 | **是** |
| 按游戏切换手柄配置（鸣潮、绝区零） | ClawTweaks | 无 | 前台进程变化时切换固件配置/软件映射 | 视内容 |
| 软件“增强震动”（弱震放大、曲线） | 无 | 无 | 截获游戏发给虚拟手柄的震动再改写给实体手柄 | **是** |
| FSE 主页游戏库 | ClawTweaks Library | 无 | 新做 | 否 |
| Game Bar 小组件 | ClawTweaks | 无 | 新做 | 否 |

结论：大部分手柄功能可以走**固件**，不必上虚拟手柄。真正需要 ViGEm + HidHide 的只有陀螺仪、软件死区（如果固件不支持）和增强震动。依赖核查也确认 ClawTweaks 的手柄子系统现在没在运行，你目前没有在用虚拟手柄。

## 3. 架构建议

```
┌──────────── 前端（三个皮肤，同一套接口）────────────┐
│ ① FSE 主页应用      ② Game Bar 小组件     ③ Decky 插件（大屏幕） │
└───────────────┬──────────────┬────────────────┘
                │  本机 WebSocket（127.0.0.1 + 每次开机生成的令牌）
        ┌───────┴──────────────────────────┐
        │  Claw Agent（常驻后台，开机计划任务启动）  │
        │  现有 Python 逻辑：power / fan / osd /     │
        │  rumble / magpie / scaling / haptics      │
        │  新增：手柄模块、游戏库、按游戏配置          │
        └───────┬──────────────────────────┘
                │ stdio / 命名管道（和现在一样）
   ClawPower.exe  ClawRouter.exe  ClawTouchBridge.exe  ClawPad.exe（新，C#）
   PawnIO/MSR     MSI_Event WMI   提权触控/重绘          HID 固件、ViGEm、HidHide
```

### 3.1 后端复用：把 Decky 外壳换掉，逻辑原样搬

查过代码：`claw_power.py`、`claw_rumble.py`、`claw_magpie.py`、`claw_scaling.py`、`claw_compatibility.py`、`claw_navigation_haptics.py`、`claw_overlay_frames.py` **一处都没有引用 `decky`**；只有 `main.py` 用到 7 处（设置目录 `DECKY_PLUGIN_SETTINGS_DIR` 和 `decky.emit` 推送事件）。

所以第一步很便宜：

- 新写 `agent.py`，替代 `main.py` 里 Decky 的部分：设置目录换成 `%LOCALAPPDATA%\ClawToolbox`，`decky.emit` 换成 WebSocket 推送，方法调用（`get_power_state`、`save_power`、`set_rumble_strength`……）保持同名同参数。
- 自带嵌入式 Python（官方 embeddable 包），用任务计划程序“登录时启动”，不依赖 Steam、Decky、Playhub。
- `ClawPower.exe` 等 C# 助手不用改：它们现在就是自己用 `runas` 提权（本机 UAC 设置为静默提权），交给谁启动都一样。
- Decky 插件的 `main.py` 改成薄代理：转发到 Agent。**关键约束：同一时间只能有一个进程在跑功耗/风扇调速**，否则两个调速器互相覆盖，所以“Agent 上线”和“插件改代理”必须在同一个版本里切换。
- 现有 Python 测试（`tests/*_backend_test.py`）原样继续用。

长期如果想只剩一个运行时，可以再把 Python 逻辑移植到 C#，但不是前提。

### 3.2 手柄模块（新，C#）

`ClawPad.exe`，提权运行，分两层：

1. **固件层（先做）**：通过手柄的配置接口（MI_01，和震动强度同一条通道）读写 M1/M2 映射、手柄模式（XInput/桌面键鼠）、RGB、震动。协议参考 Linux `hid-msi` 驱动。好处：不引入虚拟设备，反作弊零风险，ClawTweaks 关掉后设置仍留在固件里。
2. **虚拟手柄层（可选，后做）**：HidHide 把实体手柄只对 ClawPad 可见，ViGEm 虚拟一个 Xbox 360 手柄给游戏，中间做陀螺仪、软件死区、增强震动、宏。按游戏开关，默认关。

### 3.3 前端 UI 技术选型

| 部件 | 推荐 | 理由 |
|---|---|---|
| Game Bar 小组件 | UWP/XAML 小组件 + 全信任助手（和 ClawTweaks 同构），界面用 **WebView2** 加载 Agent 提供的网页 | Game Bar 自己负责叠加在游戏上、手柄导航、打开时游戏收不到输入、独占全屏兼容；这些都是自己做悬浮窗最难的部分。界面写成网页，三个皮肤共用一套 |
| FSE 主页应用 | WinUI 3 或 WPF 外壳 + WebView2；用身份包声明 `windows.gamingApp` + `gamingHome` 能力（照 ClawTweaks Library 的做法） | 设置 → 游戏 → 全屏体验里就能选它当主页 |
| Decky 皮肤 | 保留现有 `toolbox-ui.js` 等 | 大屏幕体验不变 |
| 界面代码 | 网页（Preact/纯 JS），手柄用方向键焦点导航 | 现在插件的分组设计可直接移植，小组件、主页、Decky 三处看起来一致 |

**不用 Steam 怎么呼出：**

- Xbox 键：Windows 原生打开 Game Bar，我们的小组件固定在里面；在 FSE 里 Xbox 键回主页，就是我们的游戏库。不需要抢 Xbox 键。
- MSI 键：`ClawRouter` 已经能收 MSI_Event；现在只在大屏幕里路由（`Policy.CanRoute` 要求 `mode == "BigPicture"`）。扩展成按场景分派：大屏幕 → Steam 快捷面板（不变）；游戏中/桌面/FSE → 打开 Game Bar 并定位到我们的小组件，或执行你指定的动作（Magpie 开关、性能切换等），单击/双击/长按可分别配置。

我不推荐自己画一个置顶悬浮窗当游戏内面板：独占全屏游戏会被挤到后台；面板拿焦点会让游戏暂停或 Steam 覆盖层关闭（项目里已经踩过，见侧栏自动收回那次）；不拿焦点则游戏会同时收到导航按键，除非上虚拟手柄。Game Bar 已经解决了这三件事。

## 4. 风险

| 风险 | 说明 | 对策 |
|---|---|---|
| 反作弊 × 虚拟手柄 | 部分反作弊会拦截或标记 ViGEm/HidHide（虚拟 Xbox 手柄、隐藏实体设备）。米哈游、库洛的游戏有自家反作弊 | 固件优先；虚拟手柄层按游戏白名单启用，鸣潮/绝区零默认不启用；先测再开 |
| ViGEm 已停止维护 | ViGEmBus 作者 2023 年宣布停更，驱动仍可用但不会再修 | 只把它当可选层；关键功能不依赖它 |
| HidHide 残留 | 服务崩溃后实体手柄可能仍被隐藏，表现为“手柄没了” | 退出/崩溃时由看门狗撤销隐藏；提供一键恢复 |
| 卸载 ClawTweaks Core 会带走驱动 | 它的卸载项写着“完整清理”，ViGEmBus 和 HidHide 很可能是它装的 | 卸载前先装我们自己的驱动副本或确认版本，再卸 |
| 提权 / UIPI | Steam 和游戏是管理员运行（见项目记忆），非提权进程对游戏窗口的操作会被静默丢弃 | Agent 普通权限，涉及游戏窗口和硬件的助手提权（沿用现有做法）；ClawPad 提权 |
| 过渡期冲突 | MSI 键两边都会收到；ClawTweaks 启动时会把震动强度写回 100%；性能模式切换可能改写功耗上限；两边都开风扇会互相覆盖 | 按功能逐项移交（见路线图），每移交一项就在 ClawTweaks 里关掉对应项，而不是卸载 |
| 开发者模式依赖 | FSE 主页能力和自签名包依赖开发者模式 | 关掉开发者模式会让这两个包失效，ClawTweaks 现在也一样 |
| Game Bar 小组件开发成本 | UWP 小组件 SDK 文档少、调试麻烦 | 第 2 阶段先做最小小组件验证可行，再填功能 |

## 5. 分阶段路线

每一阶段结束时 ClawTweaks 都还在运行，只把已经接手的那几项在 ClawTweaks 里关掉。最后一步卸载前单独问你。

| 阶段 | 内容 | 完成标志 | ClawTweaks 状态 |
|---|---|---|---|
| 0 | 合并现有分支（UI、风扇、智能功耗、震动）到 main | 一个干净的基线版本 | 不变 |
| 1 | **Agent 脱离 Decky**：`agent.py` + 开机计划任务；Decky 插件改薄代理 | 不开 Steam 时风扇/功耗/OSD/震动照常工作；大屏幕里体验不变 | 不变 |
| 2 | **Game Bar 小组件 MVP**：功耗、风扇、OSD、震动、Magpie 五项；MSI 键在大屏幕外也能呼出 | 桌面、FSE、游戏中都能调这五项 | 不变 |
| 3 | **固件手柄功能**：RGB、M1/M2、桌面鼠标模式、性能模式固定、按游戏切换配置；Game Bar 主页修复改成自己的实现 | 这些项在 ClawTweaks 里逐一关掉后功能不丢 | 只剩陀螺仪等少数项 |
| 4 | **FSE 主页游戏库**：Steam/Xbox/手动添加的游戏，封面、启动、按游戏设置入口 | 能在设置里选它当 FSE 主页并日常使用 | 可停止自启（先问你） |
| 5（可选） | **虚拟手柄层**：陀螺仪、软件死区、增强震动，按游戏白名单 | 需要的游戏里能用，反作弊游戏不受影响 | 卸载（先问你，先保驱动） |

## 6. 需要你确认的事

1. 理解对不对：掌机前端 = FSE 主页游戏库，Xbox 组件 = Game Bar 小组件。
2. 按上面的顺序先做阶段 1（Agent 脱离 Decky），它风险最小、后面所有东西都要靠它。
3. 陀螺仪和增强震动你是否真的要用？不要的话可以永远不碰虚拟手柄，整体风险小很多。
