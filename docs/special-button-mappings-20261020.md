# QS / Claw 专用键映射

用户选择：Steam 大屏使用原接管，其他场景使用新映射。

## 使用入口

Claw 游戏库 → 控制中心 → 手柄 → 按键映射，点击左侧快速设置键或右侧 Claw 键。

两键使用现有的指令选择器，可选系统动作、键盘组合键、数字键盘和鼠标指令。系统动作包括 Xbox Game Bar、Claw 游戏库、控制中心、Steam 左侧主菜单、Steam 右侧快捷栏、Steam 游戏库、Magpie、屏幕键盘、截图及音量操作。

专用键目前提供按下事件，采用普通按压；不提供未经硬件支持的长按、释放或虚拟手柄输出。

## 两套独立配置

| 场景 | 配置 | 当前动作 |
| --- | --- | --- |
| Steam 大屏打开 | 原 `enabled` 开关、`qamAction` / `msiAction`；`steamBindings` 仅保存显式自定义覆盖 | QS 为左侧主菜单，Claw 为右侧快捷栏 |
| Steam 大屏关闭 | `buttonMappings`；空对象表示关闭常规映射 | QS 为 Game Bar，Claw 为 Claw 游戏库 |

原 Steam 两键动作和震动设置保持不变。首次部署保留了原 `enabled=false`，导致大屏场景没有接管；用户报告左侧快速设置键仍唤出 Game Bar 后，已按其要求开启原 Steam 接管（`enabled=true`）。Steam 大屏只最小化仍按原大屏接管处理，需关闭大屏才进入常规场景。

改变指令即保存并更新后台，无需重启。可单独禁用按键、恢复常规默认、恢复原 Steam 映射，或用“测试这项指令”检查输出。场景选择本身只切换编辑视图，不会修改另一套配置。

## 接管与恢复

沿用原 ClawTweaks 安全交还流程、手柄数量检查、Steam 菜单结果确认及连接丢失处理。Steam 前端未就绪时，额外识别实际 Steam 大屏窗口，避免误当常规场景。关闭常规映射、停止后台或接管失败会交回原功能。

有意触发的 Game Bar 与键鼠组合使用自身标记，避免被原固件快捷键过滤逻辑吞掉。原 Steam 防止误弹 Game Bar 的逻辑保留。0.17.24 将主动打开 Game Bar 的放行限制在当前场景；进入或退出大屏后清除，避免常规场景的放行绕过大屏保护。

Claw 游戏库注册 `claw-library://library` 和 `claw-library://control-center`。重复唤出时只复用现有窗口；在激活回调内读取 WinRT 参数，向 UI 线程只传字符串，避免跨线程访问激活对象导致异常退出。

## 安装与验证

- 后台版本：0.17.24；游戏库最终包：0.1.19.0。
- 新映射测试 9 项、原 Steam 菜单测试 20 项、原生场景策略 31 项（含新增 Game Bar 跨场景回归 5 项）、快捷键过滤 16 项、输入层测试均通过；风扇相关 Python 测试 25 项通过。
- 已实际连接 Steam 前端测试左右菜单请求并取得成功确认，测试后原设置全部恢复。
- 0.1.18.0 已连续执行六次游戏库 / 控制中心激活，进程保持同一个实例，日志均确认路由执行。0.1.19.0 已安装，重复唤出三次后可直接按 M 打开控制中心，键盘焦点验证通过。
- “测试指令”返回成功表示后台发出指令或收到 Steam 前端确认。用户随后实按确认：关闭 Steam 大屏后，左侧快速设置键能正常唤出 Game Bar，右侧 Claw 键能正常唤出 Claw 游戏库，两键均通过。
- 0.17.24 实机检查：从常规场景先执行 Game Bar，再进入大屏，状态为 `enabled/ready/released/gameBarGuardActive=true`，已关闭两个经过身份核对的 Game Bar 进程；左主菜单、右快捷栏的开关请求均获 Steam 前端成功确认。用户随后实按确认：大屏内左侧快速设置键正常打开 Steam 左侧主菜单。报告为 `diagnostics/button-mappings/steam-fix-live.json`。
- 独立风扇服务仍为自动启动且运行中，未修改风扇配置。

本次更新先备份原设置和程序文件至 `%LOCALAPPDATA%\ClawToolbox\repair-backups\button-mappings-*`。部署脚本为 `tools/Deploy-ButtonMappings.ps1`；接口回归脚本为 `tools/Verify-ButtonMappings.py --exercise`，会恢复测试前的两套映射。诊断报告在 `diagnostics/button-mappings/`。

## Claw 单击唤出 / 最小化（后台 0.17.27，游戏库 0.1.22.0）

普通 Windows 桌面或游戏内，Claw 键映射到游戏库或控制中心时：窗口不在前台则唤出，已在前台则最小化，再按恢复。恢复已有窗口时保留当前页面；冷启动仍打开所配置的游戏库或控制中心。快速设置键、普通启动入口及其他按键保持原来的仅唤出行为。

此切换排除 Steam 大屏和 Xbox 全屏体验（Xbox 桌面）。后台先读取前台窗口，再发送固定的恢复或最小化路由，防止协议激活先改变焦点而误判。游戏库在最小化前再次检查两种排除场景；无法确认场景时保留仅唤出行为。一般场景 Claw 窗口指令增加 350ms 去重，防止一次硬件按压的重复事件导致刚唤出便最小化。

Xbox 桌面采用微软提供的 [`IsGamingFullScreenExperienceActive`](https://learn.microsoft.com/en-us/xbox/gdk/docs/gdk-dev/pc-dev/handheld/handheld-launchers?view=gdk-2604) 查询当前状态；不使用开机启动偏好或 Xbox 进程是否存在来推断。切换不修改 Steam 接管配置。

验证结果：

- Python 按键映射测试 16 项、窗口激活策略测试 12 项、Steam 菜单及场景测试 25 项通过。
- 已安装后台 0.17.27 和游戏库 0.1.22.0，安装文件与源码哈希一致。
- 普通桌面实际执行冷启动、前台最小化、再次恢复；日志分别确认 `iconic=True/False`。在性能页面最小化并恢复后，截图确认仍为原性能页面。
- Steam / Xbox 排除规则通过自动测试；本次未实际进入 Xbox 桌面或 Steam 大屏。当前设备的 Xbox 全屏状态接口可用，查询结果为未启用。
- 已核对更新前后两套按键配置完全一致。此次更新前 Steam 接管开关为 `false`，按原值保留，未擅自开启。风扇服务未部署或重启。
- 实机诊断记录：`diagnostics/button-mappings/toggle-live.json`。硬件实按结果另待用户反馈。
