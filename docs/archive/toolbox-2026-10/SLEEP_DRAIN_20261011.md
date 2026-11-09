# 2026-10-11 夜间耗电排查

数据来源：`powercfg /batteryreport`（13:37 生成）、System 日志 Kernel-Power / USBHUB3、Sunshine 日志。`powercfg /sleepstudy` 与 `/requests` 需要管理员，本次未取得。

## 时间线（已从日志核实）

| 时间 | 事件 |
|---|---|
| 04:36 | 拔掉电源，100% |
| 04:48:59 | 按电源键进入现代待机（506，原因 Power Button），94% |
| 04:49:41 | USBHUB3 196：内置手柄 0DB0:1901 在空闲时耗电，系统尝试移除失败（QueryRemovalInitiated） |
| 04:49–06:24 | 真正处于现代待机，1 小时 36 分只耗 1%（622 mWh） |
| 06:24:35.506 | 退出待机，原因 **Austerity Battery Drain Budget Exceeded**，随即重新进入待机 |
| 06:24:35.532 | USBHUB3 205：重新枚举手柄 0DB0:1901，IsPortCycle=true |
| 06:24:36.042 | 退出待机，原因 **Input Mouse**（会话原因 InputHid），屏幕点亮 |
| 06:24–09:25 | 一直亮屏运行 3 小时 0 分，耗 91%（74.5 Wh，平均约 25 W） |
| 09:25:33 | 电量危急（524），以 Battery 原因睡眠（42），2% |

2026-10-10 凌晨两次（03:51、05:51）是完全相同的序列：省电预算超限 → 手柄端口重置 → 不到 2 秒后 Input Mouse 唤醒。

## 结论

1. **手柄鼠标端点修复没有挡住这次唤醒（已核实）。** `HID\VID_0DB0&PID_1901&MI_02&COL02` 目前仍为 Error（已停用），但 06:24:36 依旧是 Input Mouse 唤醒，紧跟在手柄端口重置之后。
2. **具体是哪个鼠标设备发出的输入，日志没有记录（未核实）。** 当前在场且启用的鼠标只剩 Sunshine 的 `Zako Virtual Mouse`（AlkaidLab，ROOT\HIDCLASS\0000）。Sunshine 日志显示 04:35 串流结束，13:36 才有新连接，夜里没有串流会话。所以更可能是手柄端口重置本身被系统当成 HID 输入，而不是 Sunshine（推断）。
3. **把一次误唤醒变成整夜耗光的是电池下 3 小时关屏时间（已核实）。** 当前方案 `Claw Xbox Sleep Display` 电池下关闭显示器为 10,800 秒，自动睡眠为“从不”。亮屏后没有任何操作，要 3 小时才会关屏回到待机；09:25 电量耗尽时正好接近 3 小时。10-10 那次是用户 08:25 手动按电源键才停下。
4. 根源上，手柄 0DB0:1901 一直阻止低功耗（DRIPS），每过约 1.5–2 小时就触发一次省电预算超限和端口重置，这一步本身不会停。
5. 亮屏期间约 25 W 的耗电，与 04:36–04:48 用户使用时（约 23 W）相近，推测当时前台程序仍在运行；无法从现有日志确认是哪个进程。

## 管理员 sleepstudy / SRUM 补充（14:3x）

文件在 `artifacts/sleep-drain-20261011/`（sleepstudy.html/xml、srum.csv、requests.txt、lastwake.txt）。

- **待机本身健康（已核实）**：04:48:59 那次待机 1 小时 36 分，硬件低功耗（DRIPS）占 98.3%，只耗 451 mWh（约 0.28 W）。前一晚两次也是 98–99%。前几名阻碍者只是 Wi-Fi、PCIe 根端口、USB 控制器各 1% 左右，手柄并不是实质耗电者。退出原因记为 0x37 Restricted Standby Battery Drain Budget Exceeded，当时 HibernateTimeoutInSec=2147483647（从不休眠）。
- `powercfg /lastwake` 没有记录唤醒源。
- **亮屏 3 小时的耗电主体是 Steam 界面（SRUM 已核实）**：06:25–09:25 CPU 封装共约 50 Wh，其中 steamwebhelper.exe 约 26 Wh（CPU 14 Wh、显示 8 Wh），GPU（PP1）约 13 Wh。Magpie 日志显示游戏 04:47 已退出，夜里没有游戏在跑。对比 10-10 同类误唤醒 2.5 小时：steamwebhelper 12 Wh、GPU 2 Wh。昨晚 Steam 界面耗电约翻倍、GPU 约 6 倍，具体是哪个页面或主题在持续重绘无法从记录确认（推断与 Steam 大屏界面持续渲染有关）。
- 当前电源请求：DISPLAY 被 Sunshine.exe 持有（正在串流，属正常），EXECUTION 被 Claude 桌面应用持有。

## 已执行（2026-10-11 13:4x，用户同意改用睡眠后休眠）

仅改当前方案的电池（DC）值，插电值不变；前后完整设置在 `artifacts/sleep-drain-20261011/`。

- 电池下关闭显示器：10,800 秒 → 600 秒（10 分钟）
- 电池下待机后休眠：从不（0x7fffffff）→ 3,600 秒（1 小时）。误唤醒发生在进入待机约 1.5–2 小时后，1 小时休眠会先一步关机。

- 14:4x 用户要求"单击电源键后绝对不唤醒"：电源按钮操作 AC/DC 睡眠（1）→ 休眠（2），注册表与 `powercfg /qh` 已核实。上面两项保留作为其他入睡方式（Steam 菜单睡眠等）的兜底。

- 14:5x 用户改主意：电源键改回睡眠（1）；待机后休眠 1 小时同时应用到插电（AC 0 → 3600）。当前生效：电源键睡眠，AC/DC 待机 1 小时后休眠，电池下 10 分钟关屏。

恢复电源键：`powercfg /setacvalueindex SCHEME_CURRENT SUB_BUTTONS PBUTTONACTION 1`、`powercfg /setdcvalueindex SCHEME_CURRENT SUB_BUTTONS PBUTTONACTION 1`。
恢复：`powercfg /setdcvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOIDLE 10800`、`powercfg /setdcvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE 2147483647`，再 `powercfg /setactive SCHEME_CURRENT`。

## 其他可选项（未执行）

- **A（推荐）**：把电池下关闭显示器时间从 3 小时改成 10 分钟左右。误唤醒最多亮屏约 10 分钟后自动回到待机，不需要休眠。代价是电池下长时间看视觉小说不操作会熄屏。
- **B**：额外停用 Zako 虚拟鼠标做一晚对照测试；代价是 Sunshine 串流时鼠标不可用，需要恢复。
- 恢复手柄鼠标端点：管理员运行 `.\diagnostics\configure-gamepad-mouse-wake.ps1 -Mode Restore`（既然没挡住，可以考虑恢复桌面鼠标模式）。
