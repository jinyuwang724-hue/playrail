# 模板 OSD 与 Game Bar 关闭重开修复

> 历史记录：本文描述上一版模板。当前经典 SteamOS 外观、置顶修复与已安装版本见 [经典 OSD 更新记录](../performance/steamos-classic-osd-20261025.md)。下文的字体、布局和版本不再代表当前版本。

当前安装：Game Bar 0.4.39.0、游戏库 0.1.104.0、ClawPower 0.17.58-r3。旧 Toolbox 控制布局保留，单击性能显示继续循环 0→1→2→3→4→0。

## 空白问题

复现了两条相关链路：旧 Game Bar 宿主在更新组件后保留了断开的连接；更关键的是，以独立桌面预览启动同一个 UWP 应用后，关闭该主窗口会让宿主页面所在进程挂起／退出，Game Bar 留下空壳。

处理：停止发布桌面预览入口，包的 AppListEntry 设为 none；Preview-GameBarWidget 脚本改为直接打开真实 Game Bar 组件，不能再创建或调整桌面主窗口。SDK 对象按视图分别持有，页面退出时停止定时器、回调与连接。更新安装后重启旧 Game Bar 宿主以清除失效连接。生命周期日志位于该组件 LocalState/lifecycle.log。

实测先复现了空白，再通过 SDK 关闭实际组件并重开；日志记录 widget close requested、widget closed、随后新的 widget launch page=true，截图中已恢复旧的完整磁贴界面。真正的宿主视口保持 464×608 DIP，桌面预览参数不再改动它。

## 四档布局

| 档位 | 显示 |
| --- | --- |
| L0 | 退出显示进程 |
| L1 | 22px FPS、极淡背景 |
| L2 | 40px 横条：FPS、帧时、CPU/GPU 占用、CPU/GPU 功耗、RAM、电量、微型帧时间图 |
| L3 | 288px 双列面板：二档字段，加温度、VRAM、预计续航；24px FPS |
| L4 | 372px 完整面板：加频率、逻辑核心占用及柱图、风扇、1% Low、GPU Busy、渲染 FPS、实际显示 FPS；26px FPS |

使用私有 Inter Medium/SemiBold 与 Noto Sans SC 字体，不安装系统字体。固定数字字宽，色值按模板；背景 #101820、80% alpha，圆角 6px、边距 16px。L1 背景为约 14% alpha。显示窗鼠标穿透、无拖动入口，位置仍在设置里改。75%–200% 缩放叠加 Windows DPI，过大时适配屏幕边界，L2 空间不足时换行。

帧率采样与传感器读取分离。显示端 100ms 检查更新，帧率任务以 200ms 为周期，硬件约每秒更新。本机验证获得约 65 呈现 FPS、120 个原始帧间隔和 14 个逻辑处理器占用；观测到约 204–219ms 的相邻快速样本更新。

Shared PresentMon 不提供有效显示完成数据时，继续用 ETW 呈现采样，并明确标记「呈现 FPS／呈现 1% Low」。1% Low 取最近最多五秒原始帧间隔中最慢 1% 的平均间隔换算；至少 100 帧才显示。GPU Busy、游戏渲染 FPS、实际显示 FPS 不能从呈现 FPS 推算，缺失时为 N/A。无法读取的温度、续航也显示 N/A，无样本的图表隐藏。

## 验证与边界

- 72 组渲染尺寸／缩放检查，包含透明圆角与固定窗口样式。
- 监控 13 项、显示进程生命周期 2 项、PresentMon 解析 6 项、AutoTDP 20 项、旧 OSD 设置 14 项通过。
- 本机分层窗口 UpdateLayeredWindow 成功，Inter Medium / Noto Sans SC 加载成功。
- Direct3D11 无边框全屏验证程序提供实时数据；真正的旧式独占全屏与 HDR 尚未验证。

普通 HWND 的 TopMost 不等于在所有独占全屏模式下都能覆盖游戏。Intel 自身的全屏覆盖还有额外窗口权限、签名和可信安装路径要求；本次未改变系统安全配置，也未宣称这些模式已经支持。

参考：[PresentMon 显示与采集组件](https://github.com/GameTechDev/PresentMon/blob/main/README-CaptureApplication.md)、[Game Bar 激活生命周期](https://learn.microsoft.com/en-us/gaming/game-bar/api/xgb-widgetactivatedeventargs)、[Game Bar 窗口控制](https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widgetcontrol)。
