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
    private const float HeX = CsEdge, HeH = 330, HeW = 700, HeW0 = 220, HeGap = 24, HeY = CsH - 170 - HeH, HeRowX = HeX + HeW + HeGap;

    /// <summary>排的平移：焦点之后的那一款贴着大卡；到尾巴时夹住，不留右边的空。</summary>
    private static float HeroRailX(int s, int n)
    {
        float total = (n - 1) * (HeW0 + HeGap) - HeGap;
        return Math.Min(0, Math.Max(CsW - CsEdge - HeRowX - total, -s * (HeW0 + HeGap)));
    }

    private static Vector3 HeroSlot(int i, int s, float railX) => new(HeRowX + railX + (i < s ? i : i - 1) * (HeW0 + HeGap), HeY, 0);

    /// <summary>卡下面的倒影：同一张图上下翻转，从卡底往下 40 % 渐隐到透明；圆角裁切同卡面。</summary>
    private void CsAddReflection(CsCase c, Vector2 size, bool follow = false)
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
        if (follow)
        {
            // 横卡会连续变宽：倒影跟着卡片尺寸走
            void Bind(CompositionObject o, string prop, string expr) { var e = G.C.CreateExpressionAnimation(expr); e.SetReferenceParameter("v", c.Root); o.StartAnimation(prop, e); }
            Bind(r, "Size", "v.Size");
            Bind(r, "CenterPoint", "Vector3(v.Size.X / 2, v.Size.Y / 2, 0)");
            var rr = G.C.CreateRoundedRectangleGeometry();
            rr.CornerRadius = new Vector2(Math.Min(size.X, size.Y * 0.6667f) * 0.105f * 1.15f);
            Bind(rr, "Size", "v.Size");
            r.Clip = G.C.CreateGeometricClip(rr);
            c.Root.Children.InsertAtBottom(r);
            return;
        }
        using (var sq = BtShape(G.Device, 0, 0, size.X, size.Y, Math.Min(size.X, size.Y * 0.6667f) * 0.105f))
            r.Clip = G.C.CreateGeometricClip(G.C.CreatePathGeometry(new CompositionPath(sq)));
        c.Root.Children.InsertAtBottom(r);
    }

    /// <summary>排里的竖卡斜着摆（绕竖轴转 RowTilt，带透视），焦点大卡正对屏幕。</summary>
    private const float HeTilt = -0.56f;   // ≈ 32°，右边缘朝外（照片里每张竖卡右边比左边高、窄得像斜过来的书）

    /// <summary>卡片的透视倾斜：属性 tilt（弧度）驱动 TransformMatrix（绕卡片中心转，透视距离 ≈ 900）。</summary>
    private void HeTiltSetup(CsCase c, float w, float h, float tilt)
    {
        c.Root.Properties.InsertScalar("tilt", tilt);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // 绕卡片当前尺寸的中心转（横卡展开 / 收回时尺寸在变）
        var e = G.C.CreateExpressionAnimation(
            "Matrix4x4.CreateTranslation(Vector3(-r.Size.X / 2, -r.Size.Y / 2, 0)) * Matrix4x4.CreateFromAxisAngle(Vector3(0, 1, 0), p.tilt) * Matrix4x4(1,0,0,0, 0,1,0,0, 0,0,1,-0.0011, 0,0,0,1) * Matrix4x4.CreateTranslation(Vector3(r.Size.X / 2, r.Size.Y / 2, 0))");
        e.SetReferenceParameter("p", c.Root.Properties);
        e.SetReferenceParameter("r", c.Root);
        c.Root.StartAnimation("TransformMatrix", e);
    }

    private void CsLayoutHero(CsPage p, bool animate, bool entry = false)
    {
        p.StandFinish?.Invoke();
        int n = CsCount(p), s = p.Sel;
        // 左右相邻两款的横版封面先解码好
        foreach (var k0 in p.HeroPre.Keys.Where(k => Math.Abs(k - s) > 1).ToList()) { Images.Release(p.HeroPre[k0]); p.HeroPre.Remove(k0); }
        foreach (int j in new[] { s - 1, s + 1 })
            if (j >= 0 && j < p.Items.Count && !p.HeroPre.ContainsKey(j) && CsWideArt(p.Items[j]) is { } wp)
                p.HeroPre[j] = Images.AcquireReuse(wp, (int)Math.Ceiling(HeW * CsRaster * 1.2f), (int)Math.Ceiling(HeW * CsRaster * 0.6f));
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
                HeTiltSetup(c, HeW0, HeH, HeTilt);
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
                if (i != s && i == p.Prev) { c.Root.StopAnimation("Opacity"); c.Root.Opacity = 0; c.Root.StartAnimation("Opacity", CsS(1, 1, CsQuart, ms)); }   // 旧大卡收回到这一格时再接手
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
        CsAddReflection(wide, new Vector2(HeW, HeH), follow: true);
        HeTiltSetup(wide, HeW, HeH, 0);
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
        // 切换动画（11-18 重做）：新焦点的竖卡从排里自己的位置连续变宽成横卡（不是拉伸），同时转正、飞到大卡位置；
        // 卡面先是竖封面，途中淡成横版封面。旧大卡反过来：连续收窄成竖卡、斜过去，横封面淡回竖封面，
        // 往前翻时滑到左边淡掉，往回翻时落回排里自己的那一格。所有属性都从当前值出发，连按可打断。
        bool fwd = s > p.Prev;
        var ease = new Bezier(0.25f, 0.8f, 0.25f, 1f);   // 前快后慢、尾巴很长，像控制中心的弹簧
        var from = fwd ? HeroSlot(s, p.Prev, prx) : new Vector3(HeX - HeW0 - HeGap, HeY, 0);
        wide.Root.StopAnimation("Offset");
        wide.Root.Offset = from;
        wide.Root.Size = new Vector2(HeW0, HeH);
        wide.Root.Properties.InsertScalar("tilt", HeTilt);
        wide.Root.StartAnimation("Offset", CsV3(new Vector3(HeX, HeY, 0), ms, ease));
        wide.Root.StartAnimation("Size", CsV2(new Vector2(HeW, HeH), ms, ease));
        wide.Root.Properties.StartAnimation("tilt", CsS(0, ms, ease));
        if (wide.Alt != null) { wide.Alt.Opacity = 1; wide.Alt.StartAnimation("Opacity", CsS(0, ms * 0.6, ease, ms * 0.15)); }
        wide.Root.Opacity = fwd ? 1 : 0;
        if (!fwd) wide.Root.StartAnimation("Opacity", CsS(1, ms * 0.4, ease));
        wide.Ring.StopAnimation("Opacity"); wide.Ring.Opacity = 0;
        old.Ring.StopAnimation("Opacity"); old.Ring.Opacity = 0;
        old.Root.StartAnimation("Offset", CsV3(fwd ? new Vector3(HeX - HeW0 - HeGap - 40, HeY, 0) : HeroSlot(p.Prev, s, rx), ms, ease));
        old.Root.StartAnimation("Size", CsV2(new Vector2(HeW0, HeH), ms, ease));
        old.Root.Properties.StartAnimation("tilt", CsS(HeTilt, ms, ease));
        old.Alt?.StartAnimation("Opacity", CsS(1, ms * 0.5, ease));
        if (fwd) old.Root.StartAnimation("Opacity", CsS(0, ms * 0.7, ease, ms * 0.2));
        p.StandLeaving = old;
        bool done = false;
        void Finish()
        {
            // 只收尾旧大卡；新大卡的动画不打断（下一次切换会从它的当前值接着动）
            if (done) return;
            done = true;
            p.StandFinish = null;
            if (p.StandCase == wide) wide.Ring.StartAnimation("Opacity", CsS(p.Acts ? 0 : 1, CsMs(200), CsQuart));
            if (p.StandLeaving == old) p.StandLeaving = null;
            p.Content.Children.Remove(old.Root);
            CsDisposeCase(old);
        }
        p.StandFinish = Finish;
        After(ms + 20, () => { if (!done) Finish(); });
        p.Prev = s;
    }
}
