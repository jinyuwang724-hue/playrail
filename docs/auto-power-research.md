# 自动功耗控制：调查与方案

日期：2026-10-11。只读调查，没有改功耗设置。

## 1. 本机芯片

| 项目 | 本机读取 / 资料 |
|---|---|
| 型号 | Intel Arc G3 Extreme（系统报告 `Intel64 Family 6 Model 204`，即 Panther Lake） |
| CPU | 14 核：2 个性能核 + 8 个能效核 + 4 个低功耗能效核，无超线程 |
| GPU | Arc B390，12 个 Xe3 核心 |
| 功耗范围 | Intel 标称可配置 8–35 W；本机寄存器报告基础功耗 25 W |
| 本机现状 | 体感助手设定 PL1 35 W / PL2 36 W（插电） |

芯片特点（影响功耗策略）：
- 它是"GPU 优先"的掌机芯片，游戏里大部分功耗给了显卡。低功耗时 Intel 会让任务尽量跑在能效核上，性能核可以完全不用。
- 评测数据（不同评测设置不同，只能看趋势）：《赛博朋克 2077》1200p 下约 15 W 41 帧、25 W 约 60 帧；30–35 W 配合超分可到 80–95 帧。25 W 以上每多 1 W 换来的帧数明显变少，35 W 时耗电和发热增加更多。
- 结论：**8 W 是下限，15–25 W 是效率最好的区间，25–35 W 适合插电跑 3A 追求帧数。**

一个本机观察：现在没开游戏，封装功耗也有约 24 W，主要是 Steam 大屏幕界面在用显卡（steamwebhelper 占约 52% 显卡）。也就是说，即使在菜单里，限制功耗也能省电。

## 2. 三种实现方式

### A. 按游戏记住（最准）
每个游戏第一次手动调好，之后启动自动切换；退出回到插电/电池预设。
- 优点：结果可预期，不会误判。
- 缺点：每个新游戏要调一次。

### B. 自动识别游戏类型，给初始档位（省事）
游戏启动时判断类型，给一个默认档位，你不满意再改（改了就按 A 记住）。
- Galgame 识别：看游戏目录里的引擎特征文件，例如 KiriKiri 的 `.xp3`、CatSystem2 的 `.int`、BGI、Artemis 的 `.pfs`、Ren'Py、SiglusEngine 的 `.pck` 等；Steam 游戏还可以读商店标签"视觉小说"。你的库里大量 galgame 都是这些引擎，识别率会比较高。
- 大型游戏识别：Unreal / 大体积 Unity 等引擎特征、游戏体积，可信度一般，作为默认值够用。
- 建议默认档位：
  - Galgame：8–10 W（建议 9 W），CPU 限频 2000 MHz
  - 小游戏 / 2D / 独立游戏：12 W
  - 3A：插电 30 W、电池 20 W
  - 未识别：沿用插电/电池预设

### C. 按帧率动态调节（"自动 TDP"，最智能）
设一个目标帧率（比如 60 帧，或跟随游戏/锁帧上限），每秒看一次实际帧率：帧率高于目标就降 1 W，低于目标就升 1 W，始终在你设的上下限之间。
- 优点：任何游戏都适用，3A 在插电和电池下自动找到"刚好够用"的功耗，是最省电的方式。
- 缺点：需要帧率数据（Intel 自带的 PresentMon 或 RTSS，和第 2 批 OSD 用的是同一个数据源）；游戏场景切换时会有一两秒的调整过程。
- 不适合 galgame：这类游戏画面不变时几乎不刷新（例如 CatSystem2 标题画面 0.4 帧），按帧率判断会误以为"太卡"而升功耗。所以 galgame 应该用 B 的固定低档，不走 C。

## 3. 推荐方案

三者组合，按优先级生效：

1. **按游戏记住的设置**（A）：你手动调过的游戏，永远用你的值。
2. **自动识别的类型档位**（B）：没调过的 galgame 和小游戏，直接用低档。
3. **按帧率动态调节**（C）：3A 和未识别的游戏，在"插电上限 / 电池上限"之间按目标帧率自动调。
4. 不在游戏里（大屏幕菜单）：用一个低档，比如 10 W，省掉菜单界面的耗电。

手动模式仍然最优先：你在面板里手动选了功耗，以上自动规则全部暂停。

## 4. 建议分步

- 第一步：A + B + 菜单低档。不依赖帧率数据，可以马上做。
- 第二步：C（按帧率自动调），和第 2 批 OSD 一起做，共用帧率数据源。

## 资料来源
- [Tom's Hardware：Arc G3 芯片规格](https://www.tomshardware.com/video-games/handheld-gaming/intel-challenges-amds-handheld-dominance-with-new-arc-g3-chips-panther-lake-silicon-brings-up-to-14-cores-arc-b390-graphics-to-handhelds)
- [Liliputing：Arc G 系列功耗范围与能效核调度](https://liliputing.com/intel-arc-g-series-chips-bring-panther-lake-graphics-to-handhelds/)
- [Notebookcheck：Arc G3 Extreme 规格与测试](https://notebookcheck.net/Intel-Arc-G3-Extreme-Processor-Benchmarks-and-Specs.1307464.0.html)
- [Digital Citizen：Claw 8 EX AI+ 评测（15 W / 25 W 帧数）](https://www.digitalcitizen.life/msi-claw-8-ex-ai-plus-review-shows-intels-arc-g3-can-seriously-challenge-amd-handhelds/)
- [HotHardware：Claw 8 EX AI+ 评测](https://hothardware.com/reviews/msi-claw-8-ex-ai-review?page=4)
