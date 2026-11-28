// Cocoon 主题色取色板（ModalSheet，1728×1060 设计单位）：
//   基于 OKLCH 感知均匀色彩空间构建的双色渐变调色工作室，同时支持手柄（LB/RB 切起止端、十字键/摇杆调色、X 互换、Y 保存）与触控/鼠标直觉拖拽。
//   左栏：配色和谐模式（自由双色 / 自然流光 / 霞光冷暖 / 同色琉璃）+ 2D 等明度纯度取色画布 + 360° 等感知亮度色相带 + 明度/纯度精调滑杆 + 快捷操作；
//   右栏：实时渐变色卡与色彩科学指标（OKLCH 坐标、色相跨度 ΔH、WCAG 陶瓷白卡文字对比度）+ Cocoon 真实组件微缩预览台。
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
        public CocoonHarmonyMode Harmony;
        public float L0, C0, H0;
        public float L1, C1, H1;

        // 焦点区域：
        // 0..3 = 四枚和谐模式胶囊
        // 4    = 2D OKLCH 取色画布（按 A 进入/退出十字键微调）
        // 5    = 色相 H 滑杆
        // 6    = 明度 L 滑杆
        // 7    = 纯度 C 滑杆
        // 8    = 互换起止色
        // 9    = 恢复默认蓝紫
        // 10   = 应用这套配色
        public int Focus = 4;
        public bool CanvasFine;
        public bool TouchMode;
        public bool ScaledSelf;
        public bool Closing;

        public uint DragId;
        public int DragTarget; // 0 none, 1 canvas, 2 hue, 3 lightness, 4 chroma
    }

    private CtpState? _ctp;
    private int _cpSwatchFocus;

    private bool CtpOpenNow => _ctp is { Closing: false };

    // ---------------- 几何常量（1920×1200 画布内 ModalSheet 96..1824 × 70..1130） ----------------
    private const float CtpLeftX = 136f, CtpLeftW = 876f, CtpCardY = 204f, CtpCardH = 886f;
    private const float CtpRightX = 1040f, CtpRightW = 648f;
    private const float CtpInnerX = CtpLeftX + 34f, CtpInnerW = CtpLeftW - 68f; // 170, 808
    private const float CtpHarmY = 232f, CtpHarmH = 62f;
    private const float CtpCanvasY = 314f, CtpCanvasH = 316f;
    private const float CtpHueY = 652f, CtpLightY = 744f, CtpChromaY = 836f, CtpSliderH = 78f;
    private const float CtpBtnY = 988f, CtpBtnH = 72f;
    private const float CtpMaxChroma = 0.28f;
    private const float CtpMinL = 0.28f, CtpMaxL = 0.90f;

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

        var (sCol, eCol) = CocoonPalette.ActivePair(_prefs);
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
            Harmony = (CocoonHarmonyMode)Math.Clamp(_prefs.CocoonHarmony, 0, 3),
            L0 = Math.Clamp(l0, CtpMinL, CtpMaxL),
            C0 = Math.Clamp(c0, 0f, CtpMaxChroma),
            H0 = h0,
            L1 = Math.Clamp(l1, CtpMinL, CtpMaxL),
            C1 = Math.Clamp(c1, 0f, CtpMaxChroma),
            H1 = h1,
            Focus = 5, // 默认落焦在色相带，左右键即可直观变色
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
            ApplyCocoonCustom(CocoonPalette.ToHex(cStart), CocoonPalette.ToHex(cEnd), (int)st.Harmony);
            Toast($"已应用自定义主题色：{CocoonPalette.ToHex(cStart)} → {CocoonPalette.ToHex(cEnd)}");
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

        if (st.Harmony != CocoonHarmonyMode.Free)
        {
            var anchor = CocoonPalette.OklchToSrgb(l, c, h);
            var partner = CocoonPalette.ComputeHarmonyPartner(anchor, st.Harmony, anchorIsStart: st.StopIndex == 0);
            var (pl, pc, ph) = CocoonPalette.SrgbToOklch(partner);
            if (st.StopIndex == 0)
            {
                st.L1 = Math.Clamp(pl, CtpMinL, CtpMaxL);
                st.C1 = Math.Clamp(pc, 0f, CtpMaxChroma);
                st.H1 = ph;
            }
            else
            {
                st.L0 = Math.Clamp(pl, CtpMinL, CtpMaxL);
                st.C0 = Math.Clamp(pc, 0f, CtpMaxChroma);
                st.H0 = ph;
            }
        }
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
        CtpUpdateFocus(st, move: false);
    }

    private void CtpResetDefault(CtpState st)
    {
        var (l0, c0, h0) = CocoonPalette.SrgbToOklch(CocoonPalette.Presets[0].Start);
        var (l1, c1, h1) = CocoonPalette.SrgbToOklch(CocoonPalette.Presets[0].End);
        st.L0 = l0; st.C0 = c0; st.H0 = h0;
        st.L1 = l1; st.C1 = c1; st.H1 = h1;
        Cue(Sfx.Positive);
        CtpSyncPreview(st);
        CtpPaint(st);
        CtpUpdateFocus(st, move: false);
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

        const int W = 180, H = 76;
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

    private static Rect CtpStopTabRect(int idx)
    {
        const float barX = 768f, barY = 104f, barW = 846f, barH = 74f, sidePad = 78f;
        float innerW = (barW - sidePad * 2f - 10f) / 2f;
        return new Rect(barX + sidePad + idx * (innerW + 10f), barY + 6f, innerW, barH - 12f);
    }

    private static Rect CtpHarmRect(int idx)
    {
        const float gap = 12f;
        float w = (CtpInnerW - gap * 3f) / 4f;
        return new Rect(CtpInnerX + idx * (w + gap), CtpHarmY, w, CtpHarmH);
    }

    private static Rect CtpActionBtnRect(int idx)
    {
        const float gap = 14f;
        float w = (CtpInnerW - gap * 2f) / 3f;
        return new Rect(CtpInnerX + idx * (w + gap), CtpBtnY, w, CtpBtnH);
    }

    private void CtpPaint(CtpState st)
    {
        var dev = G.Device;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref st.BodyS, st.Body, RsX, RsY, RsW, RsH));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-RsX, -RsY) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;

        var (colStart, colEnd) = CtpColors(st);
        var (actL, actC, actH) = CtpActiveStop(st);
        var theme = CocoonPalette.Current;

        // 1. 顶栏：B ← 胶囊、标题、LB/RB 起止色切换分段栏、Y ✓ 保存胶囊
        RsPaintPills(ds, play: false, noY: false);
        const string pageTitle = "主题色取色板";
        using (var tf = new CanvasTextFormat { FontFamily = RmFont(pageTitle), FontSize = 44, FontWeight = RsWt(pageTitle), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(pageTitle, new Rect(288, 104, 460, 74), RmInk, tf);

        const float barX = 768f, barY = 104f, barW = 846f, barH = 74f;
        ds.FillRoundedRectangle(barX, barY, barW, barH, 37, 37, Color.FromArgb(255, 0xE5, 0xE7, 0xEE));
        CgpDrawShoulderBadge(ds, barX + 10, barY + 13, 60, 48, "LB");
        CgpDrawShoulderBadge(ds, barX + barW - 70, barY + 13, 60, 48, "RB");
        for (int i = 0; i < 2; i++)
        {
            var tr = CtpStopTabRect(i);
            bool on = st.StopIndex == i;
            if (on)
            {
                RsSoftShadow(ds, (float)tr.X, (float)tr.Y + 3, (float)tr.Width, (float)tr.Height, 31, 5, 0.14f);
                ds.FillRoundedRectangle(tr, 31, 31, Colors.White);
            }
            var dotCol = i == 0 ? colStart : colEnd;
            float dcx = (float)tr.X + 34f, dcy = (float)(tr.Y + tr.Height / 2);
            ds.FillCircle(dcx, dcy, 15f, dotCol);
            ds.DrawCircle(dcx, dcy, 15f, Color.FromArgb(180, 255, 255, 255), 2f);
            string tabLabel = i == 0 ? $"起点  {CocoonPalette.ToHex(colStart)}" : $"终点  {CocoonPalette.ToHex(colEnd)}";
            using var tabF = new CanvasTextFormat { FontFamily = RmFont(tabLabel), FontSize = 26, FontWeight = RsWt(tabLabel, 600), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(tabLabel, new Rect(tr.X + 60, tr.Y, tr.Width - 70, tr.Height), on ? theme.Ink : RsSub, tabF);
        }

        // 2. 左栏 3D 卡片：OKLCH 调色台
        CpCard(ds, CtpLeftX, CtpCardY, CtpLeftW, CtpCardH, null, 32);

        // 2.1 四枚配色和谐模式胶囊
        for (int i = 0; i < 4; i++)
        {
            var hr = CtpHarmRect(i);
            bool on = (int)st.Harmony == i;
            if (on)
            {
                ds.FillRoundedRectangle(hr, 31, 31, theme.SelectionTint);
                ds.DrawRoundedRectangle((float)hr.X + 1, (float)hr.Y + 1, (float)hr.Width - 2, (float)hr.Height - 2, 31, 31, theme.Ink, 2.5f);
            }
            else
            {
                ds.FillRoundedRectangle(hr, 31, 31, Color.FromArgb(255, 0xF0, 0xF1, 0xF6));
                ds.DrawRoundedRectangle((float)hr.X + 0.75f, (float)hr.Y + 0.75f, (float)hr.Width - 1.5f, (float)hr.Height - 1.5f, 31, 31, Color.FromArgb(255, 0xE1, 0xE2, 0xEA), 1.5f);
            }
            string hName = CocoonPalette.HarmonyNames[i];
            using var hf = new CanvasTextFormat { FontFamily = RmFont(hName), FontSize = 25, FontWeight = RsWt(hName, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(hName, hr, on ? theme.Ink : RsSub, hf);
        }

        // 2.2 2D OKLCH 明度×纯度感知取色画布
        var canvasBmp = CtpEnsureCanvasBmp(st, dev, actH);
        using (var canvasClip = CanvasGeometry.CreateRoundedRectangle(dev, CtpInnerX, CtpCanvasY, CtpInnerW, CtpCanvasH, 24, 24))
        {
            using (ds.CreateLayer(1f, canvasClip))
                ds.DrawImage(canvasBmp, new Rect(CtpInnerX, CtpCanvasY, CtpInnerW, CtpCanvasH), canvasBmp.Bounds, 1f, CanvasImageInterpolation.Linear);
            ds.DrawRoundedRectangle(CtpInnerX + 0.75f, CtpCanvasY + 0.75f, CtpInnerW - 1.5f, CtpCanvasH - 1.5f, 24, 24, Color.FromArgb(90, 0, 0, 0), 1.5f);
        }

        // 另一端颜色在同画布上的辅助锚点（虚线小环）
        var (otherL, otherC, _) = st.StopIndex == 0 ? (st.L1, st.C1, st.H1) : (st.L0, st.C0, st.H0);
        float ox = CtpInnerX + Math.Clamp(otherC / CtpMaxChroma, 0f, 1f) * CtpInnerW;
        float oy = CtpCanvasY + Math.Clamp((CtpMaxL - otherL) / (CtpMaxL - CtpMinL), 0f, 1f) * CtpCanvasH;
        ds.FillCircle(ox, oy, 9f, st.StopIndex == 0 ? colEnd : colStart);
        ds.DrawCircle(ox, oy, 10f, Color.FromArgb(210, 255, 255, 255), 2.5f);

        // 当前端取色放大镜准星
        float px = CtpInnerX + Math.Clamp(actC / CtpMaxChroma, 0f, 1f) * CtpInnerW;
        float py = CtpCanvasY + Math.Clamp((CtpMaxL - actL) / (CtpMaxL - CtpMinL), 0f, 1f) * CtpCanvasH;
        RsSoftShadow(ds, px - 20, py - 17, 40, 40, 20, 5, 0.35f);
        ds.FillCircle(px, py, 19f, Colors.White);
        ds.FillCircle(px, py, 13.5f, st.StopIndex == 0 ? colStart : colEnd);
        if (st.CanvasFine)
            ds.DrawCircle(px, py, 24f, theme.Ink, 3f);

        // 画布右下角微调提示胶囊
        string canvasHint = st.CanvasFine ? "方向键移动取色点 · A / B 完成" : st.Focus == 4 && !st.TouchMode ? "按 A 用十字键微调画布" : "支持触控拖拽";
        float chw = CpTextW(ds, canvasHint, 21) + 28f;
        float chx = CtpInnerX + CtpInnerW - chw - 14f, chy = CtpCanvasY + CtpCanvasH - 46f;
        ds.FillRoundedRectangle(chx, chy, chw, 34f, 17f, 17f, Color.FromArgb(168, 24, 24, 32));
        using (var chf = new CanvasTextFormat { FontFamily = RmFont(canvasHint), FontSize = 21, FontWeight = RsWt(canvasHint, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(canvasHint, new Rect(chx, chy, chw, 34f), Color.FromArgb(255, 0xFA, 0xFA, 0xFA), chf);

        // 2.3 三条 OKLCH 感知参数滑杆（色相 H / 明度 L / 纯度 C）
        CtpDrawHueSlider(ds, st, CtpInnerX, CtpHueY, CtpInnerW, CtpSliderH, actH);
        CtpDrawChannelSlider(ds, CtpInnerX, CtpLightY, CtpInnerW, CtpSliderH, "明度 L", $"{MathF.Round(actL * 100f):0}%",
            (actL - CtpMinL) / (CtpMaxL - CtpMinL),
            CocoonPalette.OklchToSrgb(CtpMinL, actC, actH),
            CocoonPalette.OklchToSrgb(0.58f, actC, actH),
            CocoonPalette.OklchToSrgb(CtpMaxL, actC, actH),
            CocoonPalette.OklchToSrgb(actL, actC, actH));
        CtpDrawChannelSlider(ds, CtpInnerX, CtpChromaY, CtpInnerW, CtpSliderH, "纯度 C", $"{MathF.Round(actC / CtpMaxChroma * 100f):0}%",
            actC / CtpMaxChroma,
            CocoonPalette.OklchToSrgb(actL, 0f, actH),
            CocoonPalette.OklchToSrgb(actL, CtpMaxChroma * 0.5f, actH),
            CocoonPalette.OklchToSrgb(actL, CtpMaxChroma, actH),
            CocoonPalette.OklchToSrgb(actL, actC, actH));

        // 2.4 底部快捷操作三按钮
        string[] actLabels = { "互换起止 (X)", "默认蓝紫", "应用配色" };
        for (int i = 0; i < 3; i++)
        {
            var br = CtpActionBtnRect(i);
            bool primary = i == 2;
            if (primary)
            {
                RsSoftShadow(ds, (float)br.X, (float)br.Y + 4, (float)br.Width, (float)br.Height, 36, 8, 0.28f);
                using var pGrad = new CanvasLinearGradientBrush(dev, theme.PillStart, theme.PillEnd)
                {
                    StartPoint = new Vector2((float)br.X, (float)br.Y),
                    EndPoint = new Vector2((float)(br.X + br.Width), (float)(br.Y + br.Height)),
                };
                ds.FillRoundedRectangle(br, 36, 36, pGrad);
            }
            else
            {
                ds.FillRoundedRectangle(br, 36, 36, Color.FromArgb(255, 0xEE, 0xEF, 0xF5));
                ds.DrawRoundedRectangle((float)br.X + 0.75f, (float)br.Y + 0.75f, (float)br.Width - 1.5f, (float)br.Height - 1.5f, 36, 36, Color.FromArgb(255, 0xDD, 0xDF, 0xE8), 1.5f);
            }
            using var bf = new CanvasTextFormat { FontFamily = RmFont(actLabels[i]), FontSize = 27, FontWeight = RsWt(actLabels[i], 600), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(actLabels[i], br, primary ? Color.FromArgb(255, 0xFA, 0xFA, 0xFA) : RmInk, bf);
        }

        // 3. 右栏 3D 卡片：色彩科学读数 + Cocoon 真实控件实时预览台
        CtpPaintPreviewColumn(ds, colStart, colEnd, theme);
    }

    private void CtpDrawHueSlider(CanvasDrawingSession ds, CtpState st, float x, float y, float w, float h, float hue)
    {
        float labelW = 128f, valW = 94f, trackX = x + labelW, trackW = w - labelW - valW, cy = y + h / 2f;
        const string title = "色相 H";
        using (var lf = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 28, FontWeight = RsWt(title, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(title, new Rect(x, y, labelW, h), RmInk, lf);

        var hueBmp = CtpEnsureHueBmp(st, ds.Device);
        using (var clip = CanvasGeometry.CreateRoundedRectangle(ds.Device, trackX, cy - 11f, trackW, 22f, 11f, 11f))
        {
            using (ds.CreateLayer(1f, clip))
                ds.DrawImage(hueBmp, new Rect(trackX, cy - 11f, trackW, 22f), hueBmp.Bounds, 1f, CanvasImageInterpolation.Linear);
            ds.DrawRoundedRectangle(trackX + 0.5f, cy - 10.5f, trackW - 1f, 21f, 11f, 11f, Color.FromArgb(55, 0, 0, 0), 1f);
        }

        float kx = trackX + Math.Clamp(hue / 360f, 0f, 1f) * trackW;
        var knobCol = CocoonPalette.OklchToSrgb(0.66f, 0.19f, hue);
        RsSoftShadow(ds, kx - 22, cy - 19, 44, 44, 22, 4, 0.28f);
        ds.FillCircle(kx, cy, 21f, Colors.White);
        ds.FillCircle(kx, cy, 15f, knobCol);

        string val = $"{MathF.Round(hue):0}°";
        using var vf = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 26, FontWeight = new Windows.UI.Text.FontWeight(600), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        ds.DrawText(val, new Rect(trackX + trackW + 8f, y, valW - 8f, h), RsSub, vf);
    }

    private void CtpDrawChannelSlider(CanvasDrawingSession ds, float x, float y, float w, float h, string title, string val, float frac, Color c0, Color cMid, Color c1, Color knobFill)
    {
        float labelW = 128f, valW = 94f, trackX = x + labelW, trackW = w - labelW - valW, cy = y + h / 2f;
        using (var lf = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 28, FontWeight = RsWt(title, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(title, new Rect(x, y, labelW, h), RmInk, lf);

        var stops = new[]
        {
            new CanvasGradientStop { Position = 0f, Color = c0 },
            new CanvasGradientStop { Position = 0.5f, Color = cMid },
            new CanvasGradientStop { Position = 1f, Color = c1 },
        };
        using (var br = new CanvasLinearGradientBrush(ds.Device, stops) { StartPoint = new Vector2(trackX, cy), EndPoint = new Vector2(trackX + trackW, cy) })
            ds.FillRoundedRectangle(trackX, cy - 10f, trackW, 20f, 10f, 10f, br);
        ds.DrawRoundedRectangle(trackX + 0.5f, cy - 9.5f, trackW - 1f, 19f, 10f, 10f, Color.FromArgb(55, 0, 0, 0), 1f);

        float kx = trackX + Math.Clamp(frac, 0f, 1f) * trackW;
        RsSoftShadow(ds, kx - 22, cy - 19, 44, 44, 22, 4, 0.28f);
        ds.FillCircle(kx, cy, 21f, Colors.White);
        ds.FillCircle(kx, cy, 15f, knobFill);

        using var vf = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 26, FontWeight = new Windows.UI.Text.FontWeight(600), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        ds.DrawText(val, new Rect(trackX + trackW + 8f, y, valW - 8f, h), RsSub, vf);
    }

    private void CtpPaintPreviewColumn(CanvasDrawingSession ds, Color colStart, Color colEnd, CocoonThemeColors theme)
    {
        var dev = ds.Device;
        CpCard(ds, CtpRightX, CtpCardY, CtpRightW, CtpCardH, null, 32);
        float ix = CtpRightX + 32f, iw = CtpRightW - 64f;

        // 1. 顶部大幅渐变横幅 + 色彩科学遥测
        float heroY = 236f, heroH = 154f;
        RsSoftShadow(ds, ix + 4, heroY + 8, iw - 8, heroH, 28, 12, 0.26f);
        using (var heroBr = new CanvasLinearGradientBrush(dev, colStart, colEnd) { StartPoint = new Vector2(ix, heroY), EndPoint = new Vector2(ix + iw, heroY + heroH) })
            ds.FillRoundedRectangle(ix, heroY, iw, heroH, 28, 28, heroBr);
        ds.DrawRoundedRectangle(ix + 1f, heroY + 1f, iw - 2f, heroH - 2f, 28, 28, Color.FromArgb(95, 255, 255, 255), 2f);

        string pairHex = $"{CocoonPalette.ToHex(colStart)}  →  {CocoonPalette.ToHex(colEnd)}";
        using (var hf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 31, FontWeight = new Windows.UI.Text.FontWeight(700), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(pairHex, new Rect(ix + 28, heroY + 20, iw - 56, 50), Colors.White, hf);

        var (l1, _, h1) = CocoonPalette.SrgbToOklch(colStart);
        var (l2, _, h2) = CocoonPalette.SrgbToOklch(colEnd);
        float dH = MathF.Abs(((h2 - h1 + 540f) % 360f) - 180f);
        float contrast = CocoonPalette.ContrastRatio(theme.Ink, Color.FromArgb(255, 0xFD, 0xFD, 0xFE));
        string sciText = $"OKLCH ΔL {l2 - l1:+0.00;-0.00;0.00} · ΔH {dH:0}° · 白卡对比 {contrast:0.0}:1";
        float pillW = Math.Min(iw - 48f, CpTextW(ds, sciText, 22) + 32f);
        ds.FillRoundedRectangle(ix + 24, heroY + 88, pillW, 42, 21, 21, Color.FromArgb(82, 16, 16, 24));
        using (var sf = new CanvasTextFormat { FontFamily = RmFont(sciText), FontSize = 22, FontWeight = RsWt(sciText, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(sciText, new Rect(ix + 24, heroY + 88, pillW, 42), Colors.White, sf);

        // 2. 凹槽预览台底盘（模拟 Cocoon 页面冷灰陶瓷画布）
        float stageY = 414f, stageH = 642f;
        using (var stageBg = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xEF, 0xEF, 0xF4), Color.FromArgb(255, 0xE2, 0xE3, 0xEA)) { StartPoint = new Vector2(0, stageY), EndPoint = new Vector2(0, stageY + stageH) })
            ds.FillRoundedRectangle(ix, stageY, iw, stageH, 28, 28, stageBg);
        ds.DrawRoundedRectangle(ix + 0.75f, stageY + 0.75f, iw - 1.5f, stageH - 1.5f, 28, 28, Color.FromArgb(255, 0xD8, 0xD9, 0xE2), 1.5f);

        float mx = ix + 28f, mw = iw - 56f;

        // 2.1 预览：选中分类白卡 + 同心四角流光括号
        float navY = stageY + 28f, navH = 96f;
        CpCard(ds, mx, navY, mw, navH, null, 26);
        RsGlyph(ds, "artwork", mx + 52, navY + navH / 2f, theme.Ink, 0.72f);
        const string navSample = "外观与主题";
        using (var nf = new CanvasTextFormat { FontFamily = RmFont(navSample), FontSize = 31, FontWeight = RsWt(navSample, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(navSample, new Rect(mx + 104, navY, 260, navH), theme.Ink, nf);
        const string focusTag = "焦点括号";
        float ftw = CpTextW(ds, focusTag, 22) + 28f;
        ds.FillRoundedRectangle(mx + mw - ftw - 24f, navY + 26f, ftw, 44f, 22f, 22f, theme.SelectionTint);
        using (var ftf = new CanvasTextFormat { FontFamily = RmFont(focusTag), FontSize = 22, FontWeight = RsWt(focusTag, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText(focusTag, new Rect(mx + mw - ftw - 24f, navY + 26f, ftw, 44f), theme.Ink, ftf);
        RaFocusRect(ds, mx, navY, mw, navH, 26f);

        // 2.2 预览：设置卡片（开启态渐变开关 + 3D 渐变滑块）
        float ctrlY = navY + navH + 28f, ctrlH = 212f;
        CpCard(ds, mx, ctrlY, mw, ctrlH, new System.Collections.Generic.List<float> { ctrlY + 96f }, 26);
        float r1Cy = ctrlY + 48f;
        ds.FillCircle(mx + 22, r1Cy, 5.5f, theme.Ink);
        const string swLabel = "流光渐变开关";
        using (var swf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 29, FontWeight = CpTitleWt, VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText(swLabel, new Rect(mx + 42, ctrlY, 280, 96), RmInk, swf);
        float togX = mx + mw - 126f, togY = r1Cy - 32f;
        using (var togBr = new CanvasLinearGradientBrush(dev, colStart, colEnd) { StartPoint = new Vector2(togX, togY), EndPoint = new Vector2(togX + 104, togY + 64) })
            ds.FillRoundedRectangle(togX, togY, 104, 64, 32, 32, togBr);
        RsSoftShadow(ds, togX + 72 - 23, r1Cy - 20, 46, 46, 23, 3, 0.22f);
        ds.FillCircle(togX + 72, r1Cy, 23, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));

        // 滑块预览
        float slCy = ctrlY + 156f, slX0 = mx + 126f, slX1 = mx + mw - 32f, slFrac = 0.68f;
        using (var slVf = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 26, FontWeight = new Windows.UI.Text.FontWeight(600), VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText("68 %", new Rect(mx + 34, slCy - 22, 86, 44), RsSub, slVf);
        ds.FillRoundedRectangle(slX0, slCy - 5f, slX1 - slX0, 10f, 5f, 5f, Color.FromArgb(255, 0xDC, 0xDC, 0xE3));
        using (var slBr = new CanvasLinearGradientBrush(dev, colStart, theme.Deep) { StartPoint = new Vector2(slX0, 0), EndPoint = new Vector2(slX1, 0) })
            ds.FillRoundedRectangle(slX0, slCy - 5f, (slX1 - slX0) * slFrac, 10f, 5f, 5f, slBr);
        float skx = slX0 + (slX1 - slX0) * slFrac;
        RsSoftShadow(ds, skx - 20, slCy - 17, 40, 40, 20, 4, 0.28f);
        using (var skBr = new CanvasLinearGradientBrush(dev, theme.KnobStart, theme.KnobEnd) { StartPoint = new Vector2(skx - 14, slCy - 20), EndPoint = new Vector2(skx + 14, slCy + 20) })
            ds.FillCircle(skx, slCy, 20f, skBr);

        // 2.3 预览：底部圆环仪表 + 快捷圆形磁贴 + 贴纸图标
        float botY = ctrlY + ctrlH + 24f, botH = stageY + stageH - 22f - botY;
        CpCard(ds, mx, botY, mw, botH, null, 26);
        CpRing(ds, mx + 116f, botY + botH * 0.46f, 64f, 16f, 0.72f, "18", "W", "功耗");

        // 快捷磁贴预览
        float tileCx = mx + 292f, tileCy = botY + botH * 0.50f, tileR = 48f;
        SpShadow(ds, tileCx - tileR, tileCy - tileR + 5, tileR * 2, tileR * 2, tileR, 7, theme.TileGlow);
        using (var tileBr = new CanvasLinearGradientBrush(dev, theme.SoftStart, theme.Deep) { StartPoint = new Vector2(tileCx - tileR + 12, tileCy - tileR), EndPoint = new Vector2(tileCx + tileR - 12, tileCy + tileR) })
            ds.FillCircle(tileCx, tileCy, tileR, tileBr);
        SpIcon(ds, "qam.wifi", tileCx, tileCy, 38, Colors.White, 2.2f);

        // 侧栏贴纸图标预览
        float stkCx = mx + 436f, stkCy = tileCy;
        using (var stkGeo = LsIconGeo(dev, "pad").Transform(Matrix3x2.CreateTranslation(stkCx, stkCy)))
        using (var stkBr = new CanvasLinearGradientBrush(dev, colStart, colEnd) { StartPoint = new Vector2(0, stkCy - 24), EndPoint = new Vector2(0, stkCy + 24) })
        {
            RwSticker(ds, stkGeo, 9);
            ds.FillGeometry(stkGeo, stkBr);
        }
    }

    // ---------------- 焦点框定位 ----------------

    private void CtpUpdateFocus(CtpState st, bool move)
    {
        if (st.TouchMode)
        {
            if (st.Cur.Shown) CurShow(st.Cur, false, 60);
            return;
        }
        bool first = !st.Cur.Shown;
        Rect r;
        float rad;
        if (st.Focus is >= 0 and <= 3)
        {
            r = CtpHarmRect(st.Focus);
            rad = 31f;
        }
        else if (st.Focus == 4)
        {
            r = new Rect(CtpInnerX, CtpCanvasY, CtpInnerW, CtpCanvasH);
            rad = 24f;
        }
        else if (st.Focus is >= 5 and <= 7)
        {
            float sy = st.Focus == 5 ? CtpHueY : st.Focus == 6 ? CtpLightY : CtpChromaY;
            r = new Rect(CtpInnerX - 12, sy, CtpInnerW + 24, CtpSliderH);
            rad = 24f;
        }
        else
        {
            r = CtpActionBtnRect(Math.Clamp(st.Focus - 8, 0, 2));
            rad = 36f;
        }
        CurToCocoon(st.Cur, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height, rad, move: move && !first);
        if (first) CurShow(st.Cur, true, 120, 30);
    }

    // ---------------- 手柄 / 键盘输入 ----------------

    private bool CtpMove(Direction dir)
    {
        if (_ctp is not { Closing: false } st) return false;
        st.TouchMode = false;
        var (l, c, h) = CtpActiveStop(st);

        if (st.CanvasFine && st.Focus == 4)
        {
            float dl = dir == Direction.Up ? 0.025f : dir == Direction.Down ? -0.025f : 0f;
            float dc = dir == Direction.Right ? 0.012f : dir == Direction.Left ? -0.012f : 0f;
            float nl = Math.Clamp(l + dl, CtpMinL, CtpMaxL);
            float nc = Math.Clamp(c + dc, 0f, CtpMaxChroma);
            if (MathF.Abs(nl - l) < 1e-4f && MathF.Abs(nc - c) < 1e-4f) { Cue(Sfx.Bump); return true; }
            Cue(dl > 0 || dc > 0 ? Sfx.SliderUp : Sfx.SliderDown);
            CtpSetActiveStop(st, nl, nc, h);
            CtpPaint(st);
            return true;
        }

        int d = dir is Direction.Left or Direction.Up ? -1 : 1;
        bool horiz = dir is Direction.Left or Direction.Right;

        if (horiz)
        {
            if (st.Focus is >= 0 and <= 3)
            {
                int next = st.Focus + d;
                if (next is < 0 or > 3) { Cue(Sfx.Bump); return true; }
                st.Focus = next;
                CuePan(0.4f);
                CtpUpdateFocus(st, move: true);
                return true;
            }
            if (st.Focus == 5)
            {
                float nh = CocoonPalette.WrapHue(h + d * 5f);
                Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown);
                CtpSetActiveStop(st, l, c, nh);
                CtpPaint(st);
                return true;
            }
            if (st.Focus == 6)
            {
                float nl = Math.Clamp(l + d * 0.025f, CtpMinL, CtpMaxL);
                if (MathF.Abs(nl - l) < 1e-4f) { Cue(Sfx.Bump); return true; }
                Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown);
                CtpSetActiveStop(st, nl, c, h);
                CtpPaint(st);
                return true;
            }
            if (st.Focus == 7)
            {
                float nc = Math.Clamp(c + d * 0.012f, 0f, CtpMaxChroma);
                if (MathF.Abs(nc - c) < 1e-4f) { Cue(Sfx.Bump); return true; }
                Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown);
                CtpSetActiveStop(st, l, nc, h);
                CtpPaint(st);
                return true;
            }
            if (st.Focus is >= 8 and <= 10)
            {
                int next = st.Focus + d;
                if (next is < 8 or > 10) { Cue(Sfx.Bump); return true; }
                st.Focus = next;
                CuePan(0.4f);
                CtpUpdateFocus(st, move: true);
                return true;
            }
            Cue(Sfx.Bump);
            return true;
        }

        // 纵向在行层级间跳转：0(和谐栏) ↔ 4(画布) ↔ 5(色相) ↔ 6(明度) ↔ 7(纯度) ↔ 10(底部按钮)
        int nextFocus = dir == Direction.Down
            ? st.Focus switch
            {
                <= 3 => 4,
                4 => 5,
                5 => 6,
                6 => 7,
                7 => 10,
                _ => -1,
            }
            : st.Focus switch
            {
                >= 8 => 7,
                7 => 6,
                6 => 5,
                5 => 4,
                4 => (int)st.Harmony,
                _ => -1,
            };

        if (nextFocus < 0) { Cue(Sfx.Bump); return true; }
        st.Focus = nextFocus;
        CuePan(0.5f);
        CtpPaint(st);
        CtpUpdateFocus(st, move: true);
        return true;
    }

    private bool CtpActivate()
    {
        if (_ctp is not { Closing: false } st) return false;
        st.TouchMode = false;
        if (st.Focus is >= 0 and <= 3)
        {
            st.Harmony = (CocoonHarmonyMode)st.Focus;
            Cue(Sfx.Positive);
            if (st.Harmony != CocoonHarmonyMode.Free)
            {
                var (l, c, h) = CtpActiveStop(st);
                CtpSetActiveStop(st, l, c, h);
            }
            CtpPaint(st);
            CtpUpdateFocus(st, move: false);
            return true;
        }
        if (st.Focus == 4)
        {
            st.CanvasFine = !st.CanvasFine;
            Cue(st.CanvasFine ? Sfx.Activate : Sfx.Back);
            CtpPaint(st);
            return true;
        }
        if (st.Focus == 8) { CtpSwapStops(st); return true; }
        if (st.Focus == 9) { CtpResetDefault(st); return true; }
        if (st.Focus == 10) { CtpClose(commit: true); return true; }
        Cue(Sfx.Bump);
        return true;
    }

    private bool CtpBack()
    {
        if (_ctp is not { Closing: false } st) return false;
        if (st.CanvasFine)
        {
            st.CanvasFine = false;
            Cue(Sfx.Back);
            CtpPaint(st);
            return true;
        }
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
        st.CanvasFine = false;
        Cue(Sfx.Tab);
        CtpPaint(st);
        return true;
    }

    // ---------------- 触控 / 鼠标交互 ----------------

    private bool CtpPointerDown(PointerRoutedEventArgs e, Microsoft.UI.Input.PointerPoint pt)
    {
        if (_ctp is not { Closing: false } st) return false;
        var (x, y) = CpLocal(pt.Position.X, pt.Position.Y);
        e.Handled = true;
        st.TouchMode = true;
        st.CanvasFine = false;
        if (st.Cur.Shown) CurShow(st.Cur, false, 60);

        // 点在 ModalSheet 外部遮罩：取消关闭
        if (x < RsX || x > RsX + RsW || y < RsY || y > RsY + RsH)
        {
            CtpClose(commit: false);
            return true;
        }

        // 顶栏 B ← 与 Y ✓
        if (x is >= 120 and <= 280 && y is >= 92 and <= 190) { CtpClose(commit: false); return true; }
        if (x is >= 1640 and <= 1800 && y is >= 92 and <= 190) { CtpClose(commit: true); return true; }

        // 起点 / 终点分段页签
        for (int i = 0; i < 2; i++)
        {
            var tr = CtpStopTabRect(i);
            if (tr.Contains(new Windows.Foundation.Point(x, y)))
            {
                if (st.StopIndex != i) { st.StopIndex = i; Cue(Sfx.Tab); CtpPaint(st); }
                return true;
            }
        }
        if (x is >= 768 and <= 840 && y is >= 100 and <= 180) { CtpSwitchTab(-1); return true; }
        if (x is >= 1544 and <= 1618 && y is >= 100 and <= 180) { CtpSwitchTab(1); return true; }

        // 和谐模式四胶囊
        for (int i = 0; i < 4; i++)
        {
            if (CtpHarmRect(i).Contains(new Windows.Foundation.Point(x, y)))
            {
                st.Harmony = (CocoonHarmonyMode)i;
                Cue(Sfx.Positive);
                if (st.Harmony != CocoonHarmonyMode.Free)
                {
                    var (l, c, h) = CtpActiveStop(st);
                    CtpSetActiveStop(st, l, c, h);
                }
                CtpPaint(st);
                return true;
            }
        }

        // 底部三按钮
        for (int i = 0; i < 3; i++)
        {
            if (CtpActionBtnRect(i).Contains(new Windows.Foundation.Point(x, y)))
            {
                if (i == 0) CtpSwapStops(st);
                else if (i == 1) CtpResetDefault(st);
                else CtpClose(commit: true);
                return true;
            }
        }

        // 2D 画布拖拽
        if (x >= CtpInnerX && x <= CtpInnerX + CtpInnerW && y >= CtpCanvasY && y <= CtpCanvasY + CtpCanvasH)
        {
            st.DragId = pt.PointerId;
            st.DragTarget = 1;
            _rootEl.CapturePointer(e.Pointer);
            CtpApplyDrag(st, x, y);
            return true;
        }

        // 三条滑杆拖拽
        float trackX0 = CtpInnerX + 116f, trackX1 = CtpInnerX + CtpInnerW - 80f;
        if (x >= trackX0 && x <= trackX1)
        {
            int target = y >= CtpHueY && y <= CtpHueY + CtpSliderH ? 2
                       : y >= CtpLightY && y <= CtpLightY + CtpSliderH ? 3
                       : y >= CtpChromaY && y <= CtpChromaY + CtpSliderH ? 4 : 0;
            if (target > 0)
            {
                st.DragId = pt.PointerId;
                st.DragTarget = target;
                _rootEl.CapturePointer(e.Pointer);
                CtpApplyDrag(st, x, y);
                return true;
            }
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
        float trackX = CtpInnerX + 128f, trackW = CtpInnerW - 128f - 94f;
        switch (st.DragTarget)
        {
            case 1:
            {
                float nc = Math.Clamp((x - CtpInnerX) / CtpInnerW, 0f, 1f) * CtpMaxChroma;
                float nl = CtpMaxL - Math.Clamp((y - CtpCanvasY) / CtpCanvasH, 0f, 1f) * (CtpMaxL - CtpMinL);
                CtpSetActiveStop(st, nl, nc, h);
                break;
            }
            case 2:
            {
                float nh = Math.Clamp((x - trackX) / trackW, 0f, 1f) * 359.9f;
                CtpSetActiveStop(st, l, c, nh);
                break;
            }
            case 3:
            {
                float nl = CtpMinL + Math.Clamp((x - trackX) / trackW, 0f, 1f) * (CtpMaxL - CtpMinL);
                CtpSetActiveStop(st, nl, c, h);
                break;
            }
            case 4:
            {
                float nc = Math.Clamp((x - trackX) / trackW, 0f, 1f) * CtpMaxChroma;
                CtpSetActiveStop(st, l, nc, h);
                break;
            }
        }
        CtpPaint(st);
    }

    // ---------------- 外观与主题：3×3 渐变色板卡片与取色板入口绘制 ----------------

    private const float CpSwatchRowH = 442f;
    private const int CpSwatchCols = 3, CpSwatchTotal = 9;

    private static (float X, float Y, float W, float H) CpSwatchTileRect(float rowY, int index)
    {
        const float gx0 = 36f, gapX = 20f, gapY = 16f, th = 100f;
        float tw = (CpListW - gx0 * 2f - gapX * 2f) / 3f; // 376
        float gy0 = rowY + 80f;
        return (gx0 + (index % CpSwatchCols) * (tw + gapX), gy0 + (index / CpSwatchCols) * (th + gapY), tw, th);
    }

    private void CpPaintSwatchGalleryRow(CanvasDrawingSession ds, PerfRow r, float y)
    {
        var dev = ds.Device;
        if (r.Changed) ds.FillCircle(24, y + 42, 6, CpAccentInk);

        using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 35, FontWeight = CpTitleWt, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(r.Title, new Rect(44, y + 16, 480, 52), RmInk, tf);

        const string hint = "按 Y 恢复默认蓝紫";
        using (var hf = new CanvasTextFormat { FontFamily = RmFont(hint), FontSize = 24, FontWeight = RsWt(hint, 500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(hint, new Rect(CpListW - 420, y + 16, 384, 52), RsSub, hf);

        int activeIdx = _prefs.CocoonTheme >= 0 && _prefs.CocoonTheme < CocoonPalette.Presets.Length ? _prefs.CocoonTheme : CocoonPalette.Presets.Length;
        var custStart = CocoonPalette.ParseHex(_prefs.CocoonCustomStart, CocoonPalette.Presets[0].Start);
        var custEnd = CocoonPalette.ParseHex(_prefs.CocoonCustomEnd, CocoonPalette.Presets[0].End);

        for (int i = 0; i < CpSwatchTotal; i++)
        {
            var (tx, ty, tw, th) = CpSwatchTileRect(y, i);
            bool isCustom = i == CocoonPalette.Presets.Length;
            Color c1 = isCustom ? custStart : CocoonPalette.Presets[i].Start;
            Color c2 = isCustom ? custEnd : CocoonPalette.Presets[i].End;
            string name = isCustom ? "自定义配色" : CocoonPalette.Presets[i].Name;
            string mood = isCustom ? $"{CocoonPalette.ToHex(c1)} → {CocoonPalette.ToHex(c2)}" : CocoonPalette.Presets[i].Mood;
            bool sel = i == activeIdx;

            var tileTheme = i == 0 ? CocoonPalette.DefaultTheme : CocoonPalette.DeriveTheme(c1, c2);
            if (sel)
            {
                ds.FillRoundedRectangle(tx, ty, tw, th, 24, 24, tileTheme.SelectionTint);
                ds.DrawRoundedRectangle(tx + 1f, ty + 1f, tw - 2f, th - 2f, 24, 24, tileTheme.SelectionBorder, 2f);
            }
            else
            {
                ds.FillRoundedRectangle(tx, ty, tw, th, 24, 24, Color.FromArgb(255, 0xF3, 0xF4, 0xF8));
                ds.DrawRoundedRectangle(tx + 0.75f, ty + 0.75f, tw - 1.5f, th - 1.5f, 24, 24, Color.FromArgb(255, 0xE3, 0xE4, 0xEC), 1.5f);
            }

            // 左侧立体琉璃渐变圆珠
            float ocx = tx + 48f, ocy = ty + th / 2f, orad = 27f;
            RsSoftShadow(ds, ocx - orad, ocy - orad + 4, orad * 2, orad * 2, orad, 5, 0.24f);
            using (var orbBr = new CanvasLinearGradientBrush(dev, c1, c2) { StartPoint = new Vector2(ocx - orad, ocy - orad), EndPoint = new Vector2(ocx + orad, ocy + orad) })
                ds.FillCircle(ocx, ocy, orad, orbBr);
            // 左上高光弧与白描边
            ds.FillEllipse(ocx - 7f, ocy - 10f, 12f, 7f, Color.FromArgb(88, 255, 255, 255));
            ds.DrawCircle(ocx, ocy, orad - 0.75f, Color.FromArgb(190, 255, 255, 255), 1.8f);

            // 名称与心理学气质副标题
            float textX = tx + 90f, textW = tw - 90f - (sel ? 48f : 18f);
            using (var nf = new CanvasTextFormat { FontFamily = RmFont(name), FontSize = 29, FontWeight = RsWt(name, 500), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                ds.DrawText(name, new Rect(textX, ty + 14, textW, 40), sel ? tileTheme.Ink : RmInk, nf);
            using (var mf = new CanvasTextFormat { FontFamily = RmFont(mood), FontSize = 21, FontWeight = RsWt(mood, 500), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                ds.DrawText(mood, new Rect(textX, ty + 54, textW, 32), RsSub, mf);

            // 右侧已生效对勾徽章
            if (sel)
            {
                float bcx = tx + tw - 28f, bcy = ocy;
                using (var bBr = new CanvasLinearGradientBrush(dev, c1, c2) { StartPoint = new Vector2(bcx - 15, bcy - 15), EndPoint = new Vector2(bcx + 15, bcy + 15) })
                    ds.FillCircle(bcx, bcy, 15f, bBr);
                using var pb = new CanvasPathBuilder(dev);
                pb.BeginFigure(bcx - 6.5f, bcy + 0.5f);
                pb.AddLine(bcx - 1.5f, bcy + 5.5f);
                pb.AddLine(bcx + 7f, bcy - 5f);
                pb.EndFigure(CanvasFigureLoop.Open);
                using var tick = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(tick, Colors.White, 3.4f, RwRoundStroke);
            }
        }
    }

    private void CpPaintColorPickerEntryRow(CanvasDrawingSession ds, PerfRow r, float y, float h)
    {
        float cy = y + h / 2f, R = CpListW - 34f;
        if (r.Changed) ds.FillCircle(24, cy, 6, CpAccentInk);

        const string sub = "在 OKLCH 感知色域中自由调配双色渐变、色相环与明纯度";
        using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 36, FontWeight = CpTitleWt, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(r.Title, new Rect(44, y + 20, R - 460, 52), RmInk, tf);
        using (var sf = new CanvasTextFormat { FontFamily = RmFont(sub), FontSize = 25, FontWeight = RsWt(sub, 500), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
            ds.DrawText(sub, new Rect(44, y + 78, R - 460, 40), RsSub, sf);

        // 右侧实时双色渐变预览胶囊 + 色值 + ▸
        var (c1, c2) = CocoonPalette.ActivePair(_prefs);
        float pillW = 148f, pillH = 52f, pillX = R - 38f - pillW, pillY = cy - pillH / 2f;
        RsSoftShadow(ds, pillX, pillY + 4, pillW, pillH, 26, 6, 0.24f);
        using (var br = new CanvasLinearGradientBrush(ds.Device, c1, c2) { StartPoint = new Vector2(pillX, pillY), EndPoint = new Vector2(pillX + pillW, pillY + pillH) })
            ds.FillRoundedRectangle(pillX, pillY, pillW, pillH, 26, 26, br);
        ds.DrawRoundedRectangle(pillX + 1f, pillY + 1f, pillW - 2f, pillH - 2f, 26, 26, Color.FromArgb(190, 255, 255, 255), 2f);

        string hexPair = $"{CocoonPalette.ToHex(c1)} → {CocoonPalette.ToHex(c2)}";
        using (var vf = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 25, FontWeight = new Windows.UI.Text.FontWeight(600), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(hexPair, new Rect(pillX - 270, y, 252, h), RsSub, vf);

        using var pb = new CanvasPathBuilder(ds.Device);
        pb.BeginFigure(R - 10, cy - 9); pb.AddLine(R, cy); pb.AddLine(R - 10, cy + 9); pb.EndFigure(CanvasFigureLoop.Closed);
        using var tri = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(tri, RmInk);
        ds.DrawGeometry(tri, RmInk, 3, RwRoundStroke);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CtpWinRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CtpBitmapInfoHeader
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out CtpWinRect lpRect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(nint ho);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref CtpBitmapInfoHeader pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    private static async System.Threading.Tasks.Task<bool> CaptureWindowPngAsync(nint hwnd, string pngPath)
    {
        if (!GetWindowRect(hwnd, out var rc)) return false;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 64 || h <= 64) return false;
        nint screenDc = GetDC(0);
        if (screenDc == 0) return false;
        nint memDc = CreateCompatibleDC(screenDc);
        var bih = new CtpBitmapInfoHeader
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CtpBitmapInfoHeader>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        nint dib = CreateDIBSection(screenDc, ref bih, 0, out nint bits, 0, 0);
        byte[]? bgra = null;
        try
        {
            if (dib != 0 && bits != 0)
            {
                nint old = SelectObject(memDc, dib);
                bool ok = PrintWindow(hwnd, memDc, 2 /* PW_RENDERFULLCONTENT */);
                SelectObject(memDc, old);
                if (ok)
                {
                    bgra = new byte[w * h * 4];
                    System.Runtime.InteropServices.Marshal.Copy(bits, bgra, 0, bgra.Length);
                    for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
                }
            }
        }
        finally
        {
            if (dib != 0) DeleteObject(dib);
            if (memDc != 0) DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
        if (bgra == null) return false;
        using var bmp = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), bgra, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        await bmp.SaveAsync(pngPath, CanvasBitmapFileFormat.Png);
        return true;
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

    /// <summary>录屏自测（--state=reccocoontheme）：截取 Cocoon 外观与主题色板网格、不同预设效果与 OKLCH 取色板静帧。</summary>
    internal async System.Threading.Tasks.Task RecCocoonThemeAsync()
    {
        VerifyCocoonColorScience();
        await System.Threading.Tasks.Task.Delay(2200);
        var savedPrefs = _prefs;
        string dir = System.IO.Path.Combine(App.CacheDir, "rec");
        System.IO.Directory.CreateDirectory(dir);
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(HostWindow);
        async System.Threading.Tasks.Task Still(string name)
        {
            await System.Threading.Tasks.Task.Delay(650);
            var r = new FrameRecorder { Full = true };
            if (r.Start(HostWindow))
            {
                await System.Threading.Tasks.Task.Delay(220);
                Log.Info($"reccocoontheme {name}: {await r.StopAndSaveAsync(System.IO.Path.Combine(dir, "cocoon-theme-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
                return;
            }
            string png = System.IO.Path.Combine(dir, $"cocoon-theme-{name}.png");
            bool ok = await CaptureWindowPngAsync(hwnd, png);
            Log.Info($"reccocoontheme {name} png={ok}: {png}");
        }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row)
            {
                SaveUi(_prefs with { Home = HomeStyle.Row });
                CsMorphStyle();
                await System.Threading.Tasks.Task.Delay(1200);
            }
            // 从左侧栏进入设置 › 外观与主题
            PadView();
            await System.Threading.Tasks.Task.Delay(700);
            for (int i = 0; i < 6; i++) { Move(Direction.Up); await System.Threading.Tasks.Task.Delay(80); }
            for (int i = 0; i < 4; i++) { Move(Direction.Down); await System.Threading.Tasks.Task.Delay(120); }
            Activate();
            await System.Threading.Tasks.Task.Delay(900);
            // 移到第 3 个分类「外观与主题」并进右列色板网格
            Move(Direction.Down); await System.Threading.Tasks.Task.Delay(200);
            Move(Direction.Down); await System.Threading.Tasks.Task.Delay(250);
            Move(Direction.Right); await System.Threading.Tasks.Task.Delay(300);
            Move(Direction.Down); await System.Threading.Tasks.Task.Delay(350);
            await Still("01-nebula-default");

            // 切换到「青瓷薄荷」（第 3 个色板，下标 2）
            Move(Direction.Right); await System.Threading.Tasks.Task.Delay(200);
            Move(Direction.Right); await System.Threading.Tasks.Task.Delay(200);
            Activate();
            await Still("02-celadon-mint");

            // 切换到「朝霞珊瑚」（第 6 个色板，下标 5）
            Move(Direction.Down); await System.Threading.Tasks.Task.Delay(250);
            Activate();
            await Still("03-sakura-coral");

            // 往下进入「主题色取色板」并打开 ModalSheet
            Move(Direction.Down); await System.Threading.Tasks.Task.Delay(200);
            Move(Direction.Down); await System.Threading.Tasks.Task.Delay(250);
            Activate();
            await Still("04-color-picker");

            // 在取色板里切到「霞光冷暖」并旋转色相看看实时预览
            Move(Direction.Up); await System.Threading.Tasks.Task.Delay(150);
            Move(Direction.Up); await System.Threading.Tasks.Task.Delay(150);
            Move(Direction.Right); await System.Threading.Tasks.Task.Delay(150);
            Move(Direction.Right); await System.Threading.Tasks.Task.Delay(150);
            Activate(); await System.Threading.Tasks.Task.Delay(150);
            Move(Direction.Down); await System.Threading.Tasks.Task.Delay(150);
            Move(Direction.Down); await System.Threading.Tasks.Task.Delay(150);
            for (int k = 0; k < 8; k++) { Move(Direction.Left); await System.Threading.Tasks.Task.Delay(90); }
            await Still("05-picker-twilight");

            CtpClose(commit: false);
            await System.Threading.Tasks.Task.Delay(350);
        }
        catch (Exception ex) { Log.Error("reccocoontheme", ex); }
        finally
        {
            SaveUi(savedPrefs);
            RefreshCocoonThemeVisuals();
            Log.Info("reccocoontheme done");
            if (Program.Args.Contains("--exit")) Microsoft.UI.Xaml.Application.Current.Exit();
        }
    }
}
