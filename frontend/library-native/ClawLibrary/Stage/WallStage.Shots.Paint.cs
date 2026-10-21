// 截图页的画法（设计 px，1280 × 800 舞台，× _ccK 到 DIP）。字号、透明度和别的子页同一套：标题 24 / 700，
// 小字节头 13 / 500 / 42 %，行字 17，读数小字 14，等宽数字 Cascadia Mono。图片用 LoadedImageSurface 按显示尺寸解码，
// 只给屏幕附近的格子取图，远了就还给图片缓存。
// 日历：每格一个 ContainerVisual（投影、连拍的两层、底色、图、字），焦点那格放大 1.06、投影亮起、图不压暗。
// 放映 / 冲印：同一套轮播（焦点居中，左右各露一张，再外面的淡掉），只是卡的尺寸、侧边缩放、倾斜不一样。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const float ShGridTop = 104, ShGridX = 200, ShCellW = 200, ShCellH = 125, ShCellGap = 10, ShDayGap = 48;
    private static readonly FontSpec ShMono = new("Cascadia Mono", 400);
    private static readonly FontSpec ShDisplay = new("Segoe UI Variable Display", 200);
    private static readonly Color ShDanger = Gfx.Rgba(0xff, 0x7a, 0x66);
    private static readonly float[] ShTilt = { -2.2f, 1.6f, -1.1f, 2.4f, -1.8f, 0.9f, -2.6f, 1.3f };

    private sealed class ShCard
    {
        public int Index;
        public ContainerVisual El = null!;
        public SpriteVisual Photo = null!, Shadow = null!;
        public SpriteVisual? Paper, Film;
        public LoadedImageSurface? Ref;
        public string PaperDrawn = "";
    }

    /// <summary>轮播的参数：卡的尺寸、相片在卡里的位置、卡顶、卡心距、两侧的缩放 / 透明度 / 下沉。</summary>
    private readonly record struct ShCarSpec(float CardW, float CardH, float PhotoX, float PhotoY, float PhotoW, float PhotoH, float Top, float Pitch, float Side, float SideOpacity, float SideDy, bool Tilt);

    private ShCarSpec ShSpec => _shStyle == ShStyle.Prints
        ? new ShCarSpec(680, 492, 20, 20, 640, 400, 92, 600, 0.62f, 0.55f, 40, true)
        : new ShCarSpec(768, 480, 0, 0, 768, 480, 100, 664, 0.667f, 0.42f, 0, false);

    private ContainerVisual? _shGrid, _shGridView, _shCar, _shStrip;
    private SpriteVisual? _shInfo, _shIndex, _shStripBar;
    private float _shScroll, _shContentH, _shStripX;
    private readonly Dictionary<int, ShCard> _shCards = new();
    private readonly List<(ShUnit Unit, SpriteVisual Sprite, float X)> _shThumbs = new();
    private readonly List<LoadedImageSurface?> _shThumbRefs = new();
    private readonly Dictionary<(int, int, int), CompositionDrawingSurface> _shShadows = new();
    private CompositionDrawingSurface? _shFilm;
    private float _shSurfaceK;

    // 大图
    private ContainerVisual? _shV, _shVOver;
    private SpriteVisual _shVImg = null!, _shVTitle = null!, _shVIdx = null!, _shVActs = null!, _shVScrimTop = null!, _shVScrimRight = null!;
    private LoadedImageSurface? _shVRef;
    private string? _shVPath;

    private float ShRaster => _ccK * Chrome.Dpi;

    // ---------------- 整页 ----------------

    private void ShPaintAll()
    {
        if (_sh == null) return;
        if (_shSurfaceK != _ccK) { _shShadows.Clear(); _shFilm = null; _shSurfaceK = _ccK; }
        ShPaintTitle();
        ShPaintTabs();
        switch (_shStyle)
        {
            case ShStyle.Calendar:
                foreach (var u in _shUnits) ShCalOverlay(u);
                break;
            default:
                foreach (var c in _shCards.Values) ShCardPaper(c);
                ShPaintInfo();
                break;
        }
        if (_shUnits.Count == 0) ShPaintEmpty();
    }

    private void ShPaintTitle()
    {
        float k = _ccK, raster = ShRaster;
        using var tl = TextKit.Layout(G.Device, "截图", TextKit.Ui(700), 24, 400, CanvasHorizontalAlignment.Left);
        var bb = tl.LayoutBounds;
        float w = (float)bb.Width + 4, th = (float)bb.Height + 4;
        CcSet(_shTitle, CcDraw(w, th, raster, ds => ds.DrawTextLayout(tl, -(float)bb.X, -(float)bb.Y, CcText)));
        _shTitle.Size = new Vector2(w * k, th * k);
        _shTitle.Offset = new Vector3(40 * k, 32 * k, 0);
    }

    /// <summary>标题右边的游戏：名字 17 + 张数（等宽 13）；当前项白字、名字下面一道 2 px 下划线，其余 45 %。放不下时只露当前项附近的。</summary>
    private void ShPaintTabs()
    {
        float k = _ccK, raster = ShRaster;
        const float X0 = 116, MaxW = 860, H = 44, Pad = 12, Gap = 6;
        _shTabHits.Clear();
        if (_shTabs.Count == 0 || !_shLoaded) { ((CompositionSurfaceBrush)_shTabsSprite.Brush).Surface = null; return; }
        var ui = TextKit.Ui(400);
        var items = new List<(int I, float NameW, float CountW, float W)>();
        using (var probe = new CanvasCommandList(G.Device))
        using (var ds = probe.CreateDrawingSession())
            for (int i = 0; i < _shTabs.Count; i++)
            {
                float nw = PWidth(ds, _shTabs[i].Name, ui, 17), cw = PWidth(ds, _shTabs[i].Shots.Count.ToString(), ShMono, 13);
                items.Add((i, nw, cw, Pad + nw + Gap + cw + Pad));
            }
        int first = 0;
        while (first < _shTab && items.Skip(first).TakeWhile(t => t.I <= _shTab).Sum(t => t.W) > MaxW) first++;
        var shown = new List<(int I, float NameW, float CountW, float W, float X)>();
        float x = 0;
        for (int j = first; j < items.Count && x + items[j].W <= MaxW; j++) { shown.Add((items[j].I, items[j].NameW, items[j].CountW, items[j].W, x)); x += items[j].W + 8; }
        // 点击命中按序号对齐：没露出来的项给一个空范围
        for (int i = 0; i < _shTabs.Count; i++)
        {
            var s = shown.FirstOrDefault(t => t.I == i);
            _shTabHits.Add(s.W > 0 ? (X0 + s.X, s.W) : (-1000, 0));
        }
        float total = Math.Max(1, x);
        CcSet(_shTabsSprite, CcDraw(total, H, raster, ds =>
        {
            foreach (var s in shown)
            {
                bool cur = s.I == _shTab;
                var col = cur ? CcText : Gfx.WithAlpha(CcText, 0.45);
                float cy = H / 2;
                PText(ds, _shTabs[s.I].Name, ui, 17, col, s.X + Pad, cy, s.NameW + 4);
                PText(ds, _shTabs[s.I].Shots.Count.ToString(), ShMono, 13, Gfx.WithAlpha(CcText, cur ? 0.5 : 0.4), s.X + Pad + s.NameW + Gap, cy + 1, s.CountW + 4);
                if (cur) ds.FillRectangle(s.X + Pad, cy + 13, s.NameW, 2, CcText);
            }
        }));
        _shTabsSprite.Size = new Vector2(total * k, H * k);
        _shTabsSprite.Offset = new Vector3(X0 * k, 26 * k, 0);
    }

    private void ShPaintEmpty()
    {
        if (_shInfo == null) return;
        float k = _ccK;
        const float W = 800, H = 80;
        string folder = Captures.GameBarFolder();
        CcSet(_shInfo, CcDraw(W, H, ShRaster, ds =>
        {
            PText(ds, _shLoaded ? "还没有截图" : "正在读取…", TextKit.Ui(400), 17, Gfx.WithAlpha(CcText, 0.8), 0, 20, W, CanvasHorizontalAlignment.Center);
            if (_shLoaded) PText(ds, $"Game Bar 截图（{folder}）、Steam 截图和 Steam 上传的截图都会出现在这里", TextKit.Ui(400), 14, Gfx.WithAlpha(CcText, 0.5), 0, 52, W, CanvasHorizontalAlignment.Center);
        }));
        _shInfo.Size = new Vector2(W * k, H * k);
        _shInfo.Offset = new Vector3(240 * k, 360 * k, 0);
    }

    // ---------------- 换排版 / 换游戏：整块重建 ----------------

    private void ShBuildBody()
    {
        ShReleaseBody();
        float k = _ccK;
        _shInfo = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        if (_shUnits.Count == 0) { _shBody.Children.InsertAtTop(_shInfo); return; }
        if (_shStyle == ShStyle.Calendar) ShCalBuild();
        else
        {
            _shCar = G.Container(relative: false);
            _shBody.Children.InsertAtTop(_shCar);
            _shBody.Children.InsertAtTop(_shInfo);
            if (_shStyle == ShStyle.Projector) ShStripBuild();
            else
            {
                _shIndex = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
                _shBody.Children.InsertAtTop(_shIndex);
            }
            ShCarSync(false);
        }
    }

    /// <summary>还掉这一块用的所有图，清空。</summary>
    private void ShReleaseBody()
    {
        foreach (var u in _shUnits) ShUnitRelease(u);
        foreach (var c in _shCards.Values) { if (c.Ref != null) Images.Release(c.Ref); }
        _shCards.Clear();
        foreach (var r in _shThumbRefs) if (r != null) Images.Release(r);
        _shThumbRefs.Clear();
        _shThumbs.Clear();
        _shBody?.Children.RemoveAll();
        _shGrid = _shGridView = _shCar = _shStrip = null;
        _shInfo = _shIndex = _shStripBar = null;
    }

    private void ShReleaseAll()
    {
        ShReleaseBody();
        if (_shVRef != null) { Images.Release(_shVRef); _shVRef = null; }
        _shVPath = null;
        if (_shV != null) { _shV.IsVisible = false; _shVImg.Brush = null; }
        _shViewer = false;
    }

    private void ShUnitRelease(ShUnit u)
    {
        if (u.Ref != null) { Images.Release(u.Ref); u.Ref = null; }
        if (u.Img != null) u.Img.Brush = null;
        if (u.Backs != null) foreach (var b in u.Backs) b.Brush = null;
    }

    private CompositionSurfaceBrush ShBrush(ICompositionSurface s, CompositionStretch stretch = CompositionStretch.UniformToFill)
    {
        var b = G.C.CreateSurfaceBrush(s);
        b.Stretch = stretch;
        b.HorizontalAlignmentRatio = 0.5f;
        b.VerticalAlignmentRatio = 0.5f;
        return b;
    }

    /// <summary>直角块的投影（同尺寸共用一张）：四周各留 pad，下沉 dy。</summary>
    private CompositionDrawingSurface ShShadow(float w, float h, float blur, float dy, double alpha)
    {
        var key = ((int)w, (int)h, (int)(blur * 10 + alpha * 1000));
        if (_shShadows.TryGetValue(key, out var s)) return s;
        float pad = blur * 3 + dy;
        s = CcDraw(w + 2 * pad, h + 2 * pad, _ccK * Chrome.Dpi, ds =>
        {
            using var cl = new CanvasCommandList(ds);
            using (var c = cl.CreateDrawingSession()) c.FillRectangle(pad, pad + dy, w, h, Colors.Black);
            using var sh = new ShadowEffect { Source = cl, BlurAmount = blur, ShadowColor = Gfx.Rgba(0, 0, 0, alpha) };
            ds.DrawImage(sh);
        });
        _shShadows[key] = s;
        return s;
    }

    private static float ShShadowPad(float blur, float dy) => blur * 3 + dy;

    // ---------------- 日历 ----------------

    private void ShCalBuild()
    {
        float k = _ccK, raster = ShRaster;
        _shGridView = G.Container(relative: false);
        _shGridView.Offset = new Vector3(0, ShGridTop * k, 0);
        _shGridView.Size = new Vector2((float)View.Width, (float)View.Height - ShGridTop * k);
        _shGridView.Clip = G.C.CreateInsetClip();
        _shGrid = G.Container(relative: false);
        _shGridView.Children.InsertAtTop(_shGrid);
        _shBody.Children.InsertAtTop(_shGridView);
        _shBody.Children.InsertAtTop(_shInfo!);

        float y = 8;
        int i = 0;
        while (i < _shUnits.Count)
        {
            var date = _shUnits[i].Top.Time.Date;
            int j = i;
            while (j < _shUnits.Count && _shUnits[j].Top.Time.Date == date) j++;
            int n = j - i;
            bool hero = n >= 3;
            var used = new List<bool[]>();
            bool Free(int r, int c) => r >= used.Count || !used[r][c];
            void Take(int r, int c) { while (used.Count <= r) used.Add(new bool[5]); used[r][c] = true; }
            for (int u = i; u < j; u++)
            {
                var unit = _shUnits[u];
                if (u == i && hero)
                {
                    Take(0, 0); Take(0, 1); Take(1, 0); Take(1, 1);
                    unit.Hero = true; unit.X = ShGridX; unit.Y = y; unit.W = ShCellW * 2 + ShCellGap; unit.H = ShCellH * 2 + ShCellGap;
                    continue;
                }
                int cell = 0;
                while (!Free(cell / 5, cell % 5)) cell++;
                Take(cell / 5, cell % 5);
                unit.X = ShGridX + (cell % 5) * (ShCellW + ShCellGap);
                unit.Y = y + (cell / 5) * (ShCellH + ShCellGap);
                unit.W = ShCellW; unit.H = ShCellH;
            }
            // 日期：大号日 + 月 · 星期 + 张数
            var dateSprite = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            int count = _shUnits.Skip(i).Take(n).Sum(u => u.Shots.Count);
            string label = ShDay(date);
            CcSet(dateSprite, CcDraw(150, 120, raster, ds =>
            {
                using var big = TextKit.Layout(ds, date.Day.ToString("00"), ShDisplay, 72, 150, CanvasHorizontalAlignment.Left);
                var bb = big.LayoutBounds;
                ds.DrawTextLayout(big, -(float)bb.X - 3, -(float)bb.Y - 8, Gfx.WithAlpha(CcText, 0.95));
                PText(ds, label.Replace(" · ", " "), TextKit.Ui(400), 14, Gfx.WithAlpha(CcText, 0.62), 0, 84, 150);
                PText(ds, $"{count} 张", ShMono, 13, Gfx.WithAlpha(CcText, 0.5), 0, 106, 150);
            }));
            dateSprite.Size = new Vector2(150 * k, 120 * k);
            dateSprite.Offset = new Vector3(40 * k, y * k, 0);
            _shGrid.Children.InsertAtTop(dateSprite);
            y += used.Count * (ShCellH + ShCellGap) - ShCellGap + ShDayGap;
            i = j;
        }
        _shContentH = y - ShDayGap + 32;

        foreach (var u in _shUnits) ShCalMakeTile(u);
        _shScroll = 0;
        _shGrid.Offset = Vector3.Zero;
        ShCalFocusVisual(_shUnits[_shFocus], true, true);
        ShCalScroll(false);
    }

    private void ShCalMakeTile(ShUnit u)
    {
        float k = _ccK;
        var el = G.C.CreateContainerVisual();
        el.Offset = new Vector3(u.X * k, u.Y * k, 0);
        el.Size = new Vector2(u.W * k, u.H * k);
        el.CenterPoint = new Vector3(u.W * k / 2, u.H * k / 2, 0);
        const float blur = 22, dy = 16;
        float pad = ShShadowPad(blur, dy);
        u.Shadow = G.Sprite(G.C.CreateSurfaceBrush(ShShadow(u.W, u.H, blur, dy, 0.6)), relative: false);
        u.Shadow.Size = new Vector2((u.W + 2 * pad) * k, (u.H + 2 * pad) * k);
        u.Shadow.Offset = new Vector3(-pad * k, -pad * k, 0);
        u.Shadow.Opacity = 0;
        el.Children.InsertAtTop(u.Shadow);
        if (u.Shots.Count > 1)
        {
            // 连拍：后面再叠两张，往右上错开
            u.Backs = new SpriteVisual[2];
            for (int b = 0; b < 2; b++)
            {
                var back = G.Sprite(null, relative: false);
                back.Size = new Vector2(u.W * k, u.H * k);
                back.Offset = new Vector3((8 - 4 * b) * k, -(8 - 4 * b) * k, 0);
                back.Opacity = b == 0 ? 0.35f : 0.6f;
                u.Backs[b] = back;
                el.Children.InsertAtTop(back);
            }
        }
        var baseFill = G.Sprite(G.Solid(Gfx.Rgba(20, 20, 24)), relative: false);
        baseFill.Size = new Vector2(u.W * k, u.H * k);
        el.Children.InsertAtTop(baseFill);
        u.Img = G.Sprite(null, relative: false);
        u.Img.Size = new Vector2(u.W * k, u.H * k);
        u.Img.Opacity = 0.78f;
        el.Children.InsertAtTop(u.Img);
        u.Over = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        u.Over.Size = new Vector2(u.W * k, u.H * k);
        el.Children.InsertAtTop(u.Over);
        u.El = el;
        u.OverDrawn = "";
        _shGrid!.Children.InsertAtTop(el);
        ShCalOverlay(u);
    }

    /// <summary>格子上的字：收藏的小圆点（左上）、连拍张数（右下）、焦点那格的时间（左下）。没有字就不占表面。</summary>
    private void ShCalOverlay(ShUnit u)
    {
        if (u.Over == null) return;
        bool fav = u.Shots.Any(s => _shFavs.Contains(s.Path));
        bool focused = _shUnits.IndexOf(u) == _shFocus;
        var acc = PerfAccent;
        string key = $"{fav}|{focused}|{u.Shots.Count}|{acc}|{_ccK}";
        if (key == u.OverDrawn) return;
        u.OverDrawn = key;
        var brush = (CompositionSurfaceBrush)u.Over.Brush;
        if (!fav && !focused && u.Shots.Count == 1) { (brush.Surface as IDisposable)?.Dispose(); brush.Surface = null; return; }
        string time = u.Top.Time.ToString("HH:mm:ss");
        CcSet(u.Over, CcDraw(u.W, u.H, ShRaster * 1.06f, ds =>
        {
            void Shadowed(string s, FontSpec f, float size, float x, float cy, CanvasHorizontalAlignment al, float width)
            {
                PText(ds, s, f, size, Gfx.Rgba(0, 0, 0, 0.75), x, cy + 1, width, al);
                PText(ds, s, f, size, CcText, x, cy, width, al);
            }
            if (fav)
            {
                ds.FillCircle(11.5f, 11.5f, 5.5f, Gfx.Rgba(9, 9, 11, 0.35));
                ds.FillCircle(11.5f, 11.5f, 3.5f, acc);
            }
            if (u.Shots.Count > 1) Shadowed($"连拍 {u.Shots.Count}", TextKit.Ui(500), 12, 0, u.H - 13, CanvasHorizontalAlignment.Right, u.W - 9);
            if (focused) Shadowed(time, ShMono, 13, 10, u.H - 14, CanvasHorizontalAlignment.Left, 120);
        }));
    }

    private void ShCalFocusVisual(ShUnit u, bool on, bool instant)
    {
        if (u.El == null) return;
        if (on) { _shGrid!.Children.Remove(u.El); _shGrid.Children.InsertAtTop(u.El); }
        var scale = new Vector3(on ? 1.06f : 1f, on ? 1.06f : 1f, 1);
        if (instant || Reduced)
        {
            u.El.StopAnimation("Scale"); u.El.Scale = scale;
            u.Img!.StopAnimation("Opacity"); u.Img.Opacity = on ? 1 : 0.78f;
            u.Shadow!.StopAnimation("Opacity"); u.Shadow.Opacity = on ? 1 : 0;
        }
        else
        {
            u.El.StartAnimation("Scale", G.SpringV3(scale, Spring.Button));
            u.Img!.StartAnimation("Opacity", ToScalar(on ? 1 : 0.78f, 160, EaseOutCss));
            u.Shadow!.StartAnimation("Opacity", ToScalar(on ? 1 : 0, 180, EaseOutCss));
        }
        ShCalOverlay(u);
    }

    /// <summary>焦点格（带上下 28 px 余量）露在框里就不动，出框才滚到刚好露出来；弹簧滚，连按接着滚。</summary>
    private void ShCalScroll(bool animate)
    {
        if (_shGrid == null || _shUnits.Count == 0) return;
        float k = _ccK, viewH = (float)View.Height / k - ShGridTop;
        var u = _shUnits[_shFocus];
        float target = _shScroll;
        const float pad = 28;
        if (u.Y - pad < target) target = u.Y - pad;
        if (u.Y + u.H + pad > target + viewH) target = u.Y + u.H + pad - viewH;
        target = Math.Clamp(target, 0, Math.Max(0, _shContentH - viewH));
        _shScroll = target;
        if (!animate || Reduced) { _shGrid.StopAnimation("Offset.Y"); _shGrid.Offset = new Vector3(0, -target * k, 0); }
        else _shGrid.StartAnimation("Offset.Y", G.SpringScalar(-target * k, Spring.GridCenter));
        ShCalImages();
    }

    /// <summary>屏幕上下各多一屏的格子取图；离得超过两屏的还回去。</summary>
    private void ShCalImages()
    {
        float k = _ccK, viewH = (float)View.Height / k - ShGridTop, raster = ShRaster * 1.06f;
        foreach (var u in _shUnits)
        {
            bool near = u.Y + u.H >= _shScroll - viewH && u.Y <= _shScroll + 2 * viewH;
            bool far = u.Y + u.H < _shScroll - 2 * viewH || u.Y > _shScroll + 3 * viewH;
            if (near && u.Ref == null)
            {
                var s = Images.Acquire(u.Top.Path, (int)Math.Ceiling(u.W * raster), (int)Math.Ceiling(u.H * raster));
                if (s == null) continue;
                u.Ref = s;
                u.Img!.Brush = ShBrush(s);
                if (u.Backs != null)
                    foreach (var b in u.Backs) b.Brush = ShBrush(s);
            }
            else if (far && u.Ref != null) ShUnitRelease(u);
        }
    }

    // ---------------- 放映 / 冲印：轮播 ----------------

    private (Vector3 Offset, float Scale, float Rot, float Opacity) ShCardPose(int index, int d)
    {
        var p = ShSpec;
        int ad = Math.Abs(d);
        float cx = 640 + d * p.Pitch;
        float y = p.Top + (d != 0 ? p.SideDy : 0);
        float s = d == 0 ? 1 : p.Side;
        float rot = p.Tilt && d != 0 ? ShTilt[index % ShTilt.Length] : 0;
        float op = ad == 0 ? 1 : ad == 1 ? p.SideOpacity : 0;
        return (new Vector3((cx - p.CardW / 2) * _ccK, y * _ccK, 0), s, rot, op);
    }

    /// <summary>焦点 ±2 范围里的卡建好、摆到位（动画）；出了范围的淡出、还图、拆掉。</summary>
    private void ShCarSync(bool animate)
    {
        if (_shCar == null) return;
        int f = _shFocus;
        foreach (var (idx, card) in _shCards.ToList())
        {
            if (Math.Abs(idx - f) <= 2) continue;
            _shCards.Remove(idx);
            int d = Math.Sign(idx - f) * 3;
            var pose = ShCardPose(idx, d);
            if (animate && !Reduced)
            {
                card.El.StartAnimation("Offset", ToV3(pose.Offset, 350, CcScrollEase));
                card.El.StartAnimation("Opacity", ToScalar(0, 200, EaseOutCss));
                After(380, () => { _shCar?.Children.Remove(card.El); if (card.Ref != null) Images.Release(card.Ref); });
            }
            else { _shCar.Children.Remove(card.El); if (card.Ref != null) Images.Release(card.Ref); }
        }
        for (int idx = Math.Max(0, f - 2); idx <= Math.Min(_shUnits.Count - 1, f + 2); idx++)
        {
            bool fresh = !_shCards.TryGetValue(idx, out var card);
            if (fresh)
            {
                card = ShMakeCard(idx);
                _shCards[idx] = card;
                // 新进来的卡从更外面一格的位置滑进来
                var from = ShCardPose(idx, (idx - f) + Math.Sign(idx - f));
                card.El.Offset = from.Offset;
                card.El.Scale = new Vector3(from.Scale, from.Scale, 1);
                card.El.RotationAngleInDegrees = from.Rot;
                card.El.Opacity = 0;
            }
            var pose = ShCardPose(idx, idx - f);
            var sc = new Vector3(pose.Scale, pose.Scale, 1);
            if (animate && !Reduced)
            {
                card!.El.StartAnimation("Offset", ToV3(pose.Offset, 350, CcScrollEase));
                card.El.StartAnimation("Scale", ToV3(sc, 350, CcScrollEase));
                card.El.StartAnimation("RotationAngleInDegrees", ToScalar(pose.Rot, 350, CcScrollEase));
                card.El.StartAnimation("Opacity", ToScalar(pose.Opacity, 300, CcScrollEase));
            }
            else
            {
                foreach (var prop in new[] { "Offset", "Scale", "RotationAngleInDegrees", "Opacity" }) card!.El.StopAnimation(prop);
                card!.El.Offset = pose.Offset; card.El.Scale = sc; card.El.RotationAngleInDegrees = pose.Rot; card.El.Opacity = pose.Opacity;
            }
            card.Shadow.Opacity = idx == f ? 1 : 0.6f;
        }
        // 叠放：离焦点越近越靠上
        foreach (var c in _shCards.Values.OrderByDescending(c => Math.Abs(c.Index - f)))
        {
            _shCar.Children.Remove(c.El);
            _shCar.Children.InsertAtTop(c.El);
        }
        ShPaintInfo();
        if (_shStyle == ShStyle.Projector) ShStripSync(animate);
    }

    private ShCard ShMakeCard(int idx)
    {
        var p = ShSpec;
        float k = _ccK;
        var u = _shUnits[idx];
        var card = new ShCard { Index = idx, El = G.C.CreateContainerVisual() };
        card.El.Size = new Vector2(p.CardW * k, p.CardH * k);
        card.El.CenterPoint = new Vector3(p.CardW * k / 2, p.CardH * k / 2, 0);
        float blur = p.Tilt ? 24 : 30, dy = p.Tilt ? 18 : 24;
        float pad = ShShadowPad(blur, dy);
        card.Shadow = G.Sprite(G.C.CreateSurfaceBrush(ShShadow(p.CardW, p.CardH, blur, dy, 0.6)), relative: false);
        card.Shadow.Size = new Vector2((p.CardW + 2 * pad) * k, (p.CardH + 2 * pad) * k);
        card.Shadow.Offset = new Vector3(-pad * k, -pad * k, 0);
        card.El.Children.InsertAtTop(card.Shadow);
        if (p.Tilt)
        {
            card.Paper = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            card.Paper.Size = new Vector2(p.CardW * k, p.CardH * k);
            card.El.Children.InsertAtTop(card.Paper);
        }
        var fill = G.Sprite(G.Solid(p.Tilt ? Gfx.Rgba(0x1b, 0x17, 0x14) : Gfx.Rgba(20, 20, 24)), relative: false);
        fill.Size = new Vector2(p.PhotoW * k, p.PhotoH * k);
        fill.Offset = new Vector3(p.PhotoX * k, p.PhotoY * k, 0);
        card.El.Children.InsertAtTop(fill);
        card.Photo = G.Sprite(null, relative: false);
        card.Photo.Size = fill.Size;
        card.Photo.Offset = fill.Offset;
        card.El.Children.InsertAtTop(card.Photo);
        if (p.Tilt)
        {
            card.Film = G.Sprite(G.C.CreateSurfaceBrush(ShFilmSurface()), relative: false);
            card.Film.Size = fill.Size;
            card.Film.Offset = fill.Offset;
            card.El.Children.InsertAtTop(card.Film);
        }
        var s = Images.Acquire(u.Top.Path, (int)Math.Ceiling(p.PhotoW * ShRaster), (int)Math.Ceiling(p.PhotoH * ShRaster));
        if (s != null) { card.Ref = s; card.Photo.Brush = ShBrush(s); }
        ShCardPaper(card);
        _shCar!.Children.InsertAtTop(card.El);
        return card;
    }

    /// <summary>冲印的相纸：暖白纸 + 细纸纹，下沿衬线字写时间（左）和日期（右，斜体），收藏的在日期前面一个小圆点。</summary>
    private void ShCardPaper(ShCard card)
    {
        if (card.Paper == null || card.Index >= _shUnits.Count) return;
        var c = _shUnits[card.Index].Top;
        var p = ShSpec;
        bool fav = _shFavs.Contains(c.Path);
        var acc = PerfAccent;
        string key = $"{c.Path}|{fav}|{acc}|{_ccK}";
        if (key == card.PaperDrawn) return;
        card.PaperDrawn = key;
        CcSet(card.Paper, CcDraw(p.CardW, p.CardH, ShRaster, ds =>
        {
            ds.FillRoundedRectangle(0, 0, p.CardW, p.CardH, 4, 4, PaperColor);
            var rnd = new Random(c.Path.GetHashCode());
            for (int i = 0; i < 2600; i++)
                ds.FillRectangle((float)rnd.NextDouble() * p.CardW, (float)rnd.NextDouble() * p.CardH, 1, 1, Gfx.WithAlpha(Colors.Black, 0.035f));
            ds.DrawRectangle(p.PhotoX - 0.5f, p.PhotoY - 0.5f, p.PhotoW + 1, p.PhotoH + 1, Gfx.WithAlpha(Colors.Black, 0.08f), 1);
            float cy = p.PhotoY + p.PhotoH + (p.CardH - p.PhotoY - p.PhotoH) / 2;
            PText(ds, c.Time.ToString("HH:mm"), TextKit.TitleCjk, 26, InkColor, 24, cy, 200);
            string md = c.Time.ToString("MM.dd");
            var dateFont = new FontSpec("Georgia", 400, true);
            float dw = PWidth(ds, md, dateFont, 15);
            PText(ds, md, dateFont, 15, Gfx.Rgba(0x6b, 0x62, 0x5a), p.CardW - 24 - dw, cy + 2, dw + 4);
            if (fav) ds.FillCircle(p.CardW - 24 - dw - 14, cy + 2, 4.5f, acc);
        }));
    }

    /// <summary>冲印相片上压的一层：偏暖、发灰的胶片色，四周暗角，右上角一道暖色漏光，细颗粒。</summary>
    private CompositionDrawingSurface ShFilmSurface()
    {
        if (_shFilm != null) return _shFilm;
        var p = ShSpec;
        float w = p.PhotoW, h = p.PhotoH;
        _shFilm = CcDraw(w, h, ShRaster, ds =>
        {
            ds.FillRectangle(0, 0, w, h, Gfx.Rgba(0x8a, 0x76, 0x60, 0.12));
            using (var vig = new CanvasRadialGradientBrush(ds, new[]
            {
                new CanvasGradientStop { Position = 0.55f, Color = Gfx.Rgba(20, 12, 6, 0) },
                new CanvasGradientStop { Position = 1f, Color = Gfx.Rgba(20, 12, 6, 0.45) },
            }) { Center = new Vector2(w / 2, h / 2), RadiusX = w * 0.62f, RadiusY = h * 0.72f })
                ds.FillRectangle(0, 0, w, h, vig);
            using (var leak = new CanvasRadialGradientBrush(ds, Gfx.Rgba(255, 150, 70, 0.22), Gfx.Rgba(255, 150, 70, 0))
                   { Center = new Vector2(w, 0), RadiusX = w * 0.6f, RadiusY = h * 0.8f })
                ds.FillRectangle(0, 0, w, h, leak);
            var rnd = new Random(7);
            for (int i = 0; i < 9000; i++)
            {
                bool light = rnd.Next(2) == 0;
                ds.FillRectangle((float)rnd.NextDouble() * w, (float)rnd.NextDouble() * h, 1, 1, light ? Gfx.Rgba(255, 255, 255, 0.05) : Gfx.Rgba(0, 0, 0, 0.07));
            }
        });
        return _shFilm;
    }

    /// <summary>
    /// 放映：大图下面一行 —— 时间（26 细体）、日期、收藏；右边尺寸和第几张。
    /// 冲印：相纸下面居中两行 —— 游戏 · 日期 · 时间，尺寸 · 收藏；右下角第几张。
    /// </summary>
    private void ShPaintInfo()
    {
        if (_shInfo == null || _shUnits.Count == 0 || _shStyle == ShStyle.Calendar) return;
        float k = _ccK, raster = ShRaster;
        var c = _shUnits[_shFocus].Top;
        bool fav = _shFavs.Contains(c.Path);
        string pos = $"{(_shFocus + 1).ToString(_shUnits.Count >= 100 ? "000" : "00")} / {_shUnits.Count}";
        string size = ShSizeText(c);
        var acc = PerfAccent;
        if (_shStyle == ShStyle.Projector)
        {
            const float W = 768, H = 44;
            CcSet(_shInfo, CcDraw(W, H, raster, ds =>
            {
                float cy = H / 2, x = 0;
                string t = c.Time.ToString("HH:mm:ss");
                var light = TextKit.Ui(300);
                float tw = PWidth(ds, t, light, 26);
                PText(ds, t, light, 26, CcText, x, cy, tw + 4);
                x += tw + 16;
                string day = ShDay(c.Time);
                float dw = PWidth(ds, day, TextKit.Ui(400), 14);
                PText(ds, day, TextKit.Ui(400), 14, Gfx.WithAlpha(CcText, 0.62), x, cy + 2, dw + 4);
                x += dw + 16;
                if (fav)
                {
                    ds.FillCircle(x + 3.5f, cy + 2, 3.5f, acc);
                    PText(ds, "已收藏", TextKit.Ui(400), 14, Gfx.WithAlpha(CcText, 0.62), x + 13, cy + 2, 80);
                }
                float pw = PWidth(ds, pos, ShMono, 13);
                PText(ds, pos, ShMono, 13, Gfx.WithAlpha(CcText, 0.55), W - pw, cy + 2, pw + 4);
                float sw = PWidth(ds, size, TextKit.Ui(400), 14);
                PText(ds, size, TextKit.Ui(400), 14, Gfx.WithAlpha(CcText, 0.55), W - pw - 20 - sw, cy + 2, sw + 4);
            }));
            _shInfo.Size = new Vector2(W * k, H * k);
            _shInfo.Offset = new Vector3(256 * k, 594 * k, 0);
        }
        else
        {
            const float W = 800, H = 64;
            string line1 = $"{c.Game} · {ShDay(c.Time)} · {c.Time:HH:mm:ss}";
            string line2 = size + (fav ? " · 已收藏" : "");
            CcSet(_shInfo, CcDraw(W, H, raster, ds =>
            {
                PText(ds, line1, TextKit.Ui(400), 17, Gfx.WithAlpha(CcText, 0.8), 0, 14, W, CanvasHorizontalAlignment.Center);
                PText(ds, line2, TextKit.Ui(400), 14, Gfx.WithAlpha(CcText, 0.5), 0, 44, W, CanvasHorizontalAlignment.Center);
            }));
            _shInfo.Size = new Vector2(W * k, H * k);
            _shInfo.Offset = new Vector3(240 * k, 616 * k, 0);
            if (_shIndex != null)
            {
                const float IW = 160, IH = 20;
                CcSet(_shIndex, CcDraw(IW, IH, raster, ds => PText(ds, pos, ShMono, 13, Gfx.WithAlpha(CcText, 0.5), 0, IH / 2, IW, CanvasHorizontalAlignment.Right)));
                _shIndex.Size = new Vector2(IW * k, IH * k);
                _shIndex.Offset = new Vector3((1240 - IW) * k, (800 - 32 - IH) * k, 0);
            }
        }
    }

    // ---------------- 放映的小图条 ----------------

    private const float ShThumbW = 52, ShThumbH = 33, ShThumbGap = 5, ShStripTop = 668, ShStripGroupGap = 24;

    private void ShStripBuild()
    {
        float k = _ccK, raster = ShRaster;
        _shStrip = G.Container(relative: false);
        _shStrip.Offset = new Vector3(0, ShStripTop * k, 0);
        _shBody.Children.InsertAtTop(_shStrip);
        float x = 0;
        DateTime? day = null;
        foreach (var u in _shUnits)
        {
            var d = u.Top.Time.Date;
            if (day != d)
            {
                if (day != null) x += ShStripGroupGap - ShThumbGap;
                day = d;
                var label = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
                string text = d.Year == DateTime.Now.Year ? d.ToString("MM.dd") : d.ToString("yyyy.MM.dd");
                CcSet(label, CcDraw(90, 16, raster, ds => PText(ds, text, ShMono, 12, Gfx.WithAlpha(CcText, 0.5), 0, 8, 90)));
                label.Size = new Vector2(90 * k, 16 * k);
                label.Offset = new Vector3(x * k, 0, 0);
                _shStrip.Children.InsertAtTop(label);
            }
            var fill = G.Sprite(G.Solid(Gfx.Rgba(20, 20, 24)), relative: false);
            var sprite = G.Sprite(null, relative: false);
            foreach (var s in new[] { fill, sprite })
            {
                s.Size = new Vector2(ShThumbW * k, ShThumbH * k);
                s.Offset = new Vector3(x * k, 22 * k, 0);
                _shStrip.Children.InsertAtTop(s);
            }
            sprite.Opacity = 0.5f;
            _shThumbs.Add((u, sprite, x));
            _shThumbRefs.Add(null);
            x += ShThumbW + ShThumbGap;
        }
        _shStripBar = G.Sprite(G.Solid(CcText), relative: false);
        _shStripBar.Size = new Vector2(ShThumbW * k, 2 * k);
        _shStrip.Children.InsertAtTop(_shStripBar);
        _shStripX = float.NaN;
    }

    /// <summary>条放得下就居中；放不下就让焦点那张尽量居中（两端贴边）。</summary>
    private void ShStripSync(bool animate)
    {
        if (_shStrip == null || _shThumbs.Count == 0) return;
        float k = _ccK;
        float total = _shThumbs[^1].X + ShThumbW;
        var (_, _, fx) = _shThumbs[_shFocus];
        float x0 = total <= 1200 ? (1280 - total) / 2 : Math.Clamp(640 - fx - ShThumbW / 2, 1240 - total, 40);
        if (x0 != _shStripX)
        {
            bool first = float.IsNaN(_shStripX);
            _shStripX = x0;
            if (!animate || first || Reduced) { _shStrip.StopAnimation("Offset.X"); _shStrip.Offset = new Vector3(x0 * k, ShStripTop * k, 0); }
            else _shStrip.StartAnimation("Offset.X", G.SpringScalar(x0 * k, Spring.GridCenter));
        }
        for (int i = 0; i < _shThumbs.Count; i++)
        {
            var (u, s, x) = _shThumbs[i];
            float sx = x0 + x;
            bool near = sx > -300 && sx < 1580;
            if (near && _shThumbRefs[i] == null && Images.Acquire(u.Top.Path, (int)Math.Ceiling(ShThumbW * ShRaster), (int)Math.Ceiling(ShThumbH * ShRaster)) is { } r)
            {
                _shThumbRefs[i] = r;
                s.Brush = ShBrush(r);
            }
            else if (!near && _shThumbRefs[i] is { } old && (sx < -900 || sx > 2200)) { Images.Release(old); _shThumbRefs[i] = null; s.Brush = null; }
            float op = i == _shFocus ? 1 : 0.5f;
            if (animate && !Reduced) s.StartAnimation("Opacity", ToScalar(op, 160, EaseOutCss)); else { s.StopAnimation("Opacity"); s.Opacity = op; }
        }
        var bar = new Vector3(fx * k, (22 + ShThumbH + 5) * k, 0);
        if (animate && !Reduced) _shStripBar!.StartAnimation("Offset", ToV3(bar, 220, CcScrollEase));
        else { _shStripBar!.StopAnimation("Offset"); _shStripBar.Offset = bar; }
    }

    // ---------------- 焦点变了 ----------------

    private void ShFocusChanged(int old)
    {
        if (_shUnits.Count == 0) return;
        if (_shStyle == ShStyle.Calendar)
        {
            if (old >= 0 && old < _shUnits.Count) ShCalFocusVisual(_shUnits[old], false, false);
            ShCalFocusVisual(_shUnits[_shFocus], true, false);
            ShCalScroll(true);
        }
        else ShCarSync(true);
    }

    /// <summary>点到哪一格（设计 px）；没点到返回 -1。</summary>
    private int ShHitUnit(float x, float y)
    {
        if (_shUnits.Count == 0) return -1;
        if (_shStyle == ShStyle.Calendar)
        {
            if (y < ShGridTop) return -1;
            float cy = y - ShGridTop + _shScroll;
            return _shUnits.FindIndex(u => x >= u.X && x <= u.X + u.W && cy >= u.Y && cy <= u.Y + u.H);
        }
        var p = ShSpec;
        if (_shStyle == ShStyle.Projector && _shStrip != null && y >= ShStripTop + 22 && y <= ShStripTop + 22 + ShThumbH)
        {
            for (int i = 0; i < _shThumbs.Count; i++)
            {
                float tx = _shStripX + _shThumbs[i].X;
                if (x >= tx && x <= tx + ShThumbW) return i;
            }
            return -1;
        }
        if (y < p.Top || y > p.Top + p.CardH + p.SideDy) return -1;
        if (x >= 640 - p.CardW / 2 && x <= 640 + p.CardW / 2) return _shFocus;
        if (x < 640 - p.CardW / 2) return _shFocus > 0 ? _shFocus - 1 : -1;
        return _shFocus < _shUnits.Count - 1 ? _shFocus + 1 : -1;
    }

    // ---------------- 大图 ----------------

    private void EnsureShViewer()
    {
        if (_shV != null) return;
        float k = _ccK;
        _shV = G.Container();
        _shV.IsVisible = false;
        _shV.Children.InsertAtTop(G.Sprite(G.Solid(Colors.Black)));
        _shVImg = G.Sprite(null);
        _shV.Children.InsertAtTop(_shVImg);
        _shVOver = G.Container();
        _shVScrimTop = G.Sprite(G.LinearGradient(180, 1280, 140, (0, Gfx.Rgba(9, 9, 11, 0.72)), (1, Gfx.Rgba(9, 9, 11, 0))), relative: false);
        _shVScrimRight = G.Sprite(G.LinearGradient(90, 560, 800, (0, Gfx.Rgba(9, 9, 11, 0)), (0.62, Gfx.Rgba(9, 9, 11, 0.72)), (1, Gfx.Rgba(9, 9, 11, 0.72))), relative: false);
        _shVTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _shVIdx = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _shVActs = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        foreach (var v in new Visual[] { _shVScrimTop, _shVScrimRight, _shVTitle, _shVIdx, _shVActs }) _shVOver.Children.InsertAtTop(v);
        _shV.Children.InsertAtTop(_shVOver);
        _sh!.Children.InsertAtTop(_shV);
    }

    private void ShShowViewer(bool on)
    {
        EnsureShViewer();
        var v = _shV!;
        int ver = _shVersion;
        if (on)
        {
            v.IsVisible = true;
            if (Reduced) { v.StopAnimation("Opacity"); v.Opacity = 1; return; }
            v.Opacity = 0;
            v.StartAnimation("Opacity", ToScalar(1, 200, EaseOutCss));
            return;
        }
        void Hide()
        {
            if (_shViewer || _shV == null) return;
            _shV.IsVisible = false; _shV.StopAnimation("Opacity"); _shV.Opacity = 1;
            _shVImg.Brush = null;
            if (_shVRef != null) { Images.Release(_shVRef); _shVRef = null; }
            _shVPath = null;
        }
        if (Reduced) { Hide(); return; }
        v.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        After(170, () => { if (ver == _shVersion) Hide(); });
    }

    private void ShPaintViewer()
    {
        if (_shV == null || !_shViewer || ShViewerShot() is not { } c) return;
        float k = _ccK, raster = ShRaster, vw = (float)View.Width / k;
        // 图：新图解码好了才换上，不出空白帧
        if (_shVPath != c.Path)
        {
            _shVPath = c.Path;
            var s = Images.Acquire(c.Path, (int)Math.Ceiling(View.Width * Chrome.Dpi), (int)Math.Ceiling(View.Height * Chrome.Dpi));
            if (s != null)
            {
                Images.WhenLoaded(s, () =>
                {
                    if (_shVPath != c.Path || !_shViewer) { Images.Release(s); return; }
                    var old = _shVRef;
                    _shVRef = s;
                    _shVImg.Brush = ShBrush(s, CompositionStretch.Uniform);
                    if (old != null && old != s) Images.Release(old);
                });
            }
        }
        _shVOver!.StopAnimation("Opacity");
        if (Reduced) _shVOver.Opacity = _shViewerChrome ? 1 : 0;
        else _shVOver.StartAnimation("Opacity", ToScalar(_shViewerChrome ? 1 : 0, 160, EaseOutCss));

        _shVScrimTop.Size = new Vector2((float)View.Width, 140 * k);
        _shVScrimRight.Size = new Vector2(560 * k, (float)View.Height);
        _shVScrimRight.Offset = new Vector3((float)View.Width - 560 * k, 0, 0);

        // 左上：游戏名 24 / 700 + 日期时间 17
        string when = $"{ShDay(c.Time)} · {c.Time:HH:mm:ss}";
        const float TW = 900, TH = 34;
        CcSet(_shVTitle, CcDraw(TW, TH, raster, ds =>
        {
            float gw = PWidth(ds, c.Game, TextKit.Ui(700), 24);
            PText(ds, c.Game, TextKit.Ui(700), 24, CcText, 0, TH / 2, gw + 4);
            PText(ds, when, TextKit.Ui(400), 17, Gfx.WithAlpha(CcText, 0.75), gw + 16, TH / 2 + 2, TW - gw - 16);
        }));
        _shVTitle.Size = new Vector2(TW * k, TH * k);
        _shVTitle.Offset = new Vector3(40 * k, 31 * k, 0);

        string pos = $"{_shVIndex + 1:00} / {ShFlat.Count}";
        const float IW = 160, IH = 34;
        CcSet(_shVIdx, CcDraw(IW, IH, raster, ds => PText(ds, pos, ShMono, 13, Gfx.WithAlpha(CcText, 0.7), 0, IH / 2, IW, CanvasHorizontalAlignment.Right)));
        _shVIdx.Size = new Vector2(IW * k, IH * k);
        _shVIdx.Offset = new Vector3((vw - 40 - IW) * k, 31 * k, 0);

        // 右下：节头一行读数，下面四行操作（▸ 焦点，删除红字，第一下 A 之后写「再按一次 A」）
        const float AW = 340, SecH = 38, RowH = 42;
        float AH = SecH + RowH * ShActions.Length;
        bool fav = _shFavs.Contains(c.Path);
        string ext = Path.GetExtension(c.Path).TrimStart('.').ToUpperInvariant();
        string head = ShSize(c) is { } sz ? $"{c.Source} · {sz.W} × {sz.H} · {ext} · {ShBytes(c.Bytes)}" : $"{c.Source} · {ext} · {ShBytes(c.Bytes)}";
        CcSet(_shVActs, CcDraw(AW, AH, raster, ds =>
        {
            PText(ds, head, TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.5), 0, SecH - 10, AW);
            for (int i = 0; i < ShActions.Length; i++)
            {
                float cy = SecH + i * RowH + RowH / 2;
                bool f = i == _shVAction;
                bool danger = i == 3;
                var col = danger ? ShDanger : Gfx.WithAlpha(CcText, f ? 1f : 0.88f);
                if (f)
                {
                    using var pb = new CanvasPathBuilder(ds);
                    pb.BeginFigure(1, cy - 6); pb.AddLine(9, cy); pb.AddLine(1, cy + 6); pb.EndFigure(CanvasFigureLoop.Closed);
                    using var g = CanvasGeometry.CreatePath(pb);
                    ds.FillGeometry(g, CcText);
                }
                PText(ds, ShActions[i], TextKit.Ui(400), 17, col, 36, cy, 220);
                string val = i == 0 ? (fav ? "开" : "关") : i == 3 && _shArmed == c.Path ? "再按一次 A" : "";
                if (val.Length > 0)
                {
                    float w = PWidth(ds, val, TextKit.Ui(400), 17);
                    PText(ds, val, TextKit.Ui(400), 17, danger ? ShDanger : Gfx.WithAlpha(CcText, f ? 1f : 0.6f), AW - w, cy, w + 4);
                }
            }
        }));
        _shVActs.Size = new Vector2(AW * k, AH * k);
        _shVActs.Offset = new Vector3((vw - 40 - AW) * k, ((float)View.Height / k - 40 - AH) * k, 0);
    }

    private void ShViewerClick(float x, float y)
    {
        float vw = (float)View.Width / _ccK, vh = (float)View.Height / _ccK;
        const float AW = 340, SecH = 38, RowH = 42;
        float ax = vw - 40 - AW, ay = vh - 40 - SecH - RowH * ShActions.Length;
        if (_shViewerChrome && x >= ax && x <= ax + AW && y >= ay + SecH)
        {
            int row = (int)((y - ay - SecH) / RowH);
            if (row >= 0 && row < ShActions.Length)
            {
                if (row != _shVAction) { _shVAction = row; _shArmed = null; }
                ShRunAction();
                return;
            }
        }
        if (x < vw * 0.25f) { ShViewerMove(Direction.Left); return; }
        if (x > vw * 0.75f && !(_shViewerChrome && y >= ay)) { ShViewerMove(Direction.Right); return; }
        _shViewerChrome = !_shViewerChrome;
        ShPaintViewer();
    }
}
