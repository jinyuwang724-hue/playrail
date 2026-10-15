// 控制中心 → 性能：3:7 两栏，背景就是控制中心那层动态背景。
// 左栏只看（最近 60 秒功耗、风扇曲线和读数），右栏只改（功耗、CPU、风扇）；左右上下边对齐，左栏没有分隔线，底部不放按键条。
// 所有设置都经工具箱后台 agent 读写（AgentClient）：改一下就保存（350 ms 内的连续改动合并成一次），每秒取一次状态。
// 手柄：↑ ↓ 选项，← → 改值，A 开关 / 进入「编辑曲线」（← → 选温度点，↑ ↓ 改 1 %，A / B 写入退出），B 回控制中心。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class PerfRow
    {
        public string Title = "";
        public string Value = "";
        public string[]? Choices;
        public int Choice = -1;
        public bool Section, Disabled, Numeric;
        public Action<int>? Step;
        public Action? Press;
    }

    // 设计 px（× _ccK），1280×800 舞台：左栏 348、右栏 812、中间 40
    private const float PerfLeftX = 40, PerfLeftW = 348, PerfRightX = 428, PerfRightW = 812, PerfTop = 96;
    private const float PerfRowH = 42, PerfSecH = 38;
    private static readonly string[] FanModes = { "firmware", "quiet", "balanced", "performance" };
    private static readonly string[] FanModeNames = { "固件", "静音", "均衡", "性能" };
    private static readonly string[] PowerModes = { "smart", "auto", "manual" };
    private static readonly string[] PowerModeNames = { "智能", "按电源", "手动" };
    private static readonly Color PerfOrange = Gfx.Rgba(0xfd, 0x8a, 0x5a);

    private AgentClient? _agent;
    private ContainerVisual? _perf;
    private SpriteVisual _perfTitle = null!, _perfLeft = null!, _perfRight = null!;
    private bool _perfOpen, _perfCurveEdit, _perfBusy, _perfAgentDown;
    private int _perfFocus, _perfPoint, _perfVersion, _perfEdits, _perfSavedEdits;
    private JsonNode? _perfView;
    private JsonObject? _perfS;
    private readonly List<PerfRow> _perfRows = new();
    private readonly Queue<double> _perfPower = new();
    private DispatcherQueueTimer? _perfTimer;

    // ---------------- 开 / 关 ----------------

    private void OpenPerf()
    {
        if (_cc == null) return;
        EnsurePerfVisuals();
        _perfOpen = true;
        _perfCurveEdit = false;
        _perfFocus = -1;
        int v = ++_perfVersion;
        BuildPerfRows();
        _perfFocus = _perfRows.FindIndex(r => !r.Section);
        PaintPerf();
        // 控制中心的滑条、标题、说明、左下图标淡出；右上状态留着
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        _perf!.IsVisible = true;
        _perf.Opacity = 0;
        _perf.StartAnimation("Opacity", ToScalar(1, 240, EaseOutCss, 80));
        _perfTimer ??= MakePerfTimer();
        _perfTimer.Start();
        _ = PerfRefresh(true);
        Log.Info("perf page open");
    }

    private void ClosePerf(bool instant = false)
    {
        if (!_perfOpen || _perf == null) return;
        _perfOpen = false;
        _perfCurveEdit = false;
        _perfTimer?.Stop();
        int v = ++_perfVersion;
        if (_perfEdits != _perfSavedEdits) _ = PerfSave();
        if (instant || Reduced)
        {
            _perf.StopAnimation("Opacity"); _perf.IsVisible = false; _perf.Opacity = 1;
            foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); vis.Opacity = 1; }
            return;
        }
        _perf.StartAnimation("Opacity", ToScalar(0, 140, EaseOutCss));
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(1, 220, EaseOutCss, 60));
        After(160, () => { if (v == _perfVersion && !_perfOpen && _perf != null) { _perf.IsVisible = false; _perf.StopAnimation("Opacity"); _perf.Opacity = 1; } });
        Log.Info("perf page close");
    }

    /// <summary>进性能页时让位的控制中心部件（右上状态不在里面）。</summary>
    private IEnumerable<Visual> CcPageParts()
    {
        if (_ccRow != null) yield return _ccRow;
        if (_ccTitle != null) yield return _ccTitle;
        if (_ccLabel != null) yield return _ccLabel;
        foreach (var b in _ccDock) yield return b.El;
        if (_ccChrome != null) yield return _ccPowerLayer;
    }

    private void EnsurePerfVisuals()
    {
        if (_perf != null) return;
        _perf = G.Container();
        _perf.IsVisible = false;
        _perfTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _perfLeft = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _perfRight = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        foreach (var s in new[] { _perfTitle, _perfLeft, _perfRight }) _perf.Children.InsertAtTop(s);
        _cc!.Children.InsertAtTop(_perf);
    }

    private DispatcherQueueTimer MakePerfTimer()
    {
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        t.IsRepeating = true;
        t.Tick += (_, _) => { if (_perfOpen) _ = PerfRefresh(true); };
        return t;
    }

    // ---------------- 和 agent 打交道 ----------------

    private async Task PerfRefresh(bool sample)
    {
        if (_perfBusy) return;
        _perfBusy = true;
        try
        {
            _agent ??= new AgentClient();
            var view = await _agent.Call("get_power_state", JsonValue.Create(sample));
            _perfAgentDown = false;
            PerfApplyView(view, fromSave: false);
        }
        catch (Exception ex)
        {
            if (!_perfAgentDown) { Log.Warn($"perf refresh: {ex.Message}"); if (_perfOpen) Toast("连不上工具箱后台：功耗和风扇暂时改不了"); }
            _perfAgentDown = true;
            if (_perfOpen) PaintPerf();
        }
        finally { _perfBusy = false; }
    }

    private void PerfApplyView(JsonNode? view, bool fromSave)
    {
        if (view == null) return;
        _perfView = view;
        // 没有没存的改动时，本地设置跟着后台走（别的前端改了也能看到）
        if (_perfEdits == _perfSavedEdits && view["settings"] is JsonObject s) _perfS = (JsonObject)s.DeepClone();
        if (!fromSave && view["actual"]?["package"] is JsonValue pv && pv.TryGetValue(out double pkg))
        {
            _perfPower.Enqueue(pkg);
            while (_perfPower.Count > 60) _perfPower.Dequeue();
        }
        if (!_perfOpen) return;
        BuildPerfRows();
        PaintPerf();
    }

    /// <summary>改了设置：350 ms 内的连续改动合成一次保存。</summary>
    private void PerfChanged()
    {
        int edits = ++_perfEdits;
        BuildPerfRows();
        PaintPerf();
        After(350, () => { if (edits == _perfEdits) _ = PerfSave(); });
    }

    private async Task PerfSave()
    {
        if (_perfS == null || _agent == null) return;
        int edits = _perfEdits;
        var snapshot = _perfS.DeepClone();
        try
        {
            var view = await _agent.Call("save_power", snapshot);
            _perfSavedEdits = Math.Max(_perfSavedEdits, edits);
            PerfApplyView(view, fromSave: true);
        }
        catch (Exception ex)
        {
            _perfSavedEdits = Math.Max(_perfSavedEdits, edits);   // 失败的改动不再重试，下一次取状态时回到后台的值
            Log.Warn($"perf save: {ex.Message}");
            Toast($"没有保存：{ex.Message}");
        }
    }

    private async Task PerfSetEcore(bool on)
    {
        if (_agent == null) return;
        try { PerfApplyView(await _agent.Call("set_ecore", JsonValue.Create(on)), fromSave: true); Toast(on ? "模拟全核心 E 核：开，重启后生效" : "模拟全核心 E 核：关，重启后生效"); }
        catch (Exception ex) { Toast($"没有改成：{ex.Message}"); }
    }

    // ---------------- 读设置的小工具 ----------------

    private static int Int(JsonNode? n, int fallback = 0) => n is JsonValue v && v.TryGetValue(out int i) ? i : n is JsonValue d && d.TryGetValue(out double x) ? (int)Math.Round(x) : fallback;
    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double x) ? x : null;
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;

    private bool PerfCharger => Bool(_perfView?["charger"]);

    /// <summary>当前功耗设置一句话：智能 · 8–25 W / 按电源 · 25 W / 手动 · 20 W / 未接管。</summary>
    private string PerfPresetText()
    {
        var s = _perfS;
        if (s == null) return "—";
        if (!Bool(s["enabled"])) return "未接管";
        bool charger = PerfCharger;
        return (Str(s["mode"]) ?? "smart") switch
        {
            "smart" => $"智能 · {Int(s["smart"]?["floor"], 8)}–{Int(s["smart"]?[charger ? "acMax" : "dcMax"], 25)} W",
            "auto" => $"按电源 · {Int(s[charger ? "ac" : "dc"]?["tdp"], 25)} W",
            _ => $"手动 · {Int(s["manual"]?["tdp"], 20)} W",
        };
    }

    private static string Ghz(int mhz) => mhz <= 0 ? "不限" : $"{mhz / 1000.0:0.##} GHz";

    /// <summary>实测（2026-10-12）占空比 → 转速：40 % 以下贴着约 2800 转的底。</summary>
    private static double FanRpm(double duty)
    {
        double[] d = { 0, 40, 50, 60, 70, 80, 100 }, r = { 2800, 2800, 3060, 3680, 4300, 4930, 6100 };
        if (duty <= 0) return r[0];
        for (int i = 1; i < d.Length; i++)
            if (duty <= d[i]) return r[i - 1] + (r[i] - r[i - 1]) * (duty - d[i - 1]) / (d[i] - d[i - 1]);
        return r[^1];
    }

    // ---------------- 右栏的行 ----------------

    private void BuildPerfRows()
    {
        _perfRows.Clear();
        var s = _perfS;
        if (s == null)
        {
            _perfRows.Add(new PerfRow { Section = true, Title = "功耗" });
            _perfRows.Add(new PerfRow { Title = _perfAgentDown ? "连不上工具箱后台" : "正在读取…", Value = "—", Disabled = true });
            return;
        }
        bool on = Bool(s["enabled"]);
        string mode = Str(s["mode"]) ?? "smart";
        var smart = s["smart"]!;
        PerfRow Choice(string title, string[] names, int idx, Action<int> set, bool disabled = false) => new()
        {
            Title = title, Choices = names, Choice = idx, Disabled = disabled,
            Step = d => { if (!disabled) set(Math.Clamp(idx + d, 0, names.Length - 1)); },
            Press = () => { if (!disabled) set((idx + 1) % names.Length); },
        };
        PerfRow Number(string title, int value, int min, int max, Func<int, string> fmt, Action<int> set, int step = 1, bool disabled = false) => new()
        {
            Title = title, Value = fmt(value), Numeric = true, Disabled = disabled,
            Step = d => { if (!disabled) { int nv = Math.Clamp(value + d * step, min, max); if (nv != value) set(nv); } },
        };
        PerfRow List(string title, int[] values, int current, Func<int, string> fmt, Action<int> set, bool disabled = false)
        {
            int idx = Math.Max(0, Array.IndexOf(values, current));
            return new()
            {
                Title = title, Value = fmt(current), Numeric = true, Disabled = disabled,
                Step = d => { if (!disabled) { int ni = Math.Clamp(idx + d, 0, values.Length - 1); if (ni != idx) set(values[ni]); } },
            };
        }

        _perfRows.Add(new PerfRow { Section = true, Title = "功耗" });
        _perfRows.Add(Choice("功耗接管", new[] { "关", "开" }, on ? 1 : 0, i => { s["enabled"] = i == 1; PerfChanged(); }));
        _perfRows.Add(Choice("模式", PowerModeNames, Math.Max(0, Array.IndexOf(PowerModes, mode)), i => { s["mode"] = PowerModes[i]; PerfChanged(); }, !on));
        if (mode == "smart")
        {
            int[] targets = (_perfView?["targets"] as JsonArray)?.Select(n => Int(n)).ToArray() is { Length: > 0 } t ? t : new[] { 0, 30, 40, 45, 60, 90, 120 };
            int floor = Int(smart["floor"], 8);
            _perfRows.Add(List("目标帧率", targets, Int(smart["target"]), f => f == 0 ? "自动" : $"{f} 帧", f => { smart["target"] = f; PerfChanged(); }, !on));
            _perfRows.Add(Number("功耗下限", floor, 8, 20, w => $"{w} W", w =>
            {
                smart["floor"] = w;
                if (Int(smart["acMax"]) < w) smart["acMax"] = w;
                if (Int(smart["dcMax"]) < w) smart["dcMax"] = w;
                PerfChanged();
            }, disabled: !on));
            _perfRows.Add(Number("插电上限", Int(smart["acMax"], 35), floor, 35, w => $"{w} W", w => { smart["acMax"] = w; PerfChanged(); }, disabled: !on));
            _perfRows.Add(Number("电池上限", Int(smart["dcMax"], 25), floor, 35, w => $"{w} W", w => { smart["dcMax"] = w; PerfChanged(); }, disabled: !on));
        }
        else if (mode == "auto")
        {
            _perfRows.Add(Number("插电功耗", Int(s["ac"]!["tdp"], 35), 8, 35, w => $"{w} W", w => { s["ac"]!["tdp"] = w; PerfChanged(); }, disabled: !on));
            _perfRows.Add(Number("电池功耗", Int(s["dc"]!["tdp"], 25), 8, 35, w => $"{w} W", w => { s["dc"]!["tdp"] = w; PerfChanged(); }, disabled: !on));
        }
        else
        {
            _perfRows.Add(Number("固定功耗", Int(s["manual"]!["tdp"], 20), 8, 35, w => $"{w} W", w => { s["manual"]!["tdp"] = w; PerfChanged(); }, disabled: !on));
        }
        _perfRows.Add(Number("短时加成", Int(s["boost"], 1), 0, 15, w => w == 0 ? "关" : $"+{w} W", w => { s["boost"] = w; PerfChanged(); }, disabled: !on));

        _perfRows.Add(new PerfRow { Section = true, Title = "CPU" });
        int[] freqs = (_perfView?["limits"]?["freq"] as JsonArray)?.Select(n => Int(n)).ToArray() is { Length: > 0 } fq ? fq : new[] { 0, 1500, 2000, 2500, 2750, 3000, 3500, 4000 };
        bool manual = mode == "manual";
        int freq = manual ? Int(s["manual"]!["freq"]) : Int(s[PerfCharger ? "ac" : "dc"]!["freq"]);
        _perfRows.Add(List("固定 CPU 频率", freqs, freq, Ghz, f =>
        {
            if (manual) s["manual"]!["freq"] = f; else { s["ac"]!["freq"] = f; s["dc"]!["freq"] = f; }
            PerfChanged();
        }, !on));
        bool ecore = Bool(_perfView?["ecore"]?["enabled"]), pending = Bool(_perfView?["ecore"]?["pending"]);
        var er = Choice("模拟全核心 E 核", new[] { "关", "开" }, ecore ? 1 : 0, i => _ = PerfSetEcore(i == 1));
        er.Value = pending ? "重启后生效" : "";
        _perfRows.Add(er);

        _perfRows.Add(new PerfRow { Section = true, Title = "风扇" });
        var fan = s["fan"]!;
        string fmode = Str(fan["mode"]) ?? "firmware";
        bool firmware = fmode == "firmware";
        _perfRows.Add(Choice("风扇模式", FanModeNames, Math.Max(0, Array.IndexOf(FanModes, fmode)), i => { fan["mode"] = FanModes[i]; PerfChanged(); }));
        _perfRows.Add(new PerfRow
        {
            Title = "编辑这档曲线", Value = firmware ? "固件控制时不能改" : _perfCurveEdit ? "编辑中 · ← → 选点  ↑ ↓ 改" : "6 个温度点 ›", Disabled = firmware,
            Press = () => { if (!firmware) { _perfCurveEdit = !_perfCurveEdit; _perfPoint = Math.Clamp(_perfPoint, 0, 5); BuildPerfRows(); PaintPerf(); } },
        });
        _perfRows.Add(Choice("全速运转", new[] { "关", "开" }, Bool(fan["full"]) ? 1 : 0, i => { fan["full"] = i == 1; PerfChanged(); }));
        _perfRows.Add(new PerfRow
        {
            Title = "恢复这档默认曲线", Value = "›", Disabled = firmware,
            Press = () =>
            {
                if (firmware || _perfView?["fan"]?["presets"]?[fmode] is not JsonArray preset) return;
                if (fan["curves"] is not JsonObject curves) fan["curves"] = curves = new JsonObject();
                curves[fmode] = preset.DeepClone();
                PerfChanged();
                Toast($"{FanModeNames[Array.IndexOf(FanModes, fmode)]}档曲线已恢复默认");
            },
        });
        if (_perfFocus >= _perfRows.Count) _perfFocus = _perfRows.FindLastIndex(r => !r.Section);
    }

    /// <summary>这档风扇曲线的 6 个占空比（没存过就用默认）。</summary>
    private int[] PerfCurve(string mode)
    {
        var src = _perfS?["fan"]?["curves"]?[mode] as JsonArray ?? _perfView?["fan"]?["presets"]?[mode] as JsonArray;
        return src?.Select(n => Int(n)).ToArray() is { Length: 6 } c ? c : new[] { 20, 30, 50, 63, 76, 76 };
    }

    // ---------------- 输入 ----------------

    private void PerfMove(Direction dir)
    {
        if (_perfCurveEdit)
        {
            string mode = Str(_perfS?["fan"]?["mode"]) ?? "balanced";
            if (dir is Direction.Left or Direction.Right) { _perfPoint = Math.Clamp(_perfPoint + (dir == Direction.Left ? -1 : 1), 0, 5); PaintPerf(); return; }
            var c = PerfCurve(mode);
            int cap = Int(_perfView?["fan"]?["caps"]?[mode], 100);
            int nv = Math.Clamp(c[_perfPoint] + (dir == Direction.Up ? 1 : -1), 0, cap);
            if (nv == c[_perfPoint]) return;
            c[_perfPoint] = nv;
            var fan = _perfS!["fan"]!;
            if (fan["curves"] is not JsonObject curves) fan["curves"] = curves = new JsonObject();
            curves[mode] = new JsonArray(c.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            PerfChanged();
            return;
        }
        if (dir is Direction.Up or Direction.Down)
        {
            int d = dir == Direction.Up ? -1 : 1;
            for (int i = _perfFocus + d; i >= 0 && i < _perfRows.Count; i += d)
                if (!_perfRows[i].Section) { _perfFocus = i; PaintPerf(); return; }
            return;
        }
        if (_perfFocus >= 0 && _perfFocus < _perfRows.Count) _perfRows[_perfFocus].Step?.Invoke(dir == Direction.Left ? -1 : 1);
    }

    private void PerfActivate()
    {
        if (_perfCurveEdit) { _perfCurveEdit = false; BuildPerfRows(); PaintPerf(); return; }
        if (_perfFocus >= 0 && _perfFocus < _perfRows.Count) _perfRows[_perfFocus].Press?.Invoke();
    }

    private void PerfBack()
    {
        if (_perfCurveEdit) { _perfCurveEdit = false; BuildPerfRows(); PaintPerf(); return; }
        ClosePerf();
    }

    // ---------------- 绘制 ----------------

    private float PerfListHeight() => _perfRows.Sum(r => r.Section ? PerfSecH : PerfRowH);

    private void PaintPerf()
    {
        if (_perf == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        float h = PerfListHeight();
        // 标题：和控制中心的「控制中心」同一个位置、同一个字
        using (var tl = TextKit.Layout(G.Device, "性能", TextKit.Ui(700), 24, 400, CanvasHorizontalAlignment.Left))
        {
            var bb = tl.LayoutBounds;
            float w = (float)bb.Width + 4, th = (float)bb.Height + 4;
            CcSet(_perfTitle, CcDraw(w, th, raster, ds => ds.DrawTextLayout(tl, -(float)bb.X, -(float)bb.Y, CcText)));
            _perfTitle.Size = new Vector2(w * k, th * k);
            _perfTitle.Offset = new Vector3(40 * k, 32 * k, 0);
        }
        CcSet(_perfRight, CcDraw(PerfRightW, h, raster, ds => PaintPerfList(ds, h)));
        _perfRight.Size = new Vector2(PerfRightW * k, h * k);
        _perfRight.Offset = new Vector3(PerfRightX * k, PerfTop * k, 0);
        CcSet(_perfLeft, CcDraw(PerfLeftW, h, raster, ds => PaintPerfLeft(ds, h)));
        _perfLeft.Size = new Vector2(PerfLeftW * k, h * k);
        _perfLeft.Offset = new Vector3(PerfLeftX * k, PerfTop * k, 0);
    }

    private static void PText(CanvasDrawingSession ds, string s, FontSpec f, float size, Color c, float x, float cy, float width, CanvasHorizontalAlignment al = CanvasHorizontalAlignment.Left)
    {
        using var l = TextKit.Layout(ds, s, f, size, width, al);
        var b = l.LayoutBounds;
        ds.DrawTextLayout(l, x, cy - (float)(b.Y + b.Height / 2), c);
    }

    private static float PWidth(CanvasDrawingSession ds, string s, FontSpec f, float size)
    {
        using var l = TextKit.Layout(ds, s, f, size, 2000, CanvasHorizontalAlignment.Left);
        return (float)l.LayoutBounds.Width;
    }

    private void PaintPerfList(CanvasDrawingSession ds, float h)
    {
        var ui = TextKit.Ui(400);
        var mono = new FontSpec("Cascadia Mono", 400);
        float y = 0, W = PerfRightW;
        int n = 0;
        for (int i = 0; i < _perfRows.Count; i++)
        {
            var r = _perfRows[i];
            if (r.Section)
            {
                PText(ds, r.Title, TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.42), 0, y + PerfSecH - 10, 400);
                y += PerfSecH;
                continue;
            }
            n++;
            bool f = i == _perfFocus;
            float cy = y + PerfRowH / 2;
            float a = r.Disabled ? 0.32f : 1;
            if (f)
            {
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(1, cy - 6); pb.AddLine(9, cy); pb.AddLine(1, cy + 6); pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(g, Gfx.WithAlpha(CcText, a));
            }
            else PText(ds, n.ToString("00"), mono, 13, Gfx.WithAlpha(CcText, 0.32f * a), 0, cy, 40);
            PText(ds, r.Title, ui, 17, Gfx.WithAlpha(CcText, (f ? 1f : 0.88f) * a), 36, cy, 400);
            if (r.Choices != null)
            {
                // 几选一：平铺文字，当前项白色加下划线；后面可以跟一句状态（如「重启后生效」）
                float x = W;
                if (r.Value.Length > 0)
                {
                    float vw = PWidth(ds, r.Value, ui, 14);
                    x -= vw;
                    PText(ds, r.Value, ui, 14, Gfx.WithAlpha(CcText, 0.45f), x, cy, vw + 4);
                    x -= 24;
                }
                var widths = r.Choices.Select(c => PWidth(ds, c, ui, 17)).ToArray();
                for (int j = r.Choices.Length - 1; j >= 0; j--)
                {
                    x -= widths[j];
                    bool on = j == r.Choice;
                    var col = on ? (f ? CcText : Gfx.WithAlpha(CcText, 0.92)) : Gfx.WithAlpha(CcText, 0.38);
                    PText(ds, r.Choices[j], ui, 17, Gfx.WithAlpha(col, a), x, cy, widths[j] + 4);
                    if (on) ds.FillRectangle(x, cy + 13, widths[j], 2, Gfx.WithAlpha(col, a));
                    x -= 20;
                }
            }
            else
            {
                string v = r.Value;
                float vw = PWidth(ds, v, ui, 17);
                var col = Gfx.WithAlpha(CcText, (f ? 1f : 0.6f) * a);
                if (f && r.Numeric && !r.Disabled)
                {
                    // 焦点上的数值：‹ 值 ›，← → 直接改
                    float right = W;
                    DrawChevron(ds, right - 6, cy, false, col);
                    PText(ds, v, TextKit.Ui(600), 17, col, right - 22 - vw, cy, vw + 4);
                    DrawChevron(ds, right - 34 - vw, cy, true, col);
                }
                else PText(ds, v, ui, 17, col, W - vw, cy, vw + 4);
            }
            y += PerfRowH;
        }
    }

    private static void DrawChevron(CanvasDrawingSession ds, float x, float cy, bool left, Color c)
    {
        using var stroke = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        float d = left ? 1 : -1;
        ds.DrawLine(x, cy - 6, x - 6 * d, cy, c, 1.6f, stroke);
        ds.DrawLine(x - 6 * d, cy, x, cy + 6, c, 1.6f, stroke);
    }

    private void PaintPerfLeft(CanvasDrawingSession ds, float h)
    {
        var ui = TextKit.Ui(400);
        var v = _perfView;
        var s = _perfS;
        float W = PerfLeftW;
        const float header = 30, rowH = 28, gap = 10, between = 32;
        float rest = Math.Max(160, h - (header + gap + 3 * rowH + between + header + gap + 3 * rowH));
        float powerH = rest * 0.42f, fanH = rest - powerH;
        var muted = Gfx.WithAlpha(CcText, 0.5);
        var bright = Gfx.WithAlpha(CcText, 0.88);

        void Header(float top, string title, string value, Color vc)
        {
            PText(ds, title, ui, 13, muted, 0, top + header / 2, 300);
            float vw = PWidth(ds, value, TextKit.Ui(300), 20);
            PText(ds, value, TextKit.Ui(300), 20, vc, W - vw, top + header / 2, vw + 4);
        }
        void Rows(float top, (string k, string v)[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                float cy = top + i * rowH + rowH / 2;
                PText(ds, rows[i].k, ui, 14, muted, 0, cy, 200);
                float vw = PWidth(ds, rows[i].v, ui, 14);
                PText(ds, rows[i].v, ui, 14, bright, W - vw, cy, vw + 4);
            }
        }

        // ---- 功耗 ----
        double? pkg = Num(v?["actual"]?["package"]);
        Header(0, "功耗 · 最近 60 秒", pkg is double p ? $"{p:0.0} W" : "—", PerfOrange);
        float cTop = header, cH = powerH;
        var hist = _perfPower.ToArray();
        // 上限线：智能模式画当前电源下的上限（插电 / 电池），不是治理器此刻压到的值；其余模式就是设定的功耗
        bool smartMode = Str(s?["mode"]) == "smart";
        int capW = smartMode ? Int(s?["smart"]?[PerfCharger ? "acMax" : "dcMax"], 25) : Int(v?["target"]?["pl1"], 0);
        int floorW = smartMode ? Int(s?["smart"]?["floor"], 8) : 0;
        double yMax = Math.Max(Math.Max(capW, hist.Length > 0 ? hist.Max() : 0), 10) * 1.12;
        float Y(double w) => cTop + 10 + (float)((1 - Math.Clamp(w / yMax, 0, 1)) * (cH - 20));
        using (var dash = new CanvasStrokeStyle { CustomDashStyle = new[] { 2f, 3f } })
        {
            if (capW > 0 && Bool(s?["enabled"]))
            {
                ds.DrawLine(0, Y(capW), W, Y(capW), Gfx.WithAlpha(CcText, 0.45), 1, dash);
                PText(ds, $"上限 {capW} W", ui, 11, Gfx.WithAlpha(CcText, 0.55), 0, Y(capW) - 9, 200);
            }
            if (floorW > 0 && Bool(s?["enabled"]))
            {
                ds.DrawLine(0, Y(floorW), W, Y(floorW), Gfx.WithAlpha(CcText, 0.18), 1, dash);
                PText(ds, $"下限 {floorW} W", ui, 11, Gfx.WithAlpha(CcText, 0.38), 0, Y(floorW) + 9, 200);
            }
        }
        if (hist.Length >= 2)
        {
            float dx = W / 59f, x0 = W - (hist.Length - 1) * dx;
            using var line = new CanvasPathBuilder(ds);
            using var area = new CanvasPathBuilder(ds);
            line.BeginFigure(x0, Y(hist[0]));
            area.BeginFigure(x0, cTop + cH);
            area.AddLine(x0, Y(hist[0]));
            for (int i = 1; i < hist.Length; i++) { line.AddLine(x0 + i * dx, Y(hist[i])); area.AddLine(x0 + i * dx, Y(hist[i])); }
            line.EndFigure(CanvasFigureLoop.Open);
            area.AddLine(W, cTop + cH);
            area.EndFigure(CanvasFigureLoop.Closed);
            using var ag = CanvasGeometry.CreatePath(area);
            using var lg = CanvasGeometry.CreatePath(line);
            using var fill = new CanvasLinearGradientBrush(ds, Gfx.WithAlpha(PerfOrange, 0.22), Gfx.WithAlpha(PerfOrange, 0)) { StartPoint = new Vector2(0, cTop), EndPoint = new Vector2(0, cTop + cH) };
            ds.FillGeometry(ag, fill);
            using var round = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
            ds.DrawGeometry(lg, PerfOrange, 2.2f, round);
            ds.FillCircle(W - 2, Y(hist[^1]), 4, PerfOrange);
        }
        else PText(ds, _perfAgentDown ? "连不上工具箱后台" : "正在读取…", ui, 13, muted, 0, cTop + cH / 2, 300);

        string mode = Str(s?["mode"]) ?? "smart";
        bool charger = PerfCharger;
        string preset = s == null ? "—" : PerfPresetText();
        int boost = Int(s?["boost"], 0);
        int freq = mode == "manual" ? Int(s?["manual"]?["freq"]) : Int(s?[charger ? "ac" : "dc"]?["freq"]);
        Rows(cTop + cH + gap, new[]
        {
            ("功耗预设", preset), ("短时加成", s == null ? "—" : boost == 0 ? "关" : $"+{boost} W"), ("CPU 频率", s == null ? "—" : Ghz(freq)),
        });

        // ---- 风扇 ----
        float fTop = header + powerH + gap + 3 * rowH + between;
        string fmode = Str(s?["fan"]?["mode"]) ?? "firmware";
        int fi = Math.Max(0, Array.IndexOf(FanModes, fmode));
        double? rpm = Num(v?["fan"]?["rpm1"]);
        Header(fTop, fmode == "firmware" ? "风扇 · 固件控制" : $"风扇 · {FanModeNames[fi]}档", rpm is double rp ? $"{rp:0} RPM" : "—", CcText);
        float gTop = fTop + header, gH = fanH;
        int[] axis = (v?["fan"]?["axis"] as JsonArray)?.Select(n => Int(n)).ToArray() is { Length: 6 } ax ? ax : new[] { 50, 60, 70, 75, 80, 90 };
        string curveMode = fmode == "firmware" ? "balanced" : fmode;
        int[] curve = PerfCurve(curveMode);
        int cap = Int(v?["fan"]?["caps"]?[curveMode], 76);
        double rMin = 2400, rMax = FanRpm(Math.Max(cap, curve.Max())) + 500;
        float labelH = 16;
        float FX(double t) => (float)((Math.Clamp(t, axis[0], axis[^1]) - axis[0]) / (axis[^1] - axis[0]) * (W - 8) + 4);
        float FY(double r) => gTop + 6 + (float)((1 - Math.Clamp((r - rMin) / (rMax - rMin), 0, 1)) * (gH - labelH - 12));
        using (var dash = new CanvasStrokeStyle { CustomDashStyle = new[] { 2f, 3f } })
        {
            float capY = FY(FanRpm(cap));
            ds.DrawLine(0, capY, W, capY, Gfx.WithAlpha(CcText, 0.45), 1, dash);
            PText(ds, $"上限 {FanRpm(cap):0} RPM", ui, 11, Gfx.WithAlpha(CcText, 0.55), 0, capY - 9, 200);
        }
        float alphaCurve = fmode == "firmware" ? 0.35f : 1f;
        using (var pb = new CanvasPathBuilder(ds))
        {
            pb.BeginFigure(FX(axis[0]), FY(FanRpm(curve[0])));
            for (int i = 1; i < 6; i++) pb.AddLine(FX(axis[i]), FY(FanRpm(curve[i])));
            pb.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(pb);
            using var round = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
            ds.DrawGeometry(g, Gfx.WithAlpha(CcText, alphaCurve), 2.2f, round);
        }
        for (int i = 0; i < 6; i++)
        {
            float px = FX(axis[i]), py = FY(FanRpm(curve[i]));
            bool sel = _perfCurveEdit && i == _perfPoint;
            if (sel)
            {
                ds.FillCircle(px, py, 7, CcText);
                string tag = $"{axis[i]} °C · {curve[i]} %";
                float tw = PWidth(ds, tag, TextKit.Ui(600), 13);
                float tx = Math.Clamp(px - tw / 2, 0, W - tw);
                PText(ds, tag, TextKit.Ui(600), 13, CcText, tx, py - 18, tw + 4);
            }
            else
            {
                ds.FillCircle(px, py, 4, Theme.Bg);
                ds.DrawCircle(px, py, 4, Gfx.WithAlpha(CcText, alphaCurve), 1.6f);
            }
        }
        double? temp = Num(v?["live"]?["cpuTemp"]);
        if (temp is double tc && rpm is double rr)
        {
            float cx = FX(tc);
            using var dash = new CanvasStrokeStyle { CustomDashStyle = new[] { 1f, 2f } };
            ds.DrawLine(cx, gTop, cx, gTop + gH - labelH, Gfx.WithAlpha(PerfOrange, 0.8), 1.5f, dash);
            ds.FillCircle(cx, FY(rr), 5, PerfOrange);
        }
        var mono = new FontSpec("Cascadia Mono", 400);
        foreach (int t in new[] { axis[0], 60, 70, 80, axis[^1] })
        {
            string lab = t == axis[^1] ? $"{t} °C" : t.ToString();
            float lw = PWidth(ds, lab, mono, 11);
            float lx = Math.Clamp(FX(t) - lw / 2, 0, W - lw);
            PText(ds, lab, mono, 11, Gfx.WithAlpha(CcText, 0.4), lx, gTop + gH - labelH / 2, lw + 4);
        }
        double? duty = Num(v?["fan"]?["duty1"]);
        Rows(gTop + gH + gap, new[]
        {
            ("CPU 温度", temp is double t2 ? $"{t2:0} °C" : "—"),
            ("占空比", duty is double du ? $"{du:0} %" : "—"),
            ("转速上限", fmode == "firmware" ? "固件决定" : $"{cap} % ≈ {FanRpm(cap):0} RPM"),
        });
    }
}
