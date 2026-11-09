# 睡眠唤醒免登录设置

2026-10-07 已修改本机 Windows 的睡眠唤醒登录设置。

检查发现，当前电源方案 `Claw Xbox Sleep Display` 的插电和电池“唤醒时需要密码”均为开启，当前用户 `DelayLockInterval` 为 `0`，表示关屏后立即锁定。本机使用 S0 低电量待机。

已完成并回读确认：

- 当前方案 `50e23985-8a76-4170-881b-c380b06e6cdd` 的 AC / DC `CONSOLELOCK` 均设为 `0`。
- 当前用户 `HKCU\Control Panel\Desktop\DelayLockInterval` 设为 DWORD `0xffffffff`（从不）。
- 重新应用当前电源方案，并向 Windows 广播设置变更通知成功。
- 电源按钮的 AC / DC 操作仍为睡眠（`1`）。

此处已验证配置保存成功；没有实际执行睡眠或重启。按电源键进入睡眠、再按电源键唤醒后的实际效果，需要用户在设备上验证。

原始配置备份与执行结果保存在：

`artifacts/windows-wake-login/20261007-103430/`

- `before.json`：修改前的电源及当前用户设置。
- `apply-result.json`：执行成功及通知成功的记录。
- `configure-wake-login.ps1`：本次修改脚本；以当前用户的管理员权限运行并添加 `-Restore` 可恢复备份中的原始设置。

微软说明：[唤醒后密码要求](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-power#requirepasswordwhencomputerwakesonbattery)；[DelayLockInterval 与显示器关闭后的锁定时间](https://jpwinsup.github.io/blog/2026/01/05/Shell/Explorer/unexpexted-early-screen-lock/)。
