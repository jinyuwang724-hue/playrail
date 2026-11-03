// 首次启动引导：整屏盖在墙上，背景是胶片封面墙，全程手柄。五屏：
//   0 你好 —— 机型、问候、语言（默认跟系统），A 开始
//   1 账户 —— 平台卡：Steam 走「绑定 Steam 账户」（浏览器里 Steam 官方 OpenID），Xbox 和以后的平台占位
//   2 主页 —— 全屏体验主页三选一 + 几项基本设置
//   3 准备 —— 后台、风扇与功耗、小组件、停用其他前端、游戏库、封面，一个个节点亮起来
//   4 完成 —— 一切就绪，A 进游戏库
// 背景墙先用随包的通用封面（Assets\OobeWall，Steam 代表作）；Steam 绑好、封面同步好以后换成用户自己的墙。
// 字体 Motiva Sans（Assets\Fonts），中文回退微软雅黑。
// 不写说明文字、不放按键提示条：层级靠字号、焦点反白、主按钮里的 A 键帽。
// 新装（第一次启动时还没有 ui.ini）自动出现；设置 › 维护 › 重新运行首次设置；--oobe 强制打开。
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.Win32;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    public const int OobeVersion = 2;
    private const string OwnHomeApp = "ClawToolbox.Library_ba3r68zr466za!App";
    private const string XboxHomeApp = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";
    // 设计稿按 1920×1200 物理像素
    private const float ObMargin = 168, ObPad = 18;
    private static readonly FontSpec ObIcons = new("Segoe Fluent Icons", 400);
    private static readonly Color ObInk = Gfx.Rgba(9, 9, 11);
    private static readonly Color ObRed = Gfx.Rgba(0xfd, 0x5c, 0x47);
    private static readonly (string Code, string Name)[] ObLangs = { ("zh-CN", "简体中文"), ("en-US", "English") };

    private enum ObStep { Welcome, Account, Home, Prepare, Done }
    private enum ObState { Pending, Running, Done, Failed }

    private sealed class ObLive
    {
        public required SpriteVisual Sprite;
        public float W, H;
        public required Action<CanvasDrawingSession> Paint;
    }

    private sealed class ObTask
    {
        public required string Glyph;
        public required Func<string> Label;
        public required Func<Task<bool>> Run;
        public ObState State;
        public Func<float>? Fraction;
    }

    private ContainerVisual? _ob;
    private Backdrop? _obBack;
    private SpriteVisual? _obShade;
    private ContainerVisual? _obPage;
    private float _obK = 1, _obW = 1920, _obH = 1200;
    private ObStep _obStep;
    private int _obFocus, _obHome, _obLang;
    private bool _obLoop, _obClosing, _obUserWall, _obPrepared;
    private long _obLast;
    private readonly List<ObLive> _obLive = new();
    private readonly List<(SpriteVisual Sprite, int Index)> _obCards = new();
    private readonly List<SpriteVisual> _obSpinners = new();
    private readonly List<(Windows.Foundation.Rect R, Action Hit)> _obHits = new();
    private List<ObTask> _obTasks = new();
    private TaskCompletionSource<bool>? _obAgentReady;
    // Steam 账户状态：0 没绑定、4 等浏览器、1 绑了没装 Steam、2 绑了本机没这个号、3 就绪
    private int _obSteam = -1;
    private CanvasBitmap? _obAvatar;
    private (string Model, bool Claw) _obDevice;

    public bool OobeOpen => _ob != null;
    private bool ShouldShowOobe() => Program.Args.Contains("--oobe") || (UiPrefs.FreshInstall && _prefs.Oobe < OobeVersion);
    private bool ObEn => _obLang == 1;
    private string L(string zh, string en) => ObEn ? en : zh;
    private static FontSpec ObFont(ushort w) => TextKit.Deck(w);

    // ---------------- 打开 / 关闭 ----------------

    private void OpenOobe()
    {
        if (_ob != null) return;
        _obDevice = ObReadDevice();
        Log.Info($"oobe: open on {_obDevice.Model} (claw={_obDevice.Claw})");
        _obStep = ObStep.Welcome;
        _obFocus = 0;
        _obHome = _prefs.FseSteam ? 1 : 0;
        _obLang = _prefs.Lang.Length > 0 ? Math.Max(0, Array.FindIndex(ObLangs, l => l.Code == _prefs.Lang))
            : CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        _obClosing = _obUserWall = _obPrepared = false;
        _obSteam = -1;
        _obTasks = ObBuildTasks();
        _obAgentReady = new TaskCompletionSource<bool>();
        _ob = G.Container();
        _ = ObConnectAgentAsync();
        _top.Children.InsertAtTop(_ob);
        _obBack = new Backdrop(G) { Dpi = Chrome.Dpi };
        _obBack.Configure(_backgroundSettings with { Kind = BackgroundKind.Filmstrip });
        _obBack.SetFilmCovers(ObGenericCovers(), a => _dq.TryEnqueue(() => { a(); ObBackLoop(); }));
        _obShade = G.Sprite(G.LinearGradient(90, View.Width, View.Height,
            (0, Gfx.Rgba(0, 0, 0, 0.88)), (0.55, Gfx.Rgba(0, 0, 0, 0.55)), (1, Gfx.Rgba(0, 0, 0, 0.2))));
        _ob.Children.InsertAtTop(_obBack.Root);
        _ob.Children.InsertAtTop(_obShade);
        ObMeasure();
        _obBack.InstantColors = true;
        _obBack.SetCover(null, a => _dq.TryEnqueue(() => { a(); ObBackLoop(); }));
        ObBackRun();
        ObShowPage(ObStep.Welcome, 0);
    }

    /// <summary>通用墙：随包的 Steam 代表作封面（按 appid 固定洗牌，每次一样）。</summary>
    private static string[] ObGenericCovers()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "OobeWall");
            return Directory.GetFiles(dir, "*.jpg").OrderBy(f => long.TryParse(Path.GetFileNameWithoutExtension(f), out var v) ? v * 7919 % 1009 : 0).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>用户自己的封面够铺一面墙了（12 张以上）就换过去。</summary>
    private void ObMaybeUseUserWall()
    {
        if (_obUserWall || _obBack == null) return;
        var mine = FilmCoverPaths();
        if (mine.Length < 12) return;
        _obUserWall = true;
        Log.Info($"oobe: wall switched to user covers ({mine.Length})");
        _obBack.SetFilmCovers(mine, a => _dq.TryEnqueue(() => { a(); ObBackLoop(); }));
    }

    private static (string Model, bool Claw) ObReadDevice()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string maker = (k?.GetValue("SystemManufacturer") as string ?? "").Trim();
            string product = (k?.GetValue("SystemProductName") as string ?? "").Trim();
            bool msi = maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || maker.StartsWith("MSI", StringComparison.OrdinalIgnoreCase);
            bool claw = msi && product.Contains("Claw", StringComparison.OrdinalIgnoreCase);
            string brand = msi ? "MSI" : maker.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string model = product.Length == 0 ? brand : product.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? product : $"{brand} {product}".Trim();
            return (model.Length > 0 ? model : Environment.MachineName, claw);
        }
        catch { return (Environment.MachineName, false); }
    }

    private void ObMeasure()
    {
        float vw = (float)View.Width, vh = (float)View.Height;
        _obK = MathF.Min(vw * Chrome.Dpi / 1920f, vh * Chrome.Dpi / 1200f) / Chrome.Dpi;
        _obW = vw / _obK;
        _obH = vh / _obK;
        _obBack!.Dpi = Chrome.Dpi;
        _obBack.BlurDip = 0;
        _obBack.Build(vw, vh);
        _obBack.SetStrip(0, vw);
    }

    private void ObResize()
    {
        if (_ob == null) return;
        ObMeasure();
        ObShowPage(_obStep, 0);
    }

    private void ObFinish()
    {
        if (_ob == null || _obClosing) return;
        _obClosing = true;
        Log.Info($"oobe: done (home {_obHome}, lang {ObLangs[_obLang].Code})");
        SaveUi(_prefs with { Oobe = OobeVersion, Lang = ObLangs[_obLang].Code });
        if (UiPrefs.FreshInstall && _backgroundSettings.Kind != BackgroundKind.Filmstrip && !File.Exists(Path.Combine(App.CacheDir, "background.ini")))
        {
            _backgroundSettings = _backgroundSettings with { Kind = BackgroundKind.Filmstrip };
            ApplyBackgroundSettings();
        }
        Cue(Sfx.Launch);
        // 引导里换了语言：游戏库已按启动时的语言画好，重启一次换成新语言
        if ((ObLangs[_obLang].Code == "en-US") != I18n.BootEn)
        {
            After(300, () =>
            {
                try { Microsoft.Windows.AppLifecycle.AppInstance.Restart(""); }
                catch (Exception ex) { Log.Warn($"oobe restart: {ex.Message}"); }
            });
        }
        var ob = _ob;
        double ms = Reduced ? Motion.ReducedFadeMs : 520;
        ob.StartAnimation("Opacity", G.Scalar(1, 0, ms, Motion.CameraEase));
        if (_obPage != null && !Reduced)
        {
            _obPage.CenterPoint = new Vector3((float)View.Width / 2, (float)View.Height / 2, 0);
            _obPage.StartAnimation("Scale", G.SampledV3(Vector3.One, new Vector3(1.05f, 1.05f, 1), ms, t => 1 - Math.Pow(1 - t, 3)));
        }
        After(ms + 40, () =>
        {
            _top.Children.Remove(ob);
            ob.Dispose();
            _obBack?.Run(false);
            _obBack?.Dispose();
            _obBack = null;
            _ob = null; _obPage = null; _obShade = null;
            _obLive.Clear(); _obCards.Clear(); _obHits.Clear(); _obHovers.Clear(); _obSpinners.Clear();
            _obAvatar?.Dispose(); _obAvatar = null;
            UpdateBackdropRunning();
        });
    }

    // ---------------- 背景 ----------------

    private void ObBackRun()
    {
        if (_obBack == null) return;
        _obBack.Run(_backdropOn && !Reduced && _windowActive);
        ObBackLoop();
    }

    private void ObBackLoop()
    {
        if (_obLoop || _obBack == null || !_obBack.NeedsFrames) return;
        _obLoop = true;
        _obLast = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnObFrame;
    }

    private void OnObFrame(object? sender, object e)
    {
        try
        {
            long now = NowMs;
            double dt = _obLast == 0 ? 16.7 : Math.Min(100, now - _obLast);
            _obLast = now;
            _obBack?.FrameNow(dt);
            if (_obBack == null || !_obBack.NeedsFrames)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnObFrame;
                _obLoop = false;
            }
        }
        catch (Exception ex)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnObFrame;
            _obLoop = false;
            Log.Error("oobe backdrop", ex);
        }
    }

    // ---------------- 页面框架 ----------------

    /// <summary>换到 step：旧页往反方向滑出淡掉，新页从 dir 一侧滑进来（dir = 0 原地重建）。</summary>
    private void ObShowPage(ObStep step, int dir)
    {
        if (_ob == null) return;
        bool changed = step != _obStep;
        _obStep = step;
        var old = _obPage;
        _obPage = G.Container();
        _obLive.Clear(); _obCards.Clear(); _obHits.Clear(); _obHovers.Clear(); _obSpinners.Clear();
        switch (step)
        {
            case ObStep.Welcome: ObBuildWelcome(); break;
            case ObStep.Account: ObBuildAccount(); break;
            case ObStep.Home: ObBuildHome(); break;
            case ObStep.Prepare: ObBuildPrepare(); break;
            default: ObBuildDone(); break;
        }
        _ob.Children.InsertAtTop(_obPage);
        float shift = 72 * _obK;
        if (old != null)
        {
            if (Reduced || dir == 0) { _ob.Children.Remove(old); old.Dispose(); }
            else
            {
                old.StartAnimation("Opacity", G.Scalar(1, 0, 200, Bezier.EaseIn));
                old.StartAnimation("Offset.X", G.Scalar(0, -dir * shift, 200, Bezier.EaseIn));
                After(220, () => { _ob?.Children.Remove(old); old.Dispose(); });
            }
        }
        if (dir != 0 && !Reduced)
        {
            _obPage.StartAnimation("Opacity", G.Scalar(0, 1, 380, Motion.CameraEase, 90));
            _obPage.StartAnimation("Offset.X", G.Scalar(dir * shift, 0, 460, Motion.CameraEase, 90));
        }
        if (changed) ObOnEnter(step);
    }

    private void ObOnEnter(ObStep step)
    {
        switch (step)
        {
            case ObStep.Account: ObPollSteam(); break;
            case ObStep.Prepare: if (!_obPrepared) { _obPrepared = true; _ = ObPrepareAsync(); } else After(700, ObMaybeDone); break;
            case ObStep.Done: ObCelebrate(); break;
        }
    }

    private SpriteVisual ObSprite(float x, float y, float w, float h, Action<CanvasDrawingSession> paint)
    {
        var s = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        CcSet(s, CcDraw(w, h, _obK * Chrome.Dpi, paint));
        s.Size = new Vector2(w * _obK, h * _obK);
        s.Offset = new Vector3(x * _obK, y * _obK, 0);
        s.CenterPoint = new Vector3(w * _obK / 2, h * _obK / 2, 0);
        _obPage!.Children.InsertAtTop(s);
        _obLive.Add(new ObLive { Sprite = s, W = w, H = h, Paint = paint });
        return s;
    }

    /// <summary>焦点、取值、进度变了：每张面按当前状态重画（面数很少，十来张）。</summary>
    private void ObRepaint()
    {
        if (_ob == null) return;
        float raster = _obK * Chrome.Dpi;
        foreach (var l in _obLive) CcSet(l.Sprite, CcDraw(l.W, l.H, raster, l.Paint));
    }

    private void ObHit(float x, float y, float w, float h, Action hit) => _obHits.Add((new Windows.Foundation.Rect(x * _obK, y * _obK, w * _obK, h * _obK), hit));

    private static void ObText(CanvasDrawingSession ds, string s, FontSpec f, float size, Color c, float x, float baseline, float width = 1800, CanvasHorizontalAlignment al = CanvasHorizontalAlignment.Left)
    {
        // 不用 LineMetrics（NativeAOT 下封送会抛异常）：基线取 TextKit 的字体 ascent
        using var l = TextKit.Layout(ds, s, f, size, width, al);
        ds.DrawTextLayout(l, x, baseline - TextKit.NormalLine(ds, f, size, s).Baseline, c);
    }

    /// <summary>图标字形按墨迹框居中到 (cx, cy)。</summary>
    private static void ObGlyph(CanvasDrawingSession ds, string glyph, float size, Color c, float cx, float cy)
    {
        using var l = TextKit.Layout(ds, glyph, ObIcons, size, size * 4, CanvasHorizontalAlignment.Left);
        var b = l.DrawBounds;
        ds.DrawTextLayout(l, cx - (float)(b.X + b.Width / 2), cy - (float)(b.Y + b.Height / 2), c);
    }

    /// <summary>文字按墨迹框（不是行框）居中到 (cx, cy)：键帽里的字母用，视觉上正好落在圆心。</summary>
    private static void ObInkCenter(CanvasDrawingSession ds, string s, FontSpec f, float size, Color c, float cx, float cy)
    {
        using var l = TextKit.Layout(ds, s, f, size, size * 4, CanvasHorizontalAlignment.Left);
        var b = l.DrawBounds;
        ds.DrawTextLayout(l, cx - (float)(b.X + b.Width / 2), cy - (float)(b.Y + b.Height / 2), c);
    }

    private static float ObWidth(ICanvasResourceCreator ds, string s, FontSpec f, float size)
    {
        using var l = TextKit.Layout(ds, s, f, size, 4000, CanvasHorizontalAlignment.Left);
        return (float)l.LayoutBounds.Width;
    }

    /// <summary>名字：显示名的第一段（「张三 (zhangsan@…)」只留「张三」）。</summary>
    private static string ObName()
    {
        string n = SystemInfo.UserName().Trim();
        int cut = n.IndexOfAny(new[] { '(', '（', '@' });
        if (cut > 0) n = n[..cut].Trim();
        return n.Length > 18 ? n[..18] : n;
    }

    // ---------------- 按钮 ----------------

    private const float ObBtnH = 76, ObBtnR = 8, ObBtnGap = 16, ObBtnMinW = 280;

    // 主操作：白色实心胶囊 + A 键帽；次操作：只有字，聚焦 / 悬停时才铺一层浅色胶囊；轻量入口（语言）：小号图标 + 字，同次操作
    // 主操作有最小宽度：两个字的「开始」也是一条横向的胶囊，不会被键帽撑成一团
    private enum ObKind { Primary, Secondary, Quiet }

    private float ObButtonWidth(string label, ObKind kind = ObKind.Primary) => kind switch
    {
        ObKind.Primary => MathF.Max(ObBtnMinW, 40 + 38 + 16 + ObWidth(G.Device, label, ObFont(700), 30) + 40),
        ObKind.Quiet => 24 + 26 + 12 + ObWidth(G.Device, label, ObFont(600), 26) + 24,
        _ => 32 + ObWidth(G.Device, label, ObFont(600), 30) + 32,
    };

    private float ObQuietH => 56;

    // 鼠标 / 触摸悬停：按钮区域 → 焦点序号；悬停直接把焦点挪过去，所以高亮始终只有一处
    private readonly List<(Windows.Foundation.Rect R, int Focus)> _obHovers = new();

    private void ObHover(double sx, double sy)
    {
        if (_ob == null || _obClosing) return;
        foreach (var (r, f) in _obHovers)
            if (r.Contains(new Windows.Foundation.Point(sx, sy)))
            {
                if (f != _obFocus) { _obFocus = f; ObRepaint(); }
                return;
            }
    }

    /// <summary>SteamOS 式的焦点：控件外面一圈柔和的白光（两层渐淡），不画硬边框。</summary>
    private static void ObGlow(CanvasDrawingSession ds, float x, float y, float w, float h, float r)
    {
        ds.DrawRoundedRectangle(x - 8, y - 8, w + 16, h + 16, r + 8, r + 8, Gfx.WithAlpha(CcText, 0.07), 10);
        ds.DrawRoundedRectangle(x - 3.5f, y - 3.5f, w + 7, h + 7, r + 3.5f, r + 3.5f, Gfx.WithAlpha(CcText, 0.22), 4);
    }

    /// <summary>
    /// 按钮：kind 决定层级（见 ObKind）。focus 是这个按钮的焦点序号，鼠标悬停时焦点挪过来；-1 = 不参与悬停。
    /// </summary>
    private void ObButton(float x, float y, ObKind kind, Func<string> label, Func<bool> focused, Func<bool> enabled, Action hit, int focus = -1, string? glyph = null)
    {
        float w = ObButtonWidth(label(), kind), h = kind == ObKind.Quiet ? ObQuietH : ObBtnH;
        ObSprite(x - ObPad, y - ObPad, w + 2 * ObPad + 160, h + 2 * ObPad, ds =>
        {
            string text = label();
            ObPaintButton(ds, kind, text, ObButtonWidth(text, kind), h, focused(), enabled(), glyph);
        });
        ObHit(x, y, w, h, hit);
        if (focus >= 0) _obHovers.Add((new Windows.Foundation.Rect(x * _obK, y * _obK, w * _obK, h * _obK), focus));
    }

    private void ObPaintButton(CanvasDrawingSession ds, ObKind kind, string label, float w, float h, bool focused, bool enabled, string? glyph)
    {
        float x = ObPad, y = ObPad, r = h / 2, cy = y + h / 2;
        if (kind == ObKind.Primary)
        {
            // 白色实心，没有任何描边 / 光环；聚焦 = 纯白，不在焦点 = 略暗的白
            ds.FillRoundedRectangle(x, y, w, h, r, r, enabled ? Gfx.WithAlpha(CcText, focused ? 1 : 0.8) : Gfx.WithAlpha(CcText, 0.16));
            Color fg = enabled ? ObInk : Gfx.WithAlpha(CcText, 0.5);
            // 键帽 + 字作为一组在胶囊里居中
            float tw = ObWidth(ds, label, ObFont(700), 30), group = 38 + 16 + tw;
            float gx = x + (w - group) / 2, kx = gx + 19;
            // A 键帽一直在；焦点在别的按钮上时变淡，不和那边抢 A
            ds.FillCircle(kx, cy, 19, enabled ? Gfx.WithAlpha(ObInk, focused ? 1 : 0.32) : Gfx.WithAlpha(CcText, 0.16));
            ObInkCenter(ds, "A", ObFont(700), 22, enabled ? CcText : Gfx.WithAlpha(CcText, 0.5), kx, cy);
            ObText(ds, label, ObFont(700), 30, fg, gx + 38 + 16, cy + 11, w);
            return;
        }
        bool lit = enabled && focused;
        if (lit) ds.FillRoundedRectangle(x, y, w, h, r, r, Gfx.WithAlpha(CcText, 0.14));
        Color ink = !enabled ? Gfx.WithAlpha(CcText, 0.36) : lit ? CcText : Gfx.WithAlpha(CcText, kind == ObKind.Quiet ? 0.6 : 0.72);
        if (kind == ObKind.Quiet)
        {
            float tx = x + 24;
            if (glyph != null) { ObGlyph(ds, glyph, 24, ink, tx + 13, cy); tx += 26 + 12; }
            ObText(ds, label, ObFont(600), 26, ink, tx, cy + 9, w);
            return;
        }
        ObText(ds, label, ObFont(600), 30, ink, x + 32, cy + 11, w);
    }

    // ---------------- 0 你好 ----------------

    private void ObBuildWelcome()
    {
        float cy = _obH * 0.5f;
        string model = _obDevice.Model;
        // 机型：标题上方一行小字，不加底，不和按钮抢形状
        ObSprite(ObMargin - 8, cy - 270, 1400, 70, ds => ObText(ds, model, ObFont(600), 28, Gfx.WithAlpha(CcText, 0.6), 8, 46));
        ObSprite(ObMargin - 8, cy - 210, _obW - ObMargin, 200, ds => ObText(ds, L("首次设置", "Setup"), ObFont(700), 112, CcText, 8, 150, _obW - ObMargin - 16));
        ObButton(ObMargin, cy + 56, ObKind.Primary, () => L("开始", "Start"), () => _obFocus == 0, () => true, () => { _obFocus = 0; ObActivate(); }, 0);
        // 语言：离开主操作那一行，放到左下角，24 是它自己的内边距，让地球图标和标题左缘对齐
        ObButton(ObMargin - 24, _obH - 96 - ObQuietH, ObKind.Quiet, () => ObLangs[_obLang].Name, () => _obFocus == 1, () => true, () => { _obFocus = 1; ObActivate(); }, 1, "");
    }

    // ---------------- 1 账户 ----------------

    // 和「一切就绪」同一种结构：上面一排平台圆，下面大标题（账户名）+ 一行数量，再下面按钮
    private const float ObPlatR = 96, ObPlatStep = 236;


    private string ObSteamAction() => _obSteam switch
    {
        0 => L("登录 Steam", "Sign in with Steam"),
        1 => L("安装 Steam", "Install Steam"),
        2 => L("打开 Steam", "Open Steam"),
        3 => L("继续", "Continue"),
        4 => L("正在等待浏览器", "Waiting for browser"),
        _ => L("正在查找 Steam", "Looking for Steam"),
    };

    private string ObSteamSecond() => _obSteam switch
    {
        0 => L("跳过", "Skip"),
        3 => L("更换账户", "Switch account"),
        4 => L("取消", "Cancel"),
        _ => L("继续", "Continue"),
    };

    private void ObBuildAccount()
    {
        float cy = _obH * 0.5f;
        ObSprite(ObMargin - 10, cy - 330, 220, 220, ObPaintSteamNode);
        ObSprite(ObMargin - 10 + ObPlatStep, cy - 330, 220, 220, ds =>
        {
            ds.FillCircle(110, 110, ObPlatR, Gfx.Rgba(18, 18, 20, 0.86));
            ObLogo(ds, "Xbox", 84, Gfx.WithAlpha(CcText, 0.32), 110, 110);
        });
        ObSprite(ObMargin - 8, cy - 110, _obW - ObMargin, 170, ds => ObText(ds, ObAccountTitle(), ObFont(700), 120, CcText, 8, 130, _obW - ObMargin - 16));
        ObSprite(ObMargin - 8, cy + 50, _obW - ObMargin, 80, ds => ObText(ds, ObAccountLine(), ObFont(700), 48, Gfx.WithAlpha(CcText, 0.7), 8, 56));
        float by = cy + 170, x = ObMargin;
        ObButton(x, by, ObKind.Primary, ObSteamAction, () => _obFocus == 0, () => _obSteam is >= 0 and not 4, () => { _obFocus = 0; ObActivate(); }, 0);
        x += ObButtonWidth(ObSteamAction()) + ObBtnGap;
        ObButton(x, by, ObKind.Secondary, ObSteamSecond, () => _obFocus == 1, () => _obSteam >= 0, () => { _obFocus = 1; ObActivate(); }, 1);
    }

    private string ObAccountTitle() => _obSteam is >= 1 and not 4 && _connections.Steam != null ? _connections.Steam.Name : "Steam";

    private string ObAccountLine() => _obSteam switch
    {
        3 => L($"{_lib.Games.Count(g => g.Platform == "steam")} 款游戏", $"{_lib.Games.Count(g => g.Platform == "steam")} games"),

        2 => L("本机未登录", "Not signed in here"),
        1 => L("未安装 Steam", "Steam not installed"),
        _ => "",
    };

    /// <summary>Steam 圆：绑好账户 = 头像，右下角一个白色小圆里是 Steam 标志；没绑 = 白色实心圆里一个 Steam 标志。</summary>
    private void ObPaintSteamNode(CanvasDrawingSession ds)
    {
        float cx = 110, cy = 110, r = ObPlatR;
        bool bound = _obSteam is >= 1 and not 4 && _connections.Steam != null;
        if (!bound)
        {
            ds.FillCircle(cx, cy, r, _obSteam == 4 ? Gfx.WithAlpha(CcText, 0.24) : CcText);
            ObLogo(ds, "Steam", 92, _obSteam == 4 ? CcText : ObInk, cx, cy);
            return;
        }
        using (var geo = CanvasGeometry.CreateEllipse(ds, cx, cy, r, r))
        {
            if (_obAvatar != null)
                using (ds.CreateLayer(1, geo))
                    ds.DrawImage(_obAvatar, new Windows.Foundation.Rect(cx - r, cy - r, 2 * r, 2 * r), _obAvatar.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
            else
            {
                ds.FillGeometry(geo, Gfx.WithAlpha(CcText, 0.24));
                string name = _connections.Steam!.Name;
                ObInkCenter(ds, name.Length > 0 ? name[..1].ToUpperInvariant() : "?", ObFont(700), 80, CcText, cx, cy);
            }
        }
        float bx = cx + r * 0.72f, by = cy + r * 0.72f;
        ds.FillCircle(bx, by, 30, CcText);
        ObLogo(ds, "Steam", 36, ObInk, bx, by);
    }

    // 平台标志（随包 Assets\Steam.svg、Assets\Xbox.svg），按颜色上色、按 size 的正方形居中到 (cx, cy)
    private static readonly Dictionary<string, CanvasSvgDocument> _obLogos = new();

    private static void ObLogo(CanvasDrawingSession ds, string name, float size, Color c, float cx, float cy)
    {
        if (!_obLogos.TryGetValue(name, out var doc))
        {
            string xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", name + ".svg"));
            // Xbox.svg 把颜色写在 path 上、根上是 fill="none"：统一挪到根上，才能整体换色
            xml = xml.Replace(" fill=\"white\"", "").Replace("fill=\"none\"", "fill=\"#ffffff\"");
            // 根上的 width / height 会盖过 DrawSvg 给的大小（Xbox.svg 写死 80）：只留 viewBox，才按 size 缩放
            for (int i = 0; i < 2; i++)
                xml = System.Text.RegularExpressions.Regex.Replace(xml, @"<svg([^>]*?)\s(width|height)=""[^""]*""", "<svg$1");
            doc = CanvasSvgDocument.LoadFromXml(ds, xml);
            _obLogos[name] = doc;
        }
        doc.Root.SetColorAttribute("fill", c);
        ds.DrawSvg(doc, new Windows.Foundation.Size(size, size), cx - size / 2, cy - size / 2);
    }

    /// <summary>账户页开着时每 1.5 秒看一次：绑没绑、本机 Steam 装没装、这个账户在不在本机。</summary>
    private void ObPollSteam()
    {
        if (_ob == null || _obClosing) return;
        int state;
        var account = _connections.Steam;
        string? root = null, user = null;
        if (_connectCancel != null) state = 4;
        else if (account == null) state = 0;
        else if ((root = Steam.Root()) == null) state = 1;
        else if ((user = Steam.UserDirForAccount(root, account.Id)) == null) state = 2;
        else state = 3;
        if (state != _obSteam)
        {
            _obSteam = state;
            if (state == 4) _obFocus = 1;
            else if (state == 3) _obFocus = 0;
            Log.Info($"oobe: steam state {state}");
            if (state == 3) _ = ObSteamReadyAsync(root!, account!.Id);
            if (_obStep == ObStep.Account) ObShowPage(ObStep.Account, 0);
        }
        ObMaybeUseUserWall();
        if (_obStep == ObStep.Account || _obSteam != 3) After(1500, ObPollSteam);
    }

    /// <summary>绑好的账户在本机：读头像，按这个账户重新读 Steam 游戏库，补封面，够了就换墙。</summary>
    private async Task ObSteamReadyAsync(string root, string id64)
    {
        try
        {
            string avatar = Path.Combine(root, "config", "avatarcache", id64 + ".png");
            _obAvatar?.Dispose(); _obAvatar = null;
            if (File.Exists(avatar)) { try { _obAvatar = await CanvasBitmap.LoadAsync(G.Device, avatar); } catch { } }
            if (_obStep == ObStep.Account) ObRepaint();
            await RefreshPlatformLibrary("steam", quiet: true);
            if (_obStep == ObStep.Account) ObRepaint();
            await BuildMissingArtAsync();
            ObMaybeUseUserWall();
        }
        catch (Exception ex) { Log.Warn($"oobe steam: {ex.Message}"); }
    }

    /// <summary>登录 Steam = 设置里「绑定 Steam 账户」同一条路（ConnectSteam）；回来以后轮询自己会走到下一状态。</summary>
    private void ObBindSteam()
    {
        if (_connectCancel != null) return;
        _ = ConnectSteam();
        ObPollSteam();
    }

    // ---------------- 2 主页 + 基本设置 ----------------

    // 和「正在准备」同一种节点：一排圆形图标 + 名字，直接放在墙上；选中的是白色实心圆
    private const float ObNodeR = 66, ObNodeStep = 230, ObRowH = 64, ObRowW = 820;
    private const int ObHomeCount = 4;
    /// <summary>全屏体验主页：0 Claw 游戏库、1 Steam 大屏幕（主页还是游戏库，被当主页打开时转去 Steam）、2 Xbox 应用、3 桌面（不进全屏体验）。</summary>
    private string[] ObHomeNames => new[] { L("游戏库", "Library"), L("Steam 大屏幕", "Steam Big Picture"), "Xbox", L("桌面", "Desktop") };

    /// <summary>主页页的焦点：0 = 主页选项，1..4 = 设置行，5 = 继续。</summary>
    private (string Name, string[] Values, Func<int> Get, Action<int> Set)[] ObRows => new (string, string[], Func<int>, Action<int>)[]
    {
        (L("界面音效", "UI sounds"), new[] { L("开", "On"), L("关", "Off") }, () => _prefs.Sounds ? 0 : 1, v => { SaveUi(_prefs with { Sounds = v == 0 }); ApplyHomeAudio(); }),
        (L("背景音乐", "Music"), new[] { L("开", "On"), L("关", "Off") }, () => _prefs.MusicEnabled ? 0 : 1, v => { SaveUi(_prefs with { MusicEnabled = v == 0 }); ApplyHomeAudio(); }),
        (L("手柄震动", "Haptics"), new[] { L("开", "On"), L("关", "Off") }, () => _prefs.NavHaptics ? 0 : 1, v => SaveUi(_prefs with { NavHaptics = v == 0 })),
        (L("开机先显示", "Open to"), new[] { L("游戏库", "Library"), L("控制中心", "Control center") }, () => _prefs.Boot == BootTarget.ControlCenter ? 1 : 0,
            v => SaveUi(_prefs with { Boot = v == 1 ? BootTarget.ControlCenter : BootTarget.Library })),
    };

    private void ObBuildHome()
    {
        float y0 = MathF.Max(110, (_obH - 820) / 2);
        ObSprite(ObMargin - 8, y0, _obW - ObMargin, 170, ds => ObText(ds, L("主页", "Home"), ObFont(700), 112, CcText, 8, 130));
        float ny = y0 + 270;
        for (int i = 0; i < ObHomeCount; i++)
        {
            int idx = i;
            float cx = ObMargin + ObNodeR + i * ObNodeStep;
            ObSprite(cx - 120, ny - ObNodeR - 16, 240, 250, ds => ObPaintHomeNode(ds, idx));
            ObHit(cx - 110, ny - ObNodeR - 16, 220, 230, () => { _obFocus = 0; ObSetHome(idx); });
        }
        // 设置行：没有底、没有描边，聚焦时铺一层浅色胶囊（和语言入口同一种高亮）；左右各让出 24，让字和标题左缘对齐
        float ry = ny + ObNodeR + 100;
        var rows = ObRows;
        for (int i = 0; i < rows.Length; i++)
        {
            int idx = i;
            float y = ry + i * ObRowH;
            ObSprite(ObMargin - 24 - ObPad, y - ObPad, ObRowW + 2 * ObPad, ObRowH + 2 * ObPad, ds => ObPaintRow(ds, idx));
            ObHit(ObMargin - 24, y, ObRowW, ObRowH, () =>
            {
                _obFocus = idx + 1;
                var r = ObRows[idx];
                int v = (r.Get() + 1) % r.Values.Length;
                r.Set(v);
                Cue(v == 0 ? Sfx.ToggleOn : Sfx.ToggleOff);
                ObRepaint();
            });
            _obHovers.Add((new Windows.Foundation.Rect((ObMargin - 24) * _obK, y * _obK, ObRowW * _obK, ObRowH * _obK), idx + 1));
        }
        float by = ry + rows.Length * ObRowH + 48;
        ObButton(ObMargin, by, ObKind.Primary, () => L("继续", "Continue"), () => _obFocus == 5, () => true, () => { _obFocus = 5; ObActivate(); }, 5);
    }

    /// <summary>主页选项：选中且聚焦 = 白色实心圆；选中没聚焦 = 浅色玻璃圆；没选中 = 深色圆、图标变淡。图标用各自平台的标志。</summary>
    private void ObPaintHomeNode(CanvasDrawingSession ds, int i)
    {
        float cx = 120, cy = ObNodeR + 16, r = ObNodeR;
        bool picked = i == _obHome, lit = picked && _obFocus == 0;
        ds.FillCircle(cx, cy, r, lit ? CcText : picked ? Gfx.WithAlpha(CcText, 0.24) : Gfx.Rgba(18, 18, 20, 0.86));
        var ink = lit ? ObInk : picked ? CcText : Gfx.WithAlpha(CcText, 0.55);
        switch (i)
        {
            case 1: ObLogo(ds, "Steam", 60, ink, cx, cy); break;
            case 2: ObLogo(ds, "Xbox", 56, ink, cx, cy); break;
            default: ObGlyph(ds, i == 0 ? "" : "", 52, ink, cx, cy); break;
        }
        ObText(ds, ObHomeNames[i], ObFont(700), 30, picked ? CcText : Gfx.WithAlpha(CcText, 0.55), 0, cy + r + 54, 240, CanvasHorizontalAlignment.Center);
    }

    /// <summary>设置行：文字 + 右边的开关或取值；只有聚焦时才有浅色胶囊底，其余时候直接在墙上。</summary>
    private void ObPaintRow(CanvasDrawingSession ds, int i)
    {
        var row = ObRows[i];
        bool focused = _obFocus == i + 1;
        float x = ObPad, y = ObPad, w = ObRowW, h = ObRowH, cy = y + h / 2;
        if (focused) ds.FillRoundedRectangle(x, y, w, h, h / 2, h / 2, Gfx.WithAlpha(CcText, 0.14));
        ObText(ds, row.Name, ObFont(600), 30, focused ? CcText : Gfx.WithAlpha(CcText, 0.78), x + 24, cy + 11);
        int cur = row.Get();
        float right = x + w - 24;
        bool toggle = row.Values.Length == 2 && row.Values[0] == L("开", "On");
        if (toggle)
        {
            bool on = cur == 0;
            float tw = 72, th = 38, tx = right - tw, ty = cy - th / 2;
            ds.FillRoundedRectangle(tx, ty, tw, th, th / 2, th / 2, on ? CcText : Gfx.WithAlpha(CcText, 0.22));
            ds.FillCircle(on ? tx + tw - th / 2 : tx + th / 2, cy, th / 2 - 5, on ? ObInk : Gfx.WithAlpha(CcText, 0.75));
            return;
        }
        string v = row.Values[cur];
        float vw = ObWidth(ds, v, ObFont(600), 28);
        if (focused)
        {
            ObGlyph(ds, "", 20, cur < row.Values.Length - 1 ? CcText : Gfx.WithAlpha(CcText, 0.3), right - 10, cy);
            ObText(ds, v, ObFont(600), 28, CcText, right - 36 - vw, cy + 10, vw + 6);
            ObGlyph(ds, "", 20, cur > 0 ? CcText : Gfx.WithAlpha(CcText, 0.3), right - 62 - vw, cy);
        }
        else ObText(ds, v, ObFont(600), 28, Gfx.WithAlpha(CcText, 0.6), right - vw, cy + 10, vw + 6);
    }

    private void ObSetHome(int idx)
    {
        _obHome = idx;
        ObRepaint();
    }

    // ---------------- 3 准备 ----------------

    private List<ObTask> ObBuildTasks()
    {
        var list = new List<ObTask>
        {
            new() { Glyph = "", Label = () => L("字体", "Fonts"), Run = () => ObInstallStepAsync("fonts") },
            new() { Glyph = "", Label = () => L("后台", "Service"), Run = ObInstallAgentAsync },
        };
        if (_obDevice.Claw)
            list.Add(new() { Glyph = "", Label = () => L("风扇与功耗", "Fan & power"), Run = async () => await ObInstallStepAsync("pawnio") & await ObInstallStepAsync("fan") });
        list.Add(new() { Glyph = "", Label = () => L("小组件", "Widget"), Run = () => ObInstallStepAsync("widget") });
        if (_obDevice.Claw)
            list.Add(new() { Glyph = "", Label = () => L("其他前端", "Other apps"), Run = ObDisableVendorsAsync });
        list.Add(new() { Glyph = "", Label = () => L("游戏库", "Library"), Run = async () =>
        {
            await ObApplyHomeAsync(_obHome);
            if (Steam.Root() != null) await RefreshPlatformLibrary("steam", quiet: true);
            return _lib.Games.Count > 0;
        } });
        var covers = new ObTask { Glyph = "", Label = () => L("封面", "Covers"), Run = async () => { await BuildMissingArtAsync(); ObMaybeUseUserWall(); return true; } };
        covers.Fraction = () =>
        {
            var games = _lib.Games.Where(g => g.Kind == ItemKind.Game && g.Platform is not ("folder" or "xbox")).ToList();
            return games.Count == 0 ? 1 : games.Count(g => g.Art != null) / (float)games.Count;
        };
        list.Add(covers);
        return list;
    }

    private void ObBuildPrepare()
    {
        float cy = _obH * 0.5f;
        ObSprite(ObMargin - 8, cy - 330, 1400, 150, ds => ObText(ds, L("正在准备", "Getting ready"), ObFont(700), 96, CcText, 8, 120));
        int n = _obTasks.Count;
        float left = ObMargin + 66, right = _obW - ObMargin - 66, step = n > 1 ? (right - left) / (n - 1) : 0;
        // 轨道：节点之间的连线，走完的一段是白的
        ObSprite(left, cy - 8, right - left, 16, ds =>
        {
            ds.FillRoundedRectangle(0, 5, right - left, 6, 3, 3, Gfx.WithAlpha(CcText, 0.18));
            int done = _obTasks.TakeWhile(t => t.State is ObState.Done or ObState.Failed).Count();
            float len = done == 0 ? 0 : MathF.Min(right - left, (done - 1) * step + (done < n && _obTasks[done].State == ObState.Running ? step * 0.5f : 0));
            if (len > 0) ds.FillRoundedRectangle(0, 5, len, 6, 3, 3, CcText);
        });
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            float cx = left + i * step;
            ObSprite(cx - 120, cy - 82, 240, 250, ds => ObPaintNode(ds, idx));
            var spin = ObSprite(cx - 82, cy - 82, 164, 164, ds =>
            {
                using var arc = new CanvasPathBuilder(ds);
                arc.BeginFigure(82 + 74, 82);
                arc.AddArc(new Vector2(82, 82), 74, 74, 0, MathF.PI * 0.6f);
                arc.EndFigure(CanvasFigureLoop.Open);
                using var g = CanvasGeometry.CreatePath(arc);
                using var cap = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
                ds.DrawGeometry(g, CcText, 6, cap);
            });
            var rot = G.C.CreateScalarKeyFrameAnimation();
            rot.InsertKeyFrame(0, 0); rot.InsertKeyFrame(1, 360, G.C.CreateLinearEasingFunction());
            rot.Duration = TimeSpan.FromSeconds(1.1); rot.IterationBehavior = AnimationIterationBehavior.Forever;
            spin.StartAnimation("RotationAngleInDegrees", rot);
            spin.IsVisible = _obTasks[i].State == ObState.Running && _obTasks[i].Fraction == null;
            _obSpinners.Add(spin);
        }
    }

    private void ObPaintNode(CanvasDrawingSession ds, int i)
    {
        var t = _obTasks[i];
        float cx = 120, cy = 82, r = 66;
        switch (t.State)
        {
            case ObState.Done:
                ds.FillCircle(cx, cy, r, CcText);
                ObGlyph(ds, "", 56, ObInk, cx, cy);
                break;
            case ObState.Failed:
                ds.FillCircle(cx, cy, r, Gfx.Rgba(18, 18, 20));
                ds.DrawCircle(cx, cy, r - 3, ObRed, 6);
                ObGlyph(ds, t.Glyph, 52, ObRed, cx, cy);
                break;
            default:
                bool running = t.State == ObState.Running;
                ds.FillCircle(cx, cy, r, Gfx.Rgba(18, 18, 20));
                ds.DrawCircle(cx, cy, r - 3, Gfx.WithAlpha(CcText, running ? 0.3 : 0.16), 6);
                if (running && t.Fraction != null)
                {
                    float f = Math.Clamp(t.Fraction(), 0, 1);
                    if (f > 0.001f)
                    {
                        using var arc = new CanvasPathBuilder(ds);
                        arc.BeginFigure(cx, cy - (r - 3));
                        arc.AddArc(new Vector2(cx, cy), r - 3, r - 3, -MathF.PI / 2, MathF.PI * 2 * MathF.Min(f, 0.999f));
                        arc.EndFigure(CanvasFigureLoop.Open);
                        using var g = CanvasGeometry.CreatePath(arc);
                        ds.DrawGeometry(g, CcText, 6);
                    }
                }
                ObGlyph(ds, t.Glyph, 52, Gfx.WithAlpha(CcText, running ? 1 : 0.4), cx, cy);
                break;
        }
        string label = t.Label();
        if (t.Fraction != null && t.State == ObState.Running) label += $"  {Math.Round(t.Fraction() * 100)}%";
        var lc = t.State == ObState.Pending ? Gfx.WithAlpha(CcText, 0.45) : t.State == ObState.Failed ? ObRed : CcText;
        ObText(ds, label, ObFont(700), 30, lc, 0, 210, 240, CanvasHorizontalAlignment.Center);
    }

    private void ObRepaintPrepare()
    {
        if (_ob == null || _obStep != ObStep.Prepare) return;
        ObRepaint();
        for (int i = 0; i < _obSpinners.Count && i < _obTasks.Count; i++)
            _obSpinners[i].IsVisible = _obTasks[i].State == ObState.Running && _obTasks[i].Fraction == null;
    }

    private async Task ObPrepareAsync()
    {
        foreach (var t in _obTasks)
        {
            t.State = ObState.Running; ObRepaintPrepare();
            long t0 = NowMs;
            bool ok;
            var ticker = t.Fraction != null ? new ObTicker(this, 250, ObRepaintPrepare) : null;
            try { ok = await t.Run(); }
            catch (Exception ex) { Log.Warn($"oobe task {t.Label()}: {ex.Message}"); ok = false; }
            ticker?.Stop();
            long wait = 650 - (NowMs - t0);
            if (wait > 0) await Task.Delay((int)wait);
            t.State = ok ? ObState.Done : ObState.Failed;
            Log.Info($"oobe task {t.Label()}: {(ok ? "ok" : "failed")}");
            Cue(ok ? Sfx.ToggleOn : Sfx.Error);
            ObRepaintPrepare();
        }
        After(900, ObMaybeDone);
    }

    private void ObMaybeDone()
    {
        if (_ob == null || _obClosing || _obStep != ObStep.Prepare) return;
        if (_obTasks.Any(t => t.State is ObState.Pending or ObState.Running)) return;
        ObShowPage(ObStep.Done, 1);
    }

    /// <summary>周期重画（封面进度）：每 ms 毫秒调一次，Stop 为止。</summary>
    private sealed class ObTicker
    {
        private bool _stopped;
        public ObTicker(WallStage s, double ms, Action tick)
        {
            void Next() { if (_stopped) return; tick(); s.After(ms, Next); }
            s.After(ms, Next);
        }
        public void Stop() => _stopped = true;
    }

    // ---------------- 4 完成 ----------------

    private void ObBuildDone()
    {
        float cy = _obH * 0.5f;
        int n = _lib.Games.Count;
        var check = ObSprite(ObMargin - 10, cy - 330, 220, 220, ds =>
        {
            ds.FillCircle(110, 110, 96, CcText);
            ObGlyph(ds, "", 96, ObInk, 110, 110);
        });
        if (!Reduced)
        {
            check.Scale = new Vector3(0.6f, 0.6f, 1);
            check.StartAnimation("Scale", G.SpringV3(Vector3.One, Spring.Visual(0.5, 0.35)));
        }
        ObSprite(ObMargin - 8, cy - 110, _obW - ObMargin, 170, ds => ObText(ds, L("设置完成", "Setup complete"), ObFont(700), 120, CcText, 8, 130));
        ObSprite(ObMargin - 8, cy + 50, _obW - ObMargin, 80, ds => ObText(ds, L($"{n} 款游戏", $"{n} games"), ObFont(700), 48, Gfx.WithAlpha(CcText, 0.7), 8, 56));
        _obFocus = 0;
        ObButton(ObMargin, cy + 170, ObKind.Primary, () => L("进入游戏库", "Open library"), () => true, () => true, ObActivate);
    }

    private void ObCelebrate()
    {
        ObMaybeUseUserWall();
        if (_obShade != null && !Reduced) _obShade.StartAnimation("Opacity", G.Scalar(1, 0.72, 900, Motion.CameraEase));
        Cue(Sfx.Positive);
    }

    // ---------------- 输入 ----------------

    private void ObMove(Direction dir)
    {
        if (_obClosing) return;
        bool h = dir is Direction.Left or Direction.Right;
        int d = dir is Direction.Left or Direction.Up ? -1 : 1;
        switch (_obStep)
        {
            case ObStep.Welcome:
                // 语言入口在左下角，和「开始」上下排：上下切换
                if (h || _obFocus + d is < 0 or > 1) { Cue(Sfx.Bump); return; }
                _obFocus += d; ObRepaint(); return;
            case ObStep.Account:
                if (!h || _obSteam == 4 || _obFocus + d is < 0 or > 1) { Cue(Sfx.Bump); return; }
                _obFocus += d; ObRepaint(); return;
            case ObStep.Home:
                if (!h)
                {
                    int f = _obFocus + d;
                    if (f is < 0 or > 5) { Cue(Sfx.Bump); return; }
                    _obFocus = f; ObRepaint(); return;
                }
                if (_obFocus == 0)
                {
                    int next = _obHome + d;
                    if (next < 0 || next >= ObHomeCount) { Cue(Sfx.Bump); return; }
                    ObSetHome(next); return;
                }
                if (_obFocus is >= 1 and <= 4)
                {
                    var r = ObRows[_obFocus - 1];
                    int v = r.Get() + d;
                    if (v < 0 || v >= r.Values.Length) { Cue(Sfx.Bump); return; }
                    r.Set(v); Cue(v == 0 ? Sfx.ToggleOn : Sfx.ToggleOff); ObRepaint(); return;
                }
                Cue(Sfx.Bump); return;
            default:
                Cue(Sfx.Bump); return;
        }
    }

    private void ObActivate()
    {
        if (_obClosing) return;
        switch (_obStep)
        {
            case ObStep.Welcome:
                if (_obFocus == 1)
                {
                    _obLang = (_obLang + 1) % ObLangs.Length;
                    SaveUi(_prefs with { Lang = ObLangs[_obLang].Code });
                    I18n.Init(ObLangs[_obLang].Code);
                    Cue(Sfx.ToggleOn);
                    ObShowPage(ObStep.Welcome, 0);
                    return;
                }
                Cue(Sfx.DetailIn); _obFocus = 0; ObShowPage(ObStep.Account, 1); return;
            case ObStep.Account:
                if (_obFocus == 1)
                {
                    switch (_obSteam)
                    {
                        case 4: _connectCancel?.Cancel(); Cue(Sfx.Back); return;
                        case 3: Cue(Sfx.Positive); ObBindSteam(); return;
                        default: Cue(Sfx.DetailIn); _obFocus = 0; ObShowPage(ObStep.Home, 1); return;
                    }
                }
                switch (_obSteam)
                {
                    case 0: Cue(Sfx.Positive); ObBindSteam(); return;
                    case 1: SystemInfo.Open("https://store.steampowered.com/about/"); Cue(Sfx.Positive); return;
                    case 2: SystemInfo.Open("steam://open/main"); Cue(Sfx.Positive); return;
                    case 3: Cue(Sfx.DetailIn); _obFocus = 0; ObShowPage(ObStep.Home, 1); return;
                }
                Cue(Sfx.Bump); return;
            case ObStep.Home:
                if (_obFocus < 5) { _obFocus++; ObRepaint(); return; }
                Cue(Sfx.DetailIn); ObShowPage(ObStep.Prepare, 1); return;
            case ObStep.Prepare:
                Cue(Sfx.Bump); return;
            default:
                ObFinish(); return;
        }
    }

    private void ObBack()
    {
        if (_obClosing) return;
        if (_obStep is ObStep.Welcome or ObStep.Prepare or ObStep.Done) { Cue(Sfx.Bump); return; }
        Cue(Sfx.DetailOut);
        _obFocus = 0;
        ObShowPage(_obStep - 1, -1);
    }

    private void ObClick(double sx, double sy)
    {
        foreach (var (r, hit) in _obHits.ToArray())
            if (r.Contains(new Windows.Foundation.Point(sx, sy))) { hit(); return; }
    }

    // ---------------- 后台 ----------------

    private async Task ObConnectAgentAsync()
    {
        long until = NowMs + 30000;
        while (NowMs < until && _ob != null)
        {
            try
            {
                _agent ??= new AgentClient();
                if (await _agent.Call("get_settings_state") is JsonObject) { _obAgentReady?.TrySetResult(true); return; }
            }
            catch { }
            await Task.Delay(1000);
        }
        _obAgentReady?.TrySetResult(false);
    }

    // ---------------- 组件安装 ----------------
    // 安装包里的 Payload\Install-Components.ps1 以管理员身份跑一次：后台、PawnIO、风扇服务、小组件，
    // 每项本机已有且不旧就跳过。它把每一步的状态写进 %LOCALAPPDATA%\ClawToolbox\setup\status-<run>.json，
    // 这边每 400 ms 读一次，节点按各自那一步的结果走。用户拒绝 UAC → 这几项都算失败。

    private Task<bool>? _obInstall;
    private string? _obInstallStatus;

    private Task<bool> ObStartInstall() => _obInstall ??= Task.Run(() =>
    {
        try
        {
            string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawToolbox");
            string run = Guid.NewGuid().ToString("N");
            string script = Path.Combine(AppContext.BaseDirectory, "Payload", "Install-Components.ps1");
            string steps = _obDevice.Claw ? "fonts,agent,sleep,pawnio,fan,widget" : "fonts,agent,sleep,widget";
            var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            _obInstallStatus = Path.Combine(home, "setup", $"status-{run}.json");
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -AgentHome \"{home}\" -User \"{me.Name}\" -UserSid {me.User?.Value} -Steps {steps} -Run {run}")
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            using var p = System.Diagnostics.Process.Start(psi);
            Log.Info($"oobe: component installer started ({steps})");
            return p != null;
        }
        catch (Exception ex) { Log.Warn($"oobe: component installer: {ex.Message}"); return false; }   // 1223 = 用户拒绝了 UAC
    });

    /// <summary>等安装脚本里某一步结束：done → true，failed / 脚本没起来 / 超时 → false。</summary>
    private async Task<bool> ObInstallStepAsync(string step)
    {
        if (!await ObStartInstall()) return false;
        long until = NowMs + 240000;
        while (NowMs < until && _ob != null)
        {
            try
            {
                if (_obInstallStatus != null && File.Exists(_obInstallStatus)
                    && JsonNode.Parse(File.ReadAllText(_obInstallStatus)) is JsonObject s)
                {
                    string state = (string?)s[step]?["state"] ?? "";
                    if (state == "done") { Log.Info($"oobe install {step}: {(string?)s[step]?["detail"]}"); return true; }
                    if (state == "failed" || (state is "" or "pending" && s["finished"] is JsonValue f && f.TryGetValue(out bool fin) && fin))
                    {
                        Log.Warn($"oobe install {step}: {(string?)s[step]?["detail"]}");
                        return false;
                    }
                }
            }
            catch { }   // 脚本正在换文件：下一轮再读
            await Task.Delay(400);
        }
        return false;
    }

    /// <summary>后台：装好（或确认已装）以后重新连上；后面的「其他前端」「游戏库」都靠它。</summary>
    private async Task<bool> ObInstallAgentAsync()
    {
        bool installed = await ObInstallStepAsync("agent");
        if (installed && !await ObInstallStepAsync("sleep")) return false;
        if (_obAgentReady != null && _obAgentReady.Task.IsCompleted && _obAgentReady.Task.Result) return true;
        if (!installed) return false;
        long until = NowMs + 30000;
        while (NowMs < until && _ob != null)
        {
            try
            {
                _agent ??= new AgentClient();
                if (await _agent.Call("get_settings_state") is JsonObject)
                {
                    _obAgentReady = new TaskCompletionSource<bool>();
                    _obAgentReady.SetResult(true);
                    return true;
                }
            }
            catch { }
            await Task.Delay(1000);
        }
        return false;
    }

    private async Task<bool> ObDisableVendorsAsync()
    {
        if (_obAgentReady == null || !await _obAgentReady.Task || _agent == null) return false;
        JsonArray? vendors = null;
        for (int i = 0; i < 3 && vendors == null; i++)
        {
            vendors = (await _agent.Call("get_settings_state") as JsonObject)?["vendors"] as JsonArray;
            if (vendors == null) await Task.Delay(2000);
        }
        if (vendors == null) return false;
        foreach (var app in vendors.OfType<JsonObject>())
        {
            if (!ObBool(app["installed"]) || ObBool(app["disabled"])) continue;
            string id = (string?)app["id"] ?? "";
            Log.Info($"oobe: disable vendor {id}");
            await _agent.CallLong("set_vendor_app", 60, JsonValue.Create(id), JsonValue.Create(true));
        }
        return true;
    }

    private static bool ObBool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;

    private async Task ObApplyHomeAsync(int pick)
    {
        SaveUi(_prefs with { FseSteam = pick == 1 });
        if (_obAgentReady == null || !await _obAgentReady.Task || _agent == null) { Log.Warn("oobe: home not applied, agent unavailable"); return; }
        try
        {
            await _agent.Call("set_home_app", JsonValue.Create(pick switch { 2 => XboxHomeApp, 3 => "off", _ => OwnHomeApp }));
            Log.Info($"oobe: home = {pick}");
        }
        catch (Exception ex) { Log.Warn($"oobe: set home: {ex.Message}"); }
    }

    private static bool ServiceInstalled(string name)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}"); return k != null; }
        catch { return false; }
    }

    private static bool PackageRegistered(string familyPrefix)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            return k?.GetSubKeyNames().Any(n => n.StartsWith(familyPrefix, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch { return false; }
    }
}
