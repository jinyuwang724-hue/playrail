using System.Text.Json.Nodes;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private JsonObject? _osdState, _osdDraft;
    private bool _osdLoading, _osdUnavailable;
    private SettingsWriteQueue? _osdWrites;
    private readonly Dictionary<string, string> _osdSelectedMetric = new();

    private async Task OsdLoad()
    {
        if (_osdLoading || _osdWrites?.IsBusy == true) return;
        _osdLoading = true;
        long revision = _osdWrites?.Revision ?? 0;
        try
        {
            _agent ??= new AgentClient();
            var state = await _agent.Call("get_osd_state") as JsonObject;
            if (state != null && revision == (_osdWrites?.Revision ?? 0) && _osdWrites?.IsBusy != true)
            {
                _osdState = (JsonObject)state.DeepClone();
                _osdDraft = state["settings"]?.DeepClone() as JsonObject;
            }
            _osdUnavailable = false;
        }
        catch (Exception ex) { _osdUnavailable = true; Log.Warn("OSD settings: " + ex.Message); }
        finally { _osdLoading = false; }
        if (_stOpen && _gsItem == null && _stCat == StOsdCat) Repaint();
    }

    private void OsdSave(JsonObject next)
    {
        _osdDraft = (JsonObject)next.DeepClone();
        _osdWrites ??= new SettingsWriteQueue(ex => Toast("显示设置未能保存：" + ex.Message), () => _ = OsdLoad());
        var snapshot = next.DeepClone();
        Repaint();
        _ = _osdWrites.Enqueue("osd", 150, async () =>
        {
            _agent ??= new AgentClient();
            await _agent.Call("save_osd", snapshot);
        });
    }

    private void BuildStOsd()
    {
        _stRows.Add(new PerfRow { Section = true, Title = "性能显示" });
        if (_osdDraft == null)
        {
            _stRows.Add(StInfo("显示设置", _osdUnavailable ? "连不上工具箱后台" : "正在读取…"));
            return;
        }
        BuildOsdOptions(_osdDraft, OsdSave, _osdState?["catalog"] as JsonArray, _osdState?["layoutDefaults"] as JsonObject, "global");
        if (Str(_osdState?["error"]) is { Length: > 0 } error) _stRows.Add(StInfo("显示状态", error));
    }

    // Global and per-game OSD use exactly the same existing setting-row components.
    private void BuildOsdOptions(JsonObject osd, Action<JsonObject> save, JsonArray? catalog, JsonObject? defaults, string scope)
    {
        void Change(string key, JsonNode value) { var next = (JsonObject)osd.DeepClone(); next[key] = value.DeepClone(); save(next); }
        var layout = osd["layout"] as JsonObject ?? defaults;
        bool custom = Bool(layout?["custom"]);
        _stRows.Add(StToggle("显示性能数据", Bool(osd["enabled"]), v => Change("enabled", JsonValue.Create(v))));
        if (scope == "global")
        {
            _stRows.Add(StPick("悬浮窗位置", new[] { "左上", "上方居中", "右上", "左侧居中", "屏幕居中", "右侧居中", "左下", "下方居中", "右下" }, Int(osd["position"]), i => Change("position", JsonValue.Create(i)), 0));
            _stRows.Add(StNumber("缩放", Int(osd["scale"], 100), 75, 200, 25, n => $"{n}%", n => Change("scale", JsonValue.Create(n)), 100));
            _stRows.Add(StPick("显示档位", new[] { "1 · FPS 数字", "2 · 横向摘要", "3 · 标准列表", "4 · 完整监控" }, Int(osd["level"], 2) - 1, i => Change("level", JsonValue.Create(i + 1)), 1));
            string[] globalSizes = { "small", "medium", "large" };
            _stRows.Add(StPick("字号", new[] { "小", "中", "大" }, Math.Max(0, Array.IndexOf(globalSizes, Str(osd["size"]))), i => Change("size", JsonValue.Create(globalSizes[i])), 1));
            return;
        }
        _stRows.Add(StPick("显示布局", new[] { "一档", "二档", "三档", "四档", "自定义" }, custom ? 4 : Int(osd["level"], 2) - 1, i =>
        {
            var next = (JsonObject)osd.DeepClone();
            if (i < 4) next["level"] = i + 1;
            if (layout != null)
            {
                var part = (JsonObject)layout.DeepClone(); part["custom"] = i == 4; next["layout"] = part;
            }
            save(next);
        }, 1));
        string[] sizes = { "small", "medium", "large" };
        _stRows.Add(StPick("字号", new[] { "小", "中", "大" }, Math.Max(0, Array.IndexOf(sizes, Str(osd["size"]))), i => Change("size", JsonValue.Create(sizes[i])), 1));
        if (!custom) _stRows.Add(StPick("行数", new[] { "一行", "两行" }, Int(osd["rows"], 1) - 1, i => Change("rows", JsonValue.Create(i + 1)), 0));
        _stRows.Add(StToggle("缩小单位", Bool(osd["smallUnits"]), v => Change("smallUnits", JsonValue.Create(v))));
        if (!custom || layout == null || catalog == null || catalog.Count == 0) return;
        void Layout(Action<JsonObject> edit)
        {
            var next = (JsonObject)osd.DeepClone(); var part = (JsonObject)layout.DeepClone();
            edit(part); next["layout"] = part; save(next);
        }
        void Field(string key, int value) => Layout(p => p[key] = value);
        _stRows.Add(new PerfRow { Section = true, Title = "排版" });
        _stRows.Add(StPick("位置", new[] { "左上", "上方居中", "右上", "左侧居中", "屏幕居中", "右侧居中", "左下", "下方居中", "右下" }, Int(layout["anchor"]), i => Field("anchor", i), 0));
        _stRows.Add(StNumber("水平边距", Int(layout["x"], 12), 0, 500, 1, n => $"{n} px", n => Field("x", n), 12));
        _stRows.Add(StNumber("垂直边距", Int(layout["y"], 12), 0, 500, 1, n => $"{n} px", n => Field("y", n), 12));
        _stRows.Add(StNumber("项目间距", Int(layout["spacing"], 2), 1, 8, 1, n => $"{n}", n => Field("spacing", n), 2));
        _stRows.Add(OsdColourRow("文字颜色", Str(layout["color"]) ?? "FFFFFF", false, c => Layout(p => p["color"] = c)));
        _stRows.Add(StNumber("文字不透明度", Int(layout["opacity"], 100), 10, 100, 5, n => $"{n} %", n => Field("opacity", n), 100));
        _stRows.Add(StNumber("背景不透明度", Int(layout["background"]), 0, 100, 5, n => $"{n} %", n => Field("background", n), 0));
        _stRows.Add(StToggle("防烧屏偏移", Bool(layout["antiBurnIn"]), on => Layout(p => p["antiBurnIn"] = on)));
        _stRows.Add(new PerfRow { Section = true, Title = "显示项目" });
        var items = layout["items"] as JsonArray;
        if (items == null || items.Count == 0) return;
        string[] ids = catalog.Select(c => Str(c?["id"]) ?? "").ToArray();
        string selected = _osdSelectedMetric.GetValueOrDefault(scope, "fps");
        int pick = Math.Max(0, Array.IndexOf(ids, selected)); selected = ids[pick];
        _stRows.Add(StPick("选择项目", catalog.Select(c => Str(c?["name"]) ?? "").ToArray(), pick, i => { _osdSelectedMetric[scope] = ids[i]; Repaint(); }));
        int index = items.ToList().FindIndex(p => Str(p?["id"]) == selected);
        if (index < 0 || items[index] is not JsonObject item) return;
        void Item(string key, JsonNode value) => Layout(p => ((JsonObject)((JsonArray)p["items"]!)[index]!)[key] = value.DeepClone());
        _stRows.Add(StToggle("显示此项目", Bool(item["enabled"]), on => Item("enabled", JsonValue.Create(on))));
        _stRows.Add(StNumber("所在行", Int(item["row"]) + 1, 1, 8, 1, n => $"第 {n} 行", n => Item("row", JsonValue.Create(n - 1)), 1));
        _stRows.Add(StNumber("排列顺序", index + 1, 1, items.Count, 1, n => $"{n} / {items.Count}", n => Layout(p =>
        {
            var list = (JsonArray)p["items"]!; var moving = list[index]; list.RemoveAt(index); list.Insert(n - 1, moving);
        }), 1));
        _stRows.Add(OsdColourRow("项目颜色", Str(item["color"]) ?? "", true, c => Item("color", JsonValue.Create(c))));
        _stRows.Add(new PerfRow { Title = "恢复默认排版", Press = () =>
        {
            if (defaults == null) return;
            var next = (JsonObject)osd.DeepClone(); var part = (JsonObject)defaults.DeepClone(); part["custom"] = true;
            next["layout"] = part; save(next);
        } });
    }

    private PerfRow OsdColourRow(string title, string current, bool automatic, Action<string> set)
    {
        var colors = new List<(string Name, string Value)>();
        if (automatic) colors.Add(("自动", ""));
        colors.AddRange(new[] { ("白色", "FFFFFF"), ("绿色", "A6E3A1"), ("蓝色", "89B4FA"), ("紫色", "CBA6F7"), ("橙色", "FAB387"), ("黄色", "F9E2AF"), ("青色", "89DCEB") });
        int selected = colors.FindIndex(c => c.Value.Equals(current, StringComparison.OrdinalIgnoreCase));
        var names = colors.Select(c => c.Name).Append("自定义").ToArray();
        return StPick(title, names, selected < 0 ? colors.Count : selected, i =>
        {
            if (i < colors.Count) { set(colors[i].Value); return; }
            _settingsEdit.End();
            StEditText(current.Length == 0 ? "#FFFFFF" : "#" + current, text =>
            {
                string color = text.Trim().TrimStart('#');
                if (color.Length != 6 || !color.All(Uri.IsHexDigit)) { Toast("请输入六位 RGB 色值，例如 FFFFFF"); return; }
                set(color.ToUpperInvariant());
            });
        }, 0);
    }
}
