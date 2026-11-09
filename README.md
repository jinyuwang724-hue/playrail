<p align="center"><img src="assets/branding/claw-pages-128.png" alt="Playrail 图标" width="80"></p>

# Playrail

面向 MSI Claw 的手柄优先 Windows 游戏界面。通过游戏库启动 Steam、已安装的 Xbox 和本地游戏，在游戏内打开快捷面板，或给常玩的游戏单独配置功耗、画面和手柄。

- **游戏库**支持浏览、搜索、收藏与单游戏设置。
- **快捷面板**集中提供功耗、亮度、音量、帧率上限、刷新率、截图、窗口切换与无线开关。
- **性能和系统**包含实时功耗、风扇曲线、性能叠加层、画面缩放，以及游戏模式和 Windows 桌面切换。

## 1.0 测试版

面向 Windows 11 x64，主要设备为 MSI Claw 8 EX AI+。到 [GitHub Releases](https://github.com/jinyuwang724-hue/playrail/releases/tag/v1.0.0-beta.1) 下载 MSIX、公钥证书和 SHA-256 校验表。测试包使用自签名证书。核对校验值后，在用于测试的设备上信任该证书，再安装 MSIX。打开开始菜单中的 **Claw 游戏库**，按首次设置向导安装后台组件。 [微软 MSIX 测试签名说明](https://learn.microsoft.com/windows/msix/package/sign-msix-package-guide)

## 源码与构建

- 原生游戏库和快捷面板位于 [frontend/library-native](frontend/library-native/)。
- Decky 前端和 Python 后台位于 [plugin](plugin/)，硬件、输入与系统组件位于 [native](native/)。
- 构建依赖 Windows x64、.NET 9 SDK、Visual Studio C++ Build Tools 与 Windows SDK。后台构建还需要 Python 3.12，前端测试使用 Node.js。
- [build.ps1](build.ps1) 构建后台辅助程序。原生 MSIX 使用 [Build-Msix.ps1](frontend/library-native/tools/Build-Msix.ps1)，需准备签名证书及脚本所列 Payload。

项目自有代码采用 [MIT](LICENSE)。修改版 Magpie 及其对应源码采用 [GPL-3.0](native/upstream/Magpie-0.12.1/LICENSE)。组件许可见[第三方声明](frontend/library-native/ClawLibrary/Assets/THIRD-PARTY-NOTICES.txt)。
