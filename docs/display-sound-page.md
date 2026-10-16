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
| `set_resolution` | w, h | 新 |
| `set_vrr` | bool | 新 |
| `set_hdr` | bool | 新 |
| `set_auto_brightness` | bool | 新 |
| `set_night_light` | on, strength 0–100 | 新 |
| `set_display_scale` | percent | 新 |
| `set_audio_output` | endpoint id | 新 |
| `set_mic_mute` | bool | 新 |
| `set_gpu_option` | name, value（name = gpu 里的字段名） | 新 |

新方法都要加进 `plugin/claw_agent.py` 的白名单。
