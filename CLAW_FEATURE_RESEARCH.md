# Claw 工具箱下一步功能调研

日期：2026-10-08。基线：工具箱 0.8.2。目标：在接下来的 1–2 个版本里，让用户在 Steam 大屏幕／Playhub Gaming 中完成更多日常操作，减少切回桌面。本文是调研与建议，本轮没有增加插件功能或改变设备设置。

最新方向：唯一 P0 是用户指定的 SteamOS 缩放模式（自动、整数、适应、拉伸、填充）与缩放过滤器（线性、最近、FSR、NIS），排除小黄鸭替代路线。其他性能及通用功能当前暂缓。详见 [专项调研](STEAMOS_SCALING_RESEARCH.md) 与 [功能清单](FEATURE_BACKLOG.md)。本文的推荐和排序均为历史建议，不是当前开发计划；体感助手仍是功耗管理者。

## 历史判断

用户明确把“游戏模式下少切回桌面”放在第一位，因此新功能应围绕未完成的操作流程来选。现有的音频、显示、Lossless Scaling、封面和启动过渡已有本机组件承接，适合继续沿用。非 Steam 入口中有四个游戏位于 SD 卡，存档备份和文件操作是值得补齐的方向。窗口选择及返回游戏也符合这个目标，但本机发生窗口故障的频率尚未记录，需先验证与 Playhub 的窗口管理配合。ClawTweaks 已有多项掌机控制能力，而当前按键接管会暂停其后台，兼容交接比重复实现全部控制更值得单独研究。近期可以先做存档备份和工具箱自身的检查／恢复，再推进窗口助手与文件操作。

## 本机观察，与推断分开

以下是本轮只读观察：

- Windows 11，约 32 GB 内存；系统报告型号为 Claw 8 EX AI+ CG3EM Launch Pack。
- C 盘约 931.4 GB，剩余 85.1 GB（9.1%）；D 盘为可移动 exFAT 盘，约 477.5 GB，剩余 38.1 GB（8.0%）。这是检查时的容量快照。
- Steam 已保存的非 Steam 快捷方式共 8 个：4 个指向 C 盘，4 个指向 D:\gal。8 个启动目标当前均存在，没有发现待修复的失效路径。3 个入口带启动参数。
- 在已知 D:\gal 游戏目录仅查看一级目录名：Amakano3 下有 savedata，amakano2plus 下有 save。这是存档候选位置，没有读取存档内容，也没有验证所有存档是否完整存于这些目录；其余两款没有匹配到这类名称，不代表没有存档。
- 已装 Decky 插件：Playhub 2.1.0、Playhub Artworks、Launch Curtain、CSS Loader、Claw 工具箱 0.8.2。
- 已装 ClawTweaks Center、ClawTweaks Core、MSI Center M SDK；检查时 Lossless Scaling、RTSS、Clash Verge 和工具箱后台正在运行。
- 本机 Playhub 源码提供音频输入／输出、音量、显示模式／刷新率／HDR、Lossless Scaling 与逐游戏配置入口。此处证明接口和控件存在，没有实测每个功能是否在此机正常工作。
- 本机 Playhub 的 CPU 功耗读写接口明确返回 feature_removed / supported_write=false，不能将旧的 TDP 接口视为可用能力。
- 当前工具箱的按键接管会关闭属于 ClawTweaks 的组件界面并正常暂停其 helper；若 helper 重新启动，按键接管会暂停。这是现有兼容逻辑，不能直接加一个启动原软件的按钮就宣称两者可并行。

推断：SD 卡游戏、可见的存档候选和偏好的纯手柄操作，为存档及文件助手提供了使用基础；容量偏低提供了空间查看的理由，但不能据此声称机器已经因空间不足出现故障。窗口失焦、游戏解压频率、存档丢失以及换卡后的路径失效均未在本轮复现。优先级是产品建议，不是故障诊断或用户行为频率统计。

## 推荐功能及边界

| 顺序 | 功能 | 最小可用范围 | 证据／置信度 | 严重性与频率 | 可行性 |
|---|---|---|---|---|---|
| 1 | 存档备份 | 选游戏、确认存档位置、备份、查看历史版本、恢复；恢复前保留当前版本 | 4 个 SD 卡 Galgame 入口、2 个目录候选；需求推断中高，完整存档识别尚未验证 | 存档损失影响高；本机没有确认丢失事件，使用频率待用户确认 | 文件备份较明确；自动识别需要规则或用户确认 |
| 2 | 工具箱自检与恢复 | 检查自己的前后台版本、按键接管和反馈通路；给出具体原因与恢复入口；专用 MSI/Xbox 键可显示按下反馈 | 历史上出现过旧面板、helper 交接及菜单确认问题；高置信度存在诊断需求，这些历史问题已有修复 | 发生时影响高；不能把历史问题算作当前仍持续发生 | 可复用现有状态与恢复逻辑，先做自检，再做失败时恢复 |
| 3 | 窗口助手 | 列出当前游戏及相关启动器／设置窗口；选择目标、切回游戏、找回可见窗口 | 符合用户少退桌面目标；本机当前失焦频率未验证，需求置信度中 | 遮挡或失焦时影响中高；频率未统计 | 必须验证 Playhub、Steam 覆盖层和 Lossless Scaling 的焦点交接 |
| 4 | 轻量文件与 SD 卡工具 | 在插件内浏览 C/D、看空间；先做 ZIP 解压到指定游戏目录和明确选择的文件复制，后续再加入移动及路径更新 | 两盘可用空间、4 个 SD 游戏入口是直接证据；解压／搬运频率是推断 | 影响中；容量已确认，具体操作频率未统计 | 浏览及 ZIP 解压较明确；跨盘移动与路径更新范围较大，应分期 |
| 5 | Claw 原生控制中心兼容入口 | 在游戏模式内调出既有 ClawTweaks 控件，返回 Steam 后恢复工具箱接管 | 本机已安装原软件，接管冲突由代码确认；入口交接的需求置信度中高、实现置信度待验证 | 原硬件控制与 Steam 菜单的可达性影响高；用户调整频率未知 | 先做兼容性小验证；不能直接启动 helper 或复制整套硬件控制 |

### 存档备份

建议在游戏模式内完成“选择游戏 → 确认位置 → 备份／选择历史恢复”。SD 卡游戏可以把备份放到用户选定的 C 盘目录，并保留少量历史版本。先支持手动备份，自动退出游戏后备份留给后续版本。不要根据文件夹名字直接宣称识别成功，也不能认为添加为 Steam 快捷方式就自动获得 Steam Cloud：快捷方式只是入口，Cloud 需要游戏配置或 API 集成。[Valve 快捷方式说明](https://help.steampowered.com/en/faqs/view/4B8B-9697-2338-40EC)、[Steam Cloud 文档](https://partner.steamgames.com/doc/features/cloud)。

实现可以评估复用 Ludusavi 的存档识别和命令行备份，而在工具箱中提供手柄界面。它支持文件／注册表存档以及自定义游戏位置，但这并不证明本机四款 Galgame 都能自动识别。第一版应允许用户确认或手选路径。[Ludusavi](https://github.com/mtkennerly/ludusavi)、[自定义游戏文档](https://github.com/mtkennerly/ludusavi/blob/master/docs/help/custom-games.md)。

### 窗口与文件操作

窗口助手的价值是处理已经打开的游戏、启动器或设置窗口，提供清楚的选择和返回游戏入口。只打开资源管理器仍会把后续操作留在桌面，因此文件助手的关键操作需要在插件内完成。Windows 对前台激活有条件限制，窗口助手不能承诺所有程序都能随意抢到焦点，需与现有模式切换及覆盖层配合。[SetForegroundWindow 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)。

文件工具先按需操作指定文件和游戏目录，不做全盘自动扫描／清理。后续若加入游戏搬家，应在复制完成后保留既有 Steam AppID，仅更新目标和工作目录，同时保留原有名称、分类、参数及控制器配置。当前路径全部有效，单独的“失效路径修复”可以作为添加游戏模块的后续小扩展，紧急程度较低。

### 原控制中心与工具箱的交接

ClawTweaks 项目文档覆盖 TDP、逐游戏配置、手柄／鼠标切换、快捷动作和充电限制等能力。适合研究怎样在游戏模式内复用这些控件；本机安装版本的全部硬件功能没有在本轮实测。兼容验证应观察：打开原控件时如何交还按键，关闭后如何回到 Steam 并恢复接管，失败时如何保持可用的菜单操作。[ClawTweaks 作者仓库](https://github.com/enterTheVoidCode/ClawTweaks)。

本机源码证据：[StopTweaks](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/ClawRouter.cs:136)、[检测 helper 重新启动](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/ClawRouter.cs:205)。

## 历史近期建议

- 下一版可先落地：手动存档备份，或工具箱自检／恢复。两者范围可以控制，并能直接留在当前 UI 内完成。
- 后续 1–2 个版本：窗口助手的小范围验证、ZIP 解压及 SD 空间查看；通过交接验证后再提供 ClawTweaks 入口。
- 需要更深入确认：跨盘搬家、自动识别全部 Galgame 存档、区域模拟启动、后台自动备份。当前没有足够本机使用证据把它们列为第一批。
- 现有流程收尾：添加游戏后归类并跳转非 Steam 页，仍需用户实际面板的最终反馈；既有代码及接口检查不能替代这一步。

## 沿用已有能力

音频／显示／Lossless Scaling 沿用本机 Playhub；封面沿用 [Playhub Artworks](https://github.com/LoZazaMastro/Playhub-Artworks)；启动遮罩沿用 [Launch Curtain](https://github.com/LoZazaMastro/Launch-Curtain)。Xbox／Epic／GOG 等平台导入与一般模式切换已有 [Playhub](https://github.com/LoZazaMastro/Playhub/blob/main/README.md) 承接。常规游戏按键布局继续使用 Steam Input。工具箱优先补足当前模式下的操作入口和 Claw 专用兼容问题。

## 来源与限制

- 本机证据：CIM 设备／磁盘快照、已装插件元数据与源码、已装 Claw 组件名称、Steam shortcuts.vdf 聚合结果、四个已知 Galgame 目录的一级条目。聚合结果见 [本地记录](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/claw-feature-research-local.json)。没有读取存档正文或订阅链接。
- 项目作者与官方文档：Playhub、Artworks、Launch Curtain、ClawTweaks、Ludusavi，Valve 和 Microsoft，支撑已有能力及可行性边界。
- 公共反馈检索：Playhub 的 issue 信号较少；Valve issue #1464 是 SteamOS 多窗口反馈，不能证明本机 Windows 出现相同问题。ClawTweaks 社区有控制器／Game Bar／恢复的个别反馈，不能据此推算发生率；技术判断以上述作者资料和本机代码为主。
- 没有功能实测、代理联网测试、游戏启动、存档恢复或硬件设置变更。本轮不能评价这些功能在本机的稳定性。
