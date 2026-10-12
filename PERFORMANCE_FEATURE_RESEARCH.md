# Claw 性能与显示：本机调研记录

日期：2026-10-08。环境：Windows Claw，工具箱 0.8.2。本轮为只读调研；没有切换功耗、缩放、核心设置或代理。

## 当前结论与优先级

最新需求以用户指定的两项为准：缩放模式“自动、整数、适应、拉伸、填充”，缩放过滤器“线性、最近、FSR、NIS”。这是当前唯一 P0，详见 [专项调研](STEAMOS_SCALING_RESEARCH.md) 与 [功能清单](FEATURE_BACKLOG.md)。

小黄鸭替代路线已取消，包含其 DLL 与 Playhub 控制接口。Windows 需独立的游戏画面捕获、GPU 算法与输出后端；可以实现对应功能规则，尚不能保证与 SteamOS 合成器完全相同的延迟或全部游戏兼容性。锐度归属于 FSR／NIS 过滤器，不再列独立功能块。

体感助手功耗、RTSS、全核心模拟 E 核仍在清单，当前暂缓，不继续扩展调研或开发。

## 本机基础（此前只读证据）

| 组件 | 观察 | 后续意义 |
|---|---|---|
| Windows／GPU | Windows 11，Intel Arc B390 | 为 Windows 窗口捕获与官方 FSR／NIS shader 提供适配基础，尚未运行验证 |
| MotionAssistant | 窗口 V1.2.1.1，EXE 1.2.1.0，卸载项 1.2.11 | 三种标记不同，以本机行为为依据；接入暂缓 |
| RTSS | 已装 7.3.7，SDK 有 OSD 状态、配置与共享内存说明 | 保留已有游戏检测及监控；新控制功能暂缓 |
| CPU | 系统报告 Intel(R) Arc(TM) G3 Extreme，14 核／14 线程 | 功耗与核心分类不能按早期 Claw 或 AMD 后端套用 |

此前记录本机存在 Lossless Scaling 与 Playhub 缩放接口，这只是软件库存证据；不再作为本需求的推荐路线。原始快照见 [本地记录](diagnostics/claw-performance-research-local.json)。

下面保留暂缓项目的机制与风险边界，供未来开发使用，不改变当前缩放优先级。
## 体感助手功耗接入（暂缓）

本机 default.ini 保存插电 ACTDP=35、电池 DCTDP=25，自定义档位 15／20／30 W；AutoSetTDP=True，UnitedTDP=False。界面也有两套功耗及快捷按钮。它们是配置与 UI 证据，不是当前硬件允许的最大值或完整成功回读证明。

用户已有软件分工明确：体感助手负责性能和功耗，ClawTweaks 保留手柄／鼠标转换等功能。建议工具箱作为体感助手的操作前端，避免再开独立的功耗循环。Playhub 本机的旧 TDP 接口返回 feature_removed，不能拿来作为 Intel 功耗写入后端。

已看到体感助手配置键和热键设置，但没有确认通用 TDP／OSD 的公开实时控制 API。本机 Program.Main 接受启动参数，构造函数主要用于单实例启动／显示；WndProc 已检查的分支用于显示窗口和电源事件，没有发现该处提供功耗设置命令。这是有限检查，不能断言程序完全没有其他接口。

开发前需确认：能否通过受支持的控制途径实时应用并回读 TDP、配置文件是否只有启动时载入、自动功耗循环是否覆盖插件操作、权限失败如何呈现。仅写 ini 后显示“已应用”不合格。当前本机程序含 PawnIO 初始化路径，也不能把 GPD 较早资料中的 WinRing0 描述直接当成当前驱动状态。

## RTSS 监控接入（暂缓）

RTSS 本机 SDK 的 HotkeyHandler 示例通过 SetFlags 操作 OSD 可见性；RTSSProfileInterface.h 说明 EnableOSD 与配置读写；RTSSSharedMemory.h 描述应用帧率及客户端 OSD 数据。体感助手本机配置有 OSDEnabled、OSDType、OSDFontSize、OSDLSFPS 等字段，因此“开关／显示级别／字号”有现成依据，实时控制与持久化还需协调。

本工作区 [此前 OSD 修复记录](MOTION_OSD_FIX.md) 已确认体感助手与 RTSS 的共存路径，并为 54 个游戏主程序建立独立检测配置。RTSS 全局检测仍是 None。后续接入需保留游戏专用配置、体感助手的传感器来源和小黄鸭输出 FPS；不能只切换 RTSS 全局检测就认为解决了全部监控问题。旧修复的运行样本不是本轮性能测评。

建议先支持现有 OSD 的显示／隐藏，随后接入简洁／详细和字号。FPS、帧时间、CPU/GPU 占用、温度与功耗只展示实际数据源提供的项；缺失项显示不可用。RTSS 锁帧单独作为可配套研究，不扩大为本次已确认的监控需求。

## 全核心模拟 E 核：已确认机制（暂缓）

本机程序静态调用链为 AllECoreMaskCheckBox_Click → ProcessorMaskTool.SetAllCoresAsSmall → WriteRegistry。它计算全部逻辑线程掩码，写入：

```text
HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\Kgroups\00
SmallProcessorMask
```

程序自身成功提示明确要求重启。取消选项走 DeleteSmallCoreSetting，删除同名值并提示重启恢复。此处只读取 PE 元数据与 IL，没有加载或执行 MotionAssistant 的方法。原始证据见 [本机静态检查](diagnostics/motion-performance-code-inspection.txt)。

据此可判断该功能是在 Windows 的核心分类设置上把全线程标记为 Small；不能描述为把物理 P 核变成 E 核，也不能等同限频、关闭睿频、进程亲和性或 TDP。它与 TDP 页的“全核自动／优先 E 核”选择也不是同一项。实际调度与性能收益受系统、硬件和游戏影响，本轮没有验证，不承诺降低功耗或提高帧数。

本机该值已存在，为 DWORD 16383（0x3FFF，与 14 个逻辑线程的全位掩码相符）。这证明配置已写入，不能证明当前启动已经应用；没有追溯写入时间和重启状态。

后续 UI 放在高级设置中：显示已保存值、已知生效状态／待重启／未知，明确重启需求，由用户决定重启时机。应保存原始存在状态、类型和值后再写；恢复时还原原值，不能对本机既有设置统一删除。此项属于整机设置，不能作为退出某个游戏便即时恢复的逐游戏开关。

## 暂缓项目的后续验收备忘

1. 体感助手继续负责功耗；应用与回读可靠，失败不显示成功。
2. RTSS 保留原有游戏检测及传感器来源，避免覆盖用户配置。
3. 核心分类保存并恢复原值，明确待重启状态。

本轮未进行这些功能测试，当前只优先研究两项画面缩放功能。
