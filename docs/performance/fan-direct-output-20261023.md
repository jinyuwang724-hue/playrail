# 风扇改用持续直接输出：实现与实机验证

2026-10-23，本机风扇服务已从写入平坦曲线表的 1.0.5 切换到 **1.1.1 / ec_output**。沿用体感助手 CG3EM 分支的控制对象与持续写入思路，保留工具箱原来的曲线、温度平滑、双扇转速反馈和服务生命周期管理。

## 控制方式

- CPU 温度约每秒更新一次，原有曲线计算目标风量；两次温控更新之间继续刷新输出，间隔约 250ms 加上 I/O 耗时。
- 通过现有 PawnIO 驱动加载官方签名 `LpcACPIEC.bin`，走 ACPI EC 命令/数据口 `0x66/0x62`，写两个当前输出 `0x71/0x89`。没有引入体感助手的 InpOut 驱动。
- 接管时恢复原厂六点表及第八档 94，清除全速，然后开启已核对的 `D4.7` 自定义位。运行中持续写当前输出，不再每次改六点平坦表或反复 Auto/自定义切换。
- 体感助手该分支没有设置 D4 的代码，不能因此认定应清除此位：本机短测中，清除后输出很快被固件覆盖；开启后，两种写入通路都能看到实际转速随输出升降。本次实现基于这一实测选择。
- 4700 RPM 策略从输出 74 开始封顶。机械稳定后，如双扇任一路持续超速，再按实际转速小步下调。输出字节偶尔被固件改变，不视为必须交回 Auto 的故障；以持续的双扇转速判断效果。
- 正常停止、睡眠时先停止所有输出脉冲，再明确恢复原厂控制位和曲线。恢复运行后重新接管。持续读写故障交回原厂，并等待 10 秒再尝试，避免每秒反复接管。

实现位置：[底层输出](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/ClawFanHardware.cs)、[服务和温控](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/ClawFanService.cs)、[依赖及许可证](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/upstream/PawnIO-ACPIEC/README.md)。模块只允许访问上述两个端口，服务自身只暴露固定的两路风扇输出；IPC 不接受端口、寄存器或任意指令。

目前直接输出只允许已核对的 **CG3EM / 1T91EMS1.103**，其他组合拒绝启用。配置中 `outputStrategy` 缺省为 `direct`；保留 `table` 供明确回退时使用。

## 温度处理

第一次 1.1.0 升温试验遇到 CPU 瞬时 95°C，引发单次读数交还原厂，试验未通过。因此改为 1.1.1 的持续条件：

- CPU ≥95°C 持续至少 3 秒，或 CPU 单次 ≥100°C，或 EC 温度 ≥95°C，明确交还原厂。
- 交还后停止直接输出，CPU ≤85°C 且 EC ≤80°C 持续 30 秒后重新接管。
- 单次 95°C 尖峰只按原曲线提高风量，不切换控制源。没有修改固件温度阈值、保护标志或处理器自身热保护。

**4700 是正常直接控制阶段的目标上限。上述紧急交还原厂时允许原厂提高转速。** 本次没有刻意把机器加热到紧急阈值来做实机验证；这些分支由模拟测试覆盖。

## 已完成验证

| 项目 | 结果 |
|---|---|
| 实际部署 | 1.1.1，`ec_output`，安装文件与构建文件哈希一致 |
| 有限升温试验 | 约 70 秒，63 次采样；机械稳定后 45 次采样，两路合计范围 **4486–4615 RPM** |
| 温度和控制状态 | CPU 最高 88°C，EC 最高 76°C；本轮无错误、无温度交还、未观察到 5800 RPM |
| 曲线表 | 试验全程保留原厂六点表，第八档仍为 94；效果来自当前输出控制 |
| 恢复用户设置 | 临时固定高风量测试后恢复原有曲线，25 秒观察末两扇约 2824 / 2727 RPM |
| 功耗设置 | 试验前后功耗配置文件哈希相同 |
| 实机停止与重启 | 停止后读回原厂自动、全速关闭、原厂六点表及第八档 94；重启后恢复直接控制，配置文件哈希相同 |
| 核心测试 | 曲线平滑、保存失败回滚、持续刷新、输出覆盖不切 Auto、故障重试、转速修正、持续过热、单次紧急温度、尖峰不累计、停止/睡眠/恢复等均通过 |

结果文件：[升温试验](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/fan-direct-20261023/response-warm.json)、[生命周期](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/fan-direct-20261023/lifecycle.json)、[部署](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/fan-direct-20261023/deployment.json)。可复查入口：[响应测试](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/tools/Test-DirectFanResponse.py)、[生命周期测试](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/tools/Test-DirectFanLifecycle.ps1)、[核心测试](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/tests/FanServiceTests.cs)。

测试只用有限的普通计算升温，达到预设温度即停止计算，没有启动游戏或更改功耗/CPU/GPU频率。这能确认本机输出通路、转速封顶响应及正常交还流程，尚不能证明长时间游戏、睡眠唤醒实机、其他固件或所有温度条件下都不出现超速。旧的 WMI/ACPI 短测及失败的 1.1.0 记录保留在同一诊断目录，未计作通过。

原始 1.0.5 服务备份位于 `diagnostics/fan-direct-20261023/direct-backup-20261023-143634/`。构建、安装和打包脚本已包含固定哈希校验的官方模块及许可证。没有改动用户保存的风扇曲线值或功耗设置。
