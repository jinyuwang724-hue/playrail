# Game Bar 关闭程序目标丢失修复

2026-10-26，Asia/Shanghai。已部署到本机 0.17.58 后台；保留已有 Game Bar 包、OSD、风扇及功耗设置。

## 原因与实现

安装版的 `system.game` 来自 `GameSessions.visible`，关闭接口也依赖 `sessions.game`。该状态用于游戏识别；当前 Game Bar OSD 的直接采样路径不会把候选窗口提升为已确认游戏，非 Steam 游戏或没有有效启动提示的应用因此可能一直无法出现在关闭磁贴上。

任务切换时，系统还会短暂把桌面置为前台，再进入 `explorer.exe` 的 `XamlExplorerHostIslandWindow`。仅过滤 Game Bar 进程无法覆盖这条链路。

新增 `plugin/claw_close_target.py`，以独立的 100 ms 任务跟踪前台应用，不依赖 OSD、PresentMon、游戏分类或功耗接管。Game Bar、Xbox 主页、游戏库和任务切换器不会覆盖应用目标。普通桌面立即隐藏关闭目标；只有在两秒内进入已知任务切换器，才允许恢复短暂经过桌面时保留的目标，避免回到桌面后误用旧应用。

普通应用只要有可访问的进程身份与可见窗口即可成为目标，浏览器也按“关闭程序”的含义处理；系统窗口、工具箱和辅助进程不能成为目标。应用切换会更新目标，退出和 PID 复用会清除旧目标。关闭前重新采样、验证 PID 与创建时间，并在后台关闭请求期间持有同一进程句柄。[Windows 的 PID 生命周期规则](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-process_information)保证该句柄释放前进程号不会被重新分配。

`system_cached()` 每次读取都会刷新目标，不再让较旧的硬件缓存延迟应用切换。后台退出时取消独立采样任务。原有界面二次确认逻辑未修改。

## 安装与验证

主工作区源码比安装版旧，因此没有直接覆盖安装版 `claw_power.py`。使用 `tools/Prepare-CloseTargetRepair.py` 只合并导入、采样任务、缓存和关闭方法，再以 `tools/Deploy-CloseTargetRepair.py` 备份、正常退出并重启后台。部署前后检查设置与版本指针，均保持一致。

最终跟踪模块 SHA256：`4270679aa5ae189b9b223695d90a872db32e76b671f7a429c79ba0a808dbd228`。

首次原始后台备份：`%LOCALAPPDATA%/ClawToolbox/repair-backups/close-target-20261026-023955/`。补充桌面过渡处理后的备份：`close-target-20261026-024203/`。其后本机另一次后台更新保留了本修复的导入、采样、缓存、关闭及退出处理；未用旧候选覆盖该更新。

- `tests/close_target_test.py`：18 项通过，覆盖未分类应用、Xbox/Game Bar 覆盖、反复任务切换、桌面过渡、两个应用切换、进程退出/PID 复用、旧界面状态、关闭期间的句柄和采样退出。
- 先完成桌面模式的真实 Game Bar 名称显示、任务视图往返及生产关闭接口测试。
- 随后进入真正的 Xbox 全屏体验。原生截图中，任务视图底部“XBOX 模式”处于选中状态，显示 LB/RB 与横向任务卡片。停留在该界面时，关闭目标仍是测试应用 PID 26032，记录为 `artifacts/gamebar-close-target-20261026-r3/xbox-task-view.json`。
- 返回应用、重开 Game Bar 后，顶部名称与“关闭程序”磁贴仍显示正确的测试窗口。再以另一实例完成同样流程，通过正式后台 `close_game` 关闭 PID 15236；关闭后目标为空，记录为 `xbox-close-verification.json`。仅关闭自行创建的无用户数据测试应用。
- UI 自动化不能把底层应用的截图坐标点击路由给 Game Bar，点击被工具拒绝。未完成鼠标/实体手柄的两次按键确认测试；不能把生产接口测试写成实体按钮测试。未启动或关闭用户的真实游戏。

补充检查：旧 `tests/power_backend_test.py` 在风扇平滑曲线的既有断言（第 77 行）失败；本次没有改动该策略。此结果不计入上述 18 项针对性回归，也不宣称全套历史测试通过。

复测命令：完整 Python 环境运行 `tests/close_target_test.py`；也可追加安装版 app 目录，验证实际部署文件。MSI/手柄映射、功耗、OSD 外观与游戏识别规则不属于此次改动。
