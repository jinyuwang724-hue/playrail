# 游戏库收起到托盘

## 行为

Claw 键收起、窗口的普通最小化请求，在柔焦退场结束后隐藏窗口，保留系统托盘图标。隐藏窗口不占任务栏或 Alt+Tab 列表，进程及页面状态保留。再次按 Claw 键，或双击托盘图标，恢复同一窗口、原页面和位置，并取得前台焦点。

保持 Folia 动效的 520ms 入场、360ms 退场，以及退场中再次请求时的反向切换。收起后取消置顶，返回原来窗口；托盘状态屏蔽游戏库手柄输入，暂停原有后台刷新。关闭按钮继续遵守原有「关闭窗口后留在托盘」设置，托盘菜单的「退出游戏库」仍然退出。

## 实现

- `MainWindow.Shortcut.cs`：动画完成后调用统一 `CompleteShortcutHide`；确认托盘图标创建成功再 `AppWindow.Hide()`，随后返回原窗口。托盘不可用时保留普通最小化作为恢复入口，且不保留托盘输入阻止标记。
- `App.cs`：已有后台运行入口复用统一收起方法，清理正在进行的柔焦动画，避免旧动画完成回调改变新状态。
- `Data/LibraryWindow.cs`：拆出返回前台窗口的操作；拦截本窗口的 `SC_MINIMIZE` 请求，同样走动画收起。
- 使用已有 `LibraryTray`，不增加重复图标或额外托盘进程。现有按键后台已支持查找隐藏窗口，无需更改 Router 或映射模块。

## 构建与保留

基于已安装 0.1.114.0 的完全匹配源码，完整构建目录为 `artifacts/tray-hide/src/frontend/library-native`。0.1.115.0 为过程验证包，最终包为 **0.1.116.0**。

- 准备：`tools/Prepare-TrayHide.py`
- 安装：`tools/Install-TrayHide.py`
- 构建日志：`artifacts/tray-hide/build-116.log`
- 来源和版本：`artifacts/tray-hide/integration.json`
- 安装校验、备份和 EXE 哈希：`artifacts/tray-hide/deployment.json`
- 回退包：安装报告的备份目录中保留原 0.1.114.0 签名 MSIX。

安装只更新游戏库 UI；校验用户 INI、按键/功耗配置、Router、按键模块及 Agent PID 原样保留。主工作区只同步上述三个文件的相关变更，未回退其他任务的功能。

## 验证入口

`tools/Verify-TrayHide.py` 经由现有按键映射 API 测试切换，并记录真实 HWND 状态和 `Shell_NotifyIconGetRect` 结果。`system-minimize` 和 `tray-open` 针对同一已校验游戏库窗口测试系统命令 / 托盘消息处理入口；它们不代表实际手动点击托盘。

记录文件：`artifacts/tray-hide/live-verification.jsonl`、`reversal-verification.json`。Computer Use 用于检查恢复后的实际画面。物理 Claw 键监听链路没有在本次更新中修改。

最终 0.1.116.0 的验证结果：

- 同一 PID 992 / HWND 2688762：收起后 `visible=false`、`iconic=false`、`topmost=false`，前台返回原窗口。
- 隐藏期间 `Shell_NotifyIconGetRect=0`，托盘条目仍存在。Claw 恢复后同一窗口 `visible=true`、`foregroundLibrary=true`、`topmost=true`、`cloaked=0`。
- 普通最小化请求、托盘双击消息入口均完成同样的隐藏 / 恢复；实际恢复画面见 `artifacts/tray-hide/qa-shots/restored-116.png`。
- 相隔 100ms 连续发送两次现有切换动作，退场反向回入场，最终窗口可见且前台，没有被旧完成回调隐藏。此项测试绕过硬件防抖，用于覆盖动画中途的消息状态。
- 最后留在托盘，Agent PID 25468、Router PID 22328 原样保留。最终安装 EXE SHA256：`9aca1c5f654af3cc72790c0a8e9e07e76c2313935c9c3b4f2dcac3605c7d4fa2`。

另有安装后首次协议冷启动的观察：系统曾报告 `visible=true`、`foregroundLibrary=true` 但 `cloaked=2`；完成一次收起 / 恢复后为 `cloaked=0` 且实际画面可见。本次未将首启的 Shell 遮蔽行为作为已解决事项；上述验证结论针对托盘收起 / 恢复路径。
