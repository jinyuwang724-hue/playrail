using System.Text.Json.Nodes;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private long _gsPowerRevision;
    private bool _gsPowerSaving;
    private readonly Dictionary<string, (JsonObject Request, JsonObject? Profile, LibraryItem Item, long Revision)> _gsPowerPending = new();

    private void BuildGsPower(JsonObject? s, string wait, string noExe)
    {
        var pw = s?["power"] as JsonObject;
        _stRows.Add(new PerfRow { Section = true, Title = "功耗" });
        if (s == null) { _stRows.Add(StInfo("功耗配置档", wait)); return; }
        if (Str(pw?["exe"]) == null) { _stRows.Add(StInfo("功耗配置档", noExe)); return; }
        var profile = pw?["profile"] as JsonObject ?? new JsonObject { ["mode"] = "global" };
        var defaults = pw?["defaults"] as JsonObject;
        int tdpMin = Int(pw?["tdpMin"], 8), tdpMax = Int(pw?["tdpMax"], 35);
        string[] modes = { "global", "manual", "auto", "autotdp" };
        int mi = Math.Max(0, Array.IndexOf(modes, Str(profile["mode"])));
        JsonObject Copy() => (JsonObject)profile.DeepClone();
        void Change(string key, JsonNode? value) { var p = Copy(); p[key] = value?.DeepClone(); GsPower(p); }
        JsonObject AutoDefaults() => (JsonObject)(pw?["autotdp"]?.DeepClone() ?? new JsonObject { ["target"] = 60, ["floor"] = 12, ["acMax"] = 35, ["dcMax"] = 25 });
        JsonObject PowerDefault(string key, int watts) => defaults?[key]?.DeepClone() as JsonObject ?? new() { ["tdp"] = watts, ["freq"] = 0 };
        _stRows.Add(StPick("功耗配置档", new[] { "跟随全局", "固定功耗", "按电源切换", "自动" }, mi, i =>
        {
            // Changing the power mode keeps this game's independent FPS and OSD.
            var p = new JsonObject { ["mode"] = modes[i] };
            foreach (string key in new[] { "fpsLimit", "osd" }) if (profile[key] != null) p[key] = profile[key]!.DeepClone();
            if (i == 1) { var d = PowerDefault("manual", 20); p["tdp"] = Int(d["tdp"], 20); p["freq"] = Int(d["freq"]); }
            if (i == 2) { p["ac"] = PowerDefault("ac", 35); p["dc"] = PowerDefault("dc", 25); }
            if (i == 3) foreach (var kv in AutoDefaults()) p[kv.Key] = kv.Value?.DeepClone();
            if (i > 0) p["boost"] = Int(defaults?["boost"], 1);
            GsPower(p);
        }, 0));
        var frequencies = (pw?["freqChoices"] as JsonArray)?.Select(n => Int(n)).ToList() ?? new() { 0, 1500, 2000, 2500, 2750, 3000, 3500, 4000 };
        void Frequency(string title, JsonObject part, Action<JsonObject> save)
        {
            int selected = Int(part["freq"]);
            int[] choices = frequencies.Append(selected).Distinct().Order().ToArray();
            _stRows.Add(StPick(title, choices.Select(f => f == 0 ? "不限" : $"{f / 1000.0:0.##} GHz").ToArray(), Array.IndexOf(choices, selected), i =>
            { var p = (JsonObject)part.DeepClone(); p["freq"] = choices[i]; save(p); }, 0));
        }
        if (mi == 1)
        {
            _stRows.Add(StNumber("功耗", Int(profile["tdp"], 20), tdpMin, tdpMax, 1, n => $"{n} W", n => Change("tdp", JsonValue.Create(n)), 20));
            Frequency("CPU 频率上限", profile, GsPower);
        }
        if (mi == 2)
            foreach (string key in new[] { "ac", "dc" })
            {
                string label = key == "ac" ? "插电" : "电池";
                var part = profile[key] as JsonObject ?? PowerDefault(key, key == "ac" ? 35 : 25);
                _stRows.Add(StNumber(label + "功耗", Int(part["tdp"], 20), tdpMin, tdpMax, 1, n => $"{n} W", n =>
                { var p = (JsonObject)part.DeepClone(); p["tdp"] = n; Change(key, p); }, key == "ac" ? 35 : 25));
                Frequency(label + " CPU 频率上限", part, p => Change(key, p));
            }
        if (mi == 3)
        {
            var d = AutoDefaults();
            int floor = Int(profile["floor"], Int(d["floor"], 12));
            int ac = Int(profile["acMax"], Int(d["acMax"], 35)), dc = Int(profile["dcMax"], Int(d["dcMax"], 25));
            _stRows.Add(StNumber("目标帧率", Int(profile["target"], Int(d["target"], 60)), 20, 240, 1, n => $"{n} FPS", n => Change("target", JsonValue.Create(n)), 60));
            _stRows.Add(StNumber("功耗下限", floor, tdpMin, Math.Min(ac, dc), 1, n => $"{n} W", n => Change("floor", JsonValue.Create(n)), Math.Clamp(12, tdpMin, Math.Min(ac, dc))));
            _stRows.Add(StNumber("插电功耗上限", ac, floor, tdpMax, 1, n => $"{n} W", n => Change("acMax", JsonValue.Create(n)), tdpMax));
            _stRows.Add(StNumber("电池功耗上限", dc, floor, tdpMax, 1, n => $"{n} W", n => Change("dcMax", JsonValue.Create(n)), Math.Max(floor, 25)));
        }
        if (mi > 0)
        {
            _stRows.Add(StNumber("短时功耗加成", Int(profile["boost"], Int(defaults?["boost"], 1)), 0, 15, 1, n => $"+{n} W", n => Change("boost", JsonValue.Create(n)), 1));
            if (!Bool(pw?["enabled"])) _stRows.Add(StInfo("功耗控制", "请在控制中心开启功耗控制"));
        }
        _stRows.Add(new PerfRow { Section = true, Title = "帧率上限（RTSS）" });
        bool customFps = profile["fpsLimit"] != null;
        _stRows.Add(StPick("限帧配置", new[] { "跟随全局", "独立配置" }, customFps ? 1 : 0, i => Change("fpsLimit", i == 0 ? null : JsonValue.Create(60)), 0));
        if (customFps) _stRows.Add(StNumber("帧率上限", Int(profile["fpsLimit"]), 0, 360, 1, n => n == 0 ? "不限" : $"{n} FPS", n => Change("fpsLimit", JsonValue.Create(n)), 60));
        if (Str(pw?["fpsError"]) is { Length: > 0 } error) _stRows.Add(StInfo("RTSS 状态", error));
        _stRows.Add(new PerfRow { Section = true, Title = "性能显示（RTSS）" });
        var osd = profile["osd"] as JsonObject;
        _stRows.Add(StPick("显示配置", new[] { "跟随全局", "独立配置" }, osd == null ? 0 : 1, i => Change("osd", i == 0 ? null : defaults?["osd"]?.DeepClone() ?? new JsonObject
        { ["enabled"] = true, ["level"] = 2, ["size"] = "medium", ["rows"] = 1, ["smallUnits"] = false }), 0));
        if (osd != null)
        {
            BuildOsdOptions(osd, p => Change("osd", p), pw?["osdCatalog"] as JsonArray, pw?["osdLayoutDefaults"] as JsonObject, _gsItem?.Id ?? "game");
        }
    }

    private void GsPower(JsonObject? profile)
    {
        if (_gsItem == null || _gsState?["power"] is not JsonObject state) return;
        // JSON null means inherit; remove it instead of sending an invalid optional value.
        if (profile != null)
        {
            foreach (string key in profile.Where(p => p.Value == null).Select(p => p.Key).ToArray()) profile.Remove(key);
            if (Str(profile["mode"]) == "global" && profile.Count == 1) profile = null;
        }
        state["profile"] = profile?.DeepClone();
        string exe = Str(state["exe"])!;
        _gsPowerPending[exe] = (GsRequest(true), profile?.DeepClone() as JsonObject, _gsItem, ++_gsPowerRevision);
        Repaint();
        _ = FlushGsPower();
    }

    private async Task FlushGsPower()
    {
        if (_gsPowerSaving) return;
        _gsPowerSaving = true;
        try
        {
            await Task.Delay(200);
            while (_gsPowerPending.Count > 0)
            {
                var next = _gsPowerPending.First(); _gsPowerPending.Remove(next.Key);
                var edit = next.Value;
                try
                {
                    _agent ??= new AgentClient();
                    await _agent.CallLong("game_settings_set", 40, edit.Request, JsonValue.Create("power"), edit.Profile);
                }
                catch (Exception ex) { Toast($"游戏配置未能保存：{ex.Message}"); }
                if (_gsItem == edit.Item && edit.Revision == _gsPowerRevision) await GsLoad();
            }
        }
        finally { _gsPowerSaving = false; }
    }
}
