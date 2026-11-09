# 游戏模式 / 桌面模式（ClawShell）

- **游戏模式**：登录后由 `%LOCALAPPDATA%\ClawToolbox\shell\ClawShell.exe shell` 当 Windows 外壳（HKCU `Software\Microsoft\Windows NT\CurrentVersion\Winlogon` 的 `Shell` 值），不启动 explorer；它代跑登录项（Run 键、启动文件夹，按「启动应用」开关），拉起游戏库并一直看着它。
- **桌面模式**：没有 `Shell` 值，普通 explorer。
- **不重启切换**：控制中心 › 电源 ›「切换到桌面 / 返回游戏模式」；桌面上的「返回游戏模式」快捷方式；任何时候按住手柄 **视图 + 菜单** 2 秒。只启停 explorer，游戏库常驻。
- **默认模式**：设置 › 通用 › 开机进入。选游戏模式时隐藏「全屏体验」（那是 explorer 的功能）。

## 护栏

| 情况 | 结果 |
| --- | --- |
| 登录时按住 Shift，或按住手柄 视图 + 菜单（前 2 秒） | 进桌面，删掉 `Shell` 值 |
| 存在 `%LOCALAPPDATA%\ClawToolbox\force-desktop.flag` | 进桌面，删掉 `Shell` 值；拒绝切到 / 设成游戏模式，直到删掉这个文件 |
| 游戏库 60 秒内退出 / 起不来 3 次 | 启动 explorer，删掉 `Shell` 值 |
| ClawShell 自身任何异常 | 启动 explorer，删掉 `Shell` 值 |
| 切到游戏模式前游戏库或 Steam 起不来 | 不关 explorer |
| explorer 关不掉（被 Winlogon 拉回） | 恢复 explorer，报失败 |

万一黑屏：Ctrl+Alt+Del › 任务管理器 › 运行新任务 `explorer.exe`；或删掉上面的 `Shell` 值后注销。

## 实现

- `native/ClawShell`：`ShellGuard.cs`（决策，测试用假系统）、`WindowsShellSystem.cs`（注册表、explorer、XInput、激活游戏库）、`Program.cs`（命令行）。部分做法参考 Playhub v2.1.0（MIT，见 `PLAYHUB-LICENSE.txt`）。
- 构建 + 测试：`tools\Build-ClawShell.ps1`（`tests/ClawShellTests.cs`，42 项）。
- 游戏库（MSIX）带 `--task` 调用 `Payload\shell\ClawShell.exe`：它经计划任务 `\ClawToolbox\Shell` 把自己复制到 `%LOCALAPPDATA%\ClawToolbox\shell` 再在应用包外运行（应用包里起的进程写 HKCU 会进包的虚拟注册表、explorer 也会跑在包里；计划任务不能直接运行 WindowsApps 里的 exe）。退出码经 `shell\req-<id>.txt` 回传，详情在 `shell\state.json`，日志 `logs\shell.log`。
- 没有 explorer 时：系统触摸键盘（TextInputHost）、Win+G、`ms-gamebar:` 都不可用；游戏库输入框改开 `osk.exe`。
