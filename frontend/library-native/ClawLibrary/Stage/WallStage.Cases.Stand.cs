// 首页样式「展台」（第三轮原型 C）：左边一个 400×600 的大卡盒站在展台上，右边是信息区和一排 200×300 的小卡盒。
// 为了不重复：被拿上展台的那一盒从排里抽走，后面的往前补（以前留一个空槽，停在原地像卡住的空白封面）。
// 换游戏时新的一盒从它在排里的位置放大飞上展台（520 ms quart-out，途中上浮 24），旧的一盒缩小飞回排里
// 让出来的那个位置，落下后才显示。连按时先把上一段直接做完再开始下一段。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const float StdX = CsEdge, StdW = 400, StdH = 600, StdY = CsH - 64 - StdH;
    private const float StdInfoX = StdX + StdW + 56, StdInfoTop = 536;
    private const float StdRowX = StdInfoX, StdW0 = 200, StdH0 = 300, StdGap = 24, StdRowY = CsH - 64 - StdH0;
    private const double StdFlyMs = 520;

    /// <summary>排里前面保留一格（上一款）；到尾巴时夹住，不留右边的空。排里少了展台上那一盒，所以按 n − 1 盒算。</summary>
    private static float StandRailX(int s, int n)
    {
        float total = (n - 1) * (StdW0 + StdGap) - StdGap;
        float x = (StdW0 + StdGap) * (1 - s);
        return Math.Max(Math.Min(0, CsW - CsEdge - StdRowX - total), Math.Min(0, x));
    }

    /// <summary>第 i 盒在排里的位置（展台上那一盒 s 不占格，它后面的往前补一格）。</summary>
    private static Vector3 StandSlot(int i, int s, float railX) => new(StdRowX + railX + (i < s ? i : i - 1) * (StdW0 + StdGap), StdRowY, 0);

    private void CsLayoutStand(CsPage p, bool animate, bool entry = false)
    {
        // 上一段飞行还没走完：直接做完
        p.StandFinish?.Invoke();
        int n = CsCount(p), s = p.Sel;
        float rx = StandRailX(s, n), prx = StandRailX(p.Prev, n);
        p.RailX = rx;
        if (p.Row!.Clip == null) p.Row.Clip = G.C.CreateInsetClip(StdRowX, 0, 0, 0);
        double ms = CsMs(550);
        int lo = Math.Max(0, (int)Math.Floor(-Math.Max(rx, prx) / (StdW0 + StdGap)) - 1);
        int hi = Math.Min(n - 1, (int)Math.Ceiling((CsW - StdRowX - Math.Min(rx, prx)) / (StdW0 + StdGap)) + 1);
        int order = 0;
        foreach (var gone in p.Cases.Keys.Where(i => i < lo - 2 || i > hi + 2).ToList())
            if (p.Cases.Remove(gone, out var gc)) { p.Row.Children.Remove(gc.Root); CsDisposeCase(gc); }
        for (int i = lo; i <= hi; i++)
        {
            if (!p.Cases.TryGetValue(i, out var c))
            {
                c = CsNewCase(i < p.Items.Count ? p.Items[i] : null, i, new Vector2(StdW0, StdH0));
                c.Root.Offset = StandSlot(i, animate ? p.Prev : s, animate ? prx : rx);
                c.Dim.Opacity = ShDim;
                p.Cases[i] = c;
                p.Row.Children.InsertAtTop(c.Root);
            }
            var off = StandSlot(i, s, rx);
            c.Offset = off; c.Size = new Vector2(StdW0, StdH0);
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
            else c.Root.StartAnimation("Offset", CsV3(off, ms, CsQuart));
        }
        // 展台上那一盒在排里藏起来（不占格）；排里的小卡盒一律不画焦点（焦点只在展台的大卡盒或按钮上）
        if (p.Cases.TryGetValue(s, out var selRow)) { selRow.Root.StopAnimation("Opacity"); selRow.Root.Opacity = 0; }
        foreach (var rc in p.Cases.Values) { rc.Ring.StopAnimation("Opacity"); rc.Ring.Opacity = 0; }

        // 展台上的那一盒（选中没变，比如从按钮回到卡盒排：只改描边）
        var g = s < p.Items.Count ? p.Items[s] : null;
        if (p.StandCase is { } cur && cur.Index == s && cur.Item == g)
        {
            cur.Ring.StartAnimation("Opacity", CsS(p.Acts ? 0 : 1, CsMs(250), CsQuart));
            p.Prev = s;
            return;
        }
        var stand = CsNewCase(g, s, new Vector2(StdW, StdH), hiRes: true);
        CsSelect(stand, true, shine: animate, delay: 120, dimOthers: false);
        stand.Dim.Opacity = 0;
        stand.Ring.Opacity = p.Acts ? 0 : 1;
        stand.Offset = new Vector3(StdX, StdY, 0);
        stand.Size = new Vector2(StdW, StdH);
        p.Content.Children.InsertAtTop(stand.Root);
        var old = p.StandCase;
        p.StandCase = stand;
        if (!animate || old == null || Reduced)
        {
            stand.Root.Offset = stand.Offset;
            if (entry && !Reduced)
            {
                stand.Root.Opacity = 0;
                stand.Root.Offset = stand.Offset + new Vector3(0, 40, 0);
                stand.Root.StartAnimation("Offset", CsV3(stand.Offset, 650, CsQuart, 60));
                stand.Root.StartAnimation("Opacity", CsS(1, 450, CsQuart, 60));
            }
            if (old != null) { p.Content.Children.Remove(old.Root); CsDisposeCase(old); }
            foreach (var (i, c) in p.Cases) if (i != s) { c.Root.StopAnimation("Opacity"); c.Root.Opacity = 1; }
            p.Prev = s;
            return;
        }
        // 新的一盒：从自己的空槽（缩成一半）放大飞上展台，途中上浮 24 再落下
        int prevSel = p.Prev;
        var from = StandSlot(s, p.Prev, prx);
        var ez = G.Ease(CsQuart);
        stand.Root.StopAnimation("Offset");
        stand.Root.Offset = from;
        stand.Root.Scale = new Vector3(0.5f, 0.5f, 1);
        var fly = G.C.CreateVector3KeyFrameAnimation();
        fly.InsertKeyFrame(0, from, G.Linear);
        fly.InsertKeyFrame(0.55f, new Vector3(StdX, StdY - 24, 0), ez);
        fly.InsertKeyFrame(1, new Vector3(StdX, StdY, 0), G.Ease(Bezier.EaseOut));
        fly.Duration = Gfx.Ms(StdFlyMs);
        var grow = G.C.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3(0.5f, 0.5f, 1), G.Linear);
        grow.InsertKeyFrame(0.55f, new Vector3(1.02f, 1.02f, 1), ez);
        grow.InsertKeyFrame(1, Vector3.One, G.Ease(Bezier.EaseOut));
        grow.Duration = fly.Duration;
        stand.Root.StartAnimation("Offset", fly);
        stand.Root.StartAnimation("Scale", grow);
        // 飞行中不带焦点，落定后再亮
        stand.Ring.StopAnimation("Opacity");
        stand.Ring.Opacity = 0;
        // 旧的一盒：缩小飞回自己的位置（排同时在平移，按平移后的位置），落下后那一格再显示
        old.Ring.StopAnimation("Opacity");
        old.Ring.Opacity = 0;
        old.Glow.StartAnimation("Opacity", CsS(0, CsMs(300), CsQuart));
        var back = StandSlot(prevSel, s, rx);
        old.Root.StartAnimation("Offset", CsV3(back, StdFlyMs, CsQuart));
        old.Root.StartAnimation("Scale", CsV3(new Vector3(0.5f, 0.5f, 1), StdFlyMs, CsQuart));
        p.StandLeaving = old;
        bool done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            p.StandFinish = null;
            foreach (var prop in new[] { "Offset", "Scale" }) stand.Root.StopAnimation(prop);
            stand.Root.Offset = new Vector3(StdX, StdY, 0);
            stand.Root.Scale = Vector3.One;
            if (p.StandCase == stand) stand.Ring.StartAnimation("Opacity", CsS(p.Acts ? 0 : 1, CsMs(200), CsQuart));
            if (p.StandLeaving == old) p.StandLeaving = null;
            p.Content.Children.Remove(old.Root);
            CsDisposeCase(old);
            if (p.Cases.TryGetValue(prevSel, out var back0) && prevSel != p.Sel) { back0.Root.StopAnimation("Opacity"); back0.Root.Opacity = 1; }
        }
        p.StandFinish = Finish;
        After(StdFlyMs + 20, () => { if (!done) Finish(); });
        p.Prev = s;
    }

    private void CsStandDispose(CsPage p)
    {
        p.StandFinish = null;
        if (p.StandCase != null) { CsDisposeCase(p.StandCase); p.StandCase = null; }
        if (p.StandLeaving != null) { CsDisposeCase(p.StandLeaving); p.StandLeaving = null; }

    }
}
