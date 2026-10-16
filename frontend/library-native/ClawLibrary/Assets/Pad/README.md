# Claw 8 EX 手柄线稿

来源：本机安装的 MSI Center M 3.0.60831（WindowsApps 包 `9426MICRO-STARINTERNATION.64797CC12EF8E`），
`MSI Center M\UC_ControlMode.dll` 内嵌的 WPF 资源（`UC_ControlMode.g.resources`）。
本机型号 `Claw 8 EX AI+ CG3EM`，SKU `1T91`，对应资源里带 `_1t91` 后缀的图（`_8` 是 Claw 8 AI+，无后缀是 Claw A1M）。

| 文件 | 原资源 |
|---|---|
| claw8ex-front.png | images/dark/mapping/img_keymapping_1t91.png（700×310） |
| claw8ex-back.png | images/dark/mapping/img_keymapping_back_1t91.png（700×310） |

同一 dll 里还有 1T91 的扳机侧视图（trigger/img_trigger_left|right_1t91）、摇杆 / 震动示意图、M1 / M2 / Claw 键 / 快速设置键小图标，需要时照上面的路径取。
图归 MSI 所有，只在本机自用的前端里用，仓库是私有的；不要随公开发布一起分发。
键位坐标（原图像素）写在 Stage/WallStage.Gamepad.cs 的 GpKeys。
