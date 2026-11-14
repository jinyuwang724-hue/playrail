// 控制中心第三种样式「色带」（11-14 jinyu：新样式，保留拍立得和卡片）。
// 掌机前端的控制中心要一屏看全、扫一眼就有状态：六个功能是六条全高的色带，同时都在屏幕上；
// 焦点那条用弹簧撑开到约 44 % 宽，写图标、名字和实时读数（功耗 / 手柄 / 刷新率 / 电量），其余收成窄条只留图标。
// ← / → 换焦点（撑开的那条换人），A：焦点那条继续铺满全屏再交给子页面，B 回来时色带已经复位。
// 不用任何图片素材：只有功能色、线条图标和文字。Y 在 拍立得 → 卡片 → 色带 之间轮换，存在 controlcenter.ini。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class CcBand
    {
        public required ContainerVisual El;
        public required CompositionRoundedRectangleGeometry Geo;
        public required CompositionColorBrush Fill;
        public required SpriteVisual Wide, Narrow;
        public string Drawn = "";
    }

    /// <summary>色带样式开着（和 _ccCards 互斥）。</summary>
    private bool _ccBands = LoadCcBands();
    private ContainerVisual? _ccBandsRoot;
    private bool _ccBandsGoing;
    private readonly List<CcBand> _ccBandList = new();
    private (float W, float H) _ccBandsDrawnFor;

    // 设计 px（1280 × 800 设计稿，× _ccK 到 DIP）
    private const float BdEdge = 40, BdTop = 112, BdBottomGap = 32 + 48 + 28, BdGap = 10, BdRadius = 22, BdWideShare = 0.44f;
    private static readonly Spring BdSpring = new(300, 30, 1);   // ζ ≈ 0.87：撑开不晃，换焦点连按可打断

    private static bool LoadCcBands()
    {
        try { return File.Exists(CcPrefsPath) && File.ReadAllLines(CcPrefsPath).Contains("bands=true"); }
        catch { return false; }
    }

    private void SaveCcStyle()
    {
        try { Directory.CreateDirectory(App.CacheDir); File.WriteAllText(CcPrefsPath, $"cards={(_ccCards ? "true" : "false")}\nbands={(_ccBands ? "true" : "false")}"); }
        catch (Exception ex) { Log.Warn($"control center prefs: {ex.Message}"); }
    }

    /// <summary>Y：拍立得 → 卡片 → 色带 → 拍立得。</summary>
    private void CcCycleStyle()
    {
        if (_ccBands) { _ccBands = false; SetCcCardStyle(false); }
        else if (_ccCards) { _ccBands = true; SetCcCardStyle(false); }
        else SetCcCardStyle(true);
        SaveCcStyle();
        CcBandsApply();
        Toast($"控制中心样式：{(_ccBands ? "色带" : _ccCards ? "卡片" : "拍立得")}");
    }


    // ---------------- 建立 / 切换 ----------------

    private void CcEnsureBands()
    {
        if (_cc == null || _ccBandsRoot != null) return;
        _ccBandsRoot = G.Container();
        // 在卡片排的位置（背景之上、左下功能坞之下）
        _cc.Children.InsertAbove(_ccBandsRoot, _ccRow!);
        foreach (var b in _ccBlocks)
        {
            var geo = G.C.CreateRoundedRectangleGeometry();
            geo.CornerRadius = new Vector2(BdRadius);
            var fill = G.C.CreateColorBrush(CcBandColor(b, false));
            var shape = G.C.CreateSpriteShape(geo);
            shape.FillBrush = fill;
            var sv = G.C.CreateShapeVisual();
            sv.Size = new Vector2(2000, 1400);
            sv.Shapes.Add(shape);
            var el = G.C.CreateContainerVisual();
            el.Children.InsertAtTop(sv);
            var wide = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            var narrow = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            wide.Opacity = 0;
            el.Children.InsertAtTop(wide);
            el.Children.InsertAtTop(narrow);
            _ccBandsRoot.Children.InsertAtTop(el);
            _ccBandList.Add(new CcBand { El = el, Geo = geo, Fill = fill, Wide = wide, Narrow = narrow });
        }
    }

    /// <summary>按当前样式显示色带或卡片排（CcMeasure、换样式、打开时调）。</summary>
    private void CcBandsApply()
    {
        if (_cc == null) return;
        if (_ccBands) CcEnsureBands();
        if (_ccRow != null) _ccRow.IsVisible = !_ccBands;
        if (_ccLabel != null) _ccLabel.IsVisible = !_ccBands;
        if (_ccBandsRoot == null) return;
        _ccBandsRoot.IsVisible = _ccBands;
        if (!_ccBands) return;
        _ccBandsRoot.Scale = new Vector3(_ccK, _ccK, 1);
        CcBandsPaint();
        CcBandsLayout(animate: false);
    }

    private float BdW => (float)View.Width / _ccK;
    private float BdH => (float)View.Height / _ccK;

    /// <summary>第 i 条的矩形（设计 px）：焦点那条宽，其余平分剩下的宽度。</summary>
    private (float X, float Y, float W, float H) CcBandRect(int i)
    {
        float left = BdEdge, right = BdW - BdEdge, top = BdTop, bottom = BdH - BdBottomGap;
        int n = _ccBandList.Count;
        float total = right - left, wide = MathF.Round(total * BdWideShare), narrow = (total - wide - BdGap * (n - 1)) / (n - 1);
        float x = left;
        for (int j = 0; j < i; j++) x += (j == _ccFocus ? wide : narrow) + BdGap;
        return (x, top, i == _ccFocus ? wide : narrow, bottom - top);
    }

    private float BdWideW => MathF.Round((BdW - 2 * BdEdge) * BdWideShare);
    private float BdNarrowW => (BdW - 2 * BdEdge - BdWideW - BdGap * (_ccBandList.Count - 1)) / (_ccBandList.Count - 1);

    /// <summary>每条色带走到自己的位置；焦点那条亮、写全文字，其余压暗只留图标。</summary>
    private void CcBandsLayout(bool animate)
    {
        if (_ccBandsRoot == null || !_ccBands) return;
        for (int i = 0; i < _ccBandList.Count; i++)
        {
            var band = _ccBandList[i];
            var r = CcBandRect(i);
            bool on = i == _ccFocus;
            var off = new Vector3(r.X, r.Y, 0);
            var size = new Vector2(r.W, r.H);
            if (!animate || Reduced)
            {
                // 弹簧（NaturalMotion）停下后同一帧里直接写属性会被它最后一拍盖掉（铺满后回来还停在全屏）：
                // 改成 1 ms 的关键帧动画把值「推」过去
                band.El.StartAnimation("Offset", CcBandSnapV3(off));
                band.Geo.StartAnimation("Size", CcBandSnapV2(size));
                band.Geo.StartAnimation("Offset", CcBandSnapV2(Vector2.Zero));
                band.Geo.StartAnimation("CornerRadius", CcBandSnapV2(new Vector2(BdRadius)));
                band.Fill.StopAnimation("Color"); band.Fill.Color = CcBandColor(_ccBlocks[i], on);
                band.Wide.StopAnimation("Opacity"); band.Wide.Opacity = on ? 1 : 0;
                band.Narrow.StopAnimation("Opacity"); band.Narrow.Opacity = on ? 0 : 1;
            }
            else
            {
                band.El.StartAnimation("Offset", G.SpringV3(off, BdSpring));
                band.Geo.StartAnimation("Size", CcBandSpringV2(size));
                band.Geo.StartAnimation("Offset", CcBandSpringV2(Vector2.Zero));
                band.Geo.StartAnimation("CornerRadius", CcBandSpringV2(new Vector2(BdRadius)));
                var c = G.C.CreateColorKeyFrameAnimation();
                c.InsertKeyFrame(1, CcBandColor(_ccBlocks[i], on), G.Ease(EaseOutCss));
                c.Duration = Gfx.Ms(260);
                band.Fill.StartAnimation("Color", c);
                // 文字等撑开到一半再出来（不会在窄条里挤成一团）；收窄时先走
                band.Wide.StartAnimation("Opacity", G.Scalar(band.Wide.Opacity, on ? 1 : 0, on ? 220 : 90, EaseOutCss, on ? 110 : 0));
                band.Narrow.StartAnimation("Opacity", G.Scalar(band.Narrow.Opacity, on ? 0 : 1, on ? 90 : 200, EaseOutCss, on ? 0 : 90));
            }
            band.Narrow.Offset = new Vector3((BdNarrowW - band.Narrow.Size.X) / 2, 24, 0);
        }
    }

    private Vector2KeyFrameAnimation CcBandSnapV2(Vector2 to)
    {
        var a = G.C.CreateVector2KeyFrameAnimation();
        a.InsertKeyFrame(0, to, G.Linear);
        a.InsertKeyFrame(1, to, G.Linear);
        a.Duration = Gfx.Ms(1);
        return a;
    }

    private Vector3KeyFrameAnimation CcBandSnapV3(Vector3 to)
    {
        var a = G.C.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, to, G.Linear);
        a.InsertKeyFrame(1, to, G.Linear);
        a.Duration = Gfx.Ms(1);
        return a;
    }

    private SpringVector2NaturalMotionAnimation CcBandSpringV2(Vector2 to)
    {
        var a = G.C.CreateSpringVector2Animation();
        a.FinalValue = to;
        a.DampingRatio = BdSpring.Zeta;
        a.Period = BdSpring.Period;
        return a;
    }

    /// <summary>焦点：功能色本身；其余：功能色压到三成亮，和背景拉开但还认得出颜色。</summary>
    private static Color CcBandColor(CcBlock b, bool on) =>
        on ? b.Accent : Gfx.Mix(Gfx.Rgba(14, 14, 16), 0.68, b.Accent);

    // ---------------- 画 ----------------

    /// <summary>内容变了才重画（每秒的读数刷新也走这里）。</summary>
    private void CcBandsPaint()
    {
        if (_ccBandsRoot == null || !_ccBands) return;
        float raster = _ccK * Chrome.Dpi;
        float ww = BdWideW, hh = BdH - BdBottomGap - BdTop, nw = BdNarrowW;
        bool sizeChanged = _ccBandsDrawnFor != (ww, hh);
        _ccBandsDrawnFor = (ww, hh);
        for (int i = 0; i < _ccBandList.Count; i++)
        {
            var b = _ccBlocks[i];
            var band = _ccBandList[i];
            string key = $"{b.Name}|{b.Reading}|{b.Summary}|{b.Second}|{b.Accent}|{ww}x{hh}";
            if (!sizeChanged && key == band.Drawn) continue;
            band.Drawn = key;
            band.Wide.Size = new Vector2(ww, hh);
            CcSet(band.Wide, CcDraw(ww, hh, raster, ds => CcPaintBandWide(ds, b, ww, hh)));
            const float ns = 56;
            band.Narrow.Size = new Vector2(ns, ns);
            CcSet(band.Narrow, CcDraw(ns, ns, raster, ds => LineIcons.Draw(ds, b.Icon, ns / 2, ns / 2, 24, Gfx.WithAlpha(Gfx.Rgba(255, 255, 255), 0.82))));
            band.Narrow.Offset = new Vector3((nw - ns) / 2, 24, 0);
        }
    }

    /// <summary>撑开那条：左上图标，下面名字；左下大读数，读数下一行说明。全是白字，压在功能色上。</summary>
    private void CcPaintBandWide(CanvasDrawingSession ds, CcBlock b, float w, float h)
    {
        var ink = Gfx.Rgba(255, 255, 255);
        const float pad = 32;
        // 底部压一层渐暗，读数在亮色（黄、橙）上也看得清
        using (var g = new Microsoft.Graphics.Canvas.Brushes.CanvasLinearGradientBrush(ds, Gfx.Rgba(0, 0, 0, 0), Gfx.Rgba(0, 0, 0, 0.32)))
        {
            g.StartPoint = new Vector2(0, h * 0.45f);
            g.EndPoint = new Vector2(0, h);
            ds.FillRoundedRectangle(0, 0, w, h, BdRadius, BdRadius, g);
        }
        LineIcons.Draw(ds, b.Icon, pad + 18, pad + 18, 34, ink);
        using (var name = TextKit.Layout(ds, b.Name, TextKit.Ui(700), 30, w - pad * 2, CanvasHorizontalAlignment.Left))
            ds.DrawTextLayout(name, pad, pad + 56 - (float)name.LayoutBounds.Y, ink);
        string big = b.HasReading && b.Reading.Length > 0 ? b.Reading : b.Second;
        string small = b.Summary.Length > 0 ? b.Summary : b.HasReading ? b.Second : "";
        if (b.HasReading && small.EndsWith(big.Trim())) small = "";   // 「刷新率 120 Hz」和大读数重复时不写第二遍
        float bottom = h - pad;
        if (small.Length > 0)
        {
            using var s = TextKit.Layout(ds, small, TextKit.Ui(500), 17, w - pad * 2, CanvasHorizontalAlignment.Left);
            s.WordWrapping = CanvasWordWrapping.NoWrap;
            s.TrimmingGranularity = CanvasTextTrimmingGranularity.Character;
            s.TrimmingSign = CanvasTrimmingSign.Ellipsis;
            bottom -= (float)s.LayoutBounds.Height;
            ds.DrawTextLayout(s, pad, bottom - (float)s.LayoutBounds.Y, Gfx.WithAlpha(ink, 0.82));
            bottom -= 10;
        }
        float bigSize = b.HasReading ? 64 : 30;
        using var l = TextKit.Layout(ds, big, TextKit.Ui((ushort)(b.HasReading ? 300 : 600)), bigSize, w - pad * 2, CanvasHorizontalAlignment.Left);
        l.WordWrapping = CanvasWordWrapping.NoWrap;
        bottom -= (float)l.LayoutBounds.Height;
        ds.DrawTextLayout(l, pad, bottom - (float)l.LayoutBounds.Y, ink);
    }

    // ---------------- 输入 ----------------

    /// <summary>A：焦点那条铺满全屏（圆角收成 0），铺到一半时交给子页面；页面盖上之后色带悄悄复位。</summary>
    private bool CcBandsActivate(Action open)
    {
        if (!_ccBands || _ccBandsRoot == null || Reduced) return false;
        var band = _ccBandList[_ccFocus];
        var r = CcBandRect(_ccFocus);
        band.El.StartAnimation("Offset", G.SpringV3(Vector3.Zero, BdSpring));
        band.Geo.StartAnimation("Size", CcBandSpringV2(new Vector2(BdW, BdH)));
        band.Geo.StartAnimation("CornerRadius", CcBandSpringV2(Vector2.Zero));
        band.Wide.StartAnimation("Opacity", G.Scalar(band.Wide.Opacity, 0, 120, EaseOutCss));
        // 铺开的这条放到最上面，盖住其它色带和功能坞
        _ccBandsRoot.Children.Remove(band.El);
        _ccBandsRoot.Children.InsertAtTop(band.El);
        int v = _ccVersion;
        After(170, () =>
        {
            if (v != _ccVersion || !_ccOpen) return;
            open();
            After(520, () => CcBandsLayout(animate: false));   // 页面已经盖上（_ccVersion 进页面时会变，这里不比对）
        });
        return true;
    }

    /// <summary>点在色带上：点焦点那条 = 进入，点别的 = 换焦点。返回 true = 命中。</summary>
    private bool CcBandsClick(double sx, double sy)
    {
        if (!_ccBands || _ccBandsRoot == null) return false;
        float x = (float)(sx / _ccK), y = (float)(sy / _ccK);
        for (int i = 0; i < _ccBandList.Count; i++)
        {
            var r = CcBandRect(i);
            if (x < r.X || x > r.X + r.W || y < r.Y || y > r.Y + r.H) continue;
            if (i == _ccFocus) CcActivate(); else { Cue(Sfx.Nav); CcFocus(i); }
            return true;
        }
        return false;
    }
}
