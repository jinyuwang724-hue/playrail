# QAM 0.1.245 验证记录

2026-11-06，北京时间。设备为 MSI Claw，1920×1200，Windows 11 25H2（26200.9711）。从 0.1.243.0 原地升级到 0.1.245.0，保留应用身份和缓存。ClawShell 1.0.11 已同步到用户外壳目录，管理员任务已注册。测试结束后回到保留 Explorer 的桌面模式，默认启动模式仍为桌面。

## 快捷面板 Wi‑Fi 和蓝牙

- 同一个 WinUI 3 / Composition QAM 窗口承载列表与屏幕键盘。Wi‑Fi 和蓝牙格短按 A 切换开关，按 →、长按 A 或触摸长按进列表。进出页使用 56 px、420 ms 的相机曲线，支持打断。
- Wi‑Fi 使用 WiFiAdapter 扫描及连接，WLAN 配置用于已保存网络和忘记操作。实现密码输入与显隐、错误反馈、已连接/可用分组、断开和二次确认。个人热点没有可靠的扫描元数据时使用普通网络行样式。
- 蓝牙使用 DeviceWatcher 和自定义配对，支持数字比较、手动 PIN、Just Works、忘记，以及可由本应用管理的 BLE GATT 会话。设备公开 GATT 电量时显示读数。经典音频和由 Windows 或驱动持有的 HID 连接仍由系统管理，列表显示系统上报的实际连接状态。参见 [Windows GATT 客户端](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)及[经典蓝牙 API 的范围](https://learn.microsoft.com/en-us/windows/win32/bluetooth/about-bluetooth)。
- 1920×1200 的 Debug 实际界面截图使用固定数据。导航探针检查 B 依次关闭键盘、收起展开行、返回原格。实速录屏含反向打断，采集 102 帧并按原始时间戳导出 30 fps。一次独立截图探针有 26–28 ms 的重绘间隔，未将这些记录作为逐帧无掉帧验收。
- 真机使用测试网络连接、错误密码、断开与忘记，及蓝牙手柄/耳机配对、断开与忘记仍待完成。D3D11 程序前台唤出 QAM 时前台 HWND 保持不变，游戏中的密码输入隔离还需实体手柄与触摸测试。

Wi-Fi 列表、密码键盘、蓝牙配对截图和动画录屏保存在本地 `artifacts/qam-0.1.245/`。

## 底边上滑

- 设备上实际运行了 `\ClawToolbox\GameModeEdgeSwipe`。进入游戏模式时 `HKLM\SOFTWARE\Policies\Microsoft\Windows\EdgeUI\AllowEdgeSwipe` 从不存在变为 `DWORD 0`，回到桌面后恢复为不存在。任务运行结果为 0，保留 Explorer 的游戏模式里没有重启 Explorer。
- 对最终 C# 策略逻辑使用隔离 HKCU 路径验证。覆盖原值缺失、重复进入、DWORD 原值、未展开的 ExpandString 原值和会话标记缺失时的恢复。现有开始菜单／搜索出现后收起的兜底仍接在窗口前台监听和切换流程上。
- 桌面触摸注入对照返回 Win32 87，5 次轨迹没有产生有效输入。底边手势即时拦截、桌面手势恢复、四种前台各 20 次、强制关机后的恢复及输入法、触摸键盘、Game Bar 回归仍需真机操作。微软文档建议重启 Explorer 或设备后验证此策略。系统还另有触控优化任务栏设置，这次未实测其注册表替代项。 [边缘策略](https://learn.microsoft.com/en-us/windows/iot/iot-enterprise/customize/edge-swipe-policy) · [触控优化任务栏](https://support.microsoft.com/en-us/windows/experience/personalization/customize-the-taskbar-in-windows)

## 窗口切换

通过现有库窗口消息注入唤出切换器、选中明确的测试 HWND、确认，并读回 Windows 前台窗口。窗口切换日志记录目标进程、提权标记、窗口是否覆盖显示器、每步耗时、前台 HWND 及错误码。通用 Win32 路径无法读取外部进程的 DXGI 独占标志，日志记录 `exclusive=unknown`。独占测试由测试程序记录自身交换链状态。

| 场景 | 方法与结果 |
| --- | --- |
| D3D11 独占全屏 | 专用测试程序确认 `GetFullscreenState=TRUE`，三个往返 6/6，端到端 338–484 ms，切回均恢复 `Present=S_OK`。唤出 QAM 时测试程序仍拥有前台 HWND。切换期间记录到短暂 `DXGI_STATUS_OCCLUDED`，截图可见系统合成后的标题栏。未使用商业游戏或逐帧测量分辨率切换。 |
| 管理员进程 | 提权 WinForms 程序与普通窗口往返 6/6，339–397 ms，目标提权由进程令牌确认。 |
| 文件管理器 | 200 次普通窗口混合样本中，68 次以 Explorer 为目标，68/68，P50 453 ms、P95 519 ms。它也作为部分试次的源窗口。 |
| 物理特殊键 | 路由和输入隔离走模拟路径。物理短按和长按各 30 次、固件串扰待验收。 |
| 手指底边上滑后立即切换 | 待实体触摸验收。 |
| 系统设置和开始菜单 | 系统设置的测试窗口未能通过脚本稳定拿到前台，因此没有形成有效回归样本。 |
| 随机顺序混合切换 | 两个普通 WinForms 窗口与一个 Explorer 窗口，200/200。P50 453 ms、P95 531 ms、最长 664 ms。内部激活路径 P50 94 ms、P95 125 ms。无回退、无最终空前台。样本不包含独占、提权和实体按键；前两项单独测试。 |

200 次切换的 CSV、切换阶段日志、独占日志和提权日志保存在本地 `artifacts/qam-0.1.245/`。

## 构建

Debug 编译及签名 NativeAOT MSIX 构建通过。ClawShell 42/42，设置队列和触控回归通过，手柄选择及轮询派发测试通过。安装使用升级方式，后台沿用 0.17.58，本次修改位于游戏库与 ClawShell。后台原有就地热补丁的后续版本合并仍属后台发布工作。

MSIX SHA-256 为 `947D77EF6CC692996FC0A13CC75E9662299C3B5CE08658DE819398F44D536BB0`。
