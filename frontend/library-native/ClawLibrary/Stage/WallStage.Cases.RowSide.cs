// 「单行」首页的右侧栏（11-21 jinyu 定稿：Cocoon 样式的快捷面板，原型 artifacts/side-panel/cocoon-side-panels-v3.html）。
// 数值从 jinyu 的 Cocoon 视频逐帧量（1920×1080），换到我们 1920×1200 的设计单位：横向不变，面板只拉长中间的分组卡片。
//   入口：右上状态胶囊（R1 / 点胶囊）；再按 R1、B、点面板外关闭。左栏不做，L1 不响应。
//   开：同一个元素从胶囊变成面板——先横向展开到面板宽（~40 ms，底色从灰磨砂渐到白），再向下展开到满高（~130 ms）；
//       内容不淡入，靠裁剪边界揭开；状态行原地不动。关：先收高再收宽（~90 ms）。全程弹簧、可打断。
//   快捷区：两行 × 5 个直径 120 的圆形开关；亮度 / 音量两行滑块；下面一张分组卡片：功耗刻度条 + 帧率上限 / 分辨率 / 刷新率三行下拉。
//   焦点：和首页卡片、二级页同一套四角括号（光晕、沿周长流光、弹簧、橡皮筋）；功耗那一行不画括号，只把刻度条点亮成紫色。
// 只参考 Cocoon 的数值和行为，没有用它的代码或素材。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using ClawLibrary.Input;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Radios;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // ---------------- 几何（设计单位 1920×1200） ----------------
    private const float SpL1 = 1114, SpR = 1882, SpT = 23, SpH0 = 112, SpRad = 36;
    private const float SpH1 = CsH - 38 - SpT;                 // 底边留 38
    private const float SpBX = 1154, SpBW = 688;                // 面板里内容的左边和宽（左右各留 40）
    private const float SpTile = 120, SpTileGX = 22, SpTileGY = 18, SpTileY = 151;
    private const float SpSlH = 76, SpBriY = 427, SpVolY = 521;
    private const float SpTrackX0 = SpBX + 76 + 30, SpTrackX1 = SpBX + SpBW - 76 - 30, SpTrackW = SpTrackX1 - SpTrackX0;
    private const float SpGY0 = 617, SpGY1 = CsH - 62;          // 分组卡片（中间区域：比视频多出来的 120 都给它）
    private const float SpPwH = 138, SpSepH = 2;
    private const float SpPickW = 262, SpPickH = 56, SpOptH = 62, SpPickX = SpBX + SpBW - 14 - SpPickW;
    private const float SpMeterX = SpBX + 26, SpMeterPitch = 21, SpMeterBase = SpGY0 + 126;
    private const int SpWMin = 5, SpWMax = 35;
    private static float SpPickPitch => (SpGY1 - (SpGY0 + SpPwH)) / 3;
    private static float SpPickY(int i) => SpGY0 + SpPwH + SpSepH + i * SpPickPitch;
    private static float SpPickRowH => SpPickPitch - SpSepH;
    private const float SpSubY = 217, SpSubRowH = 84;

    private static readonly Color SpOn1 = Color.FromArgb(255, 0x8F, 0x8B, 0xFF), SpOn2 = Color.FromArgb(255, 0x73, 0x40, 0xFF);
    private static readonly Color SpTileInk = Color.FromArgb(255, 0x2A, 0x2A, 0x31), SpMute = Color.FromArgb(255, 0x5D, 0x5D, 0x68);

    private static readonly (string Id, string Icon)[] SpTiles =
    {
        ("wifi", "qam.wifi"), ("bt", "qam.bt"), ("osd", "sp.osd"), ("pad", "qam.pad"), ("gear", "sp.gear"),
        ("shot", "qam.shot"), ("kbd", "qam.kbd"), ("win", "qam.win"), ("desk", "qam.desk"), ("pwr", "qam.off"),
    };
    private static readonly string[] SpPicks = { "fps", "res", "hz" };

    private sealed class SpState
    {
        public required ContainerVisual Root, Panel, Body, Sub;
        public required SpriteVisual Fill, Status, Static, PwText, PwChev, Glow, Line, Line2, Rim, Drop;
        public required CompositionPropertySet P;
        public required CsPage Page;
        public float L0;
        public bool Open = true;
        public int Gen;
        public string Focus = "t:wifi";
        public string? SubPage, SubFrom, Armed;
        /// <summary>展开着的下拉是第几个选项行（-1 = 没有），焦点在哪个选项、向上还是向下展开。</summary>
        public int Drop_ = -1, DropSel;
        public bool DropUp;
        public float CurW, CurH, CurR;
        public CompositionDrawingSurface? StatusSurf, StaticSurf, PwTextSurf, PwChevSurf, SubSurf, DropSurf;
        public readonly Dictionary<string, (SpriteVisual Off, SpriteVisual On, CompositionDrawingSurface SOff, CompositionDrawingSurface SOn)> Tiles = new();
        public readonly SpriteVisual[] Knob = new SpriteVisual[2], Fill_ = new SpriteVisual[2];
        public readonly List<CompositionDrawingSurface> Owned = new();
        public readonly ContainerVisual[] Ticks = new ContainerVisual[SpWMax - SpWMin + 1];
        public ContainerVisual Roll = null!;
        public SpriteVisual? RollCur;
        public readonly SpriteVisual[] PickBg = new SpriteVisual[3];
        public readonly ContainerVisual[] PickVal = new ContainerVisual[3];
        public readonly SpriteVisual?[] PickCur = new SpriteVisual?[3];
        public readonly CompositionDrawingSurface?[] PickBgSurf = new CompositionDrawingSurface?[3];
        public string StatusKey = "";
        public Microsoft.UI.Dispatching.DispatcherQueueTimer? Tick, Chase;
        public int Polls;
        public float ActShown = -1;
        public bool CurShown, StaticMuted, PadMouse;
        public readonly string?[] PickText = new string?[3];
        public List<(string Id, string Icon, string Title, string Right, bool Toggle, bool On)> SubRows = new();
    }

    private SpState? _sp;
    private SettingsWriteQueue? _spWrites;
    // 最近一次读到的系统状态（跨开关面板留着：下次一打开就是对的值，后台读回来再更正）
    private JsonObject? _spState, _spDraft, _spGs;
    private LibraryItem? _spGame;
    private bool? _spWifi, _spBt;
    private bool _spMouse;
    private int _spTdp = 15;
    private (int W, int H)[] _spResList = Array.Empty<(int W, int H)>();
    private (int W, int H) _spRes;
    private List<QamWireless.Entry> _spNets = new();
    private List<string> _spBtNames = new();

    private bool SpOpenNow => _sp is { Open: true };

    // ---------------- 入口（R1 / 点胶囊） ----------------

    /// <summary>LB / RB 在单行首页：RB 开 / 关右侧栏，LB 不响应。处理了返回 true。</summary>
    private bool SpSwitchTab(int delta)
    {
        if (_cs?.Page is not { Style: HomeStyle.Row } p || _csInsert != null || _rm != null || _rs != null || _rq != null) return false;
        if (delta < 0) { Cue(Sfx.None); return true; }
        if (SpOpenNow) SpClose(); else SpOpen(p);
        return true;
    }

    private float SpPillLeft()
    {
        var now = DateTime.Now;
        float w = RwClockWidth(G.Device, RwClock(now), now.Hour < 12 ? "上午" : "下午");
        return 1496 - MathF.Round(w - RwClockW0);
    }

    /// <summary>首页搭好后在后台把右侧栏搭好、读一次状态（藏着）：按 R1 时直接开始动画，不用等现画（第一次现画要 ~130 ms）。</summary>
    private void SpPrewarm(CsPage p)
    {
        if (_cs?.Page != p || p.Rw is not { Disposed: false } || _sp is { } cur && cur.Page == p && cur.Root.Parent == _cs.Root) return;
        if (_sp is { Open: true }) return;
        SpDispose(instant: true);
        var sp = SpBuild(p);
        sp.Open = false;
        sp.Root.IsVisible = false;
        _sp = sp;
        SpPaintAll(sp);
        SpFocusTo(sp, "t:wifi", move: false);
        foreach (var v in SpCursorVis(sp)) v.Opacity = 0;
        _ = SpRefresh(sp, first: true);
    }

    /// <summary>收好后藏起来：裁剪回到胶囊大小、子页 / 下拉还原，下次打开直接用。</summary>
    private void SpHide(SpState sp)
    {
        sp.Root.IsVisible = false;
        foreach (var k in new[] { "l", "h" }) sp.P.StopAnimation(k);
        sp.L0 = SpPillLeft();
        sp.P.InsertScalar("l0", sp.L0);
        sp.P.InsertScalar("l", sp.L0);
        sp.P.InsertScalar("h", SpH0);
        if (sp.SubPage != null) { sp.SubPage = null; sp.Armed = null; sp.Body.StopAnimation("Opacity"); sp.Sub.StopAnimation("Opacity"); sp.Body.Opacity = 1; sp.Sub.Opacity = 0; }
        foreach (var v in SpCursorVis(sp)) { v.StopAnimation("Opacity"); v.Opacity = 0; }
        sp.CurShown = false;
    }

    private void SpOpen(CsPage p)
    {
        if (_cs == null || p.Rw == null) return;
        Cue(Sfx.MenuIn);
        var sp = _sp;
        bool fresh = false;
        if (sp == null || sp.Page != p || sp.Root.Parent != _cs.Root)
        {
            SpDispose(instant: true);
            sp = SpBuild(p);
            _sp = sp;
            SpPaintAll(sp);
            fresh = true;
        }
        bool hidden = !sp.Root.IsVisible;
        sp.Open = true; sp.Gen++;
        if (hidden)
        {
            if (!fresh) SpHide(sp);
            sp.Root.IsVisible = true;
            SpPaintStatus(sp);
            SpFocusTo(sp, "t:wifi", move: false);
            foreach (var v in SpCursorVis(sp)) v.Opacity = 0;
            SpCursorShow(sp, true, Reduced ? 1 : 120, delay: Reduced ? 0 : 140);
        }
        else
        {
            // 正在关：原地反向，接着展开（弹簧从当前值走）
            SpFocusTo(sp, "t:wifi", move: false);
            SpCursorShow(sp, true, 120);
        }
        RwCursorShow(p, false);
        SpAnimOpen(sp, true);
        SpStartTimers(sp);
        _ = SpRefresh(sp, first: fresh);
        Log.Info("side panel open");
    }

    private void SpClose(bool instant = false)
    {
        if (_sp is not { Open: true } sp) return;
        Cue(Sfx.MenuOut);
        sp.Open = false;
        int gen = ++sp.Gen;
        GamepadPoller.RepeatOverrideMs = 0;
        SpDropClose(sp, animate: false);
        sp.Tick?.Stop(); sp.Chase?.Stop();
        SpCursorShow(sp, false, 60);
        if (sp.Page.Rw is { PanelFocus: < 0 }) RwCursorShow(sp.Page, true);
        if (instant || Reduced) { SpHide(sp); return; }
        SpAnimOpen(sp, false);
        After(320, () => { if (_sp == sp && !sp.Open && sp.Gen == gen) SpHide(sp); });
        Log.Info("side panel close");
    }
    private void SpDispose(bool instant)
    {
        if (_sp is not { } sp) return;
        _sp = null;
        GamepadPoller.RepeatOverrideMs = 0;
        sp.Tick?.Stop(); sp.Chase?.Stop();
        try
        {
            _cs?.Root.Children.Remove(sp.Root);
            foreach (var s in sp.Owned) s.Dispose();
            foreach (var s in new[] { sp.StatusSurf, sp.StaticSurf, sp.PwTextSurf, sp.PwChevSurf, sp.SubSurf, sp.DropSurf }) s?.Dispose();
            foreach (var s in sp.PickBgSurf) s?.Dispose();
            foreach (var t in sp.Tiles.Values) { t.SOff.Dispose(); t.SOn.Dispose(); }
            sp.Root.Dispose();
        }
        catch (ObjectDisposedException) { }
    }

    // ---------------- 开 / 关动效 ----------------

    private void SpSpring(CompositionObject o, string prop, float to, float k, float zeta = 1, double delay = 0)
    {
        if (Reduced) { if (o is CompositionPropertySet ps) { ps.StopAnimation(prop); ps.InsertScalar(prop, to); } return; }
        var a = G.C.CreateSpringScalarAnimation();
        a.DampingRatio = zeta;
        a.Period = TimeSpan.FromSeconds(1 / Math.Sqrt(k));   // 合成器的弹簧 ω = 1 / Period（实测）
        a.FinalValue = to;
        if (delay > 0) a.DelayTime = Gfx.Ms(delay);
        try { o.StartAnimation(prop, a); }
        catch (Exception ex) { Log.Warn($"side panel spring {prop}: {ex.Message}"); o.StartAnimation(prop, CsS(to, 120, CsQuart, delay)); }
    }

    /// <summary>
    /// 开：横向（刚度 4200，~40 ms）→ 30 ms 后纵向（1250，~130 ms）；关：纵向先收（2600），50 ms 后横向收（5200）。
    /// 都是从当前值起的临界阻尼弹簧，中途反向不跳。
    /// </summary>
    private void SpAnimOpen(SpState sp, bool open)
    {
        if (open)
        {
            SpSpring(sp.P, "l", SpL1, 4200);
            SpSpring(sp.P, "h", SpH1, 1250, delay: 30);
        }
        else
        {
            SpSpring(sp.P, "h", SpH0, 2600);
            SpSpring(sp.P, "l", sp.L0, 5200, delay: 50);
        }
    }

    // ---------------- 搭画面 ----------------

    private SpState SpBuild(CsPage p)
    {
        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var panel = G.Container(relative: false);
        panel.Size = new Vector2(CsW, CsH);
        var body = G.Container(relative: false);
        body.Size = new Vector2(CsW, CsH);
        var sub = G.Container(relative: false);
        sub.Size = new Vector2(CsW, CsH);
        sub.Opacity = 0;
        var props = G.C.CreatePropertySet();
        float l0 = SpPillLeft();
        props.InsertScalar("l", l0);
        props.InsertScalar("h", SpH0);
        props.InsertScalar("l0", l0);
        props.InsertScalar("pw", _spTdp);
        props.InsertScalar("foc", 0);
        props.InsertColor("c0", Color.FromArgb(0, 110, 110, 116));
        props.InsertColor("c1", Color.FromArgb(247, 0xF4, 0xF5, 0xF8));
        var fillBrush = G.C.CreateColorBrush(Color.FromArgb(0, 110, 110, 116));
        var fill = G.Sprite(fillBrush, relative: false);
        fill.Offset = new Vector3(SpL1, SpT, 0);
        fill.Size = new Vector2(SpR - SpL1, SpH1);
        var sp = new SpState
        {
            Root = root, Panel = panel, Body = body, Sub = sub, Fill = fill, Status = Sp(), Static = Sp(), PwText = Sp(), PwChev = Sp(),
            Glow = Sp(), Line = Sp(), Line2 = Sp(), Rim = Sp(), Drop = Sp(), P = props, Page = p, L0 = l0,
        };
        // 底色跟着横向展开的进度从灰（透明）渐到白 97 %
        var color = G.C.CreateExpressionAnimation($"ColorLerpRGB(p.c0, p.c1, Clamp((p.l0 - p.l) / (p.l0 - {F(SpL1)}), 0, 1))");
        color.SetReferenceParameter("p", props);
        fillBrush.StartAnimation("Color", color);
        // 裁剪：面板的圆角矩形，左边和高度由弹簧驱动
        var geo = G.C.CreateRoundedRectangleGeometry();
        geo.CornerRadius = new Vector2(SpRad);
        var off = G.C.CreateExpressionAnimation($"Vector2(p.l, {F(SpT)})");
        off.SetReferenceParameter("p", props);
        var size = G.C.CreateExpressionAnimation($"Vector2({F(SpR)} - p.l, p.h)");
        size.SetReferenceParameter("p", props);
        geo.StartAnimation("Offset", off);
        geo.StartAnimation("Size", size);
        panel.Clip = G.C.CreateGeometricClip(geo);

        panel.Children.InsertAtTop(fill);
        panel.Children.InsertAtTop(sp.Status);
        panel.Children.InsertAtTop(body);
        panel.Children.InsertAtTop(sub);
        body.Children.InsertAtTop(sp.Static);
        // 快捷开关：关的样子在下、开的样子在上，切换只淡入淡出上面那层（200 ms）
        for (int i = 0; i < SpTiles.Length; i++)
        {
            var (x, y) = SpTileXY(i);
            var vOff = Sp(); var vOn = Sp();
            float pad = 24, w = SpTile + pad * 2;
            foreach (var v in new[] { vOff, vOn }) { v.Size = new Vector2(w, w); v.Offset = new Vector3(x - pad, y - pad, 0); v.CenterPoint = new Vector3(w / 2, w / 2, 0); body.Children.InsertAtTop(v); }
            int px = (int)Math.Ceiling(w * CsRaster);
            var sOff = G.Surface(px, px); var sOn = G.Surface(px, px);
            ((CompositionSurfaceBrush)vOff.Brush).Surface = sOff;
            ((CompositionSurfaceBrush)vOn.Brush).Surface = sOn;
            vOn.Opacity = 0;
            sp.Tiles[SpTiles[i].Id] = (vOff, vOn, sOff, sOn);
        }
        // 滑块：渐变填充（按滑钮位置横向缩放）+ 滑钮（弹簧）
        for (int k = 0; k < 2; k++)
        {
            float cy = (k == 0 ? SpBriY : SpVolY) + SpSlH / 2;
            var fillV = Sp();
            var fs = G.Surface((int)Math.Ceiling(SpTrackW * CsRaster), (int)Math.Ceiling(8 * CsRaster));
            sp.Owned.Add(fs);
            using (var ds = CanvasComposition.CreateDrawingSession(fs))
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateScale(CsRaster);
                using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, Color.FromArgb(255, 0x73, 0x40, 0xFF)) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(SpTrackW, 0) };
                ds.FillRoundedRectangle(0, 0, SpTrackW, 8, 4, 4, br);
            }
            ((CompositionSurfaceBrush)fillV.Brush).Surface = fs;
            ((CompositionSurfaceBrush)fillV.Brush).Stretch = CompositionStretch.Fill;
            fillV.Size = new Vector2(SpTrackW, 8);
            fillV.Offset = new Vector3(SpTrackX0, cy - 4, 0);
            var knob = Sp();
            float kp = 12, kw = 56 + kp * 2;
            var ks = G.Surface((int)Math.Ceiling(kw * CsRaster), (int)Math.Ceiling(kw * CsRaster));
            sp.Owned.Add(ks);
            using (var ds = CanvasComposition.CreateDrawingSession(ks))
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateTranslation(kp, kp) * Matrix3x2.CreateScale(CsRaster);
                SpShadow(ds, 0, 3, 56, 56, 28, 5, Color.FromArgb(89, 80, 40, 200));
                using var br = new CanvasLinearGradientBrush(ds.Device, Color.FromArgb(255, 0x9A, 0x9C, 0xFF), Color.FromArgb(255, 0x6F, 0x3C, 0xFF)) { StartPoint = new Vector2(8, 0), EndPoint = new Vector2(48, 56) };
                ds.FillCircle(28, 28, 28, br);
            }
            ((CompositionSurfaceBrush)knob.Brush).Surface = ks;
            knob.Size = new Vector2(kw, kw);
            knob.Offset = new Vector3(SpTrackX0 - 28 - kp, cy - 28 - kp, 0);
            var scale = G.C.CreateExpressionAnimation($"Vector3(Clamp((k.Offset.X + {F(28 + kp)} - {F(SpTrackX0)}) / {F(SpTrackW)}, 0, 1), 1, 1)");
            scale.SetReferenceParameter("k", knob);
            fillV.StartAnimation("Scale", scale);
            body.Children.InsertAtTop(fillV);
            body.Children.InsertAtTop(knob);
            sp.Fill_[k] = fillV; sp.Knob[k] = knob;
        }
        // 功耗：标题 / 读数、‹ ›、滚动数字、刻度（每根三层：灰底、深色亮、紫色亮；亮不亮按弹簧值 pw，焦点时深色换成紫色）
        body.Children.InsertAtTop(sp.PwText);
        body.Children.InsertAtTop(sp.PwChev);
        var chevOp = G.C.CreateExpressionAnimation("p.foc");
        chevOp.SetReferenceParameter("p", props);
        sp.PwChev.StartAnimation("Opacity", chevOp);
        sp.Roll = G.Container(relative: false);
        sp.Roll.Offset = new Vector3(SpRollX, SpRollY, 0);
        sp.Roll.Size = new Vector2(92, 66);
        sp.Roll.Clip = G.C.CreateInsetClip(0, 0, 0, 0);
        body.Children.InsertAtTop(sp.Roll);
        for (int w = SpWMin; w <= SpWMax; w++)
        {
            bool tall = w is 8 or 15 or 25 or 35;
            float th = tall ? 32 : 18, tx = SpMeterX + (w - SpWMin) * SpMeterPitch;
            var box = G.Container(relative: false);
            box.Offset = new Vector3(tx, SpMeterBase - th, 0);
            box.Size = new Vector2(6, th);
            box.CenterPoint = new Vector3(3, th, 0);
            for (int layer = 0; layer < 3; layer++)
            {
                CompositionBrush brush = layer switch
                {
                    0 => G.Solid(Color.FromArgb(255, 0xD6, 0xD7, 0xDD)),
                    1 => G.Solid(Color.FromArgb(255, 0x2B, 0x2B, 0x33)),
                    _ => SpVGrad(Color.FromArgb(255, 0x9A, 0x9C, 0xFF), SpOn2),
                };
                var v = G.Sprite(brush, relative: false);
                v.Size = new Vector2(6, th);
                var rr = G.C.CreateRoundedRectangleGeometry(); rr.Size = new Vector2(6, th); rr.CornerRadius = new Vector2(3);
                v.Clip = G.C.CreateGeometricClip(rr);
                if (layer > 0)
                {
                    string lit = $"Clamp((p.pw - {F(w - 0.5f)}) * 6, 0, 1)";
                    var op = G.C.CreateExpressionAnimation(layer == 1 ? $"{lit} * (1 - p.foc)" : $"{lit} * p.foc");
                    op.SetReferenceParameter("p", props);
                    v.StartAnimation("Opacity", op);
                }
                box.Children.InsertAtTop(v);
            }
            sp.Ticks[w - SpWMin] = box;
            body.Children.InsertAtTop(box);
        }
        for (int i = 0; i < 3; i++)
        {
            var bg = Sp();
            body.Children.InsertAtTop(bg);
            sp.PickBg[i] = bg;
            var val = G.Container(relative: false);
            float cy = SpPickY(i) + SpPickRowH / 2;
            val.Offset = new Vector3(SpPickX + 4, cy - SpPickH / 2, 0);
            val.Size = new Vector2(SpPickW - 50, SpPickH);
            val.Clip = G.C.CreateInsetClip(0, 0, 0, 0);
            body.Children.InsertAtTop(val);
            sp.PickVal[i] = val;
        }
        // 焦点框在内容上面、下拉卡片在最上面（括号缩到选项上时要压在卡片上）
        panel.Children.InsertAtTop(sp.Drop);
        sp.Drop.Opacity = 0;
        foreach (var v in SpCursorVis(sp)) panel.Children.InsertAtTop(v);
        root.Children.InsertAtTop(panel);
        _cs!.Root.Children.InsertAtTop(root);
        return sp;
    }

    private const float SpRollX = SpBX + SpBW - 26 - 22 - 34 - 92, SpRollY = SpGY0 + 12;

    private CompositionLinearGradientBrush SpVGrad(Color a, Color b)
    {
        var g = G.C.CreateLinearGradientBrush();
        g.StartPoint = new Vector2(0, 0); g.EndPoint = new Vector2(0, 1);
        g.ColorStops.Add(G.C.CreateColorGradientStop(0, a));
        g.ColorStops.Add(G.C.CreateColorGradientStop(1, b));
        return g;
    }

    /// <summary>拿掉一个自带画布的小精灵（滚动数字、胶囊里的值），连画布一起释放。</summary>
    private static void SpDropVis(ContainerVisual parent, Visual v)
    {
        try
        {
            if (v.Parent == parent) parent.Children.Remove(v);
            if (v is SpriteVisual s && s.Brush is CompositionSurfaceBrush b) { (b.Surface as IDisposable)?.Dispose(); b.Dispose(); }
            v.Dispose();
        }
        catch (ObjectDisposedException) { }
    }

    private static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static (float X, float Y) SpTileXY(int i) => (SpBX + i % 5 * (SpTile + SpTileGX), SpTileY + i / 5 * (SpTile + SpTileGY));

    private static IEnumerable<Visual> SpCursorVis(SpState sp) => new Visual[] { sp.Glow, sp.Line, sp.Line2, sp.Rim };

    private void SpShadow(CanvasDrawingSession ds, float x, float y, float w, float h, float r, float blur, Color c)
    {
        using var cl = new CanvasCommandList(ds.Device);
        using (var s = cl.CreateDrawingSession()) { s.Transform = ds.Transform; s.FillRoundedRectangle(x, y, w, h, r, r, c); }
        using var bl = new GaussianBlurEffect { Source = cl, BlurAmount = blur * CsRaster, BorderMode = EffectBorderMode.Soft };
        var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(bl); ds.Transform = t;
    }

    // ---------------- 画 ----------------

    private void SpPaintAll(SpState sp)
    {
        SpPaintStatus(sp);
        SpPaintStatic(sp);
        for (int i = 0; i < SpTiles.Length; i++) SpPaintTile(sp, i);
        SpApplyTiles(sp, animate: false);
        SpApplySliders(sp, animate: false);
        SpPaintPwText(sp);
        SpPaintChev(sp);
        SpRollTo(sp, _spTdp, 0);
        sp.P.InsertScalar("pw", _spTdp);
        for (int i = 0; i < 3; i++) { SpPaintPickBg(sp, i); SpSetPickVal(sp, i, 0); }
    }

    private CompositionDrawingSurface SpSurf(ref CompositionDrawingSurface? surf, SpriteVisual v, float x, float y, float w, float h)
    {
        float R = CsRaster;
        int pw = (int)Math.Ceiling(w * R), ph = (int)Math.Ceiling(h * R);
        if (surf == null || (int)surf.Size.Width != pw || (int)surf.Size.Height != ph) { surf?.Dispose(); surf = G.Surface(pw, ph); }
        ((CompositionSurfaceBrush)v.Brush).Surface = surf;
        v.Size = new Vector2(w, h);
        v.Offset = new Vector3(x, y, 0);
        return surf;
    }

    /// <summary>状态行：和首页右上胶囊一模一样的图标和时间，画在面板上（面板盖住胶囊后它原地不动）。</summary>
    private void SpPaintStatus(SpState sp)
    {
        if (sp.Page.Rw is not { } rw) return;
        var now = DateTime.Now;
        string clock = RwClock(now), ampm = now.Hour < 12 ? "上午" : "下午";
        float dx = MathF.Round(RwClockWidth(G.Device, clock, ampm) - RwClockW0);
        sp.StatusKey = RwStatusKey(rw);
        float x0 = 1300, y0 = SpT, w = SpR - x0, h = SpH0;
        var surf = SpSurf(ref sp.StatusSurf, sp.Status, x0, y0, w, h);
        using var ds = CanvasComposition.CreateDrawingSession(surf);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        RwDrawRight(ds, rw, -dx, clock, ampm);
    }

    /// <summary>不随数值动的部分：滑块两端圆按钮和轨道、分组卡片（照二级页）、功耗单位。</summary>
    private void SpPaintStatic(SpState sp)
    {
        float x0 = SpBX - 30, y0 = SpTileY - 30, w = SpBW + 60, h = SpGY1 - y0 + 30;
        var surf = SpSurf(ref sp.StaticSurf, sp.Static, x0, y0, w, h);
        using var ds = CanvasComposition.CreateDrawingSession(surf);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
        bool muted = Bool(_spState?["system"]?["muted"]);
        for (int k = 0; k < 2; k++)
        {
            float y = k == 0 ? SpBriY : SpVolY, cy = y + SpSlH / 2;
            foreach (var (cx, icon) in new[] { (SpBX + 38, k == 0 ? "sp.sunlo" : muted ? "qam.mute" : "sp.vol0"), (SpBX + SpBW - 38, k == 0 ? "qam.sun" : "qam.vol") })
            {
                ds.FillCircle(cx, cy, 38, Color.FromArgb(255, 0xE8, 0xE8, 0xEE));
                QsIcon(ds, icon, cx, cy, 34, SpTileInk, 2);
            }
            ds.FillRoundedRectangle(SpTrackX0, cy - 4, SpTrackW, 8, 4, 4, Color.FromArgb(255, 0xDC, 0xDC, 0xE3));
        }
        var seps = new List<float> { SpGY0 + SpPwH };
        for (int i = 1; i < 3; i++) seps.Add(SpGY0 + SpPwH + i * SpPickPitch);
        RsPaintGroupAt(ds, SpBX, SpBW, SpGY0, SpGY1, seps, SpSepH);
        using var uf = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 26, FontWeight = new Windows.UI.Text.FontWeight(600), VerticalAlignment = CanvasVerticalAlignment.Bottom };
        ds.DrawText("W", new Windows.Foundation.Rect(SpRollX + 96, SpRollY, 40, 66 - 10), Color.FromArgb(255, 0x5F, 0x61, 0x68), uf);
    }

    private bool SpTileOn(string id) => id switch
    {
        "wifi" => _spWifi == true,
        "bt" => _spBt == true,
        "osd" => Bool(_spDraft?["osd"]?["enabled"]),
        "pad" => !_spMouse,
        _ => false,
    };

    private void SpPaintTile(SpState sp, int i)
    {
        var (id, icon) = SpTiles[i];
        if (id == "pad" && _spMouse) icon = "qam.mouse";
        var t = sp.Tiles[id];
        float pad = 24, c = pad + SpTile / 2;
        using (var ds = CanvasComposition.CreateDrawingSession(t.SOff))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(CsRaster);
            SpShadow(ds, pad, pad + 5, SpTile, SpTile, SpTile / 2, 6, Color.FromArgb(31, 30, 30, 50));
            ds.FillCircle(c, c, SpTile / 2, Color.FromArgb(255, 0xFB, 0xFB, 0xFD));
            ds.DrawCircle(c, c, SpTile / 2 - 0.75f, Color.FromArgb(150, 255, 255, 255), 1.5f);
            QsIcon(ds, icon, c, c, 46, SpTileInk, 2.2f);
        }
        using (var ds = CanvasComposition.CreateDrawingSession(t.SOn))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(CsRaster);
            SpShadow(ds, pad, pad + 6, SpTile, SpTile, SpTile / 2, 8, Color.FromArgb(89, 115, 64, 255));
            using var br = new CanvasLinearGradientBrush(ds.Device, SpOn1, SpOn2) { StartPoint = new Vector2(pad + 20, pad), EndPoint = new Vector2(pad + SpTile - 20, pad + SpTile) };
            ds.FillCircle(c, c, SpTile / 2, br);
            QsIcon(ds, icon, c, c, 46, Colors.White, 2.2f);
        }
    }

    private void SpApplyTiles(SpState sp, bool animate)
    {
        foreach (var (id, _) in SpTiles)
        {
            var on = sp.Tiles[id].On;
            float want = SpTileOn(id) ? 1 : 0;
            if (MathF.Abs(on.Opacity - want) < 0.01f && !animate) continue;
            if (animate && !Reduced) on.StartAnimation("Opacity", CsS(want, 200, Bezier.EaseOut));
            else { on.StopAnimation("Opacity"); on.Opacity = want; }
        }
    }

    private int SpLevel(int k)
    {
        var s = _spState?["system"];
        if (k == 1 && Bool(s?["muted"])) return 0;
        return (int)Math.Clamp(SpNum(s, k == 0 ? "brightness" : "volume", 50), 0, 100);
    }

    /// <summary>滑钮到位：临界阻尼弹簧，单步约 150 ms，不回弹；填充跟着滑钮走（表达式）。</summary>
    private void SpApplySliders(SpState sp, bool animate, int only = -1)
    {
        for (int k = 0; k < 2; k++)
        {
            if (only >= 0 && k != only) continue;
            float cy = (k == 0 ? SpBriY : SpVolY) + SpSlH / 2, kp = 12;
            var to = new Vector3(SpTrackX0 + SpTrackW * SpLevel(k) / 100f - 28 - kp, cy - 28 - kp, 0);
            if (animate && !Reduced) sp.Knob[k].StartAnimation("Offset", RwSpring(to, 900));
            else RwJump(sp.Knob[k], to);
        }
    }

    private static double SpNum(JsonNode? root, string path, double fallback = 0)
    {
        JsonNode? n = root;
        foreach (var part in path.Split('.')) { if (n is not JsonObject o) return fallback; n = o[part]; }
        return n is JsonValue v && (v.TryGetValue(out double d) || (v.TryGetValue(out int i) && (d = i) == i) || (v.TryGetValue(out long l) && (d = l) == l)) ? d : fallback;
    }

    private string SpPwSub()
    {
        string who = _spGame?.Title ?? "全局";
        if (who.Length > 12) who = who[..11] + "…";
        double act = SpNum(_spState, "power.actual.package", -1), temp = SpNum(_spState, "power.live.cpuTemp", -1);
        var parts = new List<string> { who };
        if (act >= 0) parts.Add($"实际 {(_sp?.ActShown >= 0 ? _sp!.ActShown : act):0.0} W");
        if (temp > 0) parts.Add($"{temp:0} °C");
        return string.Join(" · ", parts);
    }

    private void SpPaintPwText(SpState sp)
    {
        float x0 = SpBX + 20, y0 = SpGY0 + 8, w = 440, h = 80;
        var surf = SpSurf(ref sp.PwTextSurf, sp.PwText, x0, y0, w, h);
        using var ds = CanvasComposition.CreateDrawingSession(surf);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
        using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 28, FontWeight = RsWt("功耗"), WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText("功耗", SpBX + 26, SpGY0 + 12, RmInk, tf);
        string sub = SpPwSub();
        using var sf = new CanvasTextFormat { FontFamily = RmFont(sub), FontSize = 20, FontWeight = new Windows.UI.Text.FontWeight(500), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
        ds.DrawText(sub, new Windows.Foundation.Rect(SpBX + 26, SpGY0 + 52, 410, 30), RsSub, sf);
    }

    private void SpPaintChev(SpState sp)
    {
        float x0 = SpRollX - 40, y0 = SpRollY, w = SpBX + SpBW - x0, h = 66;
        var surf = SpSurf(ref sp.PwChevSurf, sp.PwChev, x0, y0, w, h);
        using var ds = CanvasComposition.CreateDrawingSession(surf);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
        float cy = SpRollY + 36;
        QsIcon(ds, "qam.l", SpRollX - 15, cy, 22, SpOn2, 2.5f);
        QsIcon(ds, "qam.r", SpBX + SpBW - 26 - 11, cy, 22, SpOn2, 2.5f);
    }

    private CompositionDrawingSurface SpTextSurf(string text, float w, float h, float size, string font, ushort weight, Color c, bool right, out Vector2 dim)
    {
        var surf = G.Surface((int)Math.Ceiling(w * CsRaster), (int)Math.Ceiling(h * CsRaster));
        using (var ds = CanvasComposition.CreateDrawingSession(surf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(CsRaster);
            using var f = new CanvasTextFormat { FontFamily = font, FontSize = size, FontWeight = new Windows.UI.Text.FontWeight(weight), VerticalAlignment = CanvasVerticalAlignment.Center, HorizontalAlignment = right ? CanvasHorizontalAlignment.Right : CanvasHorizontalAlignment.Left, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(text, new Windows.Foundation.Rect(0, 0, w, h), c, f);
        }
        dim = new Vector2(w, h);
        return surf;
    }

    /// <summary>功耗数字：按方向上下滚动换位（220 ms，新的从下 / 上 60 % 处进来，旧的反向出去）。</summary>
    private void SpRollTo(SpState sp, int value, int dir)
    {
        var surf = SpTextSurf(value.ToString(), 92, 66, 60, RwPoppinsB, 700, RmInk, true, out var dim);
        var v = G.Sprite(G.C.CreateSurfaceBrush(surf), relative: false);
        v.Size = dim;
        sp.Roll.Children.InsertAtTop(v);
        var old = sp.RollCur;
        sp.RollCur = v;
        var ease = new Bezier(0.2f, 1f, 0.3f, 1f);
        if (dir == 0 || Reduced)
        {
            if (old != null) SpDropVis(sp.Roll, old);
            return;
        }
        v.Offset = new Vector3(0, dir * 40, 0);
        v.Opacity = 0;
        v.StartAnimation("Offset", CsV3(Vector3.Zero, 220, ease));
        v.StartAnimation("Opacity", CsS(1, 220, ease));
        // 连按时还没走完的旧数字：直接拿掉，只留正在出去的这一个
        foreach (var c in sp.Roll.Children.ToList()) if (c != v && c != old) SpDropVis(sp.Roll, c);
        if (old != null)
        {
            old.StartAnimation("Offset", CsV3(new Vector3(0, -dir * 40, 0), 220, ease));
            old.StartAnimation("Opacity", CsS(0, 220, ease));
            After(240, () => SpDropVis(sp.Roll, old));
        }
    }

    // ---------------- 选项行（帧率上限 / 分辨率 / 刷新率）：二级页的下拉组件 ----------------

    private static readonly int[] SpFps = { 0, 30, 40, 60 };

    private (string Title, string[] Options, int Current) SpPick(int i)
    {
        switch (SpPicks[i])
        {
            case "fps":
            {
                int now = (int)SpNum(_spState, "system.fpsLimit");
                var list = SpFps.Append(now).Distinct().Order().ToArray();
                return ("帧率上限", list.Select(f => f == 0 ? "不限" : f.ToString()).ToArray(), Array.IndexOf(list, now));
            }
            case "res":
            {
                var list = _spResList.Length > 0 ? _spResList : new[] { _spRes.W > 0 ? _spRes : (W: 1920, H: 1200) };
                int cur = Math.Max(0, Array.FindIndex(list, r => r == _spRes));
                return ("分辨率", list.Select(r => $"{r.W}×{r.H}").ToArray(), cur);
            }
            default:
            {
                var rates = SpRates();
                double hz = SpNum(_spState, "system.refresh");
                if (rates.Count == 0) rates.Add(hz > 0 ? hz : 60);
                int cur = Math.Max(0, rates.IndexOf(hz));
                return ("刷新率", rates.Select(r => $"{r:0} Hz").ToArray(), cur);
            }
        }
    }

    private List<double> SpRates() =>
        (_spState?["system"]?["refreshRates"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue(out double x) ? x : 0).Where(x => x >= 40).Distinct().OrderByDescending(x => x).ToList() ?? new();

    private void SpPaintPickBg(SpState sp, int i)
    {
        float y = SpPickY(i), h = SpPickRowH, x0 = SpBX, pad = 12;
        var surf = SpSurf(ref sp.PickBgSurf[i], sp.PickBg[i], x0 - pad, y - pad, SpBW + pad * 2, h + pad * 2);
        using var ds = CanvasComposition.CreateDrawingSession(surf);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(pad - x0, pad - y) * Matrix3x2.CreateScale(CsRaster);
        var (title, _, _) = SpPick(i);
        using (var tf = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 28, FontWeight = RsWt(title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(title, new Windows.Foundation.Rect(SpBX + 26, y, 300, h), RmInk, tf);
        RsPaintPickPill(ds, SpPickX, y + h / 2 - SpPickH / 2, SpPickW, SpPickH, "", sp.Drop_ == i, 24, text: false);
    }

    /// <summary>胶囊里的当前值：dir ≠ 0 时旧值往反方向滑出、新值从这边滑进来（200 ms、40 px）。</summary>
    private void SpSetPickVal(SpState sp, int i, int dir)
    {
        var (_, opts, cur) = SpPick(i);
        string text = cur >= 0 && cur < opts.Length ? opts[cur] : "";
        if (dir == 0 && sp.PickText[i] == text) return;
        sp.PickText[i] = text;
        var box = sp.PickVal[i];
        float w = box.Size.X;
        var surf = G.Surface((int)Math.Ceiling(w * CsRaster), (int)Math.Ceiling(SpPickH * CsRaster));
        using (var ds = CanvasComposition.CreateDrawingSession(surf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(-(SpPickX + 4), -box.Offset.Y) * Matrix3x2.CreateScale(CsRaster);
            RsPaintPickText(ds, SpPickX, box.Offset.Y, SpPickW, SpPickH, text, 24);
        }
        var v = G.Sprite(G.C.CreateSurfaceBrush(surf), relative: false);
        v.Size = new Vector2(w, SpPickH);
        box.Children.InsertAtTop(v);
        var old = sp.PickCur[i];
        sp.PickCur[i] = v;
        foreach (var c in box.Children.ToList()) if (c != v && c != old) SpDropVis(box, c);
        if (old == null) return;
        if (dir == 0 || Reduced) { SpDropVis(box, old); return; }
        var ease = new Bezier(0.2f, 1f, 0.3f, 1f);
        v.Offset = new Vector3(dir * 40, 0, 0); v.Opacity = 0;
        v.StartAnimation("Offset", CsV3(Vector3.Zero, 200, ease));
        v.StartAnimation("Opacity", CsS(1, 200, ease));
        old.StartAnimation("Offset", CsV3(new Vector3(-dir * 40, 0, 0), 200, ease));
        old.StartAnimation("Opacity", CsS(0, 200, ease));
        After(220, () => SpDropVis(box, old));
    }

    // ---------------- 焦点 ----------------

    /// <summary>可聚焦的项：(编号, 行, 方框, 括号样式)。样式 c = 圆 / 胶囊（括号圆角 = 高的一半），row = 整行，none = 不画括号。</summary>
    private List<(string Id, int Row, float X, float Y, float W, float H, string Kind)> SpItems(SpState sp)
    {
        var l = new List<(string, int, float, float, float, float, string)>();
        if (sp.SubPage != null)
        {
            for (int i = 0; i < sp.SubRows.Count; i++) l.Add(($"r:{i}", i, SpBX, SpSubY + i * SpSubRowH, SpBW, SpSubRowH, "row"));
            return l;
        }
        for (int i = 0; i < SpTiles.Length; i++) { var (x, y) = SpTileXY(i); l.Add(($"t:{SpTiles[i].Id}", i / 5, x, y, SpTile, SpTile, "c")); }
        l.Add(("s:bri", 2, SpBX, SpBriY, SpBW, SpSlH, "c"));
        l.Add(("s:vol", 3, SpBX, SpVolY, SpBW, SpSlH, "c"));
        l.Add(("m:pw", 4, SpBX, SpGY0, SpBW, SpPwH, "none"));
        for (int i = 0; i < 3; i++) l.Add(($"o:{SpPicks[i]}", 5 + i, SpBX, SpPickY(i), SpBW, SpPickRowH, "row"));
        return l;
    }

    private void SpFocusTo(SpState sp, string id, bool move)
    {
        var items = SpItems(sp);
        var it = items.FirstOrDefault(e => e.Id == id);
        if (it.Id == null) { if (items.Count == 0) return; it = items[0]; }
        sp.Focus = it.Id;
        bool pw = it.Id == "m:pw";
        sp.P.StartAnimation("foc", CsS(pw ? 1 : 0, Reduced ? 1 : 140, Bezier.EaseOut));
        GamepadPoller.RepeatOverrideMs = it.Id.StartsWith("s:") ? 75 : pw ? 110 : 0;
        if (it.Kind == "none") { SpCursorShow(sp, false, 90); return; }
        SpCursorRect(sp, it.X, it.Y, it.W, it.H, it.Kind, move);
        if (sp.Open && !sp.CurShown && move) SpCursorShow(sp, true, 90);
    }

    private void SpCursorShow(SpState sp, bool on, double ms, double delay = 0)
    {
        sp.CurShown = on;
        foreach (var v in SpCursorVis(sp)) { v.StopAnimation("Opacity"); v.StartAnimation("Opacity", CsS(on ? 1 : 0, Math.Max(1, ms), Bezier.EaseOut, delay)); }
    }

    private readonly Dictionary<string, (CompositionDrawingSurface Glow, CompositionDrawingSurface Line, CompositionDrawingSurface Rim, float Raster, CompositionBrush L1, CompositionBrush L2)> _spCurCache = new();

    /// <summary>
    /// 括号到一个方框：c 样式外扩 12、圆角 = 高的一半（圆开关上是四段弧，滑块行两端弯成半圆）；row 外扩 8、圆角 42；opt 外扩 8、圆角 30。
    /// 尺寸变了按新尺寸重画，从旧大小弹过去（同二级页 RsCursorTo）；尺寸不变时位置弹簧 + 橡皮筋（同首页 / 二级页）。
    /// </summary>
    private void SpCursorRect(SpState sp, float x, float y, float w, float h, string kind, bool move)
    {
        float pad = kind == "c" ? 12 : 8;
        float W = w + pad * 2, H = h + pad * 2, r = kind == "c" ? H / 2 : kind == "opt" ? 30 : 42;
        float sw = W + RmCurPad * 2, sh = H + RmCurPad * 2;
        var to = new Vector3(x + w / 2 - sw / 2, y + h / 2 - sh / 2, 0);
        bool resize = MathF.Abs(sp.CurW - W) > 0.5f || MathF.Abs(sp.CurH - H) > 0.5f || MathF.Abs(sp.CurR - r) > 0.5f;
        if (resize)
        {
            float ow = sp.CurW, oh = sp.CurH;
            var oldCenter = new Vector3(sp.Glow.Offset.X + (ow + RmCurPad * 2) / 2, sp.Glow.Offset.Y + (oh + RmCurPad * 2) / 2, 0);
            SpPaintCursor(sp, W, H, r);
            foreach (var v in SpCursorVis(sp))
            {
                v.StopAnimation("Offset");
                v.Offset = ow > 0 ? oldCenter - new Vector3(sw / 2, sh / 2, 0) : to;
                if (move && !Reduced && ow > 0)
                {
                    v.Scale = new Vector3((ow + RmCurPad * 2) / sw, (oh + RmCurPad * 2) / sh, 1);
                    v.StartAnimation("Scale", RwSpring(Vector3.One));
                }
                else { v.StopAnimation("Scale"); v.Scale = Vector3.One; }
            }
        }
        float dx = to.X - sp.Glow.Offset.X, dy = to.Y - sp.Glow.Offset.Y;
        foreach (var v in SpCursorVis(sp))
        {
            if (move && !Reduced) v.StartAnimation("Offset", RwSpring(to)); else RwJump(v, to);
            if (move && !Reduced && !resize) SpStretch(v, dx, dy, W, H);
        }
    }

    /// <summary>橡皮筋：沿移动方向先拉长再缩回（同二级页 RsStretch，横向移动拉宽）。</summary>
    private void SpStretch(Visual v, float dx, float dy, float w, float h)
    {
        bool horiz = MathF.Abs(dx) > MathF.Abs(dy);
        float d = horiz ? MathF.Abs(dx) : MathF.Abs(dy), size = horiz ? w : h;
        if (d < 1 || size <= 0) return;
        float k = 1 + MathF.Min(0.45f, d / size * 0.35f);
        var peak = horiz ? new Vector3(k, 1, 1) : new Vector3(1, k, 1);
        var a = G.C.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, Vector3.One, G.Linear);
        a.InsertKeyFrame(0.3f, peak, G.Ease(new Bezier(0.3f, 0f, 0.4f, 1f)));
        a.InsertKeyFrame(1, Vector3.One, G.Ease(new Bezier(0.22f, 1f, 0.36f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(230);
        v.StartAnimation("Scale", a);
    }

    /// <summary>括号路径：四角各一段（直腿 + 圆弧 + 直腿）；没有直腿时（圆、胶囊）每段两头各剪掉一点，四段之间留缝。</summary>
    private static CanvasGeometry SpBracketPath(ICanvasResourceCreator dev, float W, float H, float r, float pad)
    {
        r = MathF.Min(r, MathF.Min(W, H) / 2);
        float leg = MathF.Max(0, MathF.Min(18 * RwDp, (MathF.Min(W, H) - 2 * r - 13) / 2));
        var (segs, _) = RwBracketPointsRaw(W, H, r, leg, pad, pad);
        if (leg < 0.5f)
        {
            float g = MathF.Min(9, MathF.PI * r / 2 / 4);
            for (int s = 0; s < segs.Count; s++)
            {
                var pts = segs[s];
                var cum = new float[pts.Count];
                for (int i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + Vector2.Distance(new Vector2(pts[i].X, pts[i].Y), new Vector2(pts[i - 1].X, pts[i - 1].Y));
                float total = cum[^1];
                var keep = new List<Vector3>();
                for (int i = 0; i < pts.Count; i++) if (cum[i] >= g && cum[i] <= total - g) keep.Add(pts[i]);
                if (keep.Count >= 2) segs[s] = keep;
            }
        }
        return RwBracketPath(dev, segs);
    }

    private void SpPaintCursor(SpState sp, float W, float H, float r)
    {
        sp.CurW = W; sp.CurH = H; sp.CurR = r;
        float R = CsRaster, sw = W + RmCurPad * 2, sh = H + RmCurPad * 2;
        string key = $"{W:0.#}x{H:0.#}r{r:0.#}";
        if (!_spCurCache.TryGetValue(key, out var c) || c.Raster != R)
        {
            int pw = (int)Math.Ceiling(sw * R), ph = (int)Math.Ceiling(sh * R);
            var dev = G.Device;
            using var path = SpBracketPath(dev, W, H, r, RmCurPad);
            var glow = G.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(glow))
            {
                ds.Clear(Colors.Transparent);
                using var cl = new CanvasCommandList(ds);
                using (var s = cl.CreateDrawingSession()) { s.Transform = Matrix3x2.CreateScale(R); s.DrawGeometry(path, RwC2, RwSw * 2, RwRoundStroke); }
                using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 7 * RwDp * R / 2, BorderMode = EffectBorderMode.Soft };
                ds.DrawImage(blur);
            }
            var rim = G.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(rim))
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateScale(R);
                using var outer = path.Stroke(RwSw + 2 * RwDp, RwRoundStroke);
                using var inner = path.Stroke(MathF.Max(0.1f, RwSw - 2 * RwDp), RwRoundStroke);
                using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                ds.FillGeometry(ring, Color.FromArgb(102, 0xfa, 0xfa, 0xfa));
            }
            var line = G.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(line))
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateScale(R);
                ds.DrawGeometry(path, Colors.White, RwSw, RwRoundStroke);
            }
            c = (glow, line, rim, R, RsPerimFlow(line, W, H, r, right: false), RsPerimFlow(line, W, H, r, right: true));
            _spCurCache[key] = c;
        }
        ((CompositionSurfaceBrush)sp.Glow.Brush).Surface = c.Glow;
        ((CompositionSurfaceBrush)sp.Rim.Brush).Surface = c.Rim;
        sp.Line.Brush = c.L1;
        sp.Line2.Brush = c.L2;
        sp.Line.Clip = G.C.CreateInsetClip(0, 0, sw / 2, 0);
        sp.Line2.Clip = G.C.CreateInsetClip(sw / 2, 0, 0, 0);
        foreach (var v in SpCursorVis(sp)) { v.Size = new Vector2(sw, sh); v.CenterPoint = new Vector3(sw / 2, sh / 2, 0); }
    }

    // ---------------- 输入 ----------------

    private void SpMove(Direction dir)
    {
        if (_sp is not { Open: true } sp) { Cue(Sfx.None); return; }
        int dx = dir == Direction.Left ? -1 : dir == Direction.Right ? 1 : 0, dy = dir == Direction.Up ? -1 : dir == Direction.Down ? 1 : 0;
        if (sp.Drop_ >= 0)
        {
            if (dy == 0) { Cue(Sfx.Bump); return; }
            int n = SpPick(sp.Drop_).Options.Length, to = sp.DropSel + dy;
            if (to < 0 || to >= n) { Cue(Sfx.Bump); return; }
            sp.DropSel = to;
            CuePan(0.4f);
            SpCursorRect(sp, SpPickX, SpDropY(sp) + to * SpOptH, SpPickW, SpOptH, "opt", move: true);
            return;
        }
        var items = SpItems(sp);
        var cur = items.FirstOrDefault(e => e.Id == sp.Focus);
        if (cur.Id == null) { SpFocusTo(sp, items[0].Id, false); return; }
        if (dx != 0)
        {
            if (SpAdjust(sp, cur.Id, dx)) return;
            var row = items.Where(e => e.Row == cur.Row).ToList();
            int j = row.FindIndex(e => e.Id == cur.Id) + dx;
            if (j < 0 || j >= row.Count) { Cue(Sfx.Bump, pan: dx < 0 ? 0.2f : 0.8f); return; }
            if (sp.Armed != null) { sp.Armed = null; SpPaintSub(sp); }
            CuePan(0.5f);
            SpFocusTo(sp, row[j].Id, move: true);
            return;
        }
        int nr = cur.Row + dy;
        var next = items.Where(e => e.Row == nr).ToList();
        if (next.Count == 0) { Cue(Sfx.Bump, pan: 0.5f); return; }
        float cx = cur.X + cur.W / 2;
        var best = next.OrderBy(e => MathF.Abs(e.X + e.W / 2 - cx)).First();
        if (sp.Armed != null) { sp.Armed = null; SpPaintSub(sp); }
        CuePan(0.5f);
        SpFocusTo(sp, best.Id, move: true);
    }

    /// <summary>←→ 调值：滑块 ±5 %，功耗 ±1 W，选项行直接切换（到头不循环）。调了返回 true。</summary>
    private bool SpAdjust(SpState sp, string id, int d)
    {
        if (id.StartsWith("s:"))
        {
            int k = id == "s:bri" ? 0 : 1;
            int now = SpLevel(k), v = Math.Clamp(now + 5 * d, 0, 100);
            if (v == now && !(k == 1 && Bool(_spState?["system"]?["muted"]))) { Cue(Sfx.Bump); return true; }
            Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown, MathF.Pow(2, v / 100f - 0.5f));
            SpSetLevel(sp, k, v);
            return true;
        }
        if (id == "m:pw") { SpSetTdp(sp, _spTdp + d); return true; }
        if (id.StartsWith("o:"))
        {
            int i = Array.IndexOf(SpPicks, id[2..]);
            var (_, opts, cur) = SpPick(i);
            int to = cur + d;
            if (to < 0 || to >= opts.Length) { Cue(Sfx.Bump); return true; }
            Cue(Sfx.Nav, pan: d < 0 ? 0.2f : 0.8f);
            SpChoose(sp, i, to);
            return true;
        }
        return false;
    }

    private void SpActivate()
    {
        if (_sp is not { Open: true } sp) { Cue(Sfx.None); return; }
        if (sp.Drop_ >= 0)
        {
            int i = sp.Drop_, sel = sp.DropSel;
            Cue(Sfx.Positive);
            SpDropClose(sp, animate: true);
            if (sel != SpPick(i).Current) SpChoose(sp, i, sel);
            return;
        }
        string f = sp.Focus;
        if (sp.SubPage != null) { SpSubActivate(sp, f); return; }
        if (f.StartsWith("t:")) { SpTileActivate(sp, f[2..]); return; }
        if (f == "s:vol") { SpToggleMute(sp); return; }
        if (f.StartsWith("o:")) { SpDropOpen(sp, Array.IndexOf(SpPicks, f[2..])); return; }
        Cue(Sfx.Bump);
    }

    private void SpBack()
    {
        if (_sp is not { Open: true } sp) return;
        if (sp.Drop_ >= 0) { Cue(Sfx.Back); SpDropClose(sp, animate: true); return; }
        if (sp.SubPage != null) { Cue(Sfx.Back); SpSubLeave(sp); return; }
        SpClose();
    }

    // ---------------- 快捷开关 ----------------

    private void SpTileActivate(SpState sp, string id)
    {
        if (sp.Tiles.TryGetValue(id, out var t) && !Reduced)
        {
            var a = G.C.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0.35f, new Vector3(0.93f, 0.93f, 1), G.Ease(Bezier.EaseOut));
            a.InsertKeyFrame(1, Vector3.One, G.Ease(new Bezier(0.22f, 1f, 0.36f, 1f)));
            a.Duration = TimeSpan.FromMilliseconds(220);
            t.Off.StartAnimation("Scale", a); t.On.StartAnimation("Scale", a);
        }
        Log.Info($"side panel tile {id}");
        switch (id)
        {
            case "wifi": case "bt": case "pwr":
                Cue(Sfx.MenuIn);
                SpSubEnter(sp, id);
                return;
            case "osd":
            {
                if (_spDraft is not { } d) { Cue(Sfx.Bump); return; }
                if (d["osd"] is not JsonObject osd) { osd = new JsonObject(); d["osd"] = osd; }
                bool next = !Bool(osd["enabled"]);
                osd["enabled"] = next;
                if (next && SpNum(osd, "level") <= 0) osd["level"] = 1;
                Cue(next ? Sfx.ToggleOn : Sfx.ToggleOff);
                SpQueue("save_power", JsonValue.Create(0), d.DeepClone());
                SpApplyTiles(sp, animate: true);
                return;
            }
            case "pad":
            {
                bool toMouse = !_spMouse;
                _spMouse = toMouse;
                Cue(toMouse ? Sfx.ToggleOff : Sfx.ToggleOn);
                SpPaintTile(sp, Array.FindIndex(SpTiles, e => e.Id == "pad"));
                SpApplyTiles(sp, animate: true);
                SpQueue("set_gamepad_mode", JsonValue.Create(toMouse ? "desktop" : "xinput"));
                // 切成键鼠后手柄不再驱动界面：面板收起（同游戏模式快捷面板）
                if (toMouse) After(260, () => SpClose());
                return;
            }
            case "gear":
                Cue(Sfx.Activate);
                SpClose(instant: true);
                OpenSettingsFromLibrary();
                return;
            case "shot":
                SpClose(instant: true);
                After(150, async () =>
                {
                    string? path = await CaptureScreenshotAsync(0);
                    if (path == null) Toast(_shotNeedsPermission ? "截图没能保存：在游戏库里允许「屏幕截图」权限" : "截图没能保存");
                    else { Cue(Sfx.Shutter); Toast("已截图"); }
                });
                return;
            case "kbd":
                SpClose(instant: true);
                SpQueue("toggle_keyboard");
                return;
            case "win":
                SpClose(instant: true);
                OpenSwitcherShortcut();
                return;
            case "desk":
                if (!ShellMode.Available) { Cue(Sfx.Bump); return; }
                SpClose(instant: true);
                _ = CcSwitchMode();
                return;
        }
    }

    // ---------------- 子页：Wi-Fi / 蓝牙 / 电源 ----------------

    private void SpSubEnter(SpState sp, string page)
    {
        sp.SubFrom = sp.Focus;
        sp.SubPage = page;
        sp.Armed = null;
        SpBuildSub(sp);
        SpPaintSub(sp);
        if (Reduced) { sp.Body.Opacity = 0; sp.Sub.Opacity = 1; }
        else
        {
            sp.Body.StartAnimation("Opacity", CsS(0, 90, Bezier.EaseOut));
            sp.Sub.StartAnimation("Opacity", CsS(1, 140, Bezier.EaseOut, 40));
        }
        SpFocusTo(sp, "r:0", move: true);
        if (page == "wifi") _ = SpScanWifi(sp);
        if (page == "bt") _ = SpScanBt(sp);
    }

    private void SpSubLeave(SpState sp)
    {
        sp.SubPage = null;
        sp.Armed = null;
        if (Reduced) { sp.Body.Opacity = 1; sp.Sub.Opacity = 0; }
        else
        {
            sp.Sub.StartAnimation("Opacity", CsS(0, 90, Bezier.EaseOut));
            sp.Body.StartAnimation("Opacity", CsS(1, 140, Bezier.EaseOut, 40));
        }
        SpFocusTo(sp, sp.SubFrom ?? "t:wifi", move: true);
    }

    private void SpBuildSub(SpState sp)
    {
        var rows = new List<(string, string, string, string, bool, bool)>();
        switch (sp.SubPage)
        {
            case "wifi":
                rows.Add(("toggle", "qam.wifi", "Wi-Fi", "", true, _spWifi == true));
                if (_spWifi == true)
                    foreach (var n in _spNets.Take(10))
                        rows.Add(("net:" + n.Id, "qam.wifi", n.Name, n.Connected ? "已连接" : n.Saved ? "已保存" : n.Secured ? "lock" : "", false, n.Connected));
                break;
            case "bt":
                rows.Add(("toggle", "qam.bt", "蓝牙", "", true, _spBt == true));
                if (_spBt == true)
                    foreach (var n in _spBtNames.Take(10)) rows.Add(("dev", n.Contains("Controller", StringComparison.OrdinalIgnoreCase) ? "qam.pad" : "qam.headphones", n, "已连接", false, true));
                break;
            default:
                rows.Add(("sleep", "qam.sleep", "睡眠", "", false, false));
                rows.Add(("restart", "qam.restart", "重启", "", false, false));
                rows.Add(("off", "qam.off", "关机", "", false, false));
                break;
        }
        sp.SubRows = rows;
    }

    private void SpPaintSub(SpState sp)
    {
        if (sp.SubPage == null) return;
        {
            var v = sp.Sub.Children.OfType<SpriteVisual>().FirstOrDefault();
            if (v == null) { v = G.Sprite(G.C.CreateSurfaceBrush(), relative: false); sp.Sub.Children.InsertAtTop(v); }
            float x0 = SpBX - 20, y0 = SpTileY - 10, w = SpBW + 40, h = SpGY1 - y0;
            var surf = SpSurf(ref sp.SubSurf, v, x0, y0, w, h);
            using var ds = CanvasComposition.CreateDrawingSession(surf);
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
            string title = sp.SubPage switch { "wifi" => "Wi-Fi", "bt" => "蓝牙", _ => "电源" };
            using (var tf = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 32, FontWeight = RsWt(title), WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(title, SpBX + 6, SpTileY + 6, RmInk, tf);
            for (int i = 0; i < sp.SubRows.Count; i++)
            {
                var (rid, icon, name, right, toggle, on) = sp.SubRows[i];
                float y = SpSubY + i * SpSubRowH, cy = y + SpSubRowH / 2;
                bool armed = sp.Armed == rid;
                var ink = armed ? RmRed : RmInk;
                QsIcon(ds, icon, SpBX + 24, cy, 36, armed ? RmRed : SpMute, 2);
                string label = armed ? "再按一次" + name : name;
                using (var nf = new CanvasTextFormat { FontFamily = RmFont(label), FontSize = 25, FontWeight = RsWt(label, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                    ds.DrawText(label, new Windows.Foundation.Rect(SpBX + 62, y, toggle ? 520 : 470, SpSubRowH), ink, nf);
                if (toggle)
                {
                    float tw = 84, th = 50, tx = SpBX + SpBW - 6 - tw, ty = cy - th / 2;
                    if (on) { using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(tx, ty), EndPoint = new Vector2(tx + tw, ty + th) }; ds.FillRoundedRectangle(tx, ty, tw, th, th / 2, th / 2, br); }
                    else { ds.FillRoundedRectangle(tx, ty, tw, th, th / 2, th / 2, Color.FromArgb(255, 0xE4, 0xE5, 0xE9)); ds.DrawRoundedRectangle(tx + 1.5f, ty + 1.5f, tw - 3, th - 3, th / 2 - 1.5f, th / 2 - 1.5f, Color.FromArgb(255, 0x9A, 0x9C, 0xA3), 3); }
                    RsSoftShadow(ds, (on ? tx + tw - 25 : tx + 25) - 18, cy - 18 + 2, 36, 36, 18, 3, 0.22f);
                    ds.FillCircle(on ? tx + tw - 25 : tx + 25, cy, 18, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                }
                else if (right == "lock") QsIcon(ds, "qam.lock", SpBX + SpBW - 24, cy, 26, SpMute, 2);
                else if (right.Length > 0)
                {
                    using var rf = new CanvasTextFormat { FontFamily = RmFont(right), FontSize = 22, FontWeight = RsWt(right, 500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
                    ds.DrawText(right, new Windows.Foundation.Rect(SpBX + 300, y, SpBW - 306, SpSubRowH), SpMute, rf);
                }
                if (i > 0) ds.FillRectangle(SpBX + 62, y - 1, SpBW - 68, 2, Color.FromArgb(255, 0xE6, 0xE7, 0xEA));
            }
        }
    }

    private void SpSubActivate(SpState sp, string f)
    {
        int i = int.Parse(f[2..]);
        if (i < 0 || i >= sp.SubRows.Count) return;
        var row = sp.SubRows[i];
        switch (sp.SubPage)
        {
            case "wifi" or "bt" when row.Id == "toggle":
            {
                var kind = sp.SubPage == "wifi" ? RadioKind.WiFi : RadioKind.Bluetooth;
                bool next = !row.On;
                if (kind == RadioKind.WiFi) _spWifi = next; else _spBt = next;
                Cue(next ? Sfx.ToggleOn : Sfx.ToggleOff);
                SpBuildSub(sp); SpPaintSub(sp); SpApplyTiles(sp, animate: false);
                _ = SpSetRadio(sp, kind, next);
                return;
            }
            case "wifi" when row.Id.StartsWith("net:"):
            {
                var e = _spNets.FirstOrDefault(n => "net:" + n.Id == row.Id);
                if (e == null || e.Connected) { Cue(Sfx.Bump); return; }
                if (e.Secured && !e.Saved) { Cue(Sfx.Bump); Toast($"「{e.Name}」需要密码：在系统设置里连接"); SystemInfo.Open("ms-settings:network-wifi"); return; }
                Cue(Sfx.Activate);
                _ = Task.Run(async () =>
                {
                    try { await QamWireless.Connect(e, null); } catch (Exception ex) { _dq.TryEnqueue(() => Toast($"没有连上「{e.Name}」：{ex.Message}")); }
                    _dq.TryEnqueue(() => { if (_sp == sp && sp.SubPage == "wifi") _ = SpScanWifi(sp); });
                });
                return;
            }
            case "pwr":
            {
                if (row.Id == "sleep")
                {
                    nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                    SpClose(instant: true);
                    if (!Program.Args.Contains("--power-dry")) After(300, () => SystemInfo.Sleep(hwnd));
                    return;
                }
                if (sp.Armed != row.Id) { sp.Armed = row.Id; Cue(Sfx.ModalShow); SpPaintSub(sp); return; }
                SpClose(instant: true);
                if (!Program.Args.Contains("--power-dry")) _ = CcRequestAnimatedPower(row.Id == "off" ? "shutdown" : "restart");
                return;
            }
        }
        Cue(Sfx.Bump);
    }

    private async Task SpSetRadio(SpState sp, RadioKind kind, bool on)
    {
        if (_spDry) return;
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) { Toast("没有切换：没有权限"); return; }
            foreach (var r in await Radio.GetRadiosAsync())
                if (r.Kind == kind) { await r.SetStateAsync(on ? RadioState.On : RadioState.Off); break; }
        }
        catch (Exception ex) { Toast($"没有切换：{ex.Message}"); }
        await Task.Delay(600);
        if (_sp != sp) return;
        if (kind == RadioKind.WiFi && sp.SubPage == "wifi") await SpScanWifi(sp);
        if (kind == RadioKind.Bluetooth && sp.SubPage == "bt") await SpScanBt(sp);
    }

    private async Task SpScanWifi(SpState sp)
    {
        if (_spWifi != true) return;
        try { _spNets = await QamWireless.Wifi(); }
        catch (Exception ex) { Log.Warn($"side panel wifi scan: {ex.Message}"); }
        if (_sp != sp || sp.SubPage != "wifi") return;
        SpBuildSub(sp); SpPaintSub(sp);
    }

    private async Task SpScanBt(SpState sp)
    {
        try
        {
            var sel = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
            var le = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
            var a = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(sel);
            var b = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(le);
            _spBtNames = a.Select(d => d.Name).Concat(b.Select(d => d.Name)).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        }
        catch (Exception ex) { Log.Warn($"side panel bluetooth: {ex.Message}"); }
        if (_sp != sp || sp.SubPage != "bt") return;
        SpBuildSub(sp); SpPaintSub(sp);
    }

    // ---------------- 下拉 ----------------

    private float SpDropY(SpState sp)
    {
        int n = SpPick(sp.Drop_).Options.Length;
        float capY = SpPickY(sp.Drop_) + SpPickRowH / 2 - SpPickH / 2;
        return sp.DropUp ? capY - 10 - n * SpOptH : capY + SpPickH + 10;
    }

    /// <summary>A：选项卡片在胶囊下面展开（放不下就向上），当前值蓝字；括号从整行缩到当前选项上。</summary>
    private void SpDropOpen(SpState sp, int i)
    {
        var (_, opts, cur) = SpPick(i);
        if (opts.Length < 2) { Cue(Sfx.Bump); return; }
        Cue(Sfx.Activate);
        sp.Drop_ = i;
        sp.DropSel = Math.Max(0, cur);
        float capY = SpPickY(i) + SpPickRowH / 2 - SpPickH / 2, h = opts.Length * SpOptH;
        sp.DropUp = capY + SpPickH + 10 + h > SpT + SpH1 - 12;
        float y0 = SpDropY(sp), pad = 20;
        var surf = SpSurf(ref sp.DropSurf, sp.Drop, SpPickX - pad, y0 - pad, SpPickW + pad * 2, h + pad * 2);
        using (var ds = CanvasComposition.CreateDrawingSession(surf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - SpPickX, pad - y0) * Matrix3x2.CreateScale(CsRaster);
            var row = new RsRow { Kind = RsKind.Pick, Title = "", Options = opts, Current = () => cur };
            RsPaintOptionCard(ds, row, SpPickX, y0, SpPickW, SpOptH, 24);
        }
        sp.Drop.CenterPoint = new Vector3(SpPickW / 2 + pad, sp.DropUp ? h + pad : pad, 0);
        sp.Drop.StopAnimation("Opacity"); sp.Drop.StopAnimation("Scale");
        if (Reduced) { sp.Drop.Opacity = 1; sp.Drop.Scale = Vector3.One; }
        else
        {
            var ease = new Bezier(0.2f, 1f, 0.3f, 1f);
            sp.Drop.Opacity = 0; sp.Drop.Scale = new Vector3(1, 0.6f, 1);
            sp.Drop.StartAnimation("Opacity", CsS(1, 160, ease));
            sp.Drop.StartAnimation("Scale", CsV3(Vector3.One, 160, ease));
        }
        SpPaintPickBg(sp, i);
        SpCursorRect(sp, SpPickX, y0 + sp.DropSel * SpOptH, SpPickW, SpOptH, "opt", move: true);
    }

    private void SpDropClose(SpState sp, bool animate)
    {
        if (sp.Drop_ < 0) return;
        int i = sp.Drop_;
        sp.Drop_ = -1;
        SpPaintPickBg(sp, i);
        if (animate && !Reduced)
        {
            sp.Drop.StartAnimation("Opacity", CsS(0, 110, Bezier.EaseOut));
            sp.Drop.StartAnimation("Scale", CsV3(new Vector3(1, 0.8f, 1), 110, Bezier.EaseOut));
        }
        else { sp.Drop.StopAnimation("Opacity"); sp.Drop.Opacity = 0; }
        if (sp.Open) SpFocusTo(sp, $"o:{SpPicks[i]}", move: animate);
    }

    private void SpChoose(SpState sp, int i, int to)
    {
        int cur = SpPick(i).Current;
        int dir = to > cur ? 1 : -1;
        var sys = _spState?["system"] as JsonObject;
        switch (SpPicks[i])
        {
            case "fps":
            {
                var list = SpFps.Append((int)SpNum(_spState, "system.fpsLimit")).Distinct().Order().ToArray();
                int v = list[to];
                if (sys != null) sys["fpsLimit"] = v;
                SpQueue("set_fps_limit", JsonValue.Create(v));
                break;
            }
            case "res":
            {
                if (_spResList.Length == 0) return;
                var r = _spResList[to];
                _spRes = r;
                // 换分辨率要黑一下屏：连按时只换最后停下的那一档
                SpQueueDelayed("set_resolution", 600, JsonValue.Create(r.W), JsonValue.Create(r.H));
                break;
            }
            default:
            {
                var rates = SpRates();
                if (to >= rates.Count) return;
                if (sys != null) sys["refresh"] = rates[to];
                SpQueue("set_refresh_rate", JsonValue.Create(rates[to]));
                break;
            }
        }
        SpSetPickVal(sp, i, dir);
    }

    // ---------------- 写入（同一项只留最新一次，按顺序发） ----------------

    private void SpQueue(string method, params JsonNode?[] args) => SpQueueDelayed(method, 60, args);

    private void SpQueueDelayed(string method, int delayMs, params JsonNode?[] args)
    {
        if (_spDry) { Log.Info($"side panel dry write {method}"); return; }
        _spWrites ??= new SettingsWriteQueue(ex => Toast($"没有改成：{ex.Message}"), () => { if (_sp is { Open: true } s) _ = SpRefresh(s); });
        // save_power 第一个参数是占位（只为了让调用处统一），真正的参数是第二个
        var real = method == "save_power" && args.Length == 2 ? new[] { args[1] } : args;
        _ = _spWrites.Enqueue(method, delayMs, async () =>
        {
            _agent ??= new AgentClient();
            await _agent.Call(method, real);
        });
    }

    private void SpSetLevel(SpState sp, int k, int v)
    {
        if (_spState?["system"] is not JsonObject sys) return;
        string key = k == 0 ? "brightness" : "volume";
        bool unmute = k == 1 && Bool(sys["muted"]);
        sys[key] = v;
        if (unmute) { sys["muted"] = false; sp.StaticMuted = false; SpPaintStatic(sp); }
        SpQueue(k == 0 ? "set_brightness" : "set_volume", JsonValue.Create(v));
        SpApplySliders(sp, animate: true, only: k);
    }

    /// <summary>音量行按 A：静音 / 取消静音（滑钮滑到 0 / 回到原来的音量，左边图标跟着换）。</summary>
    private void SpToggleMute(SpState sp)
    {
        if (_spState?["system"] is not JsonObject sys) { Cue(Sfx.Bump); return; }
        bool m = !Bool(sys["muted"]);
        sys["muted"] = m;
        Cue(m ? Sfx.ToggleOff : Sfx.ToggleOn);
        SpQueue("set_mute", JsonValue.Create(m));
        sp.StaticMuted = m;
        SpPaintStatic(sp);
        SpApplySliders(sp, animate: true, only: 1);
    }

    private (int Lo, int Hi) SpTdpRange() => ((int)SpNum(_spState, "power.limits.tdpMin", 8), (int)SpNum(_spState, "power.limits.tdpMax", 35));

    private int SpTdpNow()
    {
        if (_spGame != null && _spGs?["power"]?["profile"] is JsonObject prof && Str(prof["mode"]) == "manual") return (int)SpNum(prof, "tdp", _spTdp);
        var d = _spDraft;
        if (d == null) return _spTdp;
        if (Bool(d["enabled"]) && Str(d["mode"]) == "manual") return (int)Math.Round(SpNum(d, "manual.tdp", _spTdp));
        double target = SpNum(_spState, "power.target.pl1", -1);
        return (int)Math.Round(target > 0 ? target : SpNum(d, "manual.tdp", _spTdp));
    }

    /// <summary>
    /// 功耗 ±1 W：游戏在运行（而且读到了它的配置）就改这款游戏的固定功耗档，否则改全局固定功耗。
    /// 数字按方向滚、刻度弹簧扫过去（刚度 500、阻尼比 0.8），新亮起的刻度拉高 1.35 倍再落回。
    /// </summary>
    private void SpSetTdp(SpState sp, int v)
    {
        var (lo, hi) = SpTdpRange();
        v = Math.Clamp(v, Math.Max(SpWMin, lo), Math.Min(SpWMax, hi));
        if (v == _spTdp) { Cue(Sfx.Bump); return; }
        int old = _spTdp, dir = Math.Sign(v - old);
        _spTdp = v;
        Cue(dir > 0 ? Sfx.SliderUp : Sfx.SliderDown, MathF.Pow(2, (v - SpWMin) / (float)(SpWMax - SpWMin) - 0.5f));
        SpSpring(sp.P, "pw", v, 500, 0.8f);
        SpRollTo(sp, v, dir);
        if (dir > 0 && !Reduced)
            for (int w = old + 1; w <= v; w++)
            {
                if (w < SpWMin || w > SpWMax) continue;
                var a = G.C.CreateVector3KeyFrameAnimation();
                a.InsertKeyFrame(0.35f, new Vector3(1, 1.35f, 1), G.Ease(Bezier.EaseOut));
                a.InsertKeyFrame(1, Vector3.One, G.Ease(new Bezier(0.3f, 0f, 0.5f, 1f)));
                a.Duration = TimeSpan.FromMilliseconds(260);
                a.DelayTime = Gfx.Ms(30 + (w - old - 1) * 110.0 / Math.Max(1, v - old));
                sp.Ticks[w - SpWMin].StartAnimation("Scale", a);
            }
        if (_spDry) { }
        else if (_spGame is { } g && _spGs?["power"] is JsonObject pw && Str(pw["exe"]) != null)
        {
            var prof = pw["profile"] as JsonObject ?? new JsonObject();
            var p = (JsonObject)prof.DeepClone();
            if (Str(p["mode"]) != "manual")
            {
                p["mode"] = "manual";
                p["freq"] = (int)SpNum(pw, "defaults.manual.freq", 0);
                if (p["boost"] == null) p["boost"] = (int)SpNum(pw, "defaults.boost", 1);
            }
            p["tdp"] = v;
            pw["profile"] = p.DeepClone();
            var req = new JsonObject { ["id"] = g.Id, ["platform"] = g.Platform, ["title"] = g.Title, ["exe"] = Str(_spGs["exe"]), ["magpieKey"] = Str(_spGs["magpie"]?["key"]) };
            _spWrites ??= new SettingsWriteQueue(ex => Toast($"没有改成：{ex.Message}"), () => { if (_sp is { Open: true } s) _ = SpRefresh(s); });
            _ = _spWrites.Enqueue("game_power", 150, async () => { _agent ??= new AgentClient(); await _agent.CallLong("game_settings_set", 40, req, JsonValue.Create("power"), p); });
        }
        else if (_spDraft is { } d)
        {
            d["enabled"] = true; d["mode"] = "manual";
            if (d["manual"] is not JsonObject m) { m = new JsonObject(); d["manual"] = m; }
            m["tdp"] = v;
            SpQueue("save_power", JsonValue.Create(0), d.DeepClone());
        }
    }

    // ---------------- 读状态 ----------------

    private void SpStartTimers(SpState sp)
    {
        sp.Tick?.Stop();
        sp.Tick = _dq.CreateTimer();
        sp.Tick.Interval = TimeSpan.FromMilliseconds(1000);
        sp.Tick.Tick += (_, _) =>
        {
            if (_sp != sp || !sp.Open) { sp.Tick?.Stop(); return; }
            if (sp.Page.Rw is { } rw && RwStatusKey(rw) != sp.StatusKey) SpPaintStatus(sp);
            _ = SpRefresh(sp);
        };
        sp.Tick.Start();
    }

    private bool _spReading;

    private async Task SpRefresh(SpState sp, bool first = false)
    {
        if (_spReading || _spWrites?.IsBusy == true || _spDrag != null || (_spDry && !first)) return;
        _spReading = true;
        long rev = _spWrites?.Revision ?? 0;
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.Call("widget_state") is JsonObject s && _spWrites?.IsBusy != true && (_spWrites?.Revision ?? 0) == rev && _spDrag == null)
            {
                _spState = (JsonObject)s.DeepClone();
                _spDraft = s["power"]?["settings"]?.DeepClone() as JsonObject;
            }
            if (first || sp.Polls % 3 == 0)
            {
                try
                {
                    foreach (var r in await Radio.GetRadiosAsync())
                    {
                        if (r.Kind == RadioKind.WiFi) _spWifi = r.State == RadioState.On;
                        if (r.Kind == RadioKind.Bluetooth) _spBt = r.State == RadioState.On;
                    }
                }
                catch (Exception ex) { Log.Warn($"side panel radios: {ex.Message}"); }
            }
            if (first)
            {
                try
                {
                    if (await _agent.Call("get_display_state") is JsonObject d)
                    {
                        // 最多 8 档（从大到小），当前那档一定在里面：选项卡片向上展开时不顶出面板
                        var all = (d["resolutions"] as JsonArray)?.OfType<JsonArray>().Where(a => a.Count == 2).Select(a => (W: Int(a[0]), H: Int(a[1]))).Where(r => r.W > 0 && r.H > 0).Distinct().OrderByDescending(r => r.W * r.H).ToList() ?? new();
                        if (d["resolution"] is JsonArray cur0 && cur0.Count == 2) _spRes = (Int(cur0[0]), Int(cur0[1]));
                        var keep = all.Take(8).ToList();
                        if (_spRes.W > 0 && !keep.Contains(_spRes)) { if (keep.Count == 8) keep.RemoveAt(7); keep.Add(_spRes); }
                        _spResList = keep.OrderByDescending(r => r.W * r.H).ToArray();
                        if (d["resolution"] is JsonArray cur && cur.Count == 2) _spRes = (Int(cur[0]), Int(cur[1]));
                    }
                }
                catch (Exception ex) { Log.Warn($"side panel display: {ex.Message}"); }
                try { if (await _agent.Call("get_gamepad_state") is JsonObject g) _spMouse = (Str(g["chosen"]) ?? Str(g["mode"])) == "desktop"; }
                catch (Exception ex) { Log.Warn($"side panel gamepad: {ex.Message}"); }
            }
            // 正在运行的游戏：读它的功耗配置（功耗行改的是它）
            var game = _game?.Item;
            if (game == null && Str(_spState?["system"]?["game"]?["title"]) is { } title)
                game = _lib.Games.FirstOrDefault(i => string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase));
            if (game?.Id != _spGame?.Id || (game != null && (first || _spGs == null)))
            {
                _spGame = game;
                _spGs = null;
                if (game != null)
                    try { _spGs = await _agent.CallLong("game_settings_get", 30, new JsonObject { ["id"] = game.Id, ["platform"] = game.Platform, ["title"] = game.Title }) as JsonObject; }
                    catch (Exception ex) { Log.Warn($"side panel game power: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"side panel state: {ex.Message}"); }
        finally { _spReading = false; }
        sp.Polls++;
        if (_sp != sp || _spWrites?.IsBusy == true || _spDrag != null) return;
        SpApplyState(sp, first);
    }

    /// <summary>后台读回来的状态画上去：变了的才动（开关淡入淡出、滑钮弹过去、功耗数字滚动）。</summary>
    private void SpApplyState(SpState sp, bool first)
    {
        bool muted = Bool(_spState?["system"]?["muted"]);
        if (first || muted != sp.StaticMuted) { sp.StaticMuted = muted; SpPaintStatic(sp); }
        if (first || _spMouse != sp.PadMouse) { sp.PadMouse = _spMouse; SpPaintTile(sp, Array.FindIndex(SpTiles, e => e.Id == "pad")); }
        SpApplyTiles(sp, animate: !first);
        SpApplySliders(sp, animate: !first);
        int tdp = SpTdpNow();
        if (tdp != _spTdp)
        {
            int dir = Math.Sign(tdp - _spTdp);
            _spTdp = tdp;
            SpSpring(sp.P, "pw", tdp, 500, 0.8f);
            SpRollTo(sp, tdp, first ? 0 : dir);
        }
        if (sp.Drop_ < 0) for (int i = 0; i < 3; i++) SpSetPickVal(sp, i, 0);
        if (sp.SubPage is "wifi" or "bt" && sp.SubRows.Count > 0 && sp.SubRows[0].On != (sp.SubPage == "wifi" ? _spWifi == true : _spBt == true)) { SpBuildSub(sp); SpPaintSub(sp); }
        // 实际功耗：显示值慢慢追上读数
        float act = (float)SpNum(_spState, "power.actual.package", -1);
        if (sp.ActShown < 0 || act < 0) { sp.ActShown = act; SpPaintPwText(sp); return; }
        if (MathF.Abs(act - sp.ActShown) < 0.05f) { SpPaintPwText(sp); return; }
        if (sp.Chase == null)
        {
            sp.Chase = _dq.CreateTimer();
            sp.Chase.Interval = TimeSpan.FromMilliseconds(50);
            sp.Chase.Tick += (t, _) =>
            {
                float target = (float)SpNum(_spState, "power.actual.package", sp.ActShown);
                sp.ActShown += (target - sp.ActShown) * 0.15f;
                if (MathF.Abs(target - sp.ActShown) < 0.05f) { sp.ActShown = target; t.Stop(); }
                if (_sp == sp) SpPaintPwText(sp); else t.Stop();
            };
        }
        sp.Chase.Start();
    }

    // ---------------- 触控 / 鼠标 ----------------

    private (uint Id, string What, double X0, double Y0)? _spTouch;
    private string? _spDrag;

    /// <summary>按下：面板关着时点右上胶囊 = 开；开着时点面板外 / 状态行 = 关，滑块和刻度条按住拖，其他按下去松手时算点。</summary>
    private bool SpPointerDown(PointerRoutedEventArgs e, Microsoft.UI.Input.PointerPoint pt)
    {
        if (!CsActive || _cs?.Page is not { Style: HomeStyle.Row } page || _csInsert != null || _rm != null || _rs != null || _rq != null) return false;
        var (x, y) = CsLocal(pt.Position.X, pt.Position.Y);
        if (_sp is not { Open: true } sp)
        {
            if (x < SpPillLeft() || x > SpR || y < SpT || y > SpT + SpH0) return false;
            _spTouch = (pt.PointerId, "open", x, y);
        }
        else if (x < SpL1 || x > SpR || y < SpT || y > SpT + SpH1 || y < SpT + SpH0) { WithCue(Sfx.MenuOut, () => SpClose()); _spTouch = (pt.PointerId, "eat", x, y); }
        else if (sp.Drop_ >= 0) _spTouch = (pt.PointerId, "drop", x, y);
        else if (sp.SubPage == null && x >= SpTrackX0 - 24 && x <= SpTrackX1 + 24 && (y >= SpBriY && y <= SpBriY + SpSlH || y >= SpVolY && y <= SpVolY + SpSlH))
        {
            int k = y < SpVolY ? 0 : 1;
            _spDrag = k == 0 ? "s:bri" : "s:vol";
            _spTouch = (pt.PointerId, _spDrag, x, y);
            if (sp.Focus != _spDrag) SpFocusTo(sp, _spDrag, move: true);
            SpDragTo(sp, x);
        }
        else if (sp.SubPage == null && x >= SpMeterX - 16 && x <= SpMeterX + 30 * SpMeterPitch + 22 && y >= SpMeterBase - 48 && y <= SpMeterBase + 12)
        {
            _spDrag = "m:pw";
            _spTouch = (pt.PointerId, _spDrag, x, y);
            if (sp.Focus != _spDrag) SpFocusTo(sp, _spDrag, move: true);
            SpDragTo(sp, x);
        }
        else _spTouch = (pt.PointerId, "tap", x, y);
        _rootEl.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private void SpDragTo(SpState sp, double x)
    {
        if (_spDrag == "m:pw")
        {
            int v = SpWMin + (int)Math.Round(Math.Clamp((x - SpMeterX - 3) / (30 * SpMeterPitch), 0, 1) * (SpWMax - SpWMin));
            if (v != _spTdp) WithCue(Sfx.None, () => SpSetTdp(sp, v));
            return;
        }
        int k = _spDrag == "s:bri" ? 0 : 1;
        int lv = (int)Math.Round(Math.Clamp((x - SpTrackX0) / SpTrackW, 0, 1) * 100);
        if (lv != SpLevel(k) || (k == 1 && Bool(_spState?["system"]?["muted"]))) SpSetLevel(sp, k, lv);
    }

    private bool SpPointerMove(PointerRoutedEventArgs e)
    {
        if (_spTouch is not { } t || e.Pointer.PointerId != t.Id) return false;
        e.Handled = true;
        if (_spDrag != null && _sp is { Open: true } sp)
        {
            var pt = e.GetCurrentPoint(_rootEl).Position;
            var (x, _) = CsLocal(pt.X, pt.Y);
            SpDragTo(sp, x);
        }
        return true;
    }

    private bool SpPointerUp(PointerRoutedEventArgs e, bool canceled = false)
    {
        if (_spTouch is not { } t || e.Pointer.PointerId != t.Id) return false;
        _spTouch = null;
        _spDrag = null;
        _rootEl.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (canceled) return true;
        var pt = e.GetCurrentPoint(_rootEl).Position;
        var (x, y) = CsLocal(pt.X, pt.Y);
        bool tap = Math.Abs(x - t.X0) + Math.Abs(y - t.Y0) < 24;
        if (!tap) return true;
        if (t.What == "open") { if (_cs?.Page is { Style: HomeStyle.Row } p) WithCue(Sfx.MenuIn, () => SpOpen(p)); return true; }
        if (_sp is not { Open: true } sp) return true;
        if (t.What == "drop")
        {
            float y0 = SpDropY(sp);
            int n = SpPick(sp.Drop_).Options.Length, k = (int)Math.Floor((y - y0) / SpOptH);
            if (x >= SpPickX && x <= SpPickX + SpPickW && k >= 0 && k < n) { sp.DropSel = k; WithCue(Sfx.Positive, SpActivate); }
            else WithCue(Sfx.Back, () => SpDropClose(sp, animate: true));
            return true;
        }
        if (t.What != "tap") return true;
        var hit = SpItems(sp).FirstOrDefault(i => x >= i.X && x <= i.X + i.W && y >= i.Y && y <= i.Y + i.H);
        if (hit.Id == null) return true;
        WithCue(Sfx.Nav, () =>
        {
            if (sp.Focus != hit.Id) SpFocusTo(sp, hit.Id, move: true);
            if (hit.Id == "m:pw") return;
            if (hit.Id == "s:vol" && x < SpTrackX0 - 24) { SpToggleMute(sp); return; }
            if (hit.Id.StartsWith("s:")) return;
            SpActivate();
        });
        return true;
    }
}
