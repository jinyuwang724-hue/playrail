// 从 Cocoon 左侧栏进的「相册」（11-27 jinyu 确认的设计稿：claude.ai/artifact/T7xW33fuZ8VcU1DhFpFCib）。
// 只在 _ccLight（Cocoon 主题从侧栏进）时接管截图页；数据、收藏、复制、打开文件夹、删除都还是 WallStage.Shots.cs 那一套，
// 这里只管画和输入：
//   主页（1920×1200）：左上「B ←」胶囊 +「相册」；左列 = 筛选（全部 / 收藏 / 有截图的游戏 / 其他，带张数），
//     选中 = 白卡 + 紫字；右列按天分组，三列 16:9 圆角缩略图，收藏的右上角一颗白底星星；右列延伸到屏幕顶、顶部渐隐。
//   看大图：整屏铺满（保持比例），底部一颗白色信息胶囊（游戏图标、名字、时间 · 来源、第几张）+ 一行小缩略条。
//   Y 菜单：照 Cocoon 游戏菜单（左边这张图和游戏名，右边白卡：收藏 / 复制到剪贴板 / 在文件夹中显示 / 删除）。
//   删除确认：和「退出游戏」同一种小窗，白色取消 + 红色删除。
// 手柄：左列 ↑↓ 换筛选（右列立刻换、回到顶）、→ / A 进网格；网格里方向键走，A 看大图，Y 菜单，RB 收藏，B 回左列再 B 返回。
// 大图里 ← → 换张，A / Y 菜单，X 藏起胶囊和缩略条，B 回网格。触屏：点筛选、点图看大图、拖动滚、大图里点胶囊开菜单。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using Rect = Windows.Foundation.Rect;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private bool CshActive => _ccLight && _ccOpen && _shOpen;

    // 几何（1920×1200 设计单位）
    private const float CshLeftX = 44, CshLeftW = 562, CshSlot = 124, CshCols = 3, CshGapX = 28, CshGapY = 28, CshDateH = 58, CshGroupGap = 30;
    private static float CshThumbW => (CpListW - (CshCols - 1) * CshGapX) / CshCols;
    private static float CshThumbH => CshThumbW * 9 / 16;
    private const float CshPillY = 930, CshPillH = 116, CshStripY = 1062, CshStripW = 150, CshStripH = 84, CshStripGap = 18;
    private const float CshMenuX = 1100, CshMenuW = 616, CshMenuRowH = 116;
    private const float CshDlgW = 920, CshDlgH = 780;

    private sealed class CshThumb
    {
        public required Capture Shot;
        public required ContainerVisual El;
        public required SpriteVisual Img;
        public SpriteVisual? Fav;
        public LoadedImageSurface? Ref;
        public float X, Y;
    }

    private sealed class CshView
    {
        public required ContainerVisual Root, LeftBox, LeftList, RightBox, RightList, Viewer, VStrip, Menu, Dlg;
        public required SpriteVisual Bg, Head, LeftCanvas, Empty, Toast, VBlack, VImg, VShade, VPill, VIcon, MScrim, MImg, MName, MCard, DScrim, DCard, DImg;
        public required CurSet Cur, VCur, MCur;
        public CompositionDrawingSurface? HeadS, LeftS, EmptyS, ToastS, VPillS, MNameS, MCardS, DCardS, ShadowS, FavS;
        public CompositionSurfaceBrush? ShadowB, FavB;
        public int ToastGen;
        public readonly List<(SpriteVisual V, LoadedImageSurface? Ref)> Covers = new();
        public readonly List<CshThumb> Thumbs = new();
        public readonly List<(SpriteVisual V, CompositionDrawingSurface S)> Dates = new();
        public readonly List<(SpriteVisual V, LoadedImageSurface? Ref)> Strip = new();
        public LoadedImageSurface? VRef, VIconRef, MRef, DRef;
        public string? VPath;
        public float LeftScroll, RightScroll, RightH;
        public bool RightInit, LeftInit, Manual;
        public string LeftKey = "", GridKey = "", HeadKey = "";
    }

    private CshView? _csh;
    private int _cshZone;
    private List<ShFilterItem> _cshItems = new();
    private bool _cshMenuOpen, _cshDlgOpen;
    private int _cshMenuSel;
    private Capture? _cshTarget;

    // ---------------- 生命周期 ----------------

    /// <summary>OpenShots 里调：Cocoon 相册每次从左列「全部」开始，一张一格（不叠连拍）。</summary>
    private void CshOnOpen()
    {
        _cshZone = _shFilter == null ? 0 : 1;
        _cshMenuOpen = _cshDlgOpen = false;
        _cshTarget = null;
        _shStyle = ShStyle.Projector;
        if (_csh is { } v)
        {
            v.LeftKey = v.GridKey = "";
            v.LeftScroll = v.RightScroll = 0;
            v.LeftInit = v.RightInit = false;
            v.Manual = false;
            v.Cur.Shown = false;
            foreach (var c in v.Cur.All) { c.StopAnimation("Opacity"); c.Opacity = 0; }
            v.Viewer.IsVisible = v.Menu.IsVisible = v.Dlg.IsVisible = false;
        }
    }

    /// <summary>ShReleaseAll 里调：关页面时把解码的图都还回去。</summary>
    private void CshReleaseAll()
    {
        if (_csh is not { } v) return;
        foreach (var t in v.Thumbs) { Images.Release(t.Ref); t.Ref = null; }
        foreach (var (_, r) in v.Covers) Images.Release(r);
        v.Covers.Clear();
        foreach (var (_, r) in v.Strip) Images.Release(r);
        v.Strip.Clear();
        v.VStrip.Children.RemoveAll();
        foreach (var r in new[] { v.VRef, v.VIconRef, v.MRef, v.DRef }) Images.Release(r);
        v.VRef = v.VIconRef = v.MRef = v.DRef = null;
        v.VPath = null;
        v.LeftKey = v.GridKey = "";
        v.LeftList.Children.RemoveAll();
        v.LeftList.Children.InsertAtTop(v.LeftCanvas);
        v.RightList.Children.RemoveAll();
        v.Thumbs.Clear();
        foreach (var (_, s) in v.Dates) s.Dispose();
        v.Dates.Clear();
        v.Root.IsVisible = false;
    }

    private CshView CshEnsure()
    {
        if (_csh is { } existing) return existing;
        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        ContainerVisual Box() { var c = G.Container(relative: false); c.Size = new Vector2(CsW, CsH); return c; }
        var root = Box();
        var g = G.C.CreateLinearGradientBrush();
        g.StartPoint = new Vector2(0, 0); g.EndPoint = new Vector2(0, 1);
        g.ColorStops.Add(CkStop(0, Color.FromArgb(255, 0xEF, 0xEF, 0xF4)));
        g.ColorStops.Add(CkStop(1, Color.FromArgb(255, 0xE2, 0xE3, 0xEA)));
        var bg = G.Sprite(g, relative: false);
        bg.Size = new Vector2(CsW, CsH);

        var leftBox = Box();
        leftBox.Clip = G.C.CreateInsetClip(0, CpTop - 14, CsW - (CshLeftX + CshLeftW + 30), CsH - CpBottom);
        var leftList = G.Container(relative: false);
        var leftCanvas = Sp();
        leftList.Children.InsertAtTop(leftCanvas);
        leftBox.Children.InsertAtTop(leftList);

        var rightBox = Box();
        rightBox.Clip = G.C.CreateInsetClip(CpListX - 40, 0, 0, CsH - CpBottom);
        var rightList = G.Container(relative: false);
        rightBox.Children.InsertAtTop(rightList);
        {
            var fg = G.C.CreateLinearGradientBrush();
            fg.StartPoint = new Vector2(0, 0); fg.EndPoint = new Vector2(0, 1);
            var top = Color.FromArgb(255, 0xEF, 0xEF, 0xF4);
            fg.ColorStops.Add(CkStop(0, top));
            fg.ColorStops.Add(CkStop(0.35f, Color.FromArgb(235, top.R, top.G, top.B)));
            fg.ColorStops.Add(CkStop(1, Color.FromArgb(0, top.R, top.G, top.B)));
            var fade = G.Sprite(fg, relative: false);
            fade.Size = new Vector2(CsW - (CpListX - 40), CpTop);
            fade.Offset = new Vector3(CpListX - 40, 0, 0);
            rightBox.Children.InsertAtTop(fade);
        }

        // 看大图
        var viewer = Box();
        viewer.IsVisible = false;
        var vBlack = G.Sprite(G.Solid(Color.FromArgb(255, 0x0B, 0x0B, 0x0E)), relative: false);
        vBlack.Size = new Vector2(CsW, CsH);
        var vImg = G.Sprite(G.Solid(Color.FromArgb(0, 0, 0, 0)), relative: false);
        vImg.Size = new Vector2(CsW, CsH);
        var vStrip = G.Container(relative: false);
        // 底部一段由透明到半黑的渐变，胶囊和缩略条压在图上也看得清（跟着它们一起藏）
        var sg = G.C.CreateLinearGradientBrush();
        sg.StartPoint = new Vector2(0, 0); sg.EndPoint = new Vector2(0, 1);
        sg.ColorStops.Add(CkStop(0, Color.FromArgb(0, 0, 0, 0)));
        sg.ColorStops.Add(CkStop(0.45f, Color.FromArgb(90, 0, 0, 0)));
        sg.ColorStops.Add(CkStop(1, Color.FromArgb(175, 0, 0, 0)));
        var vShade = G.Sprite(sg, relative: false);
        vShade.Size = new Vector2(CsW, CsH - (CshPillY - 200));
        vShade.Offset = new Vector3(0, CshPillY - 200, 0);
        var vPill = Sp();
        var vIcon = G.Sprite(CkSolid(Color.FromArgb(255, 0xD8, 0xD9, 0xE0)), relative: false);
        foreach (var x in new Visual[] { vBlack, vImg, vShade, vStrip, vPill, vIcon }) viewer.Children.InsertAtTop(x);
        var vCur = CurNew(viewer);

        // Y 菜单
        var menu = Box();
        menu.IsVisible = false;
        var mScrim = G.Sprite(G.Solid(RmScrim), relative: false);
        mScrim.Size = new Vector2(CsW, CsH);
        var mImg = G.Sprite(CkSolid(Color.FromArgb(255, 0xD8, 0xD9, 0xE0)), relative: false);
        var mName = Sp();
        var mCard = Sp();
        foreach (var x in new Visual[] { mScrim, mImg, mName, mCard }) menu.Children.InsertAtTop(x);
        var mCur = CurNew(menu);

        // 删除确认
        var dlg = Box();
        dlg.IsVisible = false;
        var dScrim = G.Sprite(G.Solid(RmScrim), relative: false);
        dScrim.Size = new Vector2(CsW, CsH);
        var dCard = Sp();
        var dImg = G.Sprite(CkSolid(Color.FromArgb(255, 0xD8, 0xD9, 0xE0)), relative: false);
        foreach (var x in new Visual[] { dScrim, dCard, dImg }) dlg.Children.InsertAtTop(x);

        var head = Sp();
        var empty = Sp();
        var toast = Sp();
        toast.Opacity = 0;
        foreach (var x in new Visual[] { bg, head, leftBox, rightBox, empty }) root.Children.InsertAtTop(x);
        var cur = CurNew(root);
        foreach (var x in new Visual[] { viewer, menu, dlg, toast }) root.Children.InsertAtTop(x);
        _sh!.Children.InsertAtTop(root);

        var v = new CshView
        {
            Root = root, LeftBox = leftBox, LeftList = leftList, RightBox = rightBox, RightList = rightList, Viewer = viewer, VStrip = vStrip, Menu = menu, Dlg = dlg,
            Bg = bg, Head = head, LeftCanvas = leftCanvas, Empty = empty, Toast = toast, VBlack = vBlack, VImg = vImg, VShade = vShade, VPill = vPill, VIcon = vIcon,
            MScrim = mScrim, MImg = mImg, MName = mName, MCard = mCard, DScrim = dScrim, DCard = dCard, DImg = dImg,
            Cur = cur, VCur = vCur, MCur = mCur,
        };
        CshPaintShared(v);
        _csh = v;
        return v;
    }

    /// <summary>每张缩略图共用的投影和收藏星星（一张画布，多个精灵共用同一支笔刷）。</summary>
    private void CshPaintShared(CshView v)
    {
        float R = CsRaster, W = CshThumbW, H = CshThumbH, pad = 40;
        v.ShadowS = G.Surface((int)Math.Ceiling((W + pad * 2) * R), (int)Math.Ceiling((H + pad * 2) * R));
        using (var ds = CanvasComposition.CreateDrawingSession(v.ShadowS))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(R);
            RsSoftShadow(ds, 0, 3, W, H, 24, 5, 0.10f);
            RsSoftShadow(ds, 0, 12, W, H, 24, 16, 0.14f);
        }
        v.ShadowB = G.C.CreateSurfaceBrush(v.ShadowS);
        float d = 46;
        v.FavS ??= G.Surface((int)Math.Ceiling((d + 20) * R), (int)Math.Ceiling((d + 20) * R));
        CshPaintFavSurf(v.FavS, R, d);
        v.FavB = G.C.CreateSurfaceBrush(v.FavS);
    }

    private void CshPaintFavSurf(CompositionDrawingSurface surf, float R, float d = 46)
    {
        using var ds = CanvasComposition.CreateDrawingSession(surf);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(10, 10) * Matrix3x2.CreateScale(R);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        RsSoftShadow(ds, 0, 2, d, d, d / 2, 4, 0.18f);
        ds.FillCircle(d / 2, d / 2, d / 2, Color.FromArgb(240, 0xFF, 0xFF, 0xFF));
        using var star = CshStar(ds.Device, d / 2, d / 2 + 0.5f, 13);
        ds.FillGeometry(star, CpAccentInk);
    }

    // ---------------- 画 ----------------

    /// <summary>截图页的 ShBuildBody / ShPaintAll 在 Cocoon 主题下改走这里：内容变了才重建，焦点 / 滚动每次都跟。</summary>
    private void CshRefresh()
    {
        if (!CshActive || _sh == null) return;
        var v = CshEnsure();
        foreach (var old in new Visual?[] { _shBody, _shTitle, _shTabsSprite, _shFilterSprite }) if (old != null) old.IsVisible = false;
        v.Root.IsVisible = true;
        v.Root.Scale = new Vector3(_ccK / 1.5f, _ccK / 1.5f, 1);
        CshPaintHead(v);

        _cshItems = ShBuildFilterItems();
        if (_cshItems.Count == 0 || _cshItems.FindIndex(i => i.Key == _shFilter) < 0)
        {
            if (_shFilter != null && _shLoaded) { _shFilter = null; ShRebuild(); return; }
        }
        string leftKey = $"{_shLoaded}|{_shFilter}|{CocoonThemeStamp}|{string.Join(",", _cshItems.Select(i => $"{i.Key}:{i.Name}:{i.Count}"))}";
        if (leftKey != v.LeftKey) { v.LeftKey = leftKey; CshPaintLeft(v); }

        var flat = _shUnits.Select(u => u.Top).ToList();
        string gridKey = $"{_shLoaded}|{_shFilter}|{flat.Count}|{(flat.Count > 0 ? flat[0].Path + flat[^1].Path : "")}|{string.Join(",", flat.Where(c => _shFavs.Contains(c.Path)).Select(c => c.Path.Length))}";
        if (gridKey != v.GridKey)
        {
            bool sameFilter = v.GridKey.StartsWith($"{_shLoaded}|{_shFilter}|", StringComparison.Ordinal);
            v.GridKey = gridKey;
            CshBuildGrid(v, flat);
            if (!sameFilter) { v.RightScroll = 0; v.RightInit = false; v.Manual = false; }
        }
        if (_shUnits.Count == 0 && _cshZone == 1) _cshZone = 0;
        CshPaintEmpty(v);
        CshScrollAndFocus(v, move: true);
        CshEnsureImages(v);
    }

    private void CshPaintHead(CshView v)
    {
        string hKey = $"相册|{CocoonThemeStamp}";
        if (v.HeadKey == hKey) return;
        v.HeadKey = hKey;
        float x0 = 20, y0 = 20, w = 700, h = 120;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.HeadS, v.Head, x0, y0, w, h));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(40 - 134 - x0, 36 - 104 - y0) * Matrix3x2.CreateScale(CsRaster);
        RsPaintPills(ds, noY: true);
        ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
        using var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 48, FontWeight = RsWt("相册"), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        ds.DrawText("相册", new Rect(198, 36, 480, 74), RmInk, f);
    }

    private static string CshItemName(ShFilterItem it) => it.Key == null ? "全部" : it.Name;

    /// <summary>左列：每项一行（图标 / 游戏封面小方块 + 名字 + 张数），选中的一张白卡、紫字。</summary>
    private void CshPaintLeft(CshView v)
    {
        foreach (var (sp, r) in v.Covers) { v.LeftList.Children.Remove(sp); Images.Release(r); }
        v.Covers.Clear();
        int sel = Math.Max(0, _cshItems.FindIndex(i => i.Key == _shFilter));
        float H = Math.Max(1, _cshItems.Count) * CshSlot + 20, pad = 30;
        using (var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.LeftS, v.LeftCanvas, -pad, -pad, CshLeftW + pad * 2, H + pad * 2)))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(CsRaster);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            if (!_shLoaded && _cshItems.Count <= 1)
            {
                using var lf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 32, VerticalAlignment = CanvasVerticalAlignment.Center };
                ds.DrawText("正在读取…", new Rect(36, 0, 500, CshSlot), RsSub, lf);
            }
            else
                for (int i = 0; i < _cshItems.Count; i++)
                {
                    var it = _cshItems[i];
                    float y = i * CshSlot, cy = y + CshSlot / 2;
                    bool on = i == sel;
                    if (on) CpCard(ds, 0, y + 4, CshLeftW, CshSlot - 8, null, 30);
                    var ink = on ? CpAccentInk : RmInk;
                    if (it.Key is null or "fav" or "other") CshGlyph(ds, it.Key ?? "all", 66, cy, ink);
                    string name = CshItemName(it);
                    using (var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 36, FontWeight = CpTitleWt, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                        ds.DrawText(name, new Rect(126, y, CshLeftW - 126 - 110, CshSlot), ink, f);
                    using (var nf = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 30, FontWeight = new Windows.UI.Text.FontWeight(500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                        ds.DrawText(it.Count.ToString(), new Rect(CshLeftW - 136, y, 100, CshSlot), on ? Color.FromArgb(190, CpAccentInk.R, CpAccentInk.G, CpAccentInk.B) : Ck(Color.FromArgb(255, 0x8E, 0x90, 0x98)), nf);
                }
        }
        // 游戏：封面小方块（单独的精灵，图是游戏库自己的封面）
        float R = CsRaster, s = 60;
        for (int i = 0; i < _cshItems.Count; i++)
        {
            var it = _cshItems[i];
            if (it.Key is null or "fav" or "other") continue;
            var sp = G.Sprite(CkSolid(Color.FromArgb(255, 0xD8, 0xD9, 0xE0)), relative: false);
            LoadedImageSurface? r = null;
            if (_lib.ById.TryGetValue(it.Key, out var item) && RqArt(item) is { } art && (r = Images.Acquire(art, (int)Math.Ceiling(s * R), (int)Math.Ceiling(s * 1.5f * R))) != null)
            {
                var b = G.C.CreateSurfaceBrush(r);
                b.Stretch = CompositionStretch.UniformToFill;
                sp.Brush = b;
            }
            sp.Size = new Vector2(s, s);
            sp.Offset = new Vector3(66 - s / 2, i * CshSlot + CshSlot / 2 - s / 2, 0);
            var rr = G.C.CreateRoundedRectangleGeometry();
            rr.Size = new Vector2(s, s);
            rr.CornerRadius = new Vector2(16, 16);
            sp.Clip = G.C.CreateGeometricClip(rr);
            v.LeftList.Children.InsertAtTop(sp);
            v.Covers.Add((sp, r));
        }
    }

    /// <summary>左列的三种图标：全部（照片）、收藏（星星）、其他（三个点）。</summary>
    private static void CshGlyph(CanvasDrawingSession ds, string kind, float cx, float cy, Color ink)
    {
        switch (kind)
        {
            case "fav":
            {
                using var star = CshStar(ds.Device, cx, cy + 1, 25);
                ds.FillGeometry(star, ink);
                break;
            }
            case "other":
                for (int k = -1; k <= 1; k++) ds.FillCircle(cx + k * 16, cy, 6.5f, ink);
                break;
            default:
                ds.FillRoundedRectangle(cx - 27, cy - 21, 54, 42, 9, 9, ink);
                ds.FillCircle(cx - 11, cy - 7, 6, Colors.White);
                using (var pb = new CanvasPathBuilder(ds.Device))
                {
                    pb.BeginFigure(cx - 20, cy + 15); pb.AddLine(cx - 4, cy - 2); pb.AddLine(cx + 6, cy + 7); pb.AddLine(cx + 12, cy + 1); pb.AddLine(cx + 21, cy + 15); pb.EndFigure(CanvasFigureLoop.Closed);
                    using var m = CanvasGeometry.CreatePath(pb);
                    ds.FillGeometry(m, Colors.White);
                }
                break;
        }
    }

    private static CanvasGeometry CshStar(ICanvasResourceCreator dev, float cx, float cy, float r)
    {
        using var pb = new CanvasPathBuilder(dev);
        for (int k = 0; k < 10; k++)
        {
            float a = -MathF.PI / 2 + k * MathF.PI / 5, rr = k % 2 == 0 ? r : r * 0.48f;
            var p = new Vector2(cx + rr * MathF.Cos(a), cy + rr * MathF.Sin(a));
            if (k == 0) pb.BeginFigure(p); else pb.AddLine(p);
        }
        pb.EndFigure(CanvasFigureLoop.Closed);
        var g = CanvasGeometry.CreatePath(pb);
        var round = g.Stroke(r * 0.22f, new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round }).CombineWith(g, Matrix3x2.Identity, CanvasGeometryCombine.Union);
        g.Dispose();
        return round;
    }

    /// <summary>右列：按天分组的三列缩略图。每张一个小容器（共用的投影 + 圆角图 + 收藏星星），日期一行小字。图片只解码视口附近的。</summary>
    private void CshBuildGrid(CshView v, List<Capture> flat)
    {
        foreach (var t in v.Thumbs) Images.Release(t.Ref);
        v.Thumbs.Clear();
        foreach (var (_, s) in v.Dates) s.Dispose();
        v.Dates.Clear();
        v.RightList.Children.RemoveAll();
        float W = CshThumbW, H = CshThumbH, R = CsRaster, y = 0;
        DateTime? day = null;
        int col = 0;
        for (int i = 0; i < flat.Count; i++)
        {
            var c = flat[i];
            if (day != c.Time.Date)
            {
                if (day != null) y += (col > 0 ? H + CshGroupGap : CshGroupGap - CshGapY);
                day = c.Time.Date;
                col = 0;
                string text = ShDay(c.Time);
                var surf = G.Surface((int)Math.Ceiling(800 * R), (int)Math.Ceiling(CshDateH * R));
                using (var ds = CanvasComposition.CreateDrawingSession(surf))
                {
                    ds.Clear(Colors.Transparent);
                    ds.Transform = Matrix3x2.CreateScale(R);
                    using var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 30, FontWeight = new Windows.UI.Text.FontWeight(500), VerticalAlignment = CanvasVerticalAlignment.Top, WordWrapping = CanvasWordWrapping.NoWrap };
                    ds.DrawText(text, new Rect(6, 4, 790, CshDateH), Ck(Color.FromArgb(255, 0x7D, 0x80, 0x8A)), f);
                }
                var ds2 = G.Sprite(G.C.CreateSurfaceBrush(surf), relative: false);
                ds2.Size = new Vector2(800, CshDateH);
                ds2.Offset = new Vector3(0, y, 0);
                v.RightList.Children.InsertAtTop(ds2);
                v.Dates.Add((ds2, surf));
                y += CshDateH;
            }
            float x = col * (W + CshGapX);
            var el = G.Container(relative: false);
            el.Size = new Vector2(W, H);
            el.Offset = new Vector3(x, y, 0);
            var sh = G.Sprite(v.ShadowB!, relative: false);
            sh.Size = new Vector2(W + 80, H + 80);
            sh.Offset = new Vector3(-40, -40, 0);
            var img = G.Sprite(CkSolid(Color.FromArgb(255, 0xDE, 0xDF, 0xE5)), relative: false);
            img.Size = new Vector2(W, H);
            var rr = G.C.CreateRoundedRectangleGeometry();
            rr.Size = new Vector2(W, H);
            rr.CornerRadius = new Vector2(24, 24);
            img.Clip = G.C.CreateGeometricClip(rr);
            el.Children.InsertAtTop(sh);
            el.Children.InsertAtTop(img);
            SpriteVisual? fav = null;
            if (_shFavs.Contains(c.Path))
            {
                fav = G.Sprite(v.FavB!, relative: false);
                fav.Size = new Vector2(66, 66);
                fav.Offset = new Vector3(W - 16 - 46 - 10, 16 - 10, 0);
                el.Children.InsertAtTop(fav);
            }
            v.RightList.Children.InsertAtTop(el);
            v.Thumbs.Add(new CshThumb { Shot = c, El = el, Img = img, Fav = fav, X = x, Y = y });
            if (++col == CshCols) { col = 0; y += H + CshGapY; }
        }
        if (col > 0) y += H;
        v.RightH = y;
    }

    /// <summary>视口上下各一屏以内的图解码，离得远的还回去（截图多的时候不一次解几百张）。</summary>
    private void CshEnsureImages(CshView v)
    {
        float view = CpBottom - CpTop, lo = v.RightScroll - view * 1.2f, hi = v.RightScroll + view * 2.2f, R = CsRaster;
        foreach (var t in v.Thumbs)
        {
            bool near = t.Y + CshThumbH >= lo && t.Y <= hi;
            if (near && t.Ref == null)
            {
                t.Ref = Images.Acquire(t.Shot.Path, (int)Math.Ceiling(CshThumbW * R), (int)Math.Ceiling(CshThumbH * R));
                if (t.Ref != null)
                {
                    var b = G.C.CreateSurfaceBrush(t.Ref);
                    b.Stretch = CompositionStretch.UniformToFill;
                    t.Img.Brush = b;
                }
            }
            else if (!near && t.Ref != null && (t.Y > hi + view * 2 || t.Y + CshThumbH < lo - view * 2))
            {
                Images.Release(t.Ref);
                t.Ref = null;
                t.Img.Brush = CkSolid(Color.FromArgb(255, 0xDE, 0xDF, 0xE5));
            }
        }
    }

    /// <summary>右列空着时中间一行灰字（还没读完 / 没有截图 / 没有收藏）。</summary>
    private void CshPaintEmpty(CshView v)
    {
        string? text = !_shLoaded && _shUnits.Count == 0 ? "正在读取…" : _shUnits.Count > 0 ? null : _shFilter == "fav" ? "还没有收藏的截图" : "还没有截图";
        v.Empty.IsVisible = text != null;
        if (text == null) return;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.EmptyS, v.Empty, CpListX, CpTop, CpListW, CpBottom - CpTop));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateScale(CsRaster);
        using var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 34, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        ds.DrawText(text, new Rect(0, 0, CpListW, CpBottom - CpTop), Ck(Color.FromArgb(255, 0x7D, 0x80, 0x8A)), f);
    }

    /// <summary>滚到焦点露出来，焦点框过去（左列 = 选中的那一行，网格 = 焦点那张）。手指拖过之后先不自动滚。</summary>
    private void CshScrollAndFocus(CshView v, bool move)
    {
        float view = CpBottom - CpTop;
        // 左列
        int sel = Math.Max(0, _cshItems.FindIndex(i => i.Key == _shFilter));
        float lmax = Math.Max(0, _cshItems.Count * CshSlot + 20 - view);
        float lt = Math.Clamp(v.LeftScroll, 0, lmax);
        if (_cshZone == 0 && !v.Manual)
        {
            float top = sel * CshSlot - 40, bottom = (sel + 1) * CshSlot + 40;
            if (bottom > lt + view) lt = bottom - view;
            if (top < lt) lt = top;
            lt = Math.Clamp(lt, 0, lmax);
        }
        var lto = new Vector3(CshLeftX, CpTop - lt, 0);
        if (!v.LeftInit || Reduced) { v.LeftInit = true; RwJump(v.LeftList, lto); }
        else if (MathF.Abs(lt - v.LeftScroll) > 0.5f) v.LeftList.StartAnimation("Offset", RwSpring(lto));
        v.LeftScroll = lt;

        // 右列
        float rmax = Math.Max(0, v.RightH - view + 40);
        float rt = Math.Clamp(v.RightScroll, 0, rmax);
        CshThumb? ft = _cshZone == 1 && _shFocus >= 0 && _shFocus < v.Thumbs.Count ? v.Thumbs[_shFocus] : null;
        if (ft != null && !v.Manual)
        {
            float top = ft.Y - 60 - (ft.Y < CshDateH + 1 || v.Dates.Any(d => MathF.Abs(d.V.Offset.Y + CshDateH - ft.Y) < 1) ? CshDateH : 0), bottom = ft.Y + CshThumbH + 60;
            if (bottom > rt + view) rt = bottom - view;
            if (top < rt) rt = top;
            rt = Math.Clamp(rt, 0, rmax);
        }
        var rto = new Vector3(CpListX, CpTop - rt, 0);
        if (!v.RightInit || Reduced) { v.RightInit = true; RwJump(v.RightList, rto); }
        else if (MathF.Abs(rt - v.RightScroll) > 0.5f) v.RightList.StartAnimation("Offset", RwSpring(rto));
        v.RightScroll = rt;

        bool first = !v.Cur.Shown;
        if (_cshZone == 1 && ft != null) CurToCocoon(v.Cur, CpListX + ft.X, CpTop + ft.Y - rt, CshThumbW, CshThumbH, 24, move && !first);
        else if (_cshItems.Count > 0) CurToCocoon(v.Cur, CshLeftX, CpTop + sel * CshSlot + 4 - lt, CshLeftW, CshSlot - 8, 30, move && !first);
        else { if (v.Cur.Shown) CurShow(v.Cur, false, 60); return; }
        if (first) CurShow(v.Cur, true, 140, 60);
    }

    // ---------------- 看大图 ----------------

    private void CshShowViewer(bool on)
    {
        if (_csh is not { } v) return;
        if (on)
        {
            v.Viewer.IsVisible = true;
            v.Viewer.StopAnimation("Opacity");
            v.Viewer.Opacity = 1;
            v.VPath = null;
            CshPaintViewer();
            // 从缩略图放大出来（同游戏菜单的封面飞出）：图片精灵从缩略图的位置和大小走到整屏，黑底淡入
            if (!Reduced && _shFocus >= 0 && _shFocus < v.Thumbs.Count)
            {
                var t = v.Thumbs[_shFocus];
                var from = new Vector3(CpListX + t.X, CpTop + t.Y - v.RightScroll, 0);
                v.VImg.Offset = from; v.VImg.Size = new Vector2(CshThumbW, CshThumbH);
                v.VImg.StartAnimation("Offset", CsV3(Vector3.Zero, 260, new Bezier(0.22f, 1f, 0.36f, 1f)));
                var sz = G.C.CreateVector2KeyFrameAnimation();
                sz.InsertKeyFrame(1, new Vector2(CsW, CsH), G.Ease(new Bezier(0.22f, 1f, 0.36f, 1f)));
                sz.Duration = TimeSpan.FromMilliseconds(260);
                v.VImg.StartAnimation("Size", sz);
                v.VBlack.Opacity = 0;
                v.VBlack.StartAnimation("Opacity", CsS(1, 200, Bezier.EaseOut));
                foreach (var x in new Visual[] { v.VPill, v.VIcon, v.VStrip, v.VShade }) { x.Opacity = 0; x.StartAnimation("Opacity", CsS(1, 160, Bezier.EaseOut, 160)); }
            }
            Cue(Sfx.DetailIn);
        }
        else
        {
            CshCloseMenu(instant: true);
            if (Reduced) { v.Viewer.IsVisible = false; CshReleaseViewer(v); }
            else
            {
                v.Viewer.StartAnimation("Opacity", CsS(0, 150, Bezier.EaseOut));
                After(170, () => { if (!_shViewer && _csh == v) { v.Viewer.IsVisible = false; v.Viewer.Opacity = 1; CshReleaseViewer(v); } });
            }
            Cue(Sfx.Back);
            CshRefresh();
        }
    }

    private void CshReleaseViewer(CshView v)
    {
        Images.Release(v.VRef); v.VRef = null;
        Images.Release(v.VIconRef); v.VIconRef = null;
        foreach (var (_, r) in v.Strip) Images.Release(r);
        v.Strip.Clear();
        v.VStrip.Children.RemoveAll();
        v.VPath = null;
    }

    /// <summary>大图：图片铺满、底部信息胶囊、缩略条（当前那张亮、套焦点框），X 藏起胶囊和缩略条。</summary>
    private void CshPaintViewer()
    {
        if (_csh is not { } v || !_shViewer || ShViewerShot() is not { } c) return;
        float R = CsRaster;
        if (v.VPath != c.Path)
        {
            v.VPath = c.Path;
            var old = v.VRef;
            v.VRef = Images.Acquire(c.Path, (int)Math.Ceiling(CsW * R), (int)Math.Ceiling(CsH * R));
            if (v.VRef != null)
            {
                var b = G.C.CreateSurfaceBrush(v.VRef);
                b.Stretch = CompositionStretch.Uniform;
                v.VImg.Brush = b;
            }
            Images.Release(old);
            v.VImg.StopAnimation("Offset"); v.VImg.StopAnimation("Size");
            v.VImg.Offset = Vector3.Zero; v.VImg.Size = new Vector2(CsW, CsH);
            CshPaintPill(v, c);
            CshPaintStrip(v);
        }
        float op = _shViewerChrome ? 1 : 0;
        foreach (var x in new Visual[] { v.VPill, v.VIcon, v.VStrip, v.VShade })
        {
            if (Reduced) { x.StopAnimation("Opacity"); x.Opacity = op; }
            else if (MathF.Abs(x.Opacity - op) > 0.01f) x.StartAnimation("Opacity", CsS(op, 160, Bezier.EaseOut));
        }
        if (_shViewerChrome)
        {
            bool first = !v.VCur.Shown;
            CurToCocoon(v.VCur, CsW / 2 - CshStripW / 2, CshStripY, CshStripW, CshStripH, 14, !first);
            if (first) CurShow(v.VCur, true, 140, 120);
        }
        else if (v.VCur.Shown) CurShow(v.VCur, false, 80);
    }

    /// <summary>
    /// 大图底部的信息（11-27 jinyu：白色胶囊太碍眼）：不放底框，直接压在底部的暗渐变上——左下角游戏小图标、白色游戏名、
    /// 下面一行淡白的时间 · 来源；右边同一行淡白的「第几张 / 共几张」。字带一点投影，亮图上也看得清。
    /// </summary>
    private void CshPaintPill(CshView v, Capture c)
    {
        string title = c.Game, sub = $"{c.Time.Month} 月 {c.Time.Day} 日 {c.Time:HH:mm} · {c.Source}", pos = $"{_shVIndex + 1} / {ShFlat.Count}";
        float x = 0, y = CshPillY, w = CsW, h = CshPillH, ix = 64, iconS = 64, tx = ix + iconS + 24;
        using (var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.VPillS, v.VPill, x, y, w, h)))
        {
            ds.Clear(Colors.Transparent);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            using var cl = new CanvasCommandList(ds);
            using (var s = cl.CreateDrawingSession())
            {
                s.Transform = Matrix3x2.CreateScale(CsRaster);
                using (var f = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 36, FontWeight = RsWt(title, 600), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                    s.DrawText(title, new Rect(tx, 14, 1200, 50), Color.FromArgb(255, 0xFA, 0xFA, 0xFA), f);
                using (var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 27, WordWrapping = CanvasWordWrapping.NoWrap })
                    s.DrawText(sub, new Rect(tx, 62, 1200, 40), Color.FromArgb(200, 0xFA, 0xFA, 0xFA), f);
                using (var f = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 28, FontWeight = new Windows.UI.Text.FontWeight(600), HorizontalAlignment = CanvasHorizontalAlignment.Right, WordWrapping = CanvasWordWrapping.NoWrap })
                    s.DrawText(pos, new Rect(w - 64 - 400, 22, 400, 44), Color.FromArgb(200, 0xFA, 0xFA, 0xFA), f);
            }
            using (var sh = new Microsoft.Graphics.Canvas.Effects.ShadowEffect { Source = cl, BlurAmount = 5 * CsRaster, ShadowColor = Color.FromArgb(150, 0, 0, 0) })
                ds.DrawImage(sh, 0, 2 * CsRaster);
            ds.DrawImage(cl);
        }
        // 游戏图标：游戏库封面（没有归到游戏的不放）
        Images.Release(v.VIconRef); v.VIconRef = null;
        v.VIcon.IsVisible = false;
        if (c.GameId != null && _lib.ById.TryGetValue(c.GameId, out var item) && RqArt(item) is { } art)
        {
            float R = CsRaster;
            v.VIconRef = Images.Acquire(art, (int)Math.Ceiling(iconS * R), (int)Math.Ceiling(iconS * 1.5f * R));
            if (v.VIconRef != null) { var b = G.C.CreateSurfaceBrush(v.VIconRef); b.Stretch = CompositionStretch.UniformToFill; v.VIcon.Brush = b; v.VIcon.IsVisible = true; }
        }
        v.VIcon.Size = new Vector2(iconS, iconS);
        v.VIcon.Offset = new Vector3(ix, y + 22, 0);
        var rr = G.C.CreateRoundedRectangleGeometry();
        rr.Size = new Vector2(iconS, iconS);
        rr.CornerRadius = new Vector2(16, 16);
        v.VIcon.Clip = G.C.CreateGeometricClip(rr);
        if (!v.VIcon.IsVisible)
        {
            // 没有图标时字往左靠（重画一次，起点挪到图标的位置）
            using var ds = CanvasComposition.CreateDrawingSession(v.VPillS!);
            ds.Clear(Colors.Transparent);
            using var cl = new CanvasCommandList(ds);
            using (var s = cl.CreateDrawingSession())
            {
                s.Transform = Matrix3x2.CreateScale(CsRaster);
                using (var f = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 36, FontWeight = RsWt(title, 600), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                    s.DrawText(title, new Rect(ix, 14, 1300, 50), Color.FromArgb(255, 0xFA, 0xFA, 0xFA), f);
                using (var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 27, WordWrapping = CanvasWordWrapping.NoWrap })
                    s.DrawText(sub, new Rect(ix, 62, 1300, 40), Color.FromArgb(200, 0xFA, 0xFA, 0xFA), f);
                using (var f = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 28, FontWeight = new Windows.UI.Text.FontWeight(600), HorizontalAlignment = CanvasHorizontalAlignment.Right, WordWrapping = CanvasWordWrapping.NoWrap })
                    s.DrawText(pos, new Rect(w - 64 - 400, 22, 400, 44), Color.FromArgb(200, 0xFA, 0xFA, 0xFA), f);
            }
            using (var sh = new Microsoft.Graphics.Canvas.Effects.ShadowEffect { Source = cl, BlurAmount = 5 * CsRaster, ShadowColor = Color.FromArgb(150, 0, 0, 0) })
                ds.DrawImage(sh, 0, 2 * CsRaster);
            ds.DrawImage(cl);
        }
    }

    /// <summary>缩略条：当前这张居中，左右各最多四张，两边的淡一些。</summary>
    private void CshPaintStrip(CshView v)
    {
        foreach (var (_, r) in v.Strip) Images.Release(r);
        v.Strip.Clear();
        v.VStrip.Children.RemoveAll();
        var flat = ShFlat;
        float R = CsRaster;
        for (int d = -4; d <= 4; d++)
        {
            int i = _shVIndex + d;
            if (i < 0 || i >= flat.Count) continue;
            var sp = G.Sprite(G.Solid(Color.FromArgb(255, 0x3A, 0x3B, 0x42)), relative: false);
            var r = Images.Acquire(flat[i].Path, (int)Math.Ceiling(CshStripW * R), (int)Math.Ceiling(CshStripH * R));
            if (r != null) { var b = G.C.CreateSurfaceBrush(r); b.Stretch = CompositionStretch.UniformToFill; sp.Brush = b; }
            sp.Size = new Vector2(CshStripW, CshStripH);
            sp.Offset = new Vector3(CsW / 2 - CshStripW / 2 + d * (CshStripW + CshStripGap), CshStripY, 0);
            var rr = G.C.CreateRoundedRectangleGeometry();
            rr.Size = new Vector2(CshStripW, CshStripH);
            rr.CornerRadius = new Vector2(14, 14);
            sp.Clip = G.C.CreateGeometricClip(rr);
            sp.Opacity = d == 0 ? 1 : 0.55f;
            v.VStrip.Children.InsertAtTop(sp);
            v.Strip.Add((sp, r));
        }
    }

    // ---------------- Y 菜单 ----------------

    private string[] CshMenuLabels => new[] { _cshTarget != null && _shFavs.Contains(_cshTarget.Path) ? "取消收藏" : "收藏", "复制到剪贴板", "在文件夹中显示", "删除" };

    private void CshOpenMenu(Capture c)
    {
        if (_csh is not { } v) return;
        _cshTarget = c;
        _cshMenuOpen = true;
        _cshMenuSel = 0;
        Cue(Sfx.ModalShow);
        float R = CsRaster, pw = 620, ph = pw * 9 / 16, px = 260, py = 600 - ph / 2 - 30;
        Images.Release(v.MRef);
        v.MRef = Images.Acquire(c.Path, (int)Math.Ceiling(pw * R), (int)Math.Ceiling(ph * R));
        v.MImg.Brush = G.Solid(Color.FromArgb(255, 0x3A, 0x3B, 0x42));
        if (v.MRef != null) { var b = G.C.CreateSurfaceBrush(v.MRef); b.Stretch = CompositionStretch.UniformToFill; v.MImg.Brush = b; }
        v.MImg.Size = new Vector2(pw, ph);
        v.MImg.Offset = new Vector3(px, py, 0);
        var rr = G.C.CreateRoundedRectangleGeometry();
        rr.Size = new Vector2(pw, ph);
        rr.CornerRadius = new Vector2(24, 24);
        v.MImg.Clip = G.C.CreateGeometricClip(rr);
        using (var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.MNameS, v.MName, px - 100, py + ph + 20, pw + 200, 70)))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            using var f = new CanvasTextFormat { FontFamily = RmFont(c.Game), FontSize = 40, FontWeight = RsWt(c.Game), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(c.Game, new Rect(0, 0, pw + 200, 70), Color.FromArgb(255, 0xFA, 0xFA, 0xFA), f);
        }
        CshPaintMenuCard(v);
        v.Menu.IsVisible = true;
        v.MCur.Shown = false;
        CshMenuFocus(v);
        if (!Reduced)
        {
            v.MScrim.Opacity = 0; v.MScrim.StartAnimation("Opacity", CsS(1, 200, Bezier.EaseOut));
            foreach (var x in new Visual[] { v.MImg, v.MName }) { x.Opacity = 0; x.StartAnimation("Opacity", CsS(1, 160, Bezier.EaseOut, 40)); }
            v.MCard.Opacity = 0; v.MCard.Scale = new Vector3(0.94f, 0.94f, 1);
            v.MCard.CenterPoint = new Vector3(v.MCard.Size.X / 2, v.MCard.Size.Y / 2, 0);
            v.MCard.StartAnimation("Opacity", CsS(1, 90, Bezier.EaseOut, 40));
            v.MCard.StartAnimation("Scale", CsV3(Vector3.One, 180, new Bezier(0.22f, 1f, 0.36f, 1f), 40));
        }
    }

    private float CshMenuY => (CsH - 4 * CshMenuRowH) / 2;

    private void CshPaintMenuCard(CshView v)
    {
        var labels = CshMenuLabels;
        float x = CshMenuX, y = CshMenuY, w = CshMenuW, h = labels.Length * CshMenuRowH, pad = 40;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.MCardS, v.MCard, x - pad, y - pad, w + pad * 2, h + pad * 2));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(pad - x, pad - y) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        if (CocoonPalette.Dark) DkPop(ds, x, y, w, h, 30);
        else { RsSoftShadow(ds, x, y + 14, w, h, 30, 24, 0.35f); ds.FillRoundedRectangle(x, y, w, h, 30, 30, Color.FromArgb(255, 0xFA, 0xFA, 0xFA)); }
        for (int i = 0; i < labels.Length; i++)
        {
            float ry = y + i * CshMenuRowH, cy = ry + CshMenuRowH / 2;
            if (i > 0) ds.FillRectangle(x, ry - 1.5f, w, 3, Ck(Color.FromArgb(255, 0xEC, 0xEE, 0xF2)));
            bool danger = i == labels.Length - 1;
            CshMenuIcon(ds, i, x + 66, cy, danger);
            using (var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 40, FontWeight = CpTitleWt, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(labels[i], new Rect(x + 120, ry, w - 220, CshMenuRowH), danger ? RmRed : RmInk, f);
            if (i == 0)
            {
                // RB 圆章：网格和大图里按 RB 直接收藏
                float bx = x + w - 92, bw = 56, bh = 42;
                ds.FillRoundedRectangle(bx, cy - bh / 2, bw, bh, 12, 12, Ck(Color.FromArgb(255, 0x8E, 0x90, 0x98)));
                using var bf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 21, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
                ds.DrawText("RB", new Rect(bx, cy - bh / 2, bw, bh), Color.FromArgb(255, 0xF2, 0xF2, 0xF4), bf);
            }
        }
    }

    /// <summary>菜单行的贴纸图标（主题色渐变 + 白描边；删除是红色），同游戏菜单 RmIcon。</summary>
    private void CshMenuIcon(CanvasDrawingSession ds, int i, float cx, float cy, bool danger)
    {
        var dev = ds.Device;
        using var fill = danger ? new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xFF, 0x7A, 0x84), RmRed) { StartPoint = new Vector2(0, cy - 22), EndPoint = new Vector2(0, cy + 22) }
                                : new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(0, cy - 22), EndPoint = new Vector2(0, cy + 22) };
        CanvasGeometry geo;
        switch (i)
        {
            case 0: geo = CshStar(dev, cx, cy + 1, 22); break;
            case 1:
            {
                using var back = CanvasGeometry.CreateRoundedRectangle(dev, cx - 19, cy - 21, 26, 32, 5, 5);
                using var front = CanvasGeometry.CreateRoundedRectangle(dev, cx - 7, cy - 11, 26, 32, 5, 5);
                geo = back.CombineWith(front, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                break;
            }
            case 2:
            {
                using var body = CanvasGeometry.CreateRoundedRectangle(dev, cx - 21, cy - 10, 42, 28, 6, 6);
                using var tab = CanvasGeometry.CreateRoundedRectangle(dev, cx - 21, cy - 17, 19, 12, 4, 4);
                geo = body.CombineWith(tab, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                break;
            }
            default:
            {
                using var can = CanvasGeometry.CreateRoundedRectangle(dev, cx - 14, cy - 9, 28, 29, 5, 5);
                using var lid = CanvasGeometry.CreateRoundedRectangle(dev, cx - 19, cy - 16, 38, 6, 3, 3);
                using var handle = CanvasGeometry.CreateRoundedRectangle(dev, cx - 6, cy - 21, 12, 7, 3, 3);
                using var a = can.CombineWith(lid, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                geo = a.CombineWith(handle, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                break;
            }
        }
        using (geo)
        {
            RwSticker(ds, geo, 9);
            ds.FillGeometry(geo, fill);
            if (i == 3)
                for (int k = -1; k <= 1; k++) ds.FillRoundedRectangle(cx + k * 7 - 1.5f, cy - 3, 3, 17, 1.5f, 1.5f, Color.FromArgb(160, 255, 255, 255));
        }
    }

    private void CshMenuFocus(CshView v)
    {
        bool first = !v.MCur.Shown;
        CurToCocoon(v.MCur, CshMenuX, CshMenuY + _cshMenuSel * CshMenuRowH, CshMenuW, CshMenuRowH, 30, !first);
        if (first) CurShow(v.MCur, true, 120, 120);
    }

    private void CshCloseMenu(bool instant = false)
    {
        if (!_cshMenuOpen || _csh is not { } v) return;
        _cshMenuOpen = false;
        if (!instant) Cue(Sfx.Back);
        void Drop() { if (_cshMenuOpen) return; v.Menu.IsVisible = false; v.Menu.Opacity = 1; Images.Release(v.MRef); v.MRef = null; }
        if (instant || Reduced) { Drop(); return; }
        v.Menu.StartAnimation("Opacity", CsS(0, 110, Bezier.EaseOut));
        After(130, Drop);
    }

    private void CshRunMenu()
    {
        if (_cshTarget is not { } c) return;
        int sel = _cshMenuSel;
        CshCloseMenu(instant: true);
        switch (sel)
        {
            case 0: Cue(Sfx.Activate); ShToggleFav(c); break;
            case 1: Cue(Sfx.Activate); _ = ShCopy(c); break;
            case 2: Cue(Sfx.Activate); SystemInfo.Open("explorer.exe", $"/select,\"{c.Path}\""); Toast("正在打开文件夹"); break;
            default: CshOpenDialog(c); break;
        }
    }

    // ---------------- 删除确认 ----------------

    private void CshOpenDialog(Capture c)
    {
        if (_csh is not { } v) return;
        _cshTarget = c;
        _cshDlgOpen = true;
        Cue(Sfx.ModalShow);
        float x0 = (CsW - CshDlgW) / 2, y0 = (CsH - CshDlgH) / 2, pad = 40, R = CsRaster;
        string note = c.CloudId != null ? "只从这里移除，Steam 上的还在。" : "移到回收站，可以从回收站还原。";
        using (var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.DCardS, v.DCard, x0 - pad, y0 - pad, CshDlgW + pad * 2, CshDlgH + pad * 2)))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(R);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            RsSoftShadow(ds, 0, 12, CshDlgW, CshDlgH, 60, 22, 0.35f);
            ds.FillRoundedRectangle(0, 0, CshDlgW, CshDlgH, 60, 60, RsBg);
            if (CocoonPalette.Dark) DkEdge(ds, 0, 0, CshDlgW, CshDlgH, 60);
            RqHeaderPill(ds, 37, 37);
            using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 48, FontWeight = RsWt("删除截图"), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText("删除截图", new Rect(192, 37, 680, 74), RmInk, tf);
            RsSoftShadow(ds, (CshDlgW - 560) / 2, 148 + 8, 560, 315, 24, 12, 0.22f);
            using (var nf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 34, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
                ds.DrawText(note, new Rect(0, 490, CshDlgW, 60), RmInk, nf);
            float bw = (CshDlgW - 74 - 38) / 2, by = CshDlgH - 40 - RqBtnH;
            RqButton(ds, 37, by, bw, "B", "取消", green: false, busy: false);
            RqButton(ds, 37 + bw + 38, by, bw, "A", "删除", green: true, busy: false, red: true);
        }
        Images.Release(v.DRef);
        v.DRef = Images.Acquire(c.Path, (int)Math.Ceiling(560 * R), (int)Math.Ceiling(315 * R));
        v.DImg.Brush = CkSolid(Color.FromArgb(255, 0xD8, 0xD9, 0xE0));
        if (v.DRef != null) { var b = G.C.CreateSurfaceBrush(v.DRef); b.Stretch = CompositionStretch.UniformToFill; v.DImg.Brush = b; }
        v.DImg.Size = new Vector2(560, 315);
        v.DImg.Offset = new Vector3(x0 + (CshDlgW - 560) / 2, y0 + 148, 0);
        var rr = G.C.CreateRoundedRectangleGeometry();
        rr.Size = new Vector2(560, 315);
        rr.CornerRadius = new Vector2(24, 24);
        v.DImg.Clip = G.C.CreateGeometricClip(rr);
        v.Dlg.IsVisible = true;
        if (!Reduced)
        {
            v.DScrim.Opacity = 0; v.DScrim.StartAnimation("Opacity", CsS(1, 200, Bezier.EaseOut));
            foreach (var x in new SpriteVisual[] { v.DCard, v.DImg })
            {
                x.Opacity = 0; x.Scale = new Vector3(0.94f, 0.94f, 1);
                x.CenterPoint = new Vector3(CsW / 2 - x.Offset.X, CsH / 2 - x.Offset.Y, 0);
                x.StartAnimation("Opacity", CsS(1, 80, Bezier.EaseOut));
                x.StartAnimation("Scale", CsV3(Vector3.One, 160, new Bezier(0.22f, 1f, 0.36f, 1f)));
            }
        }
    }

    private void CshCloseDialog(bool instant = false)
    {
        if (!_cshDlgOpen || _csh is not { } v) return;
        _cshDlgOpen = false;
        void Drop() { if (_cshDlgOpen) return; v.Dlg.IsVisible = false; v.Dlg.Opacity = 1; Images.Release(v.DRef); v.DRef = null; }
        if (instant || Reduced) { Drop(); return; }
        v.Dlg.StartAnimation("Opacity", CsS(0, 90, Bezier.EaseOut));
        After(110, Drop);
    }

    private void CshConfirmDelete()
    {
        if (_cshTarget is not { } c) return;
        Cue(Sfx.Activate);
        CshCloseDialog(instant: true);
        int i = ShFlat.IndexOf(c);
        if (i >= 0) _shVIndex = i;
        ShDelete(c);
        if (_csh is { } v) { v.VPath = null; if (_shViewer) CshPaintViewer(); }
    }

    // ---------------- 输入（Shots.cs 的入口在 Cocoon 主题下转到这里） ----------------

    private Capture? CshCurrent => _shViewer ? ShViewerShot() : _cshZone == 1 && _shFocus >= 0 && _shFocus < _shUnits.Count ? _shUnits[_shFocus].Top : null;

    private void CshMove(Direction dir)
    {
        if (_csh is not { } v) return;
        v.Manual = false;
        bool horiz = dir is Direction.Left or Direction.Right;
        int d = dir is Direction.Up or Direction.Left ? -1 : 1;
        if (_cshDlgOpen) { Cue(Sfx.Bump); return; }
        if (_cshMenuOpen)
        {
            if (horiz) { Cue(Sfx.Bump); return; }
            int to = _cshMenuSel + d;
            if (to < 0 || to >= 4) { Cue(Sfx.Bump); return; }
            _cshMenuSel = to; CuePan(0.5f); CshMenuFocus(v);
            return;
        }
        if (_shViewer)
        {
            if (!horiz) { if (!_shViewerChrome) { _shViewerChrome = true; CshPaintViewer(); } else Cue(Sfx.Bump); return; }
            int to = _shVIndex + d;
            if (to < 0 || to >= ShFlat.Count) { Cue(Sfx.Bump); return; }
            _shVIndex = to; CuePan(d < 0 ? 0.3f : 0.7f);
            CshPaintViewer();
            return;
        }
        if (_cshZone == 0)
        {
            if (dir == Direction.Right) { CshEnterGrid(); return; }
            if (horiz) { Cue(Sfx.Bump); return; }
            int cur = Math.Max(0, _cshItems.FindIndex(i => i.Key == _shFilter)), to = cur + d;
            if (to < 0 || to >= _cshItems.Count) { Cue(Sfx.Bump); return; }
            CuePan(0.5f);
            ShSetFilter(_cshItems[to].Key);
            return;
        }
        // 网格
        int n = v.Thumbs.Count, f = Math.Clamp(_shFocus, 0, Math.Max(0, n - 1));
        if (n == 0) { _cshZone = 0; CshRefresh(); return; }
        int next = f;
        if (horiz)
        {
            var t = v.Thumbs[f];
            if (dir == Direction.Left && t.X < 1) { Cue(Sfx.Back); _cshZone = 0; CshScrollAndFocus(v, true); return; }
            next = f + d;
        }
        else
        {
            // 上 / 下一排里横向离得最近的一张（跨日期分组）
            var cur = v.Thumbs[f];
            float bestY = d < 0 ? float.MinValue : float.MaxValue;
            foreach (var t in v.Thumbs) if (d < 0 ? t.Y < cur.Y - 1 && t.Y > bestY : t.Y > cur.Y + 1 && t.Y < bestY) bestY = t.Y;
            if (bestY is float.MinValue or float.MaxValue) { Cue(Sfx.Bump); return; }
            float bestDx = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (MathF.Abs(v.Thumbs[i].Y - bestY) < 1 && MathF.Abs(v.Thumbs[i].X - cur.X) < bestDx) { bestDx = MathF.Abs(v.Thumbs[i].X - cur.X); next = i; }
        }
        if (next < 0 || next >= n || next == f) { Cue(Sfx.Bump); return; }
        _shFocus = next;
        CuePan(0.5f);
        CshScrollAndFocus(v, true);
        CshEnsureImages(v);
    }

    private void CshEnterGrid()
    {
        if (_csh is not { } v || v.Thumbs.Count == 0) { Cue(Sfx.Bump); return; }
        Cue(Sfx.Nav);
        _cshZone = 1;
        // 回到这一列的第一张（Cocoon：进右列焦点落第一行）
        _shFocus = 0;
        v.Manual = false;
        CshScrollAndFocus(v, true);
        CshEnsureImages(v);
    }

    private void CshActivate()
    {
        if (_cshDlgOpen) { CshConfirmDelete(); return; }
        if (_cshMenuOpen) { CshRunMenu(); return; }
        if (_shViewer) { if (!_shViewerChrome) { _shViewerChrome = true; CshPaintViewer(); return; } if (ShViewerShot() is { } vc) CshOpenMenu(vc); return; }
        if (_cshZone == 0) { CshEnterGrid(); return; }
        if (_shUnits.Count == 0) { Cue(Sfx.Bump); return; }
        ShOpenViewer(ShFlat.IndexOf(_shUnits[Math.Clamp(_shFocus, 0, _shUnits.Count - 1)].Top));
    }

    /// <summary>B：弹窗 → 菜单 → 大图 → 网格回左列；左列里返回 false（交给原来的关页面）。</summary>
    private bool CshBack()
    {
        if (_cshDlgOpen) { Cue(Sfx.Back); CshCloseDialog(); return true; }
        if (_cshMenuOpen) { CshCloseMenu(); return true; }
        if (_shViewer) { ShCloseViewer(); return true; }
        if (_cshZone == 1 && _csh is { } v) { Cue(Sfx.Back); _cshZone = 0; v.Manual = false; CshScrollAndFocus(v, true); return true; }
        return false;
    }

    private void CshY()
    {
        if (_cshDlgOpen || _cshMenuOpen) { Cue(Sfx.Bump); return; }
        if (CshCurrent is { } c) CshOpenMenu(c); else Cue(Sfx.Bump);
    }

    /// <summary>RB：直接收藏 / 取消收藏（网格和大图里）。</summary>
    private void CshFavKey()
    {
        if (_cshDlgOpen || _cshMenuOpen || CshCurrent is not { } c) { Cue(Sfx.Bump); return; }
        Cue(Sfx.Activate);
        ShToggleFav(c);
    }

    private void CshX()
    {
        if (!_shViewer || _cshMenuOpen || _cshDlgOpen) { Cue(Sfx.Bump); return; }
        _shViewerChrome = !_shViewerChrome;
        CshPaintViewer();
    }

    /// <summary>点（设计单位）：弹窗按钮 / 菜单行 / 大图胶囊和缩略条 / 左上返回 / 左列 / 缩略图。</summary>
    private void CshClick(float x, float y)
    {
        if (_csh is not { } v) return;
        if (_cshDlgOpen)
        {
            float x0 = (CsW - CshDlgW) / 2, y0 = (CsH - CshDlgH) / 2, bw = (CshDlgW - 74 - 38) / 2, by = y0 + CshDlgH - 40 - RqBtnH;
            if (x < x0 || x > x0 + CshDlgW || y < y0 || y > y0 + CshDlgH) { Cue(Sfx.Back); CshCloseDialog(); return; }
            if (y >= by && y <= by + RqBtnH)
            {
                if (x <= x0 + 37 + bw) { Cue(Sfx.Back); CshCloseDialog(); }
                else CshConfirmDelete();
            }
            return;
        }
        if (_cshMenuOpen)
        {
            if (x >= CshMenuX && x <= CshMenuX + CshMenuW && y >= CshMenuY && y <= CshMenuY + 4 * CshMenuRowH)
            {
                _cshMenuSel = (int)((y - CshMenuY) / CshMenuRowH);
                CshRunMenu();
            }
            else CshCloseMenu();
            return;
        }
        if (_shViewer)
        {
            if (_shViewerChrome && y >= CshStripY && y <= CshStripY + CshStripH)
            {
                int d = (int)MathF.Round((x - CsW / 2) / (CshStripW + CshStripGap));
                int to = Math.Clamp(_shVIndex + d, 0, ShFlat.Count - 1);
                if (to != _shVIndex) { _shVIndex = to; CuePan(0.5f); CshPaintViewer(); }
                return;
            }
            if (_shViewerChrome && y >= CshPillY && y <= CshPillY + CshPillH && v.VPill.Size.X > 0 && x >= v.VPill.Offset.X && x <= v.VPill.Offset.X + v.VPill.Size.X)
            {
                if (ShViewerShot() is { } vc) CshOpenMenu(vc);
                return;
            }
            _shViewerChrome = !_shViewerChrome;
            CshPaintViewer();
            return;
        }
        if (x <= 180 && y <= 120) { ShBack(); return; }
        if (x >= CshLeftX && x <= CshLeftX + CshLeftW && y >= CpTop && y <= CpBottom)
        {
            int i = (int)((y - CpTop + v.LeftScroll) / CshSlot);
            if (i < 0 || i >= _cshItems.Count) return;
            _cshZone = 0;
            v.Manual = false;
            if (_cshItems[i].Key != _shFilter) { CuePan(0.5f); ShSetFilter(_cshItems[i].Key); }
            else CshScrollAndFocus(v, true);
            return;
        }
        if (x >= CpListX && x <= CpListX + CpListW && y <= CpBottom)
        {
            float lx = x - CpListX, ly = y - CpTop + v.RightScroll;
            for (int i = 0; i < v.Thumbs.Count; i++)
            {
                var t = v.Thumbs[i];
                if (lx < t.X || lx > t.X + CshThumbW || ly < t.Y || ly > t.Y + CshThumbH) continue;
                _cshZone = 1;
                _shFocus = i;
                v.Manual = true;
                CshScrollAndFocus(v, true);
                ShOpenViewer(ShFlat.IndexOf(t.Shot));
                return;
            }
        }
    }

    /// <summary>手指拖 / 滚轮：按起点在哪一列滚那一列（设计单位）。拖过以后焦点不再把列表拽回去，直到下一次用手柄走。</summary>
    private void CshDrag(float dyCc)
    {
        if (_csh is not { } v || _shViewer || _cshMenuOpen || _cshDlgOpen) return;
        float dy = dyCc * 1.5f, view = CpBottom - CpTop;
        bool left = _dragFrom.x / _ccK * 1.5 < CpListX - 30;
        v.Manual = true;
        if (left)
        {
            float max = Math.Max(0, _cshItems.Count * CshSlot + 20 - view);
            v.LeftScroll = Math.Clamp(v.LeftScroll - dy, 0, max);
            RwJump(v.LeftList, new Vector3(CshLeftX, CpTop - v.LeftScroll, 0));
        }
        else
        {
            float max = Math.Max(0, v.RightH - view + 40);
            v.RightScroll = Math.Clamp(v.RightScroll - dy, 0, max);
            RwJump(v.RightList, new Vector3(CpListX, CpTop - v.RightScroll, 0));
            CshEnsureImages(v);
        }
        // 焦点框跟着一起走（它在不滚的那一层）
        int sel = Math.Max(0, _cshItems.FindIndex(i => i.Key == _shFilter));
        if (_cshZone == 1 && _shFocus >= 0 && _shFocus < v.Thumbs.Count) { var t = v.Thumbs[_shFocus]; CurToCocoon(v.Cur, CpListX + t.X, CpTop + t.Y - v.RightScroll, CshThumbW, CshThumbH, 24, false); }
        else if (_cshItems.Count > 0) CurToCocoon(v.Cur, CshLeftX, CpTop + sel * CshSlot + 4 - v.LeftScroll, CshLeftW, CshSlot - 8, 30, false);
    }

    // ---------------- 提示（同 Cp 页面底部小胶囊） ----------------

    private bool CshToast(string text)
    {
        if (!CshActive || _csh is not { } v) return false;
        float R = CsRaster, h = 84, pad = 30;
        float tw;
        using (var f0 = new CanvasTextFormat { FontFamily = RmFont(text), FontSize = 30, FontWeight = RsWt(text, 500), WordWrapping = CanvasWordWrapping.NoWrap })
        using (var l0 = new CanvasTextLayout(G.Device, text, f0, 1400, 100)) tw = (float)l0.LayoutBounds.Width;
        float w = Math.Min(1500, tw + 130), x = CsW / 2 - w / 2, y = _shViewer ? 60 : CpBottom - h - 30;
        string kind = NoticeKind(text);
        using (var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.ToastS, v.Toast, x - pad, y - pad, w + pad * 2, h + pad * 2)))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - x, pad - y) * Matrix3x2.CreateScale(R);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            CpCard(ds, x, y, w, h, null, h / 2);
            var c1 = kind == "error" ? Color.FromArgb(255, 0xFF, 0x7A, 0x84) : kind == "warning" ? Color.FromArgb(255, 0xFF, 0xC1, 0x6B) : RwC1;
            var c2 = kind == "error" ? RmRed : kind == "warning" ? Color.FromArgb(255, 0xF0, 0x8A, 0x24) : RwC2;
            using (var br = new CanvasLinearGradientBrush(ds.Device, c1, c2) { StartPoint = new Vector2(x + 26, y + 22), EndPoint = new Vector2(x + 66, y + 62) })
                ds.FillCircle(x + 46, y + h / 2, 18, br);
            ds.FillCircle(x + 46, y + h / 2, 6, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
            using var f = new CanvasTextFormat { FontFamily = RmFont(text), FontSize = 30, FontWeight = RsWt(text, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(text, new Rect(x + 84, y, w - 110, h), RmInk, f);
        }
        int gen = ++v.ToastGen;
        var home = v.Toast.Offset;
        if (Reduced) v.Toast.Opacity = 1;
        else
        {
            v.Toast.Offset = home + new Vector3(0, 24, 0);
            v.Toast.StartAnimation("Offset", CsV3(home, 220, new Bezier(0.2f, 0.8f, 0.2f, 1f)));
            v.Toast.StartAnimation("Opacity", CsS(1, 160, Bezier.EaseOut));
        }
        After(2400, () => { if (v.ToastGen == gen) v.Toast.StartAnimation("Opacity", CsS(0, 220, Bezier.EaseOut)); });
        return true;
    }
}
