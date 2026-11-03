# MotionAssistant 1211 逆向与当前工具箱比较

调查日期：2026-10-23，北京时间。针对本机 Claw 8 EX / CG3EM / Intel Arc G3 Extreme，比较工作区 `0.17.53` 与风扇服务源码 `1.0.5`。以下 C# 路径为 ILSpy 反编译结果，并非作者原始源码。

后续进展：用户要求沿用体感助手思路后，已另行实现并部署风扇服务 **1.1.1 / ec_output**，完成有限升温及停止/重启实测。见[直接输出实现与验证](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/docs/fan-direct-output-20261023.md)。本文“当前工具箱”和“尚未修改”的描述保留为最初调查时的 1.0.5 基线，不能用于判断后续实现。

## 结论与取舍

**根据用户本机实际使用反馈，体感助手在“最高约4700RPM、避免反复5800RPM”这个目标上比当前工具箱稳定；我们不能用回读、日志和恢复流程更完整来替代实际控速效果。体感助手的主要其他代码增量是 GPU 频率控制、按功耗查表和基于 GPU 利用率的浮动调频。但本机当前驱动的GPU频率域在两种只读初始化方式下均报告 `canControl=false`，不能直接把这项代码能力算成本机已可用的优势。功耗及 CPU 限频的底层机制与我们基本相同。**

| 项目 | 体感助手 1211 | 当前工具箱 | 判断 |
|---|---|---|---|
| Intel 功耗 | PawnIO 写 MSR 0x610；PL2=PL1+全局差值；整值固定模板 | 同样 PawnIO/MSR；读单位、保留其余字段、检查锁定及回读 | 无需更换底层；我们对原状态的保留和错误可见性更完整 |
| CPU 频率 | Windows 电源方案两类最高频率，AC/DC分开；还联动电源策略和游戏模式 | 同一对频率项，AC/DC分开；主要修改用户指定项 | 核心能力相当；我们修改范围更集中，普通模式的自动恢复仍需补齐 |
| GPU 频率 | Intel Sysman 固定范围、TDP查表、利用率浮动；多平台适配 | Sysman目前只读实际频率/温度/能量 | 代码功能更多，但本机驱动声明不可控；先做能力核验，不能直接承诺锁频有效 |
| 自动优化 | 固定TDP/按电源切换，GPU利用率决定请求MHz | PresentMon真实渲染FPS决定PL1/PL2 | 两者控制对象不同；不能用“有GPU锁频”判断整体更智能 |
| CG3EM风扇 | InpOut直接写两扇输出0x71/0x89；用户反馈最高4700稳定、不出现5800 | MSI WMI写双扇平坦表；曾反复被94档覆盖至约5800 | 体感助手实际限速表现更好；我们的辅助机制较完整，但当前控制路径未可靠满足目标 |
| 性能配置范围 | 多机型、按进程方案、四组风扇预设 | 当前功耗模块主要是全局/电源状态，三组可编辑风扇预设 | 体感助手的配置覆盖面更广；本次不比较其他手柄/体感功能 |

以上结合静态实现、我们已有遥测和用户本机反馈。**4700限速稳定性已有用户使用观察，评价应承认体感助手在该目标上的优势；尚未进行统一场景的帧时间、耗电、绝对温度或声压对照，不扩展成这些指标的全面胜负。**

## 1. 分析对象、REA 与证据边界

用户提供的 MSI 用只读 Windows Installer 数据库接口读取，提取内嵌 CAB；未调用安装流程或自定义安装动作。MSI `File` 表列出94个文件，本次主分析对象是其中的 MotionAssistant.exe 及其嵌入资源。

| 身份 | 结果 |
|---|---|
| MSI | `C:/Users/13304/Downloads/MotionAssistant_Setup_1211.msi`，55,239,168字节 |
| MSI SHA256 | `2f91522dc1aa3d9a920b6e5649d2c6cc7ee5ab469af4c6ec0c074eb2894b143d` |
| MSI属性 | ProductName=Motion Assistant；ProductVersion=1.2.11；Manufacturer=Frank Dong |
| EXE文件版本 | 1.2.1.0；程序集版本1.0.0.0。文件名1211、安装器版本和界面显示版本应分开记录 |
| EXE SHA256 | `8c2d07f1a52230f5e2cc993f731121e7b72155d1f97ec0f3b7ddc71db4e85339` |
| 托管身份 | .NET Framework 4.8，CIL，AnyCPU；PE容器machine字段为x86，不能据此认定进程必须32位 |
| MVID | `8b6487b7-573e-4339-b943-125330f8786d` |

查看了 [REA项目](https://github.com/chengyixu/rea)及[托管分析说明](https://github.com/chengyixu/rea/blob/dce81ca400eda369fe65b3de6e5a9aef4dfdaa50/docs/managed-code-analysis.md)，并实际使用 **REA CLI 6.7.0** 完成程序集身份、成员/CIL和原生调用边界三项静态检查。当前会话没有REA MCP注册，直接使用CLI即可；未修改Codex配置。ILSpy 9.1.0.7988提供单独的C#重建结果。

REA读取到288个类型、2,281个方法、2,944个字段和245条P/Invoke声明，三份结果均报告覆盖完整。这里的“完整”指这些静态解析操作，**不代表所有功能均已实测，也不代表245个原生接口均成功执行**。关键方法的token、原始IL摘要和调用声明保存在[证据索引](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/evidence-ledger.json)，输入及比较源码摘要见[文件哈希记录](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/input-and-source-hashes.json)。

证据ID：

- 程序集身份：`ev_b8854e0c307cb1f9c3bb3bf933dbcf8072b4db6fae6579bc3bf38f662eaa7ae5`。
- 成员/CIL：`ev_679ed5adc6e548abc5e50738901871e9e514022d82e96ee323a27e3744946914`。
- 原生调用声明：`ev_119e1e3802f3f88280a9e509dc5d7b68b819a7beb9205756b16b62ee6a499898`。

未运行目标EXE、嵌入DLL或驱动，未写入硬件、安装服务、改动功耗/风扇配置。补充使用独立小程序调用已安装Intel显卡驱动的只读能力/范围/状态API，不包含任何setter声明。对当前风扇服务只检查注册信息及文件身份：ClawFanService正在运行；工作区源码版本不与整个已部署工具箱的版本自动等同。

## 2. 功耗：同一条PawnIO/MSR通路，写入策略不同

体感助手的Intel调用链是：设置TDP → 一秒防抖定时器 → `TDPControl.setTDP` → `IntelMSR.ChangeTDP` → PawnIO `ioctl_write_msr` → **MSR 0x610**。

- 加载嵌入的 `IntelMSR.bin`，通过 `PawnIOLib.dll` 与驱动通信。
- PL1为用户设置值；PL2为PL1加 `IntelPL2Delta`，默认1W。
- 瓦数固定乘8换成寄存器数值，假定0.125W单位。
- `BuildMSRValue` 用固定高低32位模板拼出整个64位值；没有先读原值并保留其他字段。写入函数检查PawnIO错误，但这条设置链没有立即回读PL1/PL2确认。
- 每约两轮监控发现实际封装功耗高于目标2W时，重写同一个目标；约31轮时若实际功耗大于10W且低于目标5W，也重写。实际耗电偏离上限并不能单独证明上限被覆盖，此策略可能产生多余写入。
- 睡眠恢复会重应用目标，插拔电源后约五个主循环再切换AC/DC目标。这不是根据目标帧率改变TDP的控制器。

证据：[IntelMSR](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/IntelMSR.cs:119)、[TDP入口](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/TDPControl.cs:985)、[延迟写入](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/Form1.cs:10007)、[偏差后重写](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/Form1.cs:7770)。

我们同样使用PawnIO/MSR，但读取 **0x606功率单位**，读取0x610锁定位，只更新PL1/PL2功率字段和enable位，保留时间窗、clamp等其余内容，随后返回回读结果。产品层限定PL1为8–35W；PL2加成0–15W。底层通用helper允许的范围比产品界面更宽，不能混为同一限制。

证据：[当前功耗写入](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/ClawPower.cs:288)。判断：当前设计更适合保持本机已有固件参数；两边都不能仅凭寄存器回读保证实际耗电达到设定瓦数，也都没有因此取得突破额外固件限制的能力。

AMD分支走PawnIO `RyzenSMU.bin`及SMU mailbox，能力覆盖更多AMD家族；这是体感助手多平台适配优势，与本机Intel路径分开评估。

## 3. CPU：都是Windows最高频率限制，体感助手还改其他策略

体感助手通过 `powrprof.dll` 设置当前电源方案的 `75b0ae3f-...-e100/e101` 两项最高频率，分别AC/DC，0表示不限频。**这是最高频率限制，不能理解为让CPU始终运行在一个固定MHz。**

它只在正在插电时写AC、正在用电池时写DC，并缓存上次请求值。修改另一个电源状态的设置可能要等切换电源后才实际写入，这与此前配置显示2750但电池方案仍为0的记录相符，但不能据此单独判定当时故障原因。

其额外动作包括：显示隐藏的最高频率设置、关闭Windows Game Mode、依据频率和核心策略修改电源方案personality、设置最大处理器状态100%，限频时设置最小状态5%，重新应用方案。第一项频率写入返回值仅记录到日志，第二类频率项没有同等检查，也没有设置后读取四项进行严格比对。

证据：[AC/DC实现](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/Common.cs:886)。我们写同一组AC/DC最高频率项，但不在这条链上顺带关闭Game Mode或改personality，修改范围更集中。[当前CPU实现](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/ClawPower.cs:319)。

“CPU优化”正常界面只对AMD MAX/395一类启用：按TDP查CPU最高频率表。**本包没有为Intel G3E正常启用这项功能，不能称为已实现本机CPU/GPU联合实时优化。** INI仍接受相应布尔值，手改配置可能进入通用逻辑并使用唯一的AMD395表，这也不构成正式G3E适配。[界面与G3分支](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/Form1.cs:1575)、[CPU查表](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/Common.cs:1199)。

恢复方面不能只批评体感助手：其退出链未见恢复进入前的TDP/CPU频率；**我们普通手动/按电源模式关闭接管或关闭后台时，也没有自动恢复全部原始值**，依赖显式恢复，且CPU恢复受首次备份电源方案限制。我们AutoTDP的跨方案journal恢复较完整，应该将这种机制补到普通模式。[当前关闭/恢复](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/plugin/claw_power.py:1315)、[AutoTDP会话](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/plugin/claw_autotdp.py:113)。

## 4. GPU：体感助手最值得借鉴的部分

Intel实际调用链为：`SetGPUClock` → `TDPControl.setGFXClock` → `IntelGPU.SetGPURange` → **`ze_loader.dll / zesFrequencySetRange`**，把范围的min/max都设为目标MHz。保留的 `IntelKx` 类虽有旧的KX.exe/MCHBAR写法，但本次完整反编译搜索未见其外部调用，不能将其当成当前工作路径。

这套实现有三种用法：

1. 手动固定：保存、重应用一个请求频率，按钮以50MHz步进。
2. Balanced：按照CPU型号和TDP查表，固定到一个目标频率。
3. Auto：在TDP对应范围内，按平滑后的GPU利用率升降请求频率。

G3E分支有专门表，手动上限2300MHz。例：10W→1100MHz，15W→1400MHz，20W→1750MHz，25W→2100MHz，29W及以上→2300MHz。这些是作者预设，**不是这台机器经本次测试得到的最优值**。

Auto默认下限600MHz、上限为上述TDP表，可配置范围。Intel利用率≤70%或估算空余频率≥240MHz连续四轮后降50MHz；利用率≥94%或空余频率<140MHz时通常立即升200MHz，并有100%利用率触发的额外快升路径。主循环约每秒一次，实际周期受其他工作耗时影响。算法使用利用率及请求频率估算余量，未用目标FPS、实际MHz、帧时间或CPU瓶颈共同决策。

**它有功能，但设置是否成功缺少可靠核验：**

- 初始化直接取第一个driver、device和frequency domain，没有按GPU类型及 `canControl` 选域。
- `zesFrequencySetRange` 返回码被丢弃；`zesFrequencyGetRange`仅声明、没有实际调用。
- UI把请求MHz写到 `CurGPU` 并显示，不能据此证明驱动接受或实际时钟达到该值。
- Intel解除锁频请求100..9999MHz，未保存并恢复原始范围；退出链未见GPU恢复调用。

Level Zero规范提供域类型、`canControl`、支持范围、错误码及回读API；也定义min/max均为-1时请求默认范围。这些能力可以用于补足实现，但设备和当前驱动是否允许控制仍需验证。[官方频率规范](https://oneapi-src.github.io/level-zero-spec/level-zero/1.19.12/sysman/api/apis/frequency.html)。

证据：[IntelGPU接口](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro.TDP/IntelGPU.cs:38)、[G3E分支](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/Form1.cs:1797)、[GPU利用率调节](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/Form1.cs:7614)、[详细GPU调查笔记](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/gpu-findings.md)。

我们已经使用同一Sysman接口读取GPU实际频率、温度和能量，并按GPU域类型选择，但还没有写频率范围的接口。增加控制不需要移植其旧KX驱动方案。[当前Sysman读取](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/ClawPower.cs:871)。目前用户界面的Intel智能模式已经移除，IGCL Endurance代码主要用于控制权交接和旧会话恢复，不能算当前仍可用的自动优化模式。

### 本机GPU能力的只读补查

2026-10-23 09:17（北京时间），独立探针只调用初始化、枚举、GetProperties/GetRange/GetState。按官方头文件核对x64布局，`ze_bool_t`为uint8，保留完整属性原始字节；没有导入任何Set接口。

- Windows报告主GPU为 **Intel Arc B390**，驱动 **32.0.101.9034**；系统 `ze_loader.dll` 版本1.32.0。
- 工具箱使用的独立Sysman方式，以及体感助手使用的 `zeInit + ZES_ENABLE_SYSMAN=1` 方式，首个driver/device的GPU域都返回 `canControl=false`。
- 两条通路的GPU硬件范围、当前范围均为 **100–2300MHz**，当次实际频率约1000MHz；这些是瞬时只读状态，不能作为性能测试。
- 旧初始化方式还枚举到其他driver，但体感助手本身直接使用第一个。两种通路的结果不能混在一起挑选有利的域。

因此当前只能说：**驱动未声明该域支持软件调频；体感助手的枚举初始化可能成功，但它没有检查这个可控标志，也不核对后续写入。** 本次没有调用SetRange，未取得“写入被接受/拒绝”的运行结果，不能进一步断言绝对不可能写成功。产品不应默认把此机器展示成锁频可用。

证据：[独立Sysman结果](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/gpu-capabilities-sysman.json)、[体感助手初始化方式的结果](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/gpu-capabilities-legacy.json)、[只读探针源码](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/GpuReadOnlyProbe.cs)。

## 5. 风扇：体感助手直接覆盖输出，我们的软件服务写表

### 用户实机反馈修正

用户补充：体感助手设置约4700RPM后，最高稳定在约4700，没有我们时不时出现的约5800RPM及高温保护/恢复争抢。这是用户对同一台机器的实际使用观察，应高于仅从代码形式推导的控速优劣。此前把“我们的回读、故障处理更完整”泛化为“风扇方案更好”，结论过度，现予修正。

目前应区分三件事：体感助手实际执行所设转速更稳定；它的直接输出写法是值得优先验证的差异；稳定转速的完整因果关系仍未由同场景API/EC状态采样锁定。不能因为它没有我们的恢复状态机，就把用户稳定使用的结果解释为不可靠，也不能将其表现说成已验证了EC非平坦自主曲线。

本包在MSI制造商分支精确识别 `Claw 8 EX AI+ CG3EM Launch Pack`，有专门CG3EM路径；不能用旧Claw或AMD A8分支替代。[型号识别](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/MotherBoardInfo.cs:342)。

CG3EM实际路径：软件读取CPU温度 → 固定温区插值/缓变 → `InpOutx64.dll`端口访问 → **EC命令/数据口0x66/0x62 → 当前风扇输出0x71/0x89**。两个风扇写相同值，每次重复三轮、每轮等待333ms，再加温控循环1000ms等待，约两秒一轮加I/O耗时。

- 温控轴固定45/60/70/80/90°C，90°C端固定100；实际写值夹到1–100，0最终也写1。
- 温度更新约两秒；升温差超过3°C时有逐轮缓变，下降没有对称持续性判断。UI延迟不是精确秒数。
- EC写入失败或超时可能静默返回；没有目标输出回读验证。
- RPM读取两扇计数后向上层提供平均值，没有像我们一样分别保留两路遥测。

最需要注意的是**自动恢复**：`SetFanAuto()` 在CG3EM分支没有硬件写入，`SetFanSpeedAutoClaw8()`为只含ret的空方法（REA token `0x0600041a`，IL长度1）。退出、禁用自定义和异常保护会转到这一逻辑，能证明停止持续写入，不能证明明确恢复原厂控制位/表，也不能从静态代码判断EC自然接管的时序。

证据：[直接输出写入](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/FanControl.cs:1218)、[Auto分支](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/FanControl.cs:467)、[空恢复方法](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/decompiled/Gyro/FanControl.cs:1214)、[详细风扇调查笔记](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/motion-research-20261023/fan-findings.md)。

我们的独立Windows服务使用MSI WMI：计算一个实时风量，填平两扇各六个点，设置自定义位；包含温度平滑、升降等待、异常温度处理、回读/事务回滚、双扇独立遥测、睡眠和服务停止交还。它仍是**软件持续温控**，本机EC自主执行非平坦自定义曲线此前验收没有通过。[当前服务](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/ClawFanService.cs:93)、[底层事务及恢复](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/native/ClawFanHardware.cs:237)。

进一步核对的关键差异：我们在 `TablePayload` 中明确保留当前输出字段，只改六点表及可选的第8档，并开启0xD4自定义位；体感助手CG3EM路径持续直接写当前输出0x71/0x89，未在该路径设置0xD4。我们通常只在目标/表/控制位变化时写，体感助手即便目标不变也持续重写。因此我们的表回读一致，仍可能伴随实际输出94；降低表中ceiling也不等于立即解除已锁存的实际高输出。**控制对象、EC模式和持续写入节奏都不同，是解释实际稳定性差异的优先假设，不能仅靠继续调整温度阈值或增加恢复状态来替代验证。** 现有证据没有将差异归因到其中唯一一个因素。

当前4700策略还存在一条可能加剧切换的路径：降到ceiling65仍超速时抛出异常，Tick捕获后Release；Restore会恢复原厂第8档94，下一轮又尝试自定义限速。这是源码中可达的路径，不能认定所有5800事件都由它引起；之前已有无Auto回退仍升94的实机记录。它说明“限速失败即交还→再次接管”的处理方式也要与底层控制方式一起重审。

**当前风扇的未解决边界必须保留：**1.0.5源码新增4700RPM策略并调整第8档ceiling，已不同于1.0.4“不改94”的旧文档；现有部署记录的passed仅说明部署/配置，记录after仍有5854RPM、实际输出94、目标71、ceiling74且处于settling。不能把它解释成4700RPM实机封顶验收通过。[部署记录](C:/Users/13304/Desktop/Claw-Playhub-ButtonRouter-Handoff/diagnostics/fan-rpm-ceiling-20261022/deployment.json:491)。用户已报告体感助手限速稳定，后续应针对该效果复核其输出通路及模式，而不继续将“EC保护不可控”作为我们达不到目标的充分解释。

## 6. 建议及调查完成度

建议保留现有功耗、CPU限频和独立后台的组织方式；风扇底层控制通路需要重新评估，不能因已有辅助机制而保留无效的执行策略。改进方向如下：

1. **优先补GPU能力显示及设置核验，再决定是否开放写入。** 本机目前 `canControl=false`，应显示当前驱动未声明支持，不能直接照搬锁频控件。后续只有在支持边界明确时，再做原范围备份、返回码处理、请求范围与实际MHz回读、失败恢复和控制权互斥；验证通过后再考虑利用率或帧时间驱动的浮动调节。
2. **补齐普通手动/按电源模式的恢复流程。** 复用AutoTDP的持久journal思想，覆盖方案切换和异常退出，避免只比较别人缺点。
3. **GPU与AutoTDP逐步协调。** 两个独立反馈循环同时改功耗和MHz容易互相影响；先单独测量，再决定如何结合CPU/GPU瓶颈信号。不要直接把体感助手G3E查表值当作最佳曲线。
4. **风扇优先查明直接输出通路为何更稳定。** 以用户已观察到的4700稳定效果为目标，对照当前输出寄存器、0xD4模式及周期性写入，验证可用的控制方式，再保留必要的遥测和故障处理。现有“表格写入成功→输出升94→等待冷却再恢复”链条不应作为达到限速目标的长期方案。本次尚未改动产品或在线切换硬件路径。

本次完成：REA项目查看与CLI静态证据、安装包身份和主程序提取、四项功能从入口到硬件接口的追踪、当前源码对照、主要恢复/校验缺口，以及本机GPU域能力和范围的只读补查。尚未验证：体感助手附带的底层驱动加载、Intel GPU写入是否接受、直接EC输出的实机响应、两套软件同场景的性能/能耗/噪声差异。本次仅形成调查报告和独立只读诊断材料，未修改产品实现或硬件设置。
