// 快捷设置（快速设置键）：从左边滑出一列（左侧快捷栏，定稿 2026-10-30）。只有一栏，没有分区页和图标栏。
// 游戏不切走：游戏库窗口到前台，面板右边用 DWM 实时缩略图铺游戏画面（按游戏窗口原来的位置裁），看上去面板压在游戏上。
// 面板是实底暖黑（Braun 配色），硬边，不截图、不模糊。版式见下面「排版」。
// 焦点：整格反白；左右在功耗、亮度、音量上调值，其余格子左右上下按位置走。B 先取消退出确认，再收起面板。
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Windows.Devices.Radios;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum QsKind { Slider, Tile, Entry, Meter, Choice, Nav, Rail }

    private sealed class QsItem
    {
        public required string Id;
        public required QsKind Kind;
        public string Name = "", Value = "", Glyph = "", Section = "now";
        public bool On, Disabled;
        public float X, Y, W, H;
        /// <summary>滑条 0–1。</summary>
        public float Level;
        /// <summary>磁贴大小：S 1×1、M 2×1、L 2×2。</summary>
        public char Size = 'S';
        public string[] Options = Array.Empty<string>();
        /// <summary>几选一：当前值（-1 = 都不是）；Sub = 焦点在哪一项。</summary>
        public int Choice = -1, Sub;
    }

    // 设计 px（1280 宽）：面板 384 = 4 列 × 96
    private const float QsW = 384, QsRailW = 0, QsCx = 108, QsCw = 312, QsCellH = 64, QsCellGap = 10, QsGap = 24;
    private static readonly Color QsInk = Gfx.Rgba(0x09, 0x09, 0x0b), QsOff = Gfx.Rgba(244, 244, 245, 0.13), QsRed = Gfx.Rgba(0xff, 0x8a, 0x7a);
    private static readonly int[] QsFps = { 0, 30, 40, 45, 60 };
    private static readonly (int V, string N)[] QsRumble = { (0, "关"), (35, "弱"), (70, "中"), (100, "强") };
    private static readonly (string Id, string N)[] QsFan = { ("quiet", "静音"), ("balanced", "均衡"), ("performance", "性能") };
    private static readonly (string Id, string Glyph, string Name)[] QsSections =
    {
        ("now", "\uE7FC", "正在游戏"), ("perf", "\uE945", "性能"), ("disp", "qs.sun", "显示"), ("osd", "qs.osd", "性能读数"),
        ("snd", "qs.vol", "声音"), ("fan", "\uE9CA", "风扇"), ("pad", "\uE7FC", "手柄"), ("net", "\uE701", "连接"), ("sys", "\uE770", "系统"),
    };
    /// <summary>可放进磁贴区的功能（id、图标、名字）。</summary>
    private static readonly (string Id, string Glyph, string Name)[] QsTilePool =
    {
        ("magpie", "qs.magpie", "画面缩放"), ("osd", "qs.osd", "性能读数"), ("fan", "\uE9CA", "风扇"), ("fps", "qs.fps", "帧率上限"),
        ("lossless", "qs.duck", "小黄鸭"), ("wifi", "\uE701", "Wi-Fi"), ("bluetooth", "\uE702", "蓝牙"), ("rumble", "\uE877", "震动"),
        ("refresh", "\uE895", "刷新率"), ("screenshot", "\uF406", "截图"), ("keyboard", "\uE765", "屏幕键盘"), ("windows", "\uE7C4", "窗口"),
    };
    private static readonly (string Id, char Size)[] QsTileDefault =
        { ("magpie", 'L'), ("osd", 'M'), ("fan", 'M'), ("fps", 'S'), ("lossless", 'S'), ("wifi", 'S') };

    private ContainerVisual? _qs;
    private SpriteVisual _qsScrim = null!, _qsPanel = null!;
    private bool _qsOpen, _qsTicking, _qsHideWindowAfter;
    private float _qsShow, _qsShowTo;
    private long _qsFrameAt;
    private int _qsFocus, _qsVersion;
    private string? _qsFocusId;
    private readonly List<QsItem> _qsItems = new();
    private JsonObject? _qsState;
    private JsonObject? _qsDraft;
    private bool? _qsWifi, _qsBt, _qsLossless;
    private bool _qsLosslessKnown;
    private DwmThumb? _qsThumb;
    private nint _qsGame;
    private (int L, int T, int W, int H) _qsGameRect;
    private string? _qsNote;
    private long _qsNoteAt;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _qsPoll;
    // 列表滚动（设计 px）、各分区在列表里的起点
    private float _qsScroll, _qsContentH;
    private readonly Dictionary<string, float> _qsSecTop = new();
    // 电源：0 收着、1 展开；第几个圆有焦点（1 起，0 = 电源图标本身）；哪个圆在等二次确认
    private bool _qsPwrOpen;
    private int _qsPwrFocus;
    private string? _qsPwrArmed;
    // 磁贴编辑
    private bool _qsEditing;
    private List<(string Id, char Size)>? _qsTiles;
    // 底：截的游戏帧（已模糊压暗，按设计 px 448×H 栅格化）、封面、取色
    private CanvasRenderTarget? _qsBack;
    private CanvasBitmap? _qsCover;
    private string? _qsCoverId;
    /// <summary>封面上带 logo（用的是 header）时，上面那行游戏名不再写（名字只出现一次）。</summary>
    private bool _qsCoverHasLogo;
    // 当前页（now = 首页）、每页上次的焦点、混排磁贴往右走时记下的来路、退出游戏等二次确认的时刻
    private string _qsPage = "now";
    private readonly Dictionary<string, string> _qsLastFocus = new();
    private string? _qsBackLeft;
    private long _qsQuitArmedAt;
    private Color _qsTint = Gfx.Rgba(0xa9, 0xdf, 0xd3), _qsTintInk = Gfx.Rgba(0x0b, 0x1a, 0x17);

    internal bool QuickPanelOpen => _qsOpen;
    /// <summary>旧的列表式设计预览已去掉；触控导航还在读它。</summary>
    private static bool QsRowsStyle => false;

    // ---------------- 开 / 关 ----------------

    /// <summary>窗口出来之前调用：把游戏画面先铺满（第一帧就是游戏，不闪墙面 / 黑底），面板从左边外面开始滑。</summary>
    internal void OpenQuickPanel(nint returnWindow)
    {
        ExitScreensaver(); MarkInput();
        EnsureQsVisuals();
        bool again = _qsOpen;
        _qsOpen = true;
        ++_qsVersion;
        _qsHideWindowAfter = false;
        if (!again)
        {
            _qsFocusId = null;
            _qsScroll = 0;
            _qsPage = "now"; _qsLastFocus.Clear(); _qsBackLeft = null; _qsQuitArmedAt = 0;
            _qsPwrOpen = false; _qsPwrArmed = null; _qsEditing = false;
            _qsThumb?.Dispose(); _qsThumb = null;
            _qsBack?.Dispose(); _qsBack = null;
            nint self = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            _qsGame = returnWindow != 0 && returnWindow != self && OpenWindows.Alive(returnWindow) ? returnWindow : 0;
            if (_qsGame != 0 && GetWindowRect(_qsGame, out var r))
            {
                _qsGameRect = (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                _qsThumb = DwmThumb.Register(self, _qsGame);
            }
            _qsShow = 0;
            if (_ccOpen) CloseControlCenter();
            // The owning window acquires the input/OSD lease before showing.
        }
        _qsShowTo = 1;
        // 有游戏画面时底下铺黑（缩略图盖住右边）；没有（在游戏库里按的）就压暗当前画面
        _qsScrim.Brush = G.C.CreateColorBrush(_qsThumb != null ? Gfx.Rgba(0, 0, 0) : Gfx.Rgba(0, 0, 0, 0.45));
        _qs!.IsVisible = true;
        _top.Children.Remove(_qs); _top.Children.InsertAtTop(_qs);
        QsBuild();
        QsPaint();
        if (Reduced) _qsShow = 1;
        QsStartTicking();
        _ = QsRefresh(_qsVersion);
        _qsPoll ??= _dq.CreateTimer();
        _qsPoll.Interval = TimeSpan.FromMilliseconds(1500);
        _qsPoll.Tick -= QsPollTick; _qsPoll.Tick += QsPollTick;
        _qsPoll.Start();
        Cue(Sfx.MenuIn);
        Log.Info($"quick panel open game={_qsGame} thumb={_qsThumb != null}");
    }

    /// <summary>收起：面板滑回去，缩略图跟着铺满；走完再把窗口收起来（游戏原样露出来，和缩略图同一幅画面）。</summary>
    internal void CloseQuickPanel(bool hideWindow = true, bool instant = false)
    {
        if (!_qsOpen || _qs == null) return;
        _qsOpen = false;
        _qsPoll?.Stop();
        // Keep the lease through the complete close animation.
        if (_qsEditing) { _qsEditing = false; QsSaveTiles(); }
        _qsHideWindowAfter = hideWindow;
        Cue(Sfx.MenuOut);
        _qsShowTo = 0;
        if (instant || Reduced) { _qsShow = 0; QsFinishClose(); return; }
        QsStartTicking();
        int v = ++_qsVersion;
        After(800, () => { if (v == _qsVersion && !_qsOpen && _qs.IsVisible) QsFinishClose(); });
        Log.Info("quick panel close");
    }

    /// <summary>
    /// 告诉后台面板开着 / 收起：性能显示（OSD）这段时间让开（它带 uiAccess、每帧重新置顶，面板压不过它）；
    /// 输入层在跑时，前台游戏的虚拟手柄归零（为以后不抢前台的面板准备）。
    /// </summary>
    private async Task QsHold(bool on)
    {
        try
        {
            _agent ??= new AgentClient();
            var r = await _agent.Call("set_overlay_hold", JsonValue.Create(on));
            Log.Info($"quick panel hold {on}: {r?.ToJsonString()}");
        }
        catch (Exception ex) { Log.Warn($"quick panel hold {on}: {ex.Message}"); }
    }

    private void QsFinishClose()
    {
        if (_qs == null) return;
        bool hide = _qsHideWindowAfter;
        _qsHideWindowAfter = false;
        if (!hide && !_wsOpen) SetSpecialInput(false);
        if (hide && _qsGame != 0 && (_window as MainWindow)?.OverlayAcceptsGamepad != true) LibraryWindow.BringForward(_qsGame);
        if (hide) (_window as MainWindow)?.HideAfterQuickPanel();
        // 窗口收起之后再拆缩略图，免得最后一帧露出黑底
        After(hide ? 260 : 0, () =>
        {
            if (_qsOpen) return;
            _qsThumb?.Dispose(); _qsThumb = null;
            if (_qs != null) _qs.IsVisible = false;
        });
    }

    private void EnsureQsVisuals()
    {
        if (_qs != null) return;
        _qs = G.Container();
        _qs.IsVisible = false;
        _qsScrim = G.C.CreateSpriteVisual();
        _qsScrim.RelativeSizeAdjustment = Vector2.One;
        _qsPanel = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _qs.Children.InsertAtTop(_qsScrim);
        _qs.Children.InsertAtTop(_qsPanel);
        _top.Children.InsertAtTop(_qs);
    }

    // ---------------- 底：截一帧游戏画面 ----------------

    /// <summary>
    /// 用 Windows.Graphics.Capture 截游戏窗口的一帧：面板那一块模糊、压暗后当面板底（DWM 缩略图压在窗口内容上面，没法模糊，只能另截）。
    /// 同时从这一帧取主色给开着的磁贴。截不到（独占全屏、权限）就用深色实底。
    /// </summary>
    private async Task QsCaptureBackdrop(nint hwnd, int v)
    {
        try
        {
            var dev = G.Device;
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId { Value = id.Value });
            if (item == null) { Log.Warn("quick panel backdrop: no capture item"); return; }
            var done = new TaskCompletionSource<CanvasBitmap?>();
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(dev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            using var session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch { }
            try { session.IsBorderRequired = false; } catch { }
            pool.FrameArrived += (p, _) =>
            {
                using var f = p.TryGetNextFrame();
                if (f == null) return;
                try
                {
                    using var src = CanvasBitmap.CreateFromDirect3D11Surface(dev, f.Surface);
                    var copy = new CanvasRenderTarget(dev, src.SizeInPixels.Width, src.SizeInPixels.Height, 96);
                    using (var ds = copy.CreateDrawingSession()) ds.DrawImage(src);
                    done.TrySetResult(copy);
                }
                catch (Exception ex) { done.TrySetException(ex); }
            };
            session.StartCapture();
            var frame = await Task.WhenAny(done.Task, Task.Delay(700)) == done.Task ? await done.Task : null;
            if (frame == null) { Log.Warn("quick panel backdrop: no frame"); return; }
            _dq.TryEnqueue(() =>
            {
                if (v != _qsVersion || !_qsOpen) { frame.Dispose(); return; }
                QsBakeBackdrop(frame);
                frame.Dispose();
                QsPaint();
            });
        }
        catch (Exception ex) { Log.Warn($"quick panel backdrop: {ex.Message}"); }
    }

    private void QsBakeBackdrop(CanvasBitmap frame)
    {
        float k = _ccK > 0 ? _ccK : (float)(View.Width / 1280), raster = k * Chrome.Dpi;
        float H = (float)(View.Height / k);
        var dev = G.Device;
        _qsBack?.Dispose();
        _qsBack = new CanvasRenderTarget(dev, QsW, H, 96 * raster);
        var (gl, gt, gw, gh) = _qsGameRect;
        float px = k * Chrome.Dpi;   // 设计 px → 屏幕像素
        var dest = new Windows.Foundation.Rect(gl / px, gt / px, gw / px, gh / px);
        using (var ds = _qsBack.CreateDrawingSession())
        {
            ds.Clear(Gfx.Rgba(10, 12, 14));
            using var blur = new GaussianBlurEffect { Source = frame, BlurAmount = 18 * px, BorderMode = EffectBorderMode.Hard };
            ds.DrawImage(blur, dest, frame.Bounds);
            ds.FillRectangle(0, 0, QsW, H, Gfx.Rgba(0, 0, 0, 0.6));
        }
        // 取色：缩到 12×12 求平均，提亮成开着的磁贴色
        try
        {
            using var small = new CanvasRenderTarget(dev, 12, 12, 96);
            using (var ds = small.CreateDrawingSession()) ds.DrawImage(frame, new Windows.Foundation.Rect(0, 0, 12, 12), frame.Bounds);
            var px2 = small.GetPixelColors();
            double r = px2.Average(c => c.R), g = px2.Average(c => c.G), b = px2.Average(c => c.B);
            var (h, s, _) = QsHsl(r / 255, g / 255, b / 255);
            _qsTint = QsFromHsl(h, Math.Clamp(s * 1.2, 0.22, 0.55), 0.82);
            _qsTintInk = QsFromHsl(h, 0.5, 0.1);
        }
        catch (Exception ex) { Log.Warn($"quick panel tint: {ex.Message}"); }
    }

    private static (double H, double S, double L) QsHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-6) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color QsFromHsl(double h, double s, double l)
    {
        double F(double t) { t = (t % 1 + 1) % 1; double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p; }
        return Gfx.Rgba((int)(F(h + 1.0 / 3) * 255), (int)(F(h) * 255), (int)(F(h - 1.0 / 3) * 255));
    }

    // ---------------- 封面（Steam 横版 920×430） ----------------

    /// <summary>自测用（--qsgame=游戏名）：没开游戏也按这个游戏摆封面、名字和退出按钮。</summary>
    internal string? _qsTestGame;

    private LibraryItem? QsGameItem()
    {
        if (_game?.Item is { } g) return g;
        string? title = _qsTestGame ?? Str(QsGet("system.game.title"));
        return title == null ? null : _lib.Games.FirstOrDefault(i => string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase));
    }

    private void QsLoadCover()
    {
        var item = QsGameItem();
        if (item == null || item.Id == _qsCoverId) return;
        _qsCoverId = item.Id;
        _qsCover = null;
        string? hero = _steamRoot != null ? Steam.ArtSources(_steamRoot, _steamUser, item.Id).Hero : null;
        string? path = hero ?? QsHeaderPath(item.Id);
        _qsCoverHasLogo = hero == null;
        if (path == null) return;
        string id = item.Id;
        _ = Task.Run(async () =>
        {
            try { var b = await CanvasBitmap.LoadAsync(G.Device, path); _dq.TryEnqueue(() => { if (_qsCoverId == id) { _qsCover = b; QsBuild(); QsPaint(); } }); }
            catch (Exception ex) { Log.Warn($"quick panel cover {id}: {ex.Message}"); }
        });
    }

    /// <summary>横版封面：自己选的 → Steam grid 里的 &lt;id&gt;.png/jpg → librarycache 的 header。</summary>
    private string? QsHeaderPath(string id)
    {
        try
        {
            if (LocalGames.ArtFile(id, "") is { } own) return own;
            if (_steamUser != null)
            {
                var grid = Path.Combine(_steamUser, "config", "grid");
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
                    if (File.Exists(Path.Combine(grid, id + ext))) return Path.Combine(grid, id + ext);
            }
            if (_steamRoot != null)
            {
                var cache = Path.Combine(_steamRoot, "appcache", "librarycache", id);
                if (Directory.Exists(cache) && Directory.GetFiles(cache, "header*.jpg", SearchOption.AllDirectories).FirstOrDefault() is { } h) return h;
                var flat = Path.Combine(_steamRoot, "appcache", "librarycache", $"{id}_header.jpg");
                if (File.Exists(flat)) return flat;
            }
        }
        catch (Exception ex) { Log.Warn($"quick panel header {id}: {ex.Message}"); }
        return null;
    }

    // ---------------- 数据 ----------------

    private async Task QsRefresh(int v)
    {
        if (_pagePointer.Active || _qsTouchWrites?.IsBusy == true) return;
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.Call("widget_state") is JsonObject s && v == _qsVersion && _qsOpen && !_pagePointer.Active && _qsTouchWrites?.IsBusy != true)
            {
                _qsState = s;
                if (_qsDraft == null || !_qsSaving) _qsDraft = s["power"]?["settings"]?.DeepClone() as JsonObject;
            }
        }
        catch (Exception ex) { Log.Warn($"quick panel state: {ex.Message}"); }
        try
        {
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind == RadioKind.WiFi) _qsWifi = r.State == RadioState.On;
                if (r.Kind == RadioKind.Bluetooth) _qsBt = r.State == RadioState.On;
            }
        }
        catch (Exception ex) { Log.Warn($"quick panel radios: {ex.Message}"); }
        if (!_qsLosslessKnown)
        {
            try
            {
                if (await _agent!.Call("get_lossless_settings") is JsonObject ls)
                {
                    _qsLosslessKnown = true;
                    _qsLossless = Str(ls["exe"]) != null ? Bool(ls["running"]) : null;
                }
            }
            catch { _qsLosslessKnown = true; _qsLossless = null; }
        }
        if (v != _qsVersion || !_qsOpen) return;
        QsBuild();
        QsPaint();
    }

    private void QsPollTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object e)
    {
        if (!_qsOpen) { sender.Stop(); return; }
        _ = QsRefresh(_qsVersion);
    }

    private JsonNode? QsGet(string path, JsonNode? root = null)
    {
        JsonNode? n = root ?? _qsState;
        foreach (var part in path.Split('.')) { if (n is not JsonObject o) return null; n = o[part]; }
        return n;
    }

    private double QsNum(string path, double fallback = 0, JsonNode? root = null) =>
        QsGet(path, root) is JsonValue v && (v.TryGetValue(out double d) || (v.TryGetValue(out int i) && (d = i) == i) || (v.TryGetValue(out long l) && (d = l) == l)) ? d : fallback;   // 本地写进去的是 int，后台回来的是 double

    private bool _qsSaving;

    /// <summary>功耗 / 风扇 / 性能读数：改草稿、存回后台（save_power 收整份设置）。</summary>
    private async Task QsSavePower(string what)
    {
        if (_qsDraft == null) return;
        _qsSaving = true;
        try { _agent ??= new AgentClient(); await _agent.Call("save_power", _qsDraft.DeepClone()); }
        catch (Exception ex) { QsFlash($"{what}没有改成：{ex.Message}"); }
        finally { _qsSaving = false; }
        _ = QsRefresh(_qsVersion);
    }

    private async Task QsCall(string what, string method, params JsonNode?[] args)
    {
        try { _agent ??= new AgentClient(); await _agent.Call(method, args); }
        catch (Exception ex) { QsFlash($"{what}没有改成：{ex.Message}"); }
        _ = QsRefresh(_qsVersion);
    }

    /// <summary>只在出错时显示一行（成功了看控件本身的状态就知道，不另写字）。</summary>
    private void QsFlash(string text)
    {
        _qsNote = text;
        _qsNoteAt = NowMs;
        QsPaint();
        long at = _qsNoteAt;
        After(2600, () => { if (_qsNoteAt == at) { _qsNote = null; if (_qsOpen) QsPaint(); } });
    }

    // ---------------- 磁贴配置（qstiles.txt：一行一块「id 大小」） ----------------

    private static string QsTilesFile => Path.Combine(App.CacheDir, "qstiles.txt");

    private static List<(string Id, char Size)> QsLoadTiles()
    {
        try
        {
            if (File.Exists(QsTilesFile))
            {
                var list = new List<(string, char)>();
                foreach (var line in File.ReadAllLines(QsTilesFile))
                {
                    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 2 && QsTilePool.Any(t => t.Id == p[0]) && p[1] is "S" or "M" or "L" && list.All(x => x.Item1 != p[0])) list.Add((p[0], p[1][0]));
                }
                return list;
            }
        }
        catch (Exception ex) { Log.Warn($"quick tiles: {ex.Message}"); }
        return QsTileDefault.ToList();
    }

    private void QsSaveTiles()
    {
        try { File.WriteAllLines(QsTilesFile, (_qsTiles ?? new()).Select(t => $"{t.Id} {t.Size}")); }
        catch (Exception ex) { Log.Warn($"quick tiles save: {ex.Message}"); }
    }

    // ---------------- 排版（定稿 2026-10-30：TE / Braun ET66 式整块分格面板，Braun 配色） ----------------
    // 一列 384 宽、整屏高，细线分格，不加容器、圆角、模糊和阴影。从上到下：
    // 时间电量 56 → 功耗 160（大数字 + 常用档刻度）→ 亮度 | 音量 124（大数字 + 竖条）→ 4×3 格子 → 返回游戏库 | 退出游戏 100。
    // 没开游戏时去掉底行，格子把高度吃掉；只对游戏有效的格子变暗不可选。焦点 = 整格反白；开着 = 右上角芥末黄小方灯。

    private static readonly Color QbBg = Gfx.Rgba(0x17, 0x16, 0x14), QbLine = Gfx.Rgba(0x2c, 0x2a, 0x26), QbFg = Gfx.Rgba(0xec, 0xe6, 0xd6),
        QbAcc = Gfx.Rgba(0xe0, 0xb2, 0x28), QbDanger = Gfx.Rgba(0xff, 0x5a, 0x4e);
    private const float QbStatusH = 56, QbPowerH = 160, QbLevelH = 124, QbLeaveH = 100, QbPad = 18;
    private static readonly string[] QbKeys = { "fps", "osd", "fan", "screenshot", "refresh", "magpie", "windows", "lossless", "keyboard", "mouse", "rumble", "sleep" };

    private string QsTitle() => QsGameItem()?.Title ?? Str(QsGet("system.game.title")) ?? "快捷设置";
    private bool QsInGame => _qsGame != 0 || QsGet("system.game") is JsonObject;
    private bool QsHasGame => _game != null || _qsGame != 0 || _qsTestGame != null;
    private float QsViewH => (float)(View.Height / (_ccK > 0 ? _ccK : (float)(View.Width / 1280)));

    private void QsBuild()
    {
        string? keep = _qsFocusId ?? (_qsFocus < _qsItems.Count ? _qsItems[_qsFocus].Id : null);
        _qsItems.Clear();
        _qsSecTop.Clear();
        var d = _qsDraft;
        float H = QsViewH;
        bool game = QsHasGame;

        var (lo, hi) = QsTdpRange();
        double tdp = QsTdpNow(d, lo);
        _qsItems.Add(new QsItem { Id = "tdp", Kind = QsKind.Meter, Value = $"{tdp:0}", Level = (float)((tdp - lo) / Math.Max(1, hi - lo)), Disabled = d == null, X = 0, Y = QbStatusH, W = QsW, H = QbPowerH });

        float ly = QbStatusH + QbPowerH;
        double bright = QsNum("system.brightness", -1), vol = QsNum("system.volume", -1);
        bool muted = Bool(QsGet("system.muted"));
        _qsItems.Add(new QsItem { Id = "brightness", Kind = QsKind.Slider, Name = "亮度", Value = bright < 0 ? "--" : $"{bright:0}", Level = (float)Math.Clamp(Math.Max(0, bright) / 100, 0, 1), Disabled = bright < 0, X = 0, Y = ly, W = QsW / 2, H = QbLevelH });
        _qsItems.Add(new QsItem { Id = "volume", Kind = QsKind.Slider, Name = "音量", Value = vol < 0 ? "--" : muted ? "静音" : $"{vol:0}", Level = muted ? 0 : (float)Math.Clamp(Math.Max(0, vol) / 100, 0, 1), Disabled = vol < 0, X = QsW / 2, Y = ly, W = QsW / 2, H = QbLevelH });

        float gy = ly + QbLevelH, gb = H - (game ? QbLeaveH : 0), rh = (gb - gy) / 3, cw = QsW / 4;
        for (int i = 0; i < QbKeys.Length; i++)
        {
            var it = QsKeyItem(QbKeys[i]);
            it.X = (i % 4) * cw; it.Y = gy + (i / 4) * rh; it.W = cw; it.H = rh;
            _qsItems.Add(it);
        }
        if (game)
        {
            _qsItems.Add(new QsItem { Id = "library", Kind = QsKind.Entry, Glyph = "", Name = "返回游戏库", X = 0, Y = gb, W = QsW / 2, H = QbLeaveH });
            _qsItems.Add(new QsItem { Id = "quit", Kind = QsKind.Entry, Glyph = "qs.exit", Name = _qsQuitArmedAt > 0 ? "再按一次退出" : "退出游戏", X = QsW / 2, Y = gb, W = QsW / 2, H = QbLeaveH });
        }
        _qsContentH = H;

        int found = keep == null ? -1 : _qsItems.FindIndex(it => it.Id == keep && !it.Disabled);
        if (found < 0) found = 0;
        _qsFocus = found;
        _qsFocusId = _qsItems[_qsFocus].Id;
    }

    /// <summary>格子：值类显示当前值（左上），开关类右上角一盏灯，动作类左上一个图标；名字在左下。</summary>
    private QsItem QsKeyItem(string id)
    {
        var it = new QsItem { Id = id, Kind = QsKind.Tile };
        var d = _qsDraft;
        bool game = QsHasGame;
        switch (id)
        {
            case "fps":
                double fps = QsNum("system.fpsLimit");
                it.Name = "帧率上限"; it.Value = fps > 0 ? $"{fps:0}" : "不限"; it.On = fps > 0; it.Size = 'L'; it.Disabled = QsGet("system.fpsLimit") == null; break;
            case "osd":
                int osd = d != null && Bool(QsGet("osd.enabled", d)) ? (int)QsNum("osd.level", 1, d) : 0;
                it.Name = "性能显示"; it.Value = osd == 0 ? "关" : $"{osd}"; it.On = osd > 0; it.Size = 'L'; it.Disabled = d == null; break;
            case "fan":
                string fan = Str(QsGet("fan.mode", d)) ?? "";
                it.Name = "风扇"; it.Value = QsFan.FirstOrDefault(f => f.Id == fan).N ?? "自动"; it.Disabled = d == null; break;
            case "screenshot": it.Name = "截图"; it.Glyph = ""; it.Disabled = !game; break;
            case "refresh":
                double hz = QsNum("system.refresh");
                it.Name = "刷新率"; it.Value = hz > 0 ? $"{hz:0}" : "--"; it.Disabled = QsRefreshRates().Count < 2; break;
            case "magpie":
                bool mgOk = QsInGame && Bool(QsGet("magpie.available"));
                it.Name = "Magpie"; it.On = mgOk && Bool(QsGet("magpie.enabled")); it.Value = it.On ? "开" : "关"; it.Size = 'L'; it.Disabled = !mgOk; break;
            case "windows": it.Name = "窗口切换"; it.Glyph = ""; it.Disabled = !game; break;
            case "lossless":
                it.Name = "小黄鸭"; it.On = _qsLossless == true; it.Value = it.On ? "开" : "关"; it.Size = 'L'; it.Disabled = _qsLossless == null || !QsInGame; break;
            case "keyboard": it.Name = "屏幕键盘"; it.Glyph = ""; break;
            case "mouse":
                bool desk = Str(QsGet("gamepad.mode")) == "desktop";
                it.Name = "鼠标模式"; it.On = desk; it.Value = desk ? "开" : "关"; it.Size = 'L'; it.Disabled = QsGet("gamepad") is not JsonObject; break;
            case "rumble":
                double rumble = QsGet("rumble.strength") != null ? QsNum("rumble.strength") : 100;
                var rs = QsRumble.OrderBy(r => Math.Abs(r.V - rumble)).First();
                it.Name = "震动"; it.Value = rs.N; break;
            case "sleep": it.Name = "睡眠"; it.Glyph = ""; break;
        }
        // Size 'L' = 带灯的开关类
        return it;
    }

    private double QsTdpNow(JsonObject? d, double lo)
    {
        if (d == null) return lo;
        if (Bool(d["enabled"]) && Str(d["mode"]) == "manual") return QsNum("manual.tdp", lo, d);
        double target = QsNum("power.target.pl1", -1);
        return target > 0 ? target : QsNum("manual.tdp", lo, d);
    }

    private (double Lo, double Hi) QsTdpRange()
    {
        var quick = (QsGet("quick", _qsDraft) as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue(out double x) ? x : 0).Where(x => x > 0).ToList() ?? new();
        double lo = quick.Count > 0 ? Math.Min(5, quick.Min()) : 5, hi = quick.Count > 0 ? Math.Max(35, quick.Max()) : 35;
        return (lo, hi);
    }

    private List<double> QsRefreshRates() =>
        (QsGet("system.refreshRates") as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue(out double x) ? x : 0).Where(x => x >= 60).Distinct().ToList() ?? new();

    private bool QsIsRail(QsItem it) => it.Kind == QsKind.Rail;
    private float QsDrawY(QsItem it) => it.Y;
    private string QsCurrentSection() => _qsPage;
    private void QsEnsureVisible() { }

    // ---------------- 绘制 ----------------

    private void QsPaint()
    {
        if (_qs == null) return;
        float k = _ccK > 0 ? _ccK : (float)(View.Width / 1280), raster = k * Chrome.Dpi;
        float H = (float)(View.Height / k);
        CcSet(_qsPanel, CcDraw(QsW, H, raster, ds => QsPaintPanel(ds, H)));
        _qsPanel.Size = new Vector2(QsW * k, H * k);
        QsLayout();
    }

    private static void QbText(CanvasDrawingSession ds, string text, FontSpec f, float size, float x, float y, Color c, bool bottom = false, float width = 360)
    {
        using var l = TextKit.Layout(ds, text, f, size, width, CanvasHorizontalAlignment.Left);
        var b = l.LayoutBounds;
        ds.DrawTextLayout(l, x - (float)b.X, (bottom ? y - (float)b.Height : y) - (float)b.Y, c);
    }

    private static float QbWidth(CanvasDrawingSession ds, string text, FontSpec f, float size)
    {
        using var l = TextKit.Layout(ds, text, f, size, 600, CanvasHorizontalAlignment.Left);
        return (float)l.LayoutBounds.Width;
    }

    private static FontSpec QbNumFont(string s, ushort w) => s.Any(c => c > 127) ? TextKit.Ui(w) : TextKit.Num(w);

    private void QsPaintPanel(CanvasDrawingSession ds, float H)
    {
        ds.FillRectangle(0, 0, QsW, H, QbBg);
        var fg2 = Gfx.WithAlpha(QbFg, 0.52);

        // 时间、电量
        float sy = QbStatusH / 2;
        using (var tl = TextKit.Layout(ds, DateTime.Now.ToString("HH:mm"), TextKit.Num(500), 15, 120, CanvasHorizontalAlignment.Left))
            ds.DrawTextLayout(tl, QbPad - (float)tl.LayoutBounds.X, sy - (float)tl.LayoutBounds.Height / 2 - (float)tl.LayoutBounds.Y, fg2);
        var bat = SystemInfo.Battery();
        if (bat.HasBattery)
        {
            float bx = QsW - QbPad - 28, by = sy - 6;
            ds.DrawRectangle(bx + 0.75f, by + 0.75f, 24.5f, 10.5f, fg2, 1.5f);
            ds.FillRectangle(bx + 26, by + 3, 2, 6, fg2);
            ds.FillRectangle(bx + 3, by + 3, 20 * Math.Clamp(bat.Percent, 0, 100) / 100f, 6, bat.Charging ? QbAcc : fg2);
            using var pl = TextKit.Layout(ds, $"{bat.Percent}%", TextKit.Num(500), 15, 80, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(pl, bx - 10 - (float)pl.LayoutBounds.Width - (float)pl.LayoutBounds.X, sy - (float)pl.LayoutBounds.Height / 2 - (float)pl.LayoutBounds.Y, fg2);
        }

        for (int i = 0; i < _qsItems.Count; i++) QsDrawCell(ds, _qsItems[i], i == _qsFocus);

        // 细线：横线在每一行的上沿，竖线按格子分
        ds.FillRectangle(0, QbStatusH, QsW, 1, QbLine);
        foreach (var it in _qsItems)
        {
            if (it.Y > QbStatusH) ds.FillRectangle(it.X, it.Y, it.W, 1, QbLine);
            if (it.X > 0) ds.FillRectangle(it.X, it.Y, 1, it.H, QbLine);
        }
        ds.FillRectangle(QsW - 1, 0, 1, H, QbLine);

        // 出错时：顶上状态行换成一行红字条
        if (_qsNote != null)
        {
            ds.FillRectangle(0, 0, QsW, QbStatusH, QbDanger);
            using var nl = TextKit.Layout(ds, _qsNote, TextKit.Ui(600), 15, QsW - 2 * QbPad, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(nl, QbPad - (float)nl.LayoutBounds.X, sy - (float)nl.LayoutBounds.Height / 2 - (float)nl.LayoutBounds.Y, QbBg);
        }
    }

    private void QsDrawCell(CanvasDrawingSession ds, QsItem it, bool focus)
    {
        bool armed = it.Id == "quit" && _qsQuitArmedAt > 0;
        if (armed) ds.FillRectangle(it.X, it.Y, it.W, it.H, QbDanger);
        else if (focus && !it.Disabled) ds.FillRectangle(it.X, it.Y, it.W, it.H, QbFg);
        bool inv = armed || (focus && !it.Disabled);
        Color fg = inv ? QbBg : QbFg, fg2 = inv ? Gfx.WithAlpha(QbBg, 0.56) : Gfx.WithAlpha(QbFg, 0.52), track = inv ? Gfx.WithAlpha(QbBg, 0.16) : Gfx.WithAlpha(QbFg, 0.12);
        float x = it.X + QbPad, y = it.Y + 16, bottom = it.Y + it.H - 14;
        using var layer = ds.CreateLayer(it.Disabled ? 0.26f : 1f);
        switch (it.Kind)
        {
            case QsKind.Meter:
            {
                // 功耗：72 号数字 + W；底下一根轨道，常用档标在轨道下，当前档芥末黄
                QbText(ds, it.Value, TextKit.Num(500), 72, x, y, fg);
                float nw = QbWidth(ds, it.Value, TextKit.Num(500), 72);
                QbText(ds, "W", TextKit.Num(500), 20, x + nw + 6, y + 64, fg2, bottom: true);
                var (lo, hi) = QsTdpRange();
                float rx0 = x, rx1 = it.X + it.W - QbPad, ry = it.Y + it.H - 16 - 34;
                ds.FillRectangle(rx0, ry, rx1 - rx0, 2, track);
                if (!it.Disabled) ds.FillRectangle(rx0, ry, (rx1 - rx0) * Math.Clamp(it.Level, 0, 1), 2, QbAcc);
                double now = lo + it.Level * (hi - lo);
                foreach (var p in new[] { 8.0, 15, 25, 35 })
                {
                    if (p < lo || p > hi) continue;
                    float px = rx0 + (rx1 - rx0) * (float)((p - lo) / (hi - lo));
                    string s = $"{p:0}";
                    float w = QbWidth(ds, s, TextKit.Num(500), 13);
                    px = Math.Clamp(px - w / 2, rx0, rx1 - w);
                    QbText(ds, s, TextKit.Num(500), 13, px, it.Y + it.H - 16, Math.Abs(now - p) < 0.5 ? QbAcc : fg2, bottom: true);
                }
                break;
            }
            case QsKind.Slider:
            {
                // 亮度 / 音量：48 号数字，右边一根竖条从下往上填
                QbText(ds, it.Value, QbNumFont(it.Value, 500), it.Value.Any(c => c > 127) ? 32 : 48, x, y, fg);
                float bx = it.X + it.W - 20 - 6, bt = it.Y + 20, bh = it.H - 40;
                ds.FillRoundedRectangle(bx, bt, 6, bh, 3, 3, track);
                float fh = bh * Math.Clamp(it.Level, 0, 1);
                if (fh > 0) ds.FillRoundedRectangle(bx, bt + bh - fh, 6, fh, 3, 3, fg);
                QbText(ds, it.Name, TextKit.Ui(500), 13, x, bottom, fg2, bottom: true);
                break;
            }
            case QsKind.Tile:
            {
                if (!string.IsNullOrEmpty(it.Glyph)) CcGlyph(ds, it.Glyph, it.X + 16 + 13, it.Y + 18 + 13, 24, fg);
                else QbText(ds, it.Value, QbNumFont(it.Value, 500), it.Value.Any(c => c > 127) ? 20 : 26, x, y, fg, width: it.W - QbPad);
                if (it.Size == 'L') ds.FillRectangle(it.X + it.W - 14 - 7, it.Y + 16, 7, 7, it.On ? QbAcc : track);
                QbText(ds, it.Name, TextKit.Ui(500), 13, x, bottom, fg2, bottom: true, width: it.W - QbPad);
                break;
            }
            case QsKind.Entry:
            {
                CcGlyph(ds, it.Glyph, it.X + 16 + 12, it.Y + 16 + 12, 24, fg);
                QbText(ds, it.Name, TextKit.Ui(600), 17, x, bottom, fg, bottom: true);
                break;
            }
        }
    }

    // ---------------- 逐帧：进出场 + 缩略图 ----------------

    // 进出场帧间隔统计：每段动画走完写一行日志（帧数、最长一帧、超过 25 ms 的帧数），稳定性测试读它
    private int _qsAnimFrames, _qsAnimJanks;
    private long _qsAnimMax;

    private void QsStartTicking()
    {
        _qsFrameAt = NowMs;
        if (_qsTicking) { QsLayout(); return; }
        _qsTicking = true;
        _qsAnimFrames = _qsAnimJanks = 0; _qsAnimMax = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += QsFrame;
        QsLayout();
    }

    private void QsFrame(object? sender, object e)
    {
        long now = NowMs;
        long gap = now - _qsFrameAt;
        if (_qsAnimFrames++ > 0) { _qsAnimMax = Math.Max(_qsAnimMax, gap); if (gap > 25) _qsAnimJanks++; }
        float dt = Math.Clamp(gap / 1000f, 0, 0.05f);
        _qsFrameAt = now;
        // 逐帧趋近（可打断，中途反向从当前位置接着走）；进场略慢、退场略快，曲线对称
        _qsShow += (_qsShowTo - _qsShow) * (1 - MathF.Exp(-dt * (_qsShowTo > _qsShow ? 13 : 16)));
        bool settled = MathF.Abs(_qsShowTo - _qsShow) < 0.002f;
        if (settled) _qsShow = _qsShowTo;
        QsLayout();
        if (!settled) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= QsFrame;
        _qsTicking = false;
        Log.Info($"quick panel anim {(_qsShowTo > 0 ? "in" : "out")} frames={_qsAnimFrames} max={_qsAnimMax}ms janks={_qsAnimJanks}");
        if (!_qsOpen && _qsShow <= 0) QsFinishClose();
    }

    /// <summary>面板 X（设计 px）= -448 → 0；游戏缩略图只画面板右边那一块，位置和游戏窗口原来一样。</summary>
    private void QsLayout()
    {
        if (_qs == null) return;
        float k = _ccK > 0 ? _ccK : (float)(View.Width / 1280), dpi = Chrome.Dpi;
        float ease = 1 - MathF.Pow(1 - _qsShow, 3);
        float x = -QsW + QsW * ease;
        _qsPanel.Offset = new Vector3(x * k, 0, 0);
        _qsPanel.Opacity = _qsShow > 0.001f ? 1 : 0;   // 从屏幕外滑进来，不淡入（淡入那几帧露出的是黑底）
        _qsScrim.Opacity = _qsThumb != null ? 1 : ease;
        if (_qsThumb is not { } th) return;
        int edge = (int)MathF.Round((x + QsW) * k * dpi);
        var (gl, gt, gw, gh) = _qsGameRect;
        int left = Math.Max(edge, gl), right = gl + gw;
        if (right <= left) { th.PlaceCropped(0, 0, 0, 0, 0, 0, 0, 0); return; }
        double sx = (double)th.SourceW / gw, sy = (double)th.SourceH / gh;
        th.PlaceCropped(left, gt, right - left, gh, (int)Math.Round((left - gl) * sx), 0, (int)Math.Round((right - left) * sx), (int)Math.Round(gh * sy));
    }

    // ---------------- 输入 ----------------

    private void QsSetFocus(int i)
    {
        if (_qsFocusId == "quit" && _qsItems[i].Id != "quit") _qsQuitArmedAt = 0;   // 移开焦点 = 取消退出确认
        _qsFocus = i;
        _qsFocusId = _qsItems[i].Id;
        if (!QsIsRail(_qsItems[i])) _qsLastFocus[_qsPage] = _qsFocusId;
    }

    /// <summary>
    /// 手柄导航（评审 2026-10-29）：上下跨行，在下一行里找中心水平距离最近的；左右同一行移动，最左再按左进左栏，
    /// 从左栏按右回到这一页上次的位置；混排磁贴从大磁贴往右进它右侧最上面那块，从那块按左回到原来的大磁贴（记来路）；
    /// 功耗行和滑条有焦点时左右只调值。
    /// </summary>
    private void QsMove(Direction dir)
    {
        if (_qsPwrOpen) { QsPowerMove(dir); return; }
        var cur = _qsItems[_qsFocus];
        // 左栏：上下换页（内容区跟着显示那一页，压暗到 0.5），右 = 进这一页
        if (QsIsRail(cur))
        {
            if (dir == Direction.Right) { QsEnterSection(cur.Section == "power" ? _qsPage : cur.Section); return; }
            if (dir == Direction.Left) { Cue(Sfx.Bump); return; }
            var rail = _qsItems.Where(QsIsRail).ToList();
            int ri = rail.IndexOf(cur) + (dir == Direction.Down ? 1 : -1);
            if (ri < 0 || ri >= rail.Count) { Cue(Sfx.Bump); return; }
            string id = rail[ri].Id;
            if (rail[ri].Section != "power" && rail[ri].Section != _qsPage) { _qsPage = rail[ri].Section; _qsFocusId = id; QsBuild(); }
            QsSetFocus(_qsItems.FindIndex(i => i.Id == id));
            Cue(Sfx.Nav);
            QsPaint();
            return;
        }
        if (dir is Direction.Left or Direction.Right)
        {
            int d = dir == Direction.Right ? 1 : -1;
            if (cur.Kind is QsKind.Slider or QsKind.Meter) { QsNudge(cur, d); return; }
            if (cur.Kind == QsKind.Choice && !cur.Disabled)
            {
                int to = cur.Sub + d;
                if (to >= 0 && to < cur.Options.Length) { cur.Sub = to; Cue(Sfx.Nav); QsPaint(); return; }
                if (to < 0) { QsToRail(); return; }
                Cue(Sfx.Bump); return;
            }
            // 记过来路：从大磁贴往右进来的，按左回到那块大磁贴
            if (dir == Direction.Left && _qsBackLeft != null && _qsItems.FindIndex(i => i.Id == _qsBackLeft) is int back and >= 0)
            {
                _qsBackLeft = null;
                QsSetFocus(back); Cue(Sfx.Nav); QsPaint(); return;
            }
            // 同一行（竖向有重叠）里这个方向上最近的；往右从高的块出来时进右侧最上面那块
            int best = -1; float bestScore = float.MaxValue;
            for (int i = 0; i < _qsItems.Count; i++)
            {
                var it = _qsItems[i];
                if (i == _qsFocus || QsIsRail(it) || it.Disabled || it.Y >= cur.Y + cur.H || it.Y + it.H <= cur.Y) continue;
                float dx = d > 0 ? it.X - (cur.X + cur.W) : cur.X - (it.X + it.W);
                if (dx < -1) continue;
                float score = dx * 10 + it.Y;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best < 0)
            {
                if (dir == Direction.Left) { QsToRail(); return; }
                Cue(Sfx.Bump); return;
            }
            _qsBackLeft = d > 0 && cur.H > _qsItems[best].H + 1 ? cur.Id : null;
            QsSetFocus(best); Cue(Sfx.Nav); QsPaint();
            return;
        }
        // 上下：下一行（顶边离得最近的那一排）里中心水平距离最近的
        _qsBackLeft = null;
        float cx = cur.X + cur.W / 2;
        var cands = _qsItems.Select((it, i) => (it, i)).Where(p => !QsIsRail(p.it) && !p.it.Disabled && p.i != _qsFocus &&
            (dir == Direction.Down ? p.it.Y >= cur.Y + cur.H - 1 : p.it.Y + p.it.H <= cur.Y + 1)).ToList();
        if (cands.Count == 0) { Cue(Sfx.Bump); return; }
        float edge = dir == Direction.Down ? cands.Min(p => p.it.Y) : cands.Max(p => p.it.Y + p.it.H);
        var row = cands.Where(p => dir == Direction.Down ? p.it.Y <= edge + 4 : p.it.Y + p.it.H >= edge - 4).ToList();
        var pick = row.OrderBy(p => MathF.Abs(p.it.X + p.it.W / 2 - cx)).First();
        if (pick.it.Kind == QsKind.Choice) pick.it.Sub = Math.Max(0, pick.it.Choice);
        QsSetFocus(pick.i);
        Cue(Sfx.Nav);
        QsPaint();
    }

    private void QsToRail()
    {
        int i = _qsItems.FindIndex(it => it.Id == "rail:" + _qsPage);
        if (i < 0) { Cue(Sfx.Bump); return; }
        _qsBackLeft = null;
        QsSetFocus(i);
        Cue(Sfx.Nav);
        QsPaint();
    }

    /// <summary>进某一页：焦点回到这一页上次的位置（首页默认在功耗行，子页默认第一行）。</summary>
    private void QsEnterSection(string sec)
    {
        _qsPage = sec;
        // 目标先记在本地：QsBuild 找不到 _qsFocusId 时会退回旧下标（可能是左栏）
        string? want = _qsLastFocus.GetValueOrDefault(sec) ?? (sec == "now" ? "tdp" : null);
        _qsFocusId = want;
        QsBuild();
        int i = want != null ? _qsItems.FindIndex(it => it.Id == want) : -1;
        if (i < 0) i = _qsItems.FindIndex(it => !QsIsRail(it));
        if (i < 0) { Cue(Sfx.Bump); QsPaint(); return; }
        var it2 = _qsItems[i];
        if (it2.Kind == QsKind.Choice) it2.Sub = Math.Max(0, it2.Choice);
        QsSetFocus(i);
        Cue(Sfx.Nav);
        QsPaint();
    }

    /// <summary>LB / RB：只有一页，没有可翻的。</summary>
    internal void QsTab(int delta) => Cue(Sfx.Bump);

    /// <summary>B：先取消确认 / 收起电源 / 退出编辑，再收起面板。</summary>
    private void QsBack()
    {
        if (_qsQuitArmedAt > 0) { _qsQuitArmedAt = 0; Cue(Sfx.Back); QsBuild(); QsPaint(); return; }
        if (_qsPwrOpen) { _qsPwrOpen = false; _qsPwrArmed = null; Cue(Sfx.Back); QsPaint(); return; }
        if (_qsEditing) { _qsEditing = false; QsSaveTiles(); _qsFocusId = "edit"; Cue(Sfx.Back); QsBuild(); QsPaint(); return; }
        CloseQuickPanel();
    }


    /// <summary>滑条 / 功耗 ← →：亮度、音量 5 一格，功耗 1 W 一格。马上发给后台，本地先动。</summary>
    private void QsNudge(QsItem it, int d)
    {
        if (it.Disabled) { Cue(Sfx.Bump); return; }
        switch (it.Id)
        {
            case "brightness" or "volume":
            {
                string path = it.Id;
                double v = Math.Clamp(Math.Round(QsNum("system." + path) / 5) * 5 + d * 5, 0, 100);
                if (_qsState?["system"] is JsonObject sys) { sys[path] = v; if (it.Id == "volume") sys["muted"] = false; }
                Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown);
                _ = QsCall(it.Id == "brightness" ? "亮度" : "音量", it.Id == "brightness" ? "set_brightness" : "set_volume", JsonValue.Create(v));
                break;
            }
            case "tdp":
            {
                if (_qsDraft == null) return;
                var (lo, hi) = QsTdpRange();
                double v = Math.Clamp(Math.Round(QsTdpNow(_qsDraft, lo)) + d, lo, hi);
                QsSetTdp(v);
                Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown);
                _ = QsSavePower("功耗");
                break;
            }
        }
        QsBuild();
        QsPaint();
    }

    /// <summary>功耗只有一个设置值：改它就是手动功耗。</summary>
    private void QsSetTdp(double v)
    {
        if (_qsDraft is not { } d) return;
        d["enabled"] = true; d["mode"] = "manual";
        if (d["manual"] is not JsonObject m) { m = new JsonObject(); d["manual"] = m; }
        m["tdp"] = v;
    }

    private void QsActivate()
    {
        if (_qsPwrOpen) { QsPowerActivate(); return; }
        var it = _qsItems[_qsFocus];
        if (QsIsRail(it))
        {
            if (it.Section == "power") { _qsPwrOpen = true; _qsPwrFocus = 1; _qsPwrArmed = null; Cue(Sfx.MenuIn); QsPaint(); return; }
            QsEnterSection(it.Section);
            return;
        }
        if (_qsEditing && it.Kind == QsKind.Tile) { QsEditTile(it); return; }
        if (it.Disabled) { Cue(Sfx.Bump); return; }
        switch (it.Kind)
        {
            case QsKind.Choice: QsChoose(it, it.Sub); break;
            case QsKind.Slider when it.Id == "volume": QsChoose(new QsItem { Id = "mute", Kind = QsKind.Choice }, Bool(QsGet("system.muted")) ? 0 : 1); return;
            case QsKind.Slider or QsKind.Meter: QsNudge(it, 1); return;
            default: QsRun(it.Id); return;
        }
        Cue(Sfx.Activate);
        QsBuild();
        QsPaint();
    }

    /// <summary>几选一：选第 i 项。</summary>
    private void QsChoose(QsItem it, int i)
    {
        var d = _qsDraft;
        switch (it.Id)
        {
            case "mode" when d != null:
                if (i == 0) d["enabled"] = false;
                else if (i == 1) { d["enabled"] = true; d["mode"] = "autotdp"; }
                else { var (lo, _) = QsTdpRange(); QsSetTdp(Math.Round(QsTdpNow(d, lo))); }
                _ = QsSavePower("性能模式");
                break;
            case "fps":
            {
                int next = QsFps[i];
                if (_qsState?["system"] is JsonObject sys) sys["fpsLimit"] = next;
                _ = QsCall("帧率上限", "set_fps_limit", JsonValue.Create(next));
                break;
            }
            case "refresh":
            {
                var rates = QsRefreshRates();
                if (i >= rates.Count) return;
                if (_qsState?["system"] is JsonObject sys) sys["refresh"] = rates[i];
                _ = QsCall("刷新率", "set_refresh_rate", JsonValue.Create(rates[i]));
                break;
            }
            case "magpie":
                if ((i == 1) != Bool(QsGet("magpie.enabled")))
                {
                    if (_qsState?["magpie"] is JsonObject mg) mg["enabled"] = i == 1;
                    _ = QsCall("画面缩放", "toggle_magpie");
                }
                break;
            case "lossless":
                _qsLossless = i == 1;
                _ = QsCall("小黄鸭", "set_lossless_running", JsonValue.Create(i == 1));
                break;
            case "osd" when d != null:
            {
                if (d["osd"] is not JsonObject osd) { osd = new JsonObject(); d["osd"] = osd; }
                osd["enabled"] = i > 0;
                if (i > 0) osd["level"] = i;
                _ = QsSavePower("性能读数");
                break;
            }
            case "mute":
                if (_qsState?["system"] is JsonObject s2) s2["muted"] = i == 1;
                _ = QsCall("静音", "set_mute", JsonValue.Create(i == 1));
                break;
            case "fan" when d != null:
            {
                if (d["fan"] is not JsonObject fan) { fan = new JsonObject(); d["fan"] = fan; }
                fan["mode"] = QsFan[i].Id;
                _ = QsSavePower("风扇");
                break;
            }
            case "rumble":
                if (_qsState?["rumble"] is JsonObject ru) ru["strength"] = QsRumble[i].V;
                _ = QsCall("震动", "set_rumble_strength", JsonValue.Create(QsRumble[i].V));
                break;
            case "wifi" or "bluetooth":
                if ((it.Id == "wifi" ? _qsWifi : _qsBt) != (i == 1)) _ = QsRadio(it.Id == "wifi" ? RadioKind.WiFi : RadioKind.Bluetooth);
                break;
        }
        Cue(Sfx.Activate);
        QsBuild();
        QsPaint();
    }

    /// <summary>磁贴、入口、系统行：按一下。</summary>
    private void QsRun(string id)
    {
        var d = _qsDraft;
        switch (id)
        {
            case "edit":
                _qsEditing = true; Cue(Sfx.Activate); QsBuild(); QsPaint(); return;
            case "magpie": QsChoose(new QsItem { Id = "magpie", Kind = QsKind.Choice }, Bool(QsGet("magpie.enabled")) ? 0 : 1); return;
            case "lossless": QsChoose(new QsItem { Id = "lossless", Kind = QsKind.Choice }, _qsLossless == true ? 0 : 1); return;
            case "osd" when d != null:
            {
                int cur = Bool(QsGet("osd.enabled", d)) ? (int)QsNum("osd.level", 1, d) : 0;
                QsChoose(new QsItem { Id = "osd", Kind = QsKind.Choice }, (cur + 1) % 5); return;
            }
            case "fan" when d != null:
            {
                int cur = Array.FindIndex(QsFan, f => f.Id == (Str(QsGet("fan.mode", d)) ?? ""));
                QsChoose(new QsItem { Id = "fan", Kind = QsKind.Choice }, (cur + 1) % QsFan.Length); return;
            }
            case "fps":
            {
                int cur = Array.IndexOf(QsFps, (int)QsNum("system.fpsLimit"));
                QsChoose(new QsItem { Id = "fps", Kind = QsKind.Choice }, (cur + 1) % QsFps.Length); return;
            }
            case "refresh":
            {
                var rates = QsRefreshRates();
                if (rates.Count < 2) { Cue(Sfx.Bump); return; }
                QsChoose(new QsItem { Id = "refresh", Kind = QsKind.Choice }, (rates.IndexOf(QsNum("system.refresh")) + 1) % rates.Count); return;
            }
            case "rumble":
            {
                double cur = QsGet("rumble.strength") != null ? QsNum("rumble.strength") : 100;
                int i = Array.IndexOf(QsRumble, QsRumble.OrderBy(r => Math.Abs(r.V - cur)).First());
                QsChoose(new QsItem { Id = "rumble", Kind = QsKind.Choice }, (i + 1) % QsRumble.Length); return;
            }
            case "wifi": _ = QsRadio(RadioKind.WiFi); Cue(Sfx.Activate); return;
            case "bluetooth": _ = QsRadio(RadioKind.Bluetooth); Cue(Sfx.Activate); return;
            case "keyboard":
                CloseQuickPanel();
                _ = QsCall("屏幕键盘", "toggle_keyboard");
                return;
            case "screenshot":
                // 先收起面板再截，截到的是游戏
                CloseQuickPanel();
                After(500, () => _ = QsCall("截图", "screenshot"));
                return;
            case "windows":
                CloseQuickPanel(hideWindow: false, instant: true);
                OpenSwitcherFromAnywhere();
                return;
            case "mode":
                CloseQuickPanel(hideWindow: false, instant: true);
                _ = CcSwitchMode();
                return;
            case "mouse":
            {
                string next = Str(QsGet("gamepad.mode")) == "desktop" ? "xinput" : "desktop";
                if (_qsState?["gamepad"] is JsonObject gp) gp["mode"] = next;
                Cue(Sfx.Activate);
                if (next == "desktop")
                {
                    // 切成鼠标后手柄就不再驱动面板：先收起，回到游戏再切
                    CloseQuickPanel();
                    After(400, () => _ = QsCall("鼠标模式", "set_gamepad_mode", JsonValue.Create(next)));
                    return;
                }
                _ = QsCall("鼠标模式", "set_gamepad_mode", JsonValue.Create(next));
                QsBuild(); QsPaint();
                return;
            }
            case "sleep":
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                Log.Info("quick panel: sleep");
                CloseQuickPanel(hideWindow: false, instant: true);
                if (!Program.Args.Contains("--power-dry")) After(300, () => SystemInfo.Sleep(hwnd));
                return;
            }
            case "library":
                // 留在游戏库：面板收起、窗口不退下（游戏留在后面）
                _nowPlayingDismissed = true;
                CloseQuickPanel(hideWindow: false);
                return;
            case "quit":
                // 第一次按 A：原位变红、写「再按一次退出」；3 秒内再按才退出，B 或移开焦点取消
                if (_qsQuitArmedAt == 0 || NowMs - _qsQuitArmedAt > 3000)
                {
                    _qsQuitArmedAt = NowMs;
                    long at = _qsQuitArmedAt;
                    After(3000, () => { if (_qsQuitArmedAt == at) { _qsQuitArmedAt = 0; if (_qsOpen) { QsBuild(); QsPaint(); } } });
                    Cue(Sfx.ModalShow);
                    QsBuild(); QsPaint();
                    return;
                }
                _qsQuitArmedAt = 0;
                if (_game is { } g) QuitGame(g);
                else if (_qsGame != 0) PostMessageQs(_qsGame, 0x0010, 0, 0);   // WM_CLOSE：像点右上角 × 一样请游戏自己关
                CloseQuickPanel(hideWindow: false);
                return;
        }
    }

    /// <summary>编辑磁贴：A 换大小（小 → 中 → 大 → 拿下），虚线下面的按 A 加回来（小）；LB / RB 挪位置；B 完成。</summary>
    private void QsEditTile(QsItem it)
    {
        if (it.Id == "edit") { _qsEditing = false; QsSaveTiles(); Cue(Sfx.Back); QsBuild(); QsPaint(); return; }
        _qsTiles ??= QsTileDefault.ToList();
        if (it.Id.StartsWith("spare:"))
        {
            string id = it.Id["spare:".Length..];
            _qsTiles.Add((id, 'S'));
            _qsFocusId = id;
        }
        else
        {
            int at = _qsTiles.FindIndex(t => t.Id == it.Id);
            if (at < 0) return;
            char next = _qsTiles[at].Size switch { 'S' => 'M', 'M' => 'L', _ => 'x' };
            if (next == 'x') { _qsTiles.RemoveAt(at); _qsFocusId = "spare:" + it.Id; }
            else _qsTiles[at] = (it.Id, next);
        }
        Cue(Sfx.Activate);
        QsBuild();
        QsPaint();
    }

    // ---------------- 电源（沿左栏往上滑出的圆） ----------------

    private List<(string Id, string Glyph, string Name, bool Danger)> QsPowerItems()
    {
        var list = new List<(string, string, string, bool)>();
        if (ShellMode.Available) list.Add(("mode", "qs.mode", ShellMode.ExplorerRunning() ? "游戏模式" : "桌面模式", false));
        list.Add(("sleep", "\uE708", "睡眠", false));
        list.Add(("restart", "\uE777", "重启", true));
        list.Add(("shutdown", "\uE7E8", "关机", true));
        return list;
    }

    private void QsPowerMove(Direction dir)
    {
        int n = QsPowerItems().Count;
        int to = _qsPwrFocus + (dir == Direction.Up ? 1 : dir == Direction.Down ? -1 : 0);
        if (dir is Direction.Left or Direction.Right || to < 1 || to > n) { Cue(Sfx.Bump); return; }
        _qsPwrFocus = to;
        _qsPwrArmed = null;
        Cue(Sfx.Nav);
        QsPaint();
    }

    /// <summary>A：睡眠 / 切模式直接做；重启、关机先在原位变红写明后果，再按一次才做。</summary>
    private void QsPowerActivate()
    {
        var items = QsPowerItems();
        var (id, _, _, danger) = items[_qsPwrFocus - 1];
        if (danger && _qsPwrArmed != id) { _qsPwrArmed = id; Cue(Sfx.ModalShow); QsPaint(); return; }
        _qsPwrOpen = false; _qsPwrArmed = null;
        Log.Info($"quick panel power: {id}");
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        switch (id)
        {
            case "mode":
                CloseQuickPanel(hideWindow: false, instant: true);
                _ = CcSwitchMode();
                return;
            case "sleep":
                CloseQuickPanel(hideWindow: false, instant: true);
                if (!Program.Args.Contains("--power-dry")) After(300, () => SystemInfo.Sleep(hwnd));
                return;
            default:
                CloseQuickPanel(hideWindow: false, instant: true);
                if (!Program.Args.Contains("--power-dry")) _ = CcRequestAnimatedPower(id);
                return;
        }
    }

    private async Task QsRadio(RadioKind kind)
    {
        string name = kind == RadioKind.WiFi ? "Wi-Fi" : "蓝牙";
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) { QsFlash($"{name}没有切换：没有权限"); return; }
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind != kind) continue;
                bool on = r.State != RadioState.On;
                if (kind == RadioKind.WiFi) _qsWifi = on; else _qsBt = on;
                QsBuild(); QsPaint();
                var result = await r.SetStateAsync(on ? RadioState.On : RadioState.Off);
                if (result != RadioAccessStatus.Allowed) QsFlash($"{name}没有切换");
                break;
            }
        }
        catch (Exception ex) { QsFlash($"{name}没有切换：{ex.Message}"); }
        _ = QsRefresh(_qsVersion);
    }

    // ---------------- 触控 / 鼠标 ----------------

    private QsItem? QsItemAt(double x, double y)
    {
        if (_qsPwrOpen)
        {
            var power = _qsItems.First(i => i.Id == "rail:power");
            int n = QsPowerItems().Count;
            for (int i = 1; i <= n; i++)
                if (x >= power.X && x < power.X + 56 && y >= power.Y - i * 64 && y < power.Y - i * 64 + 56) return new QsItem { Id = "pwr:" + i, Kind = QsKind.Rail };
        }
        return _qsItems.FirstOrDefault(it => x >= it.X && x < it.X + it.W && y >= QsDrawY(it) && y < QsDrawY(it) + it.H);
    }

    private void QsClick(double sx, double sy)
    {
        float k = _ccK > 0 ? _ccK : (float)(View.Width / 1280);
        double x = sx / k, y = sy / k;
        if (x > QsW) { CloseQuickPanel(); return; }   // 点到游戏那边 = 收起
        var it = QsItemAt(x, y);
        if (it == null) { if (_qsPwrOpen) { _qsPwrOpen = false; _qsPwrArmed = null; QsPaint(); } return; }
        if (it.Id.StartsWith("pwr:"))
        {
            int n = int.Parse(it.Id[4..]);
            if (_qsPwrFocus != n) _qsPwrArmed = null;
            _qsPwrFocus = n;
            QsPowerActivate();
            return;
        }
        if (_qsPwrOpen) { _qsPwrOpen = false; _qsPwrArmed = null; }
        int idx = _qsItems.IndexOf(it);
        _qsFocus = idx; _qsFocusId = it.Id;
        if (it.Kind is QsKind.Slider or QsKind.Meter && !it.Disabled)
        {
            QsSetLevel(it, QsLevelAt(it, x));
            return;
        }
        if (it.Kind == QsKind.Choice && !it.Disabled)
        {
            // 点到哪一项选哪一项（按画的位置反推）
            int sub = QsChoiceAt(it, x);
            if (sub >= 0) { it.Sub = sub; QsChoose(it, sub); }
            return;
        }
        QsActivate();
    }

    /// <summary>触控：功耗按轨道位置；亮度、音量在整格里横着拖。</summary>
    private float QsLevelAt(QsItem it, double x) => it.Kind == QsKind.Meter
        ? (float)((x - (it.X + QbPad)) / Math.Max(1, it.W - 2 * QbPad))
        : (float)((x - (it.X + QbPad)) / Math.Max(1, it.W - 2 * QbPad));

    private int QsChoiceAt(QsItem it, double x)
    {
        using var dev = new CanvasRenderTarget(G.Device, 1, 1, 96);
        var widths = it.Options.Select(o => { using var l = TextKit.Layout(dev, o, TextKit.Ui(500), 18, 120, CanvasHorizontalAlignment.Left); return (float)l.LayoutBounds.Width; }).ToList();
        float nw; using (var nm = TextKit.Layout(dev, it.Name, TextKit.Ui(500), 18, 160, CanvasHorizontalAlignment.Left)) nw = (float)nm.LayoutBounds.Width;
        var (pad, gap) = QsChoiceFit(widths.Sum(), widths.Count, it.W - nw - 20);
        float ox = it.X + it.W - widths.Sum(w => w + 2 * pad) - gap * (widths.Count - 1);
        for (int i = 0; i < widths.Count; i++) { float bw = widths[i] + 2 * pad; if (x >= ox - gap / 2 && x < ox + bw + gap / 2) return i; ox += bw + gap; }
        return -1;
    }

    /// <summary>选项放不下时收窄选项内边距和间距，不压到左边的名字。</summary>
    private static (float pad, float gap) QsChoiceFit(float textW, int n, float avail)
    {
        float pad = 11, gap = 6;
        while (pad > 6 && textW + 2 * pad * n + gap * (n - 1) > avail) { pad -= 1; gap = Math.Max(2, gap - 1); }
        return (pad, gap);
    }

    private void QsSetLevel(QsItem it, float level)
    {
        level = Math.Clamp(level, 0, 1);
        if (it.Disabled) return;
        if (it.Id is "brightness" or "volume")
        {
            double v = Math.Round(level * 100);
            if (_qsState?["system"] is JsonObject sys) { sys[it.Id] = v; if (it.Id == "volume") sys["muted"] = false; }
            QsQueueTouchValue(it.Id == "volume" ? "set_volume" : "set_brightness", JsonValue.Create(v));
        }
        else if (it.Id == "tdp" && _qsDraft != null)
        {
            var (lo, hi) = QsTdpRange();
            double v = Math.Round(lo + (hi - lo) * level);
            // 靠近常用档就吸过去
            foreach (var snap in new[] { 8.0, 15, 25, 35 }) if (Math.Abs(v - snap) <= 1) v = snap;
            QsSetTdp(v);
            QsQueueTouchValue("save_power", _qsDraft.DeepClone());
        }
        QsBuild(); QsPaint();
    }

    private SettingsWriteQueue? _qsTouchWrites;
    // In-memory transport is used only by the opt-in input probe.
    private Func<string, JsonNode, Task>? _qsTouchTransport;
    private Task _qsTouchPending = Task.CompletedTask;
    private void QsQueueTouchValue(string method, JsonNode value)
    {
        _qsTouchWrites ??= new SettingsWriteQueue(ex => QsFlash(ex.Message), () => { if (_qsOpen) _ = QsRefresh(_qsVersion); });
        var transport = _qsTouchTransport;
        _qsTouchPending = _qsTouchWrites.Enqueue(method, 60, async () =>
        {
            if (transport != null) { await transport(method, value); return; }
            _agent ??= new AgentClient();
            await _agent.Call(method, value);
        });
    }

    private QsItem? QsTouchItemAt(double sx, double sy)
    {
        float k = TouchScale;
        return QsItemAt(sx / k, sy / k);
    }

    private void QsTouchDrag(string id, double sx)
    {
        var it = _qsItems.FirstOrDefault(item => item.Id == id);
        if (it == null || it.Disabled || it.Kind is not (QsKind.Slider or QsKind.Meter)) return;
        _qsFocus = _qsItems.IndexOf(it); _qsFocusId = id;
        QsSetLevel(it, QsLevelAt(it, sx / TouchScale));
    }

    // ---------------- 自测 ----------------

    /// <summary>自测用（--state=qs / qsnav）：在游戏库里打开快捷设置，挨个走一遍焦点。</summary>
    internal void QuickPanelTest(int steps)
    {
        if (!_qsOpen) OpenQuickPanel(0);
        var dirs = new[] { Direction.Down, Direction.Down, Direction.Right, Direction.Down, Direction.Down, Direction.Down, Direction.Down, Direction.Down, Direction.Left, Direction.Left, Direction.Down, Direction.Up };
        for (int i = 0; i < steps; i++) { int s = i; After(300 * (s + 1), () => { if (_qsOpen) QsMove(dirs[s % dirs.Length]); }); }
    }

    internal string QsFocusTest => $"{_qsPage}/{_qsFocusId}";

    internal void QuickPanelTestClose() => CloseQuickPanel(hideWindow: false);

    /// <summary>自测用（--state=qsapply）：焦点放到某个磁贴上按一次 A，日志里记下按之前 / 之后后台报的值。</summary>
    internal void QuickPanelTestPress(string id)
    {
        int i = _qsItems.FindIndex(it => it.Id == id);
        if (i < 0) { Log.Info($"quick panel test: no {id}"); return; }
        string before = _qsItems[i].Value;
        _qsFocus = i; _qsFocusId = id;
        QsActivate();
        int j = _qsItems.FindIndex(it => it.Id == id);
        Log.Info($"quick panel test press {id}: {before} -> {(j >= 0 ? _qsItems[j].Value : "?")}");
    }

    internal string QuickPanelTestValue(string id) => _qsItems.FirstOrDefault(it => it.Id == id)?.Value ?? "?";

    /// <summary>自测用：按名字把焦点 / 状态摆好（qsfocus=<id>、qspower、qsarm、qsedit、qssec=<分区>）。</summary>
    internal void QuickPanelTestState(string what)
    {
        if (!_qsOpen) OpenQuickPanel(0);
        _qsPwrOpen = false; _qsPwrArmed = null;
        if (what == "power" || what == "arm") { _qsPwrOpen = true; _qsPwrFocus = what == "arm" ? QsPowerItems().Count : 1; if (what == "arm") _qsPwrArmed = "shutdown"; }
        else if (what == "edit") { _qsEditing = true; QsBuild(); int e = _qsItems.FindIndex(i => i.Id == "osd"); if (e >= 0) QsSetFocus(e); }
        else if (what.StartsWith("sec=")) { _qsPage = what[4..]; _qsFocusId = "rail:" + _qsPage; QsBuild(); int r = _qsItems.FindIndex(i => i.Id == "rail:" + _qsPage); if (r >= 0) QsSetFocus(r); }
        else if (what.StartsWith("page=")) { QsEnterSection(what[5..]); }
        else if (what == "quit") { int q = _qsItems.FindIndex(i => i.Id == "quit"); if (q >= 0) { QsSetFocus(q); _qsQuitArmedAt = NowMs; QsBuild(); } }
        else { int f = _qsItems.FindIndex(i => i.Id == what); if (f >= 0) { var it = _qsItems[f]; if (it.Kind == QsKind.Choice) it.Sub = Math.Max(0, it.Choice); QsSetFocus(f); } }
        QsPaint();
    }

    [StructLayout(LayoutKind.Sequential)] private struct QsRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out QsRect r);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessageQs(nint hwnd, uint msg, nint wp, nint lp);
}
