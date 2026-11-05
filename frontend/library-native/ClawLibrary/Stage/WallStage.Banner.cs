// 「横幅」背景（设置 › 外观 › 游戏页面用横幅，默认开）：当前游戏的横幅图铺满，24 s 慢速推近平移，再按页面压暗。
// 游戏页面都用这一份实现：卡盒版首页（展架 / 展台 / 叠放，清晰铺满）、全部 / Steam / 本地分类页（缩小模糊）、
// 游戏设置（显示那款游戏的横幅，压暗跟进来的那一页一样）、屏保（自己的 15 s 推近，图也从这里取）。
// 海报墙、控制中心和里面的系统页照旧用用户选的老样式（隐现 / 莫奈 / 漫游 / 胶片封面墙，Backdrop.cs）。
// 开关关掉时游戏页面也改用老样式：卡盒版页面不铺自己的底，下面墙的 Backdrop 露出来并跟着当前游戏换色。
//
// 平移用一只共用的时钟（属性集 t，0 → 1 → 0，24 s 来回）驱动所有横幅图，所以从游戏库按 Y 进游戏设置时，
// 两层同一张图、同一个相位，交叉淡入看不出接缝。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>一层横幅背景（1920×1200 设计坐标）：底色 + 两张交替淡入的图 + 压暗。</summary>
    private sealed class BnLayer
    {
        public required ContainerVisual Root;
        public required SpriteVisual[] Img;
        public LoadedImageSurface?[] Surf = new LoadedImageSurface?[2];
        public int Front;
        public string? Path;
        public bool Cat;
        public HomeStyle Style;
        /// <summary>第一张图已经铺上（之后换图走 400 ms 交叉淡入）。</summary>
        public bool Shown;
    }

    private bool BannerOn => _backgroundSettings.GameBanner;
    private CompositionPropertySet? _bnClock;
    private int _bnDriftDir;

    /// <summary>
    /// 建一层横幅背景。cat = 分类页（小图放大 = 模糊，上浅下深）；否则按首页样式压暗（展架 / 展台左深右浅，叠放左深）。
    /// 关掉横幅时返回一层空的（透明），下面的老样式露出来。
    /// </summary>
    private BnLayer BnBuild(HomeStyle style, bool cat)
    {
        var bg = G.Container(relative: false);
        bg.Size = new Vector2(CsW, CsH);
        var layer = new BnLayer { Root = bg, Img = Array.Empty<SpriteVisual>(), Cat = cat, Style = style };
        if (!BannerOn) return layer;
        var floor = G.Sprite(G.Solid(Gfx.Rgba(0x0f, 0x0f, 0x10)), relative: false);
        floor.Size = new Vector2(CsW, CsH);
        bg.Children.InsertAtTop(floor);
        var imgs = new SpriteVisual[2];
        for (int i = 0; i < 2; i++)
        {
            var b = G.C.CreateSurfaceBrush();
            b.Stretch = CompositionStretch.UniformToFill;
            b.VerticalAlignmentRatio = 0.3f;
            b.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
            var s = G.Sprite(b, relative: false);
            if (cat) { s.Offset = new Vector3(-60, -60, 0); s.Size = new Vector2(CsW + 120, 900); }
            else s.Size = new Vector2(CsW, CsH);
            s.CenterPoint = new Vector3(s.Size.X / 2, s.Size.Y / 2, 0);
            s.Opacity = 0;
            bg.Children.InsertAtTop(s);
            imgs[i] = s;
        }
        layer.Img = imgs;
        Color ink = Gfx.Rgba(0x0f, 0x0f, 0x10);
        if (cat)
        {
            var fade = G.Sprite(G.LinearGradient(180, CsW + 120, 900, (0, Gfx.WithAlpha(ink, 0.15)), (0.92, ink)), relative: false);
            fade.Offset = new Vector3(-60, -60, 0); fade.Size = new Vector2(CsW + 120, 900);
            bg.Children.InsertAtTop(fade);
        }
        else if (style is HomeStyle.Shelf or HomeStyle.Stand)
        {
            // 比原型深一些：背景在动，标题和 meta 要始终清楚
            bg.Children.InsertAtTop(BnShade(90, (0, 0.9), (0.45, 0.55), (0.75, 0.18), (1, 0.12)));
            bg.Children.InsertAtTop(BnShade(180, (0, 0.6), (0.18, 0.12), (0.4, 0.2), (0.8, 0.94), (1, 1)));
        }
        else
        {
            bg.Children.InsertAtTop(BnShade(90, (0, 0.92), (0.38, 0.62), (0.7, 0.25), (1, 0.42)));
            bg.Children.InsertAtTop(BnShade(180, (0, 0.6), (0.18, 0.1), (0.6, 0.12), (1, 0.88)));
        }
        return layer;
    }

    private SpriteVisual BnShade(double deg, params (double At, double A)[] stops)
    {
        Color ink = Gfx.Rgba(0x0f, 0x0f, 0x10);
        var s = G.Sprite(G.LinearGradient(deg, CsW, CsH, stops.Select(x => (x.At, Gfx.WithAlpha(ink, x.A))).ToArray()), relative: false);
        s.Size = new Vector2(CsW, CsH);
        return s;
    }

    /// <summary>这款游戏的横幅图路径（首页 / 游戏设置优先 'g' 大背景；分类页用小的 'h' 去模糊）。</summary>
    private static string? BnPath(LibraryItem g, bool cat)
    {
        var k = g.Art;
        return k == null ? null : !cat && k.Has('g') ? k.File('g') : k.Has('h') ? k.File('h') : k.Has('q') ? k.File('q') : k.Has('p') ? k.File('p') : null;
    }

    /// <summary>
    /// 换成这款游戏的横幅：图解码好了再铺。第一张直接铺上并回调 first（页面拿它决定整层什么时候淡进来），
    /// 之后新图叠在旧图上面淡入 400 ms，淡完再撤旧图（中途不会透出底色变暗）。alive = false 时丢弃晚到的图。
    /// 返回 false = 没图可换（关掉横幅、没有横幅图、解码失败）。
    /// </summary>
    private bool BnShow(BnLayer l, LibraryItem g, bool force, Action? first, Func<bool> alive)
    {
        if (l.Img.Length < 2) return false;
        bool cat = l.Cat;
        string? path = BnPath(g, cat);
        if (path == null) return false;
        if (path == l.Path && !force) return true;
        l.Path = path;
        int next = 1 - l.Front;
        var sprite = l.Img[next];
        var brush = (CompositionSurfaceBrush)sprite.Brush;
        var fx = (float)CsLookOf(g).Fx;
        void Show(ICompositionSurface surface)
        {
            if (!alive() || l.Path != path) return;
            brush.Surface = surface;
            brush.HorizontalAlignmentRatio = fx;
            l.Front = next;
            float target = cat ? 0.4f : 1;
            var other = l.Img[1 - next];
            l.Root.Children.Remove(sprite);
            l.Root.Children.InsertAbove(sprite, other);
            sprite.StopAnimation("Opacity");
            if (!cat) BnDrift(sprite, ++_bnDriftDir % 2 == 0 ? 1 : -1);
            if (!l.Shown)
            {
                l.Shown = true;
                sprite.Opacity = target;
                first?.Invoke();
                return;
            }
            double ms = CsMs(400);
            sprite.Opacity = 0;
            sprite.StartAnimation("Opacity", CsS(target, ms, Bezier.EaseOut));
            int front = next;
            After(ms + 30, () => { if (l.Front == front) { other.StopAnimation("Opacity"); other.Opacity = 0; } });
        }
        if (cat) { CsBlurred(path, Show); return true; }
        var surf = Images.AcquireReuse(path, (int)Math.Ceiling(CsW * CsRaster), (int)Math.Ceiling(CsH * CsRaster));
        if (surf == null) return false;
        Images.Release(l.Surf[next]);
        l.Surf[next] = surf;
        Images.WhenLoaded(surf, () => { if (l.Surf[next] == surf) Show(surf); });
        return true;
    }

    /// <summary>
    /// 慢速推近平移（scale 1.04 → 1.10、横向 ±1.2 %、纵向 −1 %，24 s 来回，ease-in-out），由共用时钟驱动：
    /// 同一时刻所有横幅层的相位一样，每张图只按自己的方向（dir）走。
    /// </summary>
    private void BnDrift(SpriteVisual sprite, int dir)
    {
        if (Reduced) return;
        if (_bnClock == null)
        {
            _bnClock = G.C.CreatePropertySet();
            _bnClock.InsertScalar("t", 0);
            var a = G.C.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0, 0, G.Linear);
            a.InsertKeyFrame(1, 1, G.Ease(Bezier.EaseInOut));
            a.Duration = Gfx.Ms(24_000);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            a.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
            _bnClock.StartAnimation("t", a);
        }
        float w = sprite.Size.X;
        var sc = G.C.CreateExpressionAnimation("Vector3(1.04 + 0.06 * c.t, 1.04 + 0.06 * c.t, 1)");
        sc.SetReferenceParameter("c", _bnClock);
        var off = G.C.CreateExpressionAnimation("Vector3(d * w * 0.012 * (1 - 2 * c.t), -h * 0.01 * c.t, 0)");
        off.SetReferenceParameter("c", _bnClock);
        off.SetScalarParameter("d", dir);
        off.SetScalarParameter("w", w);
        off.SetScalarParameter("h", CsH);
        sprite.StartAnimation("Scale", sc);
        sprite.StartAnimation("Offset", off);
    }

    private void BnDispose(BnLayer l)
    {
        foreach (var s in l.Surf) Images.Release(s);
        l.Surf = new LoadedImageSurface?[2];
    }

    private bool BnUses(BnLayer? l, CompositionDrawingSurface s) => l != null && l.Img.Any(v => (v.Brush as CompositionSurfaceBrush)?.Surface == s);

    // ---------------- 游戏设置 ----------------

    private ContainerVisual? _gsBannerHost;
    private BnLayer? _gsBanner;
    private int _gsBannerVersion;
    private bool _gsBannerHold;

    /// <summary>
    /// 游戏设置打开：横幅开着时在控制中心的老样式背景上面盖一层这款游戏的横幅。压暗照进来的那一页（从分类页来就是模糊的那种），
    /// 和游戏库同一张图、同一个平移相位：从游戏库进来时图已经在缓存里，直接铺满，跟着控制中心一起淡进来，看不出接缝；
    /// 从控制中心里面进来（老样式 → 横幅）交叉淡入 300 ms。
    /// </summary>
    private void GsBannerOpen(LibraryItem item, bool fromLibrary)
    {
        GsBannerClose(instant: true);
        if (!BannerOn || _cc == null || _ccBack == null) return;
        var from = _cs?.Page;
        // 控制中心这时已经开着（CsActive = false），直接看游戏库当前停在哪一页
        bool cat = fromLibrary && from != null && from.Tab != 0;
        var style = from != null && from.Tab == 0 && from.Style != HomeStyle.Wall ? from.Style : HomeStyle.Shelf;
        var layer = BnBuild(style, cat);
        var host = G.Container(relative: false);
        host.Size = new Vector2(CsW, CsH);
        float k = CsK;
        host.Scale = new Vector3(k, k, 1);
        host.Offset = new Vector3((float)(View.Width - CsW * k) / 2, (float)(View.Height - CsH * k) / 2, 0);
        host.Children.InsertAtTop(layer.Root);
        host.Opacity = 0;
        _cc.Children.InsertAbove(host, _ccDim);
        _gsBannerHost = host;
        _gsBanner = layer;
        int v = ++_gsBannerVersion;
        bool instant = fromLibrary || Reduced;
        void Reveal()
        {
            if (v != _gsBannerVersion) return;
            host.StopAnimation("Opacity");
            if (instant) host.Opacity = 1;
            else host.StartAnimation("Opacity", CsS(1, 300, CsQuart));
        }
        if (!BnShow(layer, item, force: true, Reveal, () => v == _gsBannerVersion)) { Reveal(); }
        Log.Info($"game settings banner {(cat ? "cat" : style.ToString())} {(instant ? "instant" : "fade")}");
    }

    /// <summary>游戏设置关上：回游戏库时跟着控制中心一起走（下面就是同一张横幅）；回控制中心的设置页交叉淡出 300 ms。</summary>
    private void GsBannerClose(bool instant = false)
    {
        var host = _gsBannerHost;
        var layer = _gsBanner;
        if (host == null || layer == null) return;
        _gsBannerHost = null;
        _gsBanner = null;
        ++_gsBannerVersion;
        void Drop()
        {
            _cc?.Children.Remove(host);
            BnDispose(layer);
            host.Dispose();
        }
        if (instant || Reduced) { Drop(); return; }
        host.StartAnimation("Opacity", CsS(0, 300, CsQuart));
        After(320, Drop);
    }

    /// <summary>设置 › 外观 › 游戏页面用横幅：按 A 立即生效（游戏库当前页原地重建，开着的游戏设置换背景）。</summary>
    private void SetGameBanner(bool on)
    {
        if (_backgroundSettings.GameBanner == on) return;
        _backgroundSettings = _backgroundSettings with { GameBanner = on };
        _backgroundSettings.Save();
        Log.Info($"game banner {(on ? "on" : "off")}");
        if (_cs != null && _cs.Page != null)
        {
            if (!on) CsBackdropFollow();
            CsWallVisuals();
            if (_cs.Page.Style != HomeStyle.Wall || _cs.Tab != 0) CsShowTab(_cs.Tab, 0, entry: true);
        }
        if (_gsItem != null && _stOpen)
        {
            if (on) GsBannerOpen(_gsItem, fromLibrary: false);
            else GsBannerClose();
        }
        UpdateBackdropRunning();
    }

    /// <summary>横幅关掉时：墙下面的老样式背景跟着游戏库当前选中的游戏换色。</summary>
    private void CsBackdropFollow()
    {
        if (_backdrop == null || BannerOn || CsSpotItem() is not { } g) return;
        var art = g.Art;
        string? path = art == null ? null : art.Has('p') ? art.File('p') : art.Has('x') ? art.File('x') : art.Has('h') ? art.File('h') : null;
        _backdrop.SetCover(path, a => _dq.TryEnqueue(() => { a(); EnsureBackdropLoop(); }));
        EnsureBackdropLoop();
    }
}
