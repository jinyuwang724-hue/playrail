# Intel 驱动限帧替代 RTSS（2026-10-27）

按用户要求，限帧已从 RTSS 切换到 Intel IGCL 官方 `CTL_3D_FEATURE_FRAME_LIMIT`。
当前已部署 ClawPower `0.17.58-r29`，保留现有 OSD、核心设置、睡眠动画及其他后台更新。
本次未卸载 RTSS、PawnIO、ViGEmBus 或 HidHide。

## 行为与范围

- 快捷面板的限帧入口调用 Intel 驱动，0 表示关闭；实际范围通过驱动枚举。
- 本机返回 30–300 FPS、步长 1；支持运行中修改。
- 本机 API 支持位为 3，即 DX9、DX11；驱动未声明 DX12 / Vulkan 支持，不能承诺所有游戏有效。
- 游戏专属帧率仍保存在工具箱。前台进入该游戏时临时设置全局驱动上限，离开／正常退出时恢复原先的启用位和值；不创建额外 Intel 应用档案。
- 会话写入前保存到 `data/driver-game-fps-session.json`，后台重启时先恢复。其他软件改过驱动上限时保留该修改。
- 旧 RTSS 游戏会话由旧后台正常退出时清理；新路径不调用 RTSS 限帧接口。部署时原 RTSS 全局上限为 0，无须迁移有效限帧值。
- 原 RTSS OSD 兼容路径保留；默认独立性能悬浮窗与本次限帧无 RTSS 依赖。
- Intel 自有应用档案及游戏内限帧也可能影响最终结果；设置回读与实际帧率验证分开记录。

## 验证

原生控制测试覆盖只读不写入、能力范围、关闭时保留原值、游戏退出恢复、外部修改冲突、回读不一致回滚。
四项后台生命周期检查覆盖持久化先于写入、相同游戏不重复设置、离开游戏恢复、重启恢复和读取失败。

不加载 RTSS 的短时 DX11 硬件测试程序，使用自身 Present 调用计数：

| 请求 | 实测 FPS |
| --- | --- |
| 不限 | 3038.5437 |
| 30 | 30.0000 |
| 60 | 60.0000 |
| 90 | 89.9999 |

测试程序每档运行约 7 秒，舍弃前 2 秒。每档后恢复先前驱动设置。证据在 `artifacts/driver-frame-limit/dx11-verification.json`。
已验证实际后台 `set_fps_limit` 接口写入及回读，并将限帧留在关闭状态。功耗、游戏性能与核心设置文件未改变。
这不是 DX12 / Vulkan、真实游戏或插帧兼容性验证。

## 源码与部署

- 新实现：`native/ClawFrameLimit.cs`，由 `native/ClawPower.cs` 调用；正常构建已加入新源码。
- 部署所用的 r28 基线辅助程序与 `native-gamebar-osd` 工作目录产物 SHA-256 相同。在当前工作区 `artifacts/driver-frame-limit/native` 保留其完整源码快照，并单独接入新限帧组件。
- 当前已安装 Python 比主工作区新，因此仅对其限帧相关方法做变更，候选保存于 `artifacts/driver-frame-limit/backend`；未用主工作区旧文件覆盖后台。
- 准备、构建、部署脚本分别是 `tools/Prepare-DriverFrameLimit.py`、`tools/Build-DriverFrameLimit.ps1`、`tools/Deploy-DriverFrameLimit.py`；部署前检查已安装模块哈希，失败时回滚。
- 原始 r28 后台备份：`%LOCALAPPDATA%/ClawToolbox/repair-backups/driver-frame-limit-20261027-145720`。
- 最终部署记录：`artifacts/driver-frame-limit/deployment.json`；后一次仅同步即时状态字段的备份为 `driver-frame-limit-20261027-145951`。

参考：[Intel IGCL 官方接口](https://intel.github.io/drivers.gpu.control-library/Control/api.html#ctl-3d-feature-t)。
