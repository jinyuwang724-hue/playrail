# 等比铺满与 Magpie 集成评估

> 历史评估记录。后续 0.10.0 已完成源码编译集成、安装和测试窗口验证；当前实现见 [MAGPIE_IMPLEMENTATION_20261009.md](MAGPIE_IMPLEMENTATION_20261009.md)，下文“未实现”等状态仅描述评估时点。

日期：2026-10-09（Asia/Shanghai）。本轮完成旧功能撤除与可行性评估；新功能尚未实现，也没有安装或运行 Magpie、启动游戏验证画面。

## 结论与当前范围

用户取消 SteamOS 五种缩放模式、四种过滤器及工具箱自行实现锐化的方案。新的目标只有：保持比例铺满屏幕，允许裁剪超出边缘；由 Magpie 处理锐化。

两项从源码和架构上可行。建议让 **同一套 Magpie 捕获、裁剪、缩放和锐化管线** 处理画面，工具箱负责控制。没有必要先用自有后台缩放，再让 Magpie 捕获一次。

| 需求 | 判断 | 所需工作 |
|---|---|---|
| 等比铺满并居中裁剪 | 可行，适配工作中等 | 按有效游戏画面和目标显示器计算源裁剪，将裁剪后的画面交给 Magpie 放大 |
| 使用 Magpie 锐化 | 可行，算法已有 | 配置轻量锐化效果，例如 CAS 或 FSR_RCAS，以及锐度参数 |
| 在插件中直接控制两项 | 可行，不能仅做启动程序入口 | 增加控制适配、真实状态回读、参数生效和 Steam 菜单交接 |

本机只读查询显示 Windows 11 10.0.26200、Intel Arc B390 GPU、当前显示分辨率 1920×1200。系统和显卡类别适合开展验证。运行进程、卸载注册表及已检查的常见安装目录未发现 Magpie；这不排除便携版位于其他位置。没有把源码可行性当成这台掌机上已验证的游戏效果。

## “等比铺满”的准确含义

目标是 cover：横纵使用同一个倍率，直到两个方向均覆盖屏幕，再将超出的部分居中裁剪。不能只最大化窗口；一些游戏会在窗口内部继续绘制黑边。

设有效画面尺寸为 `W×H`，目标显示器为 `SW×SH`：

```text
scale = max(SW / W, SH / H)
visibleWidth  = SW / scale
visibleHeight = SH / scale
cropLeft = cropRight = (W - visibleWidth) / 2
cropTop = cropBottom = (H - visibleHeight) / 2
```

例如 1920×1080 游戏铺满本机 1920×1200 屏幕，倍率约 1.111。保留中央 1728×1080，源画面左右各裁 96 像素，再放大到 1920×1200；等价于放大完整画面到约 2133×1200 后，输出左右各裁约 107 像素。人物比例不变，左右边缘 HUD、按钮或文字可能随画面被裁掉。4:3 内容铺满 16:10 则主要裁上下。

Magpie v0.12.1 的 [SrcTracker.cpp](https://github.com/Blinue/Magpie/blob/v0.12.1/src/Magpie.Core/SrcTracker.cpp) 直接支持源区域的四边像素裁剪；[Profile.h](https://github.com/Blinue/Magpie/blob/v0.12.1/src/Magpie/Profile.h) 保存裁剪开关和参数。上述计算可映射到已有裁剪能力，再进行等比放大。这是根据源码做出的实现推断，仍需用真实游戏验证。

不能简单使用 Magpie 的 `Fill` 枚举冒充 cover：[EffectDrawer.cpp](https://github.com/Blinue/Magpie/blob/v0.12.1/src/Magpie.Core/EffectDrawer.cpp) 中 `Fill` 直接取目标宽高，会改变输入比例。另外 [Renderer.cpp](https://github.com/Blinue/Magpie/blob/v0.12.1/src/Magpie.Core/Renderer.cpp) 会将超过目标尺寸的最终纹理缩回屏幕，因此单纯提高倍率也无法保证裁剪铺满。**先裁源区域，再缩放** 能使用原版的现有能力。

游戏自己绘制在画面里的黑边属于捕获内容，窗口边界不等于有效画面边界。普通等比铺满不保证自动去掉这类黑边；首版应允许少量手动裁剪修正，不承诺自动识别所有黑边。

## 锐化能力和集成接口

截至调研，官方 [最新稳定版为 v0.12.1](https://github.com/Blinue/Magpie/releases/tag/v0.12.1)，优先固定该版本。稳定版 [内置效果说明](https://github.com/Blinue/Magpie/blob/v0.12.1/docs/Built-in%20effects.md) 列出 CAS、FSR_RCAS、NVSharpen、AdaptiveSharpen 等锐化效果及可调参数，支持只锐化而不改变图片尺寸；无需重新编写锐化算法。首版可只选一个轻量效果，避免再次堆出一组用户不需要的算法选项。

官方 [程序交互说明](https://github.com/Blinue/Magpie/wiki/%E4%BB%A5%E7%BC%96%E7%A8%8B%E6%96%B9%E5%BC%8F%E4%B8%8E-Magpie-%E4%BA%A4%E4%BA%92) 提供缩放开始、结束、前后台状态通知，固定输出窗口类名，以及源窗口和源／目标矩形属性，适合工具箱显示“实际生效”状态。它不是完整的控制 API，文档没有给出任意目标窗口启停或实时修改锐度的命令。

稳定版源码进一步确认：

- [ScalingService.cpp](https://github.com/Blinue/Magpie/blob/v0.12.1/src/Magpie/ScalingService.cpp) 通过热键缩放前台窗口，并支持按游戏配置自动缩放。
- [main.cpp](https://github.com/Blinue/Magpie/blob/v0.12.1/src/Magpie/main.cpp) 与 [App.cpp](https://github.com/Blinue/Magpie/blob/v0.12.1/src/Magpie/App.cpp) 没有提供完整的 `start/stop/set-sharpness` 命令行参数；`-t` 是托盘启动行为，不是目标选择参数。
- [AppSettings.cpp](https://github.com/Blinue/Magpie/blob/v0.12.1/src/Magpie/AppSettings.cpp) 在初始化时读取 JSON 配置。仅修改配置文件不能视为正在运行的实例已重新加载参数。
- [ShortcutService.cpp](https://github.com/Blinue/Magpie/blob/v0.12.1/src/Magpie/ShortcutService.cpp) 可以接收热键消息，但直接向其内部消息窗口发送命令属于版本相关的适配，不能当成上游承诺不变的官方 API。

建议分两步：先固定官方稳定版，建立独立的工具箱配置和可回读的启停控制；配置变化时仅在明确“应用”操作中安全重启或重新启动缩放。随后视实际体验决定是否为 Magpie 增加一个小型控制接口，以支持指定 HWND、幂等启停、更新裁剪／锐度并返回实际状态。不要用重复盲发切换热键来伪装启停开关，也不要每次打开或关闭插件选项就重新应用。

Magpie 是外部程序，能够做到用户在工具箱里操作、Magpie 在后台处理；不需要把它的桌面设置窗口塞进 Steam 侧栏。项目采用 [GPLv3](https://github.com/Blinue/Magpie/blob/v0.12.1/LICENSE)；如以后分发 Magpie 或适配后的版本，交付中应保留许可、上游来源及对应源码。

## 建议首版与验证边界

工具箱只保留“等比铺满”“Magpie 锐化”“锐度”及明确的应用／恢复操作。两个开关独立：只铺满、只锐化、两者同时开启，都使用同一画面管线；关闭后恢复原游戏画面。多候选游戏时才让用户选窗口，安装和打开面板不自动抓取其他程序。

开发重点应放在这些实际行为：

1. 正确选择游戏窗口，排除 Steam、工具箱和 Magpie 自身；使用当前目标显示器及实际像素尺寸，处理 SD 卡游戏路径和 DPI。
2. 配置应用前后回读源窗口、捕获／输出区域和运行状态，失败时显示原因，而不是把“设置已保存”写成“已生效”。
3. 确保打开 Steam 快设栏与主菜单、返回游戏、游戏退出、改分辨率和插拔外屏时交接正确。复用现有菜单回调，不覆盖 Steam 的独占回调槽。
4. 核对手柄、鼠标和触屏坐标；不能把游戏源区域裁剪后仍用完整原窗口坐标点击。
5. 先验证窗口模式。无边框、独占全屏、HDR 及不同捕获方式分别核对，不承诺所有游戏直接可用。记录 GPU 开销、帧率与延迟，而不是只确认设置写入成功。

详细检查计划属于后续开发；本轮没有进行 Magpie 游戏或性能测试。旧实现和测试资料保留为历史，新安装包不再包含旧画面处理后台。
