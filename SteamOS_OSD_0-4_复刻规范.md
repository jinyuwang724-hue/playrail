# SteamOS 游戏模式性能 OSD（L0–L4）复刻参考

> **用途**：为 Windows 掌机上的 Intel PresentMon OSD 二次开发提供 Steam Deck 经典布局的字段、结构和配色参考。  
> **资料版本**：根据 2023-03-23 发布的 Steam Deck MangoHud 预设提取记录及其 L1–L4 实机截图整理。  
> **重要边界**：这是可核对的 **2023 年经典 SteamOS 样式**，不是已验证的 2026 年最新 SteamOS 全版本逐像素规范；MangoHud 上游默认配色不等同于所有 SteamOS 版本的运行时最终色值。

## 1. L0–L4 性能字段对照表

标记说明：**✓** = 2023 年相应档位实机截图中可见；**△** = 预设配置涉及该类信息，或需特定环境／设备才可能出现；**—** = 该截图未显示，且不是该档位的典型字段。为了尽量准确，以下以**截图可见结果**优先，而不是把 MangoHud 可支持的所有选项都算进去。

| 监控项目 | L0 | L1 | L2 | L3 | L4 |
|---|:---:|:---:|:---:|:---:|:---:|
| FPS 数字 | — | ✓ | ✓ | ✓ | ✓ |
| 帧时间数字（ms） | — | — | — | ✓ | ✓ |
| 帧时间曲线 | — | — | ✓ | ✓ | ✓ |
| 帧时间最小／最大值 | — | — | — | ✓ | ✓ |
| GPU 使用率（%） | — | — | ✓ | ✓ | ✓ |
| GPU 功耗（W） | — | — | ✓ | ✓ | ✓ |
| GPU 温度（°C） | — | — | — | ✓ | ✓ |
| GPU 频率（MHz） | — | — | — | — | ✓ |
| CPU 总使用率（%） | — | — | ✓ | ✓ | ✓ |
| CPU 功耗（W） | — | — | ✓ | ✓ | ✓ |
| CPU 温度（°C） | — | — | — | ✓ | ✓ |
| CPU 频率（MHz） | — | — | — | — | ✓ |
| CPU 各逻辑核心使用率 | — | — | — | — | ✓ |
| CPU 各逻辑核心频率 | — | — | — | — | ✓ |
| RAM 使用量 | — | — | ✓ | ✓ | ✓ |
| VRAM 使用量 | — | — | — | ✓ | ✓ |
| 电池电量（%） | — | — | ✓ | ✓ | ✓ |
| 充电／外接电源图标 | — | — | ✓ | ✓ | ✓ |
| 预计剩余续航时间 | — | — | — | △ | △ |
| 风扇转速（RPM） | — | — | — | — | ✓ |
| FSR 状态、锐度 | — | — | — | — | ✓ |
| 帧计数（Frame Count）图表 | — | — | — | — | ✓ |
| 游戏帧时间（App）图表 | — | — | — | — | ✓ |
| 系统时间／版本信息 | — | — | — | — | ✓ |
| GPU 名称、I/O、Wine 等环境字段 | — | — | — | △ | △ |

**注意**：表中的 **GAMESCOPE 60 FPS** 是各截图中出现的 FPS 显示标签，不代表独立于其他 FPS 的第二套计数器。L4 的 CPU 各核心信息为**逐行文字及数值**，不是柱状图。

## 2. 每档布局与原始截图

| 档位 | 排版 | 典型位置 | 字段组织 | 图表 | 原始截图 |
|---|---|---|---|---|---|
| **L0 关闭** | 完全隐藏 | 无 | `no_display` | 无 | 无 |
| **L1 极简** | 单个 FPS 数字，无单位标签 | 左上角 | 截图仅见白色 `60` | 无 | [L1](https://dbeley.ovh/images/steam-deck/gaming_1.jpg) |
| **L2 横条** | 单行横向工具条 | 屏幕顶端 | `BATT → GPU → CPU → RAM → GAMESCOPE → 绿色帧时间图` | 一条微型绿色折线 | [L2](https://dbeley.ovh/images/steam-deck/gaming_2.jpg) |
| **L3 标准** | 左上角紧凑纵向字段表 | 左上角 | GPU、CPU、VRAM、RAM、BATT、GAMESCOPE；底部 Frametime 图表与 Min/Max | 帧时间折线 | [L3](https://dbeley.ovh/images/steam-deck/gaming_3.jpg) |
| **L4 详细** | 左上角高密度纵向字段表 | 左上角 | 顶部时间与版本；GPU/CPU、CPU0–CPU7、VRAM、RAM、BATT、FAN、FSR、GAMESCOPE；底部图表 | 帧时间及 Frame Count/App 区域 | [L4](https://dbeley.ovh/images/steam-deck/gaming_4.jpg) |

### 实机示意（直接引用原始截图）

**L1**

![Steam Deck L1 实机截图](https://dbeley.ovh/images/steam-deck/gaming_1.jpg)

**L2**

![Steam Deck L2 实机截图](https://dbeley.ovh/images/steam-deck/gaming_2.jpg)

**L3**

![Steam Deck L3 实机截图](https://dbeley.ovh/images/steam-deck/gaming_3.jpg)

**L4**

![Steam Deck L4 实机截图](https://dbeley.ovh/images/steam-deck/gaming_4.jpg)

## 3. 配色参考

下表是 **MangoHud 上游示例配置文件中明确列出的颜色值**，可直接用作高保真复刻的初始调色板。它们不是从上述 Steam Deck 实机截图逐像素抽样得出的认证值：实际画面受版本、背景、Alpha 混合、HDR/色彩管理影响。

| 类型 | 颜色 | HEX | 说明 |
|---|---|---|---|
| 普通数值／文字 | 白色 | `#FFFFFF` | L1 的 FPS 亦为白色 |
| GPU 标签 | 绿色 | `#2E9762` | 例如 `GPU` |
| CPU 标签 | 蓝色 | `#2E97CB` | 例如 `CPU`、CPU 核心项目 |
| VRAM 标签 | 紫色 | `#AD64C1` | `VRAM` |
| RAM 标签 | 粉紫 | `#C26693` | `RAM` |
| 电池标签 | 珊瑚橙 | `#FF9078` | `BATT` |
| 引擎／GAMESCOPE 标签 | 珊瑚红 | `#EB5B5B` | `GAMESCOPE` |
| I/O 标签 | 浅紫 | `#A491D3` | 仅在相应项出现时 |
| 帧时间折线 | 绿色 | `#00FF00` | 曲线线条 |
| 背景基色 | 近黑色 | `#020202` | 与背景透明度一起使用 |
| 文字描边 | 黑色 | `#000000` | 提高游戏画面上的可读性 |
| 横向分隔条 | 紫色 | `#AD64C1` | L2 项目间细竖线的初始参考 |

**负载动态颜色**：L4 截图中 GPU 使用率呈橙红、CPU 和各核心负载部分数值呈绿色，说明高负载档位存在动态或状态化上色；其**精确阈值与运行时规则没有在本次提取的 2023 档位文本里得到逐项确认**，不要写死为已知官方阈值。

## 4. 字体、背景、细节

| 项目 | 2023 实机截图 / 上游配置可确认的情况 | 复刻时的处理 |
|---|---|---|
| 字体名称 | 抽取的 0–4 档预设**未指定具体 `font_file`**，无法据此确认准确字体名 | 优先匹配实际截图字形，字体可先用具有紧凑数字字形的等宽字体试配，不能标注为“官方原字体” |
| 字形风格 | 白色紧凑文字、数字对齐明显 | 采用清晰、紧凑、支持单位上标的字体 |
| 字体描边 | MangoHud 示例启用 `text_outline` | 黑色描边可参考 1.5 的上游示例参数，实际效果需实机校准 |
| 背景透明度 | MangoHud 上游示例注释值为 `background_alpha=0.5` | 不等同于 SteamOS 每档经过验证的实际 Alpha；请以截图校准 |
| 背景形状 | 经典截图倾向简洁直角／极少圆角 | 不要套用现代玻璃拟态或大圆角卡片 |
| L1 排版 | 白色数字，不显示 `FPS` 字样 | 不要自动加 `FPS` 标签或彩色背景 |
| L2 排版 | 横向单行，最右侧绿色图线 | 不换行成多行卡片 |
| L3 排版 | 列表加帧时间图、Min/Max | 保留数据的列对齐方式 |
| L4 排版 | 逐核心文本、频率、风扇、FSR、两段图表 | 不擅自改成 CPU 柱状图 |

## 5. 2023 年提取的 MangoHud 预设要点

以下仅列出**最影响档位差异的配置项**；完整原文以来源页面为准。

| 档位 | 配置关键词（节选） | 意义 |
|---|---|---|
| L0 | `no_display` | 不显示 |
| L1 | `fps_only`, `fps=1`, `frame_timing=0`, `cpu_stats=0`, `gpu_stats=0`, `width=40` | 纯 FPS |
| L2 | `horizontal`, `battery`, `gpu_stats`, `cpu_stats`, `cpu_power`, `gpu_power`, `ram`, `fps`, `frame_timing=1`, `frametime=0` | 单行横条＋帧时间曲线，**不显示帧时间数值** |
| L3 | `cpu_temp`, `gpu_temp`, `ram`, `vram`, `cpu_power`, `gpu_power`, `frametime`, `battery` | 纵向信息表，温度、显存和帧时间数值 |
| L4 | `full` 加上 L3 的相关字段 | 最完整信息，包含逐核心等扩展内容 |

原始提取还在各预设使用了 `fsr_steam_sharpness`、`nis_steam_sharpness` 这类 Gamescope 相关设置：它们不能原样映射到 Windows PresentMon。L3/L4 还设置了 `io_read`、`io_write`、`arch`、`gpu_name`、`wine`，但截图中不一定逐项出现。

## 6. PresentMon 二开复刻边界

- **忠实复刻**：显示档位、标签顺序、字段有无、对齐方式、颜色分工、L2 横条和 L3/L4 纵向布局。
- **Windows 替代项**：`GAMESCOPE` 标签及 FSR/Gamescope 信息属于 SteamOS 环境。在 Windows 上如果不是运行 Gamescope，不应伪造其底层指标；可以独立定义 PresentMon FPS 与其他可真实获取的指标。
- **动态能力识别**：GPU 功耗、风扇 RPM、频率、电池状态等依赖设备与驱动，不可用时隐藏或标注 N/A。
- **机型泛化**：不能把 Steam Deck 的 8 线程核心行数、FSR 标志、风扇接口写死到 MSI Claw 8 EX、ROG Ally 或 Legion Go。
- **颜色真实性**：上述 HEX 是 MangoHud 上游初始参考，若目标是“像素级复刻”，还应按**指定 SteamOS 版本、屏幕亮度、色彩空间和采集方式**获取未经修图的截图，进行对色与字体度量校准。

## 7. 参考来源

1. [The MangoHud presets used by the Steam Deck（dbeley，2023-03-23）](https://dbeley.ovh/en/post/2023/03/23/the-mangohud-presets-used-by-the-steam-deck/) — 从 Steam Deck `/tmp/mangohud.XXXXXXXX` 提取的 0–4 档配置与实机截图。
2. [MangoHud 上游配置文件（GitHub）](https://github.com/flightlessmango/MangoHud/blob/master/data/MangoHud.conf) — 颜色配置、字体、背景 Alpha、图表、性能字段的选项定义。**注意：该文件是持续更新的 master 版本，不是 2023 SteamOS 的锁定配置快照。**
3. [Steam Deck L1](https://dbeley.ovh/images/steam-deck/gaming_1.jpg) · [L2](https://dbeley.ovh/images/steam-deck/gaming_2.jpg) · [L3](https://dbeley.ovh/images/steam-deck/gaming_3.jpg) · [L4](https://dbeley.ovh/images/steam-deck/gaming_4.jpg) — 可直接核对标签顺序与显示布局。

---

**结论**：经典 SteamOS OSD 的核心不是仅仅把字段越堆越多，而是用 **L1 单个白色 FPS 数字 → L2 顶部横向信息条 → L3 左上角纵向状态表＋帧时间曲线 → L4 完整逐核心文字监控与调试图表** 的阶梯式布局。如果以 PresentMon 二开，优先按这四种不同布局分别建立模板，不要只依赖同一列表的字段开关。
