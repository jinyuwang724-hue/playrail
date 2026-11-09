// 控制中心 → 手柄：左边一列轻量的分类（只有字，竖向居中），右边那块是 Claw 8 EX 线稿，这一类的设置挂在线稿两侧。
// 线稿（Assets/Pad，来自 MSI Center M 里 1T91 机型的图）：正面高清 900×412、背面 700×310、左右扳机侧视 420×414；
// 线稿后面压一圈柔和的暗光把视线收到中间。焦点所在的设置拉一根细线到它管的键上，那块区域整块染强调色（矢量轮廓，抗锯齿）。
// 按键映射页：线稿照常大小，每个键映射到什么紧凑地列在线稿两侧（按手柄上的位置从上到下），A 打开「映射到」（WallStage.GamepadPicker.cs）。
// 实时输入只点亮这一页管的东西：映射页亮按键、摇杆页亮摇杆、扳机页亮扳机；扳机只在侧视图上亮（正面肩部那块只归肩键）。
// 手柄 / 桌面模式：游戏库在前台时一律是手柄模式（桌面模式下这里没法操作），离开前台再切回桌面模式。
// 切模式的入口是每页线稿上方居中的「模式」（A 切换，GpToggleMode）；不做手柄组合键。
// 软件输入层不露面：映射用到它时后台自己开、布局清空时自己关（见 docs/input/gamepad-input-layer.md）。
// 各分类的内容在 WallStage.GamepadPages.cs。读：工具箱后台 get_gamepad_state、get_state、get_rumble_state、get_input_layout。
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
    /// <summary>按键映射页两侧的行：行距 46（小字键名 + 值），值最宽 128。</summary>
    private const float GpMapRowStep = 46, GpMapValueW = 128;

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
        ["Back"] = new("front", 170, 61, 12, 22, 6, null, 0x0020), ["QS"] = new("front", 170, 89, 12, 22, 6),
        ["Start"] = new("front", 726, 60, 12, 22, 6, null, 0x0010), ["Claw"] = new("front", 726, 90, 12, 22, 6),
        ["LB"] = new("front", 126, 36, 64, 22, 10, "LB", 0x0100), ["RB"] = new("front", 774, 36, 64, 22, 10, "RB", 0x0200),
        // 扳机在正面只露一条边、和肩键共用一块轮廓，所以扳机一律画在侧视图上
        ["LT"] = new("tl", 105, 280, 90, 150, 20, "TL"), ["RT"] = new("tr", 315, 280, 90, 150, 20, "TR"),
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
    /// Side 0 = 线稿正下方居中。Compact = 按键映射页的紧凑行（小一号、行距小）；Stock = 值是原样（没改过），字淡一些。
    /// </summary>
    private sealed class GpItem
    {
        public string Title = "", Value = "";
        public int Side, Row;
        public float X, Y;
        public string Key = "";
        public string[]? Lights;
        public bool Compact, Stock, Disabled;
        public string? Why;
        public string[]? Choices;
        public int Choice;
        public Action<int>? SetChoice;
        public int? Number;
        public int Min, Max = 100, StepBy = 1;
        public Func<int, string>? Format;
        public Action<int>? SetNumber;
        public Action? Press;
        /// <summary>按键映射页的格子（WallStage.GamepadMap.cs）：Col -1 = 左侧、-2 = 右侧、0…3 = 底部四栏；Cx / Cy 是找焦点用的中心；Tokens = 映射值的图标块。</summary>
        public bool Map;
        public int Col;
        public float MapW, MapH;
        public float Cx, Cy;
        public List<GpTok>? Tokens;
        public bool Adjustable => !Disabled && (Choices != null || Number != null);
    }

    private ContainerVisual? _gp;
    /// <summary>_gpArtPage 是线稿整层（映射选择打开时隐去），里面的 _gpArtLayer 震动时抖；_gpHi 是线稿上的高亮，跟着线稿一起抖。</summary>
    private ContainerVisual _gpArtPage = null!;
    private SpriteVisual _gpSpot = null!, _gpBody = null!, _gpHi = null!;
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
    private JsonObject? _gpDesktopLayout;
    private JsonNode? _gpLayer;
    /// <summary>上次读到的固件模式（变了就提示）。_gpDesktopHeld = 手柄原本在桌面模式、因为游戏库在前台先切成了手柄模式，离开前台要切回去。</summary>
    private string? _gpLastMode;
    private bool _gpDesktopHeld;
    private int _gpPollTicks, _gpHoldVersion;
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
        SettingsResetPointer();
        _gpEditing = false;
        _gpPicker = null;
        if (_gpGame != null) { _gpGame = null; _gpLayout = null; }   // 游戏设置里编辑过某款游戏的布局：这一页是全局的
        ++_gpVersion;
        _gpInSide = true;
        _gpFocus = 0;
        BuildGpItems();
        GpPlaceArt(instant: true);
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

    /// <summary>
    /// 游戏设置 › 手柄（WallStage.GameSettingsEmbed.cs）编辑的是哪款游戏自己的布局（exe 名）；null = 全局。
    /// 那里直接用这一页的按键选择（GpPicker*），画在游戏设置页里，不打开这一页。
    /// </summary>
    private string? _gpGame;

    private void CloseGamepad(bool instant = false)
    {
        if (!_gpOpen || _gp == null) return;
        if (!instant) Cue(Sfx.ModalHide);
        _gpOpen = false;
        SettingsResetPointer();
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
        _gpArtPage = G.Container();
        _gp.Children.InsertAtTop(_gpArtPage);
        _gpSpot = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _gpArtPage.Children.InsertAtTop(_gpSpot);
        _gpArtLayer = G.Container();
        _gpArtPage.Children.InsertAtTop(_gpArtLayer);
        foreach (var face in new[] { "front", "back", "tl", "tr" })
        {
            var s = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            s.Opacity = face == "front" ? 1 : 0;
            _gpArt[face] = s;
            _gpArtLayer.Children.InsertAtTop(s);
        }
        EnsureGpFx();
        _gpHi = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _gpArtLayer.Children.InsertAtTop(_gpHi);
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
            // 每 3 秒重读一次（长按 ≡ 换了模式要跟上）；正在调、映射选择开着、有没写完的改动时不读，免得把本地值冲掉
            if (++_gpPollTicks >= 90)
            {
                _gpPollTicks = 0;
                if (!_gpEditing && !_settingsGesture.Active && _gpPicker == null && _gpLayoutEdits + _gpRumbleEdits == 0) _ = GpRefresh();
            }
            var now = _pad?.Last ?? default;
            if (now == _gpShown) return;
            _gpShown = now;
            if (_gpPicker == null) PaintGpHi();
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
            await GpLoadGlyphs(Path.Combine(dir, "Glyphs"));
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
        int revision = _gpInputRevision;
        if (_gpBusy || GpRefreshObsolete(revision)) return;
        _gpBusy = true;
        try
        {
            _agent ??= new AgentClient();
            var state = await _agent.Call("get_state");
            if (GpRefreshObsolete(revision)) return;
            if (state?["settings"] is JsonObject s) _gpSettings = (JsonObject)s.DeepClone();
            var rumble = await _agent.Call("get_rumble_state");
            if (GpRefreshObsolete(revision)) return;
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
                if (GpRefreshObsolete(revision)) return;
                _gpFw = Bool(fw?["available"]) ? fw : null;
                _gpFwError = _gpFw == null ? Str(fw?["error"]) ?? "读不到手柄固件" : null;
                // 游戏库在前台时不让桌面模式生效（比如从 Game Bar 切过来）：先切回手柄模式，离开前台再切回去
                if (Str(_gpFw?["mode"]) is string mode)
                {
                    _gpLastMode = mode;
                    if (mode == "desktop" && _windowActive) _ = GpHoldGamepadMode();
                }
            }
            catch (Exception ex) { _gpFw = null; _gpFwError = ex.Message.Contains("不支持") ? "后台还没有固件读取" : ex.Message; }
            try
            {
                var layer = await _agent.Call("get_input_layer_state");
                if (GpRefreshObsolete(revision)) return;
                _gpLayer = layer;
                var layout = _gpGame != null ? await _agent.Call("get_input_layout", JsonValue.Create(_gpGame)) : await _agent.Call("get_input_layout");
                if (GpRefreshObsolete(revision)) return;
                if (layout?["layout"] is JsonObject lay) _gpLayout = (JsonObject)lay.DeepClone();
                var desktopLayout = await _agent.Call("get_desktop_layout");
                if (GpRefreshObsolete(revision)) return;
                if (desktopLayout?["layout"] is JsonObject dl) _gpDesktopLayout = (JsonObject)dl.DeepClone();
            }
            catch { _gpLayer = null; _gpLayout = null; }
        }
        catch (Exception ex)
        {
            if (_gpError == null) Log.Warn($"gamepad refresh: {ex.Message}");
            _gpError = "连不上工具箱后台";
        }
        finally { _gpBusy = false; }
        if (!_gpOpen) { if (_stOpen && _gsItem != null) Repaint(); return; }
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

    // ---------------- 游戏库里不让桌面模式生效 ----------------

    /// <summary>窗口激活 / 失去激活（WallStage.Rail 里的 Activated 调）。</summary>
    private void GpOnWindowActive(bool active)
    {
        int v = ++_gpHoldVersion;
        if (active) { _ = GpCheckDesktopMode(); return; }
        if (_gpDesktopHeld) _ = GpReleaseDesktopMode(v);
    }

    private bool _gpInputCheckRunning;
    private int _gpRecoveryVersion;

    private void GpAgentEvent(string name, JsonNode? value)
    {
        if (name != "gamepad_mode" || value is not JsonObject mode || !Bool(mode["notify"])) return;
        _dq.TryEnqueue(() =>
        {
            if (_servicesDisposed) return;
            // An explicit choice in Game Bar supersedes an older deferred
            // desktop-mode restore owned by this library window.
            ++_gpHoldVersion;
            _gpDesktopHeld = _gpDesktopNoticePending = false;
            if (_windowActive && Str(mode["mode"]) == "desktop") After(250, () => { if (_windowActive) _ = GpCheckDesktopMode(); });
            _pad?.RequestRescan();
        });
    }

    private void GpAgentConnectionChanged(bool connected)
    {
        _dq.TryEnqueue(() =>
        {
            if (_servicesDisposed) return;
            _pad?.RequestRescan();
            int version = ++_gpRecoveryVersion;
            if (!_windowActive) return;
            _ = GpRecoverInput(version, connected ? 250 : 700);
        });
    }

    private async Task GpRecoverInput(int version, int firstDelay)
    {
        for (int attempt = 0; version == _gpRecoveryVersion && !_servicesDisposed && _windowActive; attempt++)
        {
            await Task.Delay(attempt == 0 ? firstDelay : 3000);
            if (version != _gpRecoveryVersion || _servicesDisposed || !_windowActive) return;
            Log.Info("gamepad: recovering input after backend handoff");
            if (await GpCheckDesktopMode()) return;
            // An open socket can precede firmware/input readiness. Continue
            // after a timed-out check even when the handshake already succeeded.
        }
    }

    /// <summary>激活时确认手柄模式，并检查当前游戏库是否在输入层白名单里。</summary>
    private async Task<bool> GpCheckDesktopMode()
    {
        if (_gpInputCheckRunning) return false;
        _gpInputCheckRunning = true;
        try
        {
            _agent ??= new AgentClient();
            var fw = await _agent.Call("get_gamepad_state");
            if (Str(fw?["mode"]) == "desktop" && _windowActive) await GpHoldGamepadMode();
            if (_windowActive) await GpEnsureInputLayerAllowsLibrary();
            return true;
        }
        catch (Exception ex) { Log.Warn($"gamepad mode check: {ex.Message}"); return false; }
        finally { _gpInputCheckRunning = false; }
    }

    /// <summary>MSIX 升级会改可执行文件路径；只刷新放行路径，不停止或重建手柄。</summary>
    private async Task GpEnsureInputLayerAllowsLibrary()
    {
        string package = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (!package.StartsWith("ClawToolbox.Library_", StringComparison.OrdinalIgnoreCase)) return;
        var state = await _agent!.Call("get_input_layer_state");
        if (Str(state?["phase"]) != "active" || (bool?)state?["engine"]?["hidhide"]?["cloak"] != true) return;
        if (state?["engine"]?["hidhide"]?["whitelist"] is not JsonArray allowed) return;
        string current = $"\\{package}\\ClawLibrary.exe";
        if (allowed.Any(path => Str(path)?.EndsWith(current, StringComparison.OrdinalIgnoreCase) == true)) return;

        Log.Warn($"gamepad: current library package missing from input whitelist ({package}); refreshing app paths");
        await _agent.Call("refresh_input_layer_apps");
    }

    private async Task GpHoldGamepadMode()
    {
        if (_gpDesktopHeld && Str(_gpFw?["mode"]) != "desktop") return;
        _gpDesktopHeld = true;
        Log.Info("gamepad: desktop mode held as gamepad mode while the library is in front");
        try { await _agent!.Call("set_gamepad_mode", JsonValue.Create("xinput"), JsonValue.Create(false)); }
        catch (Exception ex) { _gpDesktopHeld = false; Log.Warn($"gamepad hold: {ex.Message}"); }
        if (_gpOpen) _ = GpRefresh();
    }

    /// <summary>
    /// 离开前台 1.5 秒后还没回来就切回桌面模式；前台是 Game Bar（或我们的小组件）时先等着 —— 那里要用手柄操作，回到别处再切。
    /// </summary>
    private async Task GpReleaseDesktopMode(int version)
    {
        await Task.Delay(1500);
        while (version == _gpHoldVersion && !_windowActive && SystemInfo.ForegroundProcessName() is string fg
               && (fg.StartsWith("GameBar", StringComparison.OrdinalIgnoreCase) || fg.StartsWith("ClawWidget", StringComparison.OrdinalIgnoreCase)))
            await Task.Delay(1000);
        if (version != _gpHoldVersion || _windowActive || !_gpDesktopHeld) return;
        _gpDesktopHeld = false;
        Log.Info("gamepad: library left the foreground, back to desktop mode");
        try
        {
            await _agent!.Call("set_gamepad_mode", JsonValue.Create("desktop"), JsonValue.Create(_gpDesktopNoticePending));
            _gpDesktopNoticePending = false;
        }
        catch (Exception ex) { Log.Warn($"gamepad release: {ex.Message}"); }
    }

    /// <summary>要的是桌面模式：固件已经在桌面模式，或者游戏库在前台、离开后要切回桌面模式。</summary>
    private bool GpWantDesktop => _gpDesktopHeld || Str(_gpFw?["mode"]) == "desktop";
    private bool _gpDesktopNoticePending;

    /// <summary>
    /// 手柄页的模式开关（A）：切到手柄模式立刻写固件、取消「离开后切回桌面」；切到桌面模式时游戏库在前台，
    /// 就照 GpHoldGamepadMode 的规矩记下来，离开前台 1.5 秒后由 GpReleaseDesktopMode 真的切。界面先改，再读回固件。
    /// </summary>
    private async Task GpToggleMode()
    {
        bool toDesktop = !GpWantDesktop;
        ++_gpHoldVersion;   // 作废还在等的切回
        try
        {
            _agent ??= new AgentClient();
            if (!toDesktop)
            {
                _gpDesktopHeld = false;
                _gpDesktopNoticePending = false;
                await _agent.Call("set_gamepad_mode", JsonValue.Create("xinput"));
                Log.Info("gamepad: mode set to gamepad from the gamepad page");
            }
            else if (_windowActive)
            {
                _gpDesktopHeld = true;
                _gpDesktopNoticePending = true;
                Log.Info("gamepad: desktop mode chosen, applied when the library leaves the foreground");
            }
            else await _agent.Call("set_gamepad_mode", JsonValue.Create("desktop"));
        }
        catch (Exception ex) { Toast($"没有切过去：{ex.Message}"); }
        if (_gpOpen) { BuildGpItems(); PaintGpBody(); }
        _ = GpRefresh();
    }

    /// <summary>手柄页里的 X / Y：震动页 X 测试震动、Y 恢复原来的强度；别的页不做事。</summary>
    private void GpPadButton(bool isY)
    {
        if (isY && GpGroupY()) return;
        if (_gpPicker != null || _gpEditing || GpCatId != "rumble") return;
        if (isY) _ = GpWrite("restore_rumble");
        else _ = GpTestRumble();
    }

    /// <summary>
    /// 软件输入层跟着布局走、不给开关：后台自己管（状态里 auto = true）时这里什么都不做；
    /// 老后台没有自动管理时，这里只在布局清空后把它关掉 —— 不替它打开，免得虚拟手柄被 Steam 的桌面配置当鼠标用。
    /// </summary>
    private async Task GpSyncLayer()
    {
        if (_gpLayer == null || Bool(_gpLayer["auto"])) return;
        bool want = (_gpLayout?["buttons"] as JsonObject)?.Count > 0;
        if (!want && GpLayerOn) await GpWrite("set_input_layer_enabled", JsonValue.Create(false));
    }

    private async Task GpTestRumble()
    {
        GpPlayRumble((_gpRumbleL ?? 100) / 100f, (_gpRumbleR ?? 100) / 100f);
        if (_agent == null) return;
        try { await _agent.Call("test_rumble"); }
        catch (Exception ex) { Toast($"没有震起来：{ex.Message}"); }
    }

    // ---------------- 分类 ----------------

    /// <summary>左边分类：Group = 分组小标题（不能选，Id 是这组对应的固件模式，正在用的那组标「当前」）。</summary>
    private static readonly (string Id, string Title, bool Group)[] GpSidebar =
    {
        ("xinput", "手柄模式", true), ("gpmap", "按键映射", false), ("stick", "摇杆", false), ("trigger", "扳机", false), ("rumble", "震动", false),
        ("desktop", "桌面模式", true), ("dkmap", "按键映射", false), ("mouse", "鼠标", false),
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

    /// <summary>这一页用哪几张线稿：扳机页是两张侧视图，其余是正面（焦点到背键时翻背面、到扳机时换侧视图）。</summary>
    private HashSet<string> GpPageFaces()
    {
        if (GpCatId == "trigger") return new() { "tl", "tr" };
        var it = !_gpInSide && _gpFocus >= 0 && _gpFocus < _gpItems.Count ? _gpItems[_gpFocus] : null;
        if (it == null || !GpKeys.TryGetValue(it.Key, out var k)) return new() { "front" };
        return k.Face switch { "back" => new() { "back" }, "tl" or "tr" => new() { "tl", "tr" }, _ => new() { "front" } };
    }

    private bool GpIsMapPage => GpCatId is "gpmap" or "dkmap";

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
        SettingsResumeKeyboard();
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
                GpPlaceArt();
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
            GpStepItem(cur, dir == Direction.Left ? -1 : 1);
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

    /// <summary>设置项的中心（设计 px），按方位找下一个焦点用：说明文字那块。</summary>
    private static Vector2 GpCenter(GpItem it) => it.Map ? new(it.Cx, it.Cy) :
        new(it.Side < 0 ? it.X - 80 : it.Side > 0 ? it.X + 80 : it.X, it.Y + (it.Compact ? 10 : 13));

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
        SettingsResumeKeyboard();
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
        SettingsResetPointer();
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
            GpPlaceArt(instant: true);   // 线稿整层的位移是物理像素，缩放变了要重算
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

    /// <summary>
    /// 两层，都是 1280×800 设计 px：_gpBody 是界面（标题、分类、设置、映射表、连线），_gpHi 是线稿上的高亮和实时输入（在线稿那层里，跟着线稿缩放）。
    /// 实时输入变了只重画 _gpHi。映射选择打开时线稿整层隐去。
    /// </summary>
    private void PaintGpBody()
    {
        // 游戏设置里的按键选择：画在设置页上
        if (!_gpOpen) { if (_stOpen && _gsItem != null) Repaint(); return; }
        if (_gp == null) return;
        GpUpdateMapScroll();
        float k = _ccK, raster = k * Chrome.Dpi;
        _gpTargets.Clear();
        CcSet(_gpBody, CcDraw(1280, 800, raster, PaintGpUi));
        _gpBody.Size = new Vector2(1280 * k, 800 * k);
        _gpBody.Offset = Vector3.Zero;
        PaintGpHi();
        float artAlpha = _gpPicker != null ? 0f : 1f;
        if (_gpArtPage.Opacity != artAlpha) _gpArtPage.Opacity = artAlpha;
    }

    private void PaintGpHi()
    {
        if (_gp == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        CcSet(_gpHi, CcDraw(1280, 800, raster, PaintGpArtHi));
        _gpHi.Size = new Vector2(1280 * k, 800 * k);
        _gpHi.Offset = Vector3.Zero;
    }

    private GpItem? GpFocusItem => !_gpInSide && _gpFocus >= 0 && _gpFocus < _gpItems.Count ? _gpItems[_gpFocus] : null;

    private void PaintGpUi(CanvasDrawingSession ds)
    {
        var ui = TextKit.Ui(400);
        var accent = PerfAccent;
        var muted = Gfx.WithAlpha(CcText, 0.5);
        bool connected = _pad?.Connected ?? false;

        // 标题：和控制中心的「控制中心」同一个位置、同一个字；下面一行小字是机型、连接状态和模式
        PText(ds, "手柄", TextKit.Ui(700), 24, CcText, 40, 48, 900);
        string mode = _gpDesktopHeld ? "桌面模式（游戏库里先用手柄模式）"
            : Str(_gpFw?["mode"]) switch { "desktop" => "桌面模式", "xinput" => "手柄模式", "dinput" => "DInput 模式", _ => "" };
        string sub = !connected ? "Claw 8 EX · 没有检测到手柄" : $"Claw 8 EX · 已连接{(mode.Length > 0 ? " · " + mode : "")}";
        PText(ds, sub, ui, 13, muted, 40, 82, 900);

        if (_gpPicker?.Level == GpSubLevel.Group) PaintGpGroupList(ds, _gpPicker); else PaintGpSidebar(ds);
        if (_gpPicker != null) { PaintGpPicker(ds, accent); return; }
        if (!_gpArtLoaded) PText(ds, "正在载入线稿…", ui, 13, muted, GpCx - 40, GpIsMapPage ? GpMapCy : GpCy, 300);
        // 映射页滚动：设置和格子跟线稿一起往上走，裁在标题下面、按键提示上面
        float scroll = GpIsMapPage ? _gpMapScroll : 0;
        var unscrolled = ds.Transform;
        var scrollClip = GpIsMapPage ? ds.CreateLayer(1, new Windows.Foundation.Rect(0, 96, 1280, 626)) : null;
        ds.Transform = Matrix3x2.CreateTranslation(0, -scroll) * unscrolled;
        if (GpIsMapPage) { GpMapCenterColumns(ds, _gpItems, _gpMapHeads); PaintGpMapHeads(ds); }

        var focus = GpFocusItem;
        // 两侧的设置：焦点那项拉一根细线到它管的键（摇杆映射那种没有 Key 的，连到它点亮的那块）
        // 按键映射页不拉线：焦点那行白字，线稿上它管的那块染色就够了
        string target = focus == null || focus.Side == 0 || focus.Map ? "" : focus.Key.Length > 0 ? focus.Key : focus.Lights?.FirstOrDefault() ?? "";
        if (focus != null && target.Length > 0 && GpKeys.TryGetValue(target, out var fk) && _gpFacesShown.Contains(fk.Face))
        {
            var r = GpKeyRect(fk);
            var c = GpMap(fk.Face, fk.X, fk.Y);
            bool left = focus.Side < 0;
            float half = fk.Region == null && target is "LS" or "RS" ? GpStickDisc * GpS : (float)r.Width / 2;
            var end = new Vector2(c.X + (left ? -half : half), c.Y);
            var start = new Vector2(left ? GpLeftEdge + 14 : GpRightEdge - 14, focus.Y + (focus.Compact ? 10 : 13));
            using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
            ds.DrawLine(start, end, Gfx.WithAlpha(accent, 0.9f), 1.4f, round);
            ds.FillCircle(start, 3, accent);
        }

        for (int i = 0; i < _gpItems.Count; i++)
        {
            var it = _gpItems[i];
            GpAddItemTargets(ds, it, i);
            if (it.Map) PaintGpMapRow(ds, it, it == focus);
            else PaintGpItem(ds, it, it == focus);
        }
        ds.Transform = unscrolled;
        scrollClip?.Dispose();

        // 底部的按键提示（按键图标 + 字）
        if (focus != null && focus.Title == "模式" && focus.Side == 0) GpHints(ds, ("A", GpWantDesktop ? "切到手柄模式" : "切到桌面模式"));
        else if (GpIsMapPage)
        {
            if (focus == null) GpHints(ds, ("→", "进去选键"));
            else if (_gpEditing) GpHints(ds, ("← →", "改"), ("A", "完成"));
            else if (focus.Choices != null) GpHints(ds, ("A", "改这个键在 Steam 大屏幕里做什么"));
            else GpHints(ds, ("A", "改映射"));
        }
        else if (GpCatId == "rumble") GpHints(ds, ("X", "测试震动"), ("Y", "恢复原来的强度"));
    }

    /// <summary>
    /// 底部居中一排按键提示：每个是一个圆角小框里的键名（像手柄上的键）+ 一句说明，之间空开。
    /// </summary>
    private void GpHints(CanvasDrawingSession ds, params (string Key, string Text)[] hints)
    {
        if (!_gpOpen) return;   // 游戏设置里不画按键提示
        var ui = TextKit.Ui(400);
        var keyFont = TextKit.Ui(600);
        const float y = 732, h = 22, gap = 28;
        float total = 0;
        var widths = hints.Select(x => (Key: Math.Max(h, PWidth(ds, x.Key, keyFont, 12) + 12), Text: PWidth(ds, x.Text, ui, 13))).ToArray();
        foreach (var w in widths) total += w.Key + 8 + w.Text;
        total += gap * (hints.Length - 1);
        float x0 = GpCx - total / 2;
        var line = Gfx.WithAlpha(CcText, 0.55f);
        for (int i = 0; i < hints.Length; i++)
        {
            var (kw, tw) = widths[i];
            ds.DrawRoundedRectangle(x0, y - h / 2, kw, h, h / 2, h / 2, line, 1.2f);
            float keyW = PWidth(ds, hints[i].Key, keyFont, 12);
            PText(ds, hints[i].Key, keyFont, 12, Gfx.WithAlpha(CcText, 0.85f), x0 + (kw - keyW) / 2, y, keyW + 4);
            PText(ds, hints[i].Text, ui, 13, Gfx.WithAlpha(CcText, 0.6f), x0 + kw + 8, y, tw + 4);
            int action = hints[i].Key switch { "B" => 0, "X" => 1, "Y" => 2, _ => -1 };
            if (action >= 0) _gpTargets.Add(new(action, SettingsTargetKind.Action, x0 - 4, y - 22, kw + 8 + tw + 8, 44));
            x0 += kw + 8 + tw + gap;
        }
    }

    /// <summary>线稿上的高亮（在线稿那层里画）：这一页额外的图、实时输入、焦点那项管的区域。</summary>
    private void PaintGpArtHi(CanvasDrawingSession ds)
    {
        if (_gpPicker != null) return;
        var accent = PerfAccent;
        var focus = GpFocusItem;
        PaintGpPageArt(ds, accent, focus);
        PaintGpLive(ds, accent);
        if (focus != null)
            foreach (var region in focus.Lights ?? (focus.Key.Length > 0 ? new[] { focus.Key } : Array.Empty<string>()))
                GpFill(ds, region, 0.42f, accent);
    }

    /// <summary>分类：竖向居中，只有字；分组小标题小一号、更淡，正在用的模式后面标「当前」；当前类白字，焦点在分类上时前面加 ▸。</summary>
    private void PaintGpSidebar(CanvasDrawingSession ds)
    {
        var ui = TextKit.Ui(400);
        int cat = 0;
        string? mode = GpWantDesktop ? "desktop" : Str(_gpFw?["mode"]);
        for (int i = 0; i < GpSidebar.Length; i++)
        {
            var e = GpSidebar[i];
            float cy = GpSidebarY(i);
            if (e.Group)
            {
                var gf = TextKit.Ui(500);
                PText(ds, e.Title, gf, 12, Gfx.WithAlpha(CcText, 0.36f), GpSideX, cy, 200);
                if (mode == e.Id)
                {
                    float w = PWidth(ds, e.Title, gf, 12);
                    ds.FillCircle(GpSideX + w + 10, cy, 2.5f, PerfAccent);
                    PText(ds, "当前", gf, 12, Gfx.WithAlpha(PerfAccent, 0.9f), GpSideX + w + 17, cy, 60);
                }
                continue;
            }
            _gpTargets.Add(new(cat, SettingsTargetKind.Category, 32, cy - GpCatH / 2, 138, GpCatH));
            bool cur = cat++ == _gpCat;
            var col = cur ? (_gpInSide ? CcText : Gfx.WithAlpha(CcText, 0.92f)) : Gfx.WithAlpha(CcText, 0.45f);
            PText(ds, e.Title, cur ? TextKit.Ui(600) : ui, 17, col, GpSideX + 16, cy, 200);
            if (cur && _gpInSide && _gpPicker == null) GpFocusMark(ds, GpSideX, cy, CcText);
        }
    }

    /// <summary>▸ 焦点记号（分类、映射表共用）。</summary>
    private static void GpFocusMark(CanvasDrawingSession ds, float x, float cy, Color color)
    {
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(x, cy - 6); pb.AddLine(x + 8, cy); pb.AddLine(x, cy + 6); pb.EndFigure(CanvasFigureLoop.Closed);
        using var g = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(g, color);
    }

    /// <summary>
    /// 线稿上的实时输入，只亮这一页管的：映射页亮按键（扳机不亮，正面肩部那块只归肩键）；摇杆页 / 鼠标页是摇杆推到哪、按下；
    /// 扳机页是侧视图上扳机按多深填多少；震动页不亮。
    /// </summary>
    private void PaintGpLive(CanvasDrawingSession ds, Color accent)
    {
        var snap = _gpShown;
        switch (GpCatId)
        {
            case "gpmap" or "dkmap":
                foreach (var (name, key) in GpKeys)
                    if (key.XInput != 0 && (snap.Buttons & key.XInput) != 0) GpFill(ds, name, 0.85f, accent);
                break;
            case "stick" or "mouse":
                if (!_gpFacesShown.Contains("front")) break;
                foreach (var (name, ax, ay) in new[] { ("LS", snap.LX, snap.LY), ("RS", snap.RX, snap.RY) })
                {
                    var key = GpKeys[name];
                    if ((snap.Buttons & key.XInput) != 0) GpFill(ds, name, 0.5f, accent);
                    float fx = ax / 32767f, fy = ay / 32767f;
                    if (fx * fx + fy * fy < 0.01f) continue;
                    var p = GpMap("front", key.X, key.Y) + new Vector2(fx, -fy) * (key.W / 2 * GpS);
                    ds.FillCircle(p, 5f, accent);
                }
                break;
            case "trigger":
                if (!_gpFacesShown.Contains("tl")) break;
                GpFillPartial(ds, "TL", snap.LT / 255f, accent);
                GpFillPartial(ds, "TR", snap.RT / 255f, accent);
                break;
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

    /// <summary>
    /// 一个设置：上面小字标题，下面值；焦点白字，调节中值两边是 ‹ ›，置灰的写原因。左边一列右对齐、右边一列左对齐、Side 0 居中。
    /// Compact（按键映射）：小一号、放不下的值截断加「…」，没改过的值字淡一些，调节提示在页面底部。
    /// </summary>
    private void PaintGpItem(CanvasDrawingSession ds, GpItem it, bool focused)
    {
        var ui = TextKit.Ui(400);
        float a = it.Disabled ? 0.4f : 1f;
        var titleCol = Gfx.WithAlpha(CcText, (focused ? 0.75f : 0.45f) * (it.Disabled ? 0.85f : 1f));
        var valueCol = Gfx.WithAlpha(CcText, (focused ? 1f : it.Compact ? (it.Stock ? 0.42f : 0.92f) : 0.66f) * a);
        bool editing = focused && _gpEditing;
        string value = editing && it.Choices != null ? it.Choices[it.Choice]
            : editing && it.Number is int n && it.Format != null ? it.Format(n) : it.Value;
        var valueFont = editing || focused && it.Compact ? TextKit.Ui(600) : ui;
        float titleSize = it.Compact ? 12 : 13, valueSize = it.Compact ? 15 : 19, valueDy = it.Compact ? 20 : 27;
        if (it.Compact && !editing) value = GpFit(ds, value, valueFont, valueSize, GpMapValueW);
        float tw = PWidth(ds, it.Title, ui, titleSize), vw = PWidth(ds, value, valueFont, valueSize);
        float chev = editing ? (it.Compact ? 18 : 22) : 0;
        float tx, vx;
        if (it.Side < 0) { tx = it.X - tw; vx = it.X - vw - chev; }
        else if (it.Side > 0) { tx = it.X; vx = it.X + chev; }
        else { tx = it.X - tw / 2; vx = it.X - vw / 2; }
        PText(ds, it.Title, ui, titleSize, titleCol, tx, it.Y, tw + 4);
        PText(ds, value, valueFont, valueSize, valueCol, vx, it.Y + valueDy, vw + 4);
        if (editing)
        {
            DrawChevron(ds, vx - chev + 6, it.Y + valueDy, true, valueCol);
            DrawChevron(ds, vx + vw + chev - 6, it.Y + valueDy, false, valueCol);
            GpAddArrowTargets(vx - chev + 6, vx + vw + chev - 6, it.Y + valueDy);
            if (it.Compact || it.Number != null) return;
            const string hint = "← → 改  ·  A 完成";
            float hw = PWidth(ds, hint, ui, 11);
            PText(ds, hint, ui, 11, Gfx.WithAlpha(CcText, 0.45f), it.Side < 0 ? it.X - hw : it.Side > 0 ? it.X : it.X - hw / 2, it.Y + 52, hw + 4);
        }
    }

    /// <summary>放不下就从尾巴截掉、加「…」。</summary>
    private static string GpFit(CanvasDrawingSession ds, string s, FontSpec f, float size, float max)
    {
        if (PWidth(ds, s, f, size) <= max) return s;
        for (int n = s.Length - 1; n > 1; n--)
        {
            string t = s[..n].TrimEnd(' ', '·', '+') + "…";
            if (PWidth(ds, t, f, size) <= max) return t;
        }
        return "…";
    }
}
