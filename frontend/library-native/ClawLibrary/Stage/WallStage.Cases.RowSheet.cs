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
    private enum RsKind { Nav, Toggle, Field, Info, Action, Pick, Order }

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
        /// <summary>从这一行起另开一张分组卡片。</summary>
        public bool Break;
        /// <summary>文字编辑提交后回调（刮削游戏的搜索词）。</summary>
        public Action<string>? Commit;
        /// <summary>标题下面一行灰色说明（Cocoon 每一行都有）。</summary>
        public string? Sub;
        /// <summary>Order（来源优先级）：还能不能往上 / 往下挪。</summary>
        public bool CanUp, CanDown;
        /// <summary>下拉框（Cocoon：按 A 在原地展开成一张选项卡片，行跟着变高）：选项、当前选中、选中后。</summary>
        public string[]? Options;
        public Func<int>? Current;
        public Action<int>? Choose;
        /// <summary>多选下拉（资料类型 / 图片类型）：每项右边一个勾，A 勾选 / 取消，B 收起。</summary>
        public bool Multi;
        public Func<int, bool>? Checked;
        public Action<int>? ToggleOpt;
        /// <summary>画的时候填：展开着时焦点在第几个选项（-1 = 没展开）。</summary>
        public int OpenSel = -1;
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
        /// <summary>Y 不是「保存并退出」而是一个动作（刮削页的 Y ▶ 开始）：右上胶囊画成三角，按了不退页。</summary>
        public Action? YAction;
        /// <summary>进行中锁住：B 不退（刮削进度页）；NoY = 右上不画 Y 胶囊。</summary>
        public Func<bool>? Lock;
        public bool NoY;
        /// <summary>展开着的下拉是第几行（-1 = 没有），焦点在它的第几个选项。</summary>
        public int Open = -1, OptSel;
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
        // 分层（照 Cocoon 视频逐帧拆的入场）：面板底 Sheet 单独放大；Body 里是骨架（胶囊 + 分组卡片 + 分隔线）、标题、
        // 每一行的内容（各自错开淡入）、自绘页的内容；Body 整体比面板晚一点从中心放大。
        public required ContainerVisual Body, Rows, Scroll;
        public required SpriteVisual Skel, Groups, Title, Custom, Chev;
        public CompositionDrawingSurface? SkelSurf, GroupsSurf, TitleSurf, CustomSurf, ChevSurf;
        public readonly List<SpriteVisual> RowVis = new();
        public readonly List<CompositionDrawingSurface> RowSurf = new();
        public List<(int I, float Y)> Layout = new();
        /// <summary>每次入场动画一个号：延时显示焦点框时核对，期间又翻页了就不显示旧的。</summary>
        public int EnterSeq;
        public bool FromMenu;
        /// <summary>焦点框现在画成的尺寸（整行 / 下拉选项）：换尺寸时重画并从旧尺寸弹过去。</summary>
        public float CurW, CurH;
        /// <summary>焦点框线的右半（左右各一个沿周长流动的渐变）。</summary>
        public SpriteVisual Line2 = null!;
        /// <summary>分组卡片精灵；本次排版里每组 / 每行的位置（下一次重排时拿来做滑动动画）。</summary>
        public readonly List<SpriteVisual> GroupVis = new();
        public readonly List<CompositionDrawingSurface?> GroupSurf = new();
        public readonly Dictionary<string, (float Y0, float Y1)> GroupRects = new();
        public readonly Dictionary<string, float> RowYs = new();
        /// <summary>焦点在来源行上时，↓↑ 胶囊展开成两个带十字键的按钮（单独一层，好做展开动画）。</summary>
        public SpriteVisual? Pill;
        public CompositionDrawingSurface? PillSurf;
        public int PillRow = -1;
    }

    private RsState? _rs;
    /// <summary>游戏配置页借用了游戏设置页的状态（_gsItem / _gsState），关掉时还回去。</summary>
    private bool _rsOwnsGs;
    private TextBox? _rsBox;
    private RsRow? _rsEditing;

    // 面板几何（设计单位 1920×1200）
    private const float RsX = 96, RsY = 70, RsW = 1728, RsH = 1060, RsR = 64;
    private const float RsListX = 116, RsListW = 1688, RsTop = 220, RsRowH = 136, RsSep = 4, RsBottom = 1100;
    /// <summary>分组之间的空隙、分组卡片圆角（Cocoon：开关类和跳转类分在两张卡片里，中间空 30）。</summary>
    private const float RsGap = 32, RsGroupR = 30;
    private const float RsPad = 50;
    // 面板底 #FAFAFA，分组卡片 #F7F8FA，分隔线 #E2E3E5（从视频像素取）
    /// <summary>面板底：浅色 #FAFAFA；暗色比卡片（#303137）低一级，卡片才浮得起来。</summary>
    private static Color RsBg => CocoonPalette.Dark ? DkRgb(0x232428) : Color.FromArgb(255, 0xFA, 0xFA, 0xFA);
    private static Color RsGroup => Ck(Color.FromArgb(255, 0xF7, 0xF8, 0xFA));
    private static Color RsLine => Ck(Color.FromArgb(255, 0xE2, 0xE3, 0xE5));
    private static Color RsSub => Ck(Color.FromArgb(255, 0x5F, 0x61, 0x68));
    /// <summary>中文用 PingFang Medium（500）；拉丁字母用 Poppins SemiBold。中文要 600 会被合成加粗，糊成一团。</summary>
    private static Windows.UI.Text.FontWeight RsWt(string text, ushort latin = 600) => new(text.Any(ch => ch >= 0x2E80) ? (ushort)500 : latin);

    // ---------------- 打开 / 关闭 / 压页 ----------------
    // 动效逐帧拆自 jinyu 录的 Cocoon 视频（11-20，30 fps）。以按下 A 为 0：
    //   0–60 ms   菜单白卡压扁消失，焦点框立刻收掉；封面往左飞出屏幕（~100 ms）、标题淡出；
    //   80–230    面板底从屏幕中心 0.3 倍放大到满（缓出，不回弹）；
    //   150–340   面板里的骨架（返回 / 保存胶囊、分组卡片和分隔线，还没有字）也从中心放大，比面板慢半拍；
    //   400       标题淡入；
    //   466 起    每一行的内容逐行出现，间隔 33 ms：0.96 倍放大到 1、淡入 100 ms；
    //   最后一行出现后约 230 ms 焦点框直接出现（不带动画），下箭头同时出现。
    // 页内进 / 退一层：内容清空，骨架从中心放大（150 ms），100 ms 标题，170 ms 起逐行。
    // 退回菜单：面板缩回中心消失（70 ms），菜单白卡和焦点框回来，封面从左边飞回原位（170 ms）。

    private void RsOpen(LibraryItem g, CsPage p, RsPage first)
    {
        if (_cs == null) return;
        RsCloseAll(instant: true);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        // 从游戏菜单进来：背景沿用菜单那层（组件层模糊 + 深灰遮罩），这里不再叠；单独打开时自己盖一层同样的遮罩
        bool fromMenu = _rm is { Closing: false };
        var scrim = G.Sprite(G.Solid(fromMenu ? Color.FromArgb(0, 0, 0, 0) : RmScrim), relative: false);
        scrim.Size = new Vector2(CsW, CsH);
        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var body = G.Container(relative: false);
        body.Size = new Vector2(CsW, CsH);
        body.CenterPoint = new Vector3(RsX + RsW / 2, RsY + RsH / 2, 0);
        var rows = G.Container(relative: false);
        rows.Size = new Vector2(CsW, CsH);
        // 列表（分组卡片 + 行内容）放在一个裁在面板圆角里的外框里：滚动时整体弹簧滑动，上面滑到标题下面、下面露半行
        var scroll = G.Container(relative: false);
        scroll.Size = new Vector2(CsW, CsH);
        var clipGeo = G.C.CreateRoundedRectangleGeometry();
        clipGeo.Offset = new Vector2(RsX, RsY);
        clipGeo.Size = new Vector2(RsW, RsH - 3);
        clipGeo.CornerRadius = new Vector2(RsR);
        scroll.Clip = G.C.CreateGeometricClip(clipGeo);
        var rs = new RsState
        {
            Root = root, Scrim = scrim, Sheet = Sp(), Glow = Sp(), Line = Sp(), Rim = Sp(), Item = g, Page = p,
            Body = body, Rows = rows, Scroll = scroll, Groups = Sp(), Skel = Sp(), Title = Sp(), Custom = Sp(), Chev = Sp(), FromMenu = fromMenu,
        };
        _rs = rs;
        rs.Line2 = Sp();
        rows.Children.InsertAtBottom(rs.Groups);
        scroll.Children.InsertAtTop(rows);
        foreach (var v in new Visual[] { scroll, rs.Skel, rs.Custom, rs.Title, rs.Chev }) body.Children.InsertAtTop(v);
        foreach (var v in new Visual[] { scrim, rs.Sheet, body, rs.Glow, rs.Line, rs.Line2, rs.Rim }) root.Children.InsertAtTop(v);
        _cs.Root.Children.InsertAtTop(root);
        RsPaintSheet(rs);
        RsPaintCursor(rs);
        if (fromMenu) RmHideForSheet();
        RsPush(first, animate: false);
        if (!Reduced) RsEnter(rs, open: true);
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
        if (animate && !Reduced) RsEnter(rs, open: false);
    }

    /// <summary>入场：open = 从菜单打开（面板也放大），否则只是页内换页（面板不动）。</summary>
    private void RsEnter(RsState rs, bool open)
    {
        int seq = ++rs.EnterSeq;
        var page = rs.Stack[^1];
        var outQuint = new Bezier(0.22f, 1f, 0.36f, 1f);
        double tBody = open ? 150 : 0, durBody = open ? 190 : 150, tTitle = open ? 400 : 100, tRows = open ? 466 : 170;
        if (open)
        {
            rs.Sheet.Scale = new Vector3(0.3f, 0.3f, 1);
            rs.Sheet.Opacity = 0;
            rs.Sheet.StartAnimation("Scale", CsV3(Vector3.One, 150, outQuint, 80));
            rs.Sheet.StartAnimation("Opacity", CsS(1, 40, Bezier.EaseOut, 80));
        }
        rs.Body.Scale = new Vector3(0.3f, 0.3f, 1);
        rs.Body.Opacity = 0;
        rs.Body.StartAnimation("Scale", CsV3(Vector3.One, durBody, outQuint, tBody));
        rs.Body.StartAnimation("Opacity", CsS(1, 50, Bezier.EaseOut, tBody));
        rs.Title.Opacity = 0;
        rs.Title.StartAnimation("Opacity", CsS(1, 70, Bezier.EaseOut, tTitle));
        // 自绘页（封面管理）的内容当成第一行
        var items = new List<(Visual V, float Target)>();
        if (page.Paint != null) items.Add((rs.Custom, 1));
        for (int k = 0; k < rs.RowVis.Count; k++) if (rs.RowVis[k].IsVisible) items.Add((rs.RowVis[k], RsRowOpacity(rs, k)));
        for (int k = 0; k < items.Count; k++)
        {
            var (v, target) = items[k];
            double d = tRows + 33 * k;
            v.Opacity = 0;
            v.StartAnimation("Opacity", CsS(target, 100, Bezier.EaseOut, d));
            v.Scale = new Vector3(0.96f, 0.96f, 1);
            v.StartAnimation("Scale", CsV3(Vector3.One, 130, outQuint, d));
        }
        // 焦点框和下箭头：最后一行出来后约 230 ms 直接出现
        bool cursor = page.Rows.Count > 0 && page.Paint == null;
        foreach (var v in new Visual[] { rs.Glow, rs.Line, rs.Line2, rs.Rim, rs.Chev }) v.Opacity = 0;
        After(tRows + 33 * Math.Max(0, items.Count - 1) + 230, () =>
        {
            if (_rs != rs || rs.EnterSeq != seq || rs.Stack.Count == 0 || rs.Stack[^1] != page) return;
            if (cursor) foreach (var v in new Visual[] { rs.Glow, rs.Line, rs.Line2, rs.Rim }) v.Opacity = 1;
            rs.Chev.Opacity = 1;
        });
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
        if (!Reduced) RsEnter(rs, open: false);
    }

    private void RsCloseAll(bool instant = false)
    {
        if (_rs is not { } rs) return;
        RsEndEdit(commit: false);
        _rs = null;
        rs.EnterSeq++;
        if (_rsOwnsGs) { _rsOwnsGs = false; _gsItem = null; _gsState = null; }
        void Drop()
        {
            _cs?.Root.Children.Remove(rs.Root);
            foreach (var s in new[] { rs.SheetSurf, rs.GlowSurf, rs.LineSurf, rs.RimSurf, rs.SkelSurf, rs.GroupsSurf, rs.TitleSurf, rs.CustomSurf, rs.ChevSurf }) s?.Dispose();
            foreach (var s in rs.RowSurf) s.Dispose();
            foreach (var s in rs.GroupSurf) s?.Dispose();
            rs.PillSurf?.Dispose();
            _rsEdPanel = null;
            rs.Root.Dispose();
        }
        bool toMenu = rs.FromMenu && _rm is { Closing: false };
        if (instant || Reduced) { if (toMenu) RmBackFromSheet(instant: true); Drop(); return; }
        if (toMenu)
        {
            // 内容先没，面板缩回中心消失，菜单回来
            rs.Body.Opacity = 0;
            foreach (var v in new Visual[] { rs.Glow, rs.Line, rs.Line2, rs.Rim }) v.Opacity = 0;
            var easeIn = new Bezier(0.4f, 0f, 1f, 1f);
            rs.Sheet.StartAnimation("Scale", CsV3(new Vector3(0.3f, 0.3f, 1), 70, easeIn));
            rs.Sheet.StartAnimation("Opacity", CsS(0, 70, easeIn));
            After(60, () => RmBackFromSheet(instant: false));
            After(90, Drop);
            return;
        }
        rs.Root.StartAnimation("Opacity", CsS(0, 180, CsQuart));
        After(200, Drop);
    }

    // ---------------- 画 ----------------

    /// <summary>面板底（投影 + #FAFAFA 圆角面），打开时画一次。</summary>
    private void RsPaintSheet(RsState rs)
    {
        var dev = G.Device;
        float R = CsRaster, w = RsW + RsPad * 2, h = RsH + RsPad * 2;
        rs.SheetSurf = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        using (var ds = CanvasComposition.CreateDrawingSession(rs.SheetSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(RsPad - RsX, RsPad - RsY) * Matrix3x2.CreateScale(R);
            using (var shadow = new CanvasCommandList(dev))
            {
                using (var s = shadow.CreateDrawingSession()) { s.Transform = ds.Transform; s.FillRoundedRectangle(RsX, RsY + 12, RsW, RsH, RsR, RsR, Colors.Black); }
                using var blur = new GaussianBlurEffect { Source = shadow, BlurAmount = 22 * R, BorderMode = EffectBorderMode.Soft };
                using var op = new OpacityEffect { Source = blur, Opacity = 0.35f };
                var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(op); ds.Transform = t;
            }
            ds.FillRoundedRectangle(RsX, RsY, RsW, RsH, RsR, RsR, RsBg);
            if (CocoonPalette.Dark) DkEdge(ds, RsX, RsY, RsW, RsH, RsR);
        }
        ((CompositionSurfaceBrush)rs.Sheet.Brush).Surface = rs.SheetSurf;
        rs.Sheet.Size = new Vector2(w, h);
        rs.Sheet.Offset = new Vector3(RsX - RsPad, RsY - RsPad, 0);
        rs.Sheet.CenterPoint = new Vector3(RsPad + RsW / 2, RsPad + RsH / 2, 0);
    }

    /// <summary>整张面板一个尺寸的画布（骨架、自绘内容共用这个坐标）。</summary>
    private CompositionDrawingSurface RsFullSurf(ref CompositionDrawingSurface? surf, SpriteVisual v)
    {
        float R = CsRaster, w = RsW + RsPad * 2, h = RsH + RsPad * 2;
        surf ??= G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        ((CompositionSurfaceBrush)v.Brush).Surface = surf;
        v.Size = new Vector2(w, h);
        v.Offset = new Vector3(RsX - RsPad, RsY - RsPad, 0);
        v.CenterPoint = new Vector3(RsPad + RsW / 2, RsPad + RsH / 2, 0);
        return surf;
    }

    private Matrix3x2 RsFullXf => Matrix3x2.CreateTranslation(RsPad - RsX, RsPad - RsY) * Matrix3x2.CreateScale(CsRaster);

    private void RsPaint(RsState rs, bool animate = false)
    {
        var page = rs.Stack[^1];
        var oldRows = animate ? new Dictionary<string, float>(rs.RowYs) : null;
        var oldGroups = animate ? new Dictionary<string, (float Y0, float Y1)>(rs.GroupRects) : null;
        rs.Layout = page.Paint == null ? RsLayoutRows(page) : new();
        // 骨架：胶囊、页签；分组卡片和分隔线单独一层（跟着列表滚）
        using (var ds = CanvasComposition.CreateDrawingSession(RsFullSurf(ref rs.SkelSurf, rs.Skel)))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = RsFullXf;
            if (page.Paint == null && page.Top > 0)
            {
                // 列表滚上去以后，标题栏下面一条从面板底色渐隐到透明（滚上去的那行淡淡地压在标题下面，和 Cocoon 一样）
                using var clip = CanvasGeometry.CreateRoundedRectangle(ds.Device, RsX, RsY, RsW, RsH, RsR, RsR);
                using (ds.CreateLayer(1, clip))
                {
                    using var fade = new CanvasLinearGradientBrush(ds.Device, Color.FromArgb(170, RsBg.R, RsBg.G, RsBg.B), Color.FromArgb(0, RsBg.R, RsBg.G, RsBg.B)) { StartPoint = new Vector2(0, RsY), EndPoint = new Vector2(0, RsTop - 6) };
                    ds.FillRectangle(RsX, RsY, RsW, RsTop - 6 - RsY, fade);
                }
            }
            RsPaintPills(ds, page.YAction != null, page.NoY);
            page.PaintHeaderExtra?.Invoke(ds);
        }
        rs.Groups.IsVisible = false;
        RsPaintGroupVis(rs, page);
        rs.Rows.StopAnimation("Offset");
        rs.Rows.Offset = Vector3.Zero;
        RsPaintTitle(rs, page);
        // 自绘页的内容
        if (page.Paint != null)
        {
            using var ds = CanvasComposition.CreateDrawingSession(RsFullSurf(ref rs.CustomSurf, rs.Custom));
            ds.Clear(Colors.Transparent);
            ds.Transform = RsFullXf;
            page.Paint(ds);
            rs.Custom.IsVisible = true;
        }
        else rs.Custom.IsVisible = false;
        RsPaintRows(rs, page);
        RsPaintChevron(rs, page);
        if (oldRows != null && oldGroups != null) RsAnimateRelayout(rs, page, oldRows, oldGroups);
        RsUpdatePill(rs, page, animate);
    }

    /// <summary>
    /// 从 Top 起排：每行 136 + 4，遇到 Break 空 32 另起一张卡片；排到面板底为止（最后一行可以只露一截）。
    /// 选中行必须整行在 RsBottom 以内，不够就往下滚。
    /// </summary>
    private static List<(int I, float Y)> RsLayoutRows(RsPage page)
    {
        var rows = page.Rows;
        List<(int I, float Y)> Lay(int top) => RsLay(page, top);
        page.Top = Math.Clamp(page.Top, 0, Math.Max(0, rows.Count - 1));
        if (page.Sel < page.Top) page.Top = page.Sel;
        var lay = Lay(page.Top);
        while (page.Top < page.Sel && !lay.Any(e => e.I == page.Sel && e.Y + RsHi(page, e.I) <= RsBottom)) lay = Lay(++page.Top);
        // 上面还有行、下面又空着：往回滚，把空位补上
        while (page.Top > 0)
        {
            var prev = Lay(page.Top - 1);
            if (prev.Count > 0 && prev[^1].I == rows.Count - 1 && prev[^1].Y + RsHi(page, prev[^1].I) <= RsBottom && prev.Any(e => e.I == page.Sel)) { page.Top--; lay = prev; }
            else break;
        }
        return lay;
    }

    private float RsRowY(RsState rs, int i)
    {
        foreach (var (I, Y) in rs.Layout) if (I == i) return Y;
        return RsTop;
    }

    /// <summary>露在底边外的半行淡一些（Cocoon 列表底部那行是半透明的）。</summary>
    private static float RsRowOpacity(RsState rs, int k) => k >= rs.Layout.Count ? 1f : rs.Layout[k].Y < RsTop ? 0.4f : rs.Layout[k].Y + RsHi(rs.Stack[^1], rs.Layout[k].I) > RsBottom ? 0.45f : 1f;


    // 下拉框几何：右边白色胶囊（x 1340，宽 436，高 84，竖直居中在行的上 136 里），展开后下面接一张选项卡片，每项 96 高
    private const float RsPickX = 1340, RsPickW = 436, RsPickH = 84, RsOptH = 96;

    /// <summary>这一行的高度：展开着的下拉行要加上选项卡片的高度（Cocoon 行在原地变高，下面的行往下推）。</summary>
    private static float RsHi(RsPage page, int i) =>
        RsRowH + (page.Open == i && i < page.Rows.Count && page.Rows[i].Options is { Length: > 0 } o ? RsOptH * o.Length + 30 : 0);

    /// <summary>展开的下拉里第 k 个选项的方框（设计单位）。</summary>
    private static (float X, float Y, float W, float H) RsOptRect(float rowY, int k) =>
        (RsPickX, rowY + RsRowH / 2 + RsPickH / 2 + 14 + k * RsOptH, RsPickW, RsOptH);

    /// <summary>从 top 起的行位置；上面还有行时，把上一行也排在标题下面（滚上去的那行半透明地压在标题底下，和 Cocoon 一样）。</summary>
    private static List<(int I, float Y)> RsLay(RsPage page, int top)
    {
        var l = new List<(int I, float Y)>();
        var rows = page.Rows;
        if (top > 0 && top < rows.Count) l.Add((top - 1, RsTop - RsHi(page, top - 1) - RsSep - (rows[top].Break ? RsGap : 0)));
        float y = RsTop;
        for (int i = top; i < rows.Count; i++)
        {
            if (i > top && rows[i].Break) y += RsGap;
            if (y >= RsY + RsH - 8) break;
            l.Add((i, y));
            y += RsHi(page, i) + RsSep;
        }
        return l;
    }

    /// <summary>分组卡片：#F7F8FA 圆角面、上浅下深的描边、底部内阴影和下方一圈很淡的投影；组内行间 4 px 分隔线。</summary>
    /// <summary>这一页的分组：每组的上下边、组内分隔线、组里第一行的键（动画时用来对应前后两次排版）。</summary>
    private static List<(float Y0, float Y1, List<float> Seps, string Key)> RsGroupsOf(RsPage page, List<(int I, float Y)> lay)
    {
        var groups = new List<(float Y0, float Y1, List<float> Seps, string Key)>();
        for (int k = 0; k < lay.Count; k++)
        {
            var (i, y) = lay[k];
            if (k == 0 || page.Rows[i].Break) groups.Add((y, y + RsHi(page, i), new List<float>(), RsKey(page.Rows[i])));
            else { var g = groups[^1]; g.Seps.Add(y - RsSep); groups[^1] = (g.Y0, y + RsHi(page, i), g.Seps, g.Key); }
        }
        // 最后一组下面还有没排进来的同组行（或者最后一行只露一截）：卡片一直延到面板底外面，被圆角裁掉
        if (lay.Count > 0)
        {
            var last = lay[^1];
            if ((last.I < page.Rows.Count - 1 && !page.Rows[last.I + 1].Break) || last.Y + RsHi(page, last.I) > RsBottom)
                groups[^1] = (groups[^1].Y0, RsY + RsH + 60, groups[^1].Seps, groups[^1].Key);
        }
        return groups;
    }

    private static string RsKey(RsRow r) => $"{(int)r.Kind}|{r.Icon}|{r.Title}";

    /// <summary>
    /// 分组卡片一组一个精灵（在行容器最底下，跟着列表滚）：开关展开 / 收起子项、来源换位置时，
    /// 卡片能单独滑动、长高（裁剪从旧高度展开到新高度）。
    /// </summary>
    private void RsPaintGroupVis(RsState rs, RsPage page)
    {
        var groups = page.Paint == null ? RsGroupsOf(page, rs.Layout) : new();
        rs.GroupRects.Clear();
        float R = CsRaster, pad = 24, w = RsListW + pad * 2;
        while (rs.GroupVis.Count < groups.Count)
        {
            var v = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            rs.Rows.Children.InsertAtBottom(v);
            rs.GroupVis.Add(v);
            rs.GroupSurf.Add(null);
        }
        for (int k = 0; k < rs.GroupVis.Count; k++)
        {
            var v = rs.GroupVis[k];
            if (k >= groups.Count) { v.IsVisible = false; continue; }
            var (y0, y1, seps, key) = groups[k];
            rs.GroupRects[key] = (y0, y1);
            float hh = y1 - y0 + pad * 2;
            if (rs.GroupSurf[k] == null || MathF.Abs(v.Size.Y - hh) > 0.5f)
            {
                rs.GroupSurf[k]?.Dispose();
                rs.GroupSurf[k] = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(hh * R));
                ((CompositionSurfaceBrush)v.Brush).Surface = rs.GroupSurf[k];
                v.Size = new Vector2(w, hh);
            }
            v.IsVisible = true;
            v.StopAnimation("Offset");
            v.StopAnimation("Opacity");
            v.Opacity = 1;
            v.Clip = null;
            v.Offset = new Vector3(RsListX - pad, y0 - pad, 0);
            using var ds = CanvasComposition.CreateDrawingSession(rs.GroupSurf[k]!);
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - RsListX, pad - y0) * Matrix3x2.CreateScale(R);
            RsPaintGroup(ds, y0, y1, seps);
        }
    }

    /// <summary>一张分组卡片：#F7F8FA 圆角面、上浅下深的描边、底部内阴影和下方一圈很淡的投影；组内行间 4 px 分隔线。</summary>
    private void RsPaintGroup(CanvasDrawingSession ds, float y0, float y1, List<float> seps) => RsPaintGroupAt(ds, RsListX, RsListW, y0, y1, seps);

    /// <summary>同一张分组卡片，任意横向位置 / 宽度（右侧栏的功耗 / 选项卡片也用它）。</summary>
    private void RsPaintGroupAt(CanvasDrawingSession ds, float x, float w, float y0, float y1, List<float> seps, float sepH = RsSep)
    {
        var dev = ds.Device;
        float r = RsGroupR;
        if (CocoonPalette.Dark) { DkCard(ds, x, y0, w, y1 - y0, r, seps, sepH); return; }
        {
            float h = y1 - y0;
            using (var cl = new CanvasCommandList(dev))
            {
                using (var s = cl.CreateDrawingSession()) { s.Transform = ds.Transform; s.FillRoundedRectangle(x, y0 + 5, w, h, r, r, Colors.Black); }
                using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 6 * CsRaster, BorderMode = EffectBorderMode.Soft };
                using var op = new OpacityEffect { Source = blur, Opacity = 0.16f };
                var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(op); ds.Transform = t;
            }
            using var card = CanvasGeometry.CreateRoundedRectangle(dev, x, y0, w, h, r, r);
            ds.FillGeometry(card, RsGroup);
            using (ds.CreateLayer(1, card))
            {
                // 底边往里 26 px 微微压暗
                using var shade = new CanvasLinearGradientBrush(dev, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(14, 0, 0, 0)) { StartPoint = new Vector2(0, y1 - 26), EndPoint = new Vector2(0, y1) };
                ds.FillRectangle(x, y1 - 26, w, 26, shade);
                foreach (var sy in seps) ds.FillRectangle(x, sy, w, sepH, RsLine);
                ds.DrawLine(x + r, y0 + 1, x + w - r, y0 + 1, Ck(Color.FromArgb(255, 0xFF, 0xFF, 0xFF)), 1.5f);
            }
            using var edge = new CanvasLinearGradientBrush(dev, Ck(Color.FromArgb(255, 0xE8, 0xE9, 0xEC)), Ck(Color.FromArgb(255, 0xBC, 0xBD, 0xC1))) { StartPoint = new Vector2(0, y0), EndPoint = new Vector2(0, y1) };
            ds.DrawRoundedRectangle(x + 0.75f, y0 + 0.75f, w - 1.5f, h - 1.5f, r, r, edge, 1.5f);
        }
    }

    /// <summary>每一行一个精灵（入场时逐行淡入）；只画内容，底和分隔线在骨架里。</summary>
    private void RsPaintRows(RsState rs, RsPage page)
    {
        float R = CsRaster, pad = 12, w = RsListW + pad * 2, h = RsRowH + pad * 2;
        while (rs.RowVis.Count < rs.Layout.Count)
        {
            var v = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            v.Size = new Vector2(w, h);
            v.CenterPoint = new Vector3(w / 2, h / 2, 0);
            rs.Rows.Children.InsertAtTop(v);
            rs.RowVis.Add(v);
            var s = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
            ((CompositionSurfaceBrush)v.Brush).Surface = s;
            rs.RowSurf.Add(s);
        }
        rs.RowYs.Clear();
        for (int k = 0; k < rs.RowVis.Count; k++)
        {
            var v = rs.RowVis[k];
            if (k >= rs.Layout.Count) { v.IsVisible = false; continue; }
            var (i, y) = rs.Layout[k];
            rs.RowYs[RsKey(page.Rows[i])] = y;
            v.IsVisible = true;
            v.Offset = new Vector3(RsListX - pad, y - pad, 0);
            v.StopAnimation("Opacity");
            v.StopAnimation("Scale");
            v.Scale = Vector3.One;
            v.Opacity = RsRowOpacity(rs, k);
            // 展开的下拉行更高：画布按这一行的高度重新分配（选项卡片带投影，再多留一点）
            float hk = RsHi(page, i) + pad * 2 + (page.Open == i ? 24 : 0);
            if (Math.Abs(v.Size.Y - hk) > 0.5f)
            {
                rs.RowSurf[k].Dispose();
                rs.RowSurf[k] = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(hk * R));
                ((CompositionSurfaceBrush)v.Brush).Surface = rs.RowSurf[k];
                v.Size = new Vector2(w, hk);
                v.CenterPoint = new Vector3(w / 2, h / 2, 0);
            }
            var row = page.Rows[i];
            row.OpenSel = page.Open == i ? page.OptSel : -1;
            using var ds = CanvasComposition.CreateDrawingSession(rs.RowSurf[k]);
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - RsListX, pad - y) * Matrix3x2.CreateScale(R);
            RsPaintRow(ds, row, y, rs.Item);
        }
    }

    /// <summary>下面还有：紫色向下小箭头（白描边），在面板底边中间。</summary>
    private void RsPaintChevron(RsState rs, RsPage page)
    {
        bool more = rs.Layout.Count > 0 && (rs.Layout[^1].I < page.Rows.Count - 1 || rs.Layout[^1].Y + RsHi(page, rs.Layout[^1].I) > RsBottom);
        rs.Chev.IsVisible = more;
        if (!more) return;
        float R = CsRaster, w = 60, h = 40;
        if (rs.ChevSurf == null)
        {
            rs.ChevSurf = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
            using var ds = CanvasComposition.CreateDrawingSession(rs.ChevSurf);
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            float cx = w / 2, cy = h / 2;
            using var pb = new CanvasPathBuilder(ds.Device);
            pb.BeginFigure(cx - 16, cy - 7); pb.AddLine(cx, cy + 7); pb.AddLine(cx + 16, cy - 7);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var chev = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(chev, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 13, RwRoundStroke);
            using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(cx, cy - 8), EndPoint = new Vector2(cx, cy + 8) };
            ds.DrawGeometry(chev, br, 7, RwRoundStroke);
            ((CompositionSurfaceBrush)rs.Chev.Brush).Surface = rs.ChevSurf;
            rs.Chev.Size = new Vector2(w, h);
        }
        rs.Chev.Offset = new Vector3(RsX + RsW / 2 - w / 2, RsY + RsH - 64, 0);
    }

    /// <summary>左上「B ←」灰胶囊、右上「Y ✓」紫胶囊（带一圈紫色投影）。</summary>
    private void RsPaintPills(CanvasDrawingSession ds, bool play = false, bool noY = false)
    {
        var dev = ds.Device;
        ds.FillRoundedRectangle(134, 104, 134, 74, 37, 37, Ck(Color.FromArgb(255, 0xE2, 0xE3, 0xE7)));
        ds.FillCircle(170, 141, 21, Ck(Color.FromArgb(255, 0x9A, 0x9C, 0xA3)));
        using (var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText("B", new Windows.Foundation.Rect(149, 120, 42, 42), Color.FromArgb(255, 0xF2, 0xF2, 0xF4), f);
        using (var pb = new CanvasPathBuilder(dev))
        {
            pb.BeginFigure(248, 141); pb.AddLine(212, 141); pb.EndFigure(CanvasFigureLoop.Open);
            pb.BeginFigure(228, 125); pb.AddLine(212, 141); pb.AddLine(228, 157); pb.EndFigure(CanvasFigureLoop.Open);
            using var arrow = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(arrow, RmInk, 7, RwRoundStroke);
        }
        if (noY) return;
        using (var cl = new CanvasCommandList(dev))
        {
            using (var s = cl.CreateDrawingSession()) { s.Transform = ds.Transform; s.FillRoundedRectangle(1656, 112, 126, 70, 35, 35, CpPillEnd); }
            using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 9 * CsRaster, BorderMode = EffectBorderMode.Soft };
            using var op = new OpacityEffect { Source = blur, Opacity = 0.45f };
            var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(op); ds.Transform = t;
        }
        using (var br = new CanvasLinearGradientBrush(dev, CpPillStart, CpPillEnd) { StartPoint = new Vector2(1652, 104), EndPoint = new Vector2(1786, 178) })
            ds.FillRoundedRectangle(1652, 104, 134, 74, 37, 37, br);
        ds.FillCircle(1689, 141, 21, CpPillBadge);
        using (var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText("Y", new Windows.Foundation.Rect(1668, 120, 42, 42), CpPillInk, f);
        if (play)
        {
            // Y ▶：白色实心三角（Cocoon 刮削页右上是「开始」）
            using var pb = new CanvasPathBuilder(dev);
            pb.BeginFigure(1740, 124); pb.AddLine(1766, 141); pb.AddLine(1740, 158); pb.EndFigure(CanvasFigureLoop.Closed);
            using var tri = CanvasGeometry.CreatePath(pb);
            ds.FillGeometry(tri, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
            ds.DrawGeometry(tri, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 4, RwRoundStroke);
            return;
        }
        ds.FillCircle(1750, 141, 21, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
        using (var pb = new CanvasPathBuilder(dev))
        {
            pb.BeginFigure(1740, 141); pb.AddLine(1747, 148); pb.AddLine(1760, 134); pb.EndFigure(CanvasFigureLoop.Open);
            using var tick = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(tick, CpPillInk, 5, RwRoundStroke);
        }
    }

    /// <summary>页面标题（单独一层：入场时比骨架晚出来）。</summary>
    private void RsPaintTitle(RsState rs, RsPage page)
    {
        float R = CsRaster, x0 = 280, y0 = 96, w = 1000, h = 90;
        rs.TitleSurf ??= G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        using (var ds = CanvasComposition.CreateDrawingSession(rs.TitleSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(R);
            float tx = 288;
            if (page.TitleIcon.Length > 0) { RsGlyph(ds, page.TitleIcon, 314, 141, RmInk, 0.8f); tx = 362; }
            using var f = new CanvasTextFormat { FontFamily = RmFont(page.Title), FontSize = 48, FontWeight = RsWt(page.Title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(page.Title, new Windows.Foundation.Rect(tx, 104, 900, 74), RmInk, f);
        }
        ((CompositionSurfaceBrush)rs.Title.Brush).Surface = rs.TitleSurf;
        rs.Title.Size = new Vector2(w, h);
        rs.Title.Offset = new Vector3(x0, y0, 0);
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
            using var lf = new CanvasTextFormat { FontFamily = RmFont(row.Title), FontSize = 30, FontWeight = RsWt(row.Title), WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(row.Title, textX, y + 20, RsSub, lf);
            string v = string.IsNullOrWhiteSpace(row.Value) ? "未设置" : row.Value!.Replace('\n', ' ');
            using var vf = new CanvasTextFormat { FontFamily = RmFont(v), FontSize = 40, FontWeight = RsWt(v), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(v, new Windows.Foundation.Rect(textX, y + 62, 1520 - textX, 60), string.IsNullOrWhiteSpace(row.Value) ? Ck(Color.FromArgb(255, 0x9A, 0x9C, 0xA3)) : ink, vf);
            if (row.Kind == RsKind.Field) RsGlyph(ds, row.Key == "released" ? "calendar" : "pencil", 1748, cy, RmInk, 0.8f);
            return;
        }
        if (row.Sub is { Length: > 0 } sub)
        {
            // 两行：标题在上，灰色说明在下（Cocoon「Scrape type / Fill missing basics, …」）；右边控件让出位置
            float right = row.Kind switch { RsKind.Pick => 1320, RsKind.Order => 1440, RsKind.Toggle => 1630, _ => 1700 };
            using (var tf = new CanvasTextFormat { FontFamily = RmFont(row.Title), FontSize = 38, FontWeight = RsWt(row.Title), WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(row.Title, new Windows.Foundation.Rect(textX, y + 18, right - textX, 56), ink, tf);
            using (var sf = new CanvasTextFormat { FontFamily = RmFont(sub), FontSize = 27, FontWeight = RsWt(sub, 500), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                ds.DrawText(sub, new Windows.Foundation.Rect(textX, y + 78, right - textX, 42), RsSub, sf);
        }
        else
        {
            using var tf = new CanvasTextFormat { FontFamily = RmFont(row.Title), FontSize = 40, FontWeight = RsWt(row.Title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(row.Title, new Windows.Foundation.Rect(textX, y, 1300, RsRowH), ink, tf);
        }
        if (row.Kind == RsKind.Order)
        {
            // 来源优先级：灰色胶囊里「↓ ↑」（到头的那边变淡），右边开关（Cocoon 的刮削来源行）
            float px = 1468, pw = 164, ph = 84, py = cy - ph / 2;
            if (CocoonPalette.Dark) DkWell(ds, px, py, pw, ph, ph / 2); else ds.FillRoundedRectangle(px, py, pw, ph, ph / 2, ph / 2, Color.FromArgb(255, 0xE4, 0xE5, 0xE9));
            void Chev(float x, bool up, bool on)
            {
                using var pb = new CanvasPathBuilder(ds.Device);
                float d = up ? -1 : 1;
                pb.BeginFigure(x - 13, cy - 6 * d); pb.AddLine(x, cy + 7 * d); pb.AddLine(x + 13, cy - 6 * d);
                pb.EndFigure(CanvasFigureLoop.Open);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(g, on ? RmInk : Ck(Color.FromArgb(255, 0xB4, 0xB6, 0xBC)), 7, RwRoundStroke);
            }
            Chev(px + 42, false, row.CanDown);
            Chev(px + pw - 42, true, row.CanUp);
        }
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
            case RsKind.Order:
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
                    CpToggleOffTrack(ds, x, ty, 120, 76, 4);
                }
                RsSoftShadow(ds, (on ? x + 82 : x + 38) - 27, cy - 27 + 3, 54, 54, 27, 4, 0.22f);
                ds.FillCircle(on ? x + 82 : x + 38, cy, 27, CpKnobFill(on));
                break;
            }
            case RsKind.Pick:
            {
                // 下拉框：白色胶囊、一圈很淡的投影和细描边，当前值、右边小三角（Cocoon 的「Android (None) ▾」）
                bool open = row.OpenSel >= 0 && row.Options is { Length: > 0 };
                RsPaintPickPill(ds, 1340, cy - 42, 436, 84, row.Value ?? "", open, 36);
                if (open) RsPaintOptions(ds, row, y);
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

    /// <summary>
    /// 展开的下拉：胶囊下面一张白色圆角卡片（投影、细描边），一项一行，行间细线；
    /// 单选：当前值用强调色字；多选：右边一个勾（选中是紫色渐变圆 + 白勾，没选是灰圈），焦点那一行垫淡紫底（照 Cocoon 视频）。
    /// </summary>
    /// <summary>下拉框的胶囊：白底、很淡的投影和细描边，当前值，右边小三角（展开时朝上）。右侧栏的选项行也用这一个。</summary>
    private void RsPaintPickPill(CanvasDrawingSession ds, float px, float py, float pw, float ph, string val, bool open, float fontSize, bool text = true)
    {
        float cy = py + ph / 2, k = ph / 84;
        if (CocoonPalette.Dark) DkRaised(ds, px, py, pw, ph, ph / 2);   // 亮色是带投影的白胶囊（凸起），暗色同样凸起
        else
        {
            RsSoftShadow(ds, px, py + 3 * k, pw, ph, ph / 2, 6 * k, 0.14f);
            ds.FillRoundedRectangle(px, py, pw, ph, ph / 2, ph / 2, Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
            ds.DrawRoundedRectangle(px + 0.5f, py + 0.5f, pw - 1, ph - 1, ph / 2, ph / 2, Color.FromArgb(255, 0xEC, 0xED, 0xF0), 1);
        }
        if (text) RsPaintPickText(ds, px, py, pw, ph, val, fontSize);
        float tx = px + pw - 43 * k, a = 7 * k, b = 4.5f * k;
        using var pb = new CanvasPathBuilder(ds.Device);
        if (open) { pb.BeginFigure(tx - a, cy + b); pb.AddLine(tx + a, cy + b); pb.AddLine(tx, cy - b); }   // 展开时三角朝上
        else { pb.BeginFigure(tx - a, cy - b); pb.AddLine(tx + a, cy - b); pb.AddLine(tx, cy + b); }
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var tri = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(tri, open ? RmInk : RsSub);
        ds.DrawGeometry(tri, open ? RmInk : RsSub, 3 * k, RwRoundStroke);
    }

    private static void RsPaintPickText(CanvasDrawingSession ds, float px, float py, float pw, float ph, string val, float fontSize)
    {
        float k = ph / 84;
        using var vf = new CanvasTextFormat { FontFamily = RmFont(val), FontSize = fontSize, FontWeight = RsWt(val), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
        ds.DrawText(val, new Windows.Foundation.Rect(px + 30 * k, py, pw - 90 * k, ph), RmInk, vf);
    }

    private void RsPaintOptions(CanvasDrawingSession ds, RsRow row, float y)
    {
        var (x0, y0, w, _) = RsOptRect(y, 0);
        RsPaintOptionCard(ds, row, x0, y0, w, RsOptH, 36);
    }

    /// <summary>展开的选项卡片（任意位置 / 行高 / 字号）：二级页和右侧栏共用。</summary>
    private void RsPaintOptionCard(CanvasDrawingSession ds, RsRow row, float x0, float y0, float w, float optH, float fontSize)
    {
        var opts = row.Options!;
        float h = opts.Length * optH, cr = MathF.Min(30, optH * 0.4f);
        using var card = CanvasGeometry.CreateRoundedRectangle(ds.Device, x0, y0, w, h, cr, cr);
        if (CocoonPalette.Dark) DkPop(ds, x0, y0, w, h, cr);
        else { RsSoftShadow(ds, x0, y0 + 5, w, h, cr, 8, 0.18f); ds.FillGeometry(card, Color.FromArgb(255, 0xFC, 0xFC, 0xFD)); }
        int cur = row.Current?.Invoke() ?? -1;
        using (ds.CreateLayer(1, card))
            for (int k = 0; k < opts.Length; k++)
            {
                float oy = y0 + k * optH;
                if (row.Multi && k == row.OpenSel) ds.FillRectangle(x0, oy, w, optH, CpSelectionTint);
                if (k > 0) ds.FillRectangle(x0 + 28, oy - 1, w - 56, 2, Ck(Color.FromArgb(255, 0xE6, 0xE7, 0xEA)));
                bool sel = !row.Multi && k == cur;
                using var tf = new CanvasTextFormat { FontFamily = RmFont(opts[k]), FontSize = fontSize, FontWeight = RsWt(opts[k]), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
                ds.DrawText(opts[k], new Windows.Foundation.Rect(x0 + 30 * fontSize / 36, oy, w - (row.Multi ? 110 : 60), optH), sel ? CocoonPalette.Current.SelectedOption : RmInk, tf);
                if (row.Multi)
                {
                    float cx = x0 + w - 52, ccy = oy + optH / 2;
                    if (row.Checked?.Invoke(k) == true)
                    {
                        RsSoftShadow(ds, cx - 21, ccy - 21 + 2, 42, 42, 21, 3, 0.2f);
                        using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(cx - 21, ccy - 21), EndPoint = new Vector2(cx + 21, ccy + 21) };
                        ds.FillCircle(cx, ccy, 21, br);
                        ds.DrawCircle(cx, ccy, 20, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 2.5f);
                        using var pb = new CanvasPathBuilder(ds.Device);
                        pb.BeginFigure(cx - 9, ccy); pb.AddLine(cx - 2.5f, ccy + 7); pb.AddLine(cx + 10, ccy - 7); pb.EndFigure(CanvasFigureLoop.Open);
                        using var tick = CanvasGeometry.CreatePath(pb);
                        ds.DrawGeometry(tick, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 4.5f, RwRoundStroke);
                    }
                    else ds.DrawCircle(cx, ccy, 19, Ck(Color.FromArgb(255, 0xB4, 0xB6, 0xBC)), 3.5f);
                }
            }
        ds.DrawRoundedRectangle(x0 + 0.5f, y0 + 0.5f, w - 1, h - 1, cr, cr, Ck(Color.FromArgb(255, 0xE6, 0xE7, 0xEA)), 1);
    }

    /// <summary>一圈很淡的投影（下拉框胶囊、开关圆钮）。</summary>
    private void RsSoftShadow(CanvasDrawingSession ds, float x, float y, float w, float h, float r, float blur, float alpha)
    {
        using var cl = new CanvasCommandList(ds.Device);
        using (var s = cl.CreateDrawingSession()) { s.Transform = ds.Transform; s.FillRoundedRectangle(x, y, w, h, r, r, Colors.Black); }
        using var bl = new GaussianBlurEffect { Source = cl, BlurAmount = blur * CsRaster, BorderMode = EffectBorderMode.Soft };
        using var op = new OpacityEffect { Source = bl, Opacity = alpha };
        var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(op); ds.Transform = t;
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
                ds.FillCircle(cx, cy - 14, 5, Ck(Color.FromArgb(255, 0xFA, 0xFA, 0xFA)));
                ds.FillRoundedRectangle(cx - 4.5f, cy - 4, 9, 22, 4.5f, 4.5f, Ck(Color.FromArgb(255, 0xFA, 0xFA, 0xFA)));
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
                ds.FillCircle(cx + 14, cy - 15, 3.5f, Ck(Color.FromArgb(255, 0xFA, 0xFA, 0xFA)));
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
                ds.FillRectangle(cx - 21, cy - 1, 14, 4, Ck(Color.FromArgb(255, 0xFA, 0xFA, 0xFA)));
                ds.FillRectangle(cx - 16, cy - 6, 4, 14, Ck(Color.FromArgb(255, 0xFA, 0xFA, 0xFA)));
                ds.FillCircle(cx + 14, cy - 3, 3.5f, Ck(Color.FromArgb(255, 0xFA, 0xFA, 0xFA)));
                ds.FillCircle(cx + 21, cy + 4, 3.5f, Ck(Color.FromArgb(255, 0xFA, 0xFA, 0xFA)));
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
            case "scrape":
            {
                // 魔法棒：斜着的一根棒 + 右上一颗四角星、两粒小星（自动去网上把资料和图「变」出来）
                ds.DrawLine(cx - 26, cy + 26, cx + 6, cy - 6, ink, 9, RwRoundStroke);
                void Star(float x, float y, float r)
                {
                    using var pb = new CanvasPathBuilder(dev);
                    pb.BeginFigure(x, y - r);
                    pb.AddQuadraticBezier(new Vector2(x, y), new Vector2(x + r, y));
                    pb.AddQuadraticBezier(new Vector2(x, y), new Vector2(x, y + r));
                    pb.AddQuadraticBezier(new Vector2(x, y), new Vector2(x - r, y));
                    pb.AddQuadraticBezier(new Vector2(x, y), new Vector2(x, y - r));
                    pb.EndFigure(CanvasFigureLoop.Closed);
                    using var g = CanvasGeometry.CreatePath(pb);
                    ds.FillGeometry(g, ink);
                }
                Star(cx + 16, cy - 16, 17);
                Star(cx - 12, cy - 22, 7);
                Star(cx + 24, cy + 14, 6);
                break;
            }
            case "filter":
                // 三条由长到短的圆头横杠（Cocoon「Scrape type / Types of …」）
                ds.FillRoundedRectangle(cx - 28, cy - 24, 56, 13, 6.5f, 6.5f, ink);
                ds.FillRoundedRectangle(cx - 19, cy - 5, 38, 13, 6.5f, 6.5f, ink);
                ds.FillRoundedRectangle(cx - 10, cy + 14, 20, 13, 6.5f, 6.5f, ink);
                break;
            case "globe":
                ds.DrawCircle(cx, cy, 27, ink, 6);
                ds.DrawEllipse(cx, cy, 11, 27, ink, 5);
                ds.DrawLine(cx - 27, cy, cx + 27, cy, ink, 5);
                break;
            case "sliders":
                foreach (var (ly, kx) in new[] { (-18f, 10f), (0f, -12f), (18f, 6f) })
                {
                    ds.DrawLine(cx - 28, cy + ly, cx + 28, cy + ly, ink, 6, RwRoundStroke);
                    ds.FillCircle(cx + kx, cy + ly, 8, ink);
                }
                break;
            case "abc":
            {
                using var f = new CanvasTextFormat { FontFamily = RwPoppinsXb, FontSize = 30, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
                ds.DrawText("ABC", new Windows.Foundation.Rect(cx - 50, cy - 30, 100, 60), ink, f);
                break;
            }
            case "steam":
                // Steam 风格：大圆 + 连杆 + 小圆
                ds.DrawCircle(cx + 8, cy - 8, 15, ink, 6);
                ds.FillCircle(cx + 8, cy - 8, 6, ink);
                ds.DrawLine(cx - 22, cy + 18, cx - 2, cy + 4, ink, 8, RwRoundStroke);
                ds.FillCircle(cx - 20, cy + 16, 9, ink);
                break;
            case "cube":
            {
                // 立方体（LaunchBox 的骰子图标）：正六边形轮廓 + 三条棱
                using var pb = new CanvasPathBuilder(dev);
                for (int i = 0; i < 6; i++)
                {
                    float a = MathF.PI / 3 * i - MathF.PI / 2;
                    var pt = new Vector2(cx + 28 * MathF.Cos(a), cy + 28 * MathF.Sin(a));
                    if (i == 0) pb.BeginFigure(pt); else pb.AddLine(pt);
                }
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var hex = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(hex, ink);
                var light = Ck(Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                ds.DrawLine(cx, cy, cx, cy + 26, light, 3.5f);
                ds.DrawLine(cx, cy, cx - 23, cy - 13, light, 3.5f);
                ds.DrawLine(cx, cy, cx + 23, cy - 13, light, 3.5f);
                break;
            }
            case "igdb":
            {
                ds.DrawRoundedRectangle(cx - 33, cy - 17, 66, 34, 6, 6, ink, 5);
                using var f = new CanvasTextFormat { FontFamily = RwPoppinsXb, FontSize = 17, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
                ds.DrawText("IGDB", new Windows.Foundation.Rect(cx - 33, cy - 17, 66, 34), ink, f);
                break;
            }
            case "stack":
                // 叠在一起的三张卡（SteamGridDB）
                ds.DrawRoundedRectangle(cx - 30, cy - 24, 44, 30, 6, 6, ink, 4);
                ds.DrawRoundedRectangle(cx - 22, cy - 15, 44, 30, 6, 6, ink, 4);
                ds.FillRoundedRectangle(cx - 14, cy - 6, 44, 30, 6, 6, ink);
                break;
            case "clock":
                ds.DrawCircle(cx, cy, 28, ink, 6);
                ds.DrawLine(cx, cy, cx, cy - 15, ink, 6, RwRoundStroke);
                ds.DrawLine(cx, cy, cx + 12, cy + 6, ink, 6, RwRoundStroke);
                break;
        }
        ds.Transform = t;
    }

    // ---------------- 焦点 ----------------

    private static (float W, float H, float R, float Leg) RsBracketGeom(bool option = false)
    {
        // 整行：和分组卡片同心（卡片圆角 + 外扩）；下拉选项：和 24 圆角的选项卡片同心。几何走全局 CocoonBracketPath
        float w = option ? RsPickW : RsListW, h = option ? RsOptH : RsRowH, o = CurOutsetFor(w, h);
        float W = w + o * 2f, H = h + o * 2f;
        float r = (option ? 24f : RsGroupR) + o;
        return (W, H, r, 0f);
    }

    private void RsPaintCursor(RsState rs, bool option = false)
    {
        var dev = G.Device;
        float R = CsRaster;
        var (W, H, r, _) = RsBracketGeom(option);
        rs.CurW = W; rs.CurH = H;
        float sw = W + RmCurPad * 2, sh = H + RmCurPad * 2;
        int pw = (int)Math.Ceiling(sw * R), ph = (int)Math.Ceiling(sh * R);
        using var path = CocoonBracketPath(dev, W, H, r, RmCurPad);
        foreach (var s in new[] { rs.GlowSurf, rs.RimSurf, rs.LineSurf }) s?.Dispose();
        rs.GlowSurf = G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rs.GlowSurf))
        {
            ds.Clear(Colors.Transparent);
            using var cl = new CanvasCommandList(ds);
            using (var s = cl.CreateDrawingSession()) { s.Transform = Matrix3x2.CreateScale(R); s.DrawGeometry(path, CurGlowColor, RwSw * 2, RwRoundStroke); }
            using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = CurGlowBlurDp * RwDp * R / 2, BorderMode = EffectBorderMode.Soft };
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
        // 流光沿整圈周长走：左右两半各一个竖向渐变，颜色按各自在周长上的位置随时间流动（宽框不再整块左右换色）
        rs.Line.Brush = RsPerimFlow(rs.LineSurf, W, H, r, right: false);
        rs.Line2.Brush = RsPerimFlow(rs.LineSurf, W, H, r, right: true);
        rs.Line.Clip = G.C.CreateInsetClip(0, 0, sw / 2, 0);
        rs.Line2.Clip = G.C.CreateInsetClip(sw / 2, 0, 0, 0);
        foreach (var v in new Visual[] { rs.Glow, rs.Line, rs.Line2, rs.Rim }) { v.Size = new Vector2(sw, sh); v.CenterPoint = new Vector3(sw / 2, sh / 2, 0); }
    }

    /// <summary>
    /// 焦点框到当前行；下拉展开时到当前选项。尺寸变了（整行 ↔ 选项）就按新尺寸重画，
    /// 从旧的大小和位置弹簧缩 / 放过去（Cocoon：按 A 展开时括号从整行收到选项上）。
    /// </summary>
    private void RsCursorTo(RsState rs, bool move)
    {
        var page = rs.Stack[^1];
        bool option = page.Open >= 0;
        var (W, H, _, _) = RsBracketGeom(option);
        Vector2 center;
        if (option)
        {
            var (ox, oy, ow, oh) = RsOptRect(RsRowY(rs, page.Open), page.OptSel);
            center = new Vector2(ox + ow / 2, oy + oh / 2);
        }
        else center = new Vector2(RsListX + RsListW / 2, RsRowY(rs, page.Sel) + RsRowH / 2);
        float sw = W + RmCurPad * 2, sh = H + RmCurPad * 2;
        var to = new Vector3(center.X - sw / 2, center.Y - sh / 2, 0);
        bool show = page.Rows.Count > 0 && page.Paint == null;
        if (MathF.Abs(rs.CurW - W) > 0.5f || MathF.Abs(rs.CurH - H) > 0.5f)
        {
            float ow = rs.CurW, oh = rs.CurH;
            var oldCenter = new Vector3(rs.Glow.Offset.X + (ow + RmCurPad * 2) / 2, rs.Glow.Offset.Y + (oh + RmCurPad * 2) / 2, 0);
            RsPaintCursor(rs, option);
            foreach (var v in new Visual[] { rs.Glow, rs.Line, rs.Line2, rs.Rim })
            {
                v.StopAnimation("Offset");
                v.Offset = oldCenter - new Vector3(sw / 2, sh / 2, 0);
                if (move && !Reduced && ow > 0)
                {
                    v.Scale = new Vector3((ow + RmCurPad * 2) / sw, (oh + RmCurPad * 2) / sh, 1);
                    v.StartAnimation("Scale", RwSpring(Vector3.One));
                }
                else v.Scale = Vector3.One;
            }
        }
        bool morphing = MathF.Abs(rs.Glow.Scale.X - 1) > 0.001f;
        float dy = to.Y - rs.Glow.Offset.Y;
        foreach (var v in new Visual[] { rs.Glow, rs.Line, rs.Line2, rs.Rim })
        {
            if (move && !Reduced) v.StartAnimation("Offset", RwSpring(to)); else RwJump(v, to);
            if (move && !Reduced && !morphing) RsStretch(v, dy, H);
            v.Opacity = show ? 1 : 0;
        }
    }

    /// <summary>
    /// 行间移动时焦点框先拉长再缩回（Cocoon：括号像橡皮筋一样被拽过去）：竖向放大到 1 + 行程 / 高度 × 0.35（最多 1.45），
    /// 约 70 ms 到最长、220 ms 回到原样；锚点在中心，和位置弹簧一起走。
    /// </summary>
    private void RsStretch(Visual v, float dy, float h)
    {
        if (MathF.Abs(dy) < 1 || h <= 0) return;
        float k = 1 + MathF.Min(0.45f, MathF.Abs(dy) / h * 0.35f);
        var a = G.C.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, Vector3.One, G.Linear);
        a.InsertKeyFrame(0.3f, new Vector3(1, k, 1), G.Ease(new Bezier(0.3f, 0f, 0.4f, 1f)));
        a.InsertKeyFrame(1, Vector3.One, G.Ease(new Bezier(0.22f, 1f, 0.36f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(230);
        v.StartAnimation("Scale", a);
    }

    // ---------------- 输入 ----------------

    private bool RsActive => _rs != null;

    private void RsMove(Direction dir)
    {
        if (_rs is not { } rs || _rsEditing != null) return;
        var page = rs.Stack[^1];
        if (page.Move != null) { if (page.Move(dir)) { RsPaint(rs); CuePan(0); } else Cue(Sfx.Bump); return; }
        if (page.Open >= 0)
        {
            // 下拉展开着：上下在选项里走，左右不动
            var orow = page.Rows[page.Open];
            int n = orow.Options?.Length ?? 0;
            int to = page.OptSel + (dir == Direction.Down ? 1 : dir == Direction.Up ? -1 : 0);
            if (dir is Direction.Left or Direction.Right || to < 0 || to >= n) { Cue(Sfx.Bump); return; }
            page.OptSel = to;
            CuePan(0);
            if (orow.Multi) RsPaint(rs);   // 多选：焦点那一项垫淡紫底，要重画
            RsCursorTo(rs, move: true);
            return;
        }
        if (dir is Direction.Left or Direction.Right)
        {
            if (page.Sel < page.Rows.Count && page.Rows[page.Sel] is { Kind: RsKind.Pick, Step: null, Multi: false, Options: { Length: > 1 } cyc, Choose: { } ch } crow)
            {
                Cue(Sfx.Nav, pan: dir == Direction.Left ? -0.4f : 0.4f);
                ch(((crow.Current?.Invoke() ?? 0) + (dir == Direction.Left ? -1 : 1) + cyc.Length) % cyc.Length);
                if (_rs == rs) { page.Rows = page.Build(); RsPaint(rs); }
                return;
            }
            if (page.Sel < page.Rows.Count && page.Rows[page.Sel] is { Step: { } step } srow && srow.Kind is RsKind.Pick or RsKind.Toggle or RsKind.Order)
            {
                // 来源排序：已经是第一（→）或最后（←）就挪不动——只给一声碰壁，不重画、不动画
                if (srow.Kind == RsKind.Order && (dir == Direction.Right ? !srow.CanUp : !srow.CanDown)) { Cue(Sfx.Bump); return; }
                Cue(Sfx.Nav, pan: dir == Direction.Left ? -0.4f : 0.4f);
                bool wasOn = srow.IsOn?.Invoke() == true;
                step(dir == Direction.Left ? -1 : 1);
                if (_rs == rs)
                {
                    page.Rows = page.Build();
                    RsLayoutRows(page);
                    RsPaint(rs, animate: true);   // 来源换位置：两行互相滑过去，焦点框跟着走
                    RsCursorTo(rs, move: true);
                    if (srow.Kind == RsKind.Toggle && srow.IsOn?.Invoke() is bool nowOn && nowOn != wasOn) RsToggleFx(rs, RsRowY(rs, page.Sel), nowOn);
                }
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
        RsLayoutRows(page);
        if (page.Top != top)
        {
            // 滚动：按新位置重画，整个列表从旧位置弹簧滑过来；焦点框同一根弹簧，和列表一起走
            float before = float.NaN;
            foreach (var e in RsLay(page, top)) if (e.I == page.Top) before = e.Y;
            float delta = float.IsNaN(before) ? (top - page.Top) * (RsRowH + RsSep) * -1 : before - RsTop;
            RsPaint(rs);
            if (!Reduced && MathF.Abs(delta) > 0.5f)
            {
                rs.Rows.Offset = new Vector3(0, delta, 0);
                rs.Rows.StartAnimation("Offset", RwSpring(Vector3.Zero));
                RsCursorTo(rs, move: true);
            }
            else RsCursorTo(rs, move: false);
        }
        else { RsCursorTo(rs, move: true); RsUpdatePill(rs, page, animate: true); }
    }

    private void RsActivate()
    {
        if (_rs is not { } rs) return;
        if (_rsEditing != null) { RsEndEdit(commit: true); return; }
        var page = rs.Stack[^1];
        if (page.Activate != null) { page.Activate(); if (_rs == rs && rs.Stack.Count > 0 && rs.Stack[^1] == page) RsPaint(rs); return; }
        if (page.Open >= 0)
        {
            // 下拉展开着：单选选中后收起；多选勾 / 取消，留着
            var orow = page.Rows[page.Open];
            if (orow.Multi)
            {
                Cue(orow.Checked?.Invoke(page.OptSel) == true ? Sfx.ToggleOff : Sfx.ToggleOn);
                orow.ToggleOpt?.Invoke(page.OptSel);
                page.Rows = page.Build();
                RsPaint(rs);
                return;
            }
            Cue(Sfx.Positive);
            orow.Choose?.Invoke(page.OptSel);
            RsCloseDrop(rs, page);
            return;
        }
        if (page.Sel < page.Rows.Count && page.Rows[page.Sel] is { Kind: RsKind.Pick, Options: { Length: > 0 } } prow && prow.Press == null)
        {
            // 下拉框：在原地展开（行变高，下面的行往下推），焦点框从整行缩到当前选项
            Cue(Sfx.Activate);
            page.Open = page.Sel;
            page.OptSel = prow.Multi ? 0 : Math.Clamp(prow.Current?.Invoke() ?? 0, 0, prow.Options!.Length - 1);
            RsLayoutRows(page);
            RsPaint(rs, animate: true);
            RsCursorTo(rs, move: true);
            return;
        }
        if (page.Sel >= page.Rows.Count) return;
        var row = page.Rows[page.Sel];
        switch (row.Kind)
        {
            case RsKind.Field: Cue(Sfx.Activate); RsBeginEdit(rs, row); return;
            case RsKind.Info: Cue(Sfx.Bump); return;
            default:
                Cue(row.Kind is RsKind.Toggle or RsKind.Order ? Sfx.ToggleOn : Sfx.Activate);
                bool wasOn = row.IsOn?.Invoke() == true;
                row.Press?.Invoke();
                if (_rs == rs && rs.Stack.Count > 0 && rs.Stack[^1] == page)
                {
                    page.Rows = page.Build();
                    RsPaint(rs, animate: true);   // 开关管着的子项展开 / 收起：下面的行滑开、新行淡入
                    RsCursorTo(rs, move: true);
                    if (row.Kind is RsKind.Toggle or RsKind.Order && row.IsOn?.Invoke() is bool nowOn && nowOn != wasOn) RsToggleFx(rs, RsRowY(rs, page.Sel), nowOn);
                }
                return;
        }
    }


    /// <summary>收起下拉：行变回原高度，焦点框从选项弹回整行。</summary>
    private void RsCloseDrop(RsState rs, RsPage page)
    {
        page.Open = -1;
        if (_rs != rs || rs.Stack.Count == 0 || rs.Stack[^1] != page) return;
        page.Rows = page.Build();
        RsLayoutRows(page);
        RsPaint(rs, animate: true);
        RsCursorTo(rs, move: true);
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
        if (page.Open >= 0) { RsCloseDrop(rs, page); return; }
        if (page.YAction != null) { page.YAction(); return; }
        page.Save?.Invoke();
        Cue(Sfx.Positive);
        RsPop();
    }

    /// <summary>B：有没保存的改动时先提示一次，再按才放弃。</summary>
    private bool RsBack()
    {
        if (_rs is not { } rs) return false;
        if (_rsEditing != null) { RsEndEdit(commit: false); return true; }
        if (rs.Stack[^1] is { Open: >= 0 } openPage) { Cue(Sfx.Back); RsCloseDrop(rs, openPage); return true; }
        var page = rs.Stack[^1];
        if (page.Lock?.Invoke() == true) { Cue(Sfx.Bump); return true; }
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

    // 文字输入（照 Cocoon 视频）：面板和列表退下去，屏幕上半出现一张输入卡——左上「✕ ←」、字段名做标题、右上「▶ ✓」，
    // 下面一个大的浅灰输入框，光标是紫色竖线；键盘在屏幕下半（我们用系统触摸键盘，能打中文）。A / 回车确认，B / Esc 取消。
    private const float RsEdX = 40, RsEdY = 30, RsEdW = 1840, RsEdH = 470;
    private const float RsFieldX = 80, RsFieldY = 160, RsFieldW = 1760, RsFieldH = 300;
    private SpriteVisual? _rsEdPanel;
    private CompositionDrawingSurface? _rsEdSurf;

    private void RsBeginEdit(RsState rs, RsRow row)
    {
        RsEnsureBox();
        _rsEditing = row;
        var o = _cs!.Root.Offset;
        float k = _cs.K;
        RsShowEditPanel(rs, row.Title);
        var box = _rsBox!;
        box.Text = row.Value ?? "";
        box.AcceptsReturn = row.Multiline;
        box.TextWrapping = TextWrapping.Wrap;
        box.FontSize = Math.Max(12, 44 * k);
        box.Width = (RsFieldW - 70) * k;
        box.Height = (RsFieldH - 40) * k;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(box, o.X + (RsFieldX + 35) * k);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(box, o.Y + (RsFieldY + 20) * k);
        box.Visibility = Visibility.Visible;
        box.Select(box.Text.Length, 0);   // Cocoon：光标停在末尾，不全选
        FocusTextBox(box, () => _rsEditing == row);
    }

    /// <summary>输入卡：投影、#FAFAFA 圆角面、两个胶囊、标题、浅灰渐变输入框；列表和面板淡下去。</summary>
    private void RsShowEditPanel(RsState rs, string title) => EdShowCard(rs.Root, RsEditFade(rs), title);

    private static Visual[] RsEditFade(RsState rs) => new Visual[] { rs.Body, rs.Sheet, rs.Glow, rs.Line, rs.Line2, rs.Rim };

    /// <summary>输入卡（二级页的文字输入、右侧栏的 Wi-Fi 密码共用）：画在 parent 里，fade 里的层淡下去；note = 标题右边一行状态（连接中 / 出错红字）；animate = false 只重画不重做入场。</summary>
    private void EdShowCard(ContainerVisual parent, Visual[] fade, string title, string? note = null, bool err = false, bool animate = true)
    {
        var dev = G.Device;
        float R = CsRaster, pad = 40, w = RsEdW + pad * 2, h = RsEdH + pad * 2;
        _rsEdSurf ??= G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        using (var ds = CanvasComposition.CreateDrawingSession(_rsEdSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - RsEdX, pad - RsEdY) * Matrix3x2.CreateScale(R);
            RsSoftShadow(ds, RsEdX, RsEdY + 10, RsEdW, RsEdH, 60, 18, 0.35f);
            ds.FillRoundedRectangle(RsEdX, RsEdY, RsEdW, RsEdH, 60, 60, RsBg);
            if (CocoonPalette.Dark) DkEdge(ds, RsEdX, RsEdY, RsEdW, RsEdH, 60);
            // 左上：灰胶囊里 ✕ 圆章 + 返回箭头
            ds.FillRoundedRectangle(58, 50, 134, 74, 37, 37, Ck(Color.FromArgb(255, 0xE2, 0xE3, 0xE7)));
            ds.FillCircle(94, 87, 21, Ck(Color.FromArgb(255, 0x9A, 0x9C, 0xA3)));
            ds.DrawLine(87, 80, 101, 94, Color.FromArgb(255, 0xF2, 0xF2, 0xF4), 4, RwRoundStroke);
            ds.DrawLine(101, 80, 87, 94, Color.FromArgb(255, 0xF2, 0xF2, 0xF4), 4, RwRoundStroke);
            using (var pb = new CanvasPathBuilder(dev))
            {
                pb.BeginFigure(172, 87); pb.AddLine(136, 87); pb.EndFigure(CanvasFigureLoop.Open);
                pb.BeginFigure(152, 71); pb.AddLine(136, 87); pb.AddLine(152, 103); pb.EndFigure(CanvasFigureLoop.Open);
                using var arrow = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(arrow, RmInk, 7, RwRoundStroke);
            }
            using (var f = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 48, FontWeight = RsWt(title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(title, new Windows.Foundation.Rect(212, 50, 1200, 74), RmInk, f);
            if (note is { Length: > 0 })
                using (var nf = new CanvasTextFormat { FontFamily = RmFont(note), FontSize = 34, FontWeight = RsWt(note, 500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                    ds.DrawText(note, new Windows.Foundation.Rect(1100, 50, 600, 74), err ? RmRed : RsSub, nf);
            // 右上：紫胶囊里 ▶（确认键）+ 白色对勾圆
            RsSoftShadow(ds, 1734, 58, 126, 70, 35, 9, 0.45f);
            using (var br = new CanvasLinearGradientBrush(dev, CpPillStart, CpPillEnd) { StartPoint = new Vector2(1730, 50), EndPoint = new Vector2(1864, 124) })
                ds.FillRoundedRectangle(1730, 50, 134, 74, 37, 37, br);
            ds.FillCircle(1767, 87, 21, CpPillBadge);
            using (var pb = new CanvasPathBuilder(dev))
            {
                pb.BeginFigure(1760, 77); pb.AddLine(1777, 87); pb.AddLine(1760, 97); pb.EndFigure(CanvasFigureLoop.Closed);
                using var tri = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(tri, CpPillInk);
            }
            ds.FillCircle(1828, 87, 21, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
            using (var pb = new CanvasPathBuilder(dev))
            {
                pb.BeginFigure(1818, 87); pb.AddLine(1825, 94); pb.AddLine(1838, 80); pb.EndFigure(CanvasFigureLoop.Open);
                using var tick = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(tick, CpPillInk, 5, RwRoundStroke);
            }
            // 输入框：浅灰，上浅下深
            if (CocoonPalette.Dark) DkWell(ds, RsFieldX, RsFieldY, RsFieldW, RsFieldH, 48);
            else
                using (var field = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xF2, 0xF3, 0xF6), Color.FromArgb(255, 0xE1, 0xE2, 0xE8)) { StartPoint = new Vector2(0, RsFieldY), EndPoint = new Vector2(0, RsFieldY + RsFieldH) })
                    ds.FillRoundedRectangle(RsFieldX, RsFieldY, RsFieldW, RsFieldH, 48, 48, field);
        }
        if (_rsEdPanel == null || _rsEdPanel.Parent != parent)
        {
            if (_rsEdPanel != null) _rsEdPanel.IsVisible = false;
            _rsEdPanel = G.Sprite(G.C.CreateSurfaceBrush(_rsEdSurf), relative: false);
            parent.Children.InsertAtTop(_rsEdPanel);
        }
        _rsEdPanel.Size = new Vector2(w, h);
        _rsEdPanel.Offset = new Vector3(RsEdX - pad, RsEdY - pad, 0);
        _rsEdPanel.CenterPoint = new Vector3(w / 2, pad + 60, 0);
        _rsEdPanel.IsVisible = true;
        if (!animate) return;
        foreach (var v in fade) v.StartAnimation("Opacity", CsS(0, Reduced ? 1 : 120, Bezier.EaseOut));
        if (!Reduced)
        {
            _rsEdPanel.Opacity = 0;
            _rsEdPanel.Scale = new Vector3(0.96f, 0.96f, 1);
            _rsEdPanel.StartAnimation("Opacity", CsS(1, 120, Bezier.EaseOut));
            _rsEdPanel.StartAnimation("Scale", CsV3(Vector3.One, 200, new Bezier(0.22f, 1f, 0.36f, 1f)));
        }
    }

    private void RsHideEditPanel(RsState? rs) => EdHideCard(rs?.Root, rs == null ? Array.Empty<Visual>() : RsEditFade(rs));

    private void EdHideCard(ContainerVisual? parent, Visual[] fade)
    {
        if (_rsEdPanel == null) return;
        var panel = _rsEdPanel;
        if (parent == null || panel.Parent != parent) { panel.IsVisible = false; return; }
        foreach (var v in fade) v.StartAnimation("Opacity", CsS(1, Reduced ? 1 : 140, Bezier.EaseOut));
        panel.StartAnimation("Opacity", CsS(0, Reduced ? 1 : 100, Bezier.EaseOut));
    }

    private void RsEndEdit(bool commit)
    {
        if (_rsEditing is not { } row || _rsBox == null) return;
        _rsEditing = null;
        if (commit) { row.Value = _rsBox.Text.Trim(); Cue(Sfx.Positive); row.Commit?.Invoke(row.Value); }
        _rsBox.Visibility = Visibility.Collapsed;
        _rootEl.Focus(FocusState.Programmatic);
        RsHideEditPanel(_rs);
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
            RequestedTheme = CocoonPalette.Dark ? ElementTheme.Dark : ElementTheme.Light,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            Visibility = Visibility.Collapsed,
        };
        var bg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(0, 0xFF, 0xFF, 0xFF));
        var ink = new Microsoft.UI.Xaml.Media.SolidColorBrush(RmInk);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" }) _rsBox.Resources[key] = bg;
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" }) _rsBox.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) _rsBox.Resources[key] = ink;
        _rsBox.KeyDown += (_, e) =>
        {
            if (_spEdit != null && e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Escape) { e.Handled = true; if (e.Key == Windows.System.VirtualKey.Enter) SpEditCommit(); else SpEditCancel(); return; }
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
            new RsRow { Kind = RsKind.Nav, Icon = "scrape", Title = "刮削游戏", Press = () => RsPush(RsScrapePage(g)) },
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
            // 手动改过的字段记下来：刮削默认不覆盖它们（Cocoon 的「Replace protected fields」）
            var manual = (saved.TryGetValue(RwManualKey, out var mk) ? mk.Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>()).ToHashSet();
            foreach (var r in rows) if (r.Key != null && (r.Value ?? "") != draft[r.Key]) manual.Add(r.Key);
            manual.IntersectWith(m.Keys);
            if (manual.Count > 0) m[RwManualKey] = string.Join(",", manual);
            RwMetaSave(g, m);
            Toast("已保存");
        };
        rows.Add(new RsRow { Kind = RsKind.Info, Icon = "", Break = true, Title = "游玩时长", Value = g.PlaytimeH is double h && h > 0 ? (h < 1 ? $"{h * 60:0} 分钟" : $"{h:0.#} 小时") : "0 分钟" });
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
            bool take = false, brk = false;
            foreach (var r in _stRows)
            {
                if (r.Section) { take = sections.Contains(r.Title); brk = outRows.Count > 0; continue; }
                if (!take || r.Skip) continue;
                var rr = RsFromPerf(r); rr.Break = brk; brk = false;
                outRows.Add(rr);
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
            return new RsRow
            {
                Kind = RsKind.Pick, Title = r.Title, Value = r.Choices[r.Choice], Options = r.Choices, Current = () => r.Choice,
                Choose = i => { if (r.Select != null) r.Select(i); else if (i != r.Choice) r.Step?.Invoke(i - r.Choice); },
            };
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
