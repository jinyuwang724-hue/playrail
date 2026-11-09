# 体感助手（Motion Assistant）现状盘点

盘点时间：2026-10-11，只读，未改动任何设置、未停止或卸载体感助手。
插件版本：Claw 掌机工具箱 0.12.31。

## 1. 安装与启动

| 项目 | 现状 |
|---|---|
| 程序 | Motion Assistant 1.2.11（作者 Frank Dong，MSI 安装包 `{678CBED4-5553-4CC5-A9EE-955662EE3C15}`，2026-10-04 安装） |
| 目录 | `C:\Software\体感助手\`，主程序 `MotionAssistant.exe`（.NET Framework 4.8，WinForms） |
| 进程 | `MotionAssistant.exe` 以管理员权限运行（普通权限读不到路径），由计划任务服务拉起 |
| 自启 | 计划任务 `\MotionAssistant`：登录触发，最高权限，无参数 |
| 启动方式 | `StartMode=Minimum`、`minSize=True`：启动后最小化到托盘 |
| 注册表 | 只有 `HKCU\Software\Frank Dong\Motion Assistant` 下的安装器快捷方式记录，无功能配置 |
| 配置 | `Profiles\Global.ini`（全局）、`Profiles\General\default.ini`（当前方案，`lastFile=default`）；`Profiles\Process\` 为空，没有按游戏的方案 |
| 附带服务 | `PresentMonService`（体感助手自带一份，手动启动，正在运行），用于读 FPS |
| 附带驱动 | `PawnIO`（运行中，用于读写 MSR/EC，即功耗和风扇），`ViGEmBus`（运行中，虚拟手柄），`HidHide`（运行中，多个工具共用） |

## 2. 当前启用的功能与参数

### 功耗（TDP）——已启用
- `AutoSetTDP=True`：按电源状态自动设功耗
- 插电 `ACTDP=35` W，电池 `DCTDP=25` W
- `IntelPL2Delta=1`：推测是 PL2 = PL1 + 1 W（推断，未验证）
- 快捷预设 `CustomTDP1/2/3 = 15 / 20 / 30` W
- `UnitedTDP=False`：插电和电池分开设

### CPU 频率与核心——已启用
- 最大频率 `MaxCPUFreqAC=2750`、`MaxCPUFreqDC=2750` MHz
- 当前电源方案（Claw Xbox Sleep Display）实际值：插电 `PROCFREQMAX=2750`（0xabe），**电池为 0（不限制）**。配置写的是 2750，但电池这一项没生效；可能体感助手只在切到电池时才写，盘点时是插电状态（推断）
- 核心设置 `ACCoreSet / DCCoreSet = AllCoresAuto`：没有限核或强制 E 核
- 睿频模式 `PERFBOOSTMODE` 插电和电池都是 1（已启用），看起来是系统默认，不是体感助手改的（推断）

### 风扇——已启用
- `FanControlEnable=True`，当前选第 1 条曲线（`FanSettingSelect=0`）
- 4 条曲线原始值（含义推测为：启用;最高温度?;各档转速%…;步长?，未验证）：
  - 1：`True;40;20;30;50;80;5`
  - 2：`True;60;30;50;70;90;10`
  - 3：`True;80;40;60;80;100;10`
  - 4：`True;100;50;70;90;100;10`

### 性能浮窗（OSD）——已启用
- `OSDEnabled=True`，单行（`OSDType=OneLine`），显示时间、项目名，FPS 大字，显示 Lossless Scaling 的 FPS（`OSDLSFPS=True`），字号 3，默认白色
- 通过 RTSS 显示，FPS 来自自带的 PresentMonService
- `RTSSShow=False`：不显示 RTSS 自己的窗口
- 硬件监控开启（`DisableHWMonitor=False`）
- 帧率限制 `ACRTSS=0 / DCRTSS=0`：不限帧

### GPU——基本未用
- `gpuPolicy=Auto`，`AutoLockGPU / optimizeGPU / optimizeCPU / useCustomGPU = False`，`GPUClock=0`

### 体感（陀螺仪）——装着但没自动启用
- `gyroType=USB`，模拟为鼠标（`simulateType=Mouse`），One Euro 滤波（`minCutOff=0.07, beta=0.07`）
- `AutoSimulate=False`：不会自动开启体感；能否手动在界面开着无法从配置判断
- DSU 服务器关闭，PS4 模拟关闭，JSL 鼠标关闭

### 热键——全部关闭
- `HotkeyEnables` 45 项全为 False，热键震动、声音都关

### 其他
- 屏幕键盘 `Tabtip`，触摸屏 `HID\NVTK0603&COL01\...`
- 冻结进程功能未用（`SingleFreeze=False`，`frozen_pids.txt` 为空）
- 测试记录：`Records\` 里有 2026-10-04 一次艾尔登法环帧率测试

## 3. 相关的其他自启项（供"少开软件"参考）

| 软件 | 启动方式 | 与本次相关 |
|---|---|---|
| RTSS 7.3.7 | 计划任务 `\RTSS`，`/s`，最高权限 | 体感助手 OSD 依赖它；`Profiles` 下已有约 60 个按游戏的配置（2026-10-06 生成） |
| ClawTweaks | 计划任务 `\ClawTweaks\ClawTweaksHelper` | 手柄／背键／RGB，按 handoff 约定保留 |
| MSI Center M | 计划任务和服务都已**禁用** | 不在用 |
| Intel PresentMonSharedService | 服务，自动 | Intel 自带的一份 PresentMon，插件可直接用它取 FPS |
| Lossless Scaling | HKCU Run | 与 OSD 的 LS FPS 显示有关 |

## 4. 插件接管可行性

| 功能 | 可行性 | 说明 |
|---|---|---|
| 自启 | 容易 | 插件本身已随 Decky 启动，接管后删掉 `\MotionAssistant` 计划任务即可（需用户确认） |
| CPU 最大频率 | 容易 | 写电源方案 `PROCFREQMAX`（插电/电池分开），插件已有管理员 helper（0.12.27） |
| 核心设置 | 容易 | 现在是"全核自动"，插件不做任何事就等于现状；以后做限核/E 核可走电源方案的核心调度项 |
| 帧率限制 | 容易 | 现在不限帧；以后可写 RTSS 配置或共享内存 |
| 性能浮窗 OSD | 中等 | 需要 RTSS 共享内存写 OSD 文本，FPS 用 Intel 自带 PresentMon；单行布局、Lossless FPS 要额外处理 |
| 功耗 TDP（35/25 W 自动切换） | 中等，最关键 | Intel 平台通过 MSR 0x610 / MMIO 设 PL1/PL2；PawnIO 驱动已装好并在运行，插件 helper 可调用。需要监听插电/拔电自动切换。风险中等：写错会被 EC/固件覆盖或影响稳定，需先只读核对当前 PL1/PL2 |
| 风扇曲线 | 难，建议后放 | 需写 Claw 的 EC 或 MSI WMI，曲线格式要先逆向确认；写错影响散热 |
| 体感陀螺仪 | 难且目前不需要 | 现在没自动启用，热键也全关；需要 ViGEm/鼠标模拟和传感器读取，工作量大 |
| GPU 设置 | 不需要 | 全是默认/自动 |

**结论**：现在真正在用的是"自启 + 功耗 35/25 W 自动切换 + CPU 2750 MHz 限频 + 风扇曲线 1 + 单行 OSD"。插件先做功耗、限频、OSD 三项，就能覆盖大部分日常作用；风扇曲线是唯一难点，要么继续交给体感助手或系统默认，要么单独研究。体感和热键没在用，可以不做。
