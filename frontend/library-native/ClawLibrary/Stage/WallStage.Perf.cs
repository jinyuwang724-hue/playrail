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
        /// <summary>这个模式下用不上的行：照样占位（高度不变），焦点跳过。</summary>
        public bool Skip;
        public Action<int>? Step;
        public Action? Press;
        public Action<int>? Select, SetNumber;
        public int Number, Minimum, Maximum, Increment = 1;
    }

    // 设计 px（× _ccK），1280×800 舞台：左栏 348、右栏 812、中间 40
    private const float PerfLeftX = 40, PerfLeftW = 348, PerfRightX = 428, PerfRightW = 812, PerfTop = 96;
    private const float PerfRowH = 42, PerfSecH = 38;
    /// <summary>左右两栏的显示高度固定（舞台底下留 40）；右栏内容比它高时在里面滚，焦点行带上下各 PerfScrollPad 的余量。</summary>
    private const float PerfViewH = 800 - PerfTop - 40, PerfScrollPad = 60;
    private static readonly string[] FanModes = { "quiet", "balanced", "performance" };
    private static readonly string[] FanModeNames = { "静音", "均衡", "性能" };
    private static readonly string[] PowerModes = { "autotdp", "auto", "manual" };
    private static readonly string[] PowerModeNames = { "Auto", "按电源", "手动" };
    /// <summary>强调色跟着背景的主题色走（当前游戏封面的代表色），背景还没取到色时用 folia 的橙。</summary>
    private Color PerfAccent => _ccBack?.Accent ?? Gfx.Rgba(0xfd, 0x8a, 0x5a);

    private AgentClient? _agent;
    private ContainerVisual? _perf;
    private ContainerVisual _perfRightView = null!;
    private float _perfScroll, _perfScrollY = float.NaN;
    private CpuFreq? _cpuFreq;
    private int _perfTicks;
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
        SettingsResetPointer();
        _perfCurveEdit = false;
        _perfFocus = -1;
        int v = ++_perfVersion;
        BuildPerfRows();
        _perfFocus = _perfRows.FindIndex(r => !r.Section && !r.Skip);
        _perfScroll = 0;
        _perfScrollY = float.NaN;
        (_cpuFreq ??= new CpuFreq()).Sample();
        _perfTicks = 1;
        PaintPerf();
        // 控制中心的滑条、标题、说明、左下图标淡出；右上状态留着
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        _perf!.IsVisible = true;
        _perf.Opacity = 0;
        _perf.StartAnimation("Opacity", ToScalar(1, 240, EaseOutCss, 80));
        _perfTimer ??= MakePerfTimer();
        _perfTimer.Start();
        _ = PerfRefresh(true);
        Log.Info($"perf page open (cover {_ccBack?.CoverColorsText()} → accent #{PerfAccent.R:x2}{PerfAccent.G:x2}{PerfAccent.B:x2})");
    }

    private void ClosePerf(bool instant = false)
    {
        if (!_perfOpen || _perf == null) return;
        _perfOpen = false;
        SettingsResetPointer();
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
        _perfRightView = G.Container(relative: false);
        _perfRightView.Clip = G.C.CreateInsetClip();
        _perfRightView.Children.InsertAtTop(_perfRight);
        foreach (var s in new Visual[] { _perfTitle, _perfLeft, _perfRightView }) _perf.Children.InsertAtTop(s);
        _cc!.Children.InsertAtTop(_perf);
    }

    private DispatcherQueueTimer MakePerfTimer()
    {
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        t.IsRepeating = true;
        t.Tick += (_, _) =>
        {
            if (!_perfOpen) return;
            // 实时 CPU 频率两秒采一次（左栏「CPU 频率」）；功耗、风扇每秒从 agent 取，取回来顺手重画
            if (_perfTicks++ % 2 == 0) (_cpuFreq ??= new CpuFreq()).Sample();
            _ = PerfRefresh(true);
        };
        return t;
    }

    // ---------------- 和 agent 打交道 ----------------

    private async Task PerfRefresh(bool sample)
    {
        if (_perfBusy || (_perfOpen && _settingsGesture.Active)) return;
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
        if (!_settingsGesture.Active && _perfEdits == _perfSavedEdits && view["settings"] is JsonObject s) _perfS = (JsonObject)s.DeepClone();
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
        return (Str(s["mode"]) ?? "autotdp") switch
        {
            "autotdp" => $"Auto · {Int(s["autotdp"]?["target"], 60)} 帧",
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
        string mode = Str(s["mode"]) ?? "autotdp";
        PerfRow Choice(string title, string[] names, int idx, Action<int> set, bool disabled = false) => new()
        {
            Title = title, Choices = names, Choice = idx, Disabled = disabled,
            Step = d => { if (!disabled) set(Math.Clamp(idx + d, 0, names.Length - 1)); },
            Press = () => { if (!disabled) set((idx + 1) % names.Length); },
            Select = i => { if (!disabled) set(i); },
        };
        PerfRow Number(string title, int value, int min, int max, Func<int, string> fmt, Action<int> set, int step = 1, bool disabled = false) => new()
        {
            Title = title, Value = fmt(value), Numeric = true, Disabled = disabled,
            Number = value, Minimum = min, Maximum = max, Increment = step,
            SetNumber = v => { if (!disabled) set(v); },
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
        PerfRow Info(string title, string value) => new() { Title = title, Value = value, Skip = true };
        bool autoMode = mode == "autotdp";
        if (autoMode)
        {
            if (s["autotdp"] is not JsonObject auto) s["autotdp"] = auto = new JsonObject { ["target"] = 60, ["floor"] = 12, ["acMax"] = 35, ["dcMax"] = 25 };
            _perfRows.Add(Number("目标帧率", Int(auto["target"], 60), 20, 240, n => $"{n} 帧", n => { auto["target"] = n; PerfChanged(); }, disabled: !on));
            _perfRows.Add(Number("功耗下限", Int(auto["floor"], 12), 8, Math.Min(Int(auto["acMax"], 35), Int(auto["dcMax"], 25)), n => $"{n} W", n => { auto["floor"] = n; PerfChanged(); }, disabled: !on));
            _perfRows.Add(Number("插电上限", Int(auto["acMax"], 35), Int(auto["floor"], 12), 35, n => $"{n} W", n => { auto["acMax"] = n; PerfChanged(); }, disabled: !on));
            _perfRows.Add(Number("电池上限", Int(auto["dcMax"], 25), Int(auto["floor"], 12), 35, n => $"{n} W", n => { auto["dcMax"] = n; PerfChanged(); }, disabled: !on));
            string status = Str(_perfView?["error"]) is { Length: > 0 } error ? error : !on ? "尚未开启" : Str(_perfView?["autotdp"]?["reason"]) ?? "等待游戏帧率";
            _perfRows.Add(Info("调节状态", status));
            _perfRows.Add(Info("真实帧率", Num(_perfView?["autotdp"]?["fps"]) is double fps ? $"{fps:0.0} 帧" : "等待有效样本"));
            _perfRows.Add(Number("短时加成", Int(s["boost"], 1), 0, 15, w => w == 0 ? "关" : $"+{w} W", w => { s["boost"] = w; PerfChanged(); }, disabled: !on));
        }
        else
        {
            _perfRows.Add(mode == "auto"
                ? Number("插电功耗", Int(s["ac"]!["tdp"], 35), 8, 35, w => $"{w} W", w => { s["ac"]!["tdp"] = w; PerfChanged(); }, disabled: !on)
                : Number("固定功耗", Int(s["manual"]!["tdp"], 20), 8, 35, w => $"{w} W", w => { s["manual"]!["tdp"] = w; PerfChanged(); }, disabled: !on));
            if (mode == "auto") _perfRows.Add(Number("电池功耗", Int(s["dc"]!["tdp"], 25), 8, 35, w => $"{w} W", w => { s["dc"]!["tdp"] = w; PerfChanged(); }, disabled: !on));
            _perfRows.Add(Number("短时加成", Int(s["boost"], 1), 0, 15, w => w == 0 ? "关" : $"+{w} W", w => { s["boost"] = w; PerfChanged(); }, disabled: !on));
        }

        _perfRows.Add(new PerfRow { Section = true, Title = "CPU" });
        int[] freqs = (_perfView?["limits"]?["freq"] as JsonArray)?.Select(n => Int(n)).ToArray() is { Length: > 0 } fq ? fq : new[] { 0, 1500, 2000, 2500, 2750, 3000, 3500, 4000 };
        bool manual = mode == "manual";
        int freq = manual ? Int(s["manual"]!["freq"]) : Int(s[PerfCharger ? "ac" : "dc"]!["freq"]);
        _perfRows.Add(autoMode ? Info("CPU 频率", "不限频 · 按负载运行") : List("固定 CPU 频率", freqs, freq, Ghz, f =>
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
        string fmode = Str(fan["mode"]) ?? "balanced";
        _perfRows.Add(Choice("风扇模式", FanModeNames, Math.Max(0, Array.IndexOf(FanModes, fmode)), i => { fan["control"] = "custom"; fan["mode"] = FanModes[i]; _perfCurveEdit = false; PerfChanged(); }));
        _perfRows.Add(Info("调速方式", Str(_perfView?["fan"]?["error"]) is { Length: > 0 } fanError ? fanError : Str(fan["control"]) == "auto" ? "原厂自动控制" : Bool(_perfView?["fan"]?["thermalBoost"]) ? "EC 高温保护 · 冷却后恢复曲线" : Str(_perfView?["fan"]?["execution"]) == "service" ? "开机风扇服务 · 退出界面仍生效" : "软件自动调速"));
        _perfRows.Add(new PerfRow
        {
            Title = "编辑这档曲线", Value = _perfCurveEdit ? "编辑中 · ← → 选点  ↑ ↓ 改" : "6 个温度点 ›",
            Press = () => { _perfCurveEdit = !_perfCurveEdit; _perfPoint = Math.Clamp(_perfPoint, 0, 5); BuildPerfRows(); PaintPerf(); },
        });
        if (_perfCurveEdit)
        {
            int[] axis = (_perfView?["fan"]?["axis"] as JsonArray)?.Select(n => Int(n)).ToArray() is { Length: 6 } ax ? ax : new[] { 50, 60, 70, 75, 80, 90 };
            int[] curve = PerfCurve(fmode);
            int cap = Int(_perfView?["fan"]?["caps"]?[fmode], 100);
            for (int i = 0; i < 6; i++)
            {
                int point = i;
                _perfRows.Add(Number($"{axis[i]} °C 风量", curve[i], 1, cap, n => $"{n} %", n =>
                {
                    var next = PerfCurve(fmode); next[point] = n; _perfPoint = point;
                    if (fan["curves"] is not JsonObject curves) fan["curves"] = curves = new JsonObject();
                    curves[fmode] = new JsonArray(next.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                    PerfChanged();
                }));
            }
            _perfRows.Add(new PerfRow { Title = "完成曲线编辑", Value = "›", Press = PerfFinishCurveEdit });
        }
        _perfRows.Add(Choice("全速运转", new[] { "关", "开" }, Bool(fan["full"]) ? 1 : 0, i => { fan["control"] = "custom"; fan["full"] = i == 1; PerfChanged(); }));
        _perfRows.Add(new PerfRow { Title = "恢复原厂自动控制", Value = "›", Press = () => { fan["control"] = "auto"; fan["full"] = false; PerfChanged(); } });
        _perfRows.Add(new PerfRow
        {
            Title = "恢复这档默认曲线", Value = "›",
            Press = () =>
            {
                if (_perfView?["fan"]?["presets"]?[fmode] is not JsonArray preset) return;
                if (fan["curves"] is not JsonObject curves) fan["curves"] = curves = new JsonObject();
                curves[fmode] = preset.DeepClone();
                PerfChanged();
                Toast($"{FanModeNames[Array.IndexOf(FanModes, fmode)]}档曲线已恢复默认");
            },
        });
        if (_perfFocus >= _perfRows.Count || (_perfFocus >= 0 && (_perfRows[_perfFocus].Skip || _perfRows[_perfFocus].Section)))
        {
            // 换了模式后焦点所在的行用不上了：往上找最近一个能选的（模式那一行总在上面）
            int f = Math.Min(_perfFocus, _perfRows.Count - 1);
            while (f > 0 && (_perfRows[f].Section || _perfRows[f].Skip)) f--;
            _perfFocus = f;
        }
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
        SettingsResumeKeyboard();
        if (_perfCurveEdit)
        {
            string mode = Str(_perfS?["fan"]?["mode"]) ?? "balanced";
            if (dir is Direction.Left or Direction.Right) { _perfPoint = Math.Clamp(_perfPoint + (dir == Direction.Left ? -1 : 1), 0, 5); PaintPerf(); return; }
            var c = PerfCurve(mode);
            int cap = Int(_perfView?["fan"]?["caps"]?[mode], 100);
            int nv = Math.Clamp(c[_perfPoint] + (dir == Direction.Up ? 1 : -1), 1, cap);
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
                if (!_perfRows[i].Section && !_perfRows[i].Skip) { _perfFocus = i; PaintPerf(); return; }
            return;
        }
        if (_perfFocus >= 0 && _perfFocus < _perfRows.Count) _perfRows[_perfFocus].Step?.Invoke(dir == Direction.Left ? -1 : 1);
    }

    private void PerfActivate()
    {
        SettingsResumeKeyboard();
        if (_perfCurveEdit) { PerfFinishCurveEdit(); return; }
        if (_perfFocus >= 0 && _perfFocus < _perfRows.Count) _perfRows[_perfFocus].Press?.Invoke();
    }

    private void PerfBack()
    {
        SettingsResetPointer();
        if (_perfCurveEdit) { PerfFinishCurveEdit(); return; }
        ClosePerf();
    }

    private void PerfFinishCurveEdit()
    {
        _perfCurveEdit = false;
        _perfFocus = _perfRows.FindIndex(r => r.Title == "编辑这档曲线");
        BuildPerfRows(); PaintPerf();
    }

    // ---------------- 绘制 ----------------

    private float PerfListHeight() => RowsHeight(_perfRows);
    private static float RowsHeight(List<PerfRow> rows) => rows.Sum(r => r.Section ? PerfSecH : PerfRowH);

    private void PaintPerf()
    {
        if (_perf == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        float h = PerfListHeight(), vh = PerfViewH;
        // 标题：和控制中心的「控制中心」同一个位置、同一个字
        using (var tl = TextKit.Layout(G.Device, "性能", TextKit.Ui(700), 24, 400, CanvasHorizontalAlignment.Left))
        {
            var bb = tl.LayoutBounds;
            float w = (float)bb.Width + 4, th = (float)bb.Height + 4;
            CcSet(_perfTitle, CcDraw(w, th, raster, ds => ds.DrawTextLayout(tl, -(float)bb.X, -(float)bb.Y, CcText)));
            _perfTitle.Size = new Vector2(w * k, th * k);
            _perfTitle.Offset = new Vector3(40 * k, 32 * k, 0);
        }
        // 右栏：整张列表画在一张面上，放进固定高度的裁剪框里，按焦点滚动
        _settingsTargets.Clear();
        CcSet(_perfRight, CcDraw(PerfRightW, h, raster, ds => PaintRowList(ds, _perfRows, _perfFocus, _settingsTargets)));
        _perfRight.Size = new Vector2(PerfRightW * k, h * k);
        _perfRightView.Size = new Vector2(PerfRightW * k, vh * k);
        _perfRightView.Offset = new Vector3(PerfRightX * k, PerfTop * k, 0);
        RowScrollTo(_perfRight, _perfRows, _settingsManualScroll ? -1 : _perfFocus, ref _perfScroll, ref _perfScrollY, h, vh, false);
        CcSet(_perfLeft, CcDraw(PerfLeftW, vh, raster, ds => PaintPerfLeft(ds, vh)));
        _perfLeft.Size = new Vector2(PerfLeftW * k, vh * k);
        _perfLeft.Offset = new Vector3(PerfLeftX * k, PerfTop * k, 0);
    }

    /// <summary>
    /// 焦点行（连上下各 PerfScrollPad）露在框里就不动，出框才滚到刚好露出来；分组里的第一行连分组标题一起露。
    /// 用弹簧滚（和控制中心滑条同一手感），连按时从当前位置接着滚，不跳。
    /// </summary>
    private void RowScrollTo(SpriteVisual list, List<PerfRow> rows, int focus, ref float scroll, ref float scrollY, float listH, float viewH, bool open)
    {
        float max = Math.Max(0, listH - viewH), target = Math.Clamp(scroll, 0, max);
        if (focus >= 0 && focus < rows.Count)
        {
            float top = 0;
            for (int i = 0; i < focus; i++) top += rows[i].Section ? PerfSecH : PerfRowH;
            float bottom = top + PerfRowH;
            if (focus > 0 && rows[focus - 1].Section) top -= PerfSecH;
            if (top - PerfScrollPad < target) target = top - PerfScrollPad;
            if (bottom + PerfScrollPad > target + viewH) target = bottom + PerfScrollPad - viewH;
            target = Math.Clamp(target, 0, max);
        }
        float y = -target * _ccK;
        if (target == scroll && y == scrollY) return;   // 每秒重画一次：位置没变就别打断正在走的弹簧
        bool moving = target != scroll;
        scroll = target;
        scrollY = y;
        if (!moving || Reduced || !open) { list.StopAnimation("Offset.Y"); list.Offset = new Vector3(0, y, 0); return; }
        list.StartAnimation("Offset.Y", G.SpringScalar(y, Spring.GridCenter));
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

    /// <summary>控制中心子页右栏的设置列表（性能、手柄共用）：分组标题、行号、焦点 ▸、几选一下划线、焦点数值 ‹ ›。</summary>
    private void PaintRowList(CanvasDrawingSession ds, List<PerfRow> rows, int focus, List<SettingsPointerTarget>? targets = null)
    {
        var ui = TextKit.Ui(400);
        var mono = new FontSpec("Cascadia Mono", 400);
        float y = 0, W = PerfRightW;
        int n = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Section)
            {
                PText(ds, r.Title, TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.42), 0, y + PerfSecH - 10, 400);
                y += PerfSecH;
                continue;
            }
            n++;
            targets?.Add(new(i, SettingsTargetKind.Row, 0, y, W, PerfRowH));
            bool f = i == focus;
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
            if (targets != null && r.SetNumber != null && !r.Disabled)
            {
                const float sliderWidth = 200;
                float left = W - 300;
                float fraction = Math.Clamp((r.Number - r.Minimum) / (float)Math.Max(1, r.Maximum - r.Minimum), 0, 1);
                ds.FillRoundedRectangle(left, cy - 2, sliderWidth, 4, 2, 2, Gfx.WithAlpha(CcText, .18f));
                if (fraction > 0) ds.FillRoundedRectangle(left, cy - 2, sliderWidth * fraction, 4, 2, 2, PerfAccent);
                ds.FillCircle(left + sliderWidth * fraction, cy, f ? 6 : 5, CcText);
                float vw = PWidth(ds, r.Value, ui, 17);
                PText(ds, r.Value, ui, 17, CcText, W - vw, cy, vw + 4);
                targets.Add(new(i, SettingsTargetKind.Slider, left, y, sliderWidth, PerfRowH));
            }
            else if (r.Choices != null)
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
                    float width = targets != null ? Math.Max(44, widths[j] + 20) : widths[j];
                    x -= width;
                    float textX = x + (width - widths[j]) / 2;
                    bool on = j == r.Choice;
                    var col = on ? (f ? CcText : Gfx.WithAlpha(CcText, 0.92)) : Gfx.WithAlpha(CcText, 0.38);
                    PText(ds, r.Choices[j], ui, 17, Gfx.WithAlpha(col, a), textX, cy, widths[j] + 4);
                    if (on) ds.FillRectangle(textX, cy + 13, widths[j], 2, Gfx.WithAlpha(col, a));
                    targets?.Add(new(i, SettingsTargetKind.Choice, x, y, width, PerfRowH, j));
                    if (targets == null) x -= 20;
                }
            }
            else
            {
                string v = r.Value;
                float vw = PWidth(ds, v, ui, 17);
                var col = Gfx.WithAlpha(CcText, (f ? 1f : 0.6f) * a);
                if ((f || targets != null) && r.Numeric && !r.Disabled)
                {
                    // 焦点上的数值：‹ 值 ›，← → 直接改
                    float right = W;
                    DrawChevron(ds, right - 6, cy, false, col);
                    PText(ds, v, TextKit.Ui(600), 17, col, right - 22 - vw, cy, vw + 4);
                    DrawChevron(ds, right - 34 - vw, cy, true, col);
                    targets?.Add(new(i, SettingsTargetKind.Decrease, right - 56 - vw, y, 44, PerfRowH));
                    targets?.Add(new(i, SettingsTargetKind.Increase, right - 44, y, 44, PerfRowH));
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
        var accent = PerfAccent;
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
        Header(0, "功耗 · 最近 60 秒", pkg is double p ? $"{p:0.0} W" : "—", accent);
        float cTop = header, cH = powerH;
        var hist = _perfPower.ToArray();
        bool autoMode = Str(s?["mode"]) == "autotdp";
        int capW = Int(v?["target"]?["pl1"], 0);
        int floorW = autoMode ? Int(s?["autotdp"]?["floor"], 12) : 0;
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
            using var fill = new CanvasLinearGradientBrush(ds, Gfx.WithAlpha(accent, 0.22), Gfx.WithAlpha(accent, 0)) { StartPoint = new Vector2(0, cTop), EndPoint = new Vector2(0, cTop + cH) };
            ds.FillGeometry(ag, fill);
            using var round = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
            ds.DrawGeometry(lg, accent, 2.2f, round);
            ds.FillCircle(W - 2, Y(hist[^1]), 4, accent);
        }
        else PText(ds, _perfAgentDown ? "连不上工具箱后台" : "正在读取…", ui, 13, muted, 0, cTop + cH / 2, 300);

        string preset = s == null ? "—" : PerfPresetText();
        int boost = Int(s?["boost"], 0);
        Rows(cTop + cH + gap, new[]
        {
            ("功耗预设", preset), (autoMode ? "调节方式" : "短时加成", autoMode ? "目标帧率反馈" : s == null ? "—" : boost == 0 ? "关" : $"+{boost} W"), ("CPU 频率", _cpuFreq?.Mhz is double mhz ? $"{mhz / 1000:0.00} GHz" : "—"),
        });

        // ---- 风扇 ----
        float fTop = header + powerH + gap + 3 * rowH + between;
        string fmode = Str(s?["fan"]?["mode"]) ?? "balanced";
        int fi = Math.Max(0, Array.IndexOf(FanModes, fmode));
        double? rpm = Num(v?["fan"]?["rpm1"]);
        Header(fTop, $"风扇 · {FanModeNames[fi]}档", rpm is double rp ? $"{rp:0} RPM" : "—", CcText);
        float gTop = fTop + header, gH = fanH;
        int[] axis = (v?["fan"]?["axis"] as JsonArray)?.Select(n => Int(n)).ToArray() is { Length: 6 } ax ? ax : new[] { 50, 60, 70, 75, 80, 90 };
        string curveMode = fmode;
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
        float alphaCurve = 1f;
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
            ds.DrawLine(cx, gTop, cx, gTop + gH - labelH, Gfx.WithAlpha(accent, 0.8), 1.5f, dash);
            ds.FillCircle(cx, FY(rr), 5, accent);
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
            ("曲线预期", $"{cap} % ≈ {FanRpm(cap):0} RPM"),
        });
    }
}
