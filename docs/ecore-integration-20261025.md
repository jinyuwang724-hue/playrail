# 模拟全核心 E 核接入记录

2026-10-25。本功能已经接入工具箱，开启、关闭和原值恢复均通过本机实际写入与回读验证。入口为游戏库的「控制中心 → 性能 → CPU → 模拟全核心 E 核」，Game Bar 原 E 核磁贴也使用同一后台。

## 安装包证据与行为

用户提供的 `MotionAssistant_Setup_1211.msi` SHA-256 为 `2f91522dc1aa3d9a920b6e5649d2c6cc7ee5ab469af4c6ec0c074eb2894b143d`，与此前离线提取的包一致。提取出的 `MotionAssistant.exe` SHA-256 为 `8c2d07f1a52230f5e2cc993f731121e7b72155d1f97ec0f3b7ddc71db4e85339`。静态检查入口为 `Form1.AllECoreMaskCheckBox_Click`，调用 `ProcessorMaskTool.SetAllCoresAsSmall` 或 `DeleteSmallCoreSetting`。

原程序开启时计算 `(1 << 逻辑线程数) - 1`，写入：

```text
HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\Kgroups\00
SmallProcessorMask
```

关闭时删除该值，程序自身提示修改后重启。原程序使用 BigInteger 的有符号十六进制字符串长度选类型，因此 32 个逻辑线程时即使用 QWORD；本实现保留这一行为。本机为 14 线程，对应 DWORD `0x3FFF`。

这是 Windows 保存的核心分类覆盖设置，涉及整机。物理核心类型不随之改变，退出游戏和退出工具箱也不自动恢复。未实测重启后的调度变化、功耗或帧率收益。

反编译原文和 MSI 提取映射位于被 Git 忽略的 `diagnostics/motion-research-20261023/`。没有执行安装器或调用反编译程序的方法；项目源码独立实现已观察到的设置协议，不依赖 MotionAssistant 进程或二进制文件。

## 工具箱实现

- `native/ClawSmallCores.cs`：明确使用 64 位注册表视图，校验逻辑线程范围与单处理器分组，写入后重新打开键核对数值及类型。关闭缺失设置时不创建注册表键。后台严格拒绝非布尔开关。
- `plugin/claw_power.py`：精确检查全位掩码；部分掩码、零值或无效类型不再因“值存在”就显示全核心开启。首次修改前持久保存原值，备份包含原先是否存在、DWORD/QWORD 类型和值。
- `restore_ecore`：单独恢复首次修改前的设置，与关闭开关时删除值的行为分开。备份文件为 `%LOCALAPPDATA%\ClawToolbox\data\ecore-backup.json`。本机原值已经是 DWORD `0x3FFF`，所以恢复原始设置会恢复为开启。
- 按用户后续要求，删除首次观察已有设置时的“生效状态未知”提示，不为没有重启记录的情况附加状态文字。内部继续保留重启记录；本次启动中观察到值变化时仍显示“重启后生效”。应用重开不清除待重启提示。
- 核心设置独立于功耗接管和逐游戏策略。无权限、读写失败、备份失败或多处理器分组时返回错误，界面不显示保存成功。

处理器分组数量使用微软的 [GetActiveProcessorCount](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getactiveprocessorcount) 等接口。核心效率类别的公开定义见 [PROCESSOR_RELATIONSHIP](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-processor_relationship)。启动标识查询使用 [phnt 原始头文件](https://github.com/winsiderss/phnt/blob/master/ntexapi.h) 中的只读 `SystemBootEnvironmentInformation`；此接口不可用时不据此推断已经重启。

## 本机更新与保留项

安装版本：游戏库 `0.1.106.0`、Game Bar `0.4.41.0`、后台 `0.17.58` / ClawPower `r9`。

工作区的 UI 和后台源码比本机正在运行的版本旧。本次以与已安装模块哈希完全相同的 `native-gamebar-osd` 工作树作为集成基线，仅移植核心设置相关函数和界面行，没有直接覆盖已安装的新版性能显示实现。原工作树未修改。完整可重建的集成源码、差异和签名包保存在 `artifacts/ecore/integration/`；准备工具为 `tools/Prepare-EcoreIntegration.py`。该目录中的 `integration.json` 记录原文件及候选文件的哈希。

更新通过原位升级包和正常后台退出完成。功耗、OSD、风扇配置保留。模块及旧版签名包备份目录见 `artifacts/ecore/deployment.json`；本次为 `%LOCALAPPDATA%\ClawToolbox\repair-backups\ecore-20261025-161837`。后台部署脚本校验原文件哈希，匹配失败时拒绝覆盖。UI 升级保留 LocalState，失败时使用备份包回滚。

## 验证结果与限制

- `tests/SmallCoresTests.cs` 通过：1/8/14/31/32/33/63/64 线程掩码与原程序类型规则；HKCU 独立测试键中的 DWORD/QWORD 写入、64 位无符号值回读、部分掩码原类型恢复和关闭删除。没有用单元测试修改系统核心键。
- `tests/ecore_backend_test.py` 七项通过，分别在当前工作区及实际集成源码上执行：备份、开启、关闭、恢复、应用重开、观察重启、外部改动、无启动标识、无权限、存储失败、类型和参数错误。测试需要包含 unittest 的完整 Python；裁剪后的插件运行时未打包 unittest。
- `tests/power.test.mjs` 五项通过，覆盖失败反馈、独立恢复接口、未知状态与不支持设备的禁用逻辑。
- 两个版本的原生功耗后台、游戏库和 Game Bar 均编译成功；集成版 UI 完成签名打包。
- `tools/Verify-Ecore.py` 经安装版后台实际执行关闭、开启、恢复：关闭删除成功，开启写入 DWORD `0x3FFF` 成功，错误参数被拒绝，原始值及类型完全恢复。功耗配置文件字节未改变。详细结果为 `artifacts/ecore/live-verification.json`。

未执行系统重启。最终保留测试前的 DWORD `0x3FFF`。后续按用户要求删除“生效状态未知”提示；开关保持开启。提示修正直接更新当前安装版的对应模块，保留后续升级的后台与 UI 版本，不重新部署本节最初安装的旧包。修正记录位于 `artifacts/ecore/status-cleanup/deployment.json`。

旧总测试并非全部通过：`tests/toolbox-ui.test.mjs` 有三项既有功耗摘要/风扇期望失败；使用改动前的模块与原始测试可复现相同三项失败，记录为 `diagnostics/ecore-20261025/baseline-test.log`。`tests/power_backend_test.py` 在原有风扇时间序列断言处失败，尚未执行到核心设置部分；本次没有修改风扇算法或为通过该旧断言修改其他功能。上述结果不计入本功能专项测试通过的结论。
