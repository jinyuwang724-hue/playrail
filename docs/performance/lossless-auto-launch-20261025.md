# 小黄鸭自动接入与法环验证

2026-10-25，已部署 ClawPower 0.17.58-r23。Game Bar 0.4.42.0、游戏库 0.1.107.0、OSD 绘制器保持当前版本。

## 根因

- 法环的原生配置路径正确、AutoScale=true、延迟 5 秒、LSFG3 固定 2 倍；GraphicsConfig.xml 是 BORDERLESS。
- 本机原版小黄鸭为 3.2.2。读取安装程序集的匹配逻辑确认：FindProfileForWindow 把 Process.MainModule.FileName 和 MainWindowTitle 放在同一个 try 中，前者失败会跳过两种匹配。手动启动可以退回 GetSelectedProfile，因而仍能使用选中的档案。
- 同权限实测：管理员进程读取法环 MainModule 返回“拒绝访问”，QueryFullProcessImageName(PROCESS_QUERY_LIMITED_INFORMATION) 能取得正确的 eldenring.exe 路径。记录在 artifacts/lossless-launch/elden-process-access.json。
- 工具箱此前只写入原生自动缩放档案，AfterGameLaunch 没有小黄鸭运行时接入；Steam 启动提示也未解析没有 ExePath 的原生游戏目录。

## 修复

- 复用现有 note_game_launch，不另装前端包。Steam 游戏通过对应 manifest 解析安装目录和主程序；不是另设支持游戏列表。
- 接入仅在游戏库发出启动提示后运行，最多等待 120 秒。用轻量进程查询核对精确路径、PID、创建时间、窗口和前台状态。
- 优先等待实际出帧；没有 PresentMon 数据时，使用至少 20 秒的稳定窗口等待。小黄鸭不再被帧率采集是否可用挡住。
- 先给原版自动识别留出时间。已有补帧或原版倒计时会被保留。
- 备用接入使用安装在本机的小黄鸭原版档案列表与 ScaleButton。核对原生 XML 档案索引、控件数量、名称和 AutoScale 后选择正确档案，调用原版“缩放”按钮一次。
- 托盘中的 WPF 控件不可直接读取时，使用原版自身的 WM_SHOW_LS 接口短暂唤起，再回托盘。只恢复由这次交接引起的自身窗口焦点；游戏已被用户切走时中止。原版有约 5 秒倒计时，期间持续检查前台，切走则取消本次倒计时。
- 成功以原版实际创建并显示的缩放输出窗口为依据，不把“已发出请求”当作“补帧生效”。同一游戏进程生命周期至多请求一次，避免重试反向关闭已有补帧。
- LS 交接、ETW 和硬件操作各用独立通道，倒计时不阻塞帧采集或硬件请求。硬件写入仍按原来顺序串行执行。
- “打开小黄鸭原版”现在遵循原版保存的管理员选项，解决普通权限第二实例无法唤起管理员实例的问题。

## 实测与边界

- 按工具箱相同启动流程启动法环，在标题画面复现了原版自动接入失败。
- 原版窗口可见时完成了自动接入，并验证重复提示保留已有补帧。
- 将小黄鸭停止后以原有最小化方式重新启动，验证托盘状态下的接入。最终版在 21:40:08 记录到“已自动补帧”，实际运行状态 active=true。完整轮询位于 artifacts/lossless-launch/hidden-live-handoff-final.json。
- 验证过程中 PresentMon 对法环曾无数据；未据此捏造补帧 FPS。active 表示原版捕获／输出会话已开启，具体帧率仍以可用的实际采集为准。
- 流程可能短暂显示小黄鸭窗口，随后回到托盘。无帧率采样时，等待稳定窗口加原版倒计时通常需要约 25–30 秒。
- 未更改小黄鸭程序集、游戏可执行文件或进程保护。原生档案、游戏图形设置以及功耗／风扇设置在最后核对中分别比较。

测试：自动接入 10 项、原生设置编辑 23 项、配置接入 17 项、OSD 21 项；原生助手编译成功。自动接入覆盖关闭开关、精确路径、已有会话、无采样等待、单次请求、失败确认、前台切换以及关闭后台。

部署报告沿用 artifacts/native-osd/deployment.json。此次工作开始前备份在 LocalAppData/ClawToolbox/repair-backups/native-osd-20261025-211835；最终版本安装前备份为 native-osd-20261025-213936。
