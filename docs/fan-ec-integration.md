# 三档风扇预设与 EC 接口实测（2026-10-19）

后续跨温区验收已完成：**第二层不通过，第三层因前置条件失败未执行**。参见 [完整验收报告](fan-firmware-verification-20261019.md)。后台已更新至 0.17.21，提供带超时和温度保护的专用诊断会话；以下 0.17.20 记录保留为前一轮过程证据。

## 交付结果

- 移除可选的“固件”预设，仅保留静音、均衡、性能。Decky、游戏库、Game Bar 入口同步；曲线编辑、恢复本档默认值、全速开关保留。
- 默认使用均衡。旧配置中的 `firmware` 迁移到均衡，已保存的三档曲线不丢失。
- **运行时继续使用软件温控，不启用未经验证的自定义 EC 自主调速。** UI 显示“软件自动调速”。
- 软件按 CPU 温度插值，持续升温约 6 秒后加速，持续降温约 10 秒后减速，95°C 及以上立即提高目标风量。静音、均衡的原有上限保持 76，性能最高 100。
- 读取温度失败或读到无效值时尝试解除固定风量控制；正常退出时恢复备份的 CPU/GPU 曲线并清除自定义和全速位。此处的自动交还属于内部保护，不是第四档预设。
- 后台 0.17.20，游戏库 0.1.15.0，Game Bar 0.4.18.0。

## 本机环境与证据边界

设备：MSI Claw 8 EX AI+ CG3EM，主板 MS-1T91，Intel Arc G3 Extreme，BIOS E1T91IMS.10D（2026-09-07）。

MSI 在 [Center M 更新说明](https://www.msi.com/Handheld/Recommended-For-Claw/latest-updates/Update-MSI-Center-M-3.0.2608.1201-Released) 中要求此机型至少更新至 .10A 才能获得完整的手动风扇控制；本机版本高于该要求。该说明不等于承诺第三方写入接口、温控执行方式或断电持久化。

本轮没有刷 BIOS、修改未知 EC 地址、重启或断电。因而不能从本轮结果推断断电后永久保存，也不能把“读回一致”当作“曲线正在被执行”。

## 已确认的接口

WMI namespace：`root\WMI`，实例：`MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'`。
输入输出使用 `Package_32.Bytes`；返回首字节为状态（成功为 1），以下偏移均指去掉状态字节后的 payload。

| 接口 | 已确认用途 | 约束 |
| --- | --- | --- |
| `Get_Fan(0)` | 两只风扇转速计数 | 按已有换算 `480000 / abs(a-b)` 读取 RPM |
| `Get_Fan(1/2)` | CPU/GPU 当前风量与六点表 | `[0]` 为当前风量，`[1..6]` 为六个曲线点，边界字节保留 |
| `Set_Fan` | 写入 CPU/GPU 六点风量表 | 写前读取并保留边界，写后逐点回读；失败尝试回滚 |
| `Get_Temperature(1/2)` | 读取温度相关数据 | 本机现有轴解析为 47、50、57、64、71、78°C；温区未写入 |
| `Get_Temperature(0)` | 观察到随温度变化的 EC 温度 | 与主机 CPU package 温度不等同 |
| `Get_Thermal(0/1/2)` | 只读诊断 | 未将其内容假定为可写曲线或保存接口 |
| `Get_AP(1)` + `Set_Data(0xD4)` bit 7 | 启用/解除自定义风扇控制 | 读改写，保留其他位 |
| `Get_Data(0x98)` + `Set_Data(0x98)` bit 7 | 全速覆盖 | 读改写，保留其他位 |
| `Get_Data(0xD2)` | 只读场景状态 | 本轮没有修改场景 |

枚举还发现 `Get_EC/Set_EC`、`Get_BIOS/Set_BIOS`、`Get_Thermal/Set_Thermal` 等通用方法，但没有找到明确的风扇 `Save/Commit/EEPROM` 接口。通用写入方法不能证明有安全、已知的永久保存能力；本轮未尝试未知地址。

只读 `fan_probe` 原生命令保留用于后续排查；未加入外部 Agent 方法白名单。参考了 [Handheld Companion 的 MSI 实现](https://github.com/Valkirie/HandheldCompanion/blob/main/HandheldCompanion/Devices/MSI/ClawA1M.cs) 和 [ClawTweaks 的风扇页面](https://github.com/enterTheVoidCode/ClawTweaks/blob/c728eaa3553a4a5b8804bda87ddb0ef0181e5e10/XboxGamingBar/Features/Devices/GamingWidget.MsiFanControl.cs)，并只读核对本机已安装参考工具的调用方式；没有复制其实现代码进入产品。

## 实机验证结果

本机诊断文件位于忽略提交的 `diagnostics/`；以下为本轮实际结果摘要，包含失败结果。

| 验证 | 结果 | 结论 |
| --- | --- | --- |
| 三档完整曲线写入/回读 | CPU/GPU 六点均一致 | 表格可写 |
| 自定义位开启、主机停止动态调速 | CPU 约 60→73°C，风量 30、约 2892 RPM 保持不变 | 未证实自定义曲线自主跟温执行 |
| 自定义位关闭、观察负载与冷却 | 温度约 59→76°C，风量 56→58、RPM 3404→3609 | 仅证明固件自身有调速活动，不能证明在使用写入的自定义表 |
| 关闭自定义位，分别写入前五点全 40 / 全 60、末点 100 | 两次当前风量均 56，RPM 均约 3400–3450，CPU 温度接近 | 未观察到对两张自定义表的区分响应，因此撤回实验路径 |
| 最终软件模式，以六点全 40 / 全 60 对比 | 平均 2909 / 3646 RPM | 软件风量写入可实际改变转速 |
| 最终三档切换 | 三档均无错误，自定义位开启，全速位关闭 | 三档可用 |
| 正常退出后台后独立读取 | 自定义位、全速位均关闭，两只风扇恢复 `[60,64,68,74,80,84]` | 已实际交还原始固件自动控制 |

对应本机证据：`fan-ec-live.json`、`fan-ec-autonomous-cycle.json`、`fan-curve-response-comparison.json`、`fan-software-live.json`、`fan-shutdown-handoff.json`。中间诊断中的 `autonomousResponseObserved=true` 仅反映转速变化，已由后续不同曲线对照排除其作为“自定义自主温控成功”的依据。

所有改变设置的测试均保存并在 finally 中恢复测试前设置；本机手动功耗 26 W、短时 29 W、CPU 频率上限 3 GHz 及原有三档曲线保持原值。测试完成后恢复均衡、关闭全速。

## 可重复检查

- `tests/fan_control_test.py`：三档迁移、双风扇温控、升降速延迟、异常温度回退、重置重写、失败重试、退出解除全速、原始备份保护。
- `tests/power_backend_test.py`、`tests/autotdp_test.py`、`tests/endurance_backend_test.py`、`tests/power.test.mjs`：设置兼容与功耗/UI 回归。
- `tools/Verify-Fan.py`：默认只读。`--exercise` 会临时切换三档，并进行 40/60 风量响应对比，然后恢复全部功耗设置。它不会将结果标为固件自主控制或永久固化。

本轮结果：风扇单测 8 项、AutoTDP 18 项、Intel 接入 14 项、Agent 16 项、前端风扇/功耗 3 项以及设置脚本检查通过。Agent 测试仍出现原有的 Windows asyncio 关闭连接警告；游戏库构建仍有既有 GamepadPicker AOT 与符号工具警告，均未作为本轮风扇改动处理。原生 helper、游戏库 MSIX、Game Bar 和 Decky 前端均完成构建；新版已安装，游戏库性能页已实际打开并确认仅显示三个风扇预设。

后续若要接管到 EC，需要先找到并验证当前 CG3EM 的温控启用条件：至少在不发生主机风量写入的情况下，用不同曲线在接近的 EC 温度下观察到明确不同的转速，再验证温度跨过曲线点时的响应。永久保存还需要单独核对厂商接口和重启/断电后的保持行为。
