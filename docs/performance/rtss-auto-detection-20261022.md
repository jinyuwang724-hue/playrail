# 新游戏没有 OSD：2026-10-22

用户反馈《明日方舟：终末地》和部分 Galgame 没有 OSD。

## 已确认原因

- RTSS 7.3.7 正在运行，工具箱 OSD 开关开启，RTSS 全局显示标志开启。
- 工具箱后台成功向 RTSS 的 `ClawToolbox` 共享内存槽持续写入监控文本，状态无错误。
- **Global 的 AppDetectionLevel 为 0（None），EnableOSD 为 1。** 这会使没有独立开启检测的新游戏无法收到 OSD。
- 2026-10-06 为绕过当时 MotionAssistant 覆盖全局配置的问题，为 54 个已知游戏主程序分别创建了独立检测配置。 [早期 OSD 修复记录](../archive/toolbox-2026-10/MOTION_OSD_FIX.md)
- 本机《终末地》实际程序是 `C:\Game\Arknights Endfield\Endfield.exe`，不在那批独立配置中。SDK 对它的有效配置在修复前继承 Global 的关闭检测。
- 排查期间 RTSS 记录过正在运行的 Kinkoi.exe，帧率约 120，OSD 渲染计数递增，证明已有独立配置的游戏至少有正常的数据与渲染通路；未以此代替游戏画面验证。

## 已应用修复

使用 RTSS 安装包随附 SDK 的 LoadProfile / SetProfileProperty / SaveProfile / UpdateProfiles 将 Global 改为 **Low（1）**，保持 EnableOSD=1。只改变默认检测开关，未改帧率上限、字号、延迟、图形接口或已有游戏配置。

工具箱游戏库自己使用 Direct3D 绘制界面，为 `ClawLibrary.exe` 新建检测关闭、OSD 关闭的独立配置，避免工具箱界面收到游戏监控条。

备份和前后回读记录：`diagnostics/rtss-auto-detection-20261022-202738/`。
执行与检查脚本：`tools/Set-RtssAutoDetection.ps1`，默认只读，`-Apply` 才写入。

## 验证与边界

- 修复后 SDK 回读：Global detection=1 / osd=1；Endfield.exe 的有效配置 detection=1 / osd=1，fpsLimit=0 / zoom=3 保持不变。
- 文件前后比较：Global 仅 EnableHooking 由 0 变为 1，以及保存时间变化。
- 后续独立回读仍为 Low，未观察到旧的自动跳回 None。
- 当前没有 MotionAssistant 进程；工具箱后台没有重启，也没有改功耗/风扇设置。
- 未自行启动《终末地》，其游戏画面尚未实测；已启动游戏如仍不显示，应退出后重新启动以重新建立检测。
- 部分老 Galgame 可能另有图形接口兼容问题。Global 中 HookDirectDraw=0 是当前观察值，但尚未确认用户所指 Galgame 的实际绘图接口，不能直接归因或批量改动。已向用户询问具体游戏名。

## 用户指定 Galgame 的实机补测（20:30–20:32）

用户补充《甜蜜女友》《甜蜜女友 SS》，认为本地添加的都没有，而抽查的 Steam 游戏有。

- 本地添加记录的实际主程序分别为 `D:\gal\甜蜜女友\AmakanoPECHS_GPT.exe`、`D:\gal\甜蜜女友SS\AmakanoSSCHS.exe`。旧 54 个配置只包含另一款 `Amakano2～PEchs.exe`，不包含这两个程序名。
- 两个 EXE 的 PE 导入都包含 d3d9.dll / Direct3DCreate9。本轮实际 RTSS 应用标志均为 4（D3D9），**不能将这两款归因于 GDI / DirectDraw 不兼容**。
- 保持上一节已修复的全局 Low，未为它们另加配置，分别从本地目录启动到标题画面。RTSS 检测到两个进程，OSD 渲染计数递增。
- 使用 computer-use 实际查看两个标题窗口，**两款游戏画面顶部均可见工具箱的 FPS、CPU、GPU 等监控条**。这是画面验证，不只是配置回读。
- 第一款已正常退出；SS 补测后通过游戏自己的退出确认关闭。未进入剧情、载入或保存存档。
- 本轮直接启动本地 EXE 验证引擎和 OSD 通路，没有把它当作“已从游戏库/Steam 快捷方式完整验证”。游戏库代码对已同步的本地游戏走 Steam 快捷方式，未同步的走同一 EXE；该路径未找到按来源关闭 RTSS 的逻辑。
- 因而本轮证据支持：之前的默认检测关闭和旧名单遗漏导致差异；在全局 Low 已生效后，这两款可以显示，无需逐款补进旧名单。

注意：窗口截图中完整 4 档单行 OSD 会在窄游戏窗口右侧截断。这与“完全没有 OSD”是两个问题，本轮未修改用户的 OSD 排版设置。
