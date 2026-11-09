using System.Text.Json.Nodes;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private readonly SemaphoreSlim _gpSpecialGate = new(1, 1);
    private int _gpSpecialEdits;
    private static bool GpIsSpecial(string source) => source is "QS" or "Claw";
    private static JsonObject GpDefaultSpecial(string source) => new() { ["type"] = "system", ["action"] = source == "QS" ? "gamebar" : "library" };

    private JsonNode GpSpecialCommand(string source, bool steam)
    {
        if (_gpSettings?[steam ? "steamBindings" : "buttonMappings"]?[source] is JsonNode command) return command;
        if (!steam) return GpDefaultSpecial(source);
        string action = Str(_gpSettings?[source == "QS" ? "qamAction" : "msiAction"]) ?? (source == "QS" ? "MainMenu" : "QuickAccess");
        string? mapped = action switch { "MainMenu" => "steamMainMenu", "QuickAccess" => "steamQuickAccess", "MagpieToggle" => "magpieToggle", _ => null };
        return mapped == null ? new JsonObject { ["type"] = "none" } : new JsonObject { ["type"] = "system", ["action"] = mapped };
    }
    private string GpSpecialSummary(string source, bool steam) => !steam && _gpSettings?["buttonMappings"]?[source] == null
        ? "原功能" : GpCommandText(GpSpecialCommand(source, steam));

    private void GpSetSpecial(GpPickerState picker, JsonNode command)
    {
        if (_gpSettings == null) return;
        string field = picker.SteamMapping ? "steamBindings" : "buttonMappings";
        if (_gpSettings[field] is not JsonObject bindings) _gpSettings[field] = bindings = new JsonObject();
        if (!picker.SteamMapping)
            foreach (string key in new[] { "QS", "Claw" }) if (bindings[key] == null) bindings[key] = GpDefaultSpecial(key);
        bindings[picker.Source] = command.DeepClone();
        _ = GpSaveSpecial();
    }

    private async Task GpSaveSpecial()
    {
        if (_gpSettings == null || _agent == null) return;
        int edit = ++_gpSpecialEdits;
        var snapshot = _gpSettings.DeepClone();
        await _gpSpecialGate.WaitAsync();
        try
        {
            if (edit != _gpSpecialEdits) return;
            var result = await _agent.Call("save_settings", snapshot);
            if (edit == _gpSpecialEdits && result?["settings"] is JsonObject saved) _gpSettings = (JsonObject)saved.DeepClone();
        }
        catch (Exception ex) { Toast($"没有保存：{ex.Message}"); if (edit == _gpSpecialEdits) _ = GpRefresh(); }
        finally { _gpSpecialGate.Release(); }
        if (_gpOpen) { BuildGpItems(); PaintGpBody(); }
    }

    private List<PerfRow> GpSpecialRows(GpPickerState picker)
    {
        PerfRow Choice(string title, string[] names, int index, Action<int> set) => new()
        {
            Title = title, Choices = names, Choice = index,
            Select = set, Step = direction => set(Math.Clamp(index + direction, 0, names.Length - 1)),
            Press = () => set((index + 1) % names.Length),
        };
        PerfRow Info(string title, string value) => new() { Title = title, Value = value, Skip = true };
        bool steam = picker.SteamMapping;
        var rows = new List<PerfRow>
        {
            new() { Section = true, Title = "专用按键映射" },
            Choice("生效场景", new[] { "常规场景", "Steam 大屏接管" }, steam ? 1 : 0, i => { picker.SteamMapping = i == 1; PaintGpBody(); }),
        };
        bool on = steam ? Bool(_gpSettings?["enabled"]) : (_gpSettings?["buttonMappings"] as JsonObject)?.Count > 0;
        rows.Add(Choice(steam ? "Steam 接管" : "常规映射", new[] { "关", "开" }, on ? 1 : 0, i =>
        {
            if (_gpSettings == null) return;
            if (steam) _gpSettings["enabled"] = i == 1;
            else _gpSettings["buttonMappings"] = i == 0 ? new JsonObject() : new JsonObject { ["QS"] = GpDefaultSpecial("QS"), ["Claw"] = GpDefaultSpecial("Claw") };
            _ = GpSaveSpecial();
        }));
        rows.Add(Info("触发方式", "普通按压"));
        rows.Add(new() { Title = "指令", Value = GpCommandText(GpSpecialCommand(picker.Source, steam)) + "  ›", Press = () =>
        {
            picker.Level = GpSubLevel.Chooser; picker.Tab = 4; picker.Cell = 0; picker.InTabs = false; picker.Chord.Clear(); PaintGpBody();
        } });
        rows.Add(new() { Title = "禁用这个键", Value = "›", Press = () => GpSetSpecial(picker, new JsonObject { ["type"] = "none" }) });
        rows.Add(new() { Title = steam ? "恢复原接管映射" : "恢复默认映射", Value = "›", Press = () =>
        {
            if (steam) { (_gpSettings?["steamBindings"] as JsonObject)?.Remove(picker.Source); _ = GpSaveSpecial(); }
            else GpSetSpecial(picker, GpDefaultSpecial(picker.Source));
            PaintGpBody();
        } });
        rows.Add(new() { Title = "测试指令", Value = "立即执行  ›", Press = async () =>
        {
            await GpSaveSpecial();
            try
            {
                var result = await _agent!.Call("test_button_mapping", JsonValue.Create(picker.Source), JsonValue.Create(picker.SteamMapping));
                Toast(Bool(result?["ok"]) ? "指令已执行" : $"执行失败：{Str(result?["error"])}");
            }
            catch (Exception ex) { Toast($"执行失败：{ex.Message}"); }
        } });
        return rows;
    }
}
