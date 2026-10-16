# Claw 8 EX 手柄线稿

来源：本机安装的 MSI Center M 3.0.60831（WindowsApps 包 `9426MICRO-STARINTERNATION.64797CC12EF8E`），
`MSI Center M\UC_ControlMode.dll` 内嵌的 WPF 资源（`UC_ControlMode.g.resources`）。
本机型号 `Claw 8 EX AI+ CG3EM`，SKU `1T91`，对应资源里带 `_1t91` 后缀的图（`_8` 是 Claw 8 AI+，无后缀是 Claw A1M）。

| 文件 | 原资源 | 说明 |
|---|---|---|
| claw8ex-front-hd.png | images/dark/vibration/img_vibration_1t91.png（900×412） | 正面高清版，去掉了 MSI 涂在握把上的红 |
| claw8ex-back.png | images/dark/mapping/img_keymapping_back_1t91.png（700×310） | 背面；和正面同一比例：正面坐标 = 背面 × 1.3 − (5, 10) |
| claw8ex-trigger-l.png / -r.png | images/dark/trigger/img_trigger_left / right_1t91.png（420×414） | 左右扳机侧视，去掉了涂在扳机上的红 |
| regions.txt | 由 tools/PadRegions 生成 | 高亮区域的矢量轮廓 |

图归 MSI 所有，只在本机自用的前端里用，仓库是私有的；不要随公开发布一起分发。
键位坐标（各面原图像素）写在 Stage/WallStage.Gamepad.cs 的 GpKeys。

## 去红、高亮区域

`tools/PadRegions`（原图放在 tools/PadRegions/src）：

```
dotnet run --project tools/PadRegions -c Release -- clean tools/PadRegions/src/msi-vibration-1t91.png ClawLibrary/Assets/Pad/claw8ex-front-hd.png
dotnet run --project tools/PadRegions -c Release -- regions tools/PadRegions/regions.spec ClawLibrary/Assets/Pad/regions.txt <预览目录>
```

- clean：MSI 把要高亮的地方涂成半透明红（≈ 235,55,35），去掉；线和红混过的像素去色成灰。
- regions：按 regions.spec 每行描一块：线稿放大 4 倍 → 从种子点漫水填充（clear = 碰到线条停；solid = 实心块；red = MSI 涂红的那块，
  直接沿用 MSI 的高亮形状）→ 外扩盖住线的半边 → 描外轮廓 → 抽稀 → Chaikin 平滑，存成原图像素坐标的多边形；运行时 Win2D 抗锯齿填充。
