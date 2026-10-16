# 手柄软件输入层（混合映射的软件那一半）

2026-10-16。对象：MSI Claw 8 EX（1T91），手柄 USB `0DB0:1901`（XInput 模式）。
要还原的是 Steam 输入的映射功能（清单见 main 分支 `docs/steam-input-mapping/README.md`）；这一层先做第一批：触发方式 + 指令。

## 分层：每个键只归一层管

| 层 | 做什么 | 什么时候用 |
|---|---|---|
| 固件（`claw_gamepad_fw.py`） | 一个键 → 最多 5 个键码，零延迟、不常驻 | 软件层关着；或者某个键只要简单改键 |
| 软件输入层（本文） | 长按 / 双击 / 组合键 / 连发 / 切换 / 延迟、键鼠、系统动作 | 有布局绑了键时自动开（见下） |

规则：布局里用到的键，软件层开着时固件里一律放回原键（M1 / M2 改成 F22 / F23 交给钩子），关掉时按原样放回。见「固件协调」。

## 无感：没有开关（0.17.13 起）

用户不用管「谁接管手柄」：
- **全局或任何一个游戏的布局里有键**（`buttons` 非空，列出来但禁用的键 `[]` 也算）→ 后台自动保持输入层开着；全部为空 → 自动关掉。
- 后台启动时、`save_input_layout` 之后、手柄模式变了之后都马上按这条规则开 / 关。
- 手柄不在手柄（XInput）模式时一律关（见「手柄模式」）。
- 引擎意外退出：先解除隐藏、放回固件，5 秒后自动再开；10 分钟内最多自动重开 3 次，再坏就停在 `phase: error`，等下一次保存布局 / 后台重启。
- `set_input_layer_enabled(true / false)` 只留着调试：临时盖过规则，`null` 回到规则；不保存，后台重启后照规则。界面不调用。

## 组成

```
游戏库 / Game Bar 小组件 / Decky ──JSON──▶ 工具箱后台 claw_agent.py
                                               ├ claw_gamepad_fw.py（固件；ModeWatcher 每秒看手柄模式）
                                               └ claw_input_layer.py（布局、按规则开关、冲突、固件协调）
                                                   ├ ClawInput-<版本>.exe --osd  模式切换提示
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
- 输入层只在开着时有进程；关掉就整个退出（「不常驻」）。
- 不依赖第三方 DLL：ViGEm 和 HidHide 都直接发驱动的 IOCTL（`native/InputLayer/Devices.cs`）。本机 ViGEmBus 1.22、HidHide 1.5（ClawTweaks 装的那套）。

源码：`native/InputLayer/`（Engine.cs 触发逻辑、Devices.cs 设备、Hiding.cs 隐藏记录、LayoutJson.cs、Program.cs 进程角色和主循环），
`plugin/claw_input_layer.py`，测试 `tests/InputLayerTests.cs`、`tests/input_layer_backend_test.py`，真机工具 `tools/InputLayerProbe.cs`。

## 隐藏什么、谁还看得见

藏（HidHide 黑名单）：真手柄的 XInput 设备 `USB\VID_0DB0&PID_1901&MI_00\…`（xusb22）和它的 HID 游戏控制器视图 `HID\VID_0DB0&PID_1901&IG_03\…`。
不藏：MI_01 厂商接口（后台写固件用）、MI_02 键盘 / 鼠标集合（M1 / M2 的 F22 / F23 从这里来）。

看得见（HidHide 白名单，开启时加进去，关闭时只撤自己加的）：
- 引擎自己 `ClawInput-<版本>.exe`；
- 后台的 `pythonw.exe`（导航震动按 0DB0:1901 找真手柄）；
- **Steam**：`steam.exe` 和 `bin\cef\*\steamwebhelper.exe`（路径从 `HKCU\Software\Valve\Steam\SteamPath` 找，换成磁盘上的大小写）。
  Steam 大屏幕、它的导航始终读真手柄；输入层开着之后 Steam 重启也照样读得到。
- 游戏库 `ClawLibrary.exe`、Xbox Game Bar `GameBar.exe`、工具箱的 Game Bar 小组件 `ClawWidget.exe`：从本用户的包注册表
  （`ClawToolbox.Library_*`、`Microsoft.XboxGamingOverlay_*`、`ClawToolbox.GameBarWidget_*` 的 `PackageRootFolder`）和正在运行的进程找路径，每 5 秒再找一次。
  它们在游戏外用自己的手柄导航，读真手柄（游戏外虚拟手柄是全零，见下）。

开启之后才启动的程序（游戏、ClawRouter……）只看见虚拟手柄（`045E:028E`），而且它在这些程序里就是 0 号手柄（真机实测）。
**开启前就在运行、已经打开过手柄的程序（Steam、已经打开的游戏）仍能读到真手柄**：HidHide 只拦新的打开。
本想开启时重启手柄的 XInput 功能来关掉这些旧句柄，但 xusb22 不支持重启（`pnputil /restart-device` 返回 50），
禁用再启用又可能在出错时把手柄留在禁用状态（重启电脑也不会恢复），所以不做；状态里 `notes` 会提示「游戏要重新启动后才只看到虚拟手柄」。

## 前台和布局

引擎每 100 ms 看一次前台进程（exe 名，小写）：
- **不是游戏**（Steam 界面、游戏库、Game Bar、桌面、终端……沿用功耗页的 `NOT_GAMES` 名单，加上所有 `claw*` 开头的程序）→ **虚拟手柄保持全零**，不发任何键鼠。
  Steam、游戏库、Game Bar 都在白名单里，自己读真手柄，导航和原来一样。
  （0.17.12 时这里是「原样镜像真手柄」：Steam 把这个虚拟 Xbox 手柄套上桌面配置，结果手柄模式下左摇杆能移动鼠标。0.17.13 改成全零。）
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
| `save_input_layout(layout, game=None)` | 布局对象；`game` 同上。`layout = null` + `game` = 删掉这个游戏的专用布局（全局不能删） | 同 `get_input_layout`，加 `"saved": true/false`。不合法抛错，消息是给人看的中文。存完立即生效，并按规则开 / 关输入层 |
| `set_input_layer_enabled(enabled)` | 仅调试：`true` / `false` 临时盖过规则，`null` 回到规则 | 同 `get_input_layer_state`。不保存；界面不调用 |
| `get_input_layer_state()` | — | 见下 |
| `test_input_layer(steps, layout_for=None)` | 真机自检用：`steps = [{"at": 毫秒, "buttons": ["A", …]}, …]` 代替真手柄跑一遍，`layout_for` 指定按哪个游戏的布局 | 引擎输出时间线（虚拟手柄按键位、键鼠事件，带 QPC 毫秒） |

推送事件 `input_layer`：值和 `get_input_layer_state()` 一样；开着时每秒一次，状态变了立即一次。

`get_input_layer_state()`：

```json
{
 "owner": "toolbox",                     // 现在：toolbox = 输入层在跑 / off = 没跑
 "auto": true,                           // true = 按规则（没有调试覆盖）
 "wanted": true,                         // 规则要求的状态（有布局绑了键，且手柄在 XInput 模式）
 "override": null,                       // set_input_layer_enabled 的调试覆盖
 "padMode": "xinput",                    // xinput / dinput / desktop（ModeWatcher）
 "phase": "active",                      // off / starting / active / stopping / error
 "error": null,                          // 出错或警告（给人看）
 "notes": ["开启前就在运行的程序……"],       // 开着时的固定提示
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
   "keyLatency": {"samples": 0},          // 读到 → SendInput 返回（键鼠那条路）
   "testLatency": {"samples": 0},         // 脚本测试的帧，单独算
   "xinputSlots": "0DB0:1901 045E:028E - -", "otherVirtualPads": 0, "testRunning": false}
}
```

界面：没有开关（0.17.13）。需要时可以显示 `phase = error` 的 `error`、`layout` / `foreground`（现在用哪个布局）、`latency.p99Ms`。

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

引擎意外退出后，后台按规则 5 秒后自动重开（10 分钟内最多 3 次）。

虚拟手柄：ViGEmBus 在打开它的句柄关闭时自动拔掉，引擎怎么死都不会留下一个假手柄。
HidHide 被别的程序改掉（ClawTweaks 会自己改 HidHide）：引擎每秒检查，缺了就补回去，状态里 `hidhide.reapplied` 计数并提示。

## 冲突检测（开启时和每次读状态时报告，不自动处理）

- **ClawTweaks**：`XboxGamingBarHelper.exe` / `CTW_Center.exe` 在运行；它的后台本来就在 HidHide 白名单里，仍能读真手柄；它也用 HidHide + ViGEm，可能改我们的设置。
- **Steam 输入**：Steam 在运行，且「Xbox 配置支持」开着（能从配置里读到时）→ 开了 Steam 输入的游戏会把我们的虚拟手柄再映射一遍。
  Steam 在白名单里、能看到真手柄是有意的（0.17.13）：游戏里如果同时开着 Steam 输入，Steam 还会按它自己的配置翻译真手柄——这种游戏只用一种映射。
- **别的虚拟 Xbox 手柄**（不是我们的那个）：游戏会看到多个手柄。
- **HidHide 里别人藏的设备**：隐藏开关是全局的，打开后它们也会被藏起来。

## 手柄模式：桌面模式暂停、模式切换提示（0.17.13）

手柄每次切模式（长按 ≡，或 `set_gamepad_mode`）都会用另一个 USB 产品号重新枚举：`1901` 手柄（XInput）、`1902` DInput、`1903` 桌面。
`claw_gamepad_fw.ModeWatcher` 每秒列一次 HID 接口路径（不打开手柄）看是哪个产品号；重新枚举中间的空档不算变化。看到变化时，按顺序：
1. **模式提示（OSD）**：后台起 `ClawInput-<版本>.exe --osd gamepad|desktop`（`native/InputLayer/Osd.cs`）。屏幕上方居中的小窗，深色半透明圆角，
   左边手柄 / 显示器图标（Segoe Fluent Icons / MDL2），右边「手柄模式」/「桌面模式」，淡入 0.15 秒、停 1.15 秒、淡出 0.35 秒（约 1.6 秒）。
   置顶、不抢焦点（`WS_EX_NOACTIVATE`）、点不到（`WS_EX_TRANSPARENT`）、不进任务栏；每帧重新置顶，盖得住窗口化和无边框全屏的游戏。
   **独占全屏的游戏看不到**（它独占显示输出），这种情况只能靠应用内提示。没有 MSI 的红条。
2. 推送事件 `gamepad_mode`：`{"mode": "desktop", "previous": "xinput", "at": 毫秒时间戳}`。
3. 输入层：不在 XInput 模式 → 暂停（引擎退出：不隐藏真手柄、拔掉虚拟手柄、放回 M1 / M2）；回到 XInput 模式 → 按规则恢复。
4. 固件（照 MSI Center M）：切到桌面时，桌面摇杆死区第一次用就设成 MSI 的默认值（死区 10 / 外圈 100，`dstick.left/right`），
   然后写这个模式该有的字节：桌面模式写桌面那套摇杆死区和桌面键位，回到手柄模式写回手柄那套；模式指示灯（灯光 = modeinfo 时）跟着换色。

`get_gamepad_state()` 加了：`modeChangedAt`（最近一次看到切换的毫秒时间戳，后台启动以来没切过 = `null`）、`modeChangedTo`、`desktopSticks`。
前端拿 `modeChangedAt` 变没变弹应用内提示。新接口 `set_desktop_stick_deadzone(side, deadzone, outer)`（范围同手柄的，只在桌面模式生效）。

## 延迟

测量点：引擎读到真手柄的新数据包（`XInputGetStateEx` 返回、包序号变了）→ 虚拟手柄报告的 IOCTL 完成（`latency`）；键鼠那条路量到 SendInput 返回（`engine.keyLatency`）。
只统计当拍就有输出的帧（长按、双击这类「按设计要等」的不算），刚插上虚拟手柄的前几帧不算。最近 2048 个样本给出 last / avg / p50 / p99 / max；脚本测试的帧单独算（`engine.testLatency`）。
主循环用高精度可等待定时器对准固定的 1 ms 节拍（单纯「每次等 1 ms」在本机只有约 675 Hz）。
另外：1 kHz 轮询本身平均多 0.5 ms（最多 1 ms）采样等待；手柄自己的 USB 报告间隔见 `engine.pad.packetHz`。
目标 ≤ 2 ms。真机结果见下。

## 测试

- `tests/InputLayerTests.cs`（`test.ps1` 里跑）：51 项，触发方式的时间（长按 / 双击 / 可中断 / 组合键 / 切换 / 连发 / 启停延迟）、输出合并（多键同一输出、键盘组合键顺序、扳机当按键、摇杆方向、滚轮、切换布局松键）、引擎格式解析、HidHide 记录（只撤自己加的、别人的隐藏开关保留、补回被改掉的）。
- `tests/input_layer_backend_test.py`（自带 Python 运行时直接跑）：布局校验 / 补全 / 编译、按游戏存储（拒绝路径穿越）、按规则开关（第一个绑键的布局打开、游戏布局也算、最后一个键去掉就关、调试覆盖和 `null`）、
  桌面模式暂停 / 恢复、固件（M1 → F22、被改过的 A 放回、不再用到立即放回）、引擎崩溃后放回固件并自动重开、后台重启后照规则、白名单带 Steam、冲突报告。
- `tests/gamepad_fw_backend_test.py` 的 `mode_watch`：产品号变化 → 通知一次、记时间；枚举空档不算；第一次进桌面写 10 / 100，回手柄写回手柄那套，两套各自可改。

## 真机测试（2026-10-16，经已安装的后台 0.17.12）

用户同意：ClawTweaks 保持运行直接测（它在跑、HidHide 原本隐藏开关关、黑名单空、没有虚拟手柄）；不写固件；用脚本按键。
「游戏」用 `tools/InputLayerProbe.cs`：一个在前台、**不在白名单里**的窗口，1 ms 轮询 4 个 XInput 槽、记录收到的键，和引擎用同一个 QPC 时钟。
演示布局（全局）：A = 长按 300 ms → B + 普通按压 A；X = 双击 250 ms → Tab + 普通按压 X。脚本：长按 A 600 ms；短按 A 100 ms；双击 X；单击 X。

| 检查 | 结果 |
|---|---|
| 开启 | 0.7 秒；藏了 `HID\…IG_03\…` 和 `USB\…MI_00\…`；白名单加了引擎、后台 python、游戏库（从包注册表找到）；固件没动 |
| 冲突报告 | 「ClawTweaks 在运行」；Steam 在运行，「Xbox 配置支持」从配置里读不到（null） |
| 开启后才启动的「游戏」看到的手柄 | 只有 0 号 = `045E:028E`（虚拟），真手柄看不到 ✅ |
| 前台 / 布局 | `inputlayerprobe.exe` / `global` ✅ |
| 长按 A | 引擎 301 ms 发 B，600 ms 松开；没有发 A ✅ |
| 短按 A | 松开时发 A 短按（1000–1040 ms）✅ |
| 双击 X | 第二下按下时 Tab 按下（1560 ms），松开时 Tab 松开（1701 ms）；没有发 X ✅ |
| 单击 X | 双击窗口结束后补发 X 短按（2431–2472 ms）✅ |
| 「游戏」收到 | 虚拟手柄按键比引擎写出晚 1.0–5.3 ms（含探针自己 1 ms 轮询）；Tab 按下 / 松开都收到（窗口消息晚 12.7 / 2.4 ms，是探针窗口自己的消息循环） |
| 主循环 | 984–991 Hz，高精度定时器 |
| 延迟（读到 → 虚拟手柄写完） | 脚本帧 avg 0.26 ms、p50 0.12 ms、max 0.75 ms ✅（目标 ≤ 2 ms）。真手柄没人按、包序号不变，所以真机读数没有样本；两者只差读的那一步（`XInputGetStateEx`，微秒级） |
| 强杀引擎（管理员 `taskkill /F`） | 守护进程解除隐藏（记录里 `restoredBy: guard`），桥、守护都退出；后台状态 `phase: error`「输入层意外退出，已解除隐藏」；HidHide 和测试前逐项相同 ✅ |
| 关闭 | 引擎退出、虚拟手柄拔掉；HidHide 隐藏开关关、黑名单空、白名单 3 项，和测试前逐项相同 ✅ |
| 收尾 | 删掉演示布局；固件 M1 = Alt+F12、M2 = 0x12、A / X 原键，和测试前相同；Decky、游戏库、小组件重连正常 |

测试里发现并修掉的：
- 1 ms 相对等待只有 649–675 Hz → 改成对准固定节拍，990 Hz 左右。
- ViGEm 的「查询虚拟手柄编号」调用在本机不回应 → 改成看开启后新出现的 `045E:028E` 槽。
- 开启前就在运行的程序仍能看到真手柄（两个手柄都在）→ 重启 xusb22 不支持，改成文档和状态 `notes` 里说明（见上）。

没测的：真人按真手柄（用户说用脚本测就行）——真机读数的延迟、M1 / M2 经 F22 / F23 的钩子都要等真按键才有数据；
游戏的震动转发（状态里 `rumbleRequests` 有计数，强度没核对）。

## 真机测试第二轮（2026-10-16，0.17.13）

同样可撤销：测前测后 HidHide 逐项相同；没切手柄模式（见下）；没睡眠 / 重启。

| 检查 | 结果 |
|---|---|
| 后台启动、没有布局 | `phase off`、`auto true`、`wanted false`、`padMode xinput`；`get_gamepad_state` 有 `modeChangedAt: null`、`modeChangedTo: "xinput"` ✅ |
| 存一个绑了键的布局（不调开关） | 0.8 秒后 `active` ✅ |
| 白名单 | 引擎、后台 python、`C:\Software\steam.exe`、两个 `steamwebhelper.exe`、`ClawWidget.exe`、`ClawLibrary.exe`、`GameBar.exe` ✅ |
| 游戏在前台 | 长按 A → B；B + Y + LB 原样 ✅ |
| 前台是 Steam / 游戏库（脚本按 A、B、Y、LB） | 虚拟手柄一次都没写，保持全零；「游戏」探针只看到前一段的输出 ✅ |
| 强杀引擎 | 守护进程解除隐藏 → `phase error` → 3.5 秒起重开 → 6 秒新引擎 `active` ✅ |
| 存一个空布局 | 自动 `off` ✅ |
| 模式提示 | 直接起 `ClawInput --osd desktop / gamepad` 截图：上方居中、深色圆角、图标 + 字，盖在全屏的游戏库上；约 1.2 秒后进程自己退出 ✅ |
| 收尾 | 删掉测试布局；HidHide、固件（M1 Alt+F12、M2 0x12、A 原键）和测试前相同；`desktopSticks` 为空（没进过桌面模式） |

没在真机上做的：**真的切一次手柄模式**。用户之前要求切模式先保留，这次没有新的明确同意，所以「长按 ≡ / set_gamepad_mode → 检测 → 提示 → 输入层暂停 / 恢复 → 桌面死区」
这一串只在测试里用假手柄验证过（`mode_watch`、`switch`），OSD 单独在真机上显示过。真机切一次：手柄在旁边时长按 ≡ 切到桌面再切回来即可（固件自己的功能，随时能切回）。

## 已知限制

- 只在 XInput 模式（`0DB0:1901`）工作；桌面 / DInput 模式时输入层暂停，切回来自动恢复。
- 模式提示在独占全屏的游戏里看不到。
- 开启前就在运行的程序（Steam、已打开的游戏）仍能读到真手柄，会同时看到两个手柄；游戏重启后正常（见「隐藏什么」）。
- Game Bar 打开时前台是 Game Bar → 虚拟手柄全零，Game Bar 读真手柄；Steam 游戏内覆盖层画在游戏窗口里，前台还是游戏 → 布局照常生效（和 Steam 一样）。
- MSI 键 / 快速设置键不经过这一层（还是 ClawRouter 管）。
- 陀螺仪：本机手柄当 Xbox 手柄认，没有陀螺仪数据，不在这一层。
