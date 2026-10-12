# Steam 大屏幕按键接管：0.12.1

按用户要求，自动接管开关开启时，Steam 大屏幕打开就接管 MSI／Xbox 前置键；真正退出大屏幕后恢复 ClawTweaks 原映射。最小化、失焦、启动或退出游戏均保持接管。Playhub Desktop／Gaming 状态不再参与判断。当前动作继续是 MSI → 右侧快捷面板，Xbox → 左侧主菜单。

## 实现

`plugin/steam-context.js` 读取 Steam 已有窗口状态：当前主实例为 GamepadUI，且存在大屏幕主窗口。它不检查前台、MainWindowVisible 或 MainRunningAppID；不会把残留游戏覆盖窗口或普通 Steam 客户端当作大屏幕。沿用 Steam 自身的 MobX 通知，不替换原生回调。

前端每两秒报告状态，前端标识和递增版本阻止旧实例、旧请求重新开启接管。原生后台只在状态变化时执行交接；15 秒未收到有效报告则恢复原映射，避免渲染进程失联时仍占用按键。此检查只读取内存状态，不增加定时 Playhub HTTP 请求或进程扫描。

保留关闭总开关、菜单失败恢复、按压时核对冲突 helper、正常退出协议、恢复日志以及插件卸载清理。睡眠通知仍暂停接管，恢复后等待新状态；没有修改 Windows 电源设置。Playhub 端口仅用于原有无覆盖菜单的焦点服务。

## 本机验证

原 Playhub state.json 为 Desktop，测试全过程未切换它。

- 大屏幕已打开：released=true，helperRunning=false。
- 大屏幕最小化并等待 18 秒（超过连接保护期限）：仍为 BigPicture，released=true，helperRunning=false；窗口 visible=false，后台报告持续。
- 调用 Steam 自身退出大屏幕：SteamClosed，released=false，helperRunning=true，restorePending=false。
- 重新打开：再次 BigPicture，released=true，helperRunning=false；原 MSI／Xbox 动作和所有开关保留。

实际状态记录：`artifacts/steam-ownership/lifecycle.json`。使用 `tools/verify-steam-ownership.mjs`，全程没有注入 WMI／实体键事件、没有重启 Steam 或改变 Playhub 模式。

93 项针对性检查通过（50 Node、14 Python 后端、14 原生接管策略和 15 固件快捷键边界），覆盖新状态判定、最小化／游戏保持接管、旧前端／旧报告拒绝、连接保护、菜单路由、草稿和反馈回归。最终版本 0.12.1 重载后再次通过上述实际生命周期测试；当前仅一个 ClawRouter-0.12.1 实例。193 个安装／打包文件与构建一致，Magpie 未启用。新版本实体键按压仍由用户复测；不能把自动状态交接视为实体键手感验证。本次没有测试系统睡眠、Steam 崩溃、强制结束 Decky 或失效 Playhub 焦点服务。
