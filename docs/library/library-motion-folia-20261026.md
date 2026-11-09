# Claw 唤出 / 收起：Folia 柔焦动效

2026-10-26，正式安装游戏库 **0.1.114.0**。本次仅更新游戏库 UI，按键监听后台及用户设置保持原样。

## 参考与选择

检查了 Folia 提交 `aecd330b720eb8fc728e6b10ebcec261b144d683` 的两套现成动效：

- [`overlayEntranceMotion.ts`](https://github.com/chthollyphile/folia-major/blob/aecd330b720eb8fc728e6b10ebcec261b144d683/src/components/shared/overlayEntranceMotion.ts)：280ms，轻微上移 / 缩放，适合面板。
- [`softFocus.ts`](https://github.com/chthollyphile/folia-major/blob/aecd330b720eb8fc728e6b10ebcec261b144d683/src/components/visualizer/styleTransition/softFocus.ts)：柔焦、轻微放大和透明度过渡。选择这一套作为全屏游戏库的参考，在原生合成层独立实现。

Folia 页面切换用的 80ms 入场等待没有移入唤出流程，避免按键后先等待一段黑场。没有复制 React / Web Animation 实现或添加网页运行时。

## 最终行为

| 动作 | 时长 | 变化 | 缓动 |
| --- | --- | --- | --- |
| 唤出 | 520ms | 1.02 → 1 倍；8 → 0 柔焦；透明 → 清晰 | cubic-bezier(0.16,1,0.3,1) |
| 收起 | 360ms | 1 → 1.02 倍；0 → 8 柔焦；逐渐淡出 | cubic-bezier(0.4,0,1,1) |

尺度始终不小于全屏尺寸，避免旧缩小动画露出黑色边缘。游戏库已打开但位于其他窗口下面时，唤到前台也会播放入场。快速再按从当前动画位置反向继续，不等待旧动画结束，也不让旧完成回调将新唤出的窗口最小化。

页面、滚动位置和现有输入路径保留。手柄在唤出取得焦点后可继续导航。降低动态效果或系统关闭动画时直接切换。

模糊仅在动画期间给原有合成树套临时 LayerVisual；结束后移回同一棵视觉树、移除效果并释放该次效果画刷，避免持续模糊和持续增加离屏合成。覆盖游戏库、控制中心及设置等合成内容，XAML 搜索控件跟随整体透明度和缩放。

实现注意：WinUI 的旧父节点需显式移除后才能重新挂接视觉树；临时接管开始即记录状态，失败也恢复原树。NativeAOT 向 WinRT 传递动态属性列表使用显式字符串数组。LayerVisual 的隐式内容输入画刷不能在解绑后再次绑定，因此只缓存效果工厂，画刷每次创建、结束释放。处理中途反向时保留当前画刷和动画值。

## 来源、安装和保留

本机此前已安装 0.1.111.0（包括睡眠动画设置）。使用与其 EXE SHA256 完全一致的 `artifacts/sleep-animation/settings-v2/integration-r2/` UI 源码为基线，不回退到旧主工作区项目版本。

- 完整可构建源码：`artifacts/folia-motion/src/frontend/library-native/`。
- 主工作区同步修改：`MainWindow.Shortcut.cs`、新增 `Stage/WallStage.ShortcutMotion.cs`，以及 Stage.Dispose 的资源释放调用。
- 来源及版本：`artifacts/folia-motion/integration.json`。
- 最终构建日志：`artifacts/folia-motion/build-114-final.log`。
- 准备 / 安装工具：`tools/Prepare-FoliaMotion.py`、`tools/Install-FoliaMotion.py`。
- 正式包：`artifacts/folia-motion/src/frontend/library-native/AppPackages/ClawLibrary_0.1.114.0_x64_Test/ClawLibrary_0.1.114.0_x64.msix`。
- 安装报告：`artifacts/folia-motion/deployment.json`；安装 EXE SHA256：`DB1089556D8D4286204BE1D358F97BC967566E38AA3D351512628B0885C2000A`。
- 最终更新前备份：`%LOCALAPPDATA%/ClawToolbox/repair-backups/folia-motion-20261026-153323/`，保留原 0.1.111.0 签名包及偏好。

安装核对 UI 偏好、按键 / 功耗配置、Router 和两个按键模块哈希保持原样，Agent PID 25468 始终未重启。未修改系统权限、安装输入驱动，或重启风扇 / 功耗服务。0.1.112 / 0.1.113 为本次过程中的中间验证包，后续应以 0.1.114 源码及包为准。

## 验证

- 在独立缓存的窗口预览中放慢四倍，实际截图确认入场中存在可见模糊、结束后文字和图片恢复清晰，未继续使用普通淡入淡出作为“已完成柔焦”的证据。截图在 `artifacts/folia-motion/qa-shots/enter-mid.png` 和 `settled.png`。预览进程已退出，正式版没有放慢参数。
- 预览反向切换通过：退场尚未结束即再次请求，最终保持显示 / 前台，没有被旧动画完成回调最小化。
- 正式 NativeAOT 包完成冷启动、两轮正常收起 / 恢复；日志显示 enter=520ms、leave=360ms，各轮完成且没有效果错误。收起后的窗口为 iconic=true、topmost=false，前台回到原窗口；恢复后 foregroundLibrary=true、topmost=true。
- 正式包退场开始 100ms 后再次请求收起，转为反向唤出；最终同一 PID 2124 / HWND 723076 保持显示、前台且置顶，旧退场回调未执行。记录为 `artifacts/folia-motion/retarget-verification.json`。
- 正式包已通过 Computer Use 截图确认最终画面完整清晰，见 `qa-shots/installed-settled.png`。DWM 实际 cloaked=0，不只根据 GetForegroundWindow 宣称窗口已显示。

上述动画检查使用既有动作入口，未在本次替用户模拟实体键，也未新增独占全屏游戏兼容性结论。
