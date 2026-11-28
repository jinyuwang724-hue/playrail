// Cocoon 主题色：外观与主题里的颜色珠，和「自定义」打开的取色板（ModalSheet）。
//   11-28 jinyu：界面本身就是说明，不要解释文字。取色板只留取色：
//   顶栏中间两颗珠子 = 渐变的两端（LB / RB 或点按切换，选中的那颗大一圈带环），一段渐变条把它们连起来；
//   下面一整块明度 × 纯度画布和一条色相带。颜色一动，焦点框、Y 胶囊当场跟着变色。
using System;
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using Rect = Windows.Foundation.Rect;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class CtpState
    {
        public required ContainerVisual Root, Box, Parent;
        public required SpriteVisual Scrim, Sheet, Body;
        public required CurSet Cur;
        public CompositionDrawingSurface? SheetS, BodyS;
        public CanvasBitmap? CanvasBmp, HueBmp;
        public int CachedCanvasHueKey = -999;

        public int StopIndex; // 0 = 渐变起点, 1 = 渐变终点
        public float L0, C0, H0;
        public float L1, C1, H1;

        public int Focus = 1;   // 0 = 明度 × 纯度画布, 1 = 色相带
        public bool TouchMode;
        public bool ScaledSelf;
        public bool Closing;

        public uint DragId;
        public int DragTarget; // 0 none, 1 画布, 2 色相带
    }

    private CtpState? _ctp;
    private int _cpSwatchFocus;

    private bool CtpOpenNow => _ctp is { Closing: false };

    // ---------------- 几何（1920×1200 画布，ModalSheet 96..1824 × 70..1130） ----------------
    private const float CtpCanvasX = 152f, CtpCanvasW = 1616f, CtpCanvasY = 222f, CtpCanvasH = 716f, CtpCanvasR = 34f;
    private const float CtpHueCy = 1030f, CtpHueTrack = 26f, CtpHueInset = 32f;
    private const float CtpStopCx = 960f, CtpStopGap = 132f, CtpStopCy = 141f;
    private const float CtpMaxChroma = 0.28f;
    private const float CtpMinL = 0.28f, CtpMaxL = 0.90f;

    private static float CtpHueX0 => CtpCanvasX + CtpHueInset;
    private static float CtpHueX1 => CtpCanvasX + CtpCanvasW - CtpHueInset;

    // ---------------- 颜色圆点 ----------------

        /// <summary>扁平颜色圆点（11-28 jinyu：不要珠子质感）：左上起点色 → 右下终点色的对角渐变，一道极淡的描边防止浅色融进白卡。shadow 参数保留不用。</summary>
    private void CpDrawOrb(CanvasDrawingSession ds, float cx, float cy, float r, Color c1, Color c2, bool shadow = true)
    {
        using (var body = new CanvasLinearGradientBrush(ds.Device, c1, c2) { StartPoint = new Vector2(cx - r * 0.7f, cy - r * 0.7f), EndPoint = new Vector2(cx + r * 0.7f, cy + r * 0.7f) })
            ds.FillCircle(cx, cy, r, body);
        ds.DrawCircle(cx, cy, r - 0.5f, Color.FromArgb(22, 0, 0, 0), 1f);
    }

    /// <summary>选中的珠子外面一圈细环（主题深色），和珠子之间留一道缝。</summary>
    private static void CpOrbRing(CanvasDrawingSession ds, float cx, float cy, float r) =>
        ds.DrawCircle(cx, cy, r + 10f, CpAccentInk, 3.5f);

    // ---------------- 打开 / 关闭 / 保存 ----------------

    private void CtpOpen()
    {
        if (CtpOpenNow) return;
        ContainerVisual? parent = null;
        bool scaleSelf = false;
        if (CpActive && CpHost is { } host && _cpViews.TryGetValue(host, out var cpv))
        {
            parent = cpv.Root;
        }
        else if (_st != null && _ccOpen)
        {
            parent = _st;
            scaleSelf = true;
        }
        if (parent == null) return;

        CtpDispose();
        Cue(Sfx.ModalShow);

        // 从「自定义」那颗珠子的颜色开始（行上看到什么，打开就是什么）
        var sCol = CocoonPalette.ParseHex(_prefs.CocoonCustomStart, CocoonPalette.Presets[0].Start);
        var eCol = CocoonPalette.ParseHex(_prefs.CocoonCustomEnd, CocoonPalette.Presets[0].End);
        var (l0, c0, h0) = CocoonPalette.SrgbToOklch(sCol);
        var (l1, c1, h1) = CocoonPalette.SrgbToOklch(eCol);

        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        if (scaleSelf) root.Scale = new Vector3(_ccK / 1.5f, _ccK / 1.5f, 1f);

        var scrim = G.Sprite(G.Solid(RmScrim), relative: false);
        scrim.Size = new Vector2(CsW, CsH);

        var box = G.Container(relative: false);
        box.Size = new Vector2(CsW, CsH);
        box.CenterPoint = new Vector3(RsX + RsW / 2f, RsY + RsH / 2f, 0f);

        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var sheet = Sp();
        var body = Sp();
        box.Children.InsertAtTop(sheet);
        box.Children.InsertAtTop(body);

        root.Children.InsertAtTop(scrim);
        root.Children.InsertAtTop(box);
        var cur = CurNew(root);
        parent.Children.InsertAtTop(root);

        var st = new CtpState
        {
            Root = root,
            Box = box,
            Parent = parent,
            Scrim = scrim,
            Sheet = sheet,
            Body = body,
            Cur = cur,
            L0 = Math.Clamp(l0, CtpMinL, CtpMaxL),
            C0 = Math.Clamp(c0, 0f, CtpMaxChroma),
            H0 = h0,
            L1 = Math.Clamp(l1, CtpMinL, CtpMaxL),
            C1 = Math.Clamp(c1, 0f, CtpMaxChroma),
            H1 = h1,
            Focus = 1,
            ScaledSelf = scaleSelf,
        };
        _ctp = st;

        if (CpHost is { } ch && _cpViews.TryGetValue(ch, out var outerView) && outerView.Cur.Shown)
            CurShow(outerView.Cur, false, 60);

        CtpPaintSheetBg(st);
        CtpSyncPreview(st);
        CtpPaint(st);
        CtpUpdateFocus(st, move: false);

        if (!Reduced)
        {
            scrim.Opacity = 0;
            scrim.StartAnimation("Opacity", CsS(1, 200, Bezier.EaseOut));
            box.Opacity = 0;
            box.Scale = new Vector3(0.94f, 0.94f, 1);
            box.StartAnimation("Opacity", CsS(1, 90, Bezier.EaseOut));
            box.StartAnimation("Scale", CsV3(Vector3.One, 170, new Bezier(0.22f, 1f, 0.36f, 1f)));
        }
    }

    private void CtpClose(bool commit)
    {
        if (_ctp is not { Closing: false } st) return;
        st.Closing = true;
        if (commit)
        {
            var (cStart, cEnd) = CtpColors(st);
            Cue(Sfx.Positive);
            ApplyCocoonCustom(CocoonPalette.ToHex(cStart), CocoonPalette.ToHex(cEnd), 0);
        }
        else
        {
            Cue(Sfx.Back);
            CocoonPalette.ApplyFromPrefs(_prefs);
            RefreshCocoonThemeVisuals();
        }

        CurShow(st.Cur, false, 50);
        if (CpHost is { } ch && _cpViews.TryGetValue(ch, out var outerView) && !_cpTouchMode)
        {
            outerView.Cur.Shown = false;
            CpPaint();
        }

        if (Reduced)
        {
            CtpDispose();
            return;
        }
        st.Scrim.StartAnimation("Opacity", CsS(0, 140, Bezier.EaseOut));
        st.Box.StartAnimation("Opacity", CsS(0, 110, Bezier.EaseOut));
        st.Box.StartAnimation("Scale", CsV3(new Vector3(0.95f, 0.95f, 1), 130, Bezier.EaseOut));
        var captured = st;
        After(150, () => { if (_ctp == captured) CtpDispose(); });
    }

    private void CtpDispose()
    {
        if (_ctp is not { } st) return;
        _ctp = null;
        try
        {
            st.Parent.Children.Remove(st.Root);
            st.SheetS?.Dispose();
            st.BodyS?.Dispose();
            st.CanvasBmp?.Dispose();
            st.HueBmp?.Dispose();
            st.Root.Dispose();
        }
        catch (ObjectDisposedException) { }
    }

    // ---------------- 状态与色彩同步 ----------------

    private (Color Start, Color End) CtpColors(CtpState st) =>
        (CocoonPalette.OklchToSrgb(st.L0, st.C0, st.H0), CocoonPalette.OklchToSrgb(st.L1, st.C1, st.H1));

    private (float L, float C, float H) CtpActiveStop(CtpState st) =>
        st.StopIndex == 0 ? (st.L0, st.C0, st.H0) : (st.L1, st.C1, st.H1);

    private void CtpSetActiveStop(CtpState st, float l, float c, float h)
    {
        l = Math.Clamp(l, CtpMinL, CtpMaxL);
        c = Math.Clamp(c, 0f, CtpMaxChroma);
        h = CocoonPalette.WrapHue(h);
        if (st.StopIndex == 0) { st.L0 = l; st.C0 = c; st.H0 = h; }
        else { st.L1 = l; st.C1 = c; st.H1 = h; }
        CtpSyncPreview(st);
    }

    private void CtpSwapStops(CtpState st)
    {
        (st.L0, st.L1) = (st.L1, st.L0);
        (st.C0, st.C1) = (st.C1, st.C0);
        (st.H0, st.H1) = (st.H1, st.H0);
        Cue(Sfx.Tab);
        CtpSyncPreview(st);
        CtpPaint(st);
    }

    private void CtpSyncPreview(CtpState st)
    {
        var (s, e) = CtpColors(st);
        var preview = CocoonPalette.ColorsEqual(s, CocoonPalette.Presets[0].Start) && CocoonPalette.ColorsEqual(e, CocoonPalette.Presets[0].End)
            ? CocoonPalette.DefaultTheme
            : CocoonPalette.DeriveTheme(s, e);
        CocoonPalette.SetPreview(preview);
        CurClearCache();
        CurRefreshTheme(st.Cur);
    }

    // ---------------- 位图生成（OKLCH 2D 画布 & 360° 等感知色相带） ----------------

    private CanvasBitmap CtpEnsureCanvasBmp(CtpState st, ICanvasResourceCreator dev, float hue)
    {
        int hKey = (int)MathF.Round(CocoonPalette.WrapHue(hue) * 2f);
        if (st.CanvasBmp != null && st.CachedCanvasHueKey == hKey) return st.CanvasBmp;
        st.CanvasBmp?.Dispose();
        st.CachedCanvasHueKey = hKey;

        const int W = 256, H = 120;
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            float l = CtpMaxL - (y / (float)(H - 1)) * (CtpMaxL - CtpMinL);
            for (int x = 0; x < W; x++)
            {
                float c = (x / (float)(W - 1)) * CtpMaxChroma;
                var col = CocoonPalette.OklchToSrgb(l, c, hue);
                int idx = (y * W + x) * 4;
                px[idx] = col.B;
                px[idx + 1] = col.G;
                px[idx + 2] = col.R;
                px[idx + 3] = 255;
            }
        }
        st.CanvasBmp = CanvasBitmap.CreateFromBytes(dev, px, W, H, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        return st.CanvasBmp;
    }

    private CanvasBitmap CtpEnsureHueBmp(CtpState st, ICanvasResourceCreator dev)
    {
        if (st.HueBmp != null) return st.HueBmp;
        const int W = 360, H = 4;
        var px = new byte[W * H * 4];
        for (int x = 0; x < W; x++)
        {
            var col = CocoonPalette.OklchToSrgb(0.68f, 0.175f, x);
            for (int y = 0; y < H; y++)
            {
                int idx = (y * W + x) * 4;
                px[idx] = col.B;
                px[idx + 1] = col.G;
                px[idx + 2] = col.R;
                px[idx + 3] = 255;
            }
        }
        st.HueBmp = CanvasBitmap.CreateFromBytes(dev, px, W, H, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        return st.HueBmp;
    }

    // ---------------- 绘制 ----------------

    private void CtpPaintSheetBg(CtpState st)
    {
        var dev = G.Device;
        float R = CsRaster, pad = RsPad, w = RsW + pad * 2, h = RsH + pad * 2;
        st.SheetS?.Dispose();
        st.SheetS = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        using (var ds = CanvasComposition.CreateDrawingSession(st.SheetS))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - RsX, pad - RsY) * Matrix3x2.CreateScale(R);
            RsSoftShadow(ds, RsX, RsY + 12, RsW, RsH, RsR, 22, 0.35f);
            using var bg = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xF8, 0xF8, 0xFB), Color.FromArgb(255, 0xEC, 0xED, 0xF3))
            {
                StartPoint = new Vector2(0, RsY),
                EndPoint = new Vector2(0, RsY + RsH),
            };
            ds.FillRoundedRectangle(RsX, RsY, RsW, RsH, RsR, RsR, bg);
        }
        ((CompositionSurfaceBrush)st.Sheet.Brush).Surface = st.SheetS;
        st.Sheet.Size = new Vector2(w, h);
        st.Sheet.Offset = new Vector3(RsX - pad, RsY - pad, 0);
    }

    // ---------------- 绘制 ----------------

    private static (float X, float Y) CtpStopCenter(int i) => (CtpStopCx + (i == 0 ? -CtpStopGap : CtpStopGap), CtpStopCy);

    private void CtpPaint(CtpState st)
    {
        var dev = G.Device;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref st.BodyS, st.Body, RsX, RsY, RsW, RsH));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-RsX, -RsY) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;

        var (colStart, colEnd) = CtpColors(st);
        var (actL, actC, actH) = CtpActiveStop(st);
        var act = st.StopIndex == 0 ? colStart : colEnd;

        // 顶栏：B ← 和 Y ✓；中间两颗端点珠子，一段渐变条连着
        RsPaintPills(ds);
        var (ax, ay) = CtpStopCenter(0);
        var (bx, _) = CtpStopCenter(1);
        float ba = ax + (st.StopIndex == 0 ? 42f : 18f), bb = bx - (st.StopIndex == 1 ? 42f : 18f);   // 渐变条不钻进选中环和珠子之间的缝
        using (var bar = new CanvasLinearGradientBrush(dev, colStart, colEnd) { StartPoint = new Vector2(ba, ay), EndPoint = new Vector2(bb, ay) })
            ds.FillRoundedRectangle(ba, ay - 7f, bb - ba, 14f, 7f, 7f, bar);
        CgpDrawShoulderBadge(ds, ax - 66f - 60f, ay - 24f, 60, 48, "LB");
        CgpDrawShoulderBadge(ds, bx + 66f, ay - 24f, 60, 48, "RB");
        for (int i = 0; i < 2; i++)
        {
            var (ox, oy) = CtpStopCenter(i);
            var c = i == 0 ? colStart : colEnd;
            bool on = st.StopIndex == i;
            float r = on ? 31f : 22f;
            if (on) ds.DrawCircle(ox, oy, r + 8f, CpAccentInk, 3.5f);
            CpDrawOrb(ds, ox, oy, r, c, c, shadow: on);
        }

        // 明度 × 纯度画布（当前色相）
        var canvasBmp = CtpEnsureCanvasBmp(st, dev, actH);
        using (var canvasClip = CanvasGeometry.CreateRoundedRectangle(dev, CtpCanvasX, CtpCanvasY, CtpCanvasW, CtpCanvasH, CtpCanvasR, CtpCanvasR))
        {
            using (ds.CreateLayer(1f, canvasClip))
                ds.DrawImage(canvasBmp, new Rect(CtpCanvasX, CtpCanvasY, CtpCanvasW, CtpCanvasH), canvasBmp.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
            ds.DrawRoundedRectangle(CtpCanvasX + 0.75f, CtpCanvasY + 0.75f, CtpCanvasW - 1.5f, CtpCanvasH - 1.5f, CtpCanvasR, CtpCanvasR, Color.FromArgb(40, 0, 0, 0), 1.5f);
        }
        float px = CtpCanvasX + Math.Clamp(actC / CtpMaxChroma, 0f, 1f) * CtpCanvasW;
        float py = CtpCanvasY + Math.Clamp((CtpMaxL - actL) / (CtpMaxL - CtpMinL), 0f, 1f) * CtpCanvasH;
        CtpKnob(ds, px, py, 30f, act);

        // 色相带
        float x0 = CtpHueX0, x1 = CtpHueX1, hy = CtpHueCy;
        var hueBmp = CtpEnsureHueBmp(st, dev);
        using (var clip = CanvasGeometry.CreateRoundedRectangle(dev, x0, hy - CtpHueTrack / 2f, x1 - x0, CtpHueTrack, CtpHueTrack / 2f, CtpHueTrack / 2f))
        {
            using (ds.CreateLayer(1f, clip))
                ds.DrawImage(hueBmp, new Rect(x0, hy - CtpHueTrack / 2f, x1 - x0, CtpHueTrack), hueBmp.Bounds, 1f, CanvasImageInterpolation.Linear);
            ds.DrawRoundedRectangle(x0 + 0.5f, hy - CtpHueTrack / 2f + 0.5f, x1 - x0 - 1f, CtpHueTrack - 1f, CtpHueTrack / 2f, CtpHueTrack / 2f, Color.FromArgb(40, 0, 0, 0), 1f);
        }
        CtpKnob(ds, x0 + Math.Clamp(actH / 360f, 0f, 1f) * (x1 - x0), hy, 28f, act);
    }

    /// <summary>画布和色相带上的取色点：白色厚边圆，里面是当前颜色。</summary>
    private void CtpKnob(CanvasDrawingSession ds, float x, float y, float r, Color fill)
    {
        RsSoftShadow(ds, x - r, y - r + 4, r * 2, r * 2, r, 7, 0.32f);
        ds.FillCircle(x, y, r, Colors.White);
        ds.FillCircle(x, y, r - 7f, fill);
        ds.DrawCircle(x, y, r - 7f, Color.FromArgb(36, 0, 0, 0), 1f);
    }

    // ---------------- 焦点框定位 ----------------

    private static Rect CtpFocusRect(int focus, out float rad)
    {
        if (focus == 0) { rad = CtpCanvasR; return new Rect(CtpCanvasX, CtpCanvasY, CtpCanvasW, CtpCanvasH); }
        rad = 40f;
        return new Rect(CtpCanvasX, CtpHueCy - 40f, CtpCanvasW, 80f);
    }

    private void CtpUpdateFocus(CtpState st, bool move)
    {
        if (st.TouchMode)
        {
            if (st.Cur.Shown) CurShow(st.Cur, false, 60);
            return;
        }
        bool first = !st.Cur.Shown;
        var r = CtpFocusRect(st.Focus, out float rad);
        CurToCocoon(st.Cur, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height, rad, move: move && !first);
        if (first) CurShow(st.Cur, true, 120, 30);
    }

    private void CtpFocusTo(CtpState st, int focus)
    {
        st.Focus = focus;
        CuePan(0.5f);
        CtpUpdateFocus(st, move: true);
    }

    // ---------------- 手柄 / 键盘 ----------------
    // 画布上：十字键直接挪取色点（↑↓ 明度、←→ 纯度），到底再按 ↓ 落到色相带；色相带上 ←→ 转色相，↑ 回画布。
    // A 在画布和色相带之间跳；LB / RB 换端点；Y 应用；B 放弃；X 两端对调。

    private bool CtpMove(Direction dir)
    {
        if (_ctp is not { Closing: false } st) return false;
        bool wasTouch = st.TouchMode;
        st.TouchMode = false;
        if (wasTouch) { CtpUpdateFocus(st, move: false); return true; }
        var (l, c, h) = CtpActiveStop(st);

        if (st.Focus == 0)
        {
            if (dir == Direction.Down && l <= CtpMinL + 1e-4f) { CtpFocusTo(st, 1); return true; }
            float dl = dir == Direction.Up ? 0.02f : dir == Direction.Down ? -0.02f : 0f;
            float dc = dir == Direction.Right ? 0.01f : dir == Direction.Left ? -0.01f : 0f;
            float nl = Math.Clamp(l + dl, CtpMinL, CtpMaxL);
            float nc = Math.Clamp(c + dc, 0f, CtpMaxChroma);
            if (MathF.Abs(nl - l) < 1e-4f && MathF.Abs(nc - c) < 1e-4f) { Cue(Sfx.Bump); return true; }
            Cue(dl > 0 || dc > 0 ? Sfx.SliderUp : Sfx.SliderDown);
            CtpSetActiveStop(st, nl, nc, h);
            CtpPaint(st);
            return true;
        }

        if (dir == Direction.Up) { CtpFocusTo(st, 0); return true; }
        if (dir == Direction.Down) { Cue(Sfx.Bump); return true; }
        int d = dir == Direction.Left ? -1 : 1;
        Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown);
        CtpSetActiveStop(st, l, c, h + d * 4f);
        CtpPaint(st);
        return true;
    }

    private bool CtpActivate()
    {
        if (_ctp is not { Closing: false } st) return false;
        st.TouchMode = false;
        CtpFocusTo(st, st.Focus == 0 ? 1 : 0);
        return true;
    }

    private bool CtpBack()
    {
        if (_ctp is not { Closing: false }) return false;
        CtpClose(commit: false);
        return true;
    }

    private bool CtpPadY()
    {
        if (_ctp is not { Closing: false }) return false;
        CtpClose(commit: true);
        return true;
    }

    private bool CtpPadX()
    {
        if (_ctp is not { Closing: false } st) return false;
        CtpSwapStops(st);
        return true;
    }

    private bool CtpSwitchTab(int delta)
    {
        if (_ctp is not { Closing: false } st) return false;
        int next = Math.Clamp(st.StopIndex + delta, 0, 1);
        if (next == st.StopIndex) { Cue(Sfx.Bump); return true; }
        st.StopIndex = next;
        Cue(Sfx.Tab);
        CtpPaint(st);
        return true;
    }

    // ---------------- 触控 / 鼠标 ----------------

    private bool CtpPointerDown(PointerRoutedEventArgs e, Microsoft.UI.Input.PointerPoint pt)
    {
        if (_ctp is not { Closing: false } st) return false;
        var (x, y) = CpLocal(pt.Position.X, pt.Position.Y);
        e.Handled = true;
        st.TouchMode = true;
        if (st.Cur.Shown) CurShow(st.Cur, false, 60);

        // 遮罩上：放弃
        if (x < RsX || x > RsX + RsW || y < RsY || y > RsY + RsH) { CtpClose(commit: false); return true; }

        // 顶栏 B ← / Y ✓
        if (x is >= 120 and <= 280 && y is >= 92 and <= 190) { CtpClose(commit: false); return true; }
        if (x is >= 1640 and <= 1800 && y is >= 92 and <= 190) { CtpClose(commit: true); return true; }

        // 两颗端点珠子（连同旁边的 LB / RB）
        if (y is >= 92 and <= 190)
        {
            int hitStop = x >= CtpStopCx - CtpStopGap - 140f && x < CtpStopCx ? 0 : x >= CtpStopCx && x <= CtpStopCx + CtpStopGap + 140f ? 1 : -1;
            if (hitStop >= 0)
            {
                if (st.StopIndex != hitStop) { st.StopIndex = hitStop; Cue(Sfx.Tab); CtpPaint(st); }
                return true;
            }
        }

        int target = x >= CtpCanvasX && x <= CtpCanvasX + CtpCanvasW && y >= CtpCanvasY && y <= CtpCanvasY + CtpCanvasH ? 1
                   : x >= CtpCanvasX && x <= CtpCanvasX + CtpCanvasW && y >= CtpHueCy - 44f && y <= CtpHueCy + 44f ? 2 : 0;
        if (target > 0)
        {
            st.Focus = target == 1 ? 0 : 1;
            st.DragId = pt.PointerId;
            st.DragTarget = target;
            _rootEl.CapturePointer(e.Pointer);
            CtpApplyDrag(st, x, y);
        }
        return true;
    }

    private bool CtpPointerMove(PointerRoutedEventArgs e)
    {
        if (_ctp is not { Closing: false } st || st.DragTarget == 0 || e.Pointer.PointerId != st.DragId) return false;
        e.Handled = true;
        var p = e.GetCurrentPoint(_rootEl).Position;
        var (x, y) = CpLocal(p.X, p.Y);
        CtpApplyDrag(st, x, y);
        return true;
    }

    private bool CtpPointerUp(PointerRoutedEventArgs e)
    {
        if (_ctp is not { } st || st.DragTarget == 0 || e.Pointer.PointerId != st.DragId) return false;
        st.DragTarget = 0;
        _rootEl.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        return true;
    }

    private void CtpApplyDrag(CtpState st, float x, float y)
    {
        var (l, c, h) = CtpActiveStop(st);
        if (st.DragTarget == 1)
        {
            float nc = Math.Clamp((x - CtpCanvasX) / CtpCanvasW, 0f, 1f) * CtpMaxChroma;
            float nl = CtpMaxL - Math.Clamp((y - CtpCanvasY) / CtpCanvasH, 0f, 1f) * (CtpMaxL - CtpMinL);
            CtpSetActiveStop(st, nl, nc, h);
        }
        else if (st.DragTarget == 2)
        {
            float nh = Math.Clamp((x - CtpHueX0) / (CtpHueX1 - CtpHueX0), 0f, 1f) * 359.9f;
            CtpSetActiveStop(st, l, c, nh);
        }
        CtpPaint(st);
    }

    // ---------------- 外观与主题：一排颜色珠 + 「自定义」行 ----------------

    private const float CpSwatchRowH = 256f;
    private const int CpSwatchCols = 8, CpSwatchTotal = 8;

    private static (float X, float Y, float W, float H) CpSwatchTileRect(float rowY, int index)
    {
        const float gx0 = 28f, top = 22f, th = 212f;
        float tw = (CpListW - gx0 * 2f) / CpSwatchCols;
        return (gx0 + (index % CpSwatchCols) * tw, rowY + top + (index / CpSwatchCols) * th, tw, th);
    }

    private void CpPaintSwatchGalleryRow(CanvasDrawingSession ds, PerfRow r, float y)
    {
        int active = _prefs.CocoonTheme >= 0 && _prefs.CocoonTheme < CocoonPalette.Presets.Length ? _prefs.CocoonTheme : -1;
        for (int i = 0; i < CpSwatchTotal; i++)
        {
            var (tx, ty, tw, _) = CpSwatchTileRect(y, i);
            var p = CocoonPalette.Presets[i];
            float cx = tx + tw / 2f, cy = ty + 82f, rad = 46f;
            bool sel = i == active;
            if (sel) CpOrbRing(ds, cx, cy, rad);
            CpDrawOrb(ds, cx, cy, rad, p.Start, p.End);
            using var nf = new CanvasTextFormat { FontFamily = RmFont(p.Name), FontSize = 25, FontWeight = RsWt(p.Name, (ushort)(sel ? 600 : 500)), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(p.Name, new Rect(tx, ty + 150, tw, 40), sel ? CpAccentInk : RsSub, nf);
        }
    }

    private void CpPaintColorPickerEntryRow(CanvasDrawingSession ds, PerfRow r, float y, float h)
    {
        float cy = y + h / 2f, R = CpListW - 34f;
        using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 36, FontWeight = CpTitleWt, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(r.Title, new Rect(44, y, R - 200, h), RmInk, tf);

        var c1 = CocoonPalette.ParseHex(_prefs.CocoonCustomStart, CocoonPalette.Presets[0].Start);
        var c2 = CocoonPalette.ParseHex(_prefs.CocoonCustomEnd, CocoonPalette.Presets[0].End);
        float rad = 28f, ocx = R - 44f - rad;
        if (_prefs.CocoonTheme == -1) CpOrbRing(ds, ocx, cy, rad);
        CpDrawOrb(ds, ocx, cy, rad, c1, c2);

        using var pb = new CanvasPathBuilder(ds.Device);
        pb.BeginFigure(R - 10, cy - 9); pb.AddLine(R, cy); pb.AddLine(R - 10, cy + 9); pb.EndFigure(CanvasFigureLoop.Closed);
        using var tri = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(tri, RmInk);
        ds.DrawGeometry(tri, RmInk, 3, RwRoundStroke);
    }

    private static void VerifyCocoonColorScience()
    {
        var ceramic = Color.FromArgb(255, 0xFD, 0xFD, 0xFE);
        // 1. 默认预设零偏差还原
        var def = CocoonPalette.Presets[0];
        if (def.Start.R != 0x86 || def.Start.G != 0xA7 || def.Start.B != 0xFD || def.End.R != 0x80 || def.End.G != 0x37 || def.End.B != 0xFF)
            throw new InvalidOperationException("Default Nebula preset hex mismatch");
        // 2. 全部 8 套预设：OKLCH 往返精度与陶瓷白卡 WCAG 对比度 >= 4.2:1
        for (int i = 0; i < CocoonPalette.Presets.Length; i++)
        {
            var p = CocoonPalette.Presets[i];
            var (l, c, h) = CocoonPalette.SrgbToOklch(p.Start);
            var rt = CocoonPalette.OklchToSrgb(l, c, h);
            if (Math.Abs(rt.R - p.Start.R) > 2 || Math.Abs(rt.G - p.Start.G) > 2 || Math.Abs(rt.B - p.Start.B) > 2)
                throw new InvalidOperationException($"OKLCH roundtrip drift on {p.Id}");
            var th = i == 0 ? CocoonPalette.DefaultTheme : CocoonPalette.DeriveTheme(p.Start, p.End);
            float cr = CocoonPalette.ContrastRatio(th.Ink, ceramic);
            if (cr < 4.2f)
                throw new InvalidOperationException($"Contrast too low ({cr:F2}:1) on {p.Id}");
        }
        // 3. 极端高明度自定义色（纯白 / 亮柠黄）依然锁定可读对比度
        foreach (var extreme in new[] { Color.FromArgb(255, 255, 244, 79), Colors.White })
        {
            var th = CocoonPalette.DeriveTheme(extreme, extreme);
            float cr = CocoonPalette.ContrastRatio(th.Ink, ceramic);
            if (cr < 4.2f)
                throw new InvalidOperationException($"Extreme custom color contrast failed ({cr:F2}:1)");
        }
        // 4. OKLCH MixFlow 跨色相插值不发灰（青 → 紫中点保持高彩度）
        var mid = CocoonPalette.LerpOklch(Color.FromArgb(255, 0x56, 0xDF, 0xDF), Color.FromArgb(255, 0x8E, 0x35, 0xF5), 0.5f);
        var (_, midC, _) = CocoonPalette.SrgbToOklch(mid);
        if (midC < 0.11f)
            throw new InvalidOperationException($"OKLCH midpoint lost chroma ({midC:F3})");
        Log.Info($"[PASS] CocoonPalette color science verified ({CocoonPalette.Presets.Length} presets, WCAG >= 4.2:1, mud-free OKLCH)");
    }

}
