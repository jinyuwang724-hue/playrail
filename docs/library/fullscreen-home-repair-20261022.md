# 全屏体验主页选择无效：2026-10-22

用户在游戏库「设置 → 通用 → 全屏体验」选择 Claw 游戏库，但 Windows 设置「游戏 → Xbox 模式」显示「无」。

## 原因

`claw_settings.py` 仅写入 HKCU GamingConfiguration 的 `GamingHomeApp` 和 `StartupToGamingHome`，并把硬编码的 Claw AUMID 当作可选主页。已安装游戏库 0.1.66 / 0.1.67 的清单只有协议注册和 runFullTrust，没有 windows.gamingApp 扩展、gamingHome 自定义能力和包根目录的 SCCD。

现场只读查询确认：Windows 的 AppExtensionCatalog 有 ClawTweaks Library、AnyFSE、Xbox，缺少 Claw 游戏库。注册表有 Claw AUMID 不等于它已经成为系统认可的主页。

## 已部署

- 将当前安装的 0.1.67 包重新打包为 **0.1.68.0**，补齐注册扩展、能力和 SCCD；通过原有 CN=ClawToolbox 证书签名，Add-AppxPackage 原位升级，没有卸载。
- 为保留另一工作树已经安装的外观/屏保等更新，**此次部署保留 0.1.67 的程序与资源**。ClawLibrary.exe 的 SHA256 前后均为 `5F59EA9A3E66AED70284F1D87A74B084D3477028F315CD7D082BFB59CDF2EE5B`。
- 只部署 `claw_settings.py` 到 0.17.56 后台，保留其版本、其他文件与设置。后台正常清理后通过原登录任务重启。新逻辑读取系统扩展目录，并检查 gamingHome 能力；未注册应用不能再写为有效主页。
- 源码清单、csproj 已补齐对应声明。设置页源码也改为跟随有效列表、显示未生效原因，并在后台确认后保存 Steam 跳转偏好。这部分 C# 改动已经编译通过；**未用主工作区的较旧前端覆盖已安装的外观/屏保版程序**，后续合并前端时应一并带入。
- Build-Msix.ps1 安装步骤改用原位升级；不再先卸载，以保留本地应用数据。

## 验证

- 6 项设置回归检查通过：未注册配置不算有效、拒绝未注册目标且不写注册表、有效主页写入和回读、关闭只影响自启、目录查询失败可见且阻止写入、空/单项列表与强制刷新。
- 修复后系统目录包含 `ClawToolbox.Library_ba3r68zr466za!App`，后台回读 current 为同一 AUMID，startup=true，error=null。
- 使用 computer-use 实际查看 Windows 设置；离开并重新进入 Xbox 模式页后，「选择主应用」显示 **Claw 游戏库**，「启动时进入 Xbox 模式」为 **开**。这是系统界面验证，非仅注册表回读。
- 未重启或注销 Windows，未验证下一次登录自动进入全屏体验。

修复包、原始清单、原始 GamingConfiguration 值、打包/签名日志及安装记录：`artifacts/library-fse-20261022-200545/`。
后台原始模块备份：`%LOCALAPPDATA%/ClawToolbox/repair-backups/fse-settings-20261022-200720/`（首次部署备份）；重启部署记录在后续 `fse-settings-20261022-200749/`。

重现工具：`tools/Repair-LibraryFse.ps1`（默认仅生成包，`-Install` 原位升级）；`tools/Deploy-FseSettings.py`（仅更新设置模块及正常重启后台）。

## 21:16–21:21 自动进入排查

用户确认问题是「重启或重新登录后，没有自动进入」。当前已安装版本为 0.1.69.0，扩展和 SCCD 均保留，系统目录仍认可 Claw 主页，GamingHomeApp 为 Claw AUMID，StartupToGamingHome=1。系统 API 回读当前并不在 Xbox 全屏体验中。

本次实际系统启动时间仍为 19:46:42，explorer / winlogon 的会话也仍是 19:46 建立。游戏库日志记录 20:59:34 请求 shutdown.exe /r /t 0；System/User32 事件 1074 记录该请求，21:00:39 的事件 1073 明确记录重启/关机失败。因而在 20:06 修复主页注册后，本机尚未完成一次新的重启/登录测试，不能将当前现象说成「成功重新登录后 Windows 没有启动主页」。

同时发现 Decky 旧主进程 21392 已退出，插件子进程 21308 / 21344 仍加载 `_MEI000051482` 中的 DLL，启动器 20808 的 Warning 报「Failed to remove temporary directory」。这解释了临时目录清理失败，但现有事件未点名它是阻止重启的唯一程序。

21:20 已在核对完整可执行路径、父进程确实不存在、1337 未监听后，结束这两个孤立子进程及旧启动器，再通过原 Start-Decky.ps1 恢复。恢复后 1337 前端 HTTP 200，旧孤立进程不再存在；Claw 主页配置和启动开关保持不变。没有执行 Windows 注销或重启，也未强制关闭用户其他程序。

自动进入完整链路仍待一次实际成功的重新登录验证；本次恢复残留不等于已经证明 Decky 今后的所有关机退出都正常。
