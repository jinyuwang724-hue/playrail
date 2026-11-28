// Cocoon 暗色模式（11-28 jinyu：外观与主题里一个开关；不能是纯黑）。
//   各页写死的浅色中性色统一经 Ck() 换算：浅底（页面、卡片、分隔线）→ 深灰（页面约 #1F2024、卡片约 #27282D，留一点原来的冷调），
//   深字 → 浅字，中灰（图标、键位章）落在中间。带颜色的（主题色、红色、封面）不动；主题色里的墨色和选中底色由 CocoonPalette 另算。
//   用合成画刷（页面底色渐变、纯色精灵）的地方走 CkSolid / CkStop 登记，切换时 CkRetint() 原地改色，不用重建页面。
using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using System.Threading.Tasks;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static Color Ck(Color light) => CocoonPalette.Ck(light);

    // 强引用：合成对象的托管包装可能先被回收（原生对象还在），弱引用会把还在用的色标丢掉。页面都是缓存复用的，数量有限。
    private static readonly List<(CompositionColorBrush Brush, Color Light)> _ckBrushes = new();
    private static readonly List<(CompositionColorGradientStop Stop, Color Light)> _ckStops = new();

    /// <summary>浅色中性纯色画刷：按当前明暗取色，并登记以便切换时改色。</summary>
    private CompositionColorBrush CkSolid(Color light)
    {
        var b = G.Solid(Ck(light));
        if (CocoonPalette.IsNeutral(light)) _ckBrushes.Add((b, light));
        return b;
    }

    /// <summary>渐变色标（页面底色、顶部渐隐）：同上。</summary>
    private CompositionColorGradientStop CkStop(float pos, Color light)
    {
        var s = G.C.CreateColorGradientStop(pos, Ck(light));
        if (CocoonPalette.IsNeutral(light)) _ckStops.Add((s, light));
        return s;
    }

    /// <summary>明暗切换后：登记过的画刷和色标原地换色（已释放的顺手清掉）。</summary>
    private static void CkRetint()
    {
        _ckBrushes.RemoveAll(e => { try { e.Brush.Color = Ck(e.Light); return false; } catch (ObjectDisposedException) { return true; } });
        _ckStops.RemoveAll(e => { try { e.Stop.Color = Ck(e.Light); return false; } catch (ObjectDisposedException) { return true; } });
    }

    /// <summary>录屏自测用：设成 on（null 不改），返回原来的值。</summary>
    internal bool ProbeDark(bool? on)
    {
        bool was = _prefs.CocoonDark;
        if (on is bool v) ApplyCocoonDark(v);   // 自测：直接换，不走淡变（进程可能马上被收掉）
        return was;
    }

    private void SetCocoonDark(bool on)
    {
        if (_prefs.CocoonDark == on) return;
        CkCrossfade(() => ApplyCocoonDark(on));
    }

    private void ApplyCocoonDark(bool on)
    {
        if (_prefs.CocoonDark == on) return;
        SaveUi(_prefs with { CocoonDark = on });
        CkRetint();
        if (_rsBox is { } box)   // 改名输入框是缓存的：主题和字色跟着换
        {
            box.RequestedTheme = on ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light;
            var ink = new Microsoft.UI.Xaml.Media.SolidColorBrush(RmInk);
            foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) box.Resources[key] = ink;
            box.Foreground = ink;
        }
        RefreshCocoonThemeVisuals();
    }

    // ---------------- 暗色材质（11-28 jinyu：不能只是把颜色调暗，组件要按暗色重新做质感） ----------------
    // 暗底上分层不靠灰度差，而靠：上沿亮、下沿几乎看不见的细边（光从上方来），比浅色更深更大的投影，
    // 凸起的东西（卡片、圆钮）顶上一层很淡的光，凹下去的东西（开关底、下拉、滑轨）顶上压一道内阴影、下沿一条细亮线。

    private static Color DkRgb(uint rgb, byte a = 255) => Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF));
    private static Color DkWhite(byte a) => Color.FromArgb(a, 255, 255, 255);
    private static Color DkBlack(byte a) => Color.FromArgb(a, 0, 0, 0);

    /// <summary>暗色卡片：#303137 → #28292E 竖向渐变，顶上 48 px 白 5 % 的光，分隔线 = 深槽 + 下面一条白 5 % 的细线，边是上亮下暗的渐变细线。</summary>
    private void DkCard(CanvasDrawingSession ds, float x, float y, float w, float h, float r, IEnumerable<float>? seps, float sepH = 4)
    {
        var dev = ds.Device;
        RsSoftShadow(ds, x + 2, y + 14, w - 4, h, r, 18, 0.45f);
        RsSoftShadow(ds, x, y + 2, w, h, r, 3, 0.35f);
        using var card = CanvasGeometry.CreateRoundedRectangle(dev, x, y, w, h, r, r);
        using (var fill = new CanvasLinearGradientBrush(dev, DkRgb(0x303137), DkRgb(0x28292E)) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + h) })
            ds.FillGeometry(card, fill);
        using (ds.CreateLayer(1, card))
        {
            using (var sheen = new CanvasLinearGradientBrush(dev, DkWhite(14), DkWhite(0)) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + 48) })
                ds.FillRectangle(x, y, w, 48, sheen);
            if (seps != null)
                foreach (var sy in seps)
                {
                    ds.FillRectangle(x, sy, w, MathF.Max(1.5f, sepH - 1.5f), DkRgb(0x1C1D21));
                    ds.FillRectangle(x, sy + MathF.Max(1.5f, sepH - 1.5f), w, 1.2f, DkWhite(13));
                }
        }
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0, Color = DkWhite(60) },
            new CanvasGradientStop { Position = MathF.Min(0.45f, 60 / MathF.Max(h, 1)), Color = DkWhite(16) },
            new CanvasGradientStop { Position = 1, Color = DkWhite(7) },
        };
        using var edge = new CanvasLinearGradientBrush(dev, stops) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + h) };
        ds.DrawRoundedRectangle(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, r, r, edge, 1.5f);
    }

    /// <summary>暗色凹槽（关着的开关底、下拉胶囊、滑轨）：#1B1C20 底，上沿往里一道内阴影，下沿一条白 9 % 的细亮线。</summary>
    private static void DkWell(CanvasDrawingSession ds, float x, float y, float w, float h, float r)
    {
        var dev = ds.Device;
        using var g = CanvasGeometry.CreateRoundedRectangle(dev, x, y, w, h, r, r);
        ds.FillGeometry(g, DkRgb(0x1B1C20));
        using (ds.CreateLayer(1, g))
        {
            float sh = MathF.Min(10, h * 0.4f);
            using var inner = new CanvasLinearGradientBrush(dev, DkBlack(110), DkBlack(0)) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + sh) };
            ds.FillRectangle(x, y, w, sh, inner);
        }
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0, Color = DkBlack(90) },
            new CanvasGradientStop { Position = 0.5f, Color = DkWhite(0) },
            new CanvasGradientStop { Position = 1, Color = DkWhite(26) },
        };
        using var edge = new CanvasLinearGradientBrush(dev, stops) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + h) };
        ds.DrawRoundedRectangle(x + 0.6f, y + 0.6f, w - 1.2f, h - 1.2f, MathF.Max(0, r - 0.6f), MathF.Max(0, r - 0.6f), edge, 1.2f);
    }

    /// <summary>暗色凸起圆钮（快捷开关关着、滑块两端的按钮）：#3B3C43 → #2B2C32，底下一层深投影，边上亮下暗。</summary>
    private void DkOrb(CanvasDrawingSession ds, float cx, float cy, float r)
    {
        var dev = ds.Device;
        RsSoftShadow(ds, cx - r * 0.92f, cy - r + r * 0.16f, r * 1.84f, r * 2, r, r * 0.16f, 0.55f);
        using (var fill = new CanvasLinearGradientBrush(dev, DkRgb(0x3C3D44), DkRgb(0x2B2C32)) { StartPoint = new Vector2(0, cy - r), EndPoint = new Vector2(0, cy + r) })
            ds.FillCircle(cx, cy, r, fill);
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0, Color = DkWhite(72) },
            new CanvasGradientStop { Position = 0.5f, Color = DkWhite(8) },
            new CanvasGradientStop { Position = 1, Color = DkWhite(16) },
        };
        using var rim = new CanvasLinearGradientBrush(dev, stops) { StartPoint = new Vector2(0, cy - r), EndPoint = new Vector2(0, cy + r) };
        ds.DrawCircle(cx, cy, r - 0.75f, rim, 1.5f);
    }

    /// <summary>拨动开关关着时的底：浅色 = 浅灰底 + 灰圈；暗色 = 凹槽。ring = 浅色版灰圈的线宽。</summary>
    private static void CpToggleOffTrack(CanvasDrawingSession ds, float x, float y, float w, float h, float ring)
    {
        if (CocoonPalette.Dark) { DkWell(ds, x, y, w, h, h / 2); return; }
        ds.FillRoundedRectangle(x, y, w, h, h / 2, h / 2, Color.FromArgb(255, 0xE4, 0xE5, 0xE9));
        ds.DrawRoundedRectangle(x + ring / 2, y + ring / 2, w - ring, h - ring, h / 2 - ring / 2, h / 2 - ring / 2, Color.FromArgb(255, 0x9A, 0x9C, 0xA3), ring);
    }

    /// <summary>开关圆钮：开着永远白；关着在暗色里是浅灰（白钮压在深槽里太跳）。</summary>
    private static Color CpKnobFill(bool on) => on || !CocoonPalette.Dark ? Color.FromArgb(255, 0xFA, 0xFA, 0xFA) : DkRgb(0xC9CAD2);

    /// <summary>滑轨底：浅色 = 浅灰条；暗色 = 凹槽。</summary>
    private static void CpTrackBg(CanvasDrawingSession ds, float x, float y, float w, float h)
    {
        if (CocoonPalette.Dark) { DkWell(ds, x, y, w, h, h / 2); return; }
        ds.FillRoundedRectangle(x, y, w, h, h / 2, h / 2, Color.FromArgb(255, 0xDC, 0xDC, 0xE3));
    }

    /// <summary>暗色面板 / 弹窗的边：上沿白 22 %，往下很快淡到白 4 %（光从上方来）。只画边，底色由调用方画。</summary>
    private static void DkEdge(CanvasDrawingSession ds, float x, float y, float w, float h, float r, float top = 56)
    {
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0, Color = DkWhite((byte)top) },
            new CanvasGradientStop { Position = MathF.Min(0.45f, 70 / MathF.Max(h, 1)), Color = DkWhite(14) },
            new CanvasGradientStop { Position = 1, Color = DkWhite(8) },
        };
        using var edge = new CanvasLinearGradientBrush(ds.Device, stops) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + h) };
        ds.DrawRoundedRectangle(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, r, r, edge, 1.5f);
    }

    /// <summary>暗色浮层（下拉选项卡、Y 菜单、相册菜单）：比卡片再亮一级（#37383E → #2F3035），投影更深更散。</summary>
    private void DkPop(CanvasDrawingSession ds, float x, float y, float w, float h, float r)
    {
        var dev = ds.Device;
        RsSoftShadow(ds, x, y + 16, w, h, r, 26, 0.6f);
        RsSoftShadow(ds, x, y + 2, w, h, r, 4, 0.4f);
        using var g = CanvasGeometry.CreateRoundedRectangle(dev, x, y, w, h, r, r);
        using (var fill = new CanvasLinearGradientBrush(dev, DkRgb(0x37383E), DkRgb(0x2F3035)) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + h) })
            ds.FillGeometry(g, fill);
        DkEdge(ds, x, y, w, h, r, 64);
    }

    /// <summary>暗色凸起按钮（次要按钮、胶囊）：#3A3B42 → #2E2F35，贴身投影，上亮下暗的边。</summary>
    private void DkRaised(CanvasDrawingSession ds, float x, float y, float w, float h, float r)
    {
        var dev = ds.Device;
        RsSoftShadow(ds, x, y + 5, w, h, r, 8, 0.5f);
        using var g = CanvasGeometry.CreateRoundedRectangle(dev, x, y, w, h, r, r);
        using (var fill = new CanvasLinearGradientBrush(dev, DkRgb(0x3B3C43), DkRgb(0x2E2F35)) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + h) })
            ds.FillGeometry(g, fill);
        DkEdge(ds, x, y, w, h, r, 70);
    }

    // ---------------- 明暗切换动画 ----------------
    // 11-28 jinyu：亮 / 暗切换要有颜色过渡。页面大多是 Win2D 画好的位图，重画是一下子换掉的，
    // 所以先抓一帧窗口当前的样子（Windows.Graphics.Capture，清单里有无边框截取权限），盖在最上面，
    // 底下换成新配色，再把这一帧在 480 ms 里淡掉——看到的就是旧配色平滑过渡到新配色。抓不到就直接换。

    private bool _ckFading;

    private async void CkCrossfade(Action apply)
    {
        if (Reduced || _ckFading) { apply(); return; }
        _ckFading = true;
        CanvasBitmap? shot = null;
        try { shot = await CkGrabWindow(); }
        catch (Exception ex) { Log.Warn($"theme crossfade grab: {ex.Message}"); }
        if (shot == null) { apply(); _ckFading = false; return; }
        CompositionDrawingSurface? surf = null;
        SpriteVisual? veil = null;
        try
        {
            var sz = shot.SizeInPixels;
            surf = G.Surface((int)sz.Width, (int)sz.Height);
            using (var ds = CanvasComposition.CreateDrawingSession(surf)) { ds.Clear(Microsoft.UI.Colors.Transparent); ds.DrawImage(shot); }
            var brush = G.C.CreateSurfaceBrush(surf);
            brush.Stretch = CompositionStretch.Fill;
            veil = G.C.CreateSpriteVisual();
            veil.Brush = brush;
            veil.RelativeSizeAdjustment = Vector2.One;
            _top.Children.InsertAtTop(veil);
        }
        catch (Exception ex) { Log.Warn($"theme crossfade veil: {ex.Message}"); }
        finally { shot.Dispose(); }
        apply();
        if (veil == null) { surf?.Dispose(); _ckFading = false; return; }
        var a = G.C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, 1);
        a.InsertKeyFrame(1, 0, G.Ease(new Bezier(0.33f, 0f, 0.2f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(480);
        a.DelayTime = TimeSpan.FromMilliseconds(60);   // 等新配色的重画先落到屏幕上
        veil.StartAnimation("Opacity", a);
        var v = veil; var s = surf;
        After(600, () =>
        {
            try { _top.Children.Remove(v); v.Dispose(); s?.Dispose(); } catch (ObjectDisposedException) { }
            _ckFading = false;
        });
    }

    /// <summary>抓本窗口此刻合成出来的一帧（最多等 350 ms）。</summary>
    private async Task<CanvasBitmap?> CkGrabWindow()
    {
        var item = Windows.Graphics.Capture.GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId { Value = HostWindow.AppWindow.Id.Value });
        if (item == null) return null;
        var dev = G.Device;
        var tcs = new TaskCompletionSource<CanvasBitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = Windows.Graphics.Capture.Direct3D11CaptureFramePool.CreateFreeThreaded(dev, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch { }
        try { session.IsBorderRequired = false; } catch { }
        pool.FrameArrived += (p, _) =>
        {
            using var f = p.TryGetNextFrame();
            if (f == null || tcs.Task.IsCompleted) return;
            try
            {
                using var b = CanvasBitmap.CreateFromDirect3D11Surface(dev, f.Surface);
                var rt = new CanvasRenderTarget(dev, b.SizeInPixels.Width, b.SizeInPixels.Height, 96);
                using (var ds = rt.CreateDrawingSession()) ds.DrawImage(b);
                if (!tcs.TrySetResult(rt)) rt.Dispose();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };
        session.StartCapture();
        var done = await Task.WhenAny(tcs.Task, Task.Delay(350));
        try { session.Dispose(); pool.Dispose(); } catch { }
        if (done != tcs.Task) { tcs.TrySetResult(null); if (tcs.Task.Result is { } late) late.Dispose(); return null; }
        return await tcs.Task;
    }
}
