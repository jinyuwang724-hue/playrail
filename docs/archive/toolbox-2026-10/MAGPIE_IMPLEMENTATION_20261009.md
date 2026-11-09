# Magpie 集成首版：0.10.0

2026-10-09，基于 Magpie 稳定版 v0.12.1 源码编译，后台版本为 `0.12.1-claw`。工具箱提供等比铺满、FSR RCAS 锐化、0–100% 强度、游戏窗口选择、应用和恢复。捕获、裁剪、GPU 放大、锐化及鼠标坐标映射均使用 Magpie 核心。

## 已实现的控制

- `--claw` 隐藏桌面界面，不注册 Magpie 全局快捷键、不启用自动更新。
- 父进程继承的 stdin/stdout 管道传输 JSON，每条请求带编号，支持明确的 start/stop/state。UI 线程调度原生核心，指定 HWND、捕获方式、裁剪和 RCAS 参数。
- 插件只持有自己的子进程，检测已有普通 Magpie 实例后提示退出，不切换其他实例。后台在独立设置目录运行，不修改用户安装的 Magpie 配置。
- 铺满使用源区域裁剪加 Bicubic Fit，保持比例；关闭铺满时完整显示画面。不是直接使用会改变宽高比的 Fill。内置黑边可按每边像素手动修正。
- 只有点击应用才提交，浏览设置只修改草稿。打开 Steam 菜单暂停渲染；关闭所有菜单且返回指定游戏后恢复。暂停复用进程，恢复原画面和插件卸载关闭 stdin，后台正常清理退出。
- 从原生输出窗口回读实际源／目标矩形，存在可见输出才显示生效。捕获失败返回核心错误码，游戏退出、窗口尺寸变化、后台异常均有处理。

## 编译环境与复现

本机已安装 Visual Studio 2022 Build Tools 17.14.41（v143 14.44）、C++ 桌面与 UWP C++ 工具、Windows SDK 10.0.26100.0，安装状态 complete，无需重启。隔离 Python 环境位于 `artifacts/tooling/magpie-python`，Conan 2.33.0、CMake 3.31.10。没有修改全局 PATH。

```powershell
# 首次构建需先安装上述微软组件，创建隔离 venv 并安装以下版本。
python -m venv artifacts/tooling/magpie-python
artifacts/tooling/magpie-python/Scripts/python.exe -m pip install conan==2.33.0 cmake==3.31.10
./build-magpie.ps1
./build.ps1 -FrontendOnly
python tools/package-magpie-source.py
./install.ps1
./package.ps1
```

修改后的上游完整源码在 `native/upstream/Magpie-0.12.1`；主要改动为 `ClawControl.cpp/.h`、`App.cpp`、`ScalingService.cpp/.h` 和 vcxproj。Conan/NuGet 保留上游固定依赖版本。安装包附 GPL 许可、来源说明和对应源代码 ZIP。引擎保留独立进程，插件 Python/JS 负责控制。

## 已验证

本机 Intel Arc B390，1920×1200 SDR。使用可关闭的自有 WinForms 测试窗口，没有启动用户真实游戏。

- 实际捕获 640×480 窗口，裁上下各 40 像素，原生回读 640×400 → 1920×1200；棋盘格正方形保持比例，四边铺满。
- Graphics Capture 成功，Bicubic 和 FSR_RCAS 着色器编译并运行。锐化开启前后截图 RGB 平均绝对差为 0.358 / 0.419 / 0.466，确认实际像素变化。
- 暂停后输出窗口移除；返回时同一 PID 恢复；恢复原画面后子进程正常退出，无残留处理窗口。
- 几何／参数／读操作／重复应用／菜单重叠后台测试 6 项，前端草稿、防重复应用及旧实例卸载测试 3 项，已有菜单和前端回归 25 项、后端回归 12 项通过。

证据：`artifacts/magpie-live/result.json`、`cover.png`、`cover-sharpen.png`，日志在测试运行目录内。本机已安装并重载 0.10.0，实际 Steam 侧栏控件和未选游戏时禁用应用按钮检查通过；菜单关闭后 paused=false，默认 enabled=false、active=false，无 Magpie 残留进程。按键后台 ready，原绑定保持。未进行用户真实游戏测试。

## 使用和验证范围

在游戏窗口模式下打开工具箱“缩放与锐化”，刷新并选择游戏，设置开关和强度，点击应用，关闭菜单返回游戏。要关闭请点击恢复原游戏画面。首版优先使用标准捕获，老游戏失败可手动选择 GDI 并重新应用。

用户具体游戏、真实 Steam 游戏覆盖菜单、D 盘游戏、GDI、HDR、独占全屏和触控未逐项实测。首版按 SDR 使用，触控辅助尚未接入；鼠标沿用 Magpie 映射，手柄不增加虚拟设备。不承诺所有游戏、驱动或反作弊环境可用。功耗、帧率和延迟尚未做性能测量。
