# 经典 SteamOS OSD 与全局置顶修复

> 当前更新见 [连续曲线、半秒数值与统一功耗](osd-continuous-telemetry-20261025.md)。下文保留历史实现记录；App／Frame Count 显示区域现已移除，当前版本为 Game Bar 0.4.42.0、游戏库 0.1.107.0、ClawPower r10。

已安装：Game Bar 0.4.40.0、游戏库 0.1.105.0、ClawPower 0.17.58-r8。沿用原 Toolbox 小组件布局，单击性能显示循环 L0→L1→L2→L3→L4→L0。OSD 固定、鼠标穿透，位置及缩放从设置修改。所有锚点取消 16px 外边距，直接贴住所选屏幕边缘；L1 还裁去数字周围的透明留白。本次验证后恢复用户当前设置：关闭 OSD，保留四档、左上角、100% 用户缩放。

## 曲线、电池与插帧 FPS 更新

修复真实输出列名 `msBetweenPresents` 与旧解析器要求 `MsBetweenPresents` 大小写不一致的问题。解析器现在兼容大小写及 PresentMon V1/V2 列；支持服务均值与原始事件并行采集，避免有 FPS 却没有绘图点。四档 Frame Count 从单个数值改为真实采集计数历史曲线。无数据的微型曲线也明确显示 N/A。

显示完成事件采集放到现有提权硬件后台，用独立会话、有限队列与分批读取；目标改变、关闭显示或后台退出时只停止本软件拥有的会话。先尝试显示完成跟踪，五秒没有事件时退回 API 呈现事件，避免驱动无法完成显示跟踪时整条曲线消失。

主 FPS 按有效的 displayed→presented→application 顺序选择。显示帧率可包含驱动上报的生成帧，原始渲染 FPS 单独保留供 AutoTDP 使用，不把未知数据直接乘以二。V2 解析能将 Application 与生成帧的显示间隔分别计数，不能获取显示事件时只声明呈现帧率。

BATT 放电时，二档横条右侧新增功耗和剩余时间；三、四档新增 LEFT 行。充电时保留插头。功耗从电量变化推算时用 `~` 前缀区分；续航为按当前耗电估算，数据不足时 N/A。

本次验证：监控 18 项、采集解析与后台桥接 10 项、AutoTDP 20 项、原生绘制 72 种尺寸／缩放通过，并检查绘制后的绿色曲线像素及充放电字段。模拟原始 60 FPS、显示 120 FPS 的生成帧数据，确认主显示选择 120 且原始数据不被覆盖。

真实 Direct3D11 测试已读到 64.89 呈现 FPS，帧时间及计数曲线各 120 个点，记录在 `artifacts/osd-curves-battery/live-state.json`，对应真实预览在 `live-preview/`；放电测试布局在 `preview/fixture-discharge-level-2.png`，其 22.4W／2h05m 是明确的测试数据。本机该验证程序没有读到显示完成事件，已退回呈现采样，真实插帧游戏仍待实测，不能宣称所有插帧方案都已验证。

额外运行的旧 `power_backend_test.py` 在风扇平滑断言处失败：它期待第 4 秒占空比 20，而 HEAD 原有实现返回 22；已确认 FanGovernor 与 HEAD 完全一致，本次未更改该逻辑。本次备份为 `%LOCALAPPDATA%\ClawToolbox\repair-backups\native-osd-20261025-161312`。

## 切换窗口后消失的原因与处理

旧监控循环同时负责帧率采集和向显示窗发送数据。切换前台程序时，ETW 采集清理可能等待数秒；显示窗超过四秒收不到数据便隐藏。原先置顶属性也缺少独立检查，造成切窗后 OSD 消失或落到其他窗口下面。

现在将帧率采集与显示心跳拆开：显示每 200ms 独立发送快照，不等待帧率采集。切换目标立即清空旧帧率，硬件数据继续更新。显示端每 200ms 维护 HWND_TOPMOST，移除窗口 owner，处理窗口层级变化并保持不激活、鼠标穿透。即使输入停顿超过四秒，也只将数据标为 N/A，不因断流隐藏；关闭档位或进程输入结束仍正常退出。

诊断文件为 `%LOCALAPPDATA%\ClawToolbox\data\overlay-render.json`，包括实际可见状态、WS_EX_TOPMOST、owner、前台窗口、绘制结果及字体，便于区分绘制失败和没有采集数据。

## 外观与数据

以用户提供的 `SteamOS_OSD_0-4_复刻规范.md` 及其中 2023 年经典 SteamOS 实机截图为视觉基础，字段和顺序按用户随后直接提出的更新调整；不将这些新增要求归称为已经独立核实的全部新版 SteamOS 规范。

| 档位 | 当前布局 |
| --- | --- |
| L0 | 完全隐藏 |
| L1 | 单个白色 FPS 数字，无单位、无背景 |
| L2 | 黑色半透明背景铺满屏幕宽度，左右均无空隙，整组元素水平居中；单行 FPS＋绿色帧时间曲线→CPU→GPU→RAM→VRAM→BATT，紫色细分隔线；没有帧时间数字，不换行 |
| L3 | GPU、CPU、VRAM、RAM、BATT、FPS 紧凑纵向列表；GPU/CPU 含频率、温度与功耗，VRAM 含频率，底部 Frametime 曲线和最小／最大值 |
| L4 | Time 标签及时间，不显示软件版本；GPU/CPU 频率，逐逻辑核心负载和频率文本，内存、电池、风扇、FSR 状态、Frametime、采集帧数、App 和下方新增的 Display Hz 曲线 |

颜色取文档指定值：白色数值，GPU #2E9762，CPU #2E97CB，VRAM #AD64C1，RAM #C26693，BATT #FF9078，引擎 #EB5B5B，曲线 #00FF00，背景 #020202。采用直角、约 50% 背景 alpha、黑色文字描边及上标单位。

字体使用私有 Unispace HUD，按截图字形匹配；其源字体 Unispace Bold 来自设计师官方公共领域字体包，转换为兼容 GDI+ 的轮廓并保留许可。不安装系统字体，也不将其称为已经核实的某一 SteamOS 版本官方字体。L4 负载和 FPS 动态颜色采用目前 MangoHud 上游默认渐变；60%／90% 负载、30／60 FPS 是本项目采用的规则，并非已确认的 2023 SteamOS 官方阈值。

FPS 标签统一为 FPS。FSR、App 原生帧时间或传感器不可获取时显示 N/A，不伪造 Gamescope、Wine 或游戏插帧信息。逐核心频率来自实际 Processor Information 计数器，核心数量按设备动态展开。VRAM 频率读取 Intel Sysman 的 memory frequency domain 的 actual MHz，禁止使用 GPU 核心频率或内存标称传输速率代替。当前本机驱动未提供该域，因此显示 N/A。

Display Hz 使用 DwmGetCompositionTimingInfo 的 rateRefresh，保留最近 120 次硬件采样并绘制独立 Hz 曲线。这是 Windows DWM 报告的显示刷新率，不是呈现 FPS，也不宣称是每帧物理 VRR 扫描率；换前台程序不清空屏幕刷新率历史。读取失效时清空历史并显示 N/A。Frame Count 是当前采集会话收到的有效呈现间隔数量，切换采集目标／会话会重置，不冒充游戏启动后的总帧数。

## 验证

- 72 组原生绘制尺寸／缩放检查通过，验证 L1 裁去透明留白、L2 新顺序及曲线位置、L3 频率、L4 Time／无版本行／App 下的 Display Hz。另有两种屏幕坐标原点下的九锚点断言。
- 监控 15 项、显示子进程 2 项、PresentMon 解析 6 项、AutoTDP 20 项、OSD 设置 14 项通过；包括显示心跳不被采集阻塞，及刷新率历史有界、切前台保留、数据失效清空的检查。
- 真正的原生窗口故意停止输入六秒后仍可见、置顶，显示 stale=true，owner=0。
- 本机记事本和文件资源管理器先后置前／最大化后，OSD 仍在其上方；各连续六秒状态检查均保持 visible=true、topmost=true。
- 更新后原生显示窗在本机 1920×1200 屏幕上逐个验证九个锚点，全部贴边且可见、置顶；当前二档实际坐标为 (0, 0)。Direct3D11 验证实测约 64.52 FPS、Display Hz 120.000，已有 48 个独立刷新率样本。真实数据预览保存在 `artifacts/osd-edge-refresh/live-preview/`，未修改用户的二档设置。

以上验证覆盖普通窗口与本机无边框全屏。旧式独占全屏、其他应用刻意反复争夺 TopMost 的情形及 HDR 仍未验证，不据此宣称适用所有显示模式。

## 来源与恢复

设计输入：用户原文 `C:\Users\13304\Desktop\Claw-Playhub-ButtonRouter-Handoff\SteamOS_OSD_0-4_复刻规范.md`，未修改。

- [2023 预设与截图](https://dbeley.ovh/en/post/2023/03/23/the-mangohud-presets-used-by-the-steam-deck/)
- [MangoHud 上游配置](https://github.com/flightlessmango/MangoHud/blob/master/data/MangoHud.conf) 与 [动态渐变实现](https://github.com/flightlessmango/MangoHud/blob/master/src/overlay.cpp)
- [设计师官方公共领域字体包](https://typodermicfonts.com/assets/downloads/typodermic-public-domain-2024-12.zip)
- [微软 DWM_TIMING_INFO 刷新率定义](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_timing_info)
- [Intel Level Zero Sysman 频率域定义](https://oneapi-src.github.io/level-zero-spec/level-zero/1.8.0/sysman/api.html)

部署报告：`artifacts/native-osd/deployment.json`。本次旧文件、原生渲染器、字体及配置备份：`%LOCALAPPDATA%\ClawToolbox\repair-backups\native-osd-20261025-043459`。保存设置未被重置，功耗策略和风扇设置未更改。
