# Playrail 项目文档

[项目首页](../README.md)介绍当前产品、安装与构建。本索引按模块汇集源码位置和开发记录。文件名带日期的记录描述当时的实现与验证范围。

## 按功能查找

| 功能 | 文档 | 源码入口 |
| --- | --- | --- |
| 游戏库、控制中心、设置 | [游戏库](library/) · [原生界面开发记录](native-library/README.md) | [ClawLibrary](../frontend/library-native/ClawLibrary/) |
| 游戏内快捷面板、无线连接、窗口切换 | [快捷面板](quick-access/) · [验证记录](../frontend/QAM/verification-0.1.245.md) | [快捷面板窗口](../frontend/library-native/ClawLibrary/QamWindow.cs) |
| 功耗、风扇、帧率、性能显示、缩放 | [性能与画面](performance/) | [功耗助手](../native/ClawPower.cs) · [性能叠加层](../native/PerformanceOverlay.cs) |
| 手柄、特殊键和键鼠映射 | [输入](input/) · [Steam Input 参考](steam-input-mapping/) | [原生输入层](../native/InputLayer/) · [按键路由](../native/ClawRouter.cs) |
| 游戏模式、睡眠、显示和声音 | [系统](system/) | [ClawShell](../native/ClawShell/) · [睡眠组件](../native/SleepAnimation/) |
| Xbox Game Bar 小组件 | [Game Bar](gamebar/) | [小组件源码](../native/GameBarWidget/) |
| Steam、Decky 与其他平台服务 | [平台集成](integrations/) | [插件与后台 Agent](../plugin/) |
| 设计规范、界面文案、项目展示 | [界面设计](design/) · [README 截图](screenshots/) | [品牌素材](../assets/branding/) |

## 构建与维护

根目录保留日常入口。在仓库根目录运行脚本，完整依赖和参数见[首页的源码构建说明](../README.md#从源码构建)。

| 任务 | 入口 |
| --- | --- |
| 构建 Decky 前端和后台辅助程序 | [build.ps1](../build.ps1) |
| 构建原生游戏库安装包 | [Build-Msix.ps1](../frontend/library-native/tools/Build-Msix.ps1) · [准备 Payload](../frontend/library-native/tools/Stage-OobePayload.ps1) |
| 构建修改版 Magpie | [build-magpie.ps1](../build-magpie.ps1) |
| 安装或升级 | [install.ps1](../install.ps1) |
| 运行通用回归测试 | [test.ps1](../test.ps1) · [独立测试项目](../tests/) |
| 打包 Decky 插件 | [package.ps1](../package.ps1) |

[tools/](../tools/) 存放单组件构建、安装、验证和诊断工具。需要定位用途时，可先查对应功能的文档。部分带版本号或日期的脚本会读取当时的本机快照。

## 本机文件

| 路径 | 内容 |
| --- | --- |
| `local/design/` 和 `local/fonts/` | 本机设计稿和字体原件。项目实际使用的素材位于 `assets/` 和各组件的素材目录。 |
| `local/installers/`、`local/logs/` 和 `local/shortcuts/` | 本机第三方安装包、散落的安装日志和个人快捷方式。 |
| `artifacts/` 与 `diagnostics/` | 构建输出、实验快照、设备日志及回滚资料。有脚本引用这些目录里的特定路径。 |
| `frontend/library-native/AppPackages/` 和 `plugin/bin/` | MSIX 打包输出和后台辅助程序。 |
| `tests/**/bin/` 与 `tests/**/obj/` | .NET 测试项目在本机构建时生成的输出。 |

以上目录中的本机文件通过 Git 忽略规则管理。并行开发目录可用 `git worktree list` 查询。

## 早期记录

2026 年 10 月早期的工具箱交接、试验、调研和故障记录集中在[工具箱归档](archive/toolbox-2026-10/)。归档中的 `manifest.json` 是交接快照的原始清单。依赖旧工作树的本机启动器集中在 [tools/legacy/](../tools/legacy/)。
