// 首页样式「大图」（11-18 jinyu 给的 Steam Deck 主题截图）：
// - 当前游戏的横幅大图铺满整屏，只在顶部、底部和左下压一点（BnBuild 的 Hero 分支），画面本身是主角；
// - 底部一排：焦点那款是一张横版大卡（游戏的横版图，2 : 1），后面接竖封面小卡，往左翻过的游戏滑到大卡后面；
// - 每张卡下面一段渐隐的倒影；
// - 大卡上方：标题、上次游玩 · 时长、一排按钮（开始游戏 / 游戏设置 / Steam / 搜索 / 截图）—— 我们没有二级页面，按钮留在首页；
// - 焦点和圆角同控制中心（连续曲率圆角、白框 + 外晕 + 斜向扫光）。
// 结构沿用「展台」：大卡是单独的一张（p.StandCase），排里同一款藏起来、后面的往前补；换游戏时新大卡淡入、旧的淡出，排平移。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const float HeX = CsEdge, HeH = 330, HeW = 660, HeW0 = 220, HeGap = 24, HeY = CsH - 170 - HeH, HeRowX = HeX + HeW + HeGap;

    /// <summary>排的平移：焦点之后的那一款贴着大卡；到尾巴时夹住，不留右边的空。</summary>
    private static float HeroRailX(int s, int n)
    {
        float total = (n - 1) * (HeW0 + HeGap) - HeGap;
        return Math.Min(0, Math.Max(CsW - CsEdge - HeRowX - total, -s * (HeW0 + HeGap)));
    }

    private static Vector3 HeroSlot(int i, int s, float railX) => new(HeRowX + railX + (i < s ? i : i - 1) * (HeW0 + HeGap), HeY, 0);

    /// <summary>卡下面的倒影：同一张图上下翻转，从卡底往下 40 % 渐隐到透明；圆角裁切同卡面。</summary>
    private void CsAddReflection(CsCase c, Vector2 size)
    {
        if (c.Item == null || c.Art.Brush is not CompositionSurfaceBrush art || c.Surface == null) return;
        var src = G.C.CreateSurfaceBrush(c.Surface);
        src.Stretch = CompositionStretch.UniformToFill;
        src.HorizontalAlignmentRatio = art.HorizontalAlignmentRatio;
        src.VerticalAlignmentRatio = art.VerticalAlignmentRatio;
        var fade = G.C.CreateLinearGradientBrush();
        fade.StartPoint = new Vector2(0, 1);
        fade.EndPoint = new Vector2(0, 0.6f);
        fade.ColorStops.Add(G.C.CreateColorGradientStop(0, Gfx.Rgba(255, 255, 255, 0.32)));
        fade.ColorStops.Add(G.C.CreateColorGradientStop(1, Gfx.Rgba(255, 255, 255, 0)));
        var mask = G.C.CreateMaskBrush();
        mask.Source = src;
        mask.Mask = fade;
        var r = G.Sprite(mask, relative: false);
        r.Size = size;
        r.Offset = new Vector3(0, size.Y + 10, 0);
        r.CenterPoint = new Vector3(size.X / 2, size.Y / 2, 0);
        r.Scale = new Vector3(1, -1, 1);
        using (var sq = BtShape(G.Device, 0, 0, size.X, size.Y, Math.Min(size.X, size.Y * 0.6667f) * 0.105f))
            r.Clip = G.C.CreateGeometricClip(G.C.CreatePathGeometry(new CompositionPath(sq)));
        c.Root.Children.InsertAtBottom(r);
    }

    private void CsLayoutHero(CsPage p, bool animate, bool entry = false)
    {
        p.StandFinish?.Invoke();
        int n = CsCount(p), s = p.Sel;
        float rx = HeroRailX(s, n), prx = HeroRailX(p.Prev, n);
        p.RailX = rx;
        // 往左翻过去的滑到大卡后面（排从大卡右边开始显示）
        if (p.Row!.Clip == null) p.Row.Clip = G.C.CreateInsetClip(HeRowX - 6, 0, 0, 0);
        double ms = CsMs(520);
        int lo = Math.Max(0, (int)Math.Floor(-Math.Max(rx, prx) / (HeW0 + HeGap)) - 1);
        int hi = Math.Min(n - 1, (int)Math.Ceiling((CsW - HeRowX - Math.Min(rx, prx)) / (HeW0 + HeGap)) + 1);
        int order = 0;
        foreach (var gone in p.Cases.Keys.Where(i => i < lo - 2 || i > hi + 2).ToList())
            if (p.Cases.Remove(gone, out var gc)) { p.Row.Children.Remove(gc.Root); CsDisposeCase(gc); }
        for (int i = lo; i <= hi; i++)
        {
            if (!p.Cases.TryGetValue(i, out var c))
            {
                c = CsNewCase(i < p.Items.Count ? p.Items[i] : null, i, new Vector2(HeW0, HeH));
                CsAddReflection(c, new Vector2(HeW0, HeH));
                c.Root.Offset = HeroSlot(i, animate ? p.Prev : s, animate ? prx : rx);
                c.Dim.Opacity = 0;
                p.Cases[i] = c;
                p.Row.Children.InsertAtTop(c.Root);
            }
            var off = HeroSlot(i, s, rx);
            c.Offset = off; c.Size = new Vector2(HeW0, HeH);
            if (!animate)
            {
                c.Root.StopAnimation("Offset");
                if (entry && !Reduced && off.X < CsW)
                {
                    double delay = 120 + order++ * 30;
                    c.Root.Offset = off + new Vector3(0, 40, 0);
                    c.Root.Opacity = 0;
                    c.Root.StartAnimation("Offset", CsV3(off, 600, CsQuart, delay));
                    c.Root.StartAnimation("Opacity", CsS(i == s ? 0 : 1, 420, CsQuart, delay));
                }
                else { c.Root.Offset = off; c.Root.StopAnimation("Opacity"); c.Root.Opacity = i == s ? 0 : 1; }
            }
            else
            {
                c.Root.StartAnimation("Offset", CsV3(off, ms, CsQuart));
                if (i != s && i == p.Prev) { c.Root.StopAnimation("Opacity"); c.Root.Opacity = 0; c.Root.StartAnimation("Opacity", CsS(1, CsMs(260), CsQuart, CsMs(160))); }
            }
        }
        if (p.Cases.TryGetValue(s, out var selRow)) { selRow.Root.StopAnimation("Opacity"); selRow.Root.Opacity = 0; }
        foreach (var rc in p.Cases.Values) { rc.Ring.StopAnimation("Opacity"); rc.Ring.Opacity = 0; }

        // 大卡（选中没变：只改焦点框）
        var g = s < p.Items.Count ? p.Items[s] : null;
        if (p.StandCase is { } cur && cur.Index == s && cur.Item == g)
        {
            cur.Ring.StartAnimation("Opacity", CsS(p.Acts ? 0 : 1, CsMs(250), CsQuart));
            p.Prev = s;
            return;
        }
        var wide = CsNewCase(g, s, new Vector2(HeW, HeH), wide: true);
        CsAddReflection(wide, new Vector2(HeW, HeH));
        CsSelect(wide, true, shine: animate, delay: 80, dimOthers: false);
        wide.Dim.Opacity = 0;
        wide.Ring.Opacity = p.Acts ? 0 : 1;
        wide.Offset = new Vector3(HeX, HeY, 0);
        wide.Size = new Vector2(HeW, HeH);
        wide.Root.Offset = wide.Offset;
        wide.Root.CenterPoint = new Vector3(0, HeH, 0);
        p.Content.Children.InsertAtTop(wide.Root);
        var old = p.StandCase;
        p.StandCase = wide;
        if (entry && !Reduced)
        {
            wide.Root.Opacity = 0;
            wide.Root.Offset = wide.Offset + new Vector3(0, 40, 0);
            wide.Root.StartAnimation("Offset", CsV3(wide.Offset, 650, CsQuart, 60));
            wide.Root.StartAnimation("Opacity", CsS(1, 450, CsQuart, 60));
        }
        if (old == null || !animate || Reduced)
        {
            if (old != null) { p.Content.Children.Remove(old.Root); CsDisposeCase(old); }
            p.Prev = s;
            return;
        }
        // 新大卡从 96 % 放大淡入，旧的同时淡出；落定后拆旧的
        wide.Root.Opacity = 0;
        wide.Root.Scale = new Vector3(0.96f, 0.96f, 1);
        wide.Root.StartAnimation("Opacity", CsS(1, CsMs(260), CsQuart));
        wide.Root.StartAnimation("Scale", CsV3(Vector3.One, CsMs(420), CsQuart));
        old.Ring.StopAnimation("Opacity"); old.Ring.Opacity = 0;
        old.Root.StartAnimation("Opacity", CsS(0, CsMs(200), CsQuart));
        p.StandLeaving = old;
        bool done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            p.StandFinish = null;
            wide.Root.StopAnimation("Opacity"); wide.Root.Opacity = 1;
            wide.Root.StopAnimation("Scale"); wide.Root.Scale = Vector3.One;
            if (p.StandLeaving == old) p.StandLeaving = null;
            p.Content.Children.Remove(old.Root);
            CsDisposeCase(old);
        }
        p.StandFinish = Finish;
        After(CsMs(440), () => { if (!done) Finish(); });
        p.Prev = s;
    }
}
