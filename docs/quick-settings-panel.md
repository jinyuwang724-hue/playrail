# 快捷设置面板（游戏库 0.1.173）

快速设置键（Claw 键旁边那颗）打开。Steam 大屏幕里这颗键仍是 Steam 主菜单（`qamAction` 没动）；之外由后台 `buttonMappings.QS = controlCenter` 发 `control-center` 路由，游戏库收到就开这个面板（不再进控制中心）。源码在 OOBE 快照里，补丁 `artifacts/ws/quick-settings.patch`（基于快照 0.1.166，含窗口切换页）。

- **形式**：从左边滑出一列（宽 464 设计 px，#25282C），竖着排，参照 Steam 大屏幕左边的主菜单；磁贴照 Game Bar 小组件（#3E434B，强调 #4CC2FF）。游戏不切走画面：游戏库到前台，但游戏窗口用 DWM 实时缩略图铺在面板右边（按游戏窗口原位置裁），看上去面板压在游戏上。窗口先画两帧再拿到前台，避免闪出墙面。
- **内容**：标题（当前游戏名 / 快捷设置）；亮度、音量（A 静音）、手动功耗时多一条功耗滑条；磁贴：性能（系统控制 → 智能 → 快捷档位）、风扇、性能显示、限帧、刷新率、画面缩放 / 小黄鸭（只在游戏里）、震动、键盘、截图、Wi-Fi、蓝牙（Windows.Devices.Radios，清单加了 `radios`）；底部一排：窗口（窗口切换页）、桌面 / 游戏模式、睡眠。
- **后台调用**：和小组件相同——`widget_state`、`save_power`（整份功耗设置）、`set_brightness` / `set_volume` / `set_mute` / `set_refresh_rate` / `set_fps_limit` / `toggle_magpie` / `set_rumble_strength` / `toggle_keyboard` / `screenshot`；小黄鸭 `get_lossless_settings` / `set_lossless_running`。
- **操作**：方向键按位置走，滑条 ← → 调，A 立即生效（面板里一行反馈），B 或再按快速设置键收起：面板滑回去、缩略图铺满，再把游戏拿回前台、窗口退下。点面板外面也收起。
- **自测**：`--state=qs` / `qsnav` / `qsapply`（性能显示按一整圈，日志记回读值）。

已知：游戏模式下（没有 explorer）打不开 Game Bar（激活报 0x80040900），所以面板里不放 Game Bar。不是不抢焦点的叠加层：游戏会失去焦点（和「正在游戏」面板一样）；手柄输入按 XInput 读，游戏要是不看焦点也会收到。
