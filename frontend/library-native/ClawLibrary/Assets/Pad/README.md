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

## 高亮区域

`regions.txt`：握把、肩部、背键的闭合轮廓（原图像素坐标），手柄页焦点时用 Win2D 抗锯齿填强调色（GpFill），放大多少边缘都是平的。
由 `tools/PadRegions` 生成：线稿放大 4 倍 → 从种子点漫水填充（碰到线条停）→ 外扩 2–3 px 盖住线的半边 → 描外轮廓 → Douglas–Peucker 抽稀 → Chaikin 平滑。

```
dotnet run --project tools/PadRegions -c Release -- ClawLibrary/Assets/Pad/claw8ex-front.png ClawLibrary/Assets/Pad/claw8ex-back.png ClawLibrary/Assets/Pad/regions.txt preview.png
```

| 区域 | 面 | 种子点 | 说明 |
|---|---|---|---|
| GripL / GripR | 正面 | (40,240) / (660,240) | 握把，y < 150 的侧条裁掉（和 MSI 震动页一致） |
| LB / RB | 正面 | (75,22) / (625,22) | 顶部肩部 |
| M1 / M2 | 背面 | (112,125) / (586,125) | 背键实心块（不透明像素扩散） |
