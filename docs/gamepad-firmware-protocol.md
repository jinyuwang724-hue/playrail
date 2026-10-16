# Claw 8 EX 手柄固件协议（调研 + 只读）

2026-10-16。机器：Claw 8 EX AI+（SKU 1T91），手柄 USB `0DB0:1901`，固件 bcdDevice **0x0419**。
这一步只读：只发过 `0x26`（读模式）和 `0x04`（读配置内存），**没有写过任何固件设置**。

## 结论速览

| 项目 | 固件里在哪 | 能读 | 本机当前值 | 协议上能写 | 主要风险 |
|---|---|---|---|---|---|
| 手柄模式（手柄 / 桌面） | 命令 `0x26` 读、`0x24` 切换 | ✅ | XInput（1） | ✅ `0x24` | 切换后 USB 重新枚举；桌面模式下配置接口是否还在没实测 |
| M 键功能（宏 / 组合 / 禁用） | 同上，第 2 个字节 | ✅ | 0（宏 = 用各自映射） | ✅ `0x24` | MSI 和 hid-msi 对 1、2 的含义**相反** |
| 背键 M1 映射 | 配置内存 0xBA（码在 0xBD 起） | ✅ | Left Alt + F12 | ✅ `0x21`+`0x22` | ClawTweaks 自己也写这里 |
| 背键 M2 映射 | 0x163（码在 0x166 起） | ✅ | `0x12`（MSI 叫 FR1，背键原生码） | ✅ | 同上 |
| 左 / 右摇杆死区 | 0x22C / 0x23C（0–100） | ✅ | 5 / 5 | ✅ | ClawTweaks 开虚拟手柄时会把它调小 |
| 左 / 右摇杆外圈阈值 | 0x234 / 0x244（0–100） | ✅ | 100 / 100 | ✅ | 同上 |
| 左 / 右扳机起始死区（行程起点） | 0x20F / 0x21E（0–100） | ✅ | 0 / 0 | ✅ | 同上 |
| 左 / 右扳机外圈阈值（行程终点） | 0x210 / 0x21F（0–100） | ✅ | 100 / 100 | ✅ | 同上 |
| 快速扳机（Hair trigger） | 0x35 / 0x36（开关） | ✅ | 关 / 关 | ✅ | MSI 界面没给这个开关，效果没验证 |
| 摇杆灯光 效果 / 颜色 / 亮度 / 速度 | 0x24A 起（4 个动画槽） | ✅ | 1 帧全黑 = 灭；亮度 100，速度值 3 | ✅ | 多帧写法 hid-msi 和 MSI 布局对不上；可能还有实时 RGB 命令 |
| 震动强度（已做） | 0x22 / 0x23 | ✅ | 100 / 100 | ✅（已上线） | ClawTweaks 启动时改回 100 |

一句话：用户关心的所有项**都能从固件读到当前值**，都在同一片「配置内存」里，写法也都一样（`0x21` 写 RAM，`0x22` 存 ROM）。手柄模式单独用 `0x24`。

## 来源和交叉验证

1. **Linux hid-msi**（Derek J. Clark 等，v13，2026-07-19 发到 LKML，[cover](https://lkml.iu.edu/2607.2/06410.html)、
   [1/4 模式](https://lkml.iu.edu/2607.2/06411.html)、[2/4 M 键](https://lkml.iu.edu/2607.2/06418.html)、
   [3/4 灯光](https://lkml.iu.edu/2607.2/06413.html)、[4/4 震动](https://lkml.iu.edu/2607.2/06419.html)）。
   覆盖模式、M 键、灯光、震动；**不含死区和扳机**。
2. **MSI Center M 3.0.60831**（本机 `C:\Program Files (x86)\MSI\MSI Center M\API_ControlMode.dll`，命名空间
   `HJC.MSIGamePad.Core`，没有混淆）。用一个只读的小 IL 反汇编工具看了 `ProfileInfo.Parse`、各个 `*Profile.Parse`、
   `DeviceProxyService` 的读写流程和命令枚举。整个配置内存的布局都来自这里。
3. **ClawTweaks 0.4.1.21**（`XboxGamingBarHelper.exe`）：`SetHwDeadzone` / `EnsureFirmwareAllows` 直接写
   0x22C、0x234、0x23C、0x244、0x20F、0x210、0x21E、0x21F —— 和 2 推出来的地址**完全一致**。
4. **本机实读**：整片配置内存 1478 字节读出来，各字段解析出来都是合理值（死区 5、外圈 100、扳机映射到 LT/RT 本身等）；
   hid-msi 的地址（震动 0x22、M1 0xBB、M2 0x164、灯光 0x24A）全部落在 MSI 布局对应字段上。
   快照：`side-session-notes/…/gamepad-firmware-profile-snapshot-20261016.json`（以后第一次写之前的备份）。

## 通道和帧格式

和震动强度同一个接口（`claw_rumble.py` 的 `HidChannel`）：手柄的厂商配置集合（MI_01，Usage Page ≥ 0xFF00，输入 / 输出报告都是 64 字节）。

```
发：0F 00 00 3C <cmd> <data…>            补零到 64 字节
收：10 00 00 3C <cmd> <data…>
```

命令（MSI `CommandType` 枚举全表；**粗体**是这次用到或打算用的）：

| 命令 | 名字 | 说明 |
|---|---|---|
| 0x01 / 0x02 | EnterProfileConfig / ExitProfileConfig | MSI 定义了，读写流程里没用到 |
| 0x03 | WriteProfile | 旧写法，MSI 现在用 0x21 |
| **0x04** | **ReadProfile** | 读配置内存，回 0x05 |
| **0x05** | **ReadProfileAck** | 读的回复 |
| **0x06** | **Ack** | 通用确认 |
| 0x07 / 0x08 / 0x0B / 0x0C | SwitchProfile / WriteProfileToEEPRom / ReadCurrentProfile(Ack) | 多配置槽，MSI 没用 |
| 0x09 / 0x0D / 0x0A / 0xE0 | SyncRGB / ReadRGBStatus(Ack) / RGBControl | 实时灯光控制，没逆向，**不碰** |
| 0xFD / 0xFE | CalibrationControl / Ack | 摇杆 / 扳机校准，**不碰** |
| **0x21** | **WriteProfileData** | 写配置内存（RAM），回 0x06 |
| **0x22** | **SyncToROM** | RAM 存进 ROM（断电保留）；USB 传输结束后才回 0x06 |
| 0x23 | RestoreFromROM | **危险，不要用**：2026-10-16 实测不带参数发一次，灯光区（0x24B–0x308）被填进错位的其他配置数据（帧数、亮度变 0），其余区域没变。已按备份写回并存 ROM |
| **0x24** | **SwitchMode** | 切模式，数据 `[模式, M 键功能]`，不等 ACK |
| **0x26** / **0x27** | **ReadGamepadMode** / **GamepadModeAck** | 读模式，回复 data[0]=模式，data[1]=M 键功能 |
| 0x28 | ResetDevice | 重启手柄 MCU，**不碰** |
| 0x2C / 0x2D / 0x2F / 0x30 | SetFeatureState / DisableDevice / SetMotionStatus / MotionDataAck | 陀螺仪等，这次不管 |

### 读

```
发：0F 00 00 3C 04  01  <地址高> <地址低>  <长度 ≤ 55>
收：10 00 00 3C 05  01  <地址高> <地址低>  <长度>  <数据 × 长度>     （数据从第 9 字节起）
```

- 一次最多 55 字节（64 − 9），MSI 的 `SINGLE_HID_PACKET_LENGTH = 55`。
- 第 5 字节是配置槽号。MSI 读的时候发 0、写的时候发 1；本机回复永远是 1，两种读出来的 1478 字节完全一样。我们统一用 1（和 hid-msi、震动一致）。
- 回复要按「地址 + 长度」配对：hid-msi v12 起专门处理了「迟到的旧回复」。

### 写（这次没做，只记录）

```
发：0F 00 00 3C 21  01  <地址高> <地址低>  <长度>  <数据>      → 回 06
发：0F 00 00 3C 22                                             → USB 传完才回 06（要把它收掉）
```

- 0x21 只改 RAM，立即生效；0x22 才写进 ROM。MSI 的「预览」= 只 0x21，「保存」= 0x21 + 0x22。
- MSI 每次只写**变化的那一段**（和上次写入的整片内存比，取第一个到最后一个不同字节），超过 55 字节拆成多包。
- 写之前读一遍，写完再读回来核对（震动已经这么做）。

### 切模式

```
发：0F 00 00 3C 24  <模式> <M 键功能>      不回 ACK（hid-msi 当「孤儿 ACK」处理）；MSI 等 20 ms 再发 0x26 读回来
```

模式：1 XInput、2 DInput、3 MSI、4 桌面（键鼠）、5 BIOS、6 测试。hid-msi 只开放 1 / 2 / 4，说 3 / 5 / 6 是产线测试用、不稳定。

## 配置内存布局（固件 1.66+ / 2.17+ / 3.x+，本机 0x0419 适用）

来自 MSI `ProfileInfo.Parse`，总长 1478 字节。地址就是 0x04 / 0x21 里的地址。

| 地址 | 长度 | 内容 | 本机值 |
|---|---|---|---|
| 0x000 | 32 | 自定义数据 | 全 0 |
| 0x020 | 1 | 抬起唤醒（值 ≠ 1 时为开） | 01 |
| 0x021 | 1 | 回报率 | 00 |
| **0x022** | 2 | 震动强度 左 / 右（0–100） | 100 / 100 |
| 0x024 | 5 | 音频模块 | 0 |
| 0x029 | 10 | 陀螺仪模块（标志位、灵敏度、反死区…） | 62 01 00 04 00 19 32 ff ff 00 |
| 0x033 | 2 | Shift 键 | 0 |
| **0x035** | 5 | 扩展：快速左扳机、快速右扳机、十字键斜向锁、ABXY 互换、待机时间 | 0 0 0 0 0x0A |
| 0x03A | 16 × 8 | 16 个普通键的 KeyProfile（上下左右、LB RB LS RS、A B X Y、Guide、View、Menu、截图） | 各自映射到自己 |
| **0x0BA** | 169 | M1：KeyProfile（8）+ 宏（1 + 32 × 5） | 见下 |
| **0x163** | 169 | M2：同上 | 见下 |
| **0x20C** | 15 | 左扳机 TriggerKeyProfile | 见下 |
| **0x21B** | 15 | 右扳机 | 见下 |
| **0x22A** | 16 | 左摇杆 StickKeyProfile | 见下 |
| **0x23A** | 16 | 右摇杆 | 见下 |
| **0x24A** | 883 | 灯光 LightProfile | 见下 |
| 0x5BD | 9 | 扩展 1：四个亮度字节（MSI 叫 CRO / CIR / SQU / TRI，猜是 ABXY 背光）+ 5 保留 | 32 32 32 32 0… |

### KeyProfile（8 字节，普通键和 M 键）

| +0 | +1 | +2 | +3…+7 |
|---|---|---|---|
| 序号 | 标志位：bit0 连发、bit1 死区、**bit2 启用映射 / 宏**、bit3 线性 | 连发速度 | 映射到的 5 个键码（0x00 / 0xFF 为空） |

M1 = `01 04 0C 76 3E 00 00 00` → 启用映射，Left Alt + F12（这是 ClawTweaks 写的：它把背键映射成热键再自己截获）。
M2 = `01 04 0C 12 00 00 00 00` → 映射到 0x12（MSI `PROFILE_INDEX_FR1`，背键自己的原生码）。
宏步数在 +8（M1 0xC2 / M2 0x16B），每步 5 字节，最多 32 步；本机都是 0。

hid-msi 写 M 键就是写 0xBB 起 7 字节：`04 00 c0 c1 c2 c3 c4`（标志 = 0x04、连发速度 = 0、5 个键码）。注意它把连发速度写成 0，MSI 读到的是 0x0C。

键码（MSI `KeyMapDefine` 和 hid-msi 表一致，MSI 多一些）：
- 手柄：01 上、02 下、03 左、04 右、05 LB、06 RB、07 LS、08 RS、09 A、0A B、0B X、0C Y、0D Guide、0E View、0F Menu、10 截图、11 FL1、12 FR1、13 LT、14 RT、15–1C 左右摇杆四向、1D–28 十字键 / 摇杆斜向
- 键盘：32 Esc、33–3E F1–F12、3F `、40–49 数字 1…0、4E–57 QWERTYUIOP、5C–64 ASDFGHJKL、69–6F ZXCVBNM、67 回车、68 左 Shift、74 左 Ctrl、75 Win、76 左 Alt、77 空格、78 右 Alt、79 右 Ctrl、7A–7F Insert / Home / PgUp / Del / End / PgDn、80–83 方向键、84–94 小键盘、95–9B 媒体键、9C F23、9D F22
- 鼠标：C8 左键、C9 中键、CA 右键、CB 后退、CC 前进、CD 滚轮上、CE 滚轮下
- FF 禁用

### TriggerKeyProfile（15 字节）

| +0 | +1 | +2 | +3 | +4 | +5…+9 | +10…+14 |
|---|---|---|---|---|---|---|
| 序号 | 标志位（bit1 = 死区生效） | 连发速度 | **起始死区**（0–100） | **外圈阈值**（0–100） | 映射键码 | 线性曲线模块 |

本机左扳机 `01 02 0C 00 64 13 …`：死区开、起始 0、外圈 100、映射到 LT 自己。右扳机同理映射 RT。
MSI 界面的「扳机死区 / 扳机外圈阈值」就是 +3 / +4，「扳机行程」做成一个起止两端的范围滑块正合适。

### StickKeyProfile（16 字节）

| +0 | +1 | +2 | +3 | +4 | +5 | +6 | +7 | +8 | +9 | +10 | +11…+15 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 启用 | 死区开 | **死区**（0–100） | 映射开（MSI 用来「左右摇杆互换」） | X 翻转 | Y 翻转 | 映射目标 | 目标开 | 目标值 | 外圈开 | **外圈阈值**（0–100） | 线性曲线 |

本机左右都是 `01 01 05 00 00 00 00 00 00 01 64 …`：死区 5、外圈 100。MSI 的默认值是死区 10、外圈 100；5 很可能是 ClawTweaks 调过。

MSI 对手柄模式和桌面模式**各存一套**摇杆死区（自己的配置文件里），每次模式变化时把对应那套写进固件。固件里同一时间只有一套。

### LightProfile（0x24A 起，883 字节）

```
0x24A        当前动画槽号（0–3）
0x24B + n×220  动画槽 n：帧数(1–8)、效果号、速度、亮度、8 帧 × 9 区 × RGB(3)
0x24A + 881  音乐律动开关、保留
```

本机：槽号 0；槽 0 = `01 09 03 64`（1 帧、效果号 9、速度 3、亮度 100）+ 第 0 帧 9 区全 0 → **灯是灭的**。
槽 1 = 7 帧、速度 10；槽 2 = 1 帧、亮度 0x80；槽 3 = 1 帧 —— 是别的预设，不在用。
9 个区：每个摇杆环 4 个 + 1 个（hid-msi 的「rainbow / frostfire」把 0–3、4–7 当左右摇杆，8 是 ABXY 那边）。
速度：0 最快；hid-msi 对外显示 `20 − 原值`（0–20）。亮度：hid-msi 上限 100。

**和 hid-msi 对不上的地方**：hid-msi 写多帧效果时每帧都从 `0x24A + 帧号 × 0x24` 写一个 36 字节块（每块都带头），
而 MSI 的布局是一个 4 字节头后面紧跟 27 字节一帧。第 0 帧两边一致（单色、关灯都只用第 0 帧）；
第 1 帧起两边地址不同。没写过就判断不了谁对，所以**灯光第一版只做单色 / 关灯**（1 帧，两边一致），呼吸等多帧效果按 MSI 布局，写之前单独确认。

另外：如果你看到摇杆灯其实是亮的（之前记录「ClawTweaks：白色 50%」），那说明 ClawTweaks 用的是实时 RGB 命令（0xE0 / 0x09，没逆向），没存进固件；
固件里存的是「灭」。请看一眼现在灯亮不亮，告诉我。

### 手柄模式

`0x26` 回复 `27 01 00` → XInput、M 键功能 0。
M 键功能：MSI 枚举 0 宏、1 组合、2 禁用；hid-msi（v5 起特意交换过）0 宏、1 禁用、2 组合。0 两边一致；**1 / 2 必须实测后才能写**。

## 风险

1. **桌面模式回不来**：hid-msi 说桌面模式 USB 产品号会变成 0x1903，而且驱动只在 XInput / DInput 的端点上开配置接口。
   但 MSI Center M 只监听 0x1901 / 0x1902，却能在模式 4 下改桌面摇杆设置 —— 说明这台机器桌面模式下配置接口大概率还在。
   没实测。第一次切桌面前要准备好退路（MSI Center M 能切回、或者固件组合键），工具箱这边已经让读取接受 0x1901 / 0x1902 / 0x1903 三种产品号。
2. **ClawTweaks 会抢**：它启动时把震动改回 100；开虚拟手柄时会把固件死区 / 外圈「放宽」（`[HwFloor] Widened …`），还会同步到 MSI Center M 的配置；
   M1 现在是它写的 Alt+F12 热键。我们接管 M 键 / 死区后，ClawTweaks 的背键功能和它的软件死区会失效或打架。上线时要么关掉 ClawTweaks 对应功能，要么像震动那样定时检查、不一致再写回。
3. **MSI Center M**：它的服务现在是禁用的（MSI Foundation Service 停用）。如果以后启用，它会按自己存的配置把死区 / 灯光写回去。
4. **ROM 写入次数**：0x22 写闪存，MSI 和 hid-msi 都是每次改都 sync。滑块要防抖（震动用 700 ms），只在松手后存 ROM；拖动过程中只写 RAM（0x21），不 sync。
5. **整段写错位**：一次写错地址可能把别的键的映射改坏。每次只写目标字段那几个字节、写前读、写后读回核对；所有写入的「原值」先存进 `data/gamepad.json`，并提供「恢复」。
   整片 1478 字节的快照已经留了一份。
6. **固件版本**：这套地址只适用 1.66+ / 2.17+ / 3.x+（`claw_rumble.supported_firmware`）。老固件 M1 在 0x7A、灯光在 0x1FA，布局不同，直接拒绝。
7. **校准、重置、多配置槽、实时 RGB、陀螺仪**：这次不碰。

## 对照 MSI Center M 界面

截图在 `docs/msi-center-gamepad/`（本机 2026-10-16，MSI 服务禁用状态下打开的，没改任何设置）。

| MSI 界面项 | 范围 / 当前显示 | 对应固件 | 说明 |
|---|---|---|---|
| 控制模式 手柄 / 桌面 | — | 0x24 / 0x26 模式 1 / 4 | |
| 「自动切换」勾选框 | — | 不在固件 | MSI 软件功能（注册表 `AutoSwitchMode`），我们要做就自己做 |
| 按键对应：LT LB RT RB、A B X Y、十字键四向、左右摇杆按下 | 都显示「--」 | 0x3A 起 16 个 KeyProfile；LT / RT 在扳机块 +5 起 | 这次不做按键重映射 |
| 按键对应：M1 / M2 | 显示「--」 | 0xBA / 0x163 | **固件里实际是 Alt+F12 / 0x12**：MSI 显示的是它自己存的配置 |
| 快速设置键（启动快速设置）、Claw 键（启动 MSI Center M） | — | 配置内存里没有这两个键的字段 | 软件层处理；我们的特殊键接管（ClawRouter）已经管着 |
| 摇杆死区 | 左右各 **0–50 %**，当前 5 % | 0x22C / 0x23C | 固件字节本身能存 0–100，界面按 MSI 限到 0–50 |
| 摇杆外部阈值 | 左右各 **75–100 %**，当前 100 % | 0x234 / 0x244 | 界面限 75–100 |
| 交换左右摇杆 | 勾选框；勾上后映射不能用 | 摇杆块 +3 映射开、+6 映射目标（MSI：左摇杆勾上时写 2） | 可选 |
| 扳机 | 左右各一个双滑块 **0–100 %**，当前 0–100；「同步设置」勾选框 | 0x20F/0x210、0x21E/0x21F | 「同步」只是界面上左右联动，固件里还是两组 |
| 振动 | 左右分开 0–100 %，显示 50 / 50；「测试」按钮 | 0x22 / 0x23 | **固件实际 100 / 100**，又一处证明 MSI 显示的是自己的配置 |
| 校准 | 打不开（服务禁用） | 0xFD / 0xFE | 不碰 |

摇杆死区 5 % 两边一致，大概是因为 ClawTweaks 改固件死区时会顺手同步进 MSI 的配置（它的 `MsiCenterMProfile.MirrorDeadzone`）。
结论：**不能拿 MSI 界面的数字当固件当前值**，要以 `get_gamepad_state` 读到的为准；但 MSI 界面的范围（死区 0–50、外圈 75–100、扳机 0–100 双滑块）值得照搬。

## 已经加好的只读接口

后台（分支 `claude/gamepad-fw-readonly`，`plugin/claw_gamepad_fw.py`）：`get_gamepad_state()`，只发 0x26 + 0x04，11 次读，本机实测 44–57 ms。
还没部署到已安装的后台（见文末「部署」）。
失败不抛异常，返回 `{"available": false, "error": "…"}`。本机返回（节选）：

```json
{
 "available": true, "firmware": "0419", "product": "1901",
 "mode": "xinput", "modeRaw": 1, "mkeysFunction": "macro", "mkeysFunctionRaw": 0,
 "rumble": {"left": 100, "right": 100},
 "backKeys": {
  "m1": {"mapped": true, "codes": [118, 62], "keys": ["KEY_LEFTALT", "KEY_F12"], "macroSteps": 0, "turboSpeed": 12, "flags": 4, "index": 1, "turbo": false},
  "m2": {"mapped": true, "codes": [18], "keys": ["FR1"], "macroSteps": 0, "turboSpeed": 12, "flags": 4, "index": 1, "turbo": false}},
 "triggers": {"left": {"deadzoneEnabled": true, "deadzone": 0, "outer": 100, "codes": [19], "quickTrigger": false, "flags": 2},
              "right": {"deadzoneEnabled": true, "deadzone": 0, "outer": 100, "codes": [20], "quickTrigger": false, "flags": 2}},
 "sticks": {"left": {"deadzoneEnabled": true, "deadzone": 5, "outerEnabled": true, "outer": 100, "enabled": true,
                     "mapEnabled": false, "flipX": false, "flipY": false, "mapValue": 0}, "right": {"…": "同左"}},
 "light": {"effect": "off", "frames": 1, "effectNum": 9, "speedRaw": 3, "speed": 17, "brightness": 100,
           "color": [0, 0, 0], "uniform": true, "keyframes": [[[0, 0, 0], "…9 区"]], "animation": 0},
 "extras": {"dpadDiagonalLock": false, "abxySwap": false, "standby": 10}
}
```

`light.effect` 是按帧推出来的：1 帧全黑 = off，1 帧同色 = monocolor，2 帧（色 + 黑）= breathe，其他 = custom / animated。

## 建议的写入接口（待你确认）

都遵守：写前读、只写目标字节、写后读回核对；第一次写某一项前把原值存进 `data/gamepad.json`；每项都有「恢复原值」。
滑块类拖动时只写 RAM（0x21），松手 700 ms 后再 sync ROM（0x22）。

| 接口 | 参数 | 写什么 | 备注 |
|---|---|---|---|
| `set_stick_deadzone(side, deadzone, outer)` | side = left / right；deadzone 0–50、outer 75–100（同 MSI 界面） | 0x22C/0x234 或 0x23C/0x244，顺带把对应「开」字节置 1 | 同时只按「当前模式」写；手柄 / 桌面两套值后台自己存，切模式时写对应那套（照 MSI） |
| `set_trigger_range(side, start, end)` | 0–100，start < end | 0x20F/0x210 或 0x21E/0x21F，标志 bit1 置 1 | 就是「扳机行程」；「同步左右」放在前端做 |
| `set_stick_swap(enabled)` | bool | 两个摇杆块的 +3 / +6 | 可选，照 MSI「交换左右摇杆」 |
| `set_quick_trigger(side, enabled)` | bool | 0x35 / 0x36 | 可选，效果先实测 |
| `set_back_key(key, codes)` | key = m1 / m2；codes 最多 5 个键码；恢复 = 写回第一次接管前读到的原值（背键「原生码」到底是哪个，M1 现在被 ClawTweaks 占着，没法从本机读出来） | 0xBB 起 7 字节：`04 <连发速度原值> c0…c4` | 不碰宏区；会让 ClawTweaks 的背键功能失效 |
| `set_light(effect, color, brightness, speed)` | 第一版只 off / monocolor；color = RGB；亮度 0–100；速度 0–20 | 0x24A 起：槽号 0、帧数 1、效果号 9、速度、亮度、9 区同色 | 呼吸等多帧效果单独确认后再加 |
| `set_gamepad_mode(mode)` | xinput / desktop | 0x24 `[模式, 当前 M 键功能]`，20 ms 后读回 | 风险 1，第一次需要你在旁边；只开放 xinput / desktop |
| `restore_gamepad(item)` | 某一项或 all | 把 `data/gamepad.json` 里存的原值写回 | |

先不做：M 键功能（1 / 2 含义有争议）、宏、按键重映射（A/B/X/Y 等）、校准、重置、陀螺仪、实时 RGB。

需要你确认的：
1. 上面这些接口和范围行不行，哪些先做。
2. 背键：要不要接管 M1 / M2（接管后 ClawTweaks 的背键热键会失效）。
3. 灯光：现在摇杆灯实际亮不亮？第一版只做单色 / 关灯可以吗？
4. 手柄模式：允许我在你在旁边时试一次「切到桌面再切回来」吗？

## 部署

代码在分支 `claude/gamepad-fw-readonly`（从后台安装基线 `claude/ecstatic-tharp-f18221` 的 45fab15 开出），改动：
新文件 `plugin/claw_gamepad_fw.py`、`tests/gamepad_fw_backend_test.py`；`claw_service.py` 加 `get_gamepad_state`，`claw_agent.py` 允许列表加一项；
`claw_rumble.HidChannel` 可以接受多个产品号（默认仍只认 0x1901，震动行为不变）；`install.ps1` / `package.ps1` 带上新文件。

测试：`tests/gamepad_fw_backend_test.py`（假手柄，断言只发过 0x26 / 0x04）和 `tests/rumble_backend_test.py` 都过；`--live` 读真机通过。
`agent_test.py` / `backend_test.py` 在自带的 Python 运行时里跑不了（没有 unittest），改用「把已安装后台复制一份、换上新文件、加载模块并调一次 `get_gamepad_state`」验证接线。

没有装到 `%LOCALAPPDATA%\ClawToolbox\app`：安装基线分支那边还有别的会话没提交的改动，装上需要后台换版本重启。
合进安装基线后按 `install.ps1` 正常装即可（从 Claude 应用里要经 `tools\Invoke-OutsideApp.ps1`）。

## 已实现的写入接口（2026-10-16，分支 claude/gamepad-fw-readonly 的 c1cf329）

范围全部照 MSI Center M，超出直接返回错误。每个写入方法成功后都返回一份新的 `get_gamepad_state()`。

| 方法 | 参数 | 说明 |
|---|---|---|
| `set_stick_deadzone(side, deadzone, outer)` | side `left`/`right`；死区 0–50；外圈 75–100 | |
| `set_stick_swap(enabled)` | bool | 左摇杆块 +3=1、+6=2，右摇杆块 +3=1、+6=1（同 MSI）；false = 放回原值 |
| `set_trigger_range(side, start, end)` | 0–100，start < end | 同时把死区开关位置 1 |
| `set_quick_trigger(side, enabled)` | bool | 0x35 / 0x36 |
| `set_back_key(key, codes)` | `m1`/`m2`；1–5 个键码，`[0xFF]` = 不映射 | 序号 1、映射位开、连发关；恢复用 `restore_gamepad("m1")` |
| `set_key_map(source, codes)` | source：A B X Y LB RB LT RT LS RS Up Down Left Right | `[自己的键码]` = 放回原值。LT/RT 写扳机块 +5，标志位只动 bit0/bit2 |
| `set_desktop_key_map(source, codes)` | 同上 | 只在手柄处于桌面模式时写进去，回到手柄模式自动放回（同 MSI） |
| `set_light(effect, color, brightness, speed, direction, frames)` | effect：off / monocolor / breathe / rainbow / ripple / custom / modeinfo；color `#rrggbb`；亮度 0–100；速度 0 慢 / 1 中 / 2 快；direction `cw`/`ccw`（水波）；frames（custom，1–8 帧，每帧一个颜色或 9 个区的颜色） | 帧按 MSI 布局写在槽 0。MSI 的速度：呼吸 11/14/17、幻彩 13/15/17、水波 14/17/20（固件字节 = 20 − 速度）；常亮 17、关闭 15 |
| `set_gamepad_mode(mode)` | `xinput` / `desktop` | 0x24 后等手柄报告新模式，再写当前模式该有的映射和模式指示灯。**还没在真机上试过**（用户要求先保留） |
| `restore_gamepad(what)` | `all` / `sticks` / `triggers` / `backkeys` / `keys` / `desktopkeys` / `light` / 单项名（如 `stick.left`、`m1`） | 写回第一次写之前存的原值 |
| `set_rumble_strength(left, right)` | 各 0–100；只给一个参数 = 左右相同 | `get_rumble_state` 返回 `current [左, 右]`、`left`、`right` |

`get_gamepad_state()` 新增：`keys`（14 个源键当前映射）、`sticks.swap`、`light.speedLevel`（0–2）、`light.color` 改成 `[r, g, b]`、`light.managed`、`managed`（我们接管的项目）、`desktopKeys`。
固件里的灯光只存帧，所以读回时：和我们写进去的一致就报我们设的效果 / 速度 / 方向，否则按帧推断。

后台每分钟检查一次接管的字节（启动后 3 / 30 / 120 秒也查），被 ClawTweaks 等改掉就写回去；定时检查最多 10 分钟存一次 ROM。

## 真机测试（2026-10-16 14:20，经已部署的后台）

后台 14:17 用新代码重启（`shutdown` + 计划任务 `\ClawToolbox\Agent`），Decky、游戏库、Game Bar 小组件都自动重连。
每一项：经后台写入 → 直接读整片 1478 字节，确认只改了目标字节 → `restore_gamepad` → 再读整片，和写之前的备份逐字节相同。

| 项目 | 改了哪些字节 | 结果 |
|---|---|---|
| 摇杆死区 左 12 / 90、右 8 / 95 | 0x22C 0x234 0x23C 0x244 | ✅ 写入、恢复；越界（死区 51、外圈 74）被拒 |
| 交换摇杆 | 0x22D 0x230 0x23D 0x240 | ✅ |
| 扳机 左 10–90、右 5–95，左快速扳机 | 0x20F 0x210 0x21E 0x21F 0x35 | ✅ |
| 按键映射 A→B、LT→RT | 0x7B 0x7D 0x20D 0x211 | ✅ |
| 桌面映射 A→回车（手柄模式下） | 无 | ✅ 只存不写 |
| 震动 30 / 60，再单参数 100 | 0x22 0x23 | ✅ 最后回到 100 / 100（原来就由后台接管在 100） |
| 灯光 常亮白 / 呼吸红 / 幻彩 / 水波 / 模式指示，亮度 50 | 0x24A 起 | ✅ 读回效果一致，每个停留约 4 秒，最后恢复成原来的「灭」 |
| M1 / M2 | 无 | ✅ 按用户要求接管，值保持 Alt+F12 / 0x12，后台每分钟检查 |

21 / 21 通过。测试结束时手柄内存和写入前备份逐字节相同（只有 M1/M2 记成「接管」）。

没测的：
- **灯光实际亮出来对不对、按键映射和死区的手感**：要人眼看、手按；字节已确认写进去。
- **ROM 掉电保存**：唯一能不断电验证的 0x23 不安全（见上表）。要确认就改一项、重启电脑后用 `get_gamepad_state` 看。
- **切模式**：按用户要求先保留。

测试中出过的问题：第一轮里我用 0x23 验证 ROM，把灯光区弄乱了，后面几次自动存 ROM 很可能也把乱的灯光区存了进去。
已用写入前的备份把整个灯光区（0x24A–0x5BC）写回并存 ROM，整片内存和备份一致；第二轮去掉 0x23 后全部通过。

## 模式切换的检测和桌面摇杆死区（2026-10-16，0.17.13）

照 MSI Center M：固件长按 ≡ 切换（或它快速设置里的 ControlMode 磁贴）→ 固件上报 GamepadModeUpdated → MSI 弹 OSD、写注册表 `ControlModeUpdated`、
切到桌面时同步桌面摇杆死区（默认 DeadZone 10 / Edge 100）、按模式设模式指示灯。我们的做法：

- **检测**：不靠 MSI 的服务（它禁用着）。手柄每次切模式都用另一个产品号重新枚举（`0x1901` XInput、`0x1902` DInput、`0x1903` 桌面），
  `ModeWatcher` 每秒列一次 HID 接口路径看产品号，不发任何命令；长按 ≡ 和 `set_gamepad_mode` 一样能看到。重新枚举中间的空档不算。
- **提示**：见 `docs/gamepad-input-layer.md`「手柄模式」——自己的 OSD（不要 MSI 的红条）+ 推送事件 `gamepad_mode` + `get_gamepad_state().modeChangedAt`。不写 MSI 的注册表。
- **桌面摇杆死区**：摇杆块（0x22A / 0x23A）只有一套，MSI 每次切模式重写。我们同样：桌面那套存成只在桌面模式生效的项 `dstick.left/right`
  （第一次进桌面时按 MSI 默认死区 10 / 外圈 100 建，接口 `set_desktop_stick_deadzone(side, deadzone, outer)` 可改），回到手柄模式写回手柄那套
  （用户设过的 `stick.*`，没设过就是接管前的原值）。只在桌面模式生效的项现在优先于不分模式的项。
- **模式指示灯**：灯光是 modeinfo 时，切模式后的 `enforce("mode")` 按新模式换色（之前就有）。

真机：检测 / 死区同步只用假手柄测过；没有真的切模式（用户之前要求先保留）。
