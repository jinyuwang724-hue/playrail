// 控制中心 → 手柄：和性能页同一套 3:7 两栏（背景还是控制中心那层动态背景）。
// 左栏只看：Claw 8 EX 正面 / 背面线稿（MSI Center M 里 1T91 机型的图，Assets/Pad），按下的键实时点亮，
// 右栏焦点行对应的键描一圈；下面是两个摇杆的实时位置和扳机行程。
// 右栏只改：背键、Steam 大屏幕里的特殊键、震动、摇杆与扳机、手柄模式、灯光、陀螺仪。
// 现在真能改的：特殊键（工具箱后台的按键接管：接管开关、Claw 键 / 快速设置键动作、导航震动）和震动强度 / 测试；
// 其余要先在后台做固件协议，行先摆上、置灰写「之后」。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Rect = Windows.Foundation.Rect;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // 线稿原图 700×310（MSI Center M 的 img_keymapping_1t91 / img_keymapping_back_1t91，深色版）
    private const float GpArtW = 700, GpArtH = 310;
    private static readonly string[] GpActions = { "MainMenu", "QuickAccess", "MagpieToggle", "None" };
    private static readonly string[] GpActionNames = { "主菜单", "快速访问", "超分开关", "不做事" };

    /// <summary>线稿上一个键的位置（原图像素）：圆键给圆心半径，其余给矩形和圆角。</summary>
    private readonly record struct GpKey(bool Back, float X, float Y, float W, float H, float R, ushort XInput = 0);

    private static readonly Dictionary<string, GpKey> GpKeys = new()
    {
        ["A"] = new(false, 624, 99, 24, 24, 12, 0x1000), ["B"] = new(false, 645, 79, 24, 24, 12, 0x2000),
        ["X"] = new(false, 603, 79, 24, 24, 12, 0x4000), ["Y"] = new(false, 624, 57, 24, 24, 12, 0x8000),
        ["LS"] = new(false, 69, 82, 50, 50, 25, 0x0040), ["RS"] = new(false, 582, 131, 50, 50, 25, 0x0080),
        ["Up"] = new(false, 112, 113, 17, 15, 4, 0x0001), ["Down"] = new(false, 112, 146, 17, 15, 4, 0x0002),
        ["Left"] = new(false, 96, 129, 15, 17, 4, 0x0004), ["Right"] = new(false, 128, 129, 15, 17, 4, 0x0008),
        ["View"] = new(false, 133, 55, 11, 17, 5, 0x0020), ["QS"] = new(false, 133, 78, 11, 17, 5),
        ["Menu"] = new(false, 564, 55, 11, 17, 5, 0x0010), ["Claw"] = new(false, 564, 78, 11, 17, 5),
        ["LB"] = new(false, 76, 20, 76, 14, 7, 0x0100), ["RB"] = new(false, 624, 20, 76, 14, 7, 0x0200),
        ["M1"] = new(true, 120, 136, 28, 46, 8), ["M2"] = new(true, 578, 136, 28, 46, 8),
    };

    private ContainerVisual? _gp;
    private SpriteVisual _gpTitle = null!, _gpLeft = null!, _gpRight = null!;
    private ContainerVisual _gpRightView = null!;
    private bool _gpOpen, _gpBusy;
    private int _gpFocus, _gpVersion, _gpRumbleEdits;
    private float _gpScroll, _gpScrollY = float.NaN;
    private readonly List<PerfRow> _gpRows = new();
    /// <summary>每行对应线稿上哪些键（焦点在这行时描圈）。</summary>
    private readonly List<string[]> _gpRowKeys = new();
    private JsonObject? _gpSettings;
    private int? _gpRumble;
    private string? _gpError;
    private CanvasBitmap? _gpFront, _gpBack;
    private DispatcherQueueTimer? _gpLive;
    private Input.GamepadPoller.Snapshot _gpShown;

    // ---------------- 开 / 关 ----------------

    private void OpenGamepad()
    {
        if (_cc == null) return;
        EnsureGpVisuals();
        _gpOpen = true;
        ++_gpVersion;
        _gpFocus = -1;
        _gpScroll = 0;
        _gpScrollY = float.NaN;
        BuildGpRows();
        _gpFocus = _gpRows.FindIndex(r => !r.Section && !r.Skip);
        _gpShown = _pad?.Last ?? default;
        PaintGp();
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        _gp!.IsVisible = true;
        _gp.Opacity = 0;
        _gp.StartAnimation("Opacity", ToScalar(1, 240, EaseOutCss, 80));
        _gpLive ??= MakeGpLiveTimer();
        _gpLive.Start();
        _ = GpLoadArt();
        _ = GpRefresh();
        Log.Info("gamepad page open");
    }

    private void CloseGamepad(bool instant = false)
    {
        if (!_gpOpen || _gp == null) return;
        _gpOpen = false;
        _gpLive?.Stop();
        int v = ++_gpVersion;
        if (instant || Reduced)
        {
            _gp.StopAnimation("Opacity"); _gp.IsVisible = false; _gp.Opacity = 1;
            foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); vis.Opacity = 1; }
            return;
        }
        _gp.StartAnimation("Opacity", ToScalar(0, 140, EaseOutCss));
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(1, 220, EaseOutCss, 60));
        After(160, () => { if (v == _gpVersion && !_gpOpen && _gp != null) { _gp.IsVisible = false; _gp.StopAnimation("Opacity"); _gp.Opacity = 1; } });
        Log.Info("gamepad page close");
    }

    private void EnsureGpVisuals()
    {
        if (_gp != null) return;
        _gp = G.Container();
        _gp.IsVisible = false;
        _gpTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _gpLeft = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _gpRight = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _gpRightView = G.Container(relative: false);
        _gpRightView.Clip = G.C.CreateInsetClip();
        _gpRightView.Children.InsertAtTop(_gpRight);
        foreach (var s in new Visual[] { _gpTitle, _gpLeft, _gpRightView }) _gp.Children.InsertAtTop(s);
        _cc!.Children.InsertAtTop(_gp);
    }

    /// <summary>实时输入：30 Hz 看一眼手柄状态，变了才重画左栏。</summary>
    private DispatcherQueueTimer MakeGpLiveTimer()
    {
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(33);
        t.IsRepeating = true;
        t.Tick += (_, _) =>
        {
            if (!_gpOpen) return;
            var now = _pad?.Last ?? default;
            if (now == _gpShown) return;
            _gpShown = now;
            PaintGpLeft();
        };
        return t;
    }

    private async Task GpLoadArt()
    {
        if (_gpFront != null) return;
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Pad");
            _gpFront = await CanvasBitmap.LoadAsync(G.Device, Path.Combine(dir, "claw8ex-front.png"));
            _gpBack = await CanvasBitmap.LoadAsync(G.Device, Path.Combine(dir, "claw8ex-back.png"));
            if (_gpOpen) PaintGpLeft();
        }
        catch (Exception ex) { Log.Warn($"gamepad art: {ex.Message}"); }
    }

    // ---------------- 和 agent 打交道 ----------------

    private async Task GpRefresh()
    {
        if (_gpBusy) return;
        _gpBusy = true;
        try
        {
            _agent ??= new AgentClient();
            var state = await _agent.Call("get_state");
            if (state?["settings"] is JsonObject s) _gpSettings = (JsonObject)s.DeepClone();
            var rumble = await _agent.Call("get_rumble_state");
            if (_gpRumbleEdits == 0 && rumble?["strength"] is JsonValue rv && rv.TryGetValue(out int st)) _gpRumble = st;
            _gpError = null;
        }
        catch (Exception ex)
        {
            if (_gpError == null) Log.Warn($"gamepad refresh: {ex.Message}");
            _gpError = "连不上工具箱后台";
        }
        finally { _gpBusy = false; }
        if (!_gpOpen) return;
        BuildGpRows();
        PaintGp();
    }

    private async Task GpSaveSettings()
    {
        if (_gpSettings == null || _agent == null) return;
        try
        {
            var state = await _agent.Call("save_settings", _gpSettings.DeepClone());
            if (state?["settings"] is JsonObject s) _gpSettings = (JsonObject)s.DeepClone();
        }
        catch (Exception ex) { Toast($"没有保存：{ex.Message}"); _ = GpRefresh(); return; }
        if (_gpOpen) { BuildGpRows(); PaintGp(); }
    }

    /// <summary>震动强度：连按 ← → 时 400 ms 内只写一次固件（写一次要来回好几包 HID）。</summary>
    private void GpRumbleChanged(int value)
    {
        _gpRumble = value;
        int edits = ++_gpRumbleEdits;
        BuildGpRows();
        PaintGp();
        After(400, async () =>
        {
            if (edits != _gpRumbleEdits || _agent == null) return;
            try { await _agent.Call("set_rumble_strength", JsonValue.Create(value)); }
            catch (Exception ex) { Toast($"震动强度没有写进手柄：{ex.Message}"); }
            if (edits == _gpRumbleEdits) _gpRumbleEdits = 0;
        });
    }

    private async Task GpTestRumble()
    {
        if (_agent == null) return;
        try { await _agent.Call("test_rumble"); }
        catch (Exception ex) { Toast($"没有震起来：{ex.Message}"); }
    }

    // ---------------- 右栏的行 ----------------

    private void BuildGpRows()
    {
        _gpRows.Clear();
        _gpRowKeys.Clear();
        void Add(PerfRow r, params string[] keys) { _gpRows.Add(r); _gpRowKeys.Add(keys); }
        void Section(string t) => Add(new PerfRow { Section = true, Title = t });
        PerfRow Later(string title, string why = "之后") => new() { Title = title, Value = why, Disabled = true };

        Section("背键");
        Add(Later("M1", "之后 · 要先做固件按键映射"), "M1");
        Add(Later("M2", "之后 · 要先做固件按键映射"), "M2");

        var s = _gpSettings;
        Section("Steam 大屏幕里的特殊键");
        if (s == null)
        {
            Add(new PerfRow { Title = _gpError ?? "正在读取…", Value = "—", Disabled = true });
        }
        else
        {
            bool on = Bool(s["enabled"]);
            PerfRow Choice(string title, string[] names, int idx, Action<int> set, bool disabled = false) => new()
            {
                Title = title, Choices = names, Choice = idx, Disabled = disabled,
                Step = d => { if (!disabled) { int ni = Math.Clamp(idx + d, 0, names.Length - 1); if (ni != idx) set(ni); } },
                Press = () => { if (!disabled) set((idx + 1) % names.Length); },
            };
            PerfRow KeyAction(string title, string key) => Choice(title, GpActionNames, Math.Max(0, Array.IndexOf(GpActions, Str(s[key]) ?? "None")),
                i => { s[key] = GpActions[i]; BuildGpRows(); PaintGp(); _ = GpSaveSettings(); }, !on);
            Add(Choice("接管特殊键", new[] { "关", "开" }, on ? 1 : 0, i => { s["enabled"] = i == 1; BuildGpRows(); PaintGp(); _ = GpSaveSettings(); }), "Claw", "QS");
            Add(KeyAction("Claw 键", "msiAction"), "Claw");
            Add(KeyAction("快速设置键", "qamAction"), "QS");
            Add(Choice("导航震动", new[] { "关", "开" }, Bool(s["hapticsEnabled"]) ? 1 : 0,
                i => { s["hapticsEnabled"] = i == 1; BuildGpRows(); PaintGp(); _ = GpSaveSettings(); }));
        }

        Section("震动");
        if (_gpRumble is int rumble)
        {
            Add(new PerfRow
            {
                Title = "震动强度", Value = rumble == 0 ? "关" : $"{rumble} %", Numeric = true,
                Step = d => { int nv = Math.Clamp(rumble + d * 5, 0, 100); if (nv != rumble) GpRumbleChanged(nv); },
            });
            Add(new PerfRow { Title = "测试震动", Value = "›", Press = () => _ = GpTestRumble() });
        }
        else Add(new PerfRow { Title = "震动强度", Value = _gpError ?? "正在读取…", Disabled = true });

        Section("摇杆与扳机");
        Add(Later("左摇杆死区"), "LS");
        Add(Later("右摇杆死区"), "RS");
        Add(Later("扳机行程"), "LB", "RB");

        Section("更多");
        Add(Later("手柄模式", "之后 · 手柄 / 桌面"));
        Add(Later("灯光", "之后 · 摇杆灯效、颜色、亮度"), "LS", "RS");
        Add(Later("陀螺仪", "之后"));

        if (_gpFocus >= _gpRows.Count || (_gpFocus >= 0 && _gpRows[_gpFocus].Section))
            _gpFocus = _gpRows.FindLastIndex(r => !r.Section);
    }

    // ---------------- 输入 ----------------

    private void GpMove(Direction dir)
    {
        if (dir is Direction.Up or Direction.Down)
        {
            int d = dir == Direction.Up ? -1 : 1;
            for (int i = _gpFocus + d; i >= 0 && i < _gpRows.Count; i += d)
                if (!_gpRows[i].Section && !_gpRows[i].Skip) { _gpFocus = i; PaintGp(); return; }
            return;
        }
        if (_gpFocus >= 0 && _gpFocus < _gpRows.Count) _gpRows[_gpFocus].Step?.Invoke(dir == Direction.Left ? -1 : 1);
    }

    private void GpActivate()
    {
        if (_gpFocus >= 0 && _gpFocus < _gpRows.Count) _gpRows[_gpFocus].Press?.Invoke();
    }

    // ---------------- 绘制 ----------------

    private void PaintGp()
    {
        if (_gp == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        float h = RowsHeight(_gpRows), vh = PerfViewH;
        using (var tl = TextKit.Layout(G.Device, "手柄", TextKit.Ui(700), 24, 400, CanvasHorizontalAlignment.Left))
        {
            var bb = tl.LayoutBounds;
            float w = (float)bb.Width + 4, th = (float)bb.Height + 4;
            CcSet(_gpTitle, CcDraw(w, th, raster, ds => ds.DrawTextLayout(tl, -(float)bb.X, -(float)bb.Y, CcText)));
            _gpTitle.Size = new Vector2(w * k, th * k);
            _gpTitle.Offset = new Vector3(40 * k, 32 * k, 0);
        }
        CcSet(_gpRight, CcDraw(PerfRightW, h, raster, ds => PaintRowList(ds, _gpRows, _gpFocus)));
        _gpRight.Size = new Vector2(PerfRightW * k, h * k);
        _gpRightView.Size = new Vector2(PerfRightW * k, vh * k);
        _gpRightView.Offset = new Vector3(PerfRightX * k, PerfTop * k, 0);
        RowScrollTo(_gpRight, _gpRows, _gpFocus, ref _gpScroll, ref _gpScrollY, h, vh, _gpOpen);
        PaintGpLeft();
    }

    private void PaintGpLeft()
    {
        if (_gp == null) return;
        float k = _ccK, raster = k * Chrome.Dpi, vh = PerfViewH;
        CcSet(_gpLeft, CcDraw(PerfLeftW, vh, raster, ds => PaintGpLeft(ds, vh)));
        _gpLeft.Size = new Vector2(PerfLeftW * k, vh * k);
        _gpLeft.Offset = new Vector3(PerfLeftX * k, PerfTop * k, 0);
    }

    private void PaintGpLeft(CanvasDrawingSession ds, float h)
    {
        var ui = TextKit.Ui(400);
        float W = PerfLeftW;
        var accent = PerfAccent;
        var muted = Gfx.WithAlpha(CcText, 0.5);
        const float header = 30;
        bool connected = _pad?.Connected ?? false;
        var snap = _gpShown;
        var focusKeys = _gpFocus >= 0 && _gpFocus < _gpRowKeys.Count ? _gpRowKeys[_gpFocus] : Array.Empty<string>();

        void Header(float top, string title, string value)
        {
            PText(ds, title, ui, 13, muted, 0, top + header / 2, 300);
            if (value.Length == 0) return;
            float vw = PWidth(ds, value, ui, 13);
            PText(ds, value, ui, 13, muted, W - vw, top + header / 2, vw + 4);
        }

        // 线稿：按栏宽等比缩放；按下的键填强调色，焦点行对应的键描一圈
        float s = W / GpArtW, artH = GpArtH * s;
        void Art(CanvasBitmap? bmp, float top, bool back)
        {
            if (bmp == null) { PText(ds, "正在载入线稿…", ui, 13, muted, 0, top + artH / 2, 300); return; }
            ds.DrawImage(bmp, new Rect(0, top, W, artH), bmp.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
            foreach (var (name, key) in GpKeys)
            {
                if (key.Back != back) continue;
                bool pressed = key.XInput != 0 && (snap.Buttons & key.XInput) != 0
                    || name == "LB" && snap.LT > 30 || name == "RB" && snap.RT > 30;
                bool focused = Array.IndexOf(focusKeys, name) >= 0;
                if (!pressed && !focused) continue;
                float cx = key.X * s, cy = top + key.Y * s, w = key.W * s, kh = key.H * s, r = key.R * s;
                // 摇杆按下 / 推动：圈跟着摇杆位置挪一点，看得出往哪推
                if (name is "LS" or "RS")
                {
                    var (ax, ay) = name == "LS" ? (snap.LX, snap.LY) : (snap.RX, snap.RY);
                    cx += ax / 32767f * 6; cy -= ay / 32767f * 6;
                }
                var rect = new Rect(cx - w / 2, cy - kh / 2, w, kh);
                if (pressed) ds.FillRoundedRectangle(rect, r, r, Gfx.WithAlpha(accent, 0.85));
                if (focused)
                {
                    var ring = new Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6);
                    ds.DrawRoundedRectangle(ring, r + 3, r + 3, accent, 2f);
                }
            }
        }

        Header(0, "正面", connected ? "已连接" : "没有检测到手柄");
        Art(_gpFront, header, back: false);
        float backTop = header + artH + 18;
        Header(backTop, "背面", "");
        Art(_gpBack, backTop + header, back: true);

        // 摇杆与扳机的实时值：两个圈里一个点，下面是扳机按下的百分比
        float liveTop = backTop + header + artH + 24;
        Header(liveTop, "摇杆与扳机 · 实时", "");
        float d = 108, cyS = liveTop + header + 8 + d / 2;
        void Stick(float cx, short ax, short ay, string label, byte trigger, string tlabel)
        {
            ds.DrawCircle(cx, cyS, d / 2, Gfx.WithAlpha(CcText, 0.28f), 1.2f);
            ds.DrawLine(cx - d / 2, cyS, cx + d / 2, cyS, Gfx.WithAlpha(CcText, 0.08f), 1);
            ds.DrawLine(cx, cyS - d / 2, cx, cyS + d / 2, Gfx.WithAlpha(CcText, 0.08f), 1);
            float px = cx + ax / 32767f * (d / 2 - 6), py = cyS - ay / 32767f * (d / 2 - 6);
            ds.FillCircle(px, py, 6, connected ? accent : Gfx.WithAlpha(CcText, 0.3f));
            float ly = cyS + d / 2 + 18;
            string v = $"{label}  {ax / 32767f:+0.00;-0.00; 0.00}, {ay / 32767f:+0.00;-0.00; 0.00}";
            float vw = PWidth(ds, v, new FontSpec("Cascadia Mono", 400), 11);
            PText(ds, v, new FontSpec("Cascadia Mono", 400), 11, muted, cx - vw / 2, ly, vw + 4);
            string t = $"{tlabel}  {trigger * 100 / 255} %";
            float tw = PWidth(ds, t, ui, 14);
            PText(ds, t, ui, 14, trigger > 30 ? accent : Gfx.WithAlpha(CcText, 0.88), cx - tw / 2, ly + 22, tw + 4);
        }
        Stick(W * 0.25f, snap.LX, snap.LY, "L", snap.LT, "LT");
        Stick(W * 0.75f, snap.RX, snap.RY, "R", snap.RT, "RT");
    }
}
