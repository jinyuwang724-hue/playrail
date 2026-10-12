# 游戏运行状态、桌面按键和退出卡顿修复

日期：2026-10-08，最终插件版本 0.8.4。

## 已处理的原因与改动

1. **游戏退出后大屏幕偶发不刷新**：补上运行状态变化监听。原 Playhub 主窗口菜单路径持有的焦点请求在游戏退出后立即释放，关闭本插件打开的菜单；全部游戏退出时，在当前状态事务结束后发送一次布局刷新。实机发现隐藏窗口的下一帧回调会延后约一分钟，最终改用 SharedJSContext 的 microtask，不依赖隐藏窗口绘制。没有替换 Steam 独占覆盖回调，没有空闲定时扫描，也不会强行切走正在使用的窗口。用户原始卡顿本轮没有重现，因此这是针对焦点残留和返回刷新缺失的修补，不能视为已证明所有卡顿原因消失。

2. **绝区零、鸣潮运行状态丢失**：本次检查发现 Steam 是普通权限，两个游戏需要管理员权限；Playhub 的直接启动没有落实原先的管理员兼容选项。新增具有管理员清单的 SteamAdminLauncher，在 Gaming 自定义启动应用首位启动 Steam，后续 Playhub 复用此会话。已实际确认 Steam 和游戏覆盖进程均为 Elevated。

   旧错误文件 `C:\Game\ZenlessZoneZero Game\steam_appid.txt` 和 `C:\Game\Wuthering Waves Game\steam_appid.txt` 已移动到本轮备份，保留原字节；本轮正确启动后未重新出现。Steam 日志由错误的 11857803／14637647 改为完整非 Steam GameID，快捷方式 AppID、封面、分类、鸣潮启动参数均未改动。

   Steam 重启时安装了原先已下载的 Beta 更新，当前客户端构建为 1791415817。新客户端把原 `~ RUNASADMIN` 设置视为兼容模式并显示警告；现改用独立管理员启动器及现有 Steam 快捷方式的 RunAs 标志，已备份并移除旧 shim。

   用户随后追问重启是否复发时，再核对发现 HKCU Windows Run 中仍有 `Steam="C:\Software\steam.exe" -silent` 普通权限自启，可能抢先占用 Steam 会话，使 Playhub 管理员启动器因 Steam 已运行而跳过。已备份至 `Steam-windows-startup-before.json` 并移除这个重复入口。核对默认启动模式 Gaming、Playhub 第一项管理员启动器仍启用、用户和公共开始菜单 Steam 快捷方式 RunAs 均为 true；HKLM 两个 Run 分支没有发现另一个 Steam 自启。游戏模式继续由 Playhub 自动启动 Steam；若直接启动到 Desktop，则旧 Windows 自启动不会再自动打开 Steam，可从管理员快捷方式打开。未实际重启，不能宣称完成了重启后的实测。

3. **桌面 Xbox 按键打开“捕获”小窗**：ClawTweaks helper 原逻辑关闭了 Game Bar 的“总是打开主页”，并在打开时强制激活自己的组件。仅给当前已核对的 helper 增加 `GameBarFirstSlot_PreserveHome=true` 可选设置，保留主页设置，跳过预加载和抢焦点。通过原 shutdown-request/ack 协议正常结束 helper，再由原 `\ClawTweaks\ClawTweaksHelper` 任务恢复。可见手柄前后均为 1。

   桌面模式仍由 ClawTweaks 执行原有 MSI 动作 75（右侧快捷栏）；插件桌面不执行 Gaming 菜单动作。用户实测确认：Xbox 键打开 Game Bar 主页；MSI 键可正常打开和关闭右侧快捷栏。

## 验证与范围

- 原生路由及固件来源检查 23 项，菜单／前端／导航检查 36 项通过；插件安装文件与工作区前端校验一致，实际载入版本 0.8.4。
- 绝区零和鸣潮均实际启动，Steam 全程保留正确运行状态并创建覆盖界面，进程退出后才移除记录。绝区零第二轮持续超过两分钟仍在追踪。
- 性能监控另行读取 MotionAssistant 的 RTSS 共享数据，检查游戏帧率与 OSD 更新，不将“Steam 在运行”混作性能监控证据。具体采样见本轮备份目录。
- 本轮没有进入游戏账号认证或操作账号资料。游戏实测覆盖启动及菜单阶段，不代表长时间游戏、所有场景或睡眠恢复均已验证。
- helper 修补只适用于本次严格核对的文件（原 SHA256 `DBE62A3EA97A6DC30F069ADCE3D16BB5AE2BBFD0A44CF09AD45D18C7CF141088`）。ClawTweaks 更新可能替换该文件，需要重新核对实现。修补工具遇到不同文件立即停止，不自动套用。

## 文件与恢复资料

配置与原文件备份：`artifacts/repair-20261008-1655/`。插件原版本另有安装时自动备份目录。

- `XboxGamingBarHelper.exe`：第三方 helper 原文件。
- `ClawTweaks-settings.json`、`GameBar-home-before.json`：ClawTweaks 原设置及 Game Bar 原主页设置；无需复制整个被系统占用的 UWP 设置 hive。
- `GamingMode-config.json`：Playhub 原配置。
- `Steam-compat-before.json`、`Steam-shortcut-0.lnk`、`Steam-shortcut-1.lnk`、`Steam-common-shortcut.lnk`：Steam 原兼容选项和快捷方式。0 对应用户开始菜单，1 对应工作区“大屏幕（管理员）”入口，common 对应全体用户开始菜单。
- `Steam-windows-startup-before.json`：后来核对并移除的重复 Windows 普通权限 Steam 自启动入口。
- 两个 `*-steam_appid.txt`：移出的原错误文件。
- `result.json`、运行状态、OSD 和返回验证 JSON：检查结果。

需要恢复 helper 时，先处于 Desktop 模式，按本次使用的原退出协议结束 helper，恢复原 exe 与 ClawTweaks 设置，然后运行原任务；不能覆盖运行中的 exe，也不能强制终止 helper。Game Bar 单项设置可用 `native/GameBarHomePreference.cs` 的 Set 方法恢复备份中的布尔值。恢复 Playhub 启动方案可还原配置中新增的 Steam Administrator Launcher 项，不覆盖用户之后修改的其他配置。

管理员进程间的输入权限限制见 [Microsoft SendInput 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)；Steam SDK 对 steam_appid.txt 的用途见 [Valve steam_api 文档](https://partner.steamgames.com/doc/api/steam_api)；运行状态监听和释放方式参考 [MobX reaction 文档](https://mobx.js.org/reactions.html)。本机根因判断还依据进程权限、Playhub 实现和 Steam 日志，以上链接不单独证明本机问题。
