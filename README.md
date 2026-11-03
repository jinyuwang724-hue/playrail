# Claw 掌机工具箱

面向 Windows 掌机 **MSI Claw 8 EX AI+** 的 Decky Loader 插件，在 Steam 大屏幕／Playhub Gaming 的侧栏里提供一组常用工具。

## 功能

- **原生游戏库触控**：各页面支持点击、滑动、列表滚动与数值调整，补齐快捷设置、截图及单游戏手柄子页；交互记录见 [全页面触控交互](docs/touch-pages-20261029.md)，最新源码与分类焦点修复见 [0.1.185 修复记录](docs/library-touch-fix-20261030.md)。
- **休眠与唤醒动画**：独立后台接收实体电源键，先播放约 1.15 秒的双页合拢动画再休眠，恢复画面后播放约 0.95 秒的展页与整体淡出；系统页有两个独立开关。支持登录自启动、异常恢复和开机恢复，见 [安装与验证记录](docs/sleep-animation-20261026.md) 与 [新版动效记录](docs/sleep-animation-motion-20261026.md)。
- **登录后加载过渡**：游戏库内部显示无文字双页呼吸动画，等用户桌面中的库真正可见、且画面就绪后整体淡出。Game Bar 与游戏库的关机、重启菜单也已接入进入动画；Windows 登录界面仍由系统显示。见 [修正与验证范围](docs/startup-power-animation-fix-20261027.md)。
- **缩放与锐化**：基于修改版 Magpie 0.12.1，把窗口模式游戏等比铺满屏幕，可选 FSR、Anime4K 等原版效果方案或自定义效果链，并追加 RCAS 锐化；打开 Steam 菜单时暂停，返回游戏后恢复。
- **按键映射**：接管 Claw 前置键，在 Steam 大屏幕中映射为 Steam 侧栏／快速菜单，退出后恢复桌面原有行为；处理 Game Bar 抢占问题。
- **导航反馈**：在 Steam 界面中移动焦点时提供手柄震动反馈。
- **性能与功耗**：调节 TDP 与 CPU 频率（通过 PawnIO），支持模拟全核心 E 核（修改后重启生效，并可恢复原始设置），逐步替代厂商的体感助手。
- **添加非 Steam 游戏**：在大屏幕里浏览各个磁盘，选择 `.exe` 或快捷方式，填写名称和 Steam 分类后加入游戏库。
- **网络代理**：控制本机 Clash Verge（模式、TUN、系统代理、节点选择、订阅添加与刷新）。
- **兼容性修复**：针对个别游戏（如鸣潮、绝区零）的启动与权限问题提供修复入口。

## 目录

| 路径 | 内容 |
| --- | --- |
| `plugin/` | 插件前端（JS）与后端（Python） |
| `native/` | 辅助程序 C# 源码（按键路由、功耗、Clash 桥接、Magpie 启动器等） |
| `native/upstream/Magpie-0.12.1/` | 修改过的 Magpie 源码，GPL-3.0 |
| `tests/` | 前后端测试 |
| `tools/` | 诊断、安装、修补脚本 |
| `*.md` | 开发记录与交接文档，`CLAUDE_HANDOFF.md`、`DEVELOPMENT.md` 为当前状态 |

## 构建

需要 Windows、.NET Framework 4 自带的 `csc.exe`、Node 与 Python；构建 Magpie 还需要 Visual Studio 2022 Build Tools（C++ 桌面、UWP C++、Windows SDK 26100）。

```powershell
.\build.ps1          # 编译插件与辅助程序
.\build-magpie.ps1   # 编译修改版 Magpie
.\tools\Build-SleepAnimation.ps1 # 单独编译休眠动画组件（.NET 9 Desktop Runtime）
.\test.ps1           # 运行测试
.\package.ps1        # 打包到 artifacts/
```

仓库不包含构建产物、诊断日志与本机备份，也不包含 `plugin/resources/clawtweaks-home.patch.gz`（第三方程序 ClawTweaks 的二进制补丁）。缺少该文件时 `package.ps1` 会报错，需要在本机另行生成。

## 许可

本项目自有代码使用 MIT 许可，见 [LICENSE](LICENSE)。`native/upstream/Magpie-0.12.1/` 及由它编译出的 Magpie 程序遵循 GPL-3.0，见该目录下的 LICENSE；分发包含 Magpie 的安装包时需同时提供对应源码。`native/Scaling/THIRD_PARTY_NOTICES.md` 列出了其他第三方组件。
