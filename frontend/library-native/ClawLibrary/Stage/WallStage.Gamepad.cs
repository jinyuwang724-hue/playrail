// 控制中心 → 手柄：左边一列轻量的分类（只有字：按键 / 摇杆 / 扳机 / 震动 / 特殊键 / 更多），右边那块是 Claw 8 EX 线稿，
// 当前这一类的设置挂在对应的键旁边（背景还是控制中心那层动态背景）。
// 线稿：MSI Center M 里 1T91 机型的正面 / 背面图（Assets/Pad）。按下的键实时点亮、摇杆推动时点跟着走；
// 焦点所在的设置拉一根强调色细线连到键上、键描一圈；焦点到 M1 / M2 时线稿翻到背面。
// 手柄：分类上 ↑ ↓ 换类（右边跟着换），→ 或 A 进到设置；设置之间十字键按方位移动，最左再按 ← 或 B 回分类；
// A 开关 / 测试，或进入调节（← → 改，A / B 完成）；分类上按 B 回控制中心。
// 现在真能改的：特殊键（工具箱后台的按键接管：接管开关、Claw 键 / 快速设置键动作）、导航震动、震动强度 / 测试；
// 其余要先在后台做固件协议，先摆上、置灰写「之后」。
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
    // 线稿原图 700×310（MSI Center M 的 img_keymapping_1t91 / img_keymapping_back_1t91，深色版）；舞台上放大到 600 宽，放在分类右边那块的正中
    private const float GpArtW = 700, GpArtH = 310;
    private const float GpX = 420, GpY = 262, GpW = 600, GpS = GpW / GpArtW, GpH = GpArtH * GpS;
    // 两侧设置的内边（左边右对齐到 390，右边从 1050 起），下面一排的标签行
    private const float GpLeftEdge = 390, GpRightEdge = 1050, GpBottomY = 586;
    // 左边分类：x 40，第一项中线 y 150，行距 54
    private const float GpSideX = 40, GpSideTop = 150, GpSideStep = 54;
    private static readonly string[] GpActions = { "MainMenu", "QuickAccess", "MagpieToggle", "None" };
    private static readonly string[] GpActionNames = { "主菜单", "快速访问", "超分开关", "不做事" };

    /// <summary>线稿上一个键的位置（原图像素）：中心、宽高、圆角；XInput 位不为 0 的会跟着实时按键点亮。</summary>
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
        ["Dpad"] = new(false, 112, 129, 58, 58, 29), ["ABXY"] = new(false, 624, 78, 72, 72, 36),
        ["M1"] = new(true, 120, 136, 28, 46, 8), ["M2"] = new(true, 578, 136, 28, 46, 8),
        ["GripL"] = new(false, 70, 222, 130, 146, 30), ["GripR"] = new(false, 630, 222, 130, 146, 30),
    };

    /// <summary>一个设置：Side -1 = 线稿左边（右对齐到 GpLeftEdge），1 = 右边，0 = 下面一排（X 是左边）。</summary>
    private sealed class GpItem
    {
        public string Title = "", Value = "";
        public int Side;
        public float X, Y;
        public string Key = "";
        /// <summary>焦点在这项时线稿上填哪几块（不写就填 Key 那一块）。</summary>
        public string[]? Lights;
        public bool Disabled;
        public string? Why;
        public string[]? Choices;
        public int Choice;
        public Action<int>? SetChoice;
        public int? Number;
        public int Min, Max = 100, StepBy = 1;
        public Func<int, string>? Format;
        public Action<int>? SetNumber;
        public Action? Press;
        public bool Back => Key is "M1" or "M2";
        public bool Adjustable => !Disabled && (Choices != null || Number != null);
    }

    private ContainerVisual? _gp;
    private SpriteVisual _gpFrontArt = null!, _gpBackArt = null!, _gpBody = null!;
    private bool _gpOpen, _gpBusy, _gpEditing, _gpShowBack;
    /// <summary>焦点在左边分类上（true）还是在这一类的设置上。</summary>
    private bool _gpInSide = true;
    private int _gpCat;
    private int _gpFocus, _gpVersion, _gpRumbleEdits;
    private float _gpArtK;
    private readonly List<GpItem> _gpItems = new();
    private JsonObject? _gpSettings;
    private int? _gpRumble;
    private string? _gpError;
    private CanvasBitmap? _gpFront, _gpBack;
    /// <summary>握把、肩部、背键的高亮区域：Assets/Pad/regions.txt 里的矢量轮廓，换算到舞台坐标后的几何（抗锯齿填充，放多大边都是平的）。</summary>
    private readonly Dictionary<string, CanvasGeometry> _gpRegions = new();
    private DispatcherQueueTimer? _gpLive;
    private Input.GamepadPoller.Snapshot _gpShown;

    // ---------------- 开 / 关 ----------------

    private void OpenGamepad()
    {
        if (_cc == null) return;
        EnsureGpVisuals();
        _gpOpen = true;
        _gpEditing = false;
        ++_gpVersion;
        _gpInSide = true;
        _gpFocus = 0;
        BuildGpItems();
        _gpShowBack = false;
        _gpFrontArt.Opacity = 1;
        _gpBackArt.Opacity = 0;
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
        _gpEditing = false;
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
        _gpFrontArt = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _gpBackArt = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _gpBody = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        foreach (var s in new Visual[] { _gpFrontArt, _gpBackArt, _gpBody }) _gp.Children.InsertAtTop(s);
        _cc!.Children.InsertAtTop(_gp);
    }

    /// <summary>实时输入：30 Hz 看一眼手柄状态，变了才重画（线稿上的高亮在 _gpBody 这层）。</summary>
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
            PaintGpBody();
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
            GpLoadRegions(Path.Combine(dir, "regions.txt"));
            _gpArtK = 0;
            if (_gpOpen) PaintGp();
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
        BuildGpItems();
        PaintGpBody();
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
        if (_gpOpen) { BuildGpItems(); PaintGpBody(); }
    }

    /// <summary>震动强度：连按 ← → 时 400 ms 内只写一次固件（写一次要来回好几包 HID）。</summary>
    private void GpRumbleChanged(int value)
    {
        _gpRumble = value;
        int edits = ++_gpRumbleEdits;
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

    // ---------------- 设置项 ----------------

    /// <summary>左边的分类（只有字）；每类只把自己的几项挂到线稿旁边。</summary>
    private static readonly string[] GpCats = { "按键", "摇杆", "扳机", "震动", "特殊键", "更多" };

    private void BuildGpItems()
    {
        _gpItems.Clear();
        var s = _gpSettings;
        bool on = s != null && Bool(s["enabled"]);
        string loading = _gpError ?? "正在读取…";
        const string Later = "之后做：要先在后台接上手柄固件";
        var left = new List<GpItem>();
        var right = new List<GpItem>();
        var bottom = new List<GpItem>();
        GpItem Item(string title, string key, string value, string? why = null) => new()
        {
            Title = title, Key = key, Value = value, Disabled = why != null, Why = why,
        };
        GpItem KeyAction(string title, string key, string field)
        {
            if (s == null) return Item(title, key, loading, loading);
            int idx = Math.Max(0, Array.IndexOf(GpActions, Str(s[field]) ?? "None"));
            var it = Item(title, key, on ? GpActionNames[idx] : "未接管", on ? null : "先打开下面的「接管」");
            it.Choices = GpActionNames;
            it.Choice = idx;
            it.SetChoice = i => { s[field] = GpActions[i]; _ = GpSaveSettings(); };
            return it;
        }
        GpItem Toggle(string title, string key, string field, string? why = null)
        {
            if (s == null) return Item(title, key, loading, loading);
            bool v = Bool(s[field]);
            var it = Item(title, key, v ? "开" : "关", why);
            it.Press = () => { s[field] = !v; BuildGpItems(); PaintGpBody(); _ = GpSaveSettings(); };
            return it;
        }

        switch (GpCats[_gpCat])
        {
            case "按键":
                left.Add(Item("左肩键  LB · LT", "LB", "原键位", Later));
                left.Add(Item("十字键", "Dpad", "原键位", Later));
                left.Add(Item("M1 背键", "M1", "未映射", Later));
                right.Add(Item("右肩键  RB · RT", "RB", "原键位", Later));
                right.Add(Item("A  B  X  Y", "ABXY", "原键位", Later));
                right.Add(Item("M2 背键", "M2", "未映射", Later));
                break;
            case "摇杆":
                left.Add(Item("左摇杆死区", "LS", "之后", Later));
                left.Add(Item("左摇杆外圈", "LS", "之后", Later));
                right.Add(Item("右摇杆死区", "RS", "之后", Later));
                right.Add(Item("右摇杆外圈", "RS", "之后", Later));
                bottom.Add(new GpItem { Title = "交换左右摇杆", Value = "之后", Disabled = true, Why = Later, Lights = new[] { "LS", "RS" } });
                break;
            case "扳机":
                left.Add(Item("左扳机行程", "LB", "之后", Later));
                right.Add(Item("右扳机行程", "RB", "之后", Later));
                bottom.Add(new GpItem { Title = "左右同步", Value = "之后", Disabled = true, Why = Later, Lights = new[] { "LB", "RB" } });
                break;
            case "震动":
            {
                var rumble = Item("震动强度", "GripL", _gpRumble is int r ? (r == 0 ? "关" : $"{r} %") : loading, _gpRumble == null ? loading : null);
                rumble.Lights = new[] { "GripL", "GripR" };
                if (_gpRumble is int rv)
                {
                    rumble.Number = rv; rumble.Min = 0; rumble.Max = 100; rumble.StepBy = 5;
                    rumble.Format = v => v == 0 ? "关" : $"{v} %";
                    rumble.SetNumber = GpRumbleChanged;
                }
                left.Add(rumble);
                var test = Item("测试震动", "GripR", "按 A", _gpRumble == null ? loading : null);
                test.Lights = new[] { "GripL", "GripR" };
                test.Press = () => _ = GpTestRumble();
                right.Add(test);
                var nav = Toggle("导航震动", "", "hapticsEnabled");
                nav.Lights = new[] { "GripL", "GripR" };
                bottom.Add(nav);
                break;
            }
            case "特殊键":
                left.Add(KeyAction("快速设置键", "QS", "qamAction"));
                right.Add(KeyAction("Claw 键", "Claw", "msiAction"));
                var takeover = Toggle("接管（Steam 大屏幕里）", "", "enabled");
                takeover.Lights = new[] { "QS", "Claw" };
                bottom.Add(takeover);
                break;
            default:
                left.Add(Item("手柄模式", "", "手柄 · 之后", Later));
                right.Add(new GpItem { Title = "灯光", Key = "RS", Value = "之后", Disabled = true, Why = Later, Lights = new[] { "LS", "RS" } });
                bottom.Add(Item("陀螺仪", "", "之后", Later));
                break;
        }

        // 两侧按线稿竖向居中排开，下面一排在线稿下方居中
        float mid = GpY + GpH / 2;
        void Column(List<GpItem> items, int side)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Side = side;
                items[i].X = side < 0 ? GpLeftEdge : GpRightEdge;
                items[i].Y = mid - 13 + (i - (items.Count - 1) / 2f) * 84;
            }
        }
        Column(left, -1);
        Column(right, 1);
        for (int i = 0; i < bottom.Count; i++)
        {
            bottom[i].Side = 0;
            bottom[i].X = GpX + GpW / 2 - 60 + (i - (bottom.Count - 1) / 2f) * 200;
            bottom[i].Y = GpBottomY;
        }
        _gpItems.AddRange(left);
        _gpItems.AddRange(right);
        _gpItems.AddRange(bottom);
        if (_gpFocus >= _gpItems.Count) _gpFocus = 0;
    }

    /// <summary>设置项的中心（设计 px），按方位找下一个焦点用。</summary>
    private static Vector2 GpCenter(GpItem it) => it.Side switch
    {
        < 0 => new Vector2(it.X - 80, it.Y + 13),
        > 0 => new Vector2(it.X + 80, it.Y + 13),
        _ => new Vector2(it.X + 60, it.Y + 13),
    };

    // ---------------- 输入 ----------------

    private void GpMove(Direction dir)
    {
        if (_gpInSide)
        {
            if (dir is Direction.Up or Direction.Down)
            {
                int c = Math.Clamp(_gpCat + (dir == Direction.Up ? -1 : 1), 0, GpCats.Length - 1);
                if (c == _gpCat) return;
                GpSetCat(c);
            }
            else if (dir == Direction.Right) GpEnterContent();
            return;
        }
        if (_gpFocus < 0 || _gpFocus >= _gpItems.Count) return;
        var cur = _gpItems[_gpFocus];
        if (_gpEditing)
        {
            if (dir is not (Direction.Left or Direction.Right)) return;
            int d = dir == Direction.Left ? -1 : 1;
            if (cur.Choices != null)
            {
                int ni = Math.Clamp(cur.Choice + d, 0, cur.Choices.Length - 1);
                if (ni == cur.Choice) return;
                cur.SetChoice?.Invoke(ni);
            }
            else if (cur.Number is int n)
            {
                int nv = Math.Clamp(n + d * cur.StepBy, cur.Min, cur.Max);
                if (nv == n) return;
                cur.SetNumber?.Invoke(nv);
            }
            BuildGpItems();
            PaintGpBody();
            return;
        }
        // 按方位：前方 45° 扇形里最近的（横向偏离算两倍）；扇形里没有就放宽到整个半平面；往左再没有 = 回到分类
        var c0 = GpCenter(cur);
        int best = -1;
        double bestScore = double.MaxValue;
        for (int pass = 0; pass < 2 && best < 0; pass++)
            for (int i = 0; i < _gpItems.Count; i++)
            {
                if (i == _gpFocus) continue;
                var v = GpCenter(_gpItems[i]) - c0;
                float along = dir switch { Direction.Right => v.X, Direction.Left => -v.X, Direction.Down => v.Y, _ => -v.Y };
                float across = dir is Direction.Left or Direction.Right ? Math.Abs(v.Y) : Math.Abs(v.X);
                if (along <= 4 || (pass == 0 && across > along)) continue;
                double score = along + across * 2;
                if (score < bestScore) { bestScore = score; best = i; }
            }
        if (best < 0)
        {
            if (dir == Direction.Left) GpLeaveContent();
            return;
        }
        _gpFocus = best;
        GpFlipArt(_gpItems[best].Back);
        PaintGpBody();
    }

    private void GpSetCat(int cat)
    {
        _gpCat = cat;
        _gpFocus = 0;
        BuildGpItems();
        GpFlipArt(false);
        PaintGpBody();
    }

    private void GpEnterContent()
    {
        if (_gpItems.Count == 0) return;
        _gpInSide = false;
        _gpFocus = 0;
        GpFlipArt(_gpItems[0].Back);
        PaintGpBody();
    }

    private void GpLeaveContent()
    {
        _gpInSide = true;
        _gpEditing = false;
        GpFlipArt(false);
        PaintGpBody();
    }

    private void GpActivate()
    {
        if (_gpInSide) { GpEnterContent(); return; }
        if (_gpFocus < 0 || _gpFocus >= _gpItems.Count) return;
        var it = _gpItems[_gpFocus];
        if (_gpEditing) { _gpEditing = false; PaintGpBody(); return; }
        if (it.Disabled) { Toast(it.Why ?? "现在改不了"); return; }
        if (it.Press != null) { it.Press(); return; }
        if (it.Adjustable) { _gpEditing = true; PaintGpBody(); }
    }

    /// <summary>B：调节中 → 退出调节；在设置上 → 回到分类；在分类上 → 回控制中心。</summary>
    private void GpBack()
    {
        if (_gpEditing) { _gpEditing = false; PaintGpBody(); return; }
        if (!_gpInSide) { GpLeaveContent(); return; }
        CloseGamepad();
    }

    /// <summary>焦点到背键时线稿翻到背面（两张图交叉淡入淡出 180 ms），离开再翻回来。</summary>
    private void GpFlipArt(bool back)
    {
        if (back == _gpShowBack) return;
        _gpShowBack = back;
        if (Reduced) { _gpFrontArt.Opacity = back ? 0 : 1; _gpBackArt.Opacity = back ? 1 : 0; return; }
        _gpFrontArt.StartAnimation("Opacity", ToScalar(back ? 0 : 1, 180, EaseOutCss));
        _gpBackArt.StartAnimation("Opacity", ToScalar(back ? 1 : 0, 180, EaseOutCss));
    }

    // ---------------- 绘制 ----------------

    private void PaintGp()
    {
        if (_gp == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        if (_gpArtK != k && _gpFront != null && _gpBack != null)
        {
            _gpArtK = k;
            foreach (var (sprite, bmp) in new[] { (_gpFrontArt, _gpFront!), (_gpBackArt, _gpBack!) })
            {
                CcSet(sprite, CcDraw(GpW, GpH, raster, ds => ds.DrawImage(bmp, new Rect(0, 0, GpW, GpH), bmp.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic)));
                sprite.Size = new Vector2(GpW * k, GpH * k);
                sprite.Offset = new Vector3(GpX * k, GpY * k, 0);
            }
        }
        PaintGpBody();
    }

    /// <summary>整页一层（1280×800 设计 px）：标题、分类、这一类的设置、焦点连线、线稿上的高亮。</summary>
    private void PaintGpBody()
    {
        if (_gp == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        CcSet(_gpBody, CcDraw(1280, 800, raster, PaintGpBody));
        _gpBody.Size = new Vector2(1280 * k, 800 * k);
        _gpBody.Offset = Vector3.Zero;
    }

    private static Vector2 GpPoint(float x, float y) => new(GpX + x * GpS, GpY + y * GpS);

    private void PaintGpBody(CanvasDrawingSession ds)
    {
        var ui = TextKit.Ui(400);
        var accent = PerfAccent;
        var muted = Gfx.WithAlpha(CcText, 0.5);
        bool connected = _pad?.Connected ?? false;
        var snap = _gpShown;
        var focus = !_gpInSide && _gpFocus >= 0 && _gpFocus < _gpItems.Count ? _gpItems[_gpFocus] : null;

        // 标题：和控制中心的「控制中心」同一个位置、同一个字；下面一行小字是机型和连接状态
        PText(ds, "手柄", TextKit.Ui(700), 24, CcText, 40, 48, 300);
        PText(ds, connected ? "Claw 8 EX · 手柄已连接" : "Claw 8 EX · 没有检测到手柄", ui, 13, muted, 40, 82, 400);

        // 分类：只有字，当前类白字；焦点在分类上时前面加 ▸
        for (int i = 0; i < GpCats.Length; i++)
        {
            float cy = GpSideTop + i * GpSideStep;
            bool cur = i == _gpCat;
            var col = cur ? (_gpInSide ? CcText : Gfx.WithAlpha(CcText, 0.92f)) : Gfx.WithAlpha(CcText, 0.42f);
            PText(ds, GpCats[i], cur ? TextKit.Ui(600) : ui, 18, col, GpSideX + 18, cy, 200);
            if (cur && _gpInSide)
            {
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(GpSideX, cy - 6); pb.AddLine(GpSideX + 8, cy); pb.AddLine(GpSideX, cy + 6); pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(g, CcText);
            }
        }

        if (_gpFront == null) PText(ds, "正在载入线稿…", ui, 13, muted, GpX + GpW / 2 - 40, GpY + GpH / 2, 300);

        // 线稿上的实时输入：按下的键填强调色，扳机按下按深浅填肩部，摇杆推动时一个点跟着走
        foreach (var (name, key) in GpKeys)
        {
            if (key.Back != _gpShowBack || name is "LB" or "RB") continue;
            if (key.XInput != 0 && (snap.Buttons & key.XInput) != 0) GpFill(ds, name, 0.85f, accent);
        }
        if (!_gpShowBack)
        {
            float l = Math.Max((snap.Buttons & GpKeys["LB"].XInput) != 0 ? 1 : 0, snap.LT > 20 ? snap.LT / 255f : 0);
            float r = Math.Max((snap.Buttons & GpKeys["RB"].XInput) != 0 ? 1 : 0, snap.RT > 20 ? snap.RT / 255f : 0);
            if (l > 0) GpFill(ds, "LB", 0.85f * l, accent);
            if (r > 0) GpFill(ds, "RB", 0.85f * r, accent);
            foreach (var (name, ax, ay) in new[] { ("LS", snap.LX, snap.LY), ("RS", snap.RX, snap.RY) })
            {
                float fx = ax / 32767f, fy = ay / 32767f;
                if (fx * fx + fy * fy < 0.01f) continue;
                var key = GpKeys[name];
                var p = GpPoint(key.X, key.Y) + new Vector2(fx, -fy) * (key.W / 2 * GpS);
                ds.FillCircle(p, 4.5f, accent);
            }
        }

        // 焦点：这一项管的那块区域在线稿上整块填强调色（握把、肩部、背键按线稿的边，摇杆是一圈大圆盘）；
        // 两侧的设置再拉一根细线到区域边上，下面一排不连线
        if (focus != null)
        {
            foreach (var region in focus.Lights ?? (focus.Key.Length > 0 ? new[] { focus.Key } : Array.Empty<string>()))
                GpFill(ds, region, 0.4f, accent);
            if (focus.Side != 0 && focus.Key.Length > 0 && GpKeys.TryGetValue(focus.Key, out var fk) && fk.Back == _gpShowBack)
            {
                var c = GpPoint(fk.X, fk.Y);
                float half = focus.Key is "LS" or "RS" ? GpStickDisc * GpS : fk.W * GpS / 2;
                var target = new Vector2(c.X + (focus.Side < 0 ? -half : half), c.Y);
                var start = new Vector2(focus.Side < 0 ? GpLeftEdge + 14 : GpRightEdge - 14, focus.Y + 13);
                using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
                ds.DrawLine(start, target, Gfx.WithAlpha(accent, 0.9f), 1.4f, round);
                ds.FillCircle(start, 3, accent);
            }
        }

        foreach (var it in _gpItems) PaintGpItem(ds, it, it == focus);
    }

    /// <summary>摇杆高亮的大圆盘半径（原图像素，MSI 的摇杆页也是这样罩一圈）。</summary>
    private const float GpStickDisc = 44;

    /// <summary>
    /// 线稿上一块区域填强调色：握把、肩部、背键用沿线稿边界描出的矢量轮廓（Assets/Pad/regions.txt）；
    /// 摇杆是一圈大圆盘，十字键是整个圆盘，A B X Y 填四个键，其余按键填它自己的形状。
    /// </summary>
    /// <summary>读 regions.txt：每行「名字 面 x,y x,y …」（原图像素），建成舞台坐标的多边形。生成方法见 tools/PadRegions。</summary>
    private void GpLoadRegions(string path)
    {
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            var pts = parts.Skip(2).Select(p =>
            {
                var xy = p.Split(',');
                return GpPoint(float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
            }).ToArray();
            if (_gpRegions.TryGetValue(parts[0], out var old)) old.Dispose();
            _gpRegions[parts[0]] = CanvasGeometry.CreatePolygon(G.Device, pts);
        }
    }

    private void GpFill(CanvasDrawingSession ds, string name, float alpha, Color accent)
    {
        if (!GpKeys.TryGetValue(name, out var key) || key.Back != _gpShowBack) return;
        if (_gpRegions.TryGetValue(name, out var region)) { ds.FillGeometry(region, Gfx.WithAlpha(accent, alpha)); return; }
        var c = GpPoint(key.X, key.Y);
        switch (name)
        {
            case "LS" or "RS":
                ds.FillCircle(c, GpStickDisc * GpS, Gfx.WithAlpha(accent, alpha * 0.8f));
                ds.DrawCircle(c, GpStickDisc * GpS, Gfx.WithAlpha(accent, Math.Min(1, alpha + 0.3f)), 1.2f);
                break;
            case "ABXY":
                foreach (var b in new[] { "A", "B", "X", "Y" }) GpFill(ds, b, alpha, accent);
                break;
            default:
                ds.FillRoundedRectangle(GpRect(key, 0), key.R * GpS, key.R * GpS, Gfx.WithAlpha(accent, alpha));
                break;
        }
    }

    private static Rect GpRect(GpKey key, float grow)
    {
        var c = GpPoint(key.X, key.Y);
        float w = key.W * GpS + grow * 2, h = key.H * GpS + grow * 2;
        return new Rect(c.X - w / 2, c.Y - h / 2, w, h);
    }

    /// <summary>一个设置：上面小字标题，下面值；焦点白字（下面一排另加 ▸），调节中值两边是 ‹ ›，灰的写「之后」。</summary>
    private void PaintGpItem(CanvasDrawingSession ds, GpItem it, bool focused)
    {
        var ui = TextKit.Ui(400);
        float a = it.Disabled ? 0.38f : 1f;
        var titleCol = Gfx.WithAlpha(CcText, (focused ? 0.72f : 0.45f) * (it.Disabled ? 0.8f : 1f));
        var valueCol = Gfx.WithAlpha(CcText, (focused ? 1f : 0.7f) * a);
        bool editing = focused && _gpEditing;
        string value = editing && it.Choices != null ? it.Choices[it.Choice]
            : editing && it.Number is int n && it.Format != null ? it.Format(n) : it.Value;
        var valueFont = editing ? TextKit.Ui(600) : ui;
        float tw = PWidth(ds, it.Title, ui, 13), vw = PWidth(ds, value, valueFont, 18);
        float chev = editing ? 22 : 0;
        // 左边右对齐，右边和下面一排左对齐
        float tx = it.Side < 0 ? it.X - tw : it.X, vx = it.Side < 0 ? it.X - vw - chev : it.X + chev;
        PText(ds, it.Title, ui, 13, titleCol, tx, it.Y, tw + 4);
        PText(ds, value, valueFont, 18, valueCol, vx, it.Y + 26, vw + 4);
        if (editing)
        {
            DrawChevron(ds, vx - 16, it.Y + 26, true, valueCol);
            DrawChevron(ds, vx + vw + 16, it.Y + 26, false, valueCol);
            string hint = "← → 改  ·  A 完成";
            float hw = PWidth(ds, hint, ui, 11);
            PText(ds, hint, ui, 11, Gfx.WithAlpha(CcText, 0.45f), it.Side < 0 ? it.X - hw : it.X, it.Y + 50, hw + 4);
        }
        if (focused && it.Side == 0)
        {
            using var pb = new CanvasPathBuilder(ds);
            float cy = it.Y + 26;
            pb.BeginFigure(it.X - 16, cy - 6); pb.AddLine(it.X - 8, cy); pb.AddLine(it.X - 16, cy + 6); pb.EndFigure(CanvasFigureLoop.Closed);
            using var g = CanvasGeometry.CreatePath(pb);
            ds.FillGeometry(g, CcText);
        }
    }
}
