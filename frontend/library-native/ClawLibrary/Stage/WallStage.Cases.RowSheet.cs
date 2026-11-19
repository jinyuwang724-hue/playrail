// 「单行」样式的二级页（11-20 jinyu：游戏设置照 Cocoon 的结构做）。一张几乎铺满屏幕的白色大面板：
//   左上「B ←」返回胶囊 + 页面标题，右上紫色「Y ✓」保存胶囊；
//   下面一行一项：带 › 的进下一级、带开关的直接切换、「名称 / 当前值」两行式的条目按 A 编辑；
//   焦点是同一套四角括号（两端圆），上下移动弹簧滑过去；列表超出一屏时底部有向下的小箭头。
// 页面可以一层层压进去（编辑 › 编辑元数据），B 一层层退回；最外一层退回游戏菜单。
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum RsKind { Nav, Toggle, Field, Info, Action, Pick }

    private sealed class RsRow
    {
        public RsKind Kind;
        public string Icon = "";
        public required string Title;
        public string? Value;
        public Func<bool>? IsOn;
        public Action? Press;
        public string? Key;            // Field：元数据字段名
        public bool Multiline;
        public bool Danger;
        /// <summary>Pick / Toggle：左右键改值（Cocoon 的下拉框在手柄上用左右切）。</summary>
        public Action<int>? Step;
    }

    private sealed class RsPage
    {
        public required string Title;
        public string TitleIcon = "";
        public required Func<List<RsRow>> Build;
        public List<RsRow> Rows = new();
        public int Sel;
        public int Top;                // 第一行可见的是第几行
        public Action? Save;           // Y ✓：null = 没有要保存的（按钮仍显示，按了直接退）
        public Func<bool>? Dirty;
        /// <summary>自己画内容区的页（封面管理）：给了就不画列表、不显示行焦点框，输入也交给它。</summary>
        public Action<CanvasDrawingSession>? Paint;
        public Func<Direction, bool>? Move;
        public Action? Activate;
        public Action<int>? Tab;
        public Action<CanvasDrawingSession>? PaintHeaderExtra;
    }

    private sealed class RsState
    {
        public required ContainerVisual Root;
        public required SpriteVisual Scrim, Sheet, Glow, Line, Rim;
        public required LibraryItem Item;
        public required CsPage Page;
        public readonly List<RsPage> Stack = new();
        public CompositionDrawingSurface? SheetSurf, GlowSurf, LineSurf, RimSurf;
        public bool DiscardArmed;
    }

    private RsState? _rs;
    /// <summary>游戏配置页借用了游戏设置页的状态（_gsItem / _gsState），关掉时还回去。</summary>
    private bool _rsOwnsGs;
    private TextBox? _rsBox;
    private RsRow? _rsEditing;

    // 面板几何（设计单位 1920×1200）
    private const float RsX = 96, RsY = 70, RsW = 1728, RsH = 1060, RsR = 64;
    private const float RsListX = 116, RsListW = 1688, RsTop = 220, RsRowH = 136, RsSep = 3, RsBottom = 1100;
    private static int RsVisible => (int)((RsBottom - RsTop) / (RsRowH + RsSep));
    private static readonly Color RsBg = Color.FromArgb(255, 0xF7, 0xF8, 0xFA), RsLine = Color.FromArgb(255, 0xE6, 0xE8, 0xEC), RsSub = Color.FromArgb(255, 0x5F, 0x61, 0x68);

    // ---------------- 打开 / 关闭 / 压页 ----------------

    private void RsOpen(LibraryItem g, CsPage p, RsPage first)
    {
        if (_cs == null) return;
        RsCloseAll(instant: true);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var scrim = G.Sprite(G.Solid(Color.FromArgb(160, 8, 8, 14)), relative: false);
        try
        {
            var blur = new GaussianBlurEffect { BlurAmount = 28, BorderMode = EffectBorderMode.Hard, Source = new CompositionEffectSourceParameter("src") };
            var tint = new ArithmeticCompositeEffect { MultiplyAmount = 0, Source1Amount = 0.55f, Source2Amount = 0.45f, Source1 = blur, Source2 = new ColorSourceEffect { Color = Color.FromArgb(255, 10, 10, 16) } };
            var eb = G.C.CreateEffectFactory(tint).CreateBrush();
            eb.SetSourceParameter("src", G.C.CreateBackdropBrush());
            scrim.Brush = eb;
        }
        catch (Exception ex) { Log.Warn($"sheet backdrop: {ex.Message}"); }
        scrim.Size = new Vector2(CsW, CsH);
        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var rs = new RsState { Root = root, Scrim = scrim, Sheet = Sp(), Glow = Sp(), Line = Sp(), Rim = Sp(), Item = g, Page = p };
        _rs = rs;
        foreach (var v in new Visual[] { scrim, rs.Sheet, rs.Glow, rs.Line, rs.Rim }) root.Children.InsertAtTop(v);
        _cs.Root.Children.InsertAtTop(root);
        RsPaintCursor(rs);
        RsPush(first, animate: false);
        if (!Reduced)
        {
            root.Opacity = 0;
            root.StartAnimation("Opacity", CsS(1, 220, CsQuart));
            rs.Sheet.Offset = new Vector3(rs.Sheet.Offset.X, rs.Sheet.Offset.Y + 30, 0);
            rs.Sheet.StartAnimation("Offset", CsV3(new Vector3(rs.Sheet.Offset.X, rs.Sheet.Offset.Y - 30, 0), 320, CsQuart));
        }
    }

    private void RsPush(RsPage page, bool animate = true)
    {
        if (_rs is not { } rs) return;
        page.Rows = page.Build();
        page.Sel = Math.Clamp(page.Sel, 0, Math.Max(0, page.Rows.Count - 1));
        rs.Stack.Add(page);
        rs.DiscardArmed = false;
        RsPaint(rs);
        RsCursorTo(rs, move: false);
        if (animate && !Reduced) RsSlide(rs, 1);
    }

    /// <summary>压页 / 退页：面板内容从右（压进）或左（退回）滑进来一点并淡入。</summary>
    private void RsSlide(RsState rs, int dir)
    {
        var to = rs.Sheet.Offset;
        rs.Sheet.Offset = to + new Vector3(40 * dir, 0, 0);
        rs.Sheet.StartAnimation("Offset", CsV3(to, 260, CsQuart));
        rs.Sheet.Opacity = 0.4f;
        rs.Sheet.StartAnimation("Opacity", CsS(1, 200, CsQuart));
    }

    private void RsPop()
    {
        if (_rs is not { } rs) return;
        RsEndEdit(commit: false);
        rs.Stack.RemoveAt(rs.Stack.Count - 1);
        if (rs.Stack.Count == 0) { RsCloseAll(); return; }
        var top = rs.Stack[^1];
        top.Rows = top.Build();
        rs.DiscardArmed = false;
        RsPaint(rs);
        RsCursorTo(rs, move: false);
        if (!Reduced) RsSlide(rs, -1);
    }

    private void RsCloseAll(bool instant = false)
    {
        if (_rs is not { } rs) return;
        RsEndEdit(commit: false);
        _rs = null;
        if (_rsOwnsGs) { _rsOwnsGs = false; _gsItem = null; _gsState = null; }
        void Drop()
        {
            _cs?.Root.Children.Remove(rs.Root);
            foreach (var s in new[] { rs.SheetSurf, rs.GlowSurf, rs.LineSurf, rs.RimSurf }) s?.Dispose();
            rs.Root.Dispose();
        }
        if (instant || Reduced) { Drop(); return; }
        rs.Root.StartAnimation("Opacity", CsS(0, 180, CsQuart));
        After(200, Drop);
    }

    // ---------------- 画 ----------------

    private void RsPaint(RsState rs)
    {
        var page = rs.Stack[^1];
        var dev = G.Device;
        float R = CsRaster, pad = 50, w = RsW + pad * 2, h = RsH + pad * 2;
        rs.SheetSurf ??= G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        using (var ds = CanvasComposition.CreateDrawingSession(rs.SheetSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - RsX, pad - RsY) * Matrix3x2.CreateScale(R);
            using (var shadow = new CanvasCommandList(dev))
            {
                using (var s = shadow.CreateDrawingSession()) { s.Transform = ds.Transform; s.FillRoundedRectangle(RsX, RsY + 12, RsW, RsH, RsR, RsR, Colors.Black); }
                using var blur = new GaussianBlurEffect { Source = shadow, BlurAmount = 22 * R, BorderMode = EffectBorderMode.Soft };
                using var op = new OpacityEffect { Source = blur, Opacity = 0.35f };
                var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(op); ds.Transform = t;
            }
            ds.FillRoundedRectangle(RsX, RsY, RsW, RsH, RsR, RsR, RsBg);
            RsPaintHeader(ds, page);
            page.PaintHeaderExtra?.Invoke(ds);
            if (page.Paint != null) page.Paint(ds); else RsPaintList(ds, rs, page);
        }
        ((CompositionSurfaceBrush)rs.Sheet.Brush).Surface = rs.SheetSurf;
        rs.Sheet.Size = new Vector2(w, h);
        rs.Sheet.StopAnimation("Offset");
        rs.Sheet.Offset = new Vector3(RsX - pad, RsY - pad, 0);
    }

    private void RsPaintList(CanvasDrawingSession ds, RsState rs, RsPage page)
    {
        var dev = ds.Device;
        {
            // 列表：只画可见的几行，上下多出去的被面板圆角里那块裁掉
            page.Top = Math.Clamp(page.Top, 0, Math.Max(0, page.Rows.Count - RsVisible));
            if (page.Sel < page.Top) page.Top = page.Sel;
            if (page.Sel >= page.Top + RsVisible) page.Top = page.Sel - RsVisible + 1;
            using var clip = CanvasGeometry.CreateRectangle(dev, RsX, RsTop - 4, RsW, RsBottom - RsTop + 8);
            using (ds.CreateLayer(1, clip))
                for (int i = page.Top; i < page.Rows.Count && i < page.Top + RsVisible + 1; i++)
                {
                    float y = RsTop + (i - page.Top) * (RsRowH + RsSep);
                    if (i > page.Top) ds.FillRectangle(RsListX, y - RsSep, RsListW, RsSep, RsLine);
                    RsPaintRow(ds, page.Rows[i], y, rs.Item);
                }
            if (page.Top + RsVisible < page.Rows.Count)
            {
                // 下面还有：紫色向下箭头
                float cx = RsX + RsW / 2, cy = RsBottom + 6;
                using var pb = new CanvasPathBuilder(dev);
                pb.BeginFigure(cx - 16, cy - 7); pb.AddLine(cx, cy + 7); pb.AddLine(cx + 16, cy - 7);
                pb.EndFigure(CanvasFigureLoop.Open);
                using var chev = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(chev, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 13, RwRoundStroke);
                using var br = new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(cx, cy - 8), EndPoint = new Vector2(cx, cy + 8) };
                ds.DrawGeometry(chev, br, 7, RwRoundStroke);
            }
        }
    }

    private void RsPaintHeader(CanvasDrawingSession ds, RsPage page)
    {
        var dev = ds.Device;
        // 左上：灰色胶囊里 B 圆章 + 黑色返回箭头
        ds.FillRoundedRectangle(134, 104, 134, 74, 37, 37, Color.FromArgb(255, 0xE2, 0xE3, 0xE7));
        ds.FillCircle(170, 141, 21, Color.FromArgb(255, 0x9A, 0x9C, 0xA3));
        using (var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText("B", new Windows.Foundation.Rect(149, 120, 42, 42), Color.FromArgb(255, 0xF2, 0xF2, 0xF4), f);
        using (var pb = new CanvasPathBuilder(dev))
        {
            pb.BeginFigure(248, 141); pb.AddLine(212, 141); pb.EndFigure(CanvasFigureLoop.Open);
            pb.BeginFigure(228, 125); pb.AddLine(212, 141); pb.AddLine(228, 157); pb.EndFigure(CanvasFigureLoop.Open);
            using var arrow = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(arrow, RmInk, 7, RwRoundStroke);
        }
        float tx = 288;
        if (page.TitleIcon.Length > 0) { RsGlyph(ds, page.TitleIcon, 314, 141, RmInk, 0.8f); tx = 362; }
        using (var f = new CanvasTextFormat { FontFamily = RmFont(page.Title), FontSize = 48, FontWeight = new Windows.UI.Text.FontWeight(600), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(page.Title, new Windows.Foundation.Rect(tx, 104, 1200, 74), RmInk, f);
        // 右上：紫色胶囊里 Y 圆章 + 白色对勾圆
        using (var br = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0x8A, 0x7B, 0xFF), Color.FromArgb(255, 0x7A, 0x3C, 0xFF)) { StartPoint = new Vector2(1652, 104), EndPoint = new Vector2(1786, 178) })
            ds.FillRoundedRectangle(1652, 104, 134, 74, 37, 37, br);
        ds.FillCircle(1689, 141, 21, Color.FromArgb(255, 0xB9, 0xA9, 0xFF));
        using (var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText("Y", new Windows.Foundation.Rect(1668, 120, 42, 42), Color.FromArgb(255, 0x7A, 0x4C, 0xFF), f);
        ds.FillCircle(1750, 141, 21, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
        using (var pb = new CanvasPathBuilder(dev))
        {
            pb.BeginFigure(1740, 141); pb.AddLine(1747, 148); pb.AddLine(1760, 134); pb.EndFigure(CanvasFigureLoop.Open);
            using var tick = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(tick, Color.FromArgb(255, 0x7A, 0x4C, 0xFF), 5, RwRoundStroke);
        }
    }

    private void RsPaintRow(CanvasDrawingSession ds, RsRow row, float y, LibraryItem g)
    {
        float cy = y + RsRowH / 2;
        var ink = row.Danger ? RmRed : RmInk;
        bool twoLine = row.Kind is RsKind.Field or RsKind.Info;
        float textX = row.Icon.Length > 0 ? 256 : 144;
        if (row.Icon.Length > 0) RsGlyph(ds, row.Icon, 184, cy, ink, 1);
        if (twoLine)
        {
            using var lf = new CanvasTextFormat { FontFamily = RmFont(row.Title), FontSize = 30, FontWeight = new Windows.UI.Text.FontWeight(600), WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(row.Title, textX, y + 20, RsSub, lf);
            string v = string.IsNullOrWhiteSpace(row.Value) ? "未设置" : row.Value!.Replace('\n', ' ');
            using var vf = new CanvasTextFormat { FontFamily = RmFont(v), FontSize = 42, FontWeight = new Windows.UI.Text.FontWeight(600), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(v, new Windows.Foundation.Rect(textX, y + 62, 1520 - textX, 60), string.IsNullOrWhiteSpace(row.Value) ? Color.FromArgb(255, 0x9A, 0x9C, 0xA3) : ink, vf);
            if (row.Kind == RsKind.Field) RsGlyph(ds, row.Key == "released" ? "calendar" : "pencil", 1748, cy, RmInk, 0.8f);
            return;
        }
        using (var tf = new CanvasTextFormat { FontFamily = RmFont(row.Title), FontSize = 44, FontWeight = new Windows.UI.Text.FontWeight(600), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(row.Title, new Windows.Foundation.Rect(textX, y, 1300, RsRowH), ink, tf);
        switch (row.Kind)
        {
            case RsKind.Nav:
            {
                using var pb = new CanvasPathBuilder(ds.Device);
                pb.BeginFigure(1744, cy - 9); pb.AddLine(1754, cy); pb.AddLine(1744, cy + 9); pb.EndFigure(CanvasFigureLoop.Closed);
                using var tri = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(tri, RmInk);
                ds.DrawGeometry(tri, RmInk, 3, RwRoundStroke);
                break;
            }
            case RsKind.Toggle:
            {
                bool on = row.IsOn?.Invoke() == true;
                float x = 1656, ty = cy - 38;
                if (on)
                {
                    using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(x, ty), EndPoint = new Vector2(x + 120, ty + 76) };
                    ds.FillRoundedRectangle(x, ty, 120, 76, 38, 38, br);
                }
                else
                {
                    ds.FillRoundedRectangle(x, ty, 120, 76, 38, 38, Color.FromArgb(255, 0xE4, 0xE5, 0xE9));
                    ds.DrawRoundedRectangle(x + 2, ty + 2, 116, 72, 36, 36, Color.FromArgb(255, 0x9A, 0x9C, 0xA3), 4);
                }
                ds.FillCircle(on ? x + 82 : x + 38, cy, 27, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                break;
            }
            case RsKind.Pick:
            {
                // 下拉框：浅灰圆角底、当前值、右边小三角（Cocoon 的「Use Default ▾」）
                string val = row.Value ?? "";
                float px = 1340, pw = 436, ph = 84, py = cy - ph / 2;
                ds.FillRoundedRectangle(px, py, pw, ph, 30, 30, Color.FromArgb(255, 0xEC, 0xED, 0xF1));
                using var vf = new CanvasTextFormat { FontFamily = RmFont(val), FontSize = 36, FontWeight = new Windows.UI.Text.FontWeight(600), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
                ds.DrawText(val, new Windows.Foundation.Rect(px + 30, py, pw - 90, ph), RmInk, vf);
                using var pb = new CanvasPathBuilder(ds.Device);
                pb.BeginFigure(px + pw - 50, cy - 5); pb.AddLine(px + pw - 36, cy - 5); pb.AddLine(px + pw - 43, cy + 4); pb.EndFigure(CanvasFigureLoop.Closed);
                using var tri = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(tri, RsSub);
                ds.DrawGeometry(tri, RsSub, 3, RwRoundStroke);
                break;
            }
            case RsKind.Action when row.Value is { Length: > 0 } val:
            {
                using var vf = new CanvasTextFormat { FontFamily = RmFont(val), FontSize = 36, FontWeight = new Windows.UI.Text.FontWeight(500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
                ds.DrawText(val, new Windows.Foundation.Rect(900, y, 870, RsRowH), RsSub, vf);
                break;
            }
        }
    }

    /// <summary>行首 / 标题 / 行尾的黑色线面图标（Cocoon 二级页里是纯黑实心图标）。</summary>
    private static void RsGlyph(CanvasDrawingSession ds, string name, float cx, float cy, Color ink, float k)
    {
        var dev = ds.Device;
        var t = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(k, k, new Vector2(cx, cy)) * t;
        switch (name)
        {
            case "info":
                ds.FillCircle(cx, cy, 34, ink);
                ds.FillCircle(cx, cy - 14, 5, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                ds.FillRoundedRectangle(cx - 4.5f, cy - 4, 9, 22, 4.5f, 4.5f, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                break;
            case "artwork":
                ds.DrawRoundedRectangle(cx - 30, cy - 30, 60, 60, 6, 6, ink, 7);
                ds.FillCircle(cx + 12, cy - 12, 9, ink);
                using (var pb = new CanvasPathBuilder(dev))
                {
                    pb.BeginFigure(cx - 30, cy + 22); pb.AddLine(cx - 4, cy - 6); pb.AddLine(cx + 30, cy + 26); pb.AddLine(cx + 30, cy + 30); pb.AddLine(cx - 30, cy + 30);
                    pb.EndFigure(CanvasFigureLoop.Closed);
                    using var mtn = CanvasGeometry.CreatePath(pb);
                    ds.FillGeometry(mtn, ink);
                }
                break;
            case "pencil":
            {
                using var body = CanvasGeometry.CreateRoundedRectangle(dev, -7, -30, 14, 46, 3, 3);
                using var pb = new CanvasPathBuilder(dev);
                pb.BeginFigure(-7, 19); pb.AddLine(7, 19); pb.AddLine(0, 31); pb.EndFigure(CanvasFigureLoop.Closed);
                using var tip = CanvasGeometry.CreatePath(pb);
                using var pen = body.CombineWith(tip, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                using var placed = pen.Transform(Matrix3x2.CreateRotation(MathF.PI / 4) * Matrix3x2.CreateTranslation(cx, cy));
                ds.FillGeometry(placed, ink);
                break;
            }
            case "calendar":
                ds.DrawRoundedRectangle(cx - 22, cy - 18, 44, 40, 5, 5, ink, 5);
                ds.FillRectangle(cx - 22, cy - 18, 44, 10, ink);
                ds.FillRoundedRectangle(cx - 14, cy - 26, 5, 12, 2, 2, ink);
                ds.FillRoundedRectangle(cx + 9, cy - 26, 5, 12, 2, 2, ink);
                break;
            case "duck":
                // 小黄鸭：圆身子 + 圆头 + 扁嘴
                ds.FillEllipse(cx - 2, cy + 10, 30, 20, ink);
                ds.FillCircle(cx + 10, cy - 12, 15, ink);
                ds.FillEllipse(cx + 28, cy - 9, 10, 5, ink);
                ds.FillCircle(cx + 14, cy - 15, 3.5f, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                break;
            case "magpie":
                // 放大镜里一个「+」：超分 / 放大
                ds.DrawCircle(cx - 6, cy - 6, 20, ink, 7);
                ds.DrawLine(cx + 9, cy + 9, cx + 26, cy + 26, ink, 9, RwRoundStroke);
                ds.DrawLine(cx - 15, cy - 6, cx + 3, cy - 6, ink, 5, RwRoundStroke);
                ds.DrawLine(cx - 6, cy - 15, cx - 6, cy + 3, ink, 5, RwRoundStroke);
                break;
            case "pad":
                ds.FillRoundedRectangle(cx - 32, cy - 16, 64, 36, 18, 18, ink);
                ds.FillRectangle(cx - 21, cy - 1, 14, 4, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                ds.FillRectangle(cx - 16, cy - 6, 4, 14, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                ds.FillCircle(cx + 14, cy - 3, 3.5f, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                ds.FillCircle(cx + 21, cy + 4, 3.5f, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                break;
            case "power":
            {
                // 闪电
                using var pb = new CanvasPathBuilder(dev);
                pb.BeginFigure(cx + 6, cy - 30); pb.AddLine(cx - 18, cy + 4); pb.AddLine(cx - 1, cy + 4); pb.AddLine(cx - 6, cy + 30); pb.AddLine(cx + 18, cy - 4); pb.AddLine(cx + 1, cy - 4);
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var bolt = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(bolt, ink);
                ds.DrawGeometry(bolt, ink, 3, RwRoundStroke);
                break;
            }
            case "clock":
                ds.DrawCircle(cx, cy, 28, ink, 6);
                ds.DrawLine(cx, cy, cx, cy - 15, ink, 6, RwRoundStroke);
                ds.DrawLine(cx, cy, cx + 12, cy + 6, ink, 6, RwRoundStroke);
                break;
        }
        ds.Transform = t;
    }

    // ---------------- 焦点 ----------------

    private static (float W, float H, float R, float Leg) RsBracketGeom()
    {
        // 和游戏菜单同一种：圆角 60，左右中间留 13 的缺口
        float W = RsListW + 14, H = RsRowH + 20, r = 60;
        return (W, H, r, MathF.Max(0, (H - 2 * r - 13) / 2));
    }

    private void RsPaintCursor(RsState rs)
    {
        var dev = G.Device;
        float R = CsRaster;
        var (W, H, r, leg) = RsBracketGeom();
        float sw = W + RmCurPad * 2, sh = H + RmCurPad * 2;
        int pw = (int)Math.Ceiling(sw * R), ph = (int)Math.Ceiling(sh * R);
        var (segs, _) = RwBracketPointsRaw(W, H, r, leg, RmCurPad, RmCurPad);
        using var path = RwBracketPath(dev, segs);
        rs.GlowSurf = G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rs.GlowSurf))
        {
            ds.Clear(Colors.Transparent);
            using var cl = new CanvasCommandList(ds);
            using (var s = cl.CreateDrawingSession()) { s.Transform = Matrix3x2.CreateScale(R); s.DrawGeometry(path, RwC2, RwSw * 2, RwRoundStroke); }
            using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 7 * RwDp * R / 2, BorderMode = EffectBorderMode.Soft };
            ds.DrawImage(blur);
        }
        rs.RimSurf = G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rs.RimSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            using var outer = path.Stroke(RwSw + 2 * RwDp, RwRoundStroke);
            using var inner = path.Stroke(MathF.Max(0.1f, RwSw - 2 * RwDp), RwRoundStroke);
            using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            ds.FillGeometry(ring, Color.FromArgb(102, 0xfa, 0xfa, 0xfa));
        }
        rs.LineSurf = G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rs.LineSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            ds.DrawGeometry(path, Colors.White, RwSw, RwRoundStroke);
        }
        ((CompositionSurfaceBrush)rs.Glow.Brush).Surface = rs.GlowSurf;
        ((CompositionSurfaceBrush)rs.Rim.Brush).Surface = rs.RimSurf;
        rs.Line.Brush = RwFlowBrush(rs.Page.Rw!, rs.LineSurf);
        foreach (var v in new Visual[] { rs.Glow, rs.Line, rs.Rim }) v.Size = new Vector2(sw, sh);
    }

    private void RsCursorTo(RsState rs, bool move)
    {
        var page = rs.Stack[^1];
        var (W, H, _, _) = RsBracketGeom();
        float y = RsTop + (page.Sel - page.Top) * (RsRowH + RsSep);
        var to = new Vector3(RsListX + RsListW / 2 - W / 2 - RmCurPad, y + RsRowH / 2 - H / 2 - RmCurPad, 0);
        bool show = page.Rows.Count > 0 && page.Paint == null;
        foreach (var v in new Visual[] { rs.Glow, rs.Line, rs.Rim })
        {
            if (move && !Reduced) v.StartAnimation("Offset", RwSpring(to)); else RwJump(v, to);
            v.Opacity = show ? 1 : 0;
        }
    }

    // ---------------- 输入 ----------------

    private bool RsActive => _rs != null;

    private void RsMove(Direction dir)
    {
        if (_rs is not { } rs || _rsEditing != null) return;
        var page = rs.Stack[^1];
        if (page.Move != null) { if (page.Move(dir)) { RsPaint(rs); CuePan(0); } else Cue(Sfx.Bump); return; }
        if (dir is Direction.Left or Direction.Right)
        {
            if (page.Sel < page.Rows.Count && page.Rows[page.Sel] is { Step: { } step } srow && srow.Kind is RsKind.Pick or RsKind.Toggle)
            {
                Cue(Sfx.Nav, pan: dir == Direction.Left ? -0.4f : 0.4f);
                step(dir == Direction.Left ? -1 : 1);
                if (_rs == rs) { page.Rows = page.Build(); RsPaint(rs); }
                return;
            }
            Cue(Sfx.Bump);
            return;
        }
        int j = page.Sel + (dir == Direction.Down ? 1 : -1);
        if (j < 0 || j >= page.Rows.Count) { Cue(Sfx.Bump); return; }
        int top = page.Top;
        page.Sel = j;
        rs.DiscardArmed = false;
        CuePan(0);
        if (j < page.Top || j >= page.Top + RsVisible) { RsPaint(rs); RsCursorTo(rs, move: false); }
        else RsCursorTo(rs, move: true);
        if (page.Top != top) RsCursorTo(rs, move: false);
    }

    private void RsActivate()
    {
        if (_rs is not { } rs) return;
        if (_rsEditing != null) { RsEndEdit(commit: true); return; }
        var page = rs.Stack[^1];
        if (page.Activate != null) { page.Activate(); if (_rs == rs && rs.Stack.Count > 0 && rs.Stack[^1] == page) RsPaint(rs); return; }
        if (page.Sel >= page.Rows.Count) return;
        var row = page.Rows[page.Sel];
        switch (row.Kind)
        {
            case RsKind.Field: Cue(Sfx.Activate); RsBeginEdit(rs, row); return;
            case RsKind.Info: Cue(Sfx.Bump); return;
            default:
                Cue(row.Kind == RsKind.Toggle ? Sfx.ToggleOn : Sfx.Activate);
                row.Press?.Invoke();
                if (_rs == rs && rs.Stack.Count > 0 && rs.Stack[^1] == page) { page.Rows = page.Build(); RsPaint(rs); }
                return;
        }
    }

    /// <summary>LB / RB：页里有分段页签（封面管理）就切页签。</summary>
    private void RsTab(int delta)
    {
        if (_rs is not { } rs || rs.Stack.Count == 0) return;
        var page = rs.Stack[^1];
        if (page.Tab == null) { Cue(Sfx.Bump); return; }
        Cue(Sfx.Tab);
        page.Tab(delta);
        RsPaint(rs);
    }

    /// <summary>图片异步解码好了：封面管理页开着就重画。</summary>
    private bool _rsPaintQueued;
    private void RsPaintSoon()
    {
        if (_rsPaintQueued || _rs is not { } rs || rs.Stack.Count == 0 || rs.Stack[^1].Paint == null) return;
        _rsPaintQueued = true;
        After(60, () => { _rsPaintQueued = false; if (_rs is { } r2 && r2.Stack.Count > 0) RsPaint(r2); });
    }

    /// <summary>Y ✓：保存这一页的改动并退回上一层。</summary>
    private void RsSave()
    {
        if (_rs is not { } rs) return;
        if (_rsEditing != null) RsEndEdit(commit: true);
        var page = rs.Stack[^1];
        page.Save?.Invoke();
        Cue(Sfx.Positive);
        RsPop();
    }

    /// <summary>B：有没保存的改动时先提示一次，再按才放弃。</summary>
    private bool RsBack()
    {
        if (_rs is not { } rs) return false;
        if (_rsEditing != null) { RsEndEdit(commit: false); return true; }
        var page = rs.Stack[^1];
        if (page.Dirty?.Invoke() == true && !rs.DiscardArmed)
        {
            rs.DiscardArmed = true;
            Cue(Sfx.Error);
            Toast("还没保存：按 Y 保存，再按一次 B 放弃改动");
            return true;
        }
        Cue(Sfx.Back);
        RsPop();
        return true;
    }

    // ---------------- 文字编辑（触屏键盘 / 实体键盘） ----------------

    private void RsBeginEdit(RsState rs, RsRow row)
    {
        RsEnsureBox();
        _rsEditing = row;
        var page = rs.Stack[^1];
        float y = RsTop + (page.Sel - page.Top) * (RsRowH + RsSep);
        var o = _cs!.Root.Offset;
        float k = _cs.K;
        var box = _rsBox!;
        box.Text = row.Value ?? "";
        box.AcceptsReturn = row.Multiline;
        box.TextWrapping = row.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        box.FontSize = Math.Max(12, 40 * k);
        box.Width = 1440 * k;
        box.Height = (row.Multiline ? 300 : 64) * k;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(box, o.X + 248 * k);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(box, o.Y + (y + 56) * k);
        box.Visibility = Visibility.Visible;
        box.SelectAll();
        FocusTextBox(box, () => _rsEditing == row);
    }

    private void RsEndEdit(bool commit)
    {
        if (_rsEditing is not { } row || _rsBox == null) return;
        _rsEditing = null;
        if (commit) { row.Value = _rsBox.Text.Trim(); Cue(Sfx.Positive); }
        _rsBox.Visibility = Visibility.Collapsed;
        _rootEl.Focus(FocusState.Programmatic);
        if (_rs is { } rs) RsPaint(rs);
    }

    private void RsEnsureBox()
    {
        if (_rsBox != null) return;
        _rsBox = new TextBox
        {
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            RequestedTheme = ElementTheme.Light,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            Visibility = Visibility.Collapsed,
        };
        var bg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        var ink = new Microsoft.UI.Xaml.Media.SolidColorBrush(RmInk);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" }) _rsBox.Resources[key] = bg;
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" }) _rsBox.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) _rsBox.Resources[key] = ink;
        _rsBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && _rsEditing is { Multiline: false }) { e.Handled = true; RsEndEdit(commit: true); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; RsEndEdit(commit: false); }
        };
        HookTouchKeyboard(_rsBox);
        _rootEl.Overlay.Children.Add(_rsBox);
    }

    // ---------------- 元数据（存在游戏库自己的 game-meta.json） ----------------

    private Dictionary<string, Dictionary<string, string>>? _rwMeta;
    private readonly Dictionary<string, string> _rwOrigTitle = new();
    private static string RwMetaPath => Path.Combine(App.CacheDir, "game-meta.json");

    private Dictionary<string, Dictionary<string, string>> RwMetaAll()
    {
        if (_rwMeta != null) return _rwMeta;
        try { _rwMeta = File.Exists(RwMetaPath) ? JsonSerializer.Deserialize(File.ReadAllText(RwMetaPath), RwMetaJson.Default.DictionaryStringDictionaryStringString) : null; }
        catch (Exception ex) { Log.Warn($"game meta: {ex.Message}"); }
        return _rwMeta ??= new();
    }

    private Dictionary<string, string> RwMetaOf(LibraryItem g) => RwMetaAll().TryGetValue(g.Id, out var m) ? m : new();

    private void RwMetaSave(LibraryItem g, Dictionary<string, string> m)
    {
        var all = RwMetaAll();
        var clean = m.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (clean.Count == 0) all.Remove(g.Id); else all[g.Id] = clean;
        try { File.WriteAllText(RwMetaPath, JsonSerializer.Serialize(all, RwMetaJson.Default.DictionaryStringDictionaryStringString)); }
        catch (Exception ex) { Log.Warn($"game meta save: {ex.Message}"); Toast("没有保存成功"); return; }
        RwApplyMeta(g);
    }

    /// <summary>标题改了：游戏库里所有地方都显示新标题（原来的标题记着，清空时还原）。</summary>
    private void RwApplyMeta(LibraryItem g)
    {
        if (!_rwOrigTitle.ContainsKey(g.Id)) _rwOrigTitle[g.Id] = g.Title;
        g.Title = RwMetaOf(g).TryGetValue("title", out var t) && t.Length > 0 ? t : _rwOrigTitle[g.Id];
    }

    private void RwApplyMetaAll(IEnumerable<LibraryItem> items)
    {
        var all = RwMetaAll();
        if (all.Count == 0) return;
        foreach (var g in items) if (all.ContainsKey(g.Id) || _rwOrigTitle.ContainsKey(g.Id)) RwApplyMeta(g);
    }

    // ---------------- 页面：编辑 / 编辑元数据 ----------------

    private RsPage RsEditPage(LibraryItem g, CsPage p) => new()
    {
        Title = "编辑",
        Build = () => new()
        {
            new RsRow { Kind = RsKind.Nav, Icon = "info", Title = "编辑元数据", Press = () => RsPush(RsMetaPage(g)) },
            new RsRow { Kind = RsKind.Nav, Icon = "artwork", Title = "封面管理", Press = () => RsPush(RaPage(g)) },
        },
    };

    private static readonly (string Key, string Title, bool Multi)[] RsMetaFields =
    {
        ("title", "标题", false), ("developer", "开发商", false), ("publisher", "发行商", false),
        ("released", "发行日期", false), ("genre", "类型", false), ("description", "简介", true),
    };

    private RsPage RsMetaPage(LibraryItem g)
    {
        var saved = RwMetaOf(g);
        string Orig(string key) => key switch
        {
            "title" => _rwOrigTitle.TryGetValue(g.Id, out var t) ? t : g.Title,
            "developer" => g.Subtitle is "本地" or "非 Steam" ? "" : g.Subtitle,
            "genre" => g.Genre ?? "",
            _ => "",
        };
        // 草稿：没改过的字段显示游戏库自带的值
        var draft = RsMetaFields.ToDictionary(f => f.Key, f => saved.TryGetValue(f.Key, out var v) ? v : Orig(f.Key));
        var rows = RsMetaFields.Select(f => new RsRow { Kind = RsKind.Field, Title = f.Title, Key = f.Key, Value = draft[f.Key], Multiline = f.Multi }).ToList();
        var page = new RsPage { Title = "编辑元数据", TitleIcon = "pencil", Build = () => rows };
        page.Dirty = () => rows.Any(r => r.Key != null && (r.Value ?? "") != draft[r.Key]);
        page.Save = () =>
        {
            var m = new Dictionary<string, string>();
            foreach (var r in rows) if (r.Key != null && (r.Value ?? "") != Orig(r.Key)) m[r.Key] = r.Value ?? "";
            RwMetaSave(g, m);
            Toast("已保存");
        };
        rows.Add(new RsRow { Kind = RsKind.Info, Icon = "", Title = "游玩时长", Value = g.PlaytimeH is double h && h > 0 ? (h < 1 ? $"{h * 60:0} 分钟" : $"{h:0.#} 小时") : "0 分钟" });
        rows.Add(new RsRow { Kind = RsKind.Info, Icon = "", Title = "上次游玩", Value = g.LastPlayedDays is int d ? (d == 0 ? "今天" : $"{d} 天前") : "没玩过" });
        return page;
    }

    // ---------------- 页面：游戏配置（小黄鸭 / Magpie / 手柄档位 / 功耗档位，每款游戏独立） ----------------
    // 数据和改法沿用原来的游戏设置页（game_settings_get / set，GsSet、GsPower……），这里只换成 Cocoon 式的一行一项。

    /// <summary>原来游戏设置页某个分类里、某几个小节的行，转成二级页的行（开关 / 下拉 / 数值 / 只读 / 按钮）。</summary>
    private List<RsRow> RsFromGs(int cat, params string[] sections)
    {
        int keepCat = _stCat;
        var keep = _stRows.ToList();
        var outRows = new List<RsRow>();
        try
        {
            _stCat = cat;
            _stRows.Clear();
            BuildGsRows();
            bool take = false;
            foreach (var r in _stRows)
            {
                if (r.Section) { take = sections.Contains(r.Title); continue; }
                if (!take || r.Skip) continue;
                outRows.Add(RsFromPerf(r));
            }
        }
        catch (Exception ex) { Log.Warn($"game config rows: {ex.Message}"); }
        finally { _stCat = keepCat; _stRows.Clear(); _stRows.AddRange(keep); }
        if (outRows.Count == 0) outRows.Add(new RsRow { Kind = RsKind.Info, Title = "正在读取", Value = _gsLoading ? "…" : "没有可以设置的项" });
        return outRows;
    }

    private static RsRow RsFromPerf(PerfRow r)
    {
        if (r.Disabled) return new RsRow { Kind = RsKind.Info, Title = r.Title, Value = r.Value };
        if (r.Choices is { Length: 2 } c && c[0] == "关" && c[1] == "开")
            return new RsRow { Kind = RsKind.Toggle, Title = r.Title, IsOn = () => r.Choice == 1, Press = r.Press, Step = r.Step };
        if (r.Choices != null && r.Choice >= 0 && r.Choice < r.Choices.Length)
            return new RsRow { Kind = RsKind.Pick, Title = r.Title, Value = r.Choices[r.Choice], Press = r.Press ?? (() => r.Step?.Invoke(1)), Step = r.Step };
        if (r.Numeric || r.Step != null)
            return new RsRow { Kind = RsKind.Pick, Title = r.Title, Value = r.Value.Length > 0 ? r.Value : r.Number.ToString(), Press = r.Press ?? (() => r.Step?.Invoke(1)), Step = r.Step };
        return new RsRow { Kind = RsKind.Action, Title = r.Title, Value = r.Value.Replace("  ›", "").Trim(), Press = r.Press };
    }

    private RsPage RsConfigPage(LibraryItem g)
    {
        // 借用游戏设置页的状态读这款游戏的配置；读好之后正在显示的页自己重画
        _gsItem = g;
        _gsState = null;
        _gsLosslessError = null;
        _rsOwnsGs = true;
        _ = GsLoad();
        RsPage Sub(string title, int cat, params string[] sections) => new() { Title = title, Build = () => RsFromGs(cat, sections) };
        return new RsPage
        {
            Title = "游戏配置",
            Build = () => new()
            {
                new RsRow { Kind = RsKind.Nav, Icon = "duck", Title = "小黄鸭", Press = () => RsPush(Sub("小黄鸭", 1, "小黄鸭（Lossless Scaling）")) },
                new RsRow { Kind = RsKind.Nav, Icon = "magpie", Title = "Magpie", Press = () => RsPush(Sub("Magpie", 1, "Magpie")) },
                new RsRow { Kind = RsKind.Nav, Icon = "pad", Title = "手柄档位", Press = () => RsPush(Sub("手柄档位", 2, "手柄档案", "预设")) },
                new RsRow { Kind = RsKind.Nav, Icon = "power", Title = "功耗档位", Press = () => RsPush(Sub("功耗档位", 0, "功耗", "帧率上限（RTSS）", "性能显示（RTSS）")) },
            },
        };
    }

    /// <summary>游戏设置的状态变了（读好了 / 改了一项）：二级页开着就按新状态重排当前页。</summary>
    private void RsRefresh()
    {
        if (_rs is not { } rs || rs.Stack.Count == 0 || _rsEditing != null) return;
        var page = rs.Stack[^1];
        page.Rows = page.Build();
        page.Sel = Math.Clamp(page.Sel, 0, Math.Max(0, page.Rows.Count - 1));
        RsPaint(rs);
        RsCursorTo(rs, move: false);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
internal sealed partial class RwMetaJson : System.Text.Json.Serialization.JsonSerializerContext { }
