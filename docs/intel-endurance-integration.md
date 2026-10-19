# Intel 智能功耗接入

2026-10-19：后台 0.17.18，游戏库 0.1.12，Game Bar 小组件 0.4.16。

## 用户入口

控制中心 → 性能 → 功耗接管开 → 模式「Intel 智能」，选择性能、均衡或续航。Steam 插件和 Game Bar 同样使用后台的 `smart.profile` 配置；原 Game Bar 的目标帧率卡片改为 Intel 档位。

旧智能算法及其目标帧率、功耗下限、插电/电池上限已移除。旧配置自动迁移为 Intel 性能档；手动和按电源模式的功耗、频率、短时加成保持原有设置。Intel 模式中不提供这些手动调节项，也不把驱动选档等同于实际节能已经发生。

本机驱动提供关闭/自动和三个档位，主要用于电池供电；插电时自动策略待机。驱动未声明实时切换能力，界面提示调整档位后重新启动游戏。

## 后台行为

- 通过系统 `ControlLib.dll` 的官方 IGCL 接口读取能力、写入选项并回读核对。驱动不支持时报告原因，不回退运行旧算法。
- 第一次接管前持久保存驱动配置、原始功耗寄存器及当前电源方案的四项 CPU 频率上限，然后恢复工具箱最初备份的功耗寄存器，解除手动 CPU 限频，启用 Intel 自动档。Intel 调优进程缺失时使用本机已安装的组件启动。
- Intel 接管后只监测状态；不根据工具箱的帧率采样调整 PL1/PL2，不再持续写入功耗或 CPU 频率。修改 Intel 档位仅更新驱动配置。
- 切回手动/按电源模式时先停用 Intel，恢复交接前设置，再应用用户选择的手动档。关闭接管或正常退出时恢复交接前的原 Intel 配置。
- 交接失败立即尝试回滚；中断或恢复失败时保留 `intel-endurance-session.json`，供下次恢复。不能读取备份时暂停接管。不会把旧电源方案的频率配置写入另一个方案。
- 设置保存与后台控制循环共用策略锁；风扇与 OSD 采样独立于 Intel 调节，帧率缺失不能再触发降功耗。

核心文件：`native/ClawGpu.cs`、`plugin/claw_endurance.py`、`plugin/claw_power.py`、`frontend/library-native/ClawLibrary/Stage/WallStage.Perf.cs`。

## 验证结果

1. **14 项交接测试通过**：重复刷新不写功耗、换档不写 MSR/限频、回到手动、失败回滚、调优进程失败、后台重启、中断恢复、外部关闭检测、不支持的平台和电源方案变更等。
2. 原功耗后台检查、14 项服务检查、16 项代理检查及 4 项前端回归通过。代理测试伴随现有 Windows asyncio 连接关闭警告，但断言全部通过。
3. 功耗原生助手编译、游戏库 Release NativeAOT/MSIX 打包、Game Bar 签名打包通过。保留原有手柄页 AOT 警告及符号包工具缺失提示，没有新增编译错误。
4. 本机驱动实际写入并回读性能、均衡、续航三档，Intel 调优进程成功运行。通过已安装的 0.17.18 后台再次切换三档并连续观察，CPU 四项频率上限均为 0，功耗上限保持平台接管值，没有旧算法的逐步降功耗。
5. 安装版鼠标验证 Intel 模式选择、均衡档点选、切回手动。功耗恢复为 26 W / 29 W，CPU 限频恢复为 3 GHz；Intel 关闭且交接记录已清理。风扇曲线、OSD 和其他设置保留。

本次没有启动鸣潮进行游戏内比较，因此不声称已经验证鸣潮帧率或续航改善。后续应在相同画质和场景下比较真实渲染帧率、帧时间、GPU 频率与实际耗电。

## 安装与备份

后台已安装到 `%LOCALAPPDATA%\ClawToolbox\app`；旧版文件和功耗配置备份位于 `%LOCALAPPDATA%\ClawToolbox\repair-backups\intel-endurance-20261019-113742`。现有路由、输入层和触控桥可执行文件保持原字节，仅为新后台建立版本文件名。游戏库及 Game Bar 通过 MSIX 升级保留用户数据。

只读探测工具：`tools/Probe-IntelEndurance.py`；本地验证原始记录：`diagnostics/intel-endurance-live.json`。诊断目录不随产品发布。

Intel 的接口定义与能力语义见 [官方 IGCL 文档](https://intel.github.io/drivers.gpu.control-library/Control/api.html)。
