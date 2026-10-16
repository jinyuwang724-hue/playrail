// 控制中心 → 手柄：左边一列轻量的分类（只有字，竖向居中），右边那块是 Claw 8 EX 线稿，这一类的设置挂在线稿两侧。
// 线稿（Assets/Pad，来自 MSI Center M 里 1T91 机型的图）：正面高清 900×412、背面 700×310、左右扳机侧视 420×414；
// 线稿后面压一圈柔和的暗光把视线收到中间。焦点所在的设置拉一根细线到它管的键上，那块区域整块染强调色（矢量轮廓，抗锯齿）。
// 按下的键实时点亮、摇杆推动时点跟着走、扳机按多深填多少。
// 手柄：分类上 ↑ ↓ 换类，→ / A 进到设置；设置之间十字键按方位移动，最左再 ← 或 B 回分类；分类上 B 回控制中心。
// 按键映射：焦点直接在线稿的键上走，A 打开第三级「映射到」（WallStage.GamepadPicker.cs）。
// 各分类的内容在 WallStage.GamepadPages.cs。读：工具箱后台 get_gamepad_state（固件只读）、get_state、get_rumble_state；
// 写：震动强度、特殊键已接上；固件写入（模式、映射、死区、扳机、灯光）等后台加上对应方法后自动生效，没有时提示。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Rect = Windows.Foundation.Rect;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // ---- 几何（设计 px，1280×800 舞台） ----
    // 正面 900×412 放到 760 宽，中心在分类右边那块（220…1240）的正中、舞台竖向中线
    private const float GpFrontW = 900, GpFrontH = 412, GpW = 760, GpS = GpW / GpFrontW, GpH = GpFrontH * GpS;
    private const float GpCx = 730, GpCy = 412, GpX = GpCx - GpW / 2, GpY = GpCy - GpH / 2;
    // 扳机侧视图两张并排，各 380 宽；两张图往中间那头都是渐隐的机身线，互相压一点让两半挨在一起
    private const float GpSideS = 380f / 420, GpSideW = 380, GpSideH = 414 * GpSideS, GpTlX = GpCx + 20 - GpSideW, GpTrX = GpCx - 20, GpSideY = GpCy - GpSideH / 2;
    // 两侧设置的内边（左边右对齐、右边左对齐），行距
    private const float GpLeftEdge = GpX - 20, GpRightEdge = GpX + GpW + 20, GpRowStep = 92;
    // 分类：x 40，行高 44，分组标题 34
    private const float GpSideX = 40, GpCatH = 44, GpGroupH = 34;
    /// <summary>摇杆高亮的大圆盘半径（正面原图像素，MSI 的摇杆页也是罩一圈）。</summary>
    private const float GpStickDisc = 56;

    /// <summary>线稿上一个键：所在的面（front / back / tl / tr）、中心、宽高、圆角（原图像素）；Region = 填哪块矢量区域（没有就按形状）；XInput 位用于实时点亮。</summary>
    private readonly record struct GpKey(string Face, float X, float Y, float W, float H, float R, string? Region = null, ushort XInput = 0);

    private static readonly Dictionary<string, GpKey> GpKeys = new()
    {
        ["A"] = new("front", 803, 118, 30, 30, 15, null, 0x1000), ["B"] = new("front", 831, 91, 30, 30, 15, null, 0x2000),
        ["X"] = new("front", 776, 91, 30, 30, 15, null, 0x4000), ["Y"] = new("front", 803, 64, 30, 30, 15, null, 0x8000),
        ["ABXY"] = new("front", 803, 91, 92, 92, 46),
        ["LS"] = new("front", 85, 97, 70, 70, 35, null, 0x0040), ["RS"] = new("front", 749, 158, 70, 70, 35, null, 0x0080),
        ["Dpad"] = new("front", 144, 157, 74, 74, 37),
        ["Up"] = new("front", 144, 135, 19, 18, 4, null, 0x0001), ["Down"] = new("front", 144, 178, 19, 18, 4, null, 0x0002),
        ["Left"] = new("front", 122, 157, 18, 19, 4, null, 0x0004), ["Right"] = new("front", 166, 157, 18, 19, 4, null, 0x0008),
        ["View"] = new("front", 170, 61, 12, 22, 6, null, 0x0020), ["QS"] = new("front", 170, 89, 12, 22, 6),
        ["Menu"] = new("front", 726, 60, 12, 22, 6, null, 0x0010), ["Claw"] = new("front", 726, 90, 12, 22, 6),
        ["LB"] = new("front", 126, 36, 64, 22, 10, "LB", 0x0100), ["RB"] = new("front", 774, 36, 64, 22, 10, "RB", 0x0200),
        ["LT"] = new("front", 78, 24, 56, 22, 10, "LB"), ["RT"] = new("front", 822, 24, 56, 22, 10, "RB"),
        ["GripL"] = new("front", 70, 300, 130, 150, 40, "GripL"), ["GripR"] = new("front", 830, 300, 130, 150, 40, "GripR"),
        ["M1"] = new("back", 120, 136, 28, 46, 8, "M1"), ["M2"] = new("back", 578, 136, 28, 46, 8, "M2"),
        ["TL"] = new("tl", 105, 280, 90, 150, 20, "TL"), ["BL"] = new("tl", 75, 160, 80, 70, 20, "BL"),
        ["TR"] = new("tr", 315, 280, 90, 150, 20, "TR"), ["BR"] = new("tr", 345, 160, 80, 70, 20, "BR"),
    };

    /// <summary>某个面的原图像素 → 舞台坐标。背面和正面同一比例：front = back × 1.3 − (5, 10)。</summary>
    private static Vector2 GpMap(string face, float x, float y) => face switch
    {
        "back" => new(GpX + (x * 1.3f - 5) * GpS, GpY + (y * 1.3f - 10) * GpS),
        "tl" => new(GpTlX + x * GpSideS, GpSideY + y * GpSideS),
        "tr" => new(GpTrX + x * GpSideS, GpSideY + y * GpSideS),
        _ => new(GpX + x * GpS, GpY + y * GpS),
    };

    private static float GpFaceScale(string face) => face switch { "back" => GpS * 1.3f, "tl" or "tr" => GpSideS, _ => GpS };

    private static Rect GpKeyRect(GpKey key, float grow = 0)
    {
        var c = GpMap(key.Face, key.X, key.Y);
        float s = GpFaceScale(key.Face), w = key.W * s + grow * 2, h = key.H * s + grow * 2;
        return new Rect(c.X - w / 2, c.Y - h / 2, w, h);
    }

    /// <summary>
    /// 一个设置。Side -1 = 线稿左边（右对齐到 GpLeftEdge），1 = 右边；Row 是那一侧的第几行（两侧各自竖向居中）。
    /// Hotspot = 按键映射页：焦点在线稿的键上走，只给焦点那个键画说明。
    /// </summary>
    private sealed class GpItem
    {
        public string Title = "", Value = "";
        public int Side, Row;
        public float X, Y;
        public string Key = "";
        public string[]? Lights;
        public bool Hotspot, Disabled;
        public string? Why;
        public string[]? Choices;
        public int Choice;
        public Action<int>? SetChoice;
        public int? Number;
        public int Min, Max = 100, StepBy = 1;
        public Func<int, string>? Format;
        public Action<int>? SetNumber;
        public Action? Press;
        /// <summary>颜色选项（灯光颜色）：值前面画一个色块。</summary>
        public Color[]? Swatches;
        public bool Adjustable => !Disabled && (Choices != null || Number != null);
    }

    private ContainerVisual? _gp;
    private SpriteVisual _gpSpot = null!, _gpBody = null!;
    private readonly Dictionary<string, SpriteVisual> _gpArt = new();
    private readonly Dictionary<string, CanvasBitmap> _gpBitmaps = new();
    private readonly Dictionary<string, CanvasGeometry> _gpRegions = new();
    private readonly HashSet<string> _gpFacesShown = new() { "front" };
    private bool _gpOpen, _gpBusy, _gpEditing, _gpInSide = true, _gpArtLoaded;
    private int _gpCat, _gpFocus, _gpVersion, _gpRumbleEdits;
    private float _gpArtK;
    private readonly List<GpItem> _gpItems = new();
    private JsonObject? _gpSettings;
    private JsonNode? _gpFw;
    /// <summary>软件输入层：全局布局（get_input_layout 的 layout）和开关 / 状态（get_input_layer_state）。后台没有输入层时都是 null。</summary>
    private JsonObject? _gpLayout;
    private JsonNode? _gpLayer;
    /// <summary>左右马达的震动强度（0–100），读自 get_rumble_state 的 current。</summary>
    private int? _gpRumbleL, _gpRumbleR;
    private string? _gpError, _gpFwError;
    private DispatcherQueueTimer? _gpLive;
    private Input.GamepadPoller.Snapshot _gpShown;

    // ---------------- 开 / 关 ----------------

    private void OpenGamepad()
    {
        if (_cc == null) return;
        EnsureGpVisuals();
        _gpOpen = true;
        _gpEditing = false;
        _gpPicker = null;
        ++_gpVersion;
        _gpInSide = true;
        _gpFocus = 0;
        BuildGpItems();
        GpShowFaces(GpPageFaces(), instant: true);
        _gpShown = _pad?.Last ?? default;
        PaintGp();
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        _gp!.IsVisible = true;
        _gp.Opacity = 0;
        _gp.StartAnimation("Opacity", ToScalar(1, 240, EaseOutCss, 80));
        _gpLive ??= MakeGpLiveTimer();
        if (_pad != null) _pad.StickNavigates = false;   // 手柄页只用十字键挪焦点，左摇杆留给实时输入
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
        _gpPicker = null;
        _gpLive?.Stop();
        if (_pad != null) _pad.StickNavigates = true;
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
        _gpSpot = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _gp.Children.InsertAtTop(_gpSpot);
        _gpArtLayer = G.Container();
        _gp.Children.InsertAtTop(_gpArtLayer);
        foreach (var face in new[] { "front", "back", "tl", "tr" })
        {
            var s = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            s.Opacity = face == "front" ? 1 : 0;
            _gpArt[face] = s;
            _gpArtLayer.Children.InsertAtTop(s);
        }
        EnsureGpFx();
        _gpBody = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _gp.Children.InsertAtTop(_gpBody);
        _cc!.Children.InsertAtTop(_gp);
    }

    /// <summary>实时输入和波纹动画：30 Hz 看一眼，手柄状态变了或波纹还在走才重画。</summary>
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
        if (_gpArtLoaded) return;
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Pad");
            foreach (var (face, file) in new[] { ("front", "claw8ex-front-hd"), ("back", "claw8ex-back"), ("tl", "claw8ex-trigger-l"), ("tr", "claw8ex-trigger-r") })
                _gpBitmaps[face] = await CanvasBitmap.LoadAsync(G.Device, Path.Combine(dir, file + ".png"));
            GpLoadRegions(Path.Combine(dir, "regions.txt"));
            _gpArtLoaded = true;
            _gpArtK = 0;
            if (_gpOpen) PaintGp();
        }
        catch (Exception ex) { Log.Warn($"gamepad art: {ex.Message}"); }
    }

    /// <summary>读 regions.txt：每行「名字 面 x,y x,y …」（该面原图像素），建成舞台坐标的多边形。生成方法见 tools/PadRegions。</summary>
    private void GpLoadRegions(string path)
    {
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            string face = parts[1];
            var pts = parts.Skip(2).Select(p =>
            {
                var xy = p.Split(',');
                return GpMap(face, float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
            }).ToArray();
            if (_gpRegions.TryGetValue(parts[0], out var old)) old.Dispose();
            _gpRegions[parts[0]] = CanvasGeometry.CreatePolygon(G.Device, pts);
        }
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
            if (_gpRumbleEdits == 0 && rumble != null)
            {
                int st = Int(rumble["strength"], -1);
                _gpRumbleL = rumble["current"] is JsonArray cur && cur.Count == 2 ? Int(cur[0]) : st >= 0 ? st : null;
                _gpRumbleR = rumble["current"] is JsonArray cur2 && cur2.Count == 2 ? Int(cur2[1]) : st >= 0 ? st : null;
            }
            _gpError = null;
            try
            {
                var fw = await _agent.Call("get_gamepad_state");
                _gpFw = Bool(fw?["available"]) ? fw : null;
                _gpFwError = _gpFw == null ? Str(fw?["error"]) ?? "读不到手柄固件" : null;
            }
            catch (Exception ex) { _gpFw = null; _gpFwError = ex.Message.Contains("不支持") ? "后台还没有固件读取" : ex.Message; }
            try
            {
                _gpLayer = await _agent.Call("get_input_layer_state");
                if (_gpLayoutEdits == 0 && (await _agent.Call("get_input_layout"))?["layout"] is JsonObject lay) _gpLayout = (JsonObject)lay.DeepClone();
            }
            catch { _gpLayer = null; _gpLayout = null; }
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

    /// <summary>
    /// 写手柄固件的一项。后台还没有这个方法（固件写入要等你确认后才上线）时提示一句，并重新读一遍把界面改回真实值。
    /// </summary>
    private async Task GpWrite(string method, params JsonNode?[] args)
    {
        try
        {
            _agent ??= new AgentClient();
            await _agent.Call(method, args);
        }
        catch (Exception ex)
        {
            Toast(ex.Message.Contains("不支持") ? "这一项还没接上：后台的手柄固件写入还没上线" : $"没有写进手柄：{ex.Message}");
        }
        _ = GpRefresh();
    }

    /// <summary>
    /// 震动强度（左右分开，照 MSI）：连按 ← → 时 400 ms 内只写一次固件（写一次要来回好几包 HID），写完震一下让你感受到。
    /// 后台还只认一个强度时（左右分开写的接口还没上线），左右相同就按一个强度写，不同就提示。
    /// </summary>
    private void GpRumbleChanged(int left, int right)
    {
        _gpRumbleL = left;
        _gpRumbleR = right;
        int edits = ++_gpRumbleEdits;
        After(400, async () =>
        {
            if (edits != _gpRumbleEdits || _agent == null) return;
            try
            {
                try { await _agent.Call("set_rumble_strength", JsonValue.Create(left), JsonValue.Create(right)); }
                catch (Exception ex) when (left == right && !ex.Message.Contains("连接"))
                {
                    await _agent.Call("set_rumble_strength", JsonValue.Create(left));
                }
                if (left + right > 0) await GpTestRumble();
            }
            catch (Exception ex) { Toast(left != right ? "左右分开调还没接上：后台的震动接口还只认一个强度" : $"震动强度没有写进手柄：{ex.Message}"); }
            if (edits == _gpRumbleEdits) { _gpRumbleEdits = 0; _ = GpRefresh(); }
        });
    }

    private async Task GpTestRumble()
    {
        GpPlayRumble((_gpRumbleL ?? 100) / 100f, (_gpRumbleR ?? 100) / 100f);
        if (_agent == null) return;
        try { await _agent.Call("test_rumble"); }
        catch (Exception ex) { Toast($"没有震起来：{ex.Message}"); }
    }

    // ---------------- 分类 ----------------

    /// <summary>左边分类：Group = 分组小标题（不能选）。</summary>
    private static readonly (string Id, string Title, bool Group)[] GpSidebar =
    {
        ("mode", "当前模式", false),
        ("", "手柄模式", true), ("gpmap", "按键映射", false), ("stick", "摇杆", false), ("trigger", "扳机", false), ("rumble", "震动", false),
        ("", "桌面模式", true), ("dkmap", "按键映射", false), ("mouse", "鼠标", false),
        ("", "其他", true), ("special", "特殊键", false), ("light", "灯光", false),
    };

    private static readonly int[] GpCatRows = Enumerable.Range(0, GpSidebar.Length).Where(i => !GpSidebar[i].Group).ToArray();
    private string GpCatId => GpSidebar[GpCatRows[_gpCat]].Id;

    /// <summary>分类列表竖向居中（舞台中线）：返回第 row 行的中线 y。</summary>
    private static float GpSidebarY(int row)
    {
        float total = GpSidebar.Sum(e => e.Group ? GpGroupH : GpCatH), y = GpCy - total / 2;
        for (int i = 0; i < row; i++) y += GpSidebar[i].Group ? GpGroupH : GpCatH;
        return y + (GpSidebar[row].Group ? GpGroupH - 10 : GpCatH / 2);
    }

    /// <summary>这一页用哪几张线稿：扳机页是两张侧视图，其余是正面（焦点到背键时翻背面）。</summary>
    private HashSet<string> GpPageFaces()
    {
        if (GpCatId == "trigger") return new() { "tl", "tr" };
        var it = !_gpInSide && _gpFocus >= 0 && _gpFocus < _gpItems.Count ? _gpItems[_gpFocus] : null;
        return it != null && GpKeys.TryGetValue(it.Key, out var k) && k.Face == "back" ? new() { "back" } : new() { "front" };
    }

    /// <summary>换线稿：要显示的面淡入、其余淡出（180 ms）。</summary>
    private void GpShowFaces(HashSet<string> faces, bool instant = false)
    {
        if (faces.SetEquals(_gpFacesShown) && !instant) return;
        _gpFacesShown.Clear();
        _gpFacesShown.UnionWith(faces);
        foreach (var (face, sprite) in _gpArt)
        {
            float to = faces.Contains(face) ? 1 : 0;
            if (instant || Reduced) { sprite.StopAnimation("Opacity"); sprite.Opacity = to; }
            else sprite.StartAnimation("Opacity", ToScalar(to, 180, EaseOutCss));
        }
    }

    // ---------------- 输入 ----------------

    private void GpMove(Direction dir)
    {
        if (_gpPicker != null) { GpPickerMove(dir); return; }
        if (_gpInSide)
        {
            if (dir is Direction.Up or Direction.Down)
            {
                int c = Math.Clamp(_gpCat + (dir == Direction.Up ? -1 : 1), 0, GpCatRows.Length - 1);
                if (c == _gpCat) return;
                _gpCat = c;
                _gpFocus = 0;
                BuildGpItems();
                GpShowFaces(GpPageFaces());
                PaintGpBody();
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
        GpShowFaces(GpPageFaces());
        PaintGpBody();
    }

    /// <summary>
    /// 设置项的中心（设计 px），按方位找下一个焦点用：映射页是键在线稿上的位置（背键按它在握把后面的位置算），
    /// 其余是说明文字那块。
    /// </summary>
    private static Vector2 GpCenter(GpItem it)
    {
        if (it.Hotspot && GpKeys.TryGetValue(it.Key, out var k))
            return k.Face == "back" ? GpMap("front", k.X < 350 ? 70 : 830, 290) : GpMap(k.Face, k.X, k.Y);
        return new Vector2(it.Side < 0 ? it.X - 80 : it.X + 80, it.Y + 13);
    }

    private void GpEnterContent()
    {
        if (_gpItems.Count == 0) return;
        _gpInSide = false;
        _gpFocus = Math.Clamp(_gpFocus, 0, _gpItems.Count - 1);
        GpShowFaces(GpPageFaces());
        PaintGpBody();
    }

    private void GpLeaveContent()
    {
        _gpInSide = true;
        _gpEditing = false;
        GpShowFaces(GpPageFaces());
        PaintGpBody();
    }

    private void GpActivate()
    {
        if (_gpPicker != null) { GpPickerActivate(); return; }
        if (_gpInSide) { GpEnterContent(); return; }
        if (_gpFocus < 0 || _gpFocus >= _gpItems.Count) return;
        var it = _gpItems[_gpFocus];
        if (_gpEditing) { _gpEditing = false; PaintGpBody(); return; }
        if (it.Disabled) { Toast(it.Why ?? "现在改不了"); return; }
        if (it.Press != null) { it.Press(); return; }
        if (it.Adjustable) { _gpEditing = true; PaintGpBody(); }
    }

    /// <summary>B：映射选择 → 关掉它；调节中 → 退出调节；在设置上 → 回到分类；在分类上 → 回控制中心。</summary>
    private void GpBack()
    {
        if (_gpPicker != null) { GpPickerBack(); return; }
        if (_gpEditing) { _gpEditing = false; PaintGpBody(); return; }
        if (!_gpInSide) { GpLeaveContent(); return; }
        CloseGamepad();
    }

    // ---------------- 绘制 ----------------

    private void PaintGp()
    {
        if (_gp == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        if (_gpArtK != k)
        {
            // 线稿后面的暗光：一圈椭圆径向渐变，中间压暗、边缘透明，把视线收到线稿上
            float sw = GpW * 1.5f, sh = GpH * 2.1f;
            CcSet(_gpSpot, CcDraw(sw, sh, raster, ds =>
            {
                using var brush = new CanvasRadialGradientBrush(ds, new[]
                {
                    new CanvasGradientStop { Position = 0, Color = Gfx.Rgba(0, 0, 0, 0.62) },
                    new CanvasGradientStop { Position = 0.55f, Color = Gfx.Rgba(0, 0, 0, 0.36) },
                    new CanvasGradientStop { Position = 1, Color = Gfx.Rgba(0, 0, 0, 0) },
                }) { Center = new Vector2(sw / 2, sh / 2), RadiusX = sw / 2, RadiusY = sh / 2 };
                ds.FillRectangle(0, 0, sw, sh, brush);
            }));
            _gpSpot.Size = new Vector2(sw * k, sh * k);
            _gpSpot.Offset = new Vector3((GpCx - sw / 2) * k, (GpCy - sh / 2) * k, 0);
            if (_gpArtLoaded)
            {
                _gpArtK = k;
                foreach (var (face, sprite) in _gpArt)
                {
                    var bmp = _gpBitmaps[face];
                    var tl = GpMap(face, 0, 0);
                    float w = (float)bmp.Size.Width * GpFaceScale(face), h = (float)bmp.Size.Height * GpFaceScale(face);
                    CcSet(sprite, CcDraw(w, h, raster, ds => ds.DrawImage(bmp, new Rect(0, 0, w, h), bmp.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic)));
                    sprite.Size = new Vector2(w * k, h * k);
                    sprite.Offset = new Vector3(tl.X * k, tl.Y * k, 0);
                }
                PaintGpFx(k, raster);
            }
        }
        PaintGpBody();
    }

    /// <summary>整页一层（1280×800 设计 px）：标题、分类、设置、连线、线稿上的高亮和实时输入；映射选择打开时画它。</summary>
    private void PaintGpBody()
    {
        if (_gp == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        CcSet(_gpBody, CcDraw(1280, 800, raster, PaintGpBody));
        _gpBody.Size = new Vector2(1280 * k, 800 * k);
        _gpBody.Offset = Vector3.Zero;
        // 映射选择盖在线稿上时把线稿和暗光压下去
        float artAlpha = _gpPicker != null ? 0f : 1f;
        if (_gpSpot.Opacity != artAlpha)
        {
            _gpSpot.Opacity = artAlpha;
            foreach (var (face, s) in _gpArt) if (_gpFacesShown.Contains(face)) { s.StopAnimation("Opacity"); s.Opacity = artAlpha; }
        }
    }

    private void PaintGpBody(CanvasDrawingSession ds)
    {
        var ui = TextKit.Ui(400);
        var accent = PerfAccent;
        var muted = Gfx.WithAlpha(CcText, 0.5);
        bool connected = _pad?.Connected ?? false;

        // 标题：和控制中心的「控制中心」同一个位置、同一个字；下面一行小字是机型、连接状态和当前模式
        PText(ds, "手柄", TextKit.Ui(700), 24, CcText, 40, 48, 300);
        string mode = Str(_gpFw?["mode"]) switch { "desktop" => "桌面模式", "xinput" => "手柄模式", "dinput" => "DInput 模式", _ => "" };
        string sub = connected ? $"Claw 8 EX · 已连接{(mode.Length > 0 ? " · " + mode : "")}" : "Claw 8 EX · 没有检测到手柄";
        PText(ds, sub, ui, 13, muted, 40, 82, 600);

        PaintGpSidebar(ds);
        if (_gpPicker != null) { PaintGpPicker(ds, accent); return; }
        if (!_gpArtLoaded) PText(ds, "正在载入线稿…", ui, 13, muted, GpCx - 40, GpCy, 300);

        var focus = !_gpInSide && _gpFocus >= 0 && _gpFocus < _gpItems.Count ? _gpItems[_gpFocus] : null;
        PaintGpPageArt(ds, accent, focus);
        PaintGpLive(ds, accent);

        // 焦点：这一项管的区域整块填强调色，再拉一根细线从设置连到区域边上
        if (focus != null)
        {
            foreach (var region in focus.Lights ?? (focus.Key.Length > 0 ? new[] { focus.Key } : Array.Empty<string>()))
                GpFill(ds, region, 0.42f, accent);
            if (focus.Key.Length > 0 && GpKeys.TryGetValue(focus.Key, out var fk) && _gpFacesShown.Contains(fk.Face))
            {
                var r = GpKeyRect(fk);
                var c = GpMap(fk.Face, fk.X, fk.Y);
                bool left = focus.Side < 0;
                float half = fk.Region == null && focus.Key is "LS" or "RS" ? GpStickDisc * GpS : (float)r.Width / 2;
                var target = new Vector2(c.X + (left ? -half : half), c.Y);
                var start = new Vector2(left ? GpLeftEdge + 14 : GpRightEdge - 14, focus.Y + 13);
                using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
                ds.DrawLine(start, target, Gfx.WithAlpha(accent, 0.9f), 1.4f, round);
                ds.FillCircle(start, 3, accent);
            }
        }

        foreach (var it in _gpItems)
            if (!it.Hotspot || it == focus) PaintGpItem(ds, it, it == focus);
        if (focus == null && _gpItems.Count > 0 && _gpItems[0].Hotspot)
        {
            const string hint = "按 → 进去，在线稿上选一个键，A 改它的映射";
            float hw = PWidth(ds, hint, ui, 13);
            PText(ds, hint, ui, 13, muted, GpCx - hw / 2, GpY + GpH + 44, hw + 4);
        }
    }

    /// <summary>分类：竖向居中，只有字；分组小标题小一号、更淡；当前类白字，焦点在分类上时前面加 ▸。</summary>
    private void PaintGpSidebar(CanvasDrawingSession ds)
    {
        var ui = TextKit.Ui(400);
        int cat = 0;
        for (int i = 0; i < GpSidebar.Length; i++)
        {
            var e = GpSidebar[i];
            float cy = GpSidebarY(i);
            if (e.Group) { PText(ds, e.Title, TextKit.Ui(500), 12, Gfx.WithAlpha(CcText, 0.36f), GpSideX, cy, 200); continue; }
            bool cur = cat++ == _gpCat;
            var col = cur ? (_gpInSide ? CcText : Gfx.WithAlpha(CcText, 0.92f)) : Gfx.WithAlpha(CcText, 0.45f);
            PText(ds, e.Title, cur ? TextKit.Ui(600) : ui, 17, col, GpSideX + 16, cy, 200);
            if (cur && _gpInSide && _gpPicker == null)
            {
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(GpSideX, cy - 6); pb.AddLine(GpSideX + 8, cy); pb.AddLine(GpSideX, cy + 6); pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(g, CcText);
            }
        }
    }

    /// <summary>线稿上的实时输入：按下的键填强调色，扳机按多深填多少，摇杆推动时一个点跟着走。</summary>
    private void PaintGpLive(CanvasDrawingSession ds, Color accent)
    {
        var snap = _gpShown;
        foreach (var (name, key) in GpKeys)
            if (key.XInput != 0 && name is not ("LB" or "RB") && (snap.Buttons & key.XInput) != 0) GpFill(ds, name, 0.85f, accent);
        if (_gpFacesShown.Contains("front"))
        {
            float lb = Math.Max((snap.Buttons & 0x0100) != 0 ? 1 : 0, snap.LT > 20 ? snap.LT / 255f : 0);
            float rb = Math.Max((snap.Buttons & 0x0200) != 0 ? 1 : 0, snap.RT > 20 ? snap.RT / 255f : 0);
            if (lb > 0) GpFill(ds, "LB", 0.85f * lb, accent);
            if (rb > 0) GpFill(ds, "RB", 0.85f * rb, accent);
            foreach (var (name, ax, ay) in new[] { ("LS", snap.LX, snap.LY), ("RS", snap.RX, snap.RY) })
            {
                float fx = ax / 32767f, fy = ay / 32767f;
                if (fx * fx + fy * fy < 0.01f) continue;
                var key = GpKeys[name];
                var p = GpMap("front", key.X, key.Y) + new Vector2(fx, -fy) * (key.W / 2 * GpS);
                ds.FillCircle(p, 5f, accent);
            }
        }
        if (_gpFacesShown.Contains("tl"))
        {
            // 侧视图：扳机按多深，扳机那块从上往下填多少；肩键按下整块填
            GpFillPartial(ds, "TL", snap.LT / 255f, accent);
            GpFillPartial(ds, "TR", snap.RT / 255f, accent);
            if ((snap.Buttons & 0x0100) != 0) GpFill(ds, "BL", 0.85f, accent);
            if ((snap.Buttons & 0x0200) != 0) GpFill(ds, "BR", 0.85f, accent);
        }
    }

    /// <summary>
    /// 线稿上一块填强调色：有矢量区域（握把、肩部、背键、扳机侧视）就按轮廓填；摇杆是一圈大圆盘，十字键是整个圆盘，
    /// A B X Y 填四个键，其余按键填它自己的形状。看不见的面上的键不画。
    /// </summary>
    private void GpFill(CanvasDrawingSession ds, string name, float alpha, Color accent)
    {
        if (!GpKeys.TryGetValue(name, out var key) || !_gpFacesShown.Contains(key.Face)) return;
        if (key.Region != null && _gpRegions.TryGetValue(key.Region, out var region)) { ds.FillGeometry(region, Gfx.WithAlpha(accent, alpha)); return; }
        var c = GpMap(key.Face, key.X, key.Y);
        switch (name)
        {
            case "LS" or "RS":
                ds.FillCircle(c, GpStickDisc * GpS, Gfx.WithAlpha(accent, alpha * 0.75f));
                ds.DrawCircle(c, GpStickDisc * GpS, Gfx.WithAlpha(accent, Math.Min(1, alpha + 0.3f)), 1.2f);
                break;
            case "ABXY":
                foreach (var b in new[] { "A", "B", "X", "Y" }) GpFill(ds, b, alpha, accent);
                break;
            default:
                float s = GpFaceScale(key.Face);
                ds.FillRoundedRectangle(GpKeyRect(key), key.R * s, key.R * s, Gfx.WithAlpha(accent, alpha));
                break;
        }
    }

    /// <summary>区域从上往下填 fraction 那么多（扳机按下的深度）。</summary>
    private void GpFillPartial(CanvasDrawingSession ds, string name, float fraction, Color accent)
    {
        if (fraction < 0.03f || !_gpRegions.TryGetValue(name, out var region)) return;
        var b = region.ComputeBounds();
        using var clip = CanvasGeometry.CreateRectangle(ds, (float)b.X, (float)b.Y, (float)b.Width, (float)(b.Height * fraction));
        using var part = region.CombineWith(clip, Matrix3x2.Identity, CanvasGeometryCombine.Intersect);
        ds.FillGeometry(part, Gfx.WithAlpha(accent, 0.8f));
    }

    /// <summary>一个设置：上面小字标题，下面值；焦点白字，调节中值两边是 ‹ ›，置灰的写原因；灯光颜色前面带色块。</summary>
    private void PaintGpItem(CanvasDrawingSession ds, GpItem it, bool focused)
    {
        var ui = TextKit.Ui(400);
        float a = it.Disabled ? 0.4f : 1f;
        var titleCol = Gfx.WithAlpha(CcText, (focused ? 0.75f : 0.45f) * (it.Disabled ? 0.85f : 1f));
        var valueCol = Gfx.WithAlpha(CcText, (focused ? 1f : 0.66f) * a);
        bool editing = focused && _gpEditing;
        string value = editing && it.Choices != null ? it.Choices[it.Choice]
            : editing && it.Number is int n && it.Format != null ? it.Format(n) : it.Value;
        var valueFont = editing ? TextKit.Ui(600) : ui;
        float tw = PWidth(ds, it.Title, ui, 13), vw = PWidth(ds, value, valueFont, 19);
        float swatch = it.Swatches != null ? 20 : 0;
        float chev = editing ? 22 : 0;
        bool left = it.Side < 0;
        float tx = left ? it.X - tw : it.X, vx = left ? it.X - vw - chev : it.X + chev + swatch;
        PText(ds, it.Title, ui, 13, titleCol, tx, it.Y, tw + 4);
        PText(ds, value, valueFont, 19, valueCol, vx, it.Y + 27, vw + 4);
        if (it.Swatches != null)
            ds.FillCircle(vx - 12, it.Y + 27, 6, Gfx.WithAlpha(it.Swatches[Math.Clamp(it.Choice, 0, it.Swatches.Length - 1)], a));
        if (editing)
        {
            DrawChevron(ds, vx - 16 - swatch, it.Y + 27, true, valueCol);
            DrawChevron(ds, vx + vw + 16, it.Y + 27, false, valueCol);
            const string hint = "← → 改  ·  A 完成";
            float hw = PWidth(ds, hint, ui, 11);
            PText(ds, hint, ui, 11, Gfx.WithAlpha(CcText, 0.45f), left ? it.X - hw : it.X, it.Y + 52, hw + 4);
        }
    }
}
