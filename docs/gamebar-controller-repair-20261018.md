# Game Bar 手柄导航排查（2026-10-18）

后续状态（2026-11-02）：用户重启后确认导航恢复；恢复正常 HidHide 和虚拟手柄配置后，方向/A/B 及连续开关两次也通过实机验收。详见 [快设与桌面切换实施记录](special-keys-20261101.md)。具体系统根因仍未确定，下文保留当时的排查记录。

## 实机情况

- MSI Claw 实体手柄 `0DB0:1901`，固件模式为 XInput（1），方向键和 A/B 的固件映射正常。
- 用户确认其他按键检测正常；Game Bar 的键鼠和触摸正常，顶部系统栏和全部小组件均不响应手柄。
- Game Bar `7.326.10141.0`；GameInput Redist `4.0.300.0`，Windows 内置 GameInput `0.2309.26100.9278`。
- 已安装工具箱 `0.17.17`；本工作目录插件版本较旧，但 InputLayer 六个源文件与生成已安装引擎的工作目录逐个一致。

## 已排除的单独措施

以下每一步均由用户打开 Game Bar 实测，均未恢复导航：

1. 停用输入层、解除其 HidHide 隐藏并移除虚拟手柄。
2. 重启 Game Bar、GameBarFTServer 及两个 GameInput 服务。
3. 备份并重建 Game Bar 设置。
4. 正常退出 Steam、通过原交接协议正常停止 ClawTweaks helper，暂停两个自定义小组件的自动打开。
5. 重新注册当前已安装的 Game Bar 包，并关闭自动鼠标模式。
6. 将 `enableautomousemodepostbugfixes` 设置为 false。

Game Bar 原设置及小组件自动打开状态已还原，Steam 大屏幕、ClawTweaks helper 及输入层自动规则均已恢复。两个 GameInput 服务的启动类型未改动。

## 当前实验

仅添加：

```
HKLM\SOFTWARE\Microsoft\GameInputRedist\Exe\GameBar.exe
BackgroundInput = DWORD 1
```

这是针对新版 GameInput 后台输入限制的假设，尚不能写成已确认根因。完整 Game Bar 日志显示其初始化了 GameInput Redist 路径，并使用输入委托；输入委托时不一定切换 Windows 前台窗口。

应用与撤销：`tools/Set-GameBarInputCompatibility.ps1`（管理员）；加 `-Restore` 只还原本次修改。
原值和结果：`%LOCALAPPDATA%\ClawToolbox\repair-backups\gamebar-20261018\background-input*.json`。
Game Bar 设置原始值：同目录的 `settings-values.json`，可用 `tools/Repair-GameBarSettings.py restore` 还原（先关闭 Game Bar 及组件）。

## 同时发现并修补的输入层问题

- 原引擎进程 15924 累积 11803 个线程，后台状态一直是最初 enable 的快照。不能据快照中的 0 Hz 断言实时输入循环没有运行。
- 维护流程仍在调用文档中注明本机可能不响应的 ViGEm 用户编号 IOCTL。取消该调用，使用现有 XInput 槽位差值识别虚拟手柄。
- 维护回调先尝试获取锁，忙时立即跳过，避免定时回调排队累积线程。
- `tests/InputLayerTests.cs` 增加持锁时维护回调及时返回的回归检查；共 52 项检查通过。
- 仅替换已安装的 `ClawInput-0.17.17.exe`，其他工具箱文件未降级或重新安装。原文件 SHA256：`9D693BA4ACD2C3C390E26191E59221CD738D1214801683F48F69F650B10CEB1A`。
- 修补文件 SHA256：`3CF964D1D851A24A9330976292D72A3C156A29D41C47D7C85F96E226BE95636A`。
- 原二进制备份在上述备份目录。实机查询已确认循环约 799 Hz、虚拟手柄在 1 号槽、无输出失败，进程线程数保持十几个；输入层命令正常响应。

## 诊断证据

- `diagnostics/gamebar-complete-trace.xml`：从已完成的 `_Ro.etl` 解码。尚在写入的 `_Or.etl` 只包含跟踪头，不能用于判断没有输入事件。
- `diagnostics/GameInputProbe.cpp`：只读取手柄，不收集键盘输入；验证 Windows 内置与 Redist API 都能创建并读取手柄和导航控制器。
- `%LOCALAPPDATA%\ClawToolbox\data\gamebar-diagnosis-*.json`：初始后台状态。
- 手柄在 Game Bar 接管输入时，其他进程的读数可能保持为零；不能据此断言实体按键失效。
