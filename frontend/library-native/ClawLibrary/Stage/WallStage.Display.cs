// 控制中心 → 显示与声音：和性能页同一套 3:7 两栏（用户说先这样做出来看效果，可能不要两栏）。
// 左栏只看：屏幕（按分辨率比例画的一块屏，亮度直接反映在上面）和声音（音量条、输出设备、麦克风）；右栏只改。
// 全部是全局设置，不按游戏（用户 2026-10-16：Magpie、小黄鸭这些画面缩放归游戏配置页，不在这页）。
// 读：工具箱后台 get_display_state（docs/display-sound-page.md 写了字段）；老后台没有它时退回 widget_state 里的 system（只有亮度、音量、静音、刷新率），
// 其余行变暗、写「后台还没接上」。写：每项一个 set_*，本地先改（界面立刻跟着变），亮度 / 音量这种连按的 150 ms 内合成一次。
// 手柄：↑ ↓ 选项，← → 改值，A 开关，B 回控制中心。
// 鼠标 / 触控：点选选项，点按或横向拖动数值滑条；纵向拖动和滚轮只滚动列表。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const string DsLater = "后台还没接上";

    private ContainerVisual? _ds;
    private ContainerVisual _dsRightView = null!;
    private SpriteVisual _dsTitle = null!, _dsLeft = null!, _dsRight = null!;
    private bool _dsOpen, _dsBusy, _dsAgentDown;
    private int _dsFocus, _dsVersion;
    private SettingsWriteQueue? _dsWrites;
    private float _dsScroll, _dsScrollY = float.NaN;
    /// <summary>后台的显示与声音状态（get_display_state 的结果，或老后台 widget_state 里的 system）。本地改动直接写在这里。</summary>
    private JsonObject? _dsView;
    private readonly List<PerfRow> _dsRows = new();
    private DispatcherQueueTimer? _dsTimer;

    private static readonly string[] DsLowLatency = { "off", "on", "boost" };
    private static readonly string[] DsLowLatencyNames = { "关", "开", "加强" };
    private static readonly string[] DsScaling = { "aspect", "full", "center", "integer", "retro" };
    private static readonly string[] DsScalingNames = { "保持比例", "全屏", "居中", "整数", "复古" };

    // ---------------- 开 / 关 ----------------

    private void OpenDisplay()
    {
        if (_cc == null) return;
        EnsureDsVisuals();
        _dsOpen = true;
        SettingsResetPointer();
        ++_dsVersion;
        BuildDsRows();
        _dsFocus = _dsRows.FindIndex(r => !r.Section && !r.Skip);
        _dsScroll = 0;
        _dsScrollY = float.NaN;
        PaintDs();
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        _ds!.IsVisible = true;
        _ds.Opacity = 0;
        _ds.StartAnimation("Opacity", ToScalar(1, 240, EaseOutCss, 80));
        _dsTimer ??= MakeDsTimer();
        _dsTimer.Start();
        _ = DsRefresh();
        Log.Info("display page open");
    }

    private void CloseDisplay(bool instant = false)
    {
        if (!_dsOpen || _ds == null) return;
        if (!instant) Cue(Sfx.ModalHide);
        _dsOpen = false;
        SettingsResetPointer();
        _dsTimer?.Stop();
        int v = ++_dsVersion;
        if (instant || Reduced)
        {
            _ds.StopAnimation("Opacity"); _ds.IsVisible = false; _ds.Opacity = 1;
            foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); vis.Opacity = 1; }
            return;
        }
        _ds.StartAnimation("Opacity", ToScalar(0, 140, EaseOutCss));
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(1, 220, EaseOutCss, 60));
        After(160, () => { if (v == _dsVersion && !_dsOpen && _ds != null) { _ds.IsVisible = false; _ds.StopAnimation("Opacity"); _ds.Opacity = 1; } });
        Log.Info("display page close");
    }

    private void EnsureDsVisuals()
    {
        if (_ds != null) return;
        _ds = G.Container();
        _ds.IsVisible = false;
        _dsTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _dsLeft = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _dsRight = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _dsRightView = G.Container(relative: false);
        _dsRightView.Clip = G.C.CreateInsetClip();
        _dsRightView.Children.InsertAtTop(_dsRight);
        foreach (var s in new Visual[] { _dsTitle, _dsLeft, _dsRightView }) _ds.Children.InsertAtTop(s);
        _cc!.Children.InsertAtTop(_ds);
    }

    /// <summary>两秒读一次（别处改了亮度 / 音量、插了耳机要跟上）；有没写完的改动时不读，免得把本地值冲掉。</summary>
    private DispatcherQueueTimer MakeDsTimer()
    {
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.IsRepeating = true;
        t.Tick += (_, _) => { if (_dsOpen && !_settingsGesture.Active && _dsWrites?.IsBusy != true) _ = DsRefresh(); };
        return t;
    }

    // ---------------- 和 agent 打交道 ----------------

    private async Task DsRefresh()
    {
        if (_dsBusy || _settingsGesture.Active || _dsWrites?.IsBusy == true) return;
        _dsBusy = true;
        long revision = _dsWrites?.Revision ?? 0;
        try
        {
            _agent ??= new AgentClient();
            JsonNode? view;
            try
            {
                view = await _agent.Call("get_display_state");
            }
            catch (Exception ex) when (ex.Message.Contains("不支持的后台调用"))
            {
                // 老后台：只有小组件用的那几项
                view = (await _agent.Call("widget_state"))?["system"];
            }
            _dsAgentDown = false;
            if (!_settingsGesture.Active && _dsWrites?.IsBusy != true && revision == (_dsWrites?.Revision ?? 0) && view is JsonObject o)
                _dsView = (JsonObject)o.DeepClone();
        }
        catch (Exception ex)
        {
            if (!_dsAgentDown) Log.Warn($"display refresh: {ex.Message}");
            _dsAgentDown = true;
        }
        finally { _dsBusy = false; }
        if (!_dsOpen) return;
        if (_dsWrites?.IsBusy != true && revision != (_dsWrites?.Revision ?? 0)) { _ = DsRefresh(); return; }
        BuildDsRows();
        PaintDs();
    }

    /// <summary>
    /// 同一项的连续修改只写最后一次，各项之间不会互相取消。全部写完后读回真实值，避免旧回复覆盖待写的本地值。
    /// </summary>
    private void DsSet(string method, int delayMs, params JsonNode?[] args)
    {
        _dsWrites ??= new SettingsWriteQueue(
            ex => Toast(ex.Message.Contains("不支持的后台调用") ? $"这一项{DsLater}" : $"没有改成：{ex.Message}"),
            () => { _ = DsRefresh(); });
        string key = method == "set_gpu_option" ? $"{method}:{Str(args[0])}" : method;
        _ = _dsWrites.Enqueue(key, delayMs, async () =>
        {
            _agent ??= new AgentClient();
            await _agent.Call(method, args);
        });
        BuildDsRows();
        PaintDs();
    }

    // ---------------- 右栏的行 ----------------

    private void BuildDsRows()
    {
        _dsRows.Clear();
        var v = _dsView;
        if (v == null)
        {
            _dsRows.Add(new PerfRow { Section = true, Title = "屏幕" });
            _dsRows.Add(new PerfRow { Title = _dsAgentDown ? "连不上工具箱后台" : "正在读取…", Value = "—", Disabled = true });
            return;
        }
        // 后台在 unsupported 里列出这台机器没有的（比如屏幕不支持 HDR）；字段干脆没有 = 后台还没写这一项
        var unsupported = (v["unsupported"] as JsonArray)?.Select(n => Str(n) ?? "").ToHashSet() ?? new HashSet<string>();
        PerfRow Later(string title, string field = "") => unsupported.Contains(field)
            ? new PerfRow { Title = title, Value = "这台机器不支持", Disabled = true }
            : new PerfRow { Title = title, Value = DsLater, Disabled = true };
        PerfRow Toggle(string title, string field, string method)
        {
            if (v[field] is not JsonValue) return Later(title, field);
            bool on = Bool(v[field]);
            void Set(bool to) { v[field] = to; DsSet(method, 0, JsonValue.Create(to)); }
            return new PerfRow
            {
                Title = title, Choices = new[] { "关", "开" }, Choice = on ? 1 : 0,
                Step = d => { if ((d > 0) != on) Set(d > 0); },
                Press = () => Set(!on),
                Select = i => Set(i == 1),
            };
        }
        PerfRow Number(string title, string field, int min, int max, int step, Func<int, string> fmt, Action<int> send, int? cur = null)
        {
            if (cur == null && v[field] is not JsonValue) return Later(title);
            int value = cur ?? Int(v[field]);
            return new PerfRow
            {
                Title = title, Value = fmt(value), Numeric = true,
                Number = value, Minimum = min, Maximum = max, Increment = step,
                SetNumber = send,
                Step = d => { int nv = Math.Clamp(value + d * step, min, max); if (nv != value) send(nv); },
            };
        }
        // 从一组值里选（刷新率、分辨率、缩放、输出设备……）：值少（≤ 4）平铺，值多用 ‹ › 翻
        PerfRow Pick(string title, string[] names, int idx, Action<int> set)
        {
            idx = Math.Clamp(idx, 0, names.Length - 1);
            if (names.Length <= 4)
                return new PerfRow
                {
                    Title = title, Choices = names, Choice = idx,
                    Step = d => { int ni = Math.Clamp(idx + d, 0, names.Length - 1); if (ni != idx) set(ni); },
                    Press = () => set((idx + 1) % names.Length),
                    Select = set,
                };
            return new PerfRow
            {
                Title = title, Value = names[idx], Numeric = true,
                Step = d => { int ni = Math.Clamp(idx + d, 0, names.Length - 1); if (ni != idx) set(ni); },
            };
        }

        // ---- 显示 ----
        _dsRows.Add(new PerfRow { Section = true, Title = "显示" });
        _dsRows.Add(Number("亮度", "brightness", 0, 100, 5, b => $"{b} %", b => { v["brightness"] = b; DsSet("set_brightness", 150, JsonValue.Create(b)); }));
        int[] rates = (v["refreshRates"] as JsonArray)?.Select(n => Int(n)).Where(r => r > 0).Distinct().OrderByDescending(r => r).ToArray() ?? Array.Empty<int>();
        int hz = Int(v["refresh"]);
        if (rates.Length > 0 && hz > 0)
            _dsRows.Add(Pick("刷新率", rates.Select(r => $"{r} Hz").ToArray(), Array.IndexOf(rates, hz), i => { v["refresh"] = rates[i]; DsSet("set_refresh_rate", 0, JsonValue.Create(rates[i])); }));
        else _dsRows.Add(new PerfRow { Title = "刷新率", Value = hz > 0 ? $"{hz} Hz" : "—", Disabled = true });
        if (v["resolutions"] is JsonArray resArr && resArr.Count > 0 && v["resolution"] is JsonArray cur && cur.Count == 2)
        {
            var res = resArr.OfType<JsonArray>().Where(a => a.Count == 2).Select(a => (W: Int(a[0]), H: Int(a[1]))).ToArray();
            int ri = Array.FindIndex(res, r => r.W == Int(cur[0]) && r.H == Int(cur[1]));
            _dsRows.Add(Pick("分辨率", res.Select(r => $"{r.W} × {r.H}").ToArray(), ri < 0 ? 0 : ri, i =>
            {
                v["resolution"] = new JsonArray(res[i].W, res[i].H);
                // 换分辨率要等屏幕黑一下，连按时只换最后停下的那一档
                DsSet("set_resolution", 600, JsonValue.Create(res[i].W), JsonValue.Create(res[i].H));
            }));
        }
        else _dsRows.Add(Later("分辨率", "resolution"));
        _dsRows.Add(Toggle("可变刷新率（VRR）", "vrr", "set_vrr"));

        if (!unsupported.Contains("autoBrightness") && v["autoBrightness"] is JsonValue)
            _dsRows.Add(Toggle("自动亮度", "autoBrightness", "set_auto_brightness"));
        _dsRows.Add(Toggle("HDR", "hdr", "set_hdr"));
        if (v["scales"] is JsonArray scales && scales.Count > 0)
        {
            int[] sv = scales.Select(n => Int(n)).ToArray();
            _dsRows.Add(Pick("屏幕缩放", sv.Select(s => $"{s} %").ToArray(), Math.Max(0, Array.IndexOf(sv, Int(v["scale"], 100))), i => { v["scale"] = sv[i]; DsSet("set_display_scale", 600, JsonValue.Create(sv[i])); }));
        }
        else _dsRows.Add(Later("屏幕缩放", "scales"));

        // ---- 声音 ----
        _dsRows.Add(new PerfRow { Section = true, Title = "声音" });
        bool muted = Bool(v["muted"]);
        _dsRows.Add(Number("音量", "volume", 0, 100, 2, x => muted ? $"{x} % · 静音" : $"{x} %", x => { v["volume"] = x; DsSet("set_volume", 120, JsonValue.Create(x)); }));
        _dsRows.Add(Toggle("静音", "muted", "set_mute"));
        if (v["outputs"] is JsonArray outs && outs.Count > 0)
        {
            var ids = outs.Select(o => Str(o?["id"]) ?? "").ToArray();
            var names = outs.Select(o => Str(o?["name"]) ?? "未知设备").ToArray();
            int oi = Math.Max(0, Array.IndexOf(ids, Str(v["output"]) ?? ""));
            _dsRows.Add(Pick("输出设备", names, oi, i => { v["output"] = ids[i]; DsSet("set_audio_output", 0, JsonValue.Create(ids[i])); }));
        }
        else _dsRows.Add(Later("输出设备", "outputs"));
        if (v["micMuted"] is JsonValue)
        {
            bool mic = Bool(v["micMuted"]);
            _dsRows.Add(new PerfRow
            {
                Title = "麦克风", Choices = new[] { "开", "静音" }, Choice = mic ? 1 : 0,
                Step = d => { if ((d > 0) != mic) { v["micMuted"] = d > 0; DsSet("set_mic_mute", 0, JsonValue.Create(d > 0)); } },
                Press = () => { v["micMuted"] = !mic; DsSet("set_mic_mute", 0, JsonValue.Create(!mic)); },
                Select = i => { v["micMuted"] = i == 1; DsSet("set_mic_mute", 0, JsonValue.Create(i == 1)); },
            });
        }
        else _dsRows.Add(Later("麦克风", "micMuted"));

        // ---- 显卡（Intel 驱动的控制接口；拿不到的项后台不给，这里就不出现） ----
        _dsRows.Add(new PerfRow { Section = true, Title = "显卡" });
        var gpu = v["gpu"] as JsonObject;
        if (gpu == null || !Bool(gpu["available"]))
            _dsRows.Add(gpu == null ? Later("Intel 显卡选项", "gpu") : new PerfRow { Title = "Intel 显卡选项", Value = Str(gpu["error"]) ?? "读不到显卡驱动", Disabled = true });
        else
        {
            void SetGpu(string name, JsonNode value) { gpu[name] = value.DeepClone(); DsSet("set_gpu_option", 200, JsonValue.Create(name), value); }
            if (Str(gpu["lowLatency"]) is string ll)
                _dsRows.Add(Pick("低延迟", DsLowLatencyNames, Math.Max(0, Array.IndexOf(DsLowLatency, ll)), i => SetGpu("lowLatency", JsonValue.Create(DsLowLatency[i]))));
            if (Str(gpu["frameSync"]) is string fs && gpu["frameSyncOptions"] is JsonArray fso && fso.Count > 0)
            {
                var ids = fso.Select(o => Str(o?["id"]) ?? "").ToArray();
                var names = fso.Select(o => Str(o?["name"]) ?? "").ToArray();
                _dsRows.Add(Pick("帧同步", names, Math.Max(0, Array.IndexOf(ids, fs)), i => SetGpu("frameSync", JsonValue.Create(ids[i]))));
            }
            if (Str(gpu["xessFg"]) is string fg && gpu["xessFgOptions"] is JsonArray fgo && fgo.Count > 0)
            {
                var ids = fgo.Select(o => Str(o?["id"]) ?? "").ToArray();
                var names = fgo.Select(o => Str(o?["name"]) ?? "").ToArray();
                _dsRows.Add(Pick("XeSS 多帧生成", names, Math.Max(0, Array.IndexOf(ids, fg)), i => SetGpu("xessFg", JsonValue.Create(ids[i]))));
                _dsRows.Add(new PerfRow { Title = "使用条件", Value = "需在支持的游戏内开启 XeSS 插帧", Disabled = true });
            }
            if (Str(gpu["scaling"]) is string sc)
                _dsRows.Add(Pick("显卡缩放", DsScalingNames, Math.Max(0, Array.IndexOf(DsScaling, sc)), i => SetGpu("scaling", JsonValue.Create(DsScaling[i]))));
            foreach (var (field, title) in new[] { ("sharpen", "锐化"), ("saturation", "饱和度"), ("contrast", "对比度"), ("gamma", "伽马") })
                if (gpu[field] is JsonValue)
                {
                    int val = Int(gpu[field]);
                    _dsRows.Add(Number(title, field, 0, 100, 5, x => $"{x}", x => SetGpu(field, JsonValue.Create(x)), val));
                }
        }

        if (_dsFocus >= _dsRows.Count || (_dsFocus >= 0 && (_dsRows[_dsFocus].Skip || _dsRows[_dsFocus].Section)))
        {
            int f = Math.Min(_dsFocus, _dsRows.Count - 1);
            while (f > 0 && (_dsRows[f].Section || _dsRows[f].Skip)) f--;
            _dsFocus = f;
        }
    }

    // ---------------- 输入 ----------------

    private void DsMove(Direction dir)
    {
        SettingsResumeKeyboard();
        if (dir is Direction.Up or Direction.Down)
        {
            int d = dir == Direction.Up ? -1 : 1;
            for (int i = _dsFocus + d; i >= 0 && i < _dsRows.Count; i += d)
                if (!_dsRows[i].Section && !_dsRows[i].Skip) { _dsFocus = i; PaintDs(); return; }
            return;
        }
        if (_dsFocus >= 0 && _dsFocus < _dsRows.Count && !_dsRows[_dsFocus].Disabled) RowStep(_dsRows[_dsFocus], dir == Direction.Left ? -1 : 1);
    }

    private void DsActivate()
    {
        SettingsResumeKeyboard();
        if (_dsFocus < 0 || _dsFocus >= _dsRows.Count) return;
        var r = _dsRows[_dsFocus];
        if (r.Disabled) { if (r.Value == DsLater) Toast($"这一项{DsLater}"); return; }
        RowPress(r);
    }

    private void DsBack() { if (!SettingsFinishEdit()) CloseDisplay(); }

    // ---------------- 绘制 ----------------

    private void PaintDs()
    {
        if (_ds == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        float h = RowsHeight(_dsRows), vh = PerfViewH;
        using (var tl = TextKit.Layout(G.Device, "显示与声音", TextKit.Ui(700), 24, 400, CanvasHorizontalAlignment.Left))
        {
            var bb = tl.LayoutBounds;
            float w = (float)bb.Width + 4, th = (float)bb.Height + 4;
            CcSet(_dsTitle, CcDraw(w, th, raster, ds => ds.DrawTextLayout(tl, -(float)bb.X, -(float)bb.Y, CcText)));
            _dsTitle.Size = new Vector2(w * k, th * k);
            _dsTitle.Offset = new Vector3(40 * k, 32 * k, 0);
        }
        _settingsTargets.Clear();
        CcSet(_dsRight, CcDraw(PerfRightW, h, raster, ds => PaintRowList(ds, _dsRows, _dsFocus, _settingsTargets)));
        _dsRight.Size = new Vector2(PerfRightW * k, h * k);
        _dsRightView.Size = new Vector2(PerfRightW * k, vh * k);
        _dsRightView.Offset = new Vector3(PerfRightX * k, PerfTop * k, 0);
        // 指针命中使用同一滚动位置；直接滚动避免动画中看到的位置与命中区域不一致。
        RowScrollTo(_dsRight, _dsRows, _settingsManualScroll ? -1 : _dsFocus, ref _dsScroll, ref _dsScrollY, h, vh, false);
        CcSet(_dsLeft, CcDraw(PerfLeftW, vh, raster, ds => PaintDsLeft(ds, vh)));
        _dsLeft.Size = new Vector2(PerfLeftW * k, vh * k);
        _dsLeft.Offset = new Vector3(PerfLeftX * k, PerfTop * k, 0);
    }

    /// <summary>
    /// 左栏只放读数，和性能页左栏一个写法：「屏幕」标题 + 亮度值、一根亮度条、下面几行当前的显示设置；
    /// 「声音」标题 + 音量值、一根音量条（静音时变灰、写「静音」）、下面输出设备和麦克风。
    /// </summary>
    private void PaintDsLeft(CanvasDrawingSession ds, float h)
    {
        var ui = TextKit.Ui(400);
        var v = _dsView;
        float W = PerfLeftW;
        var accent = PerfAccent;
        var muted = Gfx.WithAlpha(CcText, 0.5);
        var bright = Gfx.WithAlpha(CcText, 0.88);
        const float header = 30, rowH = 28, between = 32;

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
                string val = GpFit(ds, rows[i].v, ui, 14, W - 110);
                float vw = PWidth(ds, val, ui, 14);
                PText(ds, val, ui, 14, bright, W - vw, cy, vw + 4);
            }
        }

        void Bar(float y, int value, bool dim)
        {
            ds.FillRoundedRectangle(0, y - 3, W, 6, 3, 3, Gfx.WithAlpha(CcText, 0.14f));
            if (value > 0)
                ds.FillRoundedRectangle(0, y - 3, Math.Max(6, W * value / 100f), 6, 3, 3, dim ? Gfx.WithAlpha(CcText, 0.35f) : accent);
        }

        // ---- 屏幕 ----
        int bri = Int(v?["brightness"], -1), hz = Int(v?["refresh"]);
        Header(0, "显示", bri >= 0 ? $"{bri} %" : "—", accent);
        float briY = header + 14;
        Bar(briY, bri, false);
        var res = v?["resolution"] as JsonArray;
        string resText = res?.Count == 2 ? $"{Int(res[0])} × {Int(res[1])}" : "—";
        string hzText = hz > 0 ? $"{hz} Hz" : "—";
        string vrr = v?["vrr"] is JsonValue ? (Bool(v["vrr"]) ? "开" : "关") : "—";
        var screenRows = new List<(string, string)> { ("分辨率", resText), ("刷新率", hzText), ("可变刷新率", vrr) };
        if (v?["hdr"] is JsonValue) screenRows.Add(("HDR", Bool(v["hdr"]) ? "开" : "关"));
        if (v?["scale"] is JsonValue) screenRows.Add(("屏幕缩放", $"{Int(v["scale"])} %"));
        float rowsTop = briY + 14;
        Rows(rowsTop, screenRows.ToArray());

        // ---- 声音 ----
        float aTop = rowsTop + screenRows.Count * rowH + between;
        int vol = Int(v?["volume"], -1);
        bool isMuted = Bool(v?["muted"]);
        Header(aTop, "声音", vol < 0 ? "—" : isMuted ? "静音" : $"{vol} %", isMuted ? Gfx.WithAlpha(CcText, 0.55f) : accent);
        float barY = aTop + header + 14;
        Bar(barY, vol, isMuted);
        string output = "—";
        if (v?["outputs"] is JsonArray outs && Str(v["output"]) is string oid)
            output = outs.FirstOrDefault(o => Str(o?["id"]) == oid) is JsonNode on ? Str(on["name"]) ?? "—" : "—";
        string mic = v?["micMuted"] is JsonValue ? (Bool(v["micMuted"]) ? "静音" : "开") : "—";
        Rows(barY + 14, new[] { ("输出设备", output), ("麦克风", mic) });

        if (_dsAgentDown || v == null)
            PText(ds, _dsAgentDown ? "连不上工具箱后台" : "正在读取…", ui, 13, muted, 0, h - 12, W);
    }
}
