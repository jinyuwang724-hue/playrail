using System.Globalization;
using System.Text.Json.Nodes;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private JsonObject? _mgEdit, _mgInitial;
    private string? _mgOriginal, _mgSelected, _mgAddGroup;
    private int _mgStage = -1;
    private bool _mgAdding, _mgSaving;
    private JsonArray MgChain => (JsonArray)_mgEdit!["effects"]!;
    private bool MgDirty => _mgEdit != null && (_mgOriginal == null || !JsonNode.DeepEquals(_mgEdit, _mgInitial));
    private static string MgEffectLabel(string name) => name.Replace('\\', '/');
    private static string MgGroup(string name) => name.Contains('\\') ? name.Split('\\')[0] : "通用";

    private void MgCloseEditor()
    {
        _mgEdit = _mgInitial = null;
        _mgOriginal = _mgAddGroup = null;
        _mgStage = -1;
        _mgAdding = false;
    }

    private void MgEditorPage()
    {
        _stArmed = null;
        _stZone = 1;
        _settingsManualScroll = false;
        BuildStRows();
        _stFocus = FirstStRow();
        _stScroll = 0; _stScrollY = float.NaN;
        PaintSt();
    }

    private void MgOpenEditor(JsonObject? preset, bool copy)
    {
        _mgOriginal = copy ? null : Str(preset?["name"]);
        _mgEdit = preset == null ? new JsonObject { ["name"] = "自定义预设", ["effects"] = new JsonArray() } : (JsonObject)preset.DeepClone();
        if (copy)
        {
            string basis = preset == null ? "自定义预设" : (Str(preset["name"]) ?? "预设") + " 副本";
            var names = (_mgCatalog?["presets"] as JsonArray)?.Select(n => Str(n?["name"])).ToHashSet() ?? new();
            string name = basis;
            for (int i = 2; names.Contains(name); i++) name = $"{basis} {i}";
            _mgEdit["name"] = name;
            _mgEdit["builtin"] = false;
            _mgEdit["modified"] = false;
        }
        _mgInitial = (JsonObject)_mgEdit.DeepClone();
        _mgStage = -1; _mgAdding = false; _mgAddGroup = null;
        MgEditorPage();
    }

    private bool MgBack()
    {
        if (_mgEdit == null) return false;
        if (_mgSaving) return true;
        if (_mgAdding) { if (_mgAddGroup != null) _mgAddGroup = null; else _mgAdding = false; MgEditorPage(); return true; }
        if (_mgStage >= 0) { _mgStage = -1; MgEditorPage(); return true; }
        if (MgDirty && !StConfirm("mg-discard")) { Toast("预设尚未保存，再按一次返回放弃修改"); return true; }
        MgCloseEditor(); MgEditorPage();
        return true;
    }

    private async Task MgSaveEditor(bool remove = false)
    {
        if (_mgEdit == null || _mgSaving) return;
        var draft = _mgEdit;
        string name = (Str(draft["name"]) ?? "").Trim();
        _mgSaving = true;
        Repaint();
        try
        {
            _agent ??= new AgentClient();
            JsonNode? result = remove
                ? await _agent.Call("delete_magpie_preset", JsonValue.Create(_mgOriginal))
                : await _agent.Call("save_magpie_preset", new JsonObject { ["name"] = name, ["originalName"] = _mgOriginal, ["effects"] = MgChain.DeepClone() });
            if (_mgCatalog != null && result?["presets"] is JsonArray presets) _mgCatalog["presets"] = presets.DeepClone();
            _mgSelected = remove ? null : name;
            Toast(remove ? "预设已更新" : "预设已保存，引用此预设的游戏配置已更新");
            if (ReferenceEquals(_mgEdit, draft)) { MgCloseEditor(); if (_stOpen && _gsItem == null && _stCat == 3) MgEditorPage(); }
        }
        catch (Exception ex) { Toast($"没有保存：{ex.Message}"); }
        finally { _mgSaving = false; }
        if (_stOpen) Repaint();
    }

    private void BuildMgEditor()
    {
        if (_mgAdding) { BuildMgEffectPicker(); return; }
        if (_mgStage >= 0 && _mgStage < MgChain.Count) { BuildMgEffect(); return; }
        var edit = _mgEdit!;
        bool builtin = Bool(edit["builtin"]);
        _stRows.Add(new PerfRow { Section = true, Title = "编辑超分预设" });
        _stRows.Add(new PerfRow { Title = "预设名称", Value = Str(edit["name"]) ?? "", Disabled = builtin || _mgSaving,
            Press = () => StEditText(Str(edit["name"]) ?? "", name => { edit["name"] = name.Trim(); Repaint(); }) });
        _stRows.Add(StInfo("保存方式", "保存后用于下次缩放，游戏内当前画面不变"));
        _stRows.Add(new PerfRow { Section = true, Title = $"效果链 · {MgChain.Count} / 12" });
        if (MgChain.Count == 0) _stRows.Add(StInfo("尚无效果器", "添加后按顺序处理画面"));
        for (int i = 0; i < MgChain.Count; i++)
        {
            int index = i;
            _stRows.Add(new PerfRow { Title = $"{i + 1}. {MgEffectLabel(Str(MgChain[i]?["name"]) ?? "")}", Value = "参数与顺序 ›", Disabled = _mgSaving,
                Press = () => { _mgStage = index; MgEditorPage(); } });
        }
        _stRows.Add(new PerfRow { Title = "添加效果器…", Value = "", Disabled = _mgSaving || MgChain.Count >= 12,
            Press = () => { _mgAdding = true; _mgAddGroup = null; MgEditorPage(); } });
        _stRows.Add(new PerfRow { Section = true, Title = "保存" });
        _stRows.Add(new PerfRow { Title = "保存预设", Value = _mgSaving ? "正在保存…" : MgDirty ? "有未保存的修改" : "", Disabled = _mgSaving,
            Press = () => { _ = MgSaveEditor(); } });
        _stRows.Add(StAction("返回预设列表", "mg-discard", "", () => MgBack()));
        if (_mgOriginal != null && (!builtin || Bool(edit["modified"])))
            _stRows.Add(StAction(builtin ? "恢复内置预设" : "删除预设", "mg-delete", builtin ? "还原原始效果链" : "引用它的游戏改为「无」", () => { if (!_mgSaving && StConfirm("mg-delete")) _ = MgSaveEditor(true); }));
    }

    private void BuildMgEffectPicker()
    {
        var effects = (_mgCatalog?["effects"] as JsonArray)?.OfType<JsonObject>().OrderBy(e => Str(e["name"]), StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<JsonObject>();
        _stRows.Add(new PerfRow { Section = true, Title = _mgAddGroup == null ? "选择效果器分类" : $"添加效果器 › {_mgAddGroup}" });
        _stRows.Add(StAction("返回", "mg-picker-back", "", () => MgBack()));
        if (_mgAddGroup == null)
        {
            foreach (string group in effects.Select(e => MgGroup(Str(e["name"]) ?? "")).Distinct())
                _stRows.Add(new PerfRow { Title = group, Value = "›", Press = () => { _mgAddGroup = group; MgEditorPage(); } });
            return;
        }
        foreach (var effect in effects.Where(e => MgGroup(Str(e["name"]) ?? "") == _mgAddGroup))
        {
            string name = Str(effect["name"])!;
            _stRows.Add(new PerfRow { Title = MgEffectLabel(name), Value = Bool(effect["canScale"]) ? "可调缩放" : "", Press = () =>
            {
                MgChain.Add((JsonNode)new JsonObject { ["name"] = name, ["parameters"] = new JsonObject(), ["scalingType"] = Bool(effect["canScale"]) ? 1 : 0, ["scale"] = new JsonArray(1.0, 1.0) });
                _mgStage = MgChain.Count - 1; _mgAdding = false; _mgAddGroup = null; MgEditorPage();
            } });
        }
    }

    private void BuildMgEffect()
    {
        int index = _mgStage;
        var stage = (JsonObject)MgChain[index]!;
        string name = Str(stage["name"])!;
        var effect = (_mgCatalog?["effects"] as JsonArray)?.FirstOrDefault(e => Str(e?["name"]) == name);
        _stRows.Add(new PerfRow { Section = true, Title = MgEffectLabel(name) });
        _stRows.Add(StAction("返回效果链", "mg-chain", "", () => { _mgStage = -1; MgEditorPage(); }));
        if (effect?["parameters"] is JsonArray parameters)
            foreach (var parameter in parameters.OfType<JsonObject>())
            {
                string key = Str(parameter["name"])!;
                var values = stage["parameters"] as JsonObject;
                if (values == null) { values = new JsonObject(); stage["parameters"] = values; }
                double def = parameter["default"]!.GetValue<double>();
                double min = parameter["min"]!.GetValue<double>(), max = parameter["max"]!.GetValue<double>();
                double step = parameter["step"]!.GetValue<double>();
                var target = values;
                _stRows.Add(MgNumber(Str(parameter["label"]) ?? key, values[key]?.GetValue<double>() ?? def, min, max,
                    step, Bool(parameter["integer"]), def, v => { target[key] = v; Repaint(); }));
            }
        if (Bool(effect?["canScale"]))
        {
            _stRows.Add(new PerfRow { Section = true, Title = "缩放" });
            int type = Int(stage["scalingType"]);
            _stRows.Add(StPick("缩放方式", new[] { "倍数", "适应屏幕", "指定尺寸", "充满屏幕" }, type, i =>
            {
                stage["scalingType"] = i;
                if (i == 2 && type != 2) stage["scale"] = new JsonArray(1920.0, 1200.0);
                else if (type == 2 && i != 2) stage["scale"] = new JsonArray(1.0, 1.0);
                Repaint();
            }));
            if (type != 3)
            {
                if (stage["scale"] is not JsonArray) stage["scale"] = new JsonArray(1.0, 1.0);
                var scale = (JsonArray)stage["scale"]!;
                for (int axis = 0; axis < 2; axis++)
                {
                    int a = axis;
                    _stRows.Add(MgNumber(type == 2 ? axis == 0 ? "宽度（像素）" : "高度（像素）" : axis == 0 ? "横向比例" : "纵向比例",
                        scale[a]!.GetValue<double>(), type == 2 ? 1 : 0.01, 16384, type == 2 ? 1 : 0.05, type == 2, type == 2 ? a == 0 ? 1920 : 1200 : 1,
                        v => { scale[a] = v; Repaint(); }));
                }
            }
        }
        _stRows.Add(new PerfRow { Section = true, Title = "效果顺序" });
        _stRows.Add(new PerfRow { Title = "上移", Value = "", Disabled = index == 0, Press = () => MgMoveEffect(-1) });
        _stRows.Add(new PerfRow { Title = "下移", Value = "", Disabled = index == MgChain.Count - 1, Press = () => MgMoveEffect(1) });
        _stRows.Add(StAction("移除效果器", "mg-remove", "", () => { MgChain.RemoveAt(index); _mgStage = -1; MgEditorPage(); }));
    }

    private void MgMoveEffect(int delta)
    {
        int next = _mgStage + delta;
        if (next < 0 || next >= MgChain.Count) return;
        var stage = MgChain[_mgStage]!;
        MgChain.RemoveAt(_mgStage); MgChain.Insert(next, stage); _mgStage = next;
        Repaint();
    }

    // Keep floating point effect parameters in their native units; the same setting row supports
    // touch +/- buttons, controller left/right, exact text entry and Y to restore defaults.
    private PerfRow MgNumber(string title, double value, double min, double max, double step, bool integer, double def, Action<double> set)
    {
        void Apply(double v)
        {
            if (!double.IsFinite(v) || v < min || v > max || integer && v != Math.Truncate(v)) { Toast($"请输入 {min:0.######}–{max:0.######}" + (integer ? " 的整数" : "")); return; }
            set(v);
        }
        return new PerfRow { Title = title, Value = value.ToString("0.######", CultureInfo.InvariantCulture), Numeric = true,
            Changed = Math.Abs(value - def) > 1e-7, Reset = () => set(def),
            Step = d => Apply(Math.Clamp(Math.Round(value + d * step, 7), min, max)),
            Press = () => StEditText(value.ToString("0.########", CultureInfo.InvariantCulture), text =>
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) Apply(v); else Toast("请输入有效数值");
            }) };
    }
}
