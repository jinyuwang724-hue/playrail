# SteamOS 缩放模式与缩放过滤器：Claw 实现调研

日期：2026-10-08。环境：Windows Claw；调研原始基线 0.8.2，现已实现至工具箱 0.9.0。下文保留来源与设计依据；实际实现范围与验证见 [实现记录](SCALING_IMPLEMENTATION_20261008.md)。

## 最终复刻基准

用户最终指定：

- 缩放模式：自动 → 整数 → 适应 → 拉伸 → 填充。
- 缩放过滤器：线性 → 最近 → FSR → NIS。

这份明确选项覆盖此前“按当前稳定版”的笼统基准。近期 Steam 界面存在 Pixel／Sharp 等版本差异，本项目不据此替换用户指定的四种过滤器。“最近”按最近邻采样实现，界面沿用用户给出的名称。

最高优先级只包含这两项。FSR／NIS 的锐度为过滤器从属设置，承接此前锐化需求，不单独创建第三个功能模块。功耗、RTSS、全核心模拟 E 核保留需求，当前暂缓。

## 可行性结论

可以实现同样的选项、几何规则与实际空间滤波算法。Gamescope 上游包含五种模式及 LINEAR、NEAREST、FSR、NIS 等过滤器。[Valve 参数类型](https://github.com/ValveSoftware/gamescope/blob/master/src/main.hpp)。

Windows 需要另外实现捕获、GPU 处理与画面输出。Gamescope 现有路径依赖 Wayland／Xwayland、Vulkan 与 Linux 显示设施，Windows Steam 仅打开隐藏控件不会获得对应后端。[Valve Gamescope](https://github.com/ValveSoftware/gamescope)。

目标是功能及画面行为一致；当前没有证据保证 Windows 捕获／DWM 输出与 SteamOS 合成器的延迟、帧同步、HDR 及全部游戏兼容性完全一致。需要实际游戏验证，不能仅凭相同算法名宣布一比一完成。

小黄鸭及 Lossless.dll 明确排除。此前优先复用它们的方案取消，也不通过 Playhub 小黄鸭接口间接实现。

## 缩放模式：画面如何占据屏幕

| 选项 | 目标行为 |
|---|---|
| 自动 | 保持宽高比，按 Steam 界面说明限制自动放大至最多 2 倍，画面居中；极小窗口仍可能有黑边 |
| 整数 | 放大时选择屏幕容纳得下的最大整数倍，保持比例并居中；整数是尺寸规则，不能替换过滤器 |
| 适应 | 保持宽高比，尽量占满屏幕内的区域，允许黑边，不裁掉内容 |
| 拉伸 | 横向、纵向分别铺满屏幕，允许改变画面比例 |
| 填充 | 保持宽高比，覆盖整个屏幕，居中裁掉超出部分 |

基本几何：有效游戏画面为 Ws × Hs，输出为 Wo × Ho。适应倍率为 min(Wo/Ws, Ho/Hs)，填充倍率为 max(Wo/Ws, Ho/Hs)，拉伸分别使用横纵倍率。边框、标题栏、阴影不算输入；输出按显示像素计算，避免 Windows DPI 干扰。

上游 calc_scale_factor_scaler 仅在倍率大于 1 时对整数模式取整；大于屏幕的输入不能简单 floor 后得到 0。实际计算还包含嵌套输出及全局倍率。自动的 2 倍限制来自 Steam 界面说明，而上游 Gamescope 的程序默认上限为 FLT_MAX，需要会话配置提供；不能照搬命令行默认值。[几何计算](https://github.com/ValveSoftware/gamescope/blob/master/src/steamcompmgr.cpp)、[默认值与启动参数](https://github.com/ValveSoftware/gamescope/blob/master/src/main.cpp)。

示例：800 × 600 输入、1920 × 1200 输出。自动、整数与适应可得 1600 × 1200，左右留黑边；拉伸为 1920 × 1200；填充为 1920 × 1440，垂直裁剪。自动与适应的区别还需用 320 × 240 等很小输入核对。

## 缩放过滤器：如何生成放大画面

| 选项 | 对应算法 | 需要匹配的行为 |
|---|---|---|
| 线性 | 双线性插值 | 平滑采样，放大后边缘可能更柔和 |
| 最近 | 最近邻采样 | 取相邻源像素，非整数倍率可能有不均匀像素块；不暗换为 Pixel 的带限重建 |
| FSR | FSR 1 空间放大与锐化路径 | EASU 放大、RCAS 锐化；不能用普通锐化或游戏内 FSR 2／3 冒充 |
| NIS | NVIDIA Image Scaling | NVScaler 包含锐化，不额外叠加 NVSharpen 造成双重锐化 |

FSR 1 的公开实现包含 EASU 与 RCAS，可作为 Windows shader 的依据。它处理当前帧图像，无需游戏提供运动向量，也不产生补帧。[AMD FSR 1 源码](https://github.com/GPUOpen-Effects/FidelityFX-FSR/blob/master/ffx-fsr/ffx_fsr1.h)。

NIS SDK 提供 HLSL／GLSL、DX11／DX12／Vulkan 集成说明及 Intel GPU 的适配配置，所以本机 Intel Arc 不会仅因不是 NVIDIA 显卡而被排除。仍需正确处理输入颜色空间、采样范围和锐化参数；存在适配路径不证明本机已通过测试。[NVIDIA NIS 官方实现](https://github.com/NVIDIAGameWorks/NVIDIAImageScaling)。

模式与过滤器独立，共 5 × 4 = 20 种设置组合。整数＋线性仍是整数倍尺寸的线性采样；整数＋最近才获得对应像素采样。遇到输入尺寸或后端限制而回退时，应说明真实生效方式。

## 插件交互

在当前可扩展布局中增加“画面缩放”模块，先模式，再过滤器；各用一条手柄可左右调整的离散滑杆。保持指定顺序与名称，不加入 LS1、SGSR、Anime4K 或补帧选项。

FSR／NIS 下的锐度控件属于过滤器区域。线性／最近不提供无作用的强度控件。锐度范围、默认值和数值增加方向需针对用户这套界面核对，不能拿小黄鸭 0–10、当前 Steam 的动态限制或 Gamescope 引擎数值直接代替。

打开、展开、未修改退出只读取。实际改值才应用，滑动可合并频繁请求，随后回读；失败保留真实状态。已保存、正在运行、未达到放大条件分别呈现，避免整组执行动画。

流程留在插件内，不要求打开外部缩放软件。后台预留当前游戏／默认配置作用域，协调 Steam 活动游戏识别，避免将上一游戏设置误用到下一游戏；独立逐游戏配置管理页不是本轮新增范围。

## Windows 后端建议

建议插件调用独立的原生缩放 helper，按以下链路处理：

1. 识别游戏实际画面窗口与有效客户区，支持启动器交接主进程。
2. 使用窗口捕获取得 GPU 帧，避免整个桌面捕获递归包含自身输出。Windows Graphics Capture 的 CreateForWindow 可绑定指定窗口。[Microsoft 窗口捕获接口](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)。
3. GPU 上计算尺寸与裁剪，使用线性／最近采样或官方 FSR／NIS shader，输出到目标屏幕。
4. 保持控制器输入，处理鼠标／触摸坐标逆变换、光标、窗口尺寸和显示器变化。
5. 与 Steam／Playhub 侧栏和焦点交接。上游在 Steam 获得焦点时使用不同设置；游戏的裁剪与拉伸不应直接套到菜单上。[Valve 焦点与缩放设置](https://github.com/ValveSoftware/gamescope/blob/master/src/main.hpp)。
6. 游戏退出、捕获失败或 helper 异常时释放输出，恢复原窗口操作和可用的 Steam 菜单。

这是新的画面处理后端，工作量超过纯前端控件。本机 Windows 11 与 Intel Arc 提供适配基础，但尚未创建捕获会话、编译 shader 或测量帧率。使用官方算法时保留对应许可：AMD／NVIDIA SDK 为 MIT，Gamescope 主仓库为 BSD 2-Clause。

原生尺寸、缩小输入、独占全屏、HDR、受限捕获、RTSS 已进入游戏画面的 OSD 和 Steam 覆盖层均需明确真实兼容情况。已被其他软件放大的输出不能当作正常源画面重复处理。

## 后续开发及验收

内部顺序：真实游戏捕获与输出交接 → 五种模式 → 四种过滤器 → 插件交互与状态回读。这不代表第一版只交付部分选项。

完整验收范围：

- 恰有指定的 5 种模式与 4 种过滤器，可独立选择并保留，覆盖 20 种设置组合。
- 用 4:3、16:9、16:10 和很小输入核对黑边、整数倍率、自动上限、拉伸与居中裁剪。
- 用像素网格、细字、边缘与真实游戏核对四种过滤器；确认无重复锐化。
- 核对 FSR／NIS 的有效尺寸和锐度映射，标明未发生放大。后处理不会自动降低游戏渲染分辨率，不能承诺选择即增加帧数。
- Steam 快设栏、主菜单、返回游戏、手柄焦点正常，没有递归画面、菜单被裁剪或退出后残留输出。
- 旧 Galgame 与常见 3D 游戏分别核对帧时间、额外 GPU 占用与输入延迟；现有 RTSS 仅检查共存，不开发新监控功能。
- 未修改打开／退出不重新应用；失败不显示成功，退出游戏可以恢复。

## 证据与尚待确认

本机 Valve 资源可证明控件及状态读写代码存在，但 package/beta 为 publicbeta，不是目标稳定版的实机证据。本地当前为五种模式、三种过滤器；最终需求仍以用户指定的 5＋4 为准。这里只静态读取，没有调用 SteamClient.System.Perf.UpdateSettings。

Valve 发布记录用来核对分支，Gamescope 上游用于机制研究；master 不等于某个稳定版发布包。记录见 [本机 UI 证据](../../../diagnostics/steam-scaling-local-ui.json)、[发布记录摘录](../../../diagnostics/steam-scaling-stable-news.json)、[上游核对记录](../../../diagnostics/steam-scaling-upstream-sources.json)。

后续开发前仍需确定：用户这套界面的锐度范围与默认值、精确 shader 版本及颜色／采样映射；随后在本机验证窗口捕获、菜单交接、独占全屏、HDR 与性能开销。本轮没有实现、安装、升级或功能测试。
