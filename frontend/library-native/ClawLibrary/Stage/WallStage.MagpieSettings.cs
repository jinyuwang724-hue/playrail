// 设置 › Magpie：工具箱自带的 Magpie（源码编译、随工具箱装）每款游戏开起来时用的默认画面设置。
// 每款游戏自己的开关、自动缩放、超分预设在游戏设置 › 画面（WallStage.GameSettings.cs）；这里改的是它们共用的底子。
// 读写：后台 get_magpie_prefs / set_magpie_prefs（claw_magpie.Magpie.prefs，同一份也是控制中心里手动套用时的设置）。
using System.Text.Json.Nodes;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private JsonObject? _mgPrefs;

    private async Task MgLoad()
    {
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.Call("get_magpie_prefs") is JsonObject o && o["preferences"] is JsonObject p) _mgPrefs = (JsonObject)p.DeepClone();
        }
        catch (Exception ex) { Log.Warn($"magpie prefs: {ex.Message}"); }
        if (_stOpen && _gsItem == null && _stCat == 3) Repaint();
    }

    private void MgSet(string key, JsonNode value)
    {
        if (_mgPrefs == null) return;
        _mgPrefs[key] = value.DeepClone();
        Repaint();
        var patch = new JsonObject { [key] = value.DeepClone() };
        _ = Task.Run(async () =>
        {
            try
            {
                _agent ??= new AgentClient();
                if (await _agent.Call("set_magpie_prefs", patch) is JsonObject o && o["preferences"] is JsonObject p)
                    _dq.TryEnqueue(() => { _mgPrefs = (JsonObject)p.DeepClone(); if (_stOpen && _gsItem == null && _stCat == 3) Repaint(); });
            }
            catch (Exception ex) { _dq.TryEnqueue(() => { Toast($"没有改成：{ex.Message}"); _ = MgLoad(); }); }
        });
    }

    private static readonly string[] MgCaptureIds = { "graphics", "desktop", "gdi", "dwm" };
    private static readonly string[] MgCaptureNames = { "Graphics Capture", "桌面复制", "GDI", "DwmSharedSurface" };
    private static readonly int[] MgFrameRates = { 0, 30, 45, 60, 90, 120 };

    private void BuildStMagpie()
    {
        var p = _mgPrefs;
        if (p == null)
        {
            _stRows.Add(new PerfRow { Section = true, Title = "画面" });
            _stRows.Add(StInfo("Magpie", "正在读取…"));
            _ = MgLoad();
            return;
        }
        _stRows.Add(new PerfRow { Section = true, Title = "画面" });
        _stRows.Add(StToggle("铺满屏幕", Bool(p["fill"]), on => MgSet("fill", JsonValue.Create(on)), true));
        _stRows.Add(StToggle("锐化", Bool(p["sharpen"]), on => MgSet("sharpen", JsonValue.Create(on)), true));
        int strength = (int)Math.Round((p["strength"]?.GetValue<double>() ?? 0.5) * 100);
        if (Bool(p["sharpen"]))
            _stRows.Add(StNumber("锐化强度", strength, 0, 100, 10, n => $"{n} %", n => MgSet("strength", JsonValue.Create(n / 100.0)), 50));

        _stRows.Add(new PerfRow { Section = true, Title = "捕获与性能" });
        int cap = Math.Max(0, Array.IndexOf(MgCaptureIds, Str(p["capture"]) ?? "graphics"));
        _stRows.Add(StPick("捕获方式", MgCaptureNames, cap, i => MgSet("capture", JsonValue.Create(MgCaptureIds[i])), 0));
        int fps = Int(p["maxFrameRate"]);
        int fi = Math.Max(0, Array.IndexOf(MgFrameRates, fps));
        _stRows.Add(StPick("处理帧率上限", MgFrameRates.Select(f => f == 0 ? "不限制" : $"{f} FPS").ToArray(), fi, i => MgSet("maxFrameRate", JsonValue.Create(MgFrameRates[i])), 3));
        _stRows.Add(StToggle("3D 游戏模式", Bool(p["game3D"]), on => MgSet("game3D", JsonValue.Create(on)), false));
        _stRows.Add(StToggle("调整光标速度", Bool(p["adjustCursor"]), on => MgSet("adjustCursor", JsonValue.Create(on)), true));

        _stRows.Add(new PerfRow { Section = true, Title = "黑边修正" });
        _stRows.Add(StNumber("左右各裁掉", Int(p["cropX"]), 0, 400, 8, n => n == 0 ? "不裁" : $"{n} 像素", n => MgSet("cropX", JsonValue.Create(n)), 0));
        _stRows.Add(StNumber("上下各裁掉", Int(p["cropY"]), 0, 400, 8, n => n == 0 ? "不裁" : $"{n} 像素", n => MgSet("cropY", JsonValue.Create(n)), 0));
    }
}
