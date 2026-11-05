// 游戏库屏保（第三轮原型「统一屏保」，三种首页样式共用）：静置后整屏换成一款游戏的横幅图，慢慢推近、平移；
// 一边是大号时钟（日期、电量），另一边是这款游戏的卡盒、名字和上次游玩。每 12 s 换一款（最近玩过的在前），
// 两块文字每轮左右互换。顶栏在屏保期间让开，屏幕上只有一个时钟。任何输入只唤醒；A 唤醒时顺便选中屏幕上那款。
// 触发时机、开关和空闲时长沿用 WallStage.Screensaver.cs（设置 › 外观 的屏保开关）。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class CsSaver
    {
        public required ContainerVisual Root;
        public required SpriteVisual[] Img;
        public required SpriteVisual ClockV, GameText;
        public required ContainerVisual GameBox;
        public LoadedImageSurface?[] Surf = new LoadedImageSurface?[2];
        public CsCase? Case;
        public List<LibraryItem> Queue = new();
        public int Round;
        public LibraryItem? Game;
        public string? ClockText;
        public long NextSwitch;
    }

    private CsSaver? _csSs;
    private const double SsHoldMs = 12_000, SsPanMs = 15_000;


    /// <summary>屏保进场：只要卡盒版开着（三种首页样式都算）就用这一套，不再点亮墙上的海报。</summary>
    private bool CsSaverEnter()
    {
        if (_cs == null || _csSs != null) return false;
        var queue = CsList(1);
        if (queue.Count == 0) return false;
        float k = _cs.K;
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        root.Scale = new Vector3(k, k, 1);
        root.Offset = _cs.Root.Offset;
        root.Opacity = 0;
        var black = G.Sprite(G.Solid(Microsoft.UI.Colors.Black), relative: false);
        black.Size = new Vector2(CsW, CsH);
        root.Children.InsertAtTop(black);
        var imgs = new SpriteVisual[2];
        for (int i = 0; i < 2; i++)
        {
            var b = G.C.CreateSurfaceBrush();
            b.Stretch = CompositionStretch.UniformToFill;
            b.VerticalAlignmentRatio = 0.35f;
            var s = G.Sprite(b, relative: false);
            s.Size = new Vector2(CsW * 1.08f, CsH * 1.08f);
            s.Offset = new Vector3(-CsW * 0.04f, -CsH * 0.04f, 0);
            s.CenterPoint = new Vector3(s.Size.X / 2, s.Size.Y / 2, 0);
            s.Opacity = 0;
            root.Children.InsertAtTop(s);
            imgs[i] = s;
        }
        // 压暗（用户要求再暗一些）：0 % 黑 .5、30 % 黑 .32、52 % 黑 .36、100 % 黑 .9
        var shade = G.Sprite(G.LinearGradient(180, CsW, CsH, (0, Gfx.Rgba(0, 0, 0, 0.5)), (0.3, Gfx.Rgba(0, 0, 0, 0.32)), (0.52, Gfx.Rgba(0, 0, 0, 0.36)), (1, Gfx.Rgba(0, 0, 0, 0.9))), relative: false);
        shade.Size = new Vector2(CsW, CsH);
        root.Children.InsertAtTop(shade);
        var clock = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var box = G.Container(relative: false);
        var text = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        box.Children.InsertAtTop(text);
        root.Children.InsertAtTop(clock);
        root.Children.InsertAtTop(box);
        _top.Children.InsertAtTop(root);
        _csSs = new CsSaver { Root = root, Img = imgs, ClockV = clock, GameText = text, GameBox = box, Queue = queue };
        root.StartAnimation("Opacity", CsS(1, CsMs(900), Bezier.EaseOut));
        _cs.Header.StartAnimation("Opacity", CsS(0, CsMs(600), Bezier.EaseOut));
        CsSaverShow(first: true);
        Log.Info($"cases screensaver entered ({queue.Count} games)");
        return true;
    }

    private void CsSaverExit()
    {
        var ss = _csSs;
        if (ss == null) return;
        _csSs = null;
        if (_cs != null && _csInsert == null) _cs.Header.StartAnimation("Opacity", CsS(CsWallHome ? 0 : 1, CsMs(400), CsQuart));
        ss.Root.StartAnimation("Opacity", CsS(0, CsMs(500), Bezier.EaseOut));
        After(CsMs(520), () =>
        {
            _top.Children.Remove(ss.Root);
            foreach (var s in ss.Surf) Images.Release(s);
            if (ss.Case != null) CsDisposeCase(ss.Case);
            ss.Root.Dispose();
        });
    }

    /// <summary>A 唤醒：选中屏幕上那款游戏（卡盒版首页），只唤醒，不启动。</summary>
    private bool CsSaverWakeWithGame()
    {
        var g = _csSs?.Game;
        if (g == null || _cs == null) return false;
        ConsumeScreensaverWake();
        if (_prefs.Home == HomeStyle.Wall) return true;
        _csSpot = g.Id;
        _csSpotForce = g.Id;   // 首页重建时定位到屏幕上那款，而不是唤醒前选中的那款
        _ccGameId = g.Id;
        CsShowTab(0, _cs.Tab == 0 ? 0 : -1, entry: true);
        return true;
    }

    /// <summary>换下一款：新图在上面淡入 1.8 s、慢慢推近平移 15 s；两块文字先淡出，换边后再淡入。</summary>
    private void CsSaverShow(bool first = false)
    {
        var ss = _csSs;
        if (ss == null) return;
        if (!first) ss.Round++;
        var g = ss.Queue[ss.Round % ss.Queue.Count];
        ss.Game = g;
        ss.NextSwitch = NowMs + (long)SsHoldMs;
        int slot = ss.Round % 2;
        var sprite = ss.Img[slot];
        var other = ss.Img[1 - slot];
        var k = g.Art;
        string? path = k == null ? null : k.Has('g') ? k.File('g') : k.Has('h') ? k.File('h') : k.Has('q') ? k.File('q') : null;
        if (path != null && Images.AcquireReuse(path, (int)Math.Ceiling(CsW * 1.08f * CsRaster), (int)Math.Ceiling(CsH * 1.08f * CsRaster)) is { } surf)
        {
            Images.Release(ss.Surf[slot]);
            ss.Surf[slot] = surf;
            var brush = (CompositionSurfaceBrush)sprite.Brush;
            float fx = (float)CsLookOf(g).Fx;
            int round = ss.Round;
            Images.WhenLoaded(surf, () =>
            {
                if (_csSs != ss || ss.Round != round) return;
                brush.Surface = surf;
                brush.HorizontalAlignmentRatio = fx;
                ss.Root.Children.Remove(sprite);
                ss.Root.Children.InsertAbove(sprite, other);
                CsPan(sprite, round % 2 == 1 ? 1 : -1, loop: false);
                sprite.StopAnimation("Opacity");
                sprite.Opacity = 0;
                sprite.StartAnimation("Opacity", CsS(1, CsMs(1800), Bezier.EaseInOut));
                After(CsMs(1850), () => { if (_csSs == ss && ss.Round == round) { other.StopAnimation("Opacity"); other.Opacity = 0; } });
            });
        }
        void Put()
        {
            if (_csSs != ss) return;
            bool flip = ss.Round % 2 == 1;
            CsSaverPaintClock(force: true, rightSide: flip);
            CsSaverPaintGame(g, rightSide: !flip);
            ss.ClockV.StartAnimation("Opacity", CsS(1, CsMs(600), Bezier.EaseOut));
            ss.GameBox.StartAnimation("Opacity", CsS(1, CsMs(600), Bezier.EaseOut));
        }
        if (first) { ss.ClockV.Opacity = 0; ss.GameBox.Opacity = 0; Put(); }
        else
        {
            ss.ClockV.StartAnimation("Opacity", CsS(0, CsMs(600), Bezier.EaseOut));
            ss.GameBox.StartAnimation("Opacity", CsS(0, CsMs(600), Bezier.EaseOut));
            After(CsMs(620), Put);
        }
    }

    /// <summary>慢速推近 + 平移（scale 1 → 1.07，横向 ±1.5 %、纵向 −1 %）：屏保每款走一遍 15 s；首页背景来回往复。</summary>
    private void CsPan(SpriteVisual sprite, int dir, bool loop)
    {
        if (Reduced) return;
        float w = sprite.Size.X, x0 = -CsW * 0.04f, y0 = -CsH * 0.04f;
        var sc = G.C.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0, Vector3.One, G.Linear);
        sc.InsertKeyFrame(1, new Vector3(1.07f, 1.07f, 1), G.Linear);
        sc.Duration = Gfx.Ms(SsPanMs);
        var off = G.C.CreateVector3KeyFrameAnimation();
        off.InsertKeyFrame(0, new Vector3(x0 + dir * w * 0.015f, y0, 0), G.Linear);
        off.InsertKeyFrame(1, new Vector3(x0 - dir * w * 0.015f, y0 - CsH * 0.01f, 0), G.Linear);
        off.Duration = sc.Duration;
        if (loop)
        {
            sc.IterationBehavior = off.IterationBehavior = AnimationIterationBehavior.Forever;
            sc.Direction = off.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
            var ease = G.Ease(Bezier.EaseInOut);
            sc.InsertKeyFrame(1, new Vector3(1.07f, 1.07f, 1), ease);
            off.InsertKeyFrame(1, new Vector3(x0 - dir * w * 0.015f, y0 - CsH * 0.01f, 0), ease);
        }
        sprite.StartAnimation("Scale", sc);
        sprite.StartAnimation("Offset", off);
    }

    /// <summary>时钟一块：150 px 细体时间，下面一行日期 + 电量（电池和数字等高）。左下或右下（右对齐）。</summary>
    private void CsSaverPaintClock(bool force = false, bool? rightSide = null)
    {
        var ss = _csSs;
        if (ss == null) return;
        var now = DateTime.Now;
        string clock = now.ToString("H:mm");
        if (!force && clock == ss.ClockText) return;
        ss.ClockText = clock;
        bool right = rightSide ?? ss.ClockV.Offset.X > CsW / 2 - 1;
        string date = $"{now.Month}月{now.Day}日 星期{"日一二三四五六"[(int)now.DayOfWeek]}";
        var bat = _railModel.Battery;
        const float w = 760, h = 230;
        CsRepaint(ss.ClockV, w, h, ds =>
        {
            var align = right ? CanvasHorizontalAlignment.Right : CanvasHorizontalAlignment.Left;
            using var c = TextKit.Layout(ds, clock, TextKit.Num(300), 150, w, align);
            ds.DrawTextLayout(c, 0, 0, Gfx.Rgba(242, 242, 242));
            float lineY = 150 + 16;
            using var d = TextKit.Layout(ds, date, TextKit.Ui(400), 26, w, CanvasHorizontalAlignment.Left);
            float dw = (float)d.LayoutBounds.Width;
            var ink = Gfx.Rgba(242, 242, 242, 0.6);
            var (baseL, cap) = TextInk.Metrics(ds, TextKit.Num(400), 26, "0", "0");
            using var p = TextKit.Layout(ds, $"{bat.Percent}%", TextKit.Num(400), 26, w, CanvasHorizontalAlignment.Left);
            float battW = bat.HasBattery ? MathF.Round(cap * 1.9f) + MathF.Max(1.2f, cap * 0.09f) * 0.5f + MathF.Max(1.5f, cap * 0.11f) : 0;
            float total = dw + (bat.HasBattery ? 18 + battW + 8 + (float)p.LayoutBounds.Width : 0);
            float x = right ? w - total : 0;
            ds.DrawTextLayout(d, x, lineY, ink);
            if (!bat.HasBattery) return;
            x += dw + 18;
            float baseY = lineY + TextInk.Metrics(ds, TextKit.Ui(400), 26, date).Baseline;
            x += BatteryGlyph.Draw(ds, x, baseY - cap, cap, ink, bat, Theme.ChargingGreen, Gfx.Rgba(0xef, 0x44, 0x44)) + 8;
            ds.DrawTextLayout(p, x - (float)p.LayoutBounds.X, baseY - baseL, ink);
        });
        ss.ClockV.Offset = new Vector3(right ? CsW - CsEdge - w : CsEdge, CsH - 92 - h, 0);
    }

    /// <summary>游戏一块：卡盒（160×240）+ 名字（32 px）+ 上次游玩（22 px）；在右边时卡盒在右、文字右对齐。</summary>
    private void CsSaverPaintGame(LibraryItem g, bool rightSide)
    {
        var ss = _csSs;
        if (ss == null) return;
        if (ss.Case != null) { ss.GameBox.Children.Remove(ss.Case.Root); CsDisposeCase(ss.Case); }
        var c = CsNewCase(g, 0, new Vector2(160, 240));
        CsSelect(c, true, shine: false, dimOthers: false);
        c.Ring.Opacity = 0;
        ss.Case = c;
        ss.GameBox.Children.InsertAtBottom(c.Root);
        const float tw = 640, th = 90;
        string meta = CsMetaShort(g);
        CsRepaint(ss.GameText, tw, th, ds =>
        {
            var align = rightSide ? CanvasHorizontalAlignment.Right : CanvasHorizontalAlignment.Left;
            using var t = TextKit.Layout(ds, g.Title, TextKit.Ui(600), 32, tw, align);
            ds.DrawTextLayout(t, 0, 0, Gfx.Rgba(242, 242, 242));
            if (meta.Length == 0) return;
            using var m = TextKit.Layout(ds, meta, TextKit.Ui(400), 22, tw, align);
            ds.DrawTextLayout(m, 0, 52, Gfx.Rgba(242, 242, 242, 0.6));
        });
        float bottom = CsH - 92, boxW = 160 + 28 + tw;
        float x0 = rightSide ? CsW - CsEdge - boxW : CsEdge;
        c.Root.Offset = new Vector3(rightSide ? boxW - 160 : 0, 0, 0);
        ss.GameText.Offset = new Vector3(rightSide ? 0 : 160 + 28, 240 - th, 0);
        ss.GameBox.Offset = new Vector3(x0, bottom - 240, 0);
    }
}
