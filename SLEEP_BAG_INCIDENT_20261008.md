# 掌机在包内意外唤醒与发热：2026-10-08

本机：MSI Claw 8 EX AI+，Windows 11 构建 26200。本次用户说明，约 14:00–15:30 携带掌机回家，已按电源键睡眠；14:47 没有操作机器，它仍在包内。

## 已确认的经过

| 本地时间（UTC+8） | 证据 | 判断 |
|---|---|---|
| 13:52:34 | Kernel-Power 506：Power Button；SleepStudy Screen Off | 电源键确实触发了睡眠流程 |
| 13:53:04–14:47:32 | SleepStudy 睡眠 54 分 32 秒，软件低功耗 98.06%，硬件低功耗 98.00%；耗电 265 mWh，约 0.29 W | 这段睡眠正常，不是一直运行发热 |
| 13:53 附近 | USBHUB3 提到 0DB0:1901；SleepStudy 内置 Xbox 手柄链路阻塞约 35 秒 | 是短暂阻塞，不能认定为本次持续发热主因 |
| 14:47:32.001 | Kernel-Power 507/506：Austerity Battery Drain Budget Exceeded；SleepStudy 原文为 Restricted Standby Battery Drain Budget Exceeded | Windows 改变现代待机阶段；此名称不证明已经耗掉 5% 电量，也不代表已成功休眠 |
| 14:47:32.201 | Kernel-Power 507：Input Mouse；566：InputHid；SleepStudy 关屏阶段结束原因 Input Mouse | 非电源键输入使机器恢复活动；用户确认当时没有操作 |
| 14:47:32–15:56:02 | BatteryReport Active 68 分 30 秒；电量容量 47,987 → 25,736 mWh | 这段耗电 22.25 Wh，约 27.54 个电量百分点，平均约 19.49 W；机器持续运行足以解释包内积热，但日志未记录机身温度 |
| 15:56:02 | 再次 Power Button → Sleep | 后来才重新进入睡眠 |

13:52–14:47 整个关屏会话（含开始的约 30 秒过渡）耗电为 342 mWh，约 0.42 个电量百分点。不能将睡眠全过程描述成严重持续耗电。14:47–15:56 的总耗电包含用户到家后的可能使用，不能全部归为在包内耗电；其中 14:47 的非人为恢复活动已确认。

## 能定位到哪里，尚不能定位到哪里

系统明确记录鼠标/HID 输入唤醒，但没有留下具体鼠标设备路径或发出输入的进程。因此尚不能证明是内置手柄硬件鼠标误报、USB 重新枚举造成旧输入，或软件合成输入。

14:47 的输入与内置 USB 设备重新枚举紧邻，是后续固件／驱动排查线索，不是已经证明的根因。本轮不禁用内置手柄、不修改固件、不强制改 S3，也不关闭 Device Guard。

修复前 wake_armed 列表里的 HID-compliant mouse (003) 实际属于 Aurora80 RGB 键盘（36B0:3004）。其第一次安装与接入时间均为 16:21:32，在事故之后，故不能把它当成本次唤醒者。本次关闭它的唤醒权限，是按用户“只让电源键唤醒”的要求做预防，并非认定它造成事故。

当前没有活动唤醒定时器，电源方案的 AC/DC 唤醒计时器也已禁用。这是当前配置证据，不能反向证明事故时绝对没有其他后台活动。现有事件没有将本次恢复活动归于更新或网络。

活动阶段能耗估算中 Steam webhelper 排名最高，系统中断也较高；这可以解释恢复活动后的耗电来源，不能证明 Steam 发起了唤醒。估算值不等于实际逐应用功率测量。本轮没有改 Steam、体感助手或代理设置。

本地路由事件记录显示，ClawTweaks helper 于 13:52:23 正常退出（PID 18372，ack=True），下一次记录的恢复为 16:10:29，事故期间没有 helper-restored 记录。因此目前没有证据把 14:47 的输入归因于它的软件鼠标模拟，也没有据此修补第三方 helper。日志不覆盖未记录的外部启动行为，不能据此断言所有软件输入都已排除。

微软区分现代待机的处理器活动、显示点亮和输入唤醒，不能拿传统 S3 的 lastwake 无记录否认本次事件。[现代待机唤醒来源](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby-wake-sources)、[SleepStudy 的状态与退出原因](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby-sleepstudy)。

## 当前电源策略的薄弱点

当前方案为 Claw Xbox Sleep Display，GUID 50e23985-8a76-4170-881b-c380b06e6cdd：

- AC/DC 电源键动作均为睡眠（1）。
- 电池下自动睡眠为 0（关闭）。
- 电池下关闭显示器时间为 10,800 秒（3 小时）。
- 电池下睡眠转休眠时间为 2,147,483,647 秒，实质上没有短时间的固定休眠兜底；报告也回读到这一值。
- USB 选择性暂停已启用，无需重复“开启”并宣称修好了手柄。
- 本机支持 S0 现代待机及 S4 休眠，不支持传统 S3；休眠文件已可用。

这些配置不证明是谁唤醒了机器，但可以解释意外唤醒后为什么长时间不再自动睡眠。

## 用户要求与已应用的设置

用户明确拒绝休眠及睡眠后自动转休眠，要求保留电源键快速睡眠／恢复，只限制其他唤醒来源。此前准备的两个休眠方案已取消，均未应用。

2026-10-08 17:59 已关闭以下设备的唤醒权限：

1. Intel Wi-Fi 7 BE213。
2. Aurora80 RGB 键盘的两个键盘端点及鼠标端点。
3. USB4 根路由器。

五项均成功回读，powercfg 的 wake_armed 结果为“无”，WMI 支持唤醒权限的设备均为 Enable=false。只取消唤醒许可，未禁用设备；之后 PnP 回读显示内置手柄、鼠标、键盘、Wi-Fi 和 USB4 状态均为 OK。

应用前后完整电源设置文本完全一致。电源键 AC/DC 仍为睡眠（1），自动转休眠时间、自动睡眠时间、关闭显示器时间、USB 选择性暂停及唤醒计时器原值均未改变。没有触发睡眠、休眠或重启。

## 仍需实测的现代待机限制

这些设备权限不能作为“本机已保证只有电源键唤醒”的证据。微软明确说明，设备管理器的唤醒权限通常针对 Sx 睡眠，而不会普遍改变 S0 现代待机的唤醒行为：[WMI 与电源管理选项](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/wmi-and-the-power-management-tab)。

本机内置 MSI 手柄（0DB0:1901）的键盘／鼠标端点没有暴露 MSPower_DeviceWakeEnable 开关；不能把外接键盘权限关闭成功说成已经封住内置手柄路径。微软另有 EnableInputSuppression，但该设置适用于合盖且没有外接显示器的翻盖设备，不是无盖掌机可直接套用的“只准电源键”开关：[EnableInputSuppression](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/power-controls-enableinputsuppression)。本次没有伪造合盖状态，也没有禁用输入硬件来冒充修复。

请先在通风桌面上手动验证：按电源键睡眠，等待约一分钟，分别轻推摇杆、按普通手柄键、触屏和操作外接键盘，观察是否点亮；最后按电源键恢复。若普通输入仍能恢复活动，说明该来源绕过当前权限，需要继续针对对应内置输入／固件路径处理。短测通过后，可在桌面进行较长时间待机观察；不能用短测通过替代一小时携带场景验证。

## 18:06 拓展坞复现与用户补充

用户手动测试各个手柄按键，没有点亮屏幕；带电拓展坞插拔则会点亮。用户最终澄清：此次拓展坞只有电源线，没有接键盘。事故发生在包内时，也没有连接任何外部设备或电源。当前应以这两项澄清为准。

| 时间 | 原始记录 | 能支持的结论 |
|---|---|---|
| 18:04:46 | 506：Power Button | 开始本次手动睡眠测试 |
| 18:06:45.004 | 105：AcOnline=false | 外部供电断开 |
| 18:06:45.026–.028 | 507/506：AC/DC Display Burst Suppressed，Reason=28 | 电源变化使现代待机阶段切换；不能单凭这个记录认定已点亮屏幕 |
| 18:06:45.047 | USBHUB3 205：0DB0:1901 重新枚举，包括 IsPortCycle=true | 内置 MSI 手柄链路被驱动重新识别；不代表用户插拔了内置手柄 |
| 18:06:45.575 | 507：Input Mouse，Reason=32；566：NextSessionType=0 | 随后由鼠标类输入恢复到活动会话 |
| 18:06:49.098 | 105：AcOnline=true | 外部供电重新连接 |

这段记录仍出现 Aurora80 36B0:3004 的重新枚举条目，但用户已经确认拓展坞没有接键盘。不能从该条目推断键盘接在拓展坞上，也不能据此认定它发出唤醒输入。事件描述中的“重新枚举”是驱动操作，不等于物理插入外设。首次根据日志把键盘关联到拓展坞的判断已撤回。

事故和此次测试的共同点，是内置 0DB0:1901 重新枚举后，很快出现 Input Mouse。不同点是：包内事故先发生待机预算阶段转换，本次先发生外部供电断开。因此“拓展坞导致包内事故”不成立；“内置 USB 输入在恢复过程中产生旧输入或异常输入”是较值得继续核对的假设，仍未证明具体设备报告、按键或发出输入的进程。

关闭传统设备唤醒权限后，本次复现仍发生，故该设置没有彻底解决 S0 恢复路径。用户按键短测未唤醒也不能排除驱动重新识别设备时产生输入。本次没有新增盲目禁用手柄、改变 USB 省电策略、自动强制再睡眠或改休眠策略的措施。

原始测试记录为 dock-test-power-events.json、dock-test-all-events.json。微软区分接入供电时的显示点亮、断开供电时的处理器活动，以及 USB 设备插拔唤醒：[现代待机唤醒来源](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby-wake-sources)。当前未找到本机可用且已验证的通用“禁止电源变化唤醒”开关，不能将传统设备唤醒权限替代为这种开关。

## 原始记录与恢复

原始资料目录：[本次记录](artifacts/sleep-incident-20261008-173601)。

- sleepstudy.html、sleepstudy.xml、battery-report.xml：系统生成的报告。
- system-events.json：对应时段事件及原始 XML。
- power-settings-before.txt、device-wake-before.json、driver-info.json：修复前状态。
- incident-sessions-summary.json：从 XML 提取的会话及计算值。
- plan-BatteryHibernate.json、plan-SleepThenHibernate.json：已取消的两个休眠方案，仅保留当时只读计划记录。
- device-wake-policy-before.json、device-wake-wmi-before.json：本次设备唤醒权限备份。
- device-wake-apply-result.json、device-wake-wmi-after.json、wake-armed-after.txt：应用结果及权限回读。
- power-settings-device-fix-before.txt、power-settings-device-fix-after.txt：电源设置未改变的核对记录。

当前修复脚本：[configure-device-wake-protection.ps1](diagnostics/configure-device-wake-protection.ps1)。以管理员权限，对同一记录目录运行 -Mode Restore，可恢复原有设备唤醒权限。WMI 恢复按原设备实例路径匹配，Wi-Fi 恢复核对原 PCI 路径，原设备不在时报告失败，不用其他设备代替。恢复也不会改电源键或休眠策略。

configure-bag-sleep-protection.ps1 是已取消的休眠备选草稿，不属于本次生效的修复，不应运行其 Apply 模式。实际睡眠与电源键恢复尚未测试；设备权限成功回读不等于已经证明全部包内唤醒问题消失。
