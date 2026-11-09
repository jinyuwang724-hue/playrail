using System.Text.Json.Nodes;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private SettingsWriteQueue? _gsLosslessWrites;
    private readonly Dictionary<string, JsonObject> _gsLosslessPending = new();
    private string? _gsLosslessError;

    private void BuildGsLossless(JsonObject state)
    {
        _stRows.Add(StToggle("小黄鸭", Bool(state["enabled"]), on => GsLosslessSet("AutoScale", JsonValue.Create(on))));
        if (_gsLosslessError != null) _stRows.Add(StInfo("应用结果", _gsLosslessError));
        else _stRows.Add(StInfo("应用状态", _gsLosslessWrites?.IsBusy == true ? "正在保存并应用…"
            : Bool(state["enabled"]) ? "修改自动保存并应用" : "修改自动保存，开启后应用"));
        if (state["profile"] is not JsonObject values || state["profileSchema"] is not JsonArray schema) return;
        // The game already has its own on/off row. All other native profile
        // parameters reuse the full editor, without exposing other games/global settings.
        var groups = (JsonArray)schema.DeepClone();
        foreach (var group in groups.OfType<JsonObject>())
            if (group["fields"] is JsonArray fields)
                foreach (var field in fields.OfType<JsonObject>().Where(f => Str(f["key"]) == "AutoScale").ToArray()) fields.Remove(field);
        BuildLsFields(groups, values, GsLosslessSet);
    }

    private void GsLosslessSet(string field, JsonNode? value)
    {
        if (_gsItem == null || _gsState?["lossless"] is not JsonObject state) return;
        string id = _gsItem.Id;
        var request = GsRequest(withKeys: true);
        if (state["profile"] is JsonObject profile) profile[field] = value?.DeepClone();
        if (field == "AutoScale") state["enabled"] = value?.DeepClone();
        if (!_gsLosslessPending.TryGetValue(id, out var pending)) _gsLosslessPending[id] = pending = new JsonObject();
        pending[field] = value?.DeepClone();
        _gsLosslessError = null;
        _gsLosslessWrites ??= new SettingsWriteQueue(ex =>
        {
            _gsLosslessError = ex.Message;
            Toast("小黄鸭没有应用：" + ex.Message);
        }, () => { if (_gsItem != null) _ = GsLoad(); });
        _ = _gsLosslessWrites.Enqueue("lossless-" + id, 250, async () =>
        {
            var patch = (JsonObject)pending.DeepClone();
            pending.Clear();
            if (patch.Count == 0) return;
            _agent ??= new AgentClient();
            await _agent.CallLong("game_settings_set", 40, request.DeepClone(), JsonValue.Create("losslessOptions"), patch);
        });
        Repaint();
    }
}
