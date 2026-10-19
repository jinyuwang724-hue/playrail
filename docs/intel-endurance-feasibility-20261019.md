# Intel Endurance Gaming 接入可行性 · 2026-10-19

后续已完成接入，当前实现与验证结果见 [Intel 智能功耗接入](intel-endurance-integration.md)。以下保留先前只读排查时的记录。

## 结论

本机具备接入官方配置接口的条件，已经通过实际驱动完成能力枚举与全局配置读取。已验证的范围是“驱动声明支持、配置可以读取”；尚未验证写入、启用后的 DTT 调节及鸣潮实际帧率/能耗。

本次只运行只读探测，没有切换功耗模式、写入显卡设置、启动或重启服务。产品后台和前端未改动，也未安装新版本。

## 本机实测

| 项目 | 结果 |
| --- | --- |
| 处理器 | Intel Arc G3 Extreme |
| 机型 | MSI Claw 8 EX AI+ CG3EM Launch Pack |
| 核显 | Intel Arc B390 GPU |
| 核显驱动 | 32.0.101.9034 |
| Intel Graphics Software | 26.32.2604.4，相关服务正在运行 |
| 系统 IGCL | `C:\Windows\System32\ControlLib.dll`，1.2.301.0 |
| Endurance Gaming | 已安装，1.9.437.30；已注册自启动；检查时未发现对应运行进程 |
| DTT / IPF | `dptftcs` 与 `ipfsvc` 均正在运行，相关设备状态正常 |
| DTT / IPF 文件版本 | 2.3.20301.5051 / 2.3.20303.5058 |

使用系统已安装的 DLL，调用 `ctlInit`、`ctlEnumerateDevices`、`ctlGetSupported3DCapabilities` 和只读 `ctlGetSet3DFeature`。没有调用 Intel 示例中的写入测试。

| Endurance Gaming 项目 | 驱动返回 |
| --- | --- |
| 功能枚举 | 明确包含 Endurance Gaming |
| 控制选项 | 位掩码 5：关闭、自动；不提供强制开启 |
| 调节档位 | 位掩码 7：性能、均衡、续航 |
| 游戏专属配置 | 支持 |
| 图形 API | 位掩码 6：DX11、DX12 |
| 全局配置读取 | 成功 `0x00000000`；当前关闭，保存档位为性能 |
| 鸣潮 exe 配置读取 | `Client-Win64-Shipping.exe` 返回 `0x40000014`，未找到独立配置；这不等同于游戏不受支持 |
| 扩展状态读取 | 成功；当前不要求帧节奏控制；返回目标值 1000、刷新率值 60 |
| 热切换声明 | 没有声明 LIVE_CHANGE 支持，不应承诺游戏内立即生效 |
| 功能冲突声明 | 位掩码 1：与独立 Frame Pacing 功能冲突 |

扩展状态中的 1000 是功能关闭时读到的驱动值，不应显示成用户实际选择了“1000 FPS”，也不用于推断开启后的真实帧率目标。扩展载荷使用官方 `ctl_endurance_gaming2_t`，外层请求结构保持版本 0。

上述枚举和位掩码含义来自 [Intel 官方 IGCL 定义](https://github.com/intel/drivers.gpu.control-library/blob/master/include/igcl_api.h)，调用方式参照 [官方 3D 功能示例](https://github.com/intel/drivers.gpu.control-library/blob/master/Samples/3D_Feature_Samples/3D_Feature_Sample_App.cpp)。

## 接入方式

可以复用现有 `native/ClawGpu.cs` 的 IGCL 连接，将 Endurance Gaming 的能力与配置暴露给性能页面。无需把 Intel 闭源算法复制到本项目，也无需另装功耗驱动。

建议界面提供独立的“Intel 智能节能”控制方式：关闭/自动，以及性能/均衡/续航三档，选项以本机驱动能力为准。不要把旧算法的任意目标帧率和功耗下限直接套在官方模式上，也不要仅凭配置读取成功显示“正在节能”。

后台需要实现明确的功耗控制权交接：Intel 模式期间停止旧 `SmartGovernor` 和现有定期功耗/CPU 限频重写，防止旧算法压低上限或与 DTT 争抢设置。切换前备份状态，退出时按约定恢复；风扇与 OSD 的独立功能应继续运行。现有 `PowerControl._loop()` 不能仅增加一个驱动开关而保持原有写入循环。

驱动声明的冲突指独立 Frame Pacing（功能 0），并不是把显示页面全部帧同步、低延迟和插帧选项一概禁用。需要按实际能力处理。

## 还需验证的边界

1. **写入与回读**：选择自动及档位后，确认驱动接受并能准确回读；验证在 Intel Graphics Software 中的配置一致性和原状态恢复。
2. **实际调优组件**：EG 已安装但检查时没有运行进程，需要验证启用后的运行、DTT 策略和真实功耗响应。组件安装和驱动能力枚举并不能证明这一层正在工作。
3. **电源状态**：本机只提供自动模式。安装包说明中，SoC 节能部分要求自动模式、电池供电和有效 3D 负载；需分别测试电池与插电行为，不能承诺插电时具备同样调节效果。[Intel 说明](https://www.intel.com/content/www/us/en/support/articles/000097679/graphics.html)同样说明 Auto 与电池供电相关。
4. **鸣潮效果**：同场景、同画质、同帧率设置对比手动档和 Intel 档，记录真实渲染帧率、帧时间、低帧表现、芯片耗电、GPU 频率与温度。需要含加载结束、跑图、战斗、切回游戏及插拔电源等阶段。
5. **生效时机**：按驱动声明保守处理为可能需要重启游戏，再通过实测确定；专属配置不存在时要明确处理继承/创建，不能把查询错误当成开关状态。

## 交付与复查

新增只读探测工具 `tools/Probe-IntelEndurance.py`。工具包含 ABI 大小/偏移校验，只调用枚举、能力查询与 `bSet=false` 的配置读取；从本机 System32 加载 DLL。没有写入入口。

本机使用 ClawToolbox 自带的 64 位 Python 执行，已成功读取全局与鸣潮配置查询结果。原始记录保存在被 Git 忽略的 `diagnostics/intel-endurance-probe.json`。后续可用 `--app <exe文件名>` 读取指定应用，`--output <路径>` 保存结果。

此次接入评估已完成；配置写入和游戏实测尚未进行。
