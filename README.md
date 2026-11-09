<p align="center"><img src="assets/branding/claw-pages-128.png" alt="Playrail 应用图标" width="80"></p>
<h1 align="center">Playrail</h1>
<p align="center"><strong>为 MSI Claw 做的手柄优先 Windows 游戏界面</strong></p>
<p align="center">拿起手柄挑游戏。进游戏后调功耗、亮度或音量。为常玩的游戏保存各自的设置。</p>
<p align="center"><a href="#从游戏库开玩">游戏库</a> · <a href="#游戏中随手调">快捷面板</a> · <a href="#为每款游戏保留自己的设置">性能与游戏档案</a> · <a href="#安装与升级">安装与升级</a></p>

![Playrail 首页展示横向游戏封面架，选中艾尔登法环后可开始游戏、打开设置或收藏](docs/screenshots/0.1.267/home.jpg)

*以下五张截图均来自 Playrail 0.1.267.0 的实际运行画面。点击图片可看大图。*

## 从游戏库开玩

首页把游戏封面排成横向展架。选中一款就能看到最近游玩时间，并直接启动、收藏或进入它的设置。Steam 和本地游戏可以分开浏览。Steam 页把近期玩过的游戏放在上面，完整游戏库接在下面。

![Steam 游戏库，上方是最近游玩的横向卡带，下方是完整游戏库的竖版封面](docs/screenshots/0.1.267/steam-library.jpg)

## 游戏中随手调

快捷面板从屏幕左侧打开。常用的**功耗、亮度、音量**都放在顶部，往下就能调帧率上限、刷新率、性能显示和风扇。底部快捷键可以截图、打开屏幕键盘、切换窗口、连接无线设备或回到桌面。

![快捷面板打开在游戏库上，显示功耗、亮度、音量滑轨及窗口、无线和电源操作](docs/screenshots/0.1.267/quick-panel.jpg)

## 为每款游戏保留自己的设置

性能页把**实际芯片功耗曲线、温度和风扇转速**放在左侧，右侧可切换手动、自动和按电源调节，也能编辑风扇曲线。游戏内的性能叠加层可显示帧率和帧时间，用来观察设置调整后的表现。

![性能页左侧显示最近六十秒的芯片功耗和风扇曲线，右侧是功耗、CPU 和风扇设置](docs/screenshots/0.1.267/performance.jpg)

从游戏卡片进入设置，就可以让单个游戏沿用全局设置，或单独配置功耗、限帧、性能显示与手柄映射。比如给《艾尔登法环》单独设一套功耗和帧率，其他游戏继续使用全局设置。窗口游戏还可以通过 Magpie 缩放到全屏。

![艾尔登法环的游戏设置页，可选功耗档案及独立的限帧和性能显示设置](docs/screenshots/0.1.267/game-profile.jpg)

Playrail 还可通过 ClawShell 在游戏模式与 Windows 桌面之间切换。使用 Windows 桌面或 Steam 大屏幕时，也有 Xbox Game Bar 小组件和可选的 Decky 插件入口。 [游戏模式](docs/game-mode.md) · [Game Bar 功能](docs/gamebar-widget-features.md)

## 安装与升级

当前仓库提供源码。原生安装需要带后台组件的已签名 x64 MSIX，游戏库版本至少为 **0.1.265.0**。当前源码的包版本为 **0.1.267.0**。本地签名包需要 Windows 已信任与包匹配的签名证书。

在仓库根目录打开 PowerShell，把命令里的路径改成手头的安装包路径。

```powershell
$msix = 'C:\path\to\ClawLibrary_0.1.267.0_x64.msix'
.\install.ps1 -LibraryMsix $msix -NativeOnly
```

1. 同意后台安装的管理员权限提示。安装器会配置随包提供的 Agent 和其他后台组件。
2. 从开始菜单打开 **Claw 游戏库**。已有安装可用同一条命令原地升级，保留应用包身份和本地设置。
3. 已装新版游戏库时，可在「设置 → 维护 → 安装或更新后台」重新运行随包的后台安装。

**Decky 入口**适用于已运行 Decky Loader 的 Windows Steam 大屏幕或 Playhub Gaming。先构建插件前端和原生组件，再用 [安装脚本](install.ps1) 配置。与原生游戏库共用后台时，安装命令可以同时传入 `-LibraryMsix` 并省略 `-NativeOnly`。

如果后台安装失败，检查 `%LOCALAPPDATA%\ClawToolbox\setup` 内的 `install.log` 和对应的 `status-<运行编号>.json`。最近一次成功安装的组件版本记录在 `backend.json`。

## 兼容性

- **主要设备**为 MSI Claw 8 EX AI+。原生包面向 x64，项目的快捷面板和窗口切换验证记录使用 MSI Claw 与 Windows 11 25H2。功耗、风扇和特殊按键依赖设备与驱动。 [验证记录](frontend/QAM/verification-0.1.245.md)
- **性能与风扇**需要相应的后台权限和硬件支持，部分调节依赖 PawnIO。VRR、刷新率和驱动限帧选项取决于显示器与驱动。 [性能功能说明](docs/performance-page-features.md)
- **无线设备**的连接能力取决于 Windows、适配器和设备类型。经典蓝牙音频和由驱动持有的 HID 连接由系统管理。Wi-Fi 实际连接和实体设备配对还有待扩大验证。 [无线验证范围](frontend/QAM/verification-0.1.245.md)
- **游戏模式恢复**可用登录时按住 Shift 的方式回到桌面。遇到黑屏，可用 Ctrl+Alt+Del 打开任务管理器并运行 `explorer.exe`。 [游戏模式与恢复流程](docs/game-mode.md)

## 源码构建

| 目标 | 环境 |
| --- | --- |
| Decky 前端与后台辅助程序 | Windows x64、.NET Framework 4.x 的 `csc.exe`、.NET 9 SDK、Python 3.12 和 Node.js。 |
| 原生游戏库 MSIX | .NET 9 SDK、带 MSVC x64 的 Visual Studio Build Tools、Windows SDK、`CurrentUser\My` 中主题为 `CN=ClawToolbox` 的签名证书，以及准备齐全的 Payload。 |
| 修改版 Magpie | Visual Studio 2022 Build Tools、桌面 C++、UWP C++ 与 Windows SDK 26100。 |

```powershell
# 仅构建 Decky 前端
.\build.ps1 -FrontendOnly

# 首次构建后台，替换为本机的 Python 3.12 路径
.\build.ps1 -Python 'C:\path\to\python.exe'
.\test.ps1

# 从源码单独构建缩放组件
.\build-magpie.ps1
```

原生包使用 [Build-Msix.ps1](frontend/library-native/tools/Build-Msix.ps1)，输出到 `frontend/library-native/AppPackages/`。完整 MSIX 还需要当前版本的 ClawShell、游戏会话、Agent、Game Bar 小组件、PawnIO、风扇服务及睡眠组件。 [Payload 收集脚本](frontend/library-native/tools/Stage-OobePayload.ps1) 从已准备的本机组件收集所需文件，构建脚本会检查缺失文件及外壳版本。

[package.ps1](package.ps1) 将 Decky 插件打包到 `artifacts/`。它依赖生成的运行时、辅助程序及本机准备的 `plugin/resources/clawtweaks-home.patch.gz`。这些构建输入与输出由本地环境准备。

## 开发资料

| 目录 | 职责 |
| --- | --- |
| [frontend/library-native/](frontend/library-native/) | WinUI 3、Composition、NativeAOT 游戏库，包含快捷面板、控制中心与单游戏设置。 |
| [frontend/library/](frontend/library/) | TypeScript 网页版游戏库原型。 |
| [plugin/](plugin/) | JavaScript Decky 前端与 Python 后台 Agent。 |
| [native/](native/) | C# 输入、功耗、外壳和睡眠组件，C++/WinRT Game Bar 小组件，以及修改版 Magpie。 |
| [tests/](tests/) 与 [tools/](tools/) | 回归测试、构建、安装与诊断脚本。 |

- [原生游戏库开发记录](docs/native-library/README.md)
- [快捷面板验证与场景覆盖](frontend/QAM/verification-0.1.245.md)
- [Game Bar 小组件功能](docs/gamebar-widget-features.md)
- [增强睡眠与唤醒](docs/enhanced-sleep-20261029.md)

## 许可

本项目自有代码采用 MIT 许可。修改版 Magpie 源码及其编译程序采用 GPL-3.0。其他依赖及其许可见第三方声明。 [MIT](LICENSE) · [Magpie 许可](native/upstream/Magpie-0.12.1/LICENSE) · [第三方声明](native/Scaling/THIRD_PARTY_NOTICES.md)
