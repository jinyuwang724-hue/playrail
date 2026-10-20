# C8EX 风扇策略断电保存：固件逆向记录

日期：2026-10-19。目标是设备上电即加载自定义风扇策略，Windows 工具箱不开启、退出后也不影响执行。开机启动后台重新写入、EC 寄存器读回一致，都不算实现这个目标。

**当前结论：整项功能未实现，不能交付可刷写功能。** 已完成升级包识别、官方 EC 默认表加载函数的静态追踪与离线指令模拟；原厂 Auto 初始化位置已定位。原版 Windows 更新包的目录签名检查通过，修改单字节的副本不通过。未运行升级程序，未刷写或重启设备；现有软件版本及风扇配置未因本次分析而改变。最新分项结论见第 7 节。

## 1. 用户提供的升级包更新手柄

输入：`MSI Controller FW Updater_2608_3101.zip`。

- ZIP SHA256：`792f4da85c487b9e649a46ee5a6222f2484a553405ab8ebbc2455a2d89c54a59`。
- 唯一 EXE 的 Authenticode 签名有效，发布者为 Micro-Star International。
- 静态反编译得到 `MSI_GamePad_FW_Updater` 和嵌入的 `HJC.MSIGamePad.Core`。
- `DeviceListener.IncludeVidPid` 指定 USB HID `0DB0:1901` / `0DB0:1902`。
- `UpdateViewModel` 为 4.x 手柄选用 `MSI_1T91_V419_Key.ufw`，目标版本 4.19。
- 更新链路使用 JieLi 的 `JL_loadFirmwareData`、`JL_upgradeDeviceByHandle` 等函数。

这个组件中的 `SyncToROM (0x22)` 保存手柄配置，不能拿来保存热管理 EC 的风扇曲线。未执行任何 HID 更新或保存命令。

## 2. 找到了正确的官方 BIOS / EC 镜像

从微星官网下载两份原始包用于比较：

- [E1T91IMS.10D](https://download.msi.com/bos_exe/nb/E1T91IMS.10D.zip)：ZIP SHA256 `2b3ac545458bc2830c2d16473259e9f4be9a97a79307b568189db61f9d20f298`。
- [E1T91IMS.10A](https://download.msi.com/bos_exe/nb/E1T91IMS.10A.zip)。

10D 包内发布说明指定板号 `MS-1T91`，机型 CG3EM / CG3M，更新 EC 到 `1T91EMS1.103`。10A 对应 `1T91EMS1.102`。本机 BIOS 为 `E1T91IMS.10D`；这里的 EC 版本来自官方文件，未把它冒充为本机 EC Flash 实读结果。

`E1T91IMS.cap` 长 25,165,824 字节。文件前 `0x80000` 字节中存在 8051 指令、EC 版本字符串和风扇表候选数据；版本字符串位于文件偏移 `0x9010`。10D 这段原始数据的 SHA256 为 `c03b80ac1cb8d1c7053d4e2a16ee86c715ecc4d0f4d5b4c02dccbd89521f27db`。这只是官方包内区域的离线提取，不是本机完整 Flash 备份。

发现两组候选数据，偏移均以原始 CAP 文件为基准：

| 内容 | 第一组偏移 | 第二组偏移 | 数据 |
|---|---|---|---|
| 温度序列 | `0xFCF6` | `0xFD26` | 47、50、57、64、71、78、85 |
| 10D 占空比序列 | `0xFCFD` | `0xFD2D` | 60、64、68、74、80、84、94 |
| 10A 同位置序列 | 同上 | 同上 | 70、74、76、78、80、84、94 |

初次分析时，10D 的前六个值与本机原厂备份相符，但当时仅有字节匹配证据。**本轮已进一步确认第 7 节所列默认表加载函数读取这些地址，并通过离线指令模拟确认目标运行区。** 这仍不是实机冷启动或温控验收。第七个值 94 已确认会复制到第七个表字段，其完整温控语义仍未确认，模拟修改保留此值。

另已离线提取 `MsiBoardECDxe` / `MsiBoardECSmm`，并识别 `MsiBoardECPei`。8051 的相关窗口映射和一条初始化调用链已继续追踪至第 7 节；全部启动/恢复分支及外设行为未完成实机验证。最初按 64KB 分块的线性反汇编只作搜索材料，不能按其文件偏移直接解释跨 bank 调用。

## 3. 本机 ACPI 代码确认了现有写入的边界

使用 Windows `GetSystemFirmwareTable` 读取本机 DSDT，再以 ACPICA iASL 20260408 离线反汇编。未执行 AML 方法或写任何寄存器。DSDT 长 234,286 字节；反汇编提示部分外部方法未解析，因此不据此断言所有固件路径均已排除。

在 `\_SB.PC00.LPCB.EC.SCM0.WMAM` 中：

- `Arg1=0x11`（Get_Fan）：风扇 1 读 EC `0x71..0x78`，风扇 2 读 `0x89..0x90`。
- `Arg1=0x12`（Set_Fan）：按同样地址逐字节调用 `WMWT`。
- `Arg1=0x1A`（Set_AP）、子功能 1：写 `0xD4..0xD6`。
- `Arg1=0x1C`（Set_Data）：`WMWT(IPAR, BS00)`。
- `WMWT` 仅向 I/O `0x360` 写索引，再向 `0x361` 写数据。
- `Arg1=0x21/0x22` 是另外两页 EC 内存的读写，分别映射 `0xFC000800` / `0xFC000700`；这里的 `0x22` 与 USB 手柄的 `SyncToROM` 完全不同。

这些已检查的风扇写入分支没有独立的 Flash 保存、提交或 NVRAM 写入调用。不能据此把 `Set_AP` 的 `0x80` 位描述为“永久保存”。也未尝试向未知寄存器发送所谓保存命令。

完整枚举只取得 Windows API 返回的首张 SSDT，未声称已读取全部同名 SSDT。MSDM 激活信息不作为研究材料保留。

## 4. 为什么这次没有上线“永久写入”按钮

目前缺少的不是三个预设的界面或配置文件，而是以下实际验证：

1. ROM 数据到加载函数和完整初始化分支已完成静态追踪；还需实机确认全部启动条件、温控响应、低温档、限幅和热保护字段。
2. 确认本机是否存在受支持的非易失配置区；若必须改写 EC 程序，确认镜像校验和设备接受路径。
3. 获取这台设备的完整可恢复备份，验证独立恢复手段。官方下载包不能替代设备备份。
4. 在没有任何主机风扇写入的条件下，验证曲线跨温度节点响应，再验证关闭工具箱、重启、完全断电后上电。

官方包的驱动目录签名有效，UEFI 解析也发现 Boot Guard 清单；**这不等于已证明 EC 区受同一签名覆盖，也不等于修改 EC 后一定可以刷入。** 未绕过验证、未安装固件驱动、未运行 `FLASHWIN.bat`。当前没有经过上述验证的修改镜像，也没有已验证的本机救援路径，不能进行盲刷。

此前运行时曲线的独立温控测试结果仍见 [风扇验收记录](fan-firmware-verification-20261019.md)。本次没有用修改开机自启或后台服务来替代永久写入。

## 5. 可复现的只读工具与证据

新增 `tools/Inspect-FanFirmware.py`，只读取本地包、输出 JSON；不会执行 EXE、调用硬件接口、生成修改固件或刷写。它识别已观察到的 MS-1T91 镜像布局，列出版本、哈希和候选数据；未知布局保持 unknown，所有结果都标记 `flashReady=false`。

```text
python tools/Inspect-FanFirmware.py <官方10D.zip> --compare <官方10A.zip> --output <报告.json>
python tools/Inspect-FanFirmware.py <手柄升级包.zip> --output <报告.json>
```

本机证据保存在不提交到仓库的 `diagnostics/fan-persistence-analysis/`：`package-audit.json`、`controller-audit.json`、`DSDT.dsl`、`WMAM.full.asl.txt`、独立 WMI 方法摘录、原始镜像区域和静态分析结果。第三方工具置于 `artifacts/tooling/firmware-analysis/`，反编译的升级器源码没有作为项目代码分发。

只读检查工具已用上述三份真实升级包验证：版本及两组候选数据识别通过，未知文件保持 unknown，覆盖输入文件的请求被拒绝，检查前后三份输入的 SHA256 不变。验收记录为 `inspection-validation.json`。另通过现有只读状态接口确认后台仍为 `0.17.21`，未安装本次固件功能。上述检查通过的是分析工具，不是永久风扇策略。

## 6. 原版策略的保存位置：本机进一步核对

通过本机注册表实读及已安装 `API_UserScenario.dll` 的静态反编译，确认微星软件使用：

```text
HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario
```

`API_UserScenario.DataCenter.RegString` / `RegStging_Get` 明确指定此路径。本机 2026-10-19 的只读结果：

| 注册表值 | 用途 | 本机值 |
|---|---|---|
| `Default_Temp` | 默认温度节点，两只风扇各六个 | `47;50;57;64;71;78;47;50;57;64;71;78;` |
| `Default_Fan` | 默认占空比，两只风扇各六个 | `60;64;68;74;80;84;60;64;68;74;80;84;` |
| `High_Fan` | 高级模式保存的占空比 | `31;35;38;35;32;33;36;34;37;37;37;39;` |
| `Fan` | 微星软件保存的模式选择 | `3`（高级模式） |

这里的模式和曲线是微星软件的保存值，不能据此判断目前由其他软件管理时的实际风扇状态，也不能把 `High_Fan` 当作原厂高性能预设。

已核对的原版调用链：

1. `Check_Fan()` 读取 EC 版本，与注册表 `ECversion` 比较；版本未变且不是强制检查时直接返回。
2. 需要初始化时，分别读取 `Get_Temperature(1/2)` 和 `Get_Fan(1/2)`；后者取返回数组 `[1..6]`，拼出十二个占空比。
3. 将温度存入 `Default_Temp`，并把读出的曲线同时存入 `Default_Fan` 和 `High_Fan`。因此 `Default_Fan` 的来源是 EC 读数，不是这里硬编码的一组百分比；单独一个注册表快照也不足以保证其从未被覆盖。
4. `setFan(1)` 取 `Default_Fan`，写入两只风扇，再清除自定义位；`setFan(3)` 取 `High_Fan`，写入两只风扇并开启自定义位。

本机 `Default_Fan`、2026-10-12 工具箱保存的 `fan-backup.json` 和 10D 固件两个表的前六字节，均为 `60,64,68,74,80,84`。这提供了三个一致的数据来源；第 7 节补充了加载函数的指令级证据，实机上电测试仍未执行。

三处位置应分清：

- **固件文件中的默认曲线表**：CAP 文件 `0xFCFD` / `0xFD2D`，已追踪到默认表加载函数；实际冷启动仍需验收。
- **EC 当前运行区**：风扇 1 `0x71..0x78`、风扇 2 `0x89..0x90`，包含当前输出及曲线字段；可被软件改写。
- **微星软件的持久设置**：上述 Windows 注册表。保存在磁盘上，需软件读取并下发，不能直接当作 EC 非易失配置。

工具箱自己的原厂备份另存于 `%LOCALAPPDATA%\ClawToolbox\data\fan-backup.json`，仅用于恢复，不是微星固件的策略存储区。本次未修改这些位置的任何设置。新增只读证据为 `msi-fan-registry-readonly.json` 和 `API_UserScenario.DataCenter.decompiled.cs`。

## 7. 三部分可行性：进一步静态逆向及离线验证

| 部分 | 本轮已取得的证据 | 当前边界 |
|---|---|---|
| 修改两路默认曲线 | 已确认实际 ROM 读取地址和运行区目标；内存中替换曲线后，真实加载指令复制了新值 | 离线通过，未写入实机 Flash |
| 启动初始化及模式 | 完整初始化分支写 `0x0D` Auto 后调用上述加载函数，已有静态调用链 | Auto 原始字节无需修改；其余启动/恢复分支及实际冷启动未验收 |
| 校验及写入 | 原文件通过目录签名；修改一字节的副本不能通过原目录签名 | 现有 Windows 官方更新包不能直接复用；无已验证的替代刷写/救援通道 |

### 默认曲线加载函数

这段程序使用 bank 窗口，不能将物理文件偏移直接当成执行地址。本轮将逻辑 `0x4000..0x7FFF` 窗口映射到文件 `0x18000..0x1BFFF`，其余共用区域保持原布局。在此视图中，函数逻辑入口为 `0x5303`（文件偏移 `0x19303`）。相关共享跳板 `0x1D61` 指向 `0x5303`，初始化跳板 `0x1AE1` 指向同一 bank 的 `0x4A65`。

关键步骤已逐条核对：

1. `0x5305` 读取内部 XRAM `0xF4D4`。当 bit 7 和 bit 3 同时为 1 时直接返回，保留自定义运行表。
2. `0x5312..0x5323` 将 ROM 基地址 `0xFCF0` / `0xFD20` 放入两个临时指针。
3. 通过 `MOVC` 读取第一组 `base+0x06+i` 温度数据到 XRAM `0xF469+i`；读取 `base+0x0D+i` 曲线数据到 `0xF472+i`，`i=0..6`。
4. 对第二组执行同样过程，温度目标为 `0xF481+i`，曲线目标为 `0xF48A+i`。
5. 还会复制迟滞、其他温控参数；模拟仅替换两路六个用户曲线字段，保留所有其他 ROM 字节。

因此，六个用户占空比字段的文件偏移范围已确认为 `0xFCFD..0xFD02` 和 `0xFD2D..0xFD32`。原始第七项位于 `0xFD03` / `0xFD33`，值 94，本轮保留。它们对应 WMI 表中跳过当前输出字段后的曲线区域，而不是把当前输出字节当成一个永久低温档。

### 初始化调用链和 Auto 模式

静态追踪到的一条完整初始化路径为：

```text
应用入口 0x1000 -> 0xC724 -> 0xE929
 -> 0x3ECB 的完整初始化分支 -> 0x3F79 调用共享跳板 0x1AE1
 -> bank 内 0x4A65 初始化
 -> 0x4B8D..0x4B92 将 XRAM 0xF4D4 设为 0x0D
 -> 0x4B9E 设置参数 R7=1，0x4BA0 调用默认表加载函数 0x5303
```

`0x0D` 常量本身位于文件偏移 `0x18B91`。实现“使用修改后的出厂表”不需要把它改成 `0x8D`；后者会触发加载函数的自定义表保留分支。完整初始化分支的条件为内部 `XRAM[0x1401] & 0x7F` 不等于 2 或 3；另外两条路径尚未完成全部分析，未把静态追踪冒充为实机冷启动结果。

### 指令级离线验证工具

新增 `tools/Verify-C8exFanRom.py`，只接受 EC 区 SHA256 完全匹配的原版 `1T91EMS1.103` 文件，执行上述函数实际用到的 8051 指令子集。遇到未知指令、超出已审阅的函数/辅助函数、超出指令预算时立即失败，不猜测硬件行为。

它有两条检查路径：

- 参数 `R7=0`：执行加载函数直到返回，验证 ROM 读取地址与每个曲线字段目标逐一对应。
- 参数 `R7=1`（与初始化调用一致）：执行相同加载前缀，停在 `0x55F3 -> 0x77C9` 外设/PWM 调用之前，不模拟风扇硬件。

示例只在内存中替换的曲线为 CPU `40,50,60,70,80,90`、GPU `45,55,65,75,85,90`；两种路径均加载到对应运行区，原温度 `47,50,57,64,71,78,85`、Auto `0x0D`、第七项 94 均保留。这些示例值仅用于验证复制关系，不是经过散热验收的推荐策略，也不是已应用设置。

另逐项扰动十二个曲线字段，均仅改变对应目标；遍历 256 种模式标志验证保留分支；旧版 102 的镜像因哈希不同被拒绝。修改曲线仅存在于内存，不输出刷写镜像；输出文件只有 JSON。

```text
python tools/Verify-C8exFanRom.py <原版10D.zip> --cpu 40,50,60,70,80,90 --gpu 45,55,65,75,85,90 --output <报告.json>
```

### 官方写入路径的实际校验结果

包内 `FLASHWIN.bat` 实际调用 `Pnputil.exe /add-driver E1T91IMS.inf /install /reboot`；INF 绑定本机固件资源 `{7448B592-EDEE-4F18-8C38-27F34462F2AF}`，本机 PnP 资源也与其一致。

使用 Windows SDK 的 SignTool 进行纯离线目录签名验证：

- 原始 CAP 配合原始 `e1t91ims.cat`：退出码 0，验证成功。
- 副本只将 `0xFCFD` 的 60 改为 61：退出码 1，`File not found in the specified catalog`。修改副本已删除，原始文件 SHA256 未变。

因此，改动后继续使用原官方 CAT 的 Windows 更新包不成立。Windows 驱动包目录签名和固件自身验签是两个独立阶段，[微软说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/bringup/certifying-and-signing-the-update-package)明确区分了它们。本轮没有安装驱动包，也没有触发 UpdateCapsule 或重启。

另从 BIOS 中提取了 `IntelMSIInBIOSFlash`、`ReFlash`、`SecSMIFlash` 和 `SecFlashUpdDXE`。内置更新 UI 和固件验证函数均存在，例如 `SecSMIFlash` 的 `Fw_CapValidate` 调用链；但尚未验证其对修改版 EC 区的具体覆盖和接受条件。不能声称 BIOS 内置通道已经可用，也不能仅凭 Windows CAT 失败便断言所有其他通道都不可能。

尚缺：镜像内部完整性规则、同版本 EC 是否会被重写、可用的更新通道、本机完整 Flash 备份和独立恢复手段，以及实际冷启动/跨温度验收。在这些条件补齐前，整体结论仍为未实现，未上线永久写入功能。

新增证据：`rom-loader-simulation.json`、`rom-loader-validation.json`、`mapped-bank3-*.asm`、`catalog-original-verification.log`、`catalog-modified-verification.log`、`catalog-comparison.json`、`update-module-strings.json`、`SecSMIFlash.asm`。所有文件均位于前述本机诊断目录，未分发第三方固件或反编译源码。

## 8. 原 CAT 失败后仍可研究的路径

原 CAT 校验失败只否定“修改 CAP 后继续使用原 CAT”的方案。它不是 BIOS/EC 自身拒绝修改镜像的实验证据。

| 路径 | 能解决的问题 | 本机尚缺的证据 |
|---|---|---|
| BIOS 内置 UEFI BIOS Update | 从 BIOS 入口更新，不经上述 Windows 驱动包 CAT 提交流程 | 已提取对应模块；修改区是否被固件认证覆盖、EC 同版本重写策略以及恢复方式均未验证 |
| 新建并测试签名 Windows 固件驱动包 | 重新为修改后的 INF 和载荷生成 CAT，解决原 CAT 内容哈希不匹配 | 微软文档支持开发测试流程，但本机未建包、未安装证书、未更改启动设置、未提交更新；平台固件认证仍是独立一关 |
| 厂商支持的 EC 更新/配置通道或厂商签名定制包 | 如厂商提供，可按其支持条件处理固件认证及恢复 | 目前未找到本机可用的持久曲线配置/提交接口；不能将普通 WMI 曲线写入当作 Flash 提交 |
| 硬件编程器 | 若实际芯片和写入接口支持，可研究直接读写及恢复 | 需确认实体芯片、供电电压、镜像布局与独立恢复；官方 CAP 不是本机完整备份。直接写入也不能保证改版能通过启动阶段认证 |

微软的 [Authoring an update driver package — Test signing the firmware driver package](https://learn.microsoft.com/en-us/windows-hardware/drivers/bringup/authoring-an-update-driver-package#test-signing-the-firmware-driver-package) 明确给出生成新目录和自签名的测试流程，并要求测试系统安装证书、关闭 Secure Boot、开启 BCD 测试签名。该流程仅适用于测试，不代表本机可以刷入，也不代表固件自身的签名验证被关闭。[Certify and sign the update package](https://learn.microsoft.com/en-us/windows-hardware/drivers/bringup/certifying-and-signing-the-update-package) 明确区分 Windows 目录验证与平台固件验证。

本机随官方包提供的 Claw BIOS/EC 更新指南只说明 `FLASHWIN` 方式，没有给出完整 Flash 备份、独立救援或本机 BIOS 内置更新替代流程。因此，下一步优先离线追踪内置更新模块的 EC 认证范围、校验和及版本判断，同时核实本机备份恢复条件；不能用实际提交或强制刷写代替这些调查。本轮仅补充文档，没有改变系统安全设置、安装驱动或触发固件更新。
