// 游戏模式 ⇄ 桌面的切换画面：整屏压暗、正中一个大图标和目标模式的名字浮上来，切换真正完成后再收场 ——
// 去桌面：Windows 桌面和任务栏按原位从这层画面里淡出来，然后游戏库退到后面；
// 回游戏模式：这层淡掉，露出墙。只用一个通知看不出切换了什么。
using System.Numerics;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const string ModeGameGlyph = "", ModeDesktopGlyph = "";
    private const double ModeInMs = 260, ModeOutMs = 320, ModeHoldMs = 450;
    private ContainerVisual? _mode;
    private SpriteVisual _modeBack = null!, _modeMark = null!;
    private readonly List<DwmThumb> _modeThumbs = new();

    private void ModeShiftShow(bool toGame)
    {
        float k = Math.Min((float)View.Width / 1280, (float)View.Height / 800), raster = k * Chrome.Dpi;
        if (_mode == null)
        {
            _mode = G.Container();
            _mode.RelativeSizeAdjustment = Vector2.One;
            _modeBack = G.Sprite(G.C.CreateColorBrush(Gfx.Rgba(9, 9, 11)));
            _modeBack.RelativeSizeAdjustment = Vector2.One;
            _modeMark = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _mode.Children.InsertAtTop(_modeBack);
            _mode.Children.InsertAtTop(_modeMark);
        }
        if (_mode.Parent != null) _top.Children.Remove(_mode);   // 第一次还不在树里，Remove 会抛
        _top.Children.InsertAtTop(_mode);
        const float W = 320, H = 200;
        string label = toGame ? "游戏模式" : "桌面";
        CcSet(_modeMark, CcDraw(W, H, raster, ds =>
        {
            LineIcons.Draw(ds, toGame ? ModeGameGlyph : ModeDesktopGlyph, W / 2, 64, 72, CcText);
            using var tl = TextKit.Layout(ds, label, TextKit.Ui(600), 26, W, CanvasHorizontalAlignment.Center);
            var b = tl.LayoutBounds;
            ds.DrawTextLayout(tl, (W - (float)b.Width) / 2 - (float)b.X, 140 - (float)b.Y, CcText);
        }));
        _modeMark.Size = new Vector2(W * k, H * k);
        _modeMark.CenterPoint = new Vector3(W * k / 2, H * k / 2, 0);
        _modeMark.Offset = new Vector3(((float)View.Width - W * k) / 2, ((float)View.Height - H * k) / 2, 0);
        _mode.IsVisible = true;
        double ms = Reduced ? 1 : ModeInMs;
        _modeBack.StartAnimation("Opacity", G.Scalar(0, 0.97f, ms, EaseOutCss));
        _modeMark.StartAnimation("Opacity", G.Scalar(0, 1, ms, EaseOutCss, 80));
        var grow = G.C.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3(0.92f, 0.92f, 1));
        grow.InsertKeyFrame(1, Vector3.One);
        grow.Duration = TimeSpan.FromMilliseconds(Reduced ? 1 : ModeInMs + 120);
        _modeMark.StartAnimation("Scale", grow);
        Log.Info($"mode shift show ({label})");
    }

    /// <summary>回游戏模式 / 切换失败：这一层淡掉。</summary>
    private void ModeShiftHide()
    {
        if (_mode == null || !_mode.IsVisible) return;
        double ms = Reduced ? 1 : ModeOutMs;
        _modeMark.StartAnimation("Opacity", G.Scalar(1, 0, ms * 0.6, EaseOutCss));
        _modeBack.StartAnimation("Opacity", G.Scalar(0.97f, 0, ms, EaseOutCss));
        After(ms + 20, () => { if (_mode != null) _mode.IsVisible = false; });
    }

    /// <summary>去桌面：桌面和任务栏按原位淡进来（DWM 实时画面压在这层上面），淡满了调用 done（游戏库退到后面）。</summary>
    private void ModeShiftRevealDesktop(Action done)
    {
        foreach (var t in _modeThumbs) t.Dispose();
        _modeThumbs.Clear();
        nint self = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var rects = new List<(DwmThumb thumb, QsRect r)>();
        foreach (var cls in new[] { "Progman", "Shell_TrayWnd" })
        {
            nint w = FindWindowW(cls, null);
            if (w == 0 || !GetWindowRect(w, out var r)) continue;
            if (DwmThumb.Register(self, w) is { } th) { rects.Add((th, r)); _modeThumbs.Add(th); }
        }
        _modeMark.StartAnimation("Opacity", G.Scalar(1, 0, Reduced ? 1 : 160, EaseOutCss));
        if (rects.Count == 0 || Reduced) { done(); After(200, ModeShiftReset); return; }
        long start = NowMs;
        var timer = _dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.Tick += (_, _) =>
        {
            double t = Math.Clamp((NowMs - start) / ModeOutMs, 0, 1), e = 1 - Math.Pow(1 - t, 3);
            foreach (var (th, r) in rects) th.Place(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, e);
            if (t < 1) return;
            timer.Stop();
            done();
            After(150, ModeShiftReset);
        };
        timer.Start();
    }

    private void ModeShiftReset()
    {
        foreach (var t in _modeThumbs) t.Dispose();
        _modeThumbs.Clear();
        if (_mode != null) _mode.IsVisible = false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowW(string cls, string? title);
}
