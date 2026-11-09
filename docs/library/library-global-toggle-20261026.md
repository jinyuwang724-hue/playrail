# Claw 游戏库：游戏中唤出、收起及动画

2026-10-26，本机原位升级游戏库 0.1.107.0 → 0.1.110.0；后台保留 0.17.58，仅更新两个按键模块。0.1.108.0 / 0.1.109.0 为本次验证中的中间版本。

## 原因

旧版本用协议再次启动应用，再对已有 WinUI 窗口调用 Activate。游戏窗口已经占据前台时，这不足以保证窗口层级和实际前台焦点。手柄轮询又只向前台游戏库投递动作，所以可能出现请求已发出、库被游戏挡住且手柄没有进入库的情况。旧切换策略还排除了 Steam 大屏、Xbox 全屏体验以及状态未知的情况。

Windows 对前台激活有独立限制，不能将启动成功当作拿到前台的证明：[SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)。

## 本次行为

- 已运行的游戏库通过注册窗口消息接收动作。先核对窗口标题和包身份，并检查接收标记；无窗口或旧窗口不支持消息时回退到协议启动。
- 捕获按键触发前的窗口句柄；冷启动也通过协议参数传入返回目标。恢复使用原页面，不重新跳回首页。
- 唤出时恢复窗口、设置 TopMost、尝试取得实际前台焦点并聚焦可操作的根元素；单实例转发也向原进程授予前台激活许可。仅做两次短时焦点核验，不持续抢焦点，不修改系统的前台锁设置。
- 再按收起：140ms 淡出及 1→0.985 缩放，合成动画结束后最小化、移除 TopMost、返回先前窗口。核对返回目标仍存在且 PID 相符；用户已切到其他程序时不再抢回目标。
- 唤出：180ms 淡入及 0.985→1 缩放。使用合成线程动画，覆盖游戏库和 XAML 控件；它是游戏库内容的出入场动画，未将窗口改成对游戏画面实时透明的覆盖层。
- 遵循游戏库“降低动态效果”和系统动画设置；关闭动画时直接切换。
- 最小化完成后清理动画状态并恢复隐藏内容的透明度/缩放，允许任务栏、Alt+Tab 或游戏退出后由 Windows 直接恢复窗口；窗口重新激活时恢复已暂停的界面计时器。已最小化窗口即使被 Windows 短暂报告为原前台，也选择保留页面的恢复路由。
- 手柄焦点采样从 250ms 改为 16ms；动作真正执行前再检查前台，避免排队输入在切回游戏后继续操作游戏库。轮询线程使用缓存的 HWND，避免从后台线程读取 WinUI 窗口对象。

当 Claw 的实际配置动作为游戏库/控制中心时，不再因 Steam/Xbox 状态而拒绝切换。**没有重写用户现有两套按键配置，也没有将原 Steam 菜单动作强制改成游戏库。** QS 的原动作及接管开关保留。

## 实际来源和复现

主工作区源码版本落后于本机。已安装 0.1.107.0 的 EXE SHA256 与 `native-gamebar-osd` 工作树的发布产物完全一致，因此使用该完整源码作为构建基线，原工作树未修改。仅将本功能相关改动同步到主工作区；不能用主工作区旧的 0.1.84 项目直接覆盖本机新版。

- 完整集成源码：`artifacts/library-toggle/src/`。
- 基线和后台哈希：`artifacts/library-toggle/integration.json`。
- 最终构建日志、签名 MSIX 和发布目录：`artifacts/library-toggle/build-110.log`、`artifacts/library-toggle/src/frontend/library-native/`。
- 准备工具：`tools/Prepare-LibraryToggle.py`，已生成目录时拒绝覆盖。
- 构建：在集成目录执行 `frontend/library-native/tools/Build-Msix.ps1`。
- 部署：`tools/Deploy-LibraryToggle.py`，核对已安装后台的原哈希，备份后原位更新包，只替换 `claw_service.py`、`claw_button_mappings.py`，使用原有后台正常退出/启动流程。
- 部署报告：`artifacts/library-toggle/deployment.json`；初始旧包和后台备份在 `%LOCALAPPDATA%/ClawToolbox/repair-backups/library-toggle-20261026-024426/`，最终更新前备份在 `library-toggle-20261026-030033/`。

最终 EXE SHA256：`A39D3382E78C2E404E65296F5D4149A1ED1A10BDFDD82783EF46DA2B569C0EC8`。部署时核对按键、功耗和发布配置字节保持原样。没有部署风扇服务、输入驱动、Game Bar 或功耗助手。

## 验证及范围

- 按键映射 19 项通过，包括所有场景下的配置路由、先捕获原窗口、64 位 HWND、直接通知、旧接收器回退，以及最小化后旧前台句柄的恢复选择。
- 窗口策略 15 项通过，包括未知全屏状态、冷启动返回目标、非法句柄参数及页面保留。
- `tests/LibraryShortcutInput` 对实际集成版轮询器执行 2167 个断言，涵盖输入源选择、A/B/方向投递、后台停止投递、已排队动作丢焦后拒绝执行，以及恢复前台后不再等待 250ms。
- 本机以 `tests/LibraryToggleGame.cs` 创建全屏 TopMost 测试窗口，经现有 `test_button_mapping` 入口执行与实体键相同的已配置动作：库成为前台、TopMost=true；再次触发后 iconic=true、TopMost=false，前台回到同一个测试 HWND。截图检查库正常显示。
- 控制中心内收起/恢复后，截图确认仍为控制中心。同一游戏库进程持续运行。
- 全屏测试窗口与同一游戏库实例连续切换六次（恢复/收起各三次），每次库均取得前台，收起均回到同一个测试 HWND。采样记录恢复前台约 31ms，含动画的最小化约 172–203ms；这些是窗口状态的观测时间，不是逐帧动画延迟测量。
- 最终 0.1.110.0 另从全屏 TopMost 测试窗口冷启动，确认库 TopMost=true 且实际取得前台，再次收起回到同一个测试 HWND；普通窗口与库连续切换六次也通过。冷启动时将短时的层级核验与焦点核验一并执行，覆盖初始全屏呈现器改写层级的情况。
- 实机数据在 `artifacts/library-toggle/live-verification.jsonl`；库日志有实际前台 HWND、返回目标和动画完成后的最小化记录。正常检查后已关闭测试窗口。

以上实际窗口检查没有模拟物理按压，也没有代替用户在具体游戏内实按。没有实际验证 Xbox 全屏体验或旧式独占全屏游戏。独占全屏可能因切换前台而短暂黑屏/切换显示模式，普通桌面窗口不能保证像游戏内注入式菜单一样覆盖所有独占显示路径；微软的模式说明见 [Fullscreen Optimizations](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)。

本次保证游戏库取得焦点后使用现有手柄输入路径，没有增加对第三方游戏的输入注入或拦截；对失焦后仍主动读取 XInput 的游戏，不能据此声称已经完成手柄输入隔离。

## 10:27 实体键失效修复及用户复测

用户随后报告实体 Claw 键完全不能唤出。诊断确认游戏库窗口接收动作正常，但服务状态为“按键后台已退出”，实际留有 2026-10-25 22:30 启动的旧 Router PID 18320；所属 Python PID 22964 已不存在。该进程仍占用单实例互斥锁，新 Agent 的监听子进程因此退出。此前窗口测试通过 `test_button_mapping` 直接执行动作，未证明实体按键监听链路正常，这一验证遗漏已补齐。

处理：

- 核对旧进程完整路径、创建时间及所属进程消失，备份恢复日志与配置。请求正常退出后等待超过 20 秒仍未退出；再次核对没有恢复子程序或 ClawTweaks helper，结束这个精确识别的旧进程。只剩的 conhost 是旧控制台宿主，不是恢复子程序。
- Router 增加 `--owner-pid`，持有所属 Agent 的进程句柄；Agent 退出后，向 Router 已创建的主消息队列发送 WM_QUIT，经过正常 Dispose 释放监听和互斥锁。标准输入 EOF 也改为投递 WM_QUIT，避免消息循环尚未开始时 Application.Exit 丢失。
- 服务启动时向 Router 传入自身 PID，对尚未释放的互斥锁做有限重试；已经退出的子进程不再被当成启动成功。其他启动错误明确报出。
- 不更新游戏库 MSIX、Game Bar、功耗助手或风扇服务，按键和功耗配置字节保持原样。

修复通过 `tools/Repair-RouterLifetime.py` 部署，报告及完整候选源码保存在 `artifacts/library-toggle/router-recovery/`；备份为 `%LOCALAPPDATA%/ClawToolbox/repair-backups/router-lifetime-20261026-102650/`。构建使用与已安装 Router 哈希一致的 `gamebar-mode-toggle/artifacts/gamebar-stability` 基线，对应辅助源码一起保留。当前 Router 哈希 `A24C4BEF5B663ADA7AC2CA478D85B222709A0EBE9D2FF704B20883AC504581D7`，服务哈希 `6A096A1734D74E1BAB56162211DD7F614F1178C25262F393ACA264A2842B76E3`。

验证：实际原生进程测试 3 项通过（所属后台退出且 stdin 仍打开、正常关闭 stdin、拒绝无效 owner）；启动流程 3 项通过（互斥锁交接重试、重试次数有界、拒绝已退出子进程）；按键映射 19 项通过。部署后监听状态为 configured/canObserve/released=true、error=null，当前唯一 Router 为 PID 9100，所属 PID 25460 与 Agent 一致。

**用户已实按确认“可以唤出，也能收起”。** 后台收到连续真实 `MSI_Event`，rawCode=2228265，最近映射结果为 Claw/general/minimize-library/ok=true。记录为 `artifacts/library-toggle/router-recovery/physical-verification.json`。此结论补充并取代上文“实体键尚待复测”的历史验证范围；具体独占全屏游戏仍没有在本次复测。
