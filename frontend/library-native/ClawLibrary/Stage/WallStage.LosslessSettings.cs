using System.Text.Json.Nodes;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private JsonObject? _lsConfig, _lsEditor, _lsInitial;
    private bool _lsLoading, _lsSaving, _stBrowseLossless;
    private bool _lsBusy => _lsLoading || _lsSaving;
    private string? _lsError;
    private int _lsProfile;
    private bool LsDirty => _lsEditor != null && (!Bool(_lsInitial?["initialized"])
        || !JsonNode.DeepEquals(_lsEditor["globalValues"], _lsInitial?["globalValues"])
        || !JsonNode.DeepEquals(_lsEditor["profiles"], _lsInitial?["profiles"]));

    private void LsReadResponse(JsonObject response, bool editor)
    {
        _lsConfig = (JsonObject)response.DeepClone();
        _lsConfig.Remove("editor");
        if (!editor) return;
        if (response["editor"] is not JsonObject data || data["profiles"] is not JsonArray profiles || profiles.Count == 0)
            throw new InvalidOperationException("没有读到小黄鸭配置，请重新读取");
        _lsEditor = (JsonObject)data.DeepClone();
        _lsInitial = (JsonObject)data.DeepClone();
        _lsProfile = Math.Clamp(_lsProfile, 0, profiles.Count - 1);
    }

    private async Task LsLoad(bool refresh = false)
    {
        if (_lsBusy) return;
        _lsLoading = true;
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.Call("get_lossless_config", JsonValue.Create(refresh)) is not JsonObject config) throw new IOException("没有读到小黄鸭状态");
            _lsConfig = (JsonObject)config.DeepClone();
            if (Bool(config["configured"]) && (_lsEditor == null || !LsDirty && Str(config["revision"]) != Str(_lsInitial?["revision"])))
            {
                if (await _agent.Call("get_lossless_settings") is not JsonObject response) throw new IOException("没有读到小黄鸭设置");
                LsReadResponse(response, true);
            }
            _lsError = null;
        }
        catch (Exception ex) { _lsError = ex.Message; }
        finally { _lsLoading = false; }
        if (_stOpen && _gsItem == null && _stCat == 4 && !StEditing) Repaint();
    }

    private async Task LsReload()
    {
        if (_lsBusy || LsDirty && !StConfirm("ls-reload")) return;
        _lsLoading = true;
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.Call("get_lossless_settings") is not JsonObject response) throw new IOException("没有读到小黄鸭设置");
            LsReadResponse(response, true); _lsError = null;
        }
        catch (Exception ex) { _lsError = ex.Message; Toast(ex.Message); }
        finally { _lsLoading = false; }
        if (_stOpen) Repaint();
    }

    private JsonObject LsSaveRequest()
    {
        var changes = new JsonArray();
        var current = (JsonArray)_lsEditor!["profiles"]!;
        var initial = (JsonArray)_lsInitial!["profiles"]!;
        foreach (var profile in current.OfType<JsonObject>())
        {
            var index = profile["index"];
            var previous = index == null ? null : initial.FirstOrDefault(p => Int(p?["index"], -1) == Int(index));
            var patch = new JsonObject();
            foreach (var entry in (JsonObject)profile["values"]!)
                if (previous == null || !JsonNode.DeepEquals(entry.Value, previous["values"]?[entry.Key])) patch[entry.Key] = entry.Value?.DeepClone();
            if (patch.Count > 0) changes.Add((JsonNode)new JsonObject { ["index"] = index?.DeepClone(), ["values"] = patch });
        }
        var globals = new JsonObject();
        foreach (var entry in (JsonObject)_lsEditor["globalValues"]!)
            if (!JsonNode.DeepEquals(entry.Value, _lsInitial["globalValues"]?[entry.Key])) globals[entry.Key] = entry.Value?.DeepClone();
        var removed = new JsonArray();
        foreach (var profile in initial)
            if (!current.Any(p => p?["index"] != null && Int(p["index"]) == Int(profile?["index"]))) removed.Add(profile!["index"]!.DeepClone());
        return new JsonObject { ["revision"] = _lsInitial["revision"]?.DeepClone(), ["globalValues"] = globals, ["profiles"] = changes, ["remove"] = removed };
    }

    private async Task LsSave()
    {
        if (_lsBusy || _lsEditor == null || !LsDirty) return;
        var request = LsSaveRequest();
        _lsSaving = true; Repaint();
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.CallLong("save_lossless_settings", 40, request) is not JsonObject response) throw new IOException("没有收到保存结果");
            LsReadResponse(response, true); _lsError = null;
            Toast(Str(response["warning"]) is { Length: > 0 } warning ? warning : "小黄鸭设置已同步");
        }
        catch (Exception ex) { _lsError = ex.Message; Toast($"没有保存：{ex.Message}"); }
        finally { _lsSaving = false; }
        if (_stOpen) Repaint();
    }

    private async Task LsRun(string method, params JsonNode?[] args)
    {
        if (_lsBusy) return;
        _lsSaving = true; Repaint();
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.CallLong(method, 30, args) is JsonObject response && method != "open_lossless") LsReadResponse(response, false);
            _lsError = null;
            if (method == "configure_lossless")
            {
                _lsEditor = _lsInitial = null;
                if (_stBrowseLossless) StBrowseTo(null);
            }
        }
        catch (Exception ex) { _lsError = ex.Message; Toast(ex.Message); }
        finally { _lsSaving = false; }
        if (_stOpen) Repaint();
    }

    private void BuildStLossless()
    {
        _stRows.Add(new PerfRow { Section = true, Title = "小黄鸭" });
        bool found = Bool(_lsConfig?["configured"]), running = Bool(_lsConfig?["running"]);
        _stRows.Add(StInfo("本机状态", found ? running ? "运行中" : "未运行" : _lsLoading ? "正在检测…" : "未找到小黄鸭"));
        if (_lsError != null) _stRows.Add(StInfo("提示", _lsError));
        if (_lsConfig == null && !_lsBusy && _lsError == null) _ = LsLoad();
        if (!found)
        {
            _stRows.Add(new PerfRow { Title = "重新检测", Disabled = _lsBusy, Press = () => { _ = LsLoad(true); } });
            _stRows.Add(new PerfRow { Title = "选择程序…", Value = "LosslessScaling.exe", Disabled = _lsBusy,
                Press = () => { _stBrowseLossless = true; StBrowseTo(""); } });
            return;
        }
        if (_lsEditor?["profiles"] is not JsonArray profiles || profiles.Count == 0)
        {
            _stRows.Add(StAction("读取小黄鸭设置", "ls-reload", _lsLoading ? "正在读取…" : "", () => { _ = LsReload(); }));
            return;
        }
        _lsProfile = Math.Clamp(_lsProfile, 0, profiles.Count - 1);
        var profile = (JsonObject)profiles[_lsProfile]!;
        var values = (JsonObject)profile["values"]!;
        bool defaultProfile = Bool(profile["default"]);
        _stRows.Add(new PerfRow { Title = "保存并应用", Value = _lsSaving ? "正在同步…" : LsDirty ? "待保存" : "已同步", Disabled = _lsBusy || !LsDirty, Press = () => { _ = LsSave(); } });
        _stRows.Add(StAction("重新读取设置", "ls-reload", "", () => { _ = LsReload(); }));
        _stRows.Add(new PerfRow { Section = true, Title = "配置" });
        var names = profiles.Select(p => Str(p?["values"]?["Title"]) ?? "未命名").ToArray();
        _stRows.Add(StPick("当前配置", names, _lsProfile, i => { _lsProfile = i; Repaint(); }));
        if (!defaultProfile)
        {
            _stRows.Add(new PerfRow { Title = "配置名称", Value = Str(values["Title"]) ?? "", Disabled = _lsSaving, Press = () => StEditText(Str(values["Title"]) ?? "", text =>
            {
                if (string.IsNullOrWhiteSpace(text)) { Toast("请填写配置名称"); return; }
                values["Title"] = text.Trim(); Repaint();
            }) });
            _stRows.Add(new PerfRow { Title = "匹配程序或窗口", Value = Str(values["Path"]) ?? "", Disabled = _lsSaving, Press = () => StEditText(Str(values["Path"]) ?? "", text =>
            {
                if (string.IsNullOrWhiteSpace(text)) { Toast("请填写程序路径或窗口匹配条件"); return; }
                values["Path"] = text.Trim(); Repaint();
            }) });
        }
        BuildLsFields(_lsEditor["profileSchema"] as JsonArray, values);
        BuildLsFields(_lsEditor["globalSchema"] as JsonArray, (JsonObject)_lsEditor["globalValues"]!);
        _stRows.Add(new PerfRow { Section = true, Title = "配置管理" });
        _stRows.Add(new PerfRow { Title = "添加游戏配置…", Value = "复制当前配置", Disabled = _lsSaving, Press = () => LsBrowseProfile(profile) });
        if (!defaultProfile)
            _stRows.Add(StAction("删除当前配置", "ls-delete-profile", "保存后生效", () =>
            {
                if (!_lsSaving && StConfirm("ls-delete-profile")) { profiles.RemoveAt(_lsProfile); _lsProfile = 0; Repaint(); }
            }));
        _stRows.Add(new PerfRow { Title = running ? "停止小黄鸭后台" : "启动小黄鸭后台", Disabled = _lsBusy, Press = () => { _ = LsRun("set_lossless_running", JsonValue.Create(!running)); } });
        _stRows.Add(new PerfRow { Title = "打开小黄鸭原版", Disabled = _lsBusy, Press = () => { _ = LsRun("open_lossless"); } });
    }

    private void BuildLsFields(JsonArray? groups, JsonObject values, Action<string, JsonNode?>? changed = null)
    {
        if (groups == null) return;
        foreach (var group in groups.OfType<JsonObject>())
        {
            var fields = (group["fields"] as JsonArray)?.OfType<JsonObject>().Where(f => LsFieldVisible(f, values)).ToArray() ?? Array.Empty<JsonObject>();
            if (fields.Length == 0) continue;
            _stRows.Add(new PerfRow { Section = true, Title = Str(group["title"]) ?? "" });
            foreach (var field in fields)
            {
                string key = Str(field["key"])!, title = Str(field["title"])!;
                var value = values[key] ?? field["default"];
                void Set(JsonNode? next)
                {
                    values[key] = next?.DeepClone();
                    if (changed != null) changed(key, next?.DeepClone()); else Repaint();
                }
                PerfRow row;
                switch (Str(field["kind"]))
                {
                    case "toggle": row = StToggle(title, Bool(value), on => Set(JsonValue.Create(on)), Bool(field["default"])); break;
                    case "number":
                        if (field["choices"] is JsonArray deviceChoices)
                        {
                            var devices = deviceChoices.OfType<JsonObject>().ToList();
                            int selected = Int(value);
                            if (devices.All(c => Int(c["value"]) != selected)) devices.Add(new JsonObject { ["value"] = selected, ["label"] = $"设备 {selected}（未连接）" });
                            row = StPick(title, devices.Select(c => Str(c["label"]) ?? "").ToArray(), Math.Max(0, devices.FindIndex(c => Int(c["value"]) == selected)), i => Set(JsonValue.Create(Int(devices[i]["value"]))));
                            break;
                        }
                        double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double n) ? n : Int(node);
                        row = MgNumber(title, Number(value), Number(field["min"]), Number(field["max"]), Number(field["step"]), Bool(field["integer"]), Number(field["default"]), n => Set(JsonValue.Create(n)));
                        if (Number(value) == 0 && Str(field["zeroLabel"]) is { } zero) row.Value = zero;
                        break;
                    case "pick":
                        var choices = ((JsonArray)field["choices"]!).OfType<JsonObject>().ToList();
                        string current = Str(value) ?? "";
                        if (choices.All(c => Str(c["value"]) != current)) choices.Insert(0, new JsonObject { ["value"] = current, ["label"] = $"保留当前值（{current}）" });
                        int at = Math.Max(0, choices.FindIndex(c => Str(c["value"]) == current));
                        row = StPick(title, choices.Select(c => Str(c["label"]) ?? "").ToArray(), at, i => Set(JsonValue.Create(Str(choices[i]["value"]))));
                        break;
                    default:
                        row = new PerfRow { Title = title, Value = Str(value) ?? "", Press = () => StEditText(Str(value) ?? "", text => Set(JsonValue.Create(text.Trim()))) };
                        break;
                }
                row.Disabled = changed == null && _lsSaving;
                _stRows.Add(row);
            }
        }
    }

    private static bool LsFieldVisible(JsonObject field, JsonObject values)
    {
        if (field["when"] is not JsonObject conditions) return true;
        foreach (var condition in conditions)
            if (condition.Value is JsonArray allowed && !allowed.Any(value => JsonNode.DeepEquals(value, values[condition.Key]))) return false;
        return true;
    }

    private void LsBrowseProfile(JsonObject source)
    {
        _stBrowseExe = true;
        _stBrowsePick = path =>
        {
            var profile = (JsonObject)source.DeepClone();
            profile["index"] = null; profile["default"] = false;
            profile["values"]!["Title"] = Path.GetFileNameWithoutExtension(path);
            profile["values"]!["Path"] = path;
            profile["values"]!["AutoScale"] = true;
            var profiles = (JsonArray)_lsEditor!["profiles"]!;
            profiles.Add((JsonNode)profile); _lsProfile = profiles.Count - 1;
            StBrowseTo(null);
        };
        StBrowseTo("");
    }

    private void BuildLsBrowseExes(string path)
    {
        try
        {
            foreach (var exe in Directory.GetFiles(path, "*.exe").Where(f => string.Equals(Path.GetFileName(f), "LosslessScaling.exe", StringComparison.OrdinalIgnoreCase)))
                _stRows.Add(new PerfRow { Title = Path.GetFileName(exe), Value = "选择", Disabled = _lsBusy, Press = () => { _ = LsRun("configure_lossless", JsonValue.Create("local"), JsonValue.Create(exe)); } });
        }
        catch (Exception ex) { _stRows.Add(StInfo("无法读取程序", ex.Message)); }
    }
}
