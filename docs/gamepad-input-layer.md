# 手柄软件输入层（混合映射的软件那一半）

2026-10-16。对象：MSI Claw 8 EX（1T91），手柄 USB `0DB0:1901`（XInput 模式）。
要还原的是 Steam 输入的映射功能（清单见 main 分支 `docs/steam-input-mapping/README.md`）；这一层先做第一批：触发方式 + 指令。

## 分层：每个键只归一层管

| 层 | 做什么 | 什么时候用 |
|---|---|---|
| 固件（`claw_gamepad_fw.py`） | 一个键 → 最多 5 个键码，零延迟、不常驻 | 软件层关着；或者某个键只要简单改键 |
| 软件输入层（本文） | 长按 / 双击 / 组合键 / 连发 / 切换 / 延迟、键鼠、系统动作 | 打开「输入层」后 |

规则：布局里用到的键，软件层开着时固件里一律放回原键（M1 / M2 改成 F22 / F23 交给钩子），关掉时按原样放回。见「固件协调」。

## 组成

```
游戏库 / Game Bar 小组件 / Decky ──JSON──▶ 工具箱后台 claw_agent.py
                                               └ claw_input_layer.py（布局、开关、冲突、固件协调）
                                                   └ ClawInput-<版本>.exe  桥（普通权限，stdin/stdout）
                                                         └ ClawInput --serve   引擎（管理员，命名管道）
                                                               ├ ClawInput --guard   守护（引擎死了 / 卡住 → 解除隐藏）
                                                               ├ HidHide   把真手柄藏起来
                                                               ├ XInput    1 kHz 读真手柄（引擎在白名单里）
                                                               ├ 低级键盘钩子  M1 / M2（F22 / F23）
                                                               ├ ViGEmBus  虚拟 Xbox 360 手柄（+ 震动转发回真手柄）
                                                               └ SendInput 键盘 / 鼠标
```

- 引擎要管理员权限：本机 Steam 以管理员运行，游戏也是管理员，低权限进程的 SendInput 会被 UIPI 丢掉，低级钩子也收不到发往它们的键。
  照 ClawPower 的做法：桥用 `runas` 起引擎（本机 UAC 不弹框），两者用只许本用户 / 管理员 / SYSTEM 的命名管道相连，并核对对方进程号。
- 输入层只在打开时有进程；关掉就整个退出（「不常驻」）。
- 不依赖第三方 DLL：ViGEm 和 HidHide 都直接发驱动的 IOCTL（`native/InputLayer/Devices.cs`）。本机 ViGEmBus 1.22、HidHide 1.5（ClawTweaks 装的那套）。

源码：`native/InputLayer/`（Engine.cs 触发逻辑、Devices.cs 设备、Hiding.cs 隐藏记录、LayoutJson.cs、Program.cs 进程角色和主循环），
`plugin/claw_input_layer.py`，测试 `tests/InputLayerTests.cs`、`tests/input_layer_backend_test.py`，真机工具 `tools/InputLayerProbe.cs`。

## 隐藏什么、谁还看得见

藏（HidHide 黑名单）：真手柄的 XInput 设备 `USB\VID_0DB0&PID_1901&MI_00\…`（xusb22）和它的 HID 游戏控制器视图 `HID\VID_0DB0&PID_1901&IG_03\…`。
不藏：MI_01 厂商接口（后台写固件用）、MI_02 键盘 / 鼠标集合（M1 / M2 的 F22 / F23 从这里来）。

看得见（HidHide 白名单，开启时加进去，关闭时只撤自己加的）：
- 引擎自己 `ClawInput-<版本>.exe`；
- 后台的 `pythonw.exe`（导航震动按 0DB0:1901 找真手柄）；
- 游戏库 `ClawLibrary.exe`：从本用户的包注册表（`ClawToolbox.Library_*` 的 `PackageRootFolder`）和正在运行的进程找路径，每 5 秒再找一次。

游戏、Steam、ClawRouter 等只看见虚拟手柄（`045E:028E`）。

## 前台和布局

引擎每 100 ms 看一次前台进程（exe 名，小写）：
- **不是游戏**（Steam 界面、游戏库、Game Bar、桌面、终端……沿用功耗页的 `NOT_GAMES` 名单，加上所有 `claw*` 开头的程序）→ **直通**：虚拟手柄原样镜像真手柄，不发任何键鼠。
  所以游戏库不管读到真手柄还是虚拟手柄，导航都和原来一样；Game Bar、Steam 大屏幕也照常。
- 是游戏 → 有这个 exe 的专用布局就用它，否则用全局布局。切换时先松开所有按住的键 / 鼠标键。

## 布局数据格式

存在 `%LOCALAPPDATA%\ClawToolbox\data\input-layer\layouts\`：`global.json` 一份，`games\<exe 小写>.json` 每个游戏一份（可选）。
界面用的就是下面这种「存储格式」；后台校验、补全默认值后再编译成引擎格式（键名 → 虚拟键码等）。

```json
{
  "version": 1,
  "name": "我的布局",
  "buttons": {
    "A": [
      {"trigger": "long", "longPressMs": 300, "command": {"type": "gamepad", "button": "B"}},
      {"trigger": "regular", "command": {"type": "gamepad", "button": "A"}}
    ],
    "X": [
      {"trigger": "double", "doublePressMs": 250, "command": {"type": "keyboard", "keys": ["Tab"]}},
      {"trigger": "regular", "command": {"type": "gamepad", "button": "X"}}
    ],
    "M1": [{"trigger": "regular", "repeatHz": 10, "command": {"type": "keyboard", "keys": ["Ctrl", "C"]}}],
    "Y":  []
  },
  "triggers": {"LT": {"threshold": 50}, "RT": {"threshold": 50}},
  "sticks":   {"left": {"mode": "stick", "output": "left"}, "right": {"mode": "stick", "output": "right"}},
  "settings": {"tapMs": 40}
}
```

### `buttons`：源键 → 触发方式列表

源键（19 个）：`A B X Y LB RB LT RT Back Start Guide LS RS Up Down Left Right M1 M2`。
- **没列出的键 = 原样**（A 还是 A；LT / RT 还是模拟扳机；M1 / M2 没列出 = 什么都不发）。
- 列出但是空列表 `[]` = 禁用这个键。
- LT / RT 一旦列出就当按键用（按下阈值见 `triggers`），不再输出模拟量。
- 每个键最多 8 个触发方式，每个触发方式绑一个指令。

### 触发方式（`trigger`）及设置

| 字段 | 默认 | 范围 | 说明 |
|---|---|---|---|
| `trigger` | — | `regular` 普通按压 / `long` 长按 / `double` 双击 / `start` 开始按压 / `release` 释放按压 / `chord` 组合键 | |
| `longPressMs` | 300 | 50–5000 | 长按时间（`long` 用） |
| `doublePressMs` | 250 | 50–2000 | 双击间隔：第一下按下到松开、松开到第二下按下都要在这个时间内（`double` 用） |
| `chordWith` | — | 另一个源键 | `chord` 必填：按下这个键时 `chordWith` 已经按住才触发 |
| `repeatHz` | 0 | 0–60 | 连发（按住以重复）频率，0 = 关 |
| `toggle` | false | | 切换：按一下开、再按一下关 |
| `startDelayMs` | 0 | 0–5000 | 触发启动延迟 |
| `endDelayMs` | 0 | 0–5000 | 触发结束延迟 |
| `interruptible` | true | | 可中断（见下） |

行为（照 Steam；时间都以 1 ms 为粒度，单元测试逐条验证过）：
- **普通按压**：按住期间指令按住。
- **长按**：按住满 `longPressMs` 起指令按住，到松开为止。
- **双击**：第二下按下时指令按下，第二下松开时松开。
- **开始按压 / 释放按压**：按下 / 松开那一刻发一次「短按」（长度 `settings.tapMs`，默认 40 ms，保证 60 帧的游戏也能看到）。
- **组合键**：这一次按压开始时 `chordWith` 正按住 → 这次按压只归组合键（同一个键的其他触发方式不响应）；`chordWith` 键自己照常。
- **可中断**（普通按压）：同一个键上还有长按 / 双击时，普通按压要等判断清楚才发（Steam 也是这样延迟的）：
  - 没到长按时间就松开 → 松开时发一次短按；按满长按时间 → 只发长按，普通按压不发。
  - 有双击：短按松开后等 `doublePressMs`，没有第二下才补发短按；按住超过 `doublePressMs`（且没有长按）→ 从那时起当普通按压按住。
  - 关掉可中断 → 普通按压立刻响应，长按 / 双击另外再叠加。
  - 释放按压设为可中断时，这次按压触发过长按就不发。
- **连发**：指令处于按住状态时按 `repeatHz` 一开一关。**切换**：每次触发翻转一次。**启动延迟**：比按压晚这么久才按下；按压比延迟还短也照样补一次短按。**结束延迟**：松开再晚这么久。

### 指令（`command`）

| `type` | 字段 | 例 |
|---|---|---|
| `gamepad` | `button`：`A B X Y LB RB Back Start Guide LS RS Up Down Left Right`，以及模拟量 `LT RT`（扣满）和 `LStickUp/Down/Left/Right`、`RStickUp/…`（推满） | `{"type":"gamepad","button":"B"}` |
| `keyboard` | `keys`：1–4 个键，按顺序按下、倒序松开（组合键） | `{"type":"keyboard","keys":["Ctrl","Shift","Esc"]}` |
| `mouse` | `button`：`Left Right Middle Back Forward`；或 `wheel`：`Up Down Left Right`（每次触发一格，配连发可连续滚） | `{"type":"mouse","wheel":"Down"}` |
| `system` | `action`：`screenshot`（Win+Alt+PrtScn，Game Bar 截图）、`keyboard`（触摸键盘开关）、`volumeUp` `volumeDown` `mute` | `{"type":"system","action":"screenshot"}` |
| `none` | | 什么都不发 |

键名：`A`–`Z`、`0`–`9`、`F1`–`F24`、`Esc Tab CapsLock Space Enter Backspace LShift RShift LCtrl RCtrl LAlt RAlt LWin RWin Menu Insert Delete Home End PageUp PageDown Up Down Left Right PrintScreen ScrollLock Pause NumLock Num0–Num9 NumMultiply NumAdd NumSubtract NumDecimal NumDivide NumEnter Minus Equals LeftBracket RightBracket Backslash Semicolon Quote Comma Period Slash Grave VolumeUp VolumeDown VolumeMute MediaPlayPause MediaStop MediaNext MediaPrev`；
别名 `Ctrl Shift Alt Win Escape Return`（存的时候换成 `LCtrl` 等）。完整列表由 `get_input_layer_catalog()` 给出，界面照它出选项。
键盘发扫描码（DirectInput / Raw Input 的游戏读得到），媒体键发虚拟键码。

### `triggers` / `sticks` / `settings`

- `triggers.LT.threshold` / `RT`：1–99（%），LT / RT 当按键用时的按下阈值，松开有 4 % 回差。
- `sticks.left|right`：`mode` = `stick`（照常输出）或 `none`（不输出）；`output` = `left` / `right`（可以左右互换）。
- `settings.tapMs`：短按长度 10–500 ms。

### 第二批（格式里留了位置，现在保存会返回「…第二批才做」）

`sticks.*.mode` = `mouse`（摇杆鼠标）、`scroll`（滚轮）、`dpad`、`flick`、`radial`、`mouseRegion`、`touchMenu`、`hotbar`；
顶层 `actionSets`（操作集 / 操作集层）；触发方式上的 `extraCommands`（子指令）、`haptic`（触感）、`cycle`（指令循环）、`invert`（反转输入）；
指令 `type` = `actionSet` / `camera`。到时候在这些字段上加，不改已有字段。

## 后台接口

都经现有的 agent 连接（`AgentClient`），JSON lines / WebSocket，方法名都在 `claw_agent.METHODS` 里。

| 方法 | 参数 | 返回 |
|---|---|---|
| `get_input_layer_catalog()` | — | 源键、触发方式、默认值和范围、全部指令选项（手柄键、键名、鼠标、系统动作）、第二批清单 |
| `get_input_layout(game=None)` | `game`：exe 文件名（如 `eldenring.exe`），不给 = 全局 | `{"game", "source": "game"/"global"/"default", "layout": {…补全默认值的布局}, "games": [有专用布局的 exe…]}`。游戏没有专用布局时返回全局布局、`source = "global"` |
| `save_input_layout(layout, game=None)` | 布局对象；`game` 同上。`layout = null` + `game` = 删掉这个游戏的专用布局（全局不能删） | 同 `get_input_layout`，加 `"saved": true/false`。不合法抛错，消息是给人看的中文。输入层开着时立即生效 |
| `set_input_layer_enabled(enabled)` | `true` = 我们接管手柄，`false` = 关闭 | 同 `get_input_layer_state`。设置会记住，后台重启后按它恢复 |
| `get_input_layer_state()` | — | 见下 |
| `test_input_layer(steps, layout_for=None)` | 真机自检用：`steps = [{"at": 毫秒, "buttons": ["A", …]}, …]` 代替真手柄跑一遍，`layout_for` 指定按哪个游戏的布局 | 引擎输出时间线（虚拟手柄按键位、键鼠事件，带 QPC 毫秒） |

推送事件 `input_layer`：值和 `get_input_layer_state()` 一样；开着时每秒一次，状态变了立即一次。

`get_input_layer_state()`：

```json
{
 "owner": "toolbox",                     // 设置：toolbox = 我们接管 / off = 关闭
 "phase": "active",                      // off / starting / active / stopping / error
 "error": null,                          // 出错或警告（给人看）
 "layout": "global",                     // 当前生效：global / game:<exe> / passthrough（前台不是游戏）
 "foreground": "eldenring.exe",
 "latency": {"samples": 2048, "lastMs": 0.21, "avgMs": 0.25, "p50Ms": 0.22, "p99Ms": 0.6, "maxMs": 1.1},
 "conflicts": {
   "clawTweaks": {"running": true, "processes": ["xboxgamingbarhelper.exe"], "helperSeesPad": true},
   "steamInput": {"steamRunning": true, "xboxSupport": null, "steamSeesPad": false},
   "otherVirtualPads": 0, "otherHiddenDevices": [], "warnings": ["ClawTweaks 在运行……"]},
 "firmware": {"m1": {"codes": [118, 62], "managed": true}},   // 软件层为了接管改过的固件项和原值
 "layouts": {"global": true, "games": ["eldenring.exe"]},
 "engine": {                              // 引擎原始状态（调试 / 详情页）
   "pad": {"connected": true, "index": 0, "packetHz": 250},
   "virtualPad": {"plugged": true, "serial": 1, "index": 1, "reports": 1234, "rumbleRequests": 3},
   "hidhide": {"cloak": true, "hidden": ["HID\\VID_0DB0&PID_1901&IG_03\\…", "USB\\VID_0DB0&PID_1901&MI_00\\…"], "apps": ["\\Device\\HarddiskVolume3\\…\\ClawInput-0.17.12.exe", "…"], "reapplied": 0},
   "loopHz": 1000, "highResolutionTimer": true,
   "backKeys": {"hooked": true, "m1": false, "m2": false, "swallowed": 0},
   "testLatency": {"samples": 0}, "otherVirtualPads": 0, "testRunning": false}
}
```

界面建议：`owner` 做开关；`phase = error` 时显示 `error`；`conflicts.warnings` 逐条列出；`layout` / `foreground` 显示「现在用哪个布局」；`latency.p99Ms` 显示延迟。

## 固件协调

开启时（以及开着时保存布局后）：
- 任何一份布局（全局或游戏）用到 M1 / M2 → 固件背键改成 F22（M1）/ F23（M2），引擎的低级键盘钩子吃掉这两个键、当成按键状态（和 ClawTweaks 一样的做法，经后台已有的 `set_back_key`）。
- 布局用到的正面键如果固件里改过（比如 A→B）→ 先放回原键（`set_key_map(源键, [自己的键码])`），交给软件层。
- 改之前把原值记进 `data\input-layer\firmware.json`。不再用到的键、关闭输入层、后台退出、引擎意外退出时都按原值放回：
  原来就是后台在管的项（如现在的 M1 = Alt+F12）写回原键码；原来没人管的用 `restore_gamepad(项)` 放回接管前的字节。
- 固件读不到时照样开，状态里报「M1 / M2 暂时用不了」。

## 失败兜底：手柄绝不会一直藏着

HidHide 的设置是持久的（重启也还在），所以每一次修改先写 `data\input-layer\hidhide-journal.json`（引擎进程号、原来的隐藏开关、**我们加的**设备和程序），
恢复时只撤这些：隐藏开关回到原样（原来关就关掉——这一步先做，立刻解除隐藏），再从黑 / 白名单里删掉自己加的条目。恢复可以重复做，结果一样。

| 情况 | 谁来解除隐藏 |
|---|---|
| 正常关闭、后台退出 | 引擎（先解除隐藏，再拔掉虚拟手柄，游戏不会有「没有手柄」的瞬间） |
| 后台崩溃 / 被杀 | 桥的 stdin 断开 → 管道断开 → 引擎自己恢复并退出；桥再等 5 秒复查一遍 |
| 引擎抛异常 / 会话注销 | 引擎的异常处理、`SessionEnding` |
| 引擎被强杀 | 守护进程（等在引擎进程句柄上，200 ms 一查）；桥也会复查 |
| 引擎卡死（主循环 3 秒没心跳） | 守护进程解除隐藏并结束引擎（心跳用不含睡眠时间的时钟，睡眠唤醒不会误判） |
| 断电 / 全部进程都没了 | 下次后台启动先跑 `ClawInput --restore`（记录里的引擎不在了才恢复） |

虚拟手柄：ViGEmBus 在打开它的句柄关闭时自动拔掉，引擎怎么死都不会留下一个假手柄。
HidHide 被别的程序改掉（ClawTweaks 会自己改 HidHide）：引擎每秒检查，缺了就补回去，状态里 `hidhide.reapplied` 计数并提示。

## 冲突检测（开启时和每次读状态时报告，不自动处理）

- **ClawTweaks**：`XboxGamingBarHelper.exe` / `CTW_Center.exe` 在运行；它的后台本来就在 HidHide 白名单里，仍能读真手柄；它也用 HidHide + ViGEm，可能改我们的设置。
- **Steam 输入**：Steam 在运行，且「Xbox 配置支持」开着（能从配置里读到时）→ 开了 Steam 输入的游戏会把我们的虚拟手柄再映射一遍；Steam 进了 HidHide 白名单 → 会同时看到真手柄。
- **别的虚拟 Xbox 手柄**（不是我们的那个）：游戏会看到多个手柄。
- **HidHide 里别人藏的设备**：隐藏开关是全局的，打开后它们也会被藏起来。

## 延迟

测量点：引擎读到真手柄的新数据包（`XInputGetStateEx` 返回、包序号变了）→ 虚拟手柄报告的 IOCTL 完成、SendInput 返回。只统计当拍就有输出的帧
（长按、双击这类「按设计要等」的不算）。最近 2048 个样本给出 last / avg / p50 / p99 / max（`latency`）；脚本测试的帧单独算（`engine.testLatency`）。
另外：1 kHz 轮询本身平均多 0.5 ms（最多 1 ms）采样等待；手柄自己的 USB 报告间隔见 `engine.pad.packetHz`。
目标 ≤ 2 ms。真机结果见下。

## 测试

- `tests/InputLayerTests.cs`（`test.ps1` 里跑）：51 项，触发方式的时间（长按 / 双击 / 可中断 / 组合键 / 切换 / 连发 / 启停延迟）、输出合并（多键同一输出、键盘组合键顺序、扳机当按键、摇杆方向、滚轮、切换布局松键）、引擎格式解析、HidHide 记录（只撤自己加的、别人的隐藏开关保留、补回被改掉的）。
- `tests/input_layer_backend_test.py`（自带 Python 运行时直接跑）：布局校验 / 补全 / 编译、按游戏存储（拒绝路径穿越）、开关流程（假引擎 + 假固件：M1 → F22、被改过的 A 放回、关闭时放回）、引擎崩溃后放回固件、冲突报告。

## 真机测试

（待补）

## 已知限制

- 只认 XInput 模式（`0DB0:1901`）。切到桌面模式后引擎找不到手柄，虚拟手柄保持中立，切回来自动接上。
- 只读 XInput 第 0 号的老游戏：真手柄仍占着系统里的 0 号位置（只是被藏起来），虚拟手柄通常在 1 号，这类游戏看不到手柄。HidHide + ViGEm 的通病。
- Game Bar 打开时前台是 Game Bar → 直通；Steam 游戏内覆盖层画在游戏窗口里，前台还是游戏 → 布局照常生效（和 Steam 一样）。
- MSI 键 / 快速设置键不经过这一层（还是 ClawRouter 管）。
- 陀螺仪：本机手柄当 Xbox 手柄认，没有陀螺仪数据，不在这一层。
