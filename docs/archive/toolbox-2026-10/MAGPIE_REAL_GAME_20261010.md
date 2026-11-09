# Magpie 0.11.0：真实游戏修复与效果扩展

2026-10-10，测试游戏《常轨脱离 Creative - Ver1.06》，Steam App ID 1604380，文件 `D:\SteamLibrary\steamapps\common\常轨脱离Creative\hamidashi.exe`。本机 Intel Arc B390、1920×1200、150% DPI、SDR。测试停留在标题画面，没有加载存档或推进剧情。

## 失败原因与修复

此前用户失败记录明确包含 `Magpie管理员: 否` 和 `SrcTracker.cpp:83 不支持缩放 IL 更高的窗口`。游戏权限比 Decky 启动的 Magpie 高，核心拒绝捕获；首版自有窗口测试没有覆盖这一路径。

现在插件读取目标进程和自身的 TokenElevation，只在目标权限更高时以系统 RunAs 启动固定相邻的 Magpie.exe。普通 Decky 保持普通权限。MagpieLauncher 使用随机、带 ACL 的命名管道；双方分别核验服务端／客户端 PID。原生端使用独立重叠读写，避免双向同步管道在等响应时死锁。父进程关闭输入后原生后台正常退出。

本轮分别测试普通 Steam 启动与单次 RunAs 启动真实游戏。普通窗口的 engineElevated=false；管理员窗口的 engineElevated=true，均出现可见的 1920×1200 输出。没有调整 Steam 的持久启动权限、UAC、系统代理或睡眠设置。

## 接入的原版功能

- 原版 134 个效果的完整可选目录、参数范围、默认值和步长，数据由编译后的 Magpie 导出。
- 七个默认方案：Lanczos、FSR、FSRCNNX、CuNNy、Anime4K、CRT-Geom、Integer Scale 2x。CRT 默认方案中过期的 interlace 参数按当前着色器 schema 移除。
- 自定义效果链：添加、替换、移除、上移，最多 12 个效果；各效果参数可直接调整；可追加 RCAS。
- Graphics Capture、Desktop Duplication、GDI、DWM Shared Surface 四种捕获方式；处理帧率上限、3D 游戏模式、鼠标速度随缩放调整、手动黑边裁剪。
- 效果依次执行，最后统一等比适配屏幕。整数 2 倍及固定倍数神经网络方案也经过最终适配，因此最终像素倍率不是原版的严格整数倍率。
- 读取设置不会应用；未编辑草稿跟随后台状态，编辑中的草稿在刷新和菜单重挂载后保留。
- 实际 Steam 大屏幕界面验证自定义方案和效果下拉框；关闭下拉框后高级编辑区保持展开，方案与参数草稿保留，不触发应用。

## 实测证据

证据目录 `artifacts/magpie-real-game`：`states.jsonl`、实际截图、`native-game.log`、`native-elevated-game.log`、`pixel-comparison.json`。截图通过 Computer Use 获取，未用网页截图模型或模拟窗口代替真实游戏。

- Steam 启动后识别到 runningAppID=1604380 和匹配的原生 GamepadUI 覆盖浏览器。
- 最大化游戏客户区为 1920×1130，存在游戏内部黑边。本次上下每边手动裁 25 像素，加上等比铺满自动左右各裁 96，实际源 1728×1080 → 1920×1200，宽高比均为 1.6。25 像素仅适用于本次窗口尺寸，普通 1280×720 窗口无需照搬。
- Bicubic、RCAS、FSR、Anime4K 均在该真实游戏输出。原生日志记录对应着色器编译、Graphics Capture 初始化及目标矩形。
- 同一画面下方静态衣服区域 `[250,400,1000,740]` 排除顶部动态 RTSS；相对 Bicubic 的截图 RGB 平均绝对差：RCAS 约 4.78/4.74/4.77，FSR 约 4.94/4.92/4.94，Anime4K 约 3.03/2.99/3.06。截图经过工具的显示缩放；这些数值确认画面变化，不能当画质或性能评分。
- 实际打开 Steam 覆盖菜单后 active=false、paused=true；关闭并返回游戏后同一 PID 16336 恢复。管理员窗口另测暂停／恢复，PID 30284 保持不变。
- 后台与前端 50 项针对性检查通过（19 Python、31 Node），覆盖原版方案参数、无效效果／参数拒绝、只读、草稿保留、重复应用、菜单路由和重载边界。
- 测试结束恢复原先简洁方案、50% 锐化、零手动裁剪，关闭画面后台；管理员游戏留在标题画面，供用户自行复测。

## 仍未接入或未逐项测试

原版触控辅助、按游戏自动配置、方案导入导出、工具栏和开发调试选项尚未接入。134 个效果都可选，但本次只逐项实测 Bicubic、RCAS、FSR、Anime4K；其他效果的画质、性能及各捕获方式兼容性不能由这些结果推断。HDR、独占全屏及触控未验证。

## 构建与分发

沿用 [首版编译环境说明](MAGPIE_IMPLEMENTATION_20261009.md)。新增 `native/MagpieLauncher.cs`、`tools/magpie-catalog.py`。

```powershell
./build-magpie.ps1
python tools/magpie-catalog.py
./build.ps1 -FrontendOnly
python tools/package-magpie-source.py
./install.ps1
./package.ps1
```

原生按键路由代码本轮未变；0.11.0 使用 0.10.0 同字节的版本化路由可执行文件。安装包附 GPL 许可、完整修改后上游源代码、桥接源码及效果原文件。后台升级前复制已关闭的自有运行目录，不覆盖其他 Magpie 安装。
