# Decky 独立启动与 Playhub 清理

2026-10-18，北京时间。按用户要求替换启动脚本并移除 Playhub 安装残留。

## 启动入口

源码：`tools/Start-Decky.ps1`。安装：`C:\Users\13304\homebrew\services\Start-Decky.ps1`。两者 SHA256 相同：`d2972e0f8c07e4ded78d66db5777d9591551ac3ca81e1d0e622dc8c5ab939736`。

原脚本通过 `Global\Playhub.Decky.Start` 互斥锁防重复，等待 1337 端口，并为进程添加独立 Python 路径。新脚本使用 `Local\ClawToolbox.Decky.Start`，核对占用端口的可执行文件路径，既检查监听又检查前端 HTTP 200，已有进程不响应时记录错误而不启动副本。默认隐藏启动 `PluginLoader_noconsole.exe`，只给该进程添加 `services/python-3.11.9` 到 PATH，不更改全局 PATH。失败和首次就绪记录到 `services/Decky-startup.log`。

现有 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DeckyLoader` 已改为系统 PowerShell 隐藏运行新脚本。`tools/Restart-Decky.ps1` 同步更新。原安装目录内的 `Playhub-StartDecky.ps1` 已移入回收站。

后续修正：02:51 重启后发现应用内的 HKCU 视图与 Windows 启动提供程序读取的真实注册表不一致。原先直接在应用内写 Run 项并未替换系统实际使用的旧脚本路径。02:59 已通过一次性应用外任务执行 `tools/Register-DeckyStartup.ps1`，随后用系统 `Win32_StartupCommand` 逐字核对实际命令。今后注册或修复自启动须使用：

```powershell
.\tools\Invoke-OutsideApp.ps1 -Script "$PWD\tools\Register-DeckyStartup.ps1" -TimeoutSeconds 45
```

系统原启动项和核对结果保存在 `artifacts/decky-autostart-fix-20261018/`。03:00 已在应用外实际启动 Decky，前端和两插件后端正常；这次没有再重启 Windows，不能把应用外启动测试表述为已经过第二次登录验证。

## 清理结果

- 经 Decky 正常卸载入口移除 Playhub、Playhub Artworks、Launch Curtain；其余文件清理前均核对绝对路径位于用户的 homebrew 目录，且不含目录链接。
- 一个此前遗留的 QuickSettingsAgent 仍在运行；核对其路径为 `plugins/gaming-mode/quick_settings/bin/QuickSettingsAgent.exe` 后结束该进程。
- 剩余 Playhub 插件目录、三个插件的 settings/data/logs、五个 Playhub 主题目录、旧启动脚本共 16 个路径移入回收站。原批量永久删除命令被自动安全检查阻止，未执行；后续改用可恢复的回收站操作完成。
- 从当前启用的 `css.profile` 中移除 `Playhub Collections` 依赖，保留其余主题和 CSS 预设。Decky 插件顺序更新为 Claw 掌机工具箱、CSS Loader。
- 保留此前修好的 Decky EXE、独立 Python、Claw 工具箱及其独立后台。未变更游戏库、功耗、风扇和代理设置。
- 开发项目中的历史源码、交接文档和修复备份属于开发记录，未作为软件安装残留删除。

## 验证

新脚本从无 Decky 进程的状态成功启动服务，前端返回 HTTP 200。后端插件列表仅有 Claw 掌机工具箱 0.17.17 和 CSS Loader 2.1.2；Claw 插件重新连接原独立后台 PID 10180。两次并发启动请求均返回成功，Decky 进程集合保持不变。QuickSettingsAgent 已不存在，47993 不再监听。活动 homebrew 文件名、主题配置及 loader 设置中未发现此次移除的 Playhub 组件。

未重新启动 Windows，也未为了检查而重新打开 Steam。原启动项、脚本及改动前的 loader/CSS 配置保存在本机 `artifacts/playhub-cleanup-20261018/`，供恢复参考。
