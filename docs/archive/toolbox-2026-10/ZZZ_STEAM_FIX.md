# 绝区零：Steam 运行状态与大屏幕侧栏修复

实机诊断和验证时间：2026-10-06，Asia/Shanghai。

## 实机发现

- 游戏目标是 `C:\Game\ZenlessZoneZero Game\ZenlessZoneZero.exe`，当前游戏版本为国服 3.2.0。
- 主程序的内嵌清单要求 `requireAdministrator`。最初 Steam PID 13544 为普通权限，测试游戏 PID 15432 为管理员权限。
- 快捷方式的 AppID 为 3518295947，完整 GameID 为 15110966030047903744。路径和工作目录正确，`AllowOverlay=1`。
- 普通权限 Steam 启动时，游戏目录的 `steam_appid.txt` 被写入 `11857803\n\0`。Steam 日志随后把仍在运行的游戏记为 11857803；原快捷方式没有出现在 `m_runningAppIDs` 中，覆盖窗口数为 0。
- 仅把 Steam 改成管理员运行后，覆盖窗口出现，但保留的错误编号文件仍使快捷方式显示未运行。
- 移走错误编号文件，并从管理员 Steam 重新启动原快捷方式后，Steam 日志改为跟踪完整 GameID，快捷方式出现在运行列表中，覆盖窗口正常建立，文件没有再生成。

不能根据这一例断言其他 Steam 版本也一定有相同的提权编号行为；以上结论来自本机 Steam 构建 1791249696 的对照结果。

## 已执行

1. 临时将 Steam 以管理员身份启动，PID 19136。
2. 将错误编号文件原样移到 `C:\Game\ZenlessZoneZero Game\steam_appid.txt.codex-backup-20261006`，保留可恢复副本。
3. 创建 `Steam 大屏幕（管理员）.lnk`，现位于仓库的本机目录 `local/shortcuts/`。目标 `C:\Software\Steam.exe`，参数 `-gamepadui`，Shell Link 的 RunAsUser 标志为 true。

## 下次启动

用户后续要求 Steam 默认以管理员身份启动。已为当前用户设置 `C:\Software\steam.exe` 的兼容性属性 `~ RUNASADMIN`，等同于主程序属性中勾选“以管理员身份运行此程序”。原值保存在 `tools/steam-default-admin-before.json`，恢复入口是 `tools/Restore-Steam-DefaultLaunch.ps1`。

以后可以通过原来的 Steam 快捷方式正常启动；Windows 管理员确认仍需手动接受。随后从 Steam 库中的原 Zenless Zone Zero 快捷方式启动游戏。独立的 `Steam 大屏幕（管理员）.lnk` 也可以继续使用。

已有普通权限 Steam 运行时，再启动另一份管理员 Steam 不能可靠地替换原进程。修改设置时现有 Steam PID 19136 已为管理员权限，没有运行游戏。通过 Playhub 或其他入口重新启动后，可运行 `tools/diagnose-zzz-permissions.ps1` 核对实际 Steam 权限；外部程序启动行为与 Windows 登录自动启动仍需分别验证。

## 验证范围

- 修复后游戏 PID 18540 与 Steam 均为管理员权限。
- 原快捷方式持续处于 Steam 运行列表中，状态为运行（`display_status=4`）。
- 覆盖进程 PID 25480 对应正确的完整 GameID。
- 在游戏登录画面上分别打开并观察了左侧主菜单和右侧快捷访问面板，菜单编号分别为 1、2，覆盖界面组合状态均为 2。
- 测试结束关闭侧栏，游戏仍在运行。
- Claw 按键切换插件的启用状态及 MSI→右栏、原 Game Bar 键→左栏设置正常。测试时 Playhub 为 Desktop，因此本次没有验证 Gaming 模式下的实体按键，也没有进入实际战斗。

## 参考

[Valve Steamworks API 文档](https://partner.steamgames.com/doc/sdk/api) 说明 `steam_appid.txt` 可以覆盖 Steam 提供的应用编号，Steam API 的使用也受操作系统用户和管理员权限上下文影响。本机错误文件的来源和具体运行状态由上述实机对照验证。
