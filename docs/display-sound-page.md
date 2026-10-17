# 显示与声音页：后台接口

前端：`frontend/library-native/ClawLibrary/Stage/WallStage.Display.cs`（控制中心第 4 张卡）。全部是全局设置，不按游戏；Magpie / Lossless Scaling 归游戏配置页，不在这里（用户 2026-10-16）。

## 读：`get_display_state`

老后台没有这个方法时，前端退回 `widget_state().system`（只有亮度、音量、静音、刷新率），其余行显示「后台还没接上」。

```jsonc
{
  "brightness": 62,                 // 0–100；没有可调亮度的屏幕时不给
  "autoBrightness": false,          // 有光感才给；没有光感 → 写进 unsupported
  "refresh": 120, "refreshRates": [120, 60],
  "resolution": [1920, 1200], "resolutions": [[1920, 1200], [1680, 1050], [1280, 800]],   // 当前刷新率下的，按面积从大到小
  "vrr": true,                      // Windows「可变刷新率」开关
  "hdr": false,                     // 屏幕不支持 → unsupported 里写 "hdr"
  "nightLight": { "on": false, "strength": 50 },
  "scale": 150, "scales": [100, 125, 150, 175],          // Windows 文字和界面大小（%）
  "volume": 40, "muted": false,
  "outputs": [{ "id": "{0.0.0.00000000}.{…}", "name": "扬声器" }], "output": "{0.0.0.00000000}.{…}",
  "micMuted": false,                // 没有录音设备 → unsupported 里写 "micMuted"
  "gpu": {                          // Intel 驱动控制接口（IGCL / ControlLib）；拿不到的项不给
    "available": true, "error": null,
    "lowLatency": "off",            // off | on | boost
    "frameSync": "app", "frameSyncOptions": [{ "id": "app", "name": "应用决定" }, { "id": "vsync", "name": "垂直同步" }, { "id": "smooth", "name": "智能同步" }],
    "xessFg": false,
    "scaling": "aspect",            // aspect | full | center | integer | retro
    "sharpen": 0, "saturation": 50, "contrast": 50, "gamma": 50   // 0–100
  },
  "unsupported": ["hdr"]            // 这台机器没有的功能（前端写「这台机器不支持」）；字段没有又不在这里 = 后台还没写
}
```

## 写

每个 setter 都回新的状态（同上的对象，或其中变了的字段），前端拿来直接覆盖。

| 方法 | 参数 | 已有 |
|---|---|---|
| `set_brightness` | 0–100 | 有 |
| `set_volume` | 0–100 | 有 |
| `set_mute` | bool | 有 |
| `set_refresh_rate` | hz | 有 |
| `set_resolution` | w, h | 有（2026-10-17 真机测过：1920×1200 ⇄ 1280×800，刷新率保持 120 Hz）|
| `set_vrr` | bool | 有（2026-10-17 真机测过）|
| `set_hdr` | bool | 有（2026-10-17 真机测过）|
| `set_auto_brightness` | bool | 有；本机没有光线传感器 → `unsupported` 里写 `autoBrightness`，调用回「这台机器没有光线传感器」|
| `set_night_light` | on, strength 0–100 | 新 |
| `set_display_scale` | percent | 有（2026-10-17 真机测过：150 ⇄ 125；读回 `scale`、`scales`、`scaleRecommended`）|
| `set_audio_output` | endpoint id | 有（2026-10-17 真机测过）|
| `set_mic_mute` | bool | 有（2026-10-17 真机测过）|
| `set_gpu_option` | name, value（name = gpu 里的字段名） | 有：lowLatency（off / on / boost）、frameSync（app / vsyncOff / vsync / smooth / speed / capped）、xessFg（bool），全局设置；2026-10-17 真机逐项改回测过。锐化、饱和度等按显示输出的项还没接 |

新方法都要加进 `plugin/claw_agent.py` 的白名单。

## 实现位置

- 读和「有」的几项：`native/ClawDisplay.cs`（ClawPower.exe 里的 `DisplayAudio`），命令 `display_read` / `resolution_set` / `vrr_set` / `hdr_set` / `audio_output_set` / `mic_mute_set`，回 `reply["display"]`；Python 侧 `ClawPower.display()`、`ClawService.get_display_state` 等。
- VRR 写的是 `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` 的 `DirectXUserGlobalSettings` 里的 `VRROptimizeEnable`（设置 → 显示 → 图形里的开关）。注意 ClawPower 是提权跑的：同一个用户账户时 HKCU 一致，要真机确认。
- 输出设备切换用未公开的 `IPolicyConfig`（三个角色都设）。
- 读失败的项不给字段，原因写在 `errors`（如 `{"outputs": "…"}`）。
- 设备枚举器按 CLSID 建、直接转接口：它是 COM 单例，SystemControls 已经用自己的 ComImport 类包过一次，再 new 同 CLSID 的类会 InvalidCastException（2026-10-17 实测踩到）。
- 屏幕缩放用 DisplayConfig 没公开的 DPI 缩放项（GET -3 / SET -4，相对推荐档的偏移）；自动亮度是电源设置 ADAPTBRIGHT（插电、电池一起写）。
- 显卡：`native/ClawGpu.cs`，Intel ControlLib.dll（IGCL）的全局 3D 设置（ctlGetSet3DFeature，应用名为空 = 全局）。init 参数结构 36 字节（ApplicationUID 的 Data4 按 1 对齐），用 8 字节对齐会报 0x4000000F 尺寸不对。
- 还没写：夜间模式（这版 Windows 用按设备的 CloudStore 数据块，格式没公开，要先在设置里开关一次看清楚再写）。前端显示「后台还没接上」。
