// 控制中心的外围：右上状态栏（Wi-Fi、蓝牙、电量、日期、时间），左下功能坞（电源 · 文件管理器 · 浏览器 · 截图），
// 右下头像胶囊（folia 网格模式右下角账户按钮的样子，点了打开 Windows 设置）。电源按一下，上方滑出睡眠 / 休眠 / 重启 / 关机。
// 手柄：滑条上按 ↓ 进功能坞，← → 在五个按钮之间走，↑ 回滑条；电源菜单里 ↑ ↓ 选，B 收起。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum CcZone { Row, Dock, Power }

    private sealed class CcButton
    {
        public required string Id;
        public required string Glyph;
        public string Label = "";
        public ContainerVisual El = null!;
        public SpriteVisual Normal = null!, Focused = null!;
        /// <summary>屏幕 DIP（相对控制中心层）。</summary>
        public Vector2 Pos, Size;
        public bool On;
    }

    // 设计 px（× _ccK）
    private const float CcEdgeX = 40, CcEdgeBottom = 32, CcDockBtn = 48, CcDockGap = 10, CcDockIcon = 20;
    private const float CcPowerH = 44, CcPowerW = 128, CcPowerGap = 10;
    private const float CcAvatar = 38, CcPillPad = 5;
    private const float CcStatusH = 36;
    private const string CcSnowflake = "❄";
    /// <summary>folia 账户按钮的底：zinc-950；按钮稍微透一点，下面的动态背景能透出来。</summary>
    private static readonly Color CcChip = Gfx.Rgba(9, 9, 11, 0.78);

    private ContainerVisual? _ccChrome;
    private ContainerVisual _ccPowerLayer = null!;
    private SpriteVisual _ccStatus = null!;
    private readonly List<CcButton> _ccDock = new();
    private readonly List<CcButton> _ccPower = new();
    private CcZone _ccZone;
    private int _ccDockFocus, _ccPowerFocus;
    private bool _ccPowerOpen;
    private string _ccStatusDrawn = "", _ccChromeDrawn = "";
    private string? _ccUser;
    private SystemInfo.NetState _ccNet;
    private bool? _ccBt;
    private long _ccSysRead;
    private bool _ccSysBusy;

    private void CcEnsureChrome()
    {
        if (_ccChrome != null) return;
        _ccChrome = G.Container();
        _cc!.Children.InsertAtTop(_ccChrome);
        _ccStatus = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _ccChrome.Children.InsertAtTop(_ccStatus);
        foreach (var (id, glyph, label) in new[]
        {
            ("power", "", "电源"), ("files", "", "文件管理器"), ("browser", "", "浏览器"),
            ("shots", "", "截图"), ("avatar", "", ""),
        })
            _ccDock.Add(CcMakeButton(id, glyph, label, _ccChrome));
        _ccPowerLayer = G.Container();
        _ccPowerLayer.IsVisible = false;
        _ccChrome.Children.InsertAtTop(_ccPowerLayer);
        var caps = SystemInfo.PowerCaps();
        Log.Info($"power caps: {caps}");
        var items = new List<(string, string, string)> { ("sleep", "", "睡眠") };
        if (caps.Hibernate) items.Add(("hibernate", CcSnowflake, "休眠"));
        items.Add(("restart", "", "重启"));
        items.Add(("shutdown", "", "关机"));
        foreach (var (id, glyph, label) in items) _ccPower.Add(CcMakeButton(id, glyph, label, _ccPowerLayer));
    }

    private CcButton CcMakeButton(string id, string glyph, string label, ContainerVisual parent)
    {
        var b = new CcButton { Id = id, Glyph = glyph, Label = label };
        b.El = G.C.CreateContainerVisual();
        b.Normal = G.Sprite(G.C.CreateSurfaceBrush());
        b.Focused = G.Sprite(G.C.CreateSurfaceBrush());
        b.Focused.Opacity = 0;
        b.El.Children.InsertAtTop(b.Normal);
        b.El.Children.InsertAtTop(b.Focused);
        parent.Children.InsertAtTop(b.El);
        return b;
    }

    // ---------------- 布局与绘制 ----------------

    /// <summary>CcMeasure 里调：摆位置；尺寸 / 头像 / 用户名变了才重画按钮。</summary>
    private void CcLayoutChrome()
    {
        CcEnsureChrome();
        float k = _ccK, vw = (float)View.Width, vh = (float)View.Height;
        _ccUser ??= SystemInfo.UserName();
        float y = vh - (CcEdgeBottom + CcDockBtn) * k;
        float x = CcEdgeX * k;
        for (int i = 0; i < 4; i++)
        {
            _ccDock[i].Size = new Vector2(CcDockBtn * k);
            _ccDock[i].Pos = new Vector2(x, y);
            x += (CcDockBtn + CcDockGap) * k;
        }
        var av = _ccDock[4];
        float pillW = CcAvatarPillW();
        av.Size = new Vector2(pillW * k, CcDockBtn * k);
        av.Pos = new Vector2(vw - (CcEdgeX + pillW) * k, y);
        // 电源菜单：从电源按钮正上方一格一格往上排（离按钮最近的是睡眠）
        float px = (CcEdgeX + (CcDockBtn - CcPowerH) / 2) * k;
        for (int i = 0; i < _ccPower.Count; i++)
        {
            _ccPower[i].Size = new Vector2(CcPowerW * k, CcPowerH * k);
            _ccPower[i].Pos = new Vector2(px, y + (CcDockBtn - CcPowerH) / 2 * k - (i + 1) * (CcPowerH + CcPowerGap) * k);
        }
        foreach (var b in _ccDock.Concat(_ccPower))
        {
            b.El.Size = b.Size;
            b.El.Offset = new Vector3(b.Pos, 0);
            // 胶囊绕左边那个圆的中心放大（电源菜单），其余绕中心
            b.El.CenterPoint = _ccPower.Contains(b) ? new Vector3(b.Size.Y / 2, b.Size.Y / 2, 0) : new Vector3(b.Size / 2, 0);
        }
        string key = $"{k}|{Chrome.Dpi}|{_ccUser}|{_railModel.Avatar != null}";
        if (key != _ccChromeDrawn)
        {
            _ccChromeDrawn = key;
            foreach (var b in _ccDock.Concat(_ccPower)) CcPaintButton(b);
        }
        CcPaintStatus(force: true);
    }

    /// <summary>头像胶囊：头像 + 分隔线 + 返回箭头那一格（folia 账户按钮的样子，不写名字）。</summary>
    private float CcAvatarPillW() => CcPillPad + CcAvatar + 8 + 1 + 30 + CcPillPad;

    private void CcPaintButton(CcButton b)
    {
        float k = _ccK, w = b.Size.X / k, h = b.Size.Y / k;
        float raster = k * Chrome.Dpi * 1.1f;   // 焦点放大 1.08× 也不糊
        CcSet(b.Normal, CcDraw(w, h, raster, ds => CcDrawChip(ds, b, w, h, false)));
        CcSet(b.Focused, CcDraw(w, h, raster, ds => CcDrawChip(ds, b, w, h, true)));
    }

    /// <summary>按钮：平时 zinc-950 半透、白字，不描边；焦点 = 白底黑字（folia 首页选中页签的白胶囊）。</summary>
    private void CcDrawChip(CanvasDrawingSession ds, CcButton b, float w, float h, bool focus)
    {
        var fill = focus ? Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96) : _ccPower.Contains(b) ? Gfx.WithAlpha(CcChip, 0.96) : CcChip;   // 电源菜单浮在卡上，不透字
        var fg = focus ? Theme.Bg : Gfx.WithAlpha(CcText, 0.92);
        float r = h / 2;
        ds.FillRoundedRectangle(0, 0, w, h, r, r, fill);
        if (b.Id == "avatar")
        {
            float a = CcAvatar, ax = CcPillPad, ay = (h - a) / 2;
            using (var circle = CanvasGeometry.CreateCircle(ds, ax + a / 2, ay + a / 2, a / 2))
            using (ds.CreateLayer(1, circle))
            {
                if (_railModel.Avatar is { } bmp) ds.DrawImage(bmp, new Windows.Foundation.Rect(ax, ay, a, a), bmp.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                else
                {
                    ds.FillRectangle(ax, ay, a, a, Gfx.Rgba(0x3f, 0x3f, 0x46));
                    CcGlyph(ds, "", ax + a / 2, ay + a / 2, 18, Gfx.WithAlpha(CcText, 0.8));   // Contact
                }
            }
            float dx = ax + a + 8;
            ds.FillRectangle(dx, h / 2 - 9, 1, 18, focus ? Gfx.WithAlpha(Theme.Bg, 0.15) : Gfx.WithAlpha(CcText, 0.15));
            CcGlyph(ds, "", dx + 1 + 15, h / 2, 16, fg);
            return;
        }
        if (_ccPower.Contains(b))
        {
            CcGlyph(ds, b.Glyph, h / 2, h / 2, 18, fg);
            using var label = TextKit.Layout(ds, b.Label, TextKit.Ui(600), 15, w - h - 12, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(label, h - 4, (h - (float)label.LayoutBounds.Height) / 2 - (float)label.LayoutBounds.Y, fg);
            return;
        }
        CcGlyph(ds, b.Glyph, w / 2, h / 2, CcDockIcon, fg);
    }

    /// <summary>一个图标字形，按墨迹框居中放在 (cx, cy)。雪花用 Segoe UI Symbol（图标字体里没有）。</summary>
    private static void CcGlyph(CanvasDrawingSession ds, string glyph, float cx, float cy, float size, Color c)
    {
        var font = glyph == CcSnowflake ? new FontSpec("Segoe UI Symbol", 400) : new FontSpec(CcIconFont, 400);
        using var l = TextKit.Layout(ds, glyph, font, size, 400, CanvasHorizontalAlignment.Left);
        var ib = l.DrawBounds;
        ds.DrawTextLayout(l, cx - (float)(ib.X + ib.Width / 2), cy - (float)(ib.Y + ib.Height / 2), c);
    }

    // ---------------- 状态栏 ----------------

    private const float CcIcon = 18, CcIconStroke = 1.6f;

    /// <summary>
    /// 右上状态栏：一整个胶囊 [Wi-Fi 蓝牙 电池 电量 │ 日期 时间]。图标是同一套线框画法（18 px 格、1.6 px 圆头线），
    /// 文字同一个字号、同一个字重。内容（到分钟）没变就不重画。
    /// </summary>
    private void CcPaintStatus(bool force = false)
    {
        if (_ccChrome == null) return;
        var now = DateTime.Now;
        var bat = SystemInfo.Battery();
        string key = $"{now:yyyyMMddHHmm}|{bat}|{_ccNet}|{_ccBt}|{_ccK}|{Chrome.Dpi}";
        if (!force && key == _ccStatusDrawn) return;
        _ccStatusDrawn = key;

        var icons = new List<Action<CanvasDrawingSession, Color>>();
        icons.Add((ds, c) => CcIconNet(ds, c));
        if (_ccBt is bool bt) icons.Add((ds, c) => CcIconBluetooth(ds, bt ? c : Gfx.WithAlpha(c, 0.32)));
        if (bat.HasBattery) icons.Add((ds, c) => CcIconBattery(ds, c, bat));

        const float padX = 14, iconGap = 12, font = 14;
        var text = Gfx.WithAlpha(CcText, 0.92);
        string pct = bat.HasBattery ? $"{bat.Percent}%" : "";
        string clock = $"{now.Month}月{now.Day}日 周{"日一二三四五六"[(int)now.DayOfWeek]}  {now:HH:mm}";
        using var pctL = TextKit.Layout(G.Device, pct, TextKit.Ui(500), font, 200, CanvasHorizontalAlignment.Left);
        using var clockL = TextKit.Layout(G.Device, clock, TextKit.Ui(500), font, 400, CanvasHorizontalAlignment.Left);
        float pctW = pct.Length > 0 ? (float)pctL.LayoutBounds.Width + 5 : 0;
        float clockW = (float)clockL.LayoutBounds.Width;
        float W = padX + icons.Count * CcIcon + (icons.Count - 1) * iconGap + pctW + 12 + 1 + 12 + clockW + padX, H = CcStatusH;
        float raster = _ccK * Chrome.Dpi;
        CcSet(_ccStatus, CcDraw(W, H, raster, ds =>
        {
            ds.FillRoundedRectangle(0, 0, W, H, H / 2, H / 2, CcChip);
            float x = padX, iy = (H - CcIcon) / 2;
            var t0 = ds.Transform;
            foreach (var draw in icons)
            {
                ds.Transform = Matrix3x2.CreateTranslation(x, iy) * t0;
                draw(ds, text);
                x += CcIcon + iconGap;
            }
            ds.Transform = t0;
            x -= iconGap;
            if (pctW > 0) { ds.DrawTextLayout(pctL, x + 5, (H - (float)pctL.LayoutBounds.Height) / 2 - (float)pctL.LayoutBounds.Y, text); x += pctW; }
            x += 12;
            ds.FillRectangle(x, H / 2 - 8, 1, 16, Gfx.WithAlpha(CcText, 0.18));
            x += 1 + 12;
            ds.DrawTextLayout(clockL, x, (H - (float)clockL.LayoutBounds.Height) / 2 - (float)clockL.LayoutBounds.Y, text);
        }));
        float k = _ccK;
        _ccStatus.Size = new Vector2(W * k, H * k);
        // 和左上「控制中心」标题（24 px 粗体，上沿 32）同一条中线
        _ccStatus.Offset = new Vector3((float)View.Width - (CcEdgeX + W) * k, (32 + 16 - H / 2) * k, 0);
    }

    private static readonly CanvasStrokeStyle CcRound = new()
    {
        StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round,
    };

    /// <summary>Wi-Fi：底部一个点 + 三道弧，信号几格就亮几道（暗的 25 %）；没连上 = 全暗加一道斜线；有线 = 一台显示器。</summary>
    private void CcIconNet(CanvasDrawingSession ds, Color c)
    {
        var dim = Gfx.WithAlpha(c, 0.25);
        if (!_ccNet.Wlan && _ccNet.Wired)
        {
            ds.DrawRoundedRectangle(2, 3.5f, 14, 9.5f, 1.8f, 1.8f, c, CcIconStroke);
            ds.DrawLine(9, 13, 9, 15.5f, c, CcIconStroke, CcRound);
            ds.DrawLine(5.5f, 15.5f, 12.5f, 15.5f, c, CcIconStroke, CcRound);
            return;
        }
        bool on = _ccNet.Wlan;
        int lit = !on ? 0 : _ccNet.Bars >= 4 ? 3 : _ccNet.Bars == 3 ? 2 : _ccNet.Bars == 2 ? 1 : 0;
        var center = new Vector2(9, 15);
        ds.FillCircle(center.X, center.Y - 0.3f, 1.5f, on ? c : dim);
        float[] radii = { 4.3f, 8f, 11.6f };
        for (int i = 0; i < 3; i++)
        {
            using var pb = new CanvasPathBuilder(ds);
            float a0 = -MathF.PI * 0.75f, sweep = MathF.PI * 0.5f, r = radii[i];
            pb.BeginFigure(center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r);
            pb.AddArc(center, r, r, a0, sweep);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(g, i < lit ? c : dim, CcIconStroke, CcRound);
        }
        if (!on) ds.DrawLine(3, 3, 15, 15, c, CcIconStroke, CcRound);
    }

    /// <summary>蓝牙：那个符文，一笔画。</summary>
    private static void CcIconBluetooth(CanvasDrawingSession ds, Color c)
    {
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(5.2f, 5.6f);
        pb.AddLine(12.6f, 12.3f);
        pb.AddLine(9f, 15.6f);
        pb.AddLine(9f, 2.4f);
        pb.AddLine(12.6f, 5.7f);
        pb.AddLine(5.2f, 12.4f);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var g = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(g, c, CcIconStroke, CcRound);
    }

    /// <summary>电池：圆角外框 + 正极小头 + 按电量填充（低于 15 % 且没在充电时填红色），充电时中间一道闪电。</summary>
    private static void CcIconBattery(CanvasDrawingSession ds, Color c, BatteryState bat)
    {
        ds.DrawRoundedRectangle(1, 5, 14.2f, 8.4f, 2.4f, 2.4f, c, 1.4f);
        ds.FillRoundedRectangle(16, 7.6f, 1.6f, 3.2f, 0.8f, 0.8f, c);
        bool plug = bat.Charging || bat.OnAc;
        float full = 14.2f - 3.6f, w = MathF.Max(1.2f, full * Math.Clamp(bat.Percent, 0, 100) / 100f);
        var fill = !plug && bat.Percent < 15 ? Gfx.Rgba(0xef, 0x44, 0x44) : c;
        ds.FillRoundedRectangle(2.8f, 6.8f, w, 4.8f, 1.2f, 1.2f, fill);
        if (!bat.Charging) return;
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(9.3f, 5.6f);
        pb.AddLine(6.2f, 9.6f);
        pb.AddLine(8.3f, 9.6f);
        pb.AddLine(7.4f, 12.8f);
        pb.AddLine(10.6f, 8.6f);
        pb.AddLine(8.5f, 8.6f);
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var g = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(g, Theme.Bg);
        ds.DrawGeometry(g, Theme.Bg, 0.6f);
    }

    // ---------------- 开 / 关 / 每秒刷新 ----------------

    private void CcChromeOpen()
    {
        if (_ccChrome == null) return;
        _ccZone = CcZone.Row;
        CcPowerShow(false, instant: true);
        CcChromeFocus(instant: true);
        CcReadSystem(force: true);
        // 外围在卡片依次出现的同时淡进来
        if (!Reduced) _ccChrome.StartAnimation("Opacity", G.Scalar(0, 1, 360, EaseOutCss, delay: 160));
    }

    private void CcChromeHide()
    {
        if (_ccChrome == null) return;
        _ccChrome.StopAnimation("Opacity");
        _ccChrome.Opacity = 1;
        _ccZone = CcZone.Row;
        CcPowerShow(false, instant: true);
        CcChromeFocus(instant: true);
    }

    private void CcChromeTick()
    {
        if (_ccChrome == null) return;
        if ($"{_ccK}|{Chrome.Dpi}|{_ccUser}|{_railModel.Avatar != null}" != _ccChromeDrawn) CcLayoutChrome();
        CcPaintStatus();
        CcReadSystem();
    }

    /// <summary>网络与蓝牙在后台读（蓝牙是异步接口），5 s 一次；读到了再重画状态栏。</summary>
    private void CcReadSystem(bool force = false)
    {
        long now = NowMs;
        if (_ccSysBusy || (!force && now - _ccSysRead < 5000)) return;
        _ccSysRead = now;
        _ccSysBusy = true;
        _ = Task.Run(async () =>
        {
            var net = SystemInfo.Network();
            var bt = await SystemInfo.BluetoothOn();
            _dq.TryEnqueue(() =>
            {
                _ccSysBusy = false;
                _ccNet = net;
                _ccBt = bt;
                if (_ccOpen) CcPaintStatus();
            });
        });
    }

    // ---------------- 焦点 ----------------

    private void CcChromeFocus(bool instant = false)
    {
        for (int i = 0; i < _ccDock.Count; i++)
            CcFocusVisual(_ccDock[i], (_ccZone == CcZone.Dock && i == _ccDockFocus) || (i == 0 && _ccPowerOpen), instant);
        for (int i = 0; i < _ccPower.Count; i++)
            CcFocusVisual(_ccPower[i], _ccZone == CcZone.Power && i == _ccPowerFocus, instant);
    }

    private void CcFocusVisual(CcButton b, bool on, bool instant)
    {
        if (b.On == on && !instant) return;
        b.On = on;
        float s = on ? 1.08f : 1f;
        if (instant || Reduced)
        {
            b.Focused.StopAnimation("Opacity");
            b.Focused.Opacity = on ? 1 : 0;
            b.El.StopAnimation("Scale");
            b.El.Scale = new Vector3(s, s, 1);
            return;
        }
        b.Focused.StartAnimation("Opacity", G.Scalar(on ? 0 : 1, on ? 1 : 0, 150, EaseOutCss));
        var sc = G.C.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(1, new Vector3(s, s, 1), G.Ease(CcScrollEase));
        sc.Duration = Gfx.Ms(180);
        b.El.StartAnimation("Scale", sc);
    }

    // ---------------- 电源菜单 ----------------

    /// <summary>上滑展开：每格从电源按钮的位置往上推 16 px、淡入，0.2 s，一格比一格晚 30 ms（folia 菜单弹出 0.16 s 的节奏）。</summary>
    private void CcPowerShow(bool show, bool instant = false)
    {
        _ccPowerOpen = show;
        int v = ++_ccPowerVersion;
        if (instant || Reduced)
        {
            _ccPowerLayer.IsVisible = show;
            foreach (var p in _ccPower)
            {
                p.El.StopAnimation("Opacity"); p.El.Opacity = 1;
                p.El.StopAnimation("Offset"); p.El.Offset = new Vector3(p.Pos, 0);
            }
            return;
        }
        float rise = 16 * _ccK;
        if (show)
        {
            _ccPowerLayer.IsVisible = true;
            for (int i = 0; i < _ccPower.Count; i++)
            {
                var p = _ccPower[i];
                p.El.StartAnimation("Opacity", G.Scalar(0, 1, 200, EaseOutCss, delay: i * 30));
                var off = G.C.CreateVector3KeyFrameAnimation();
                off.InsertKeyFrame(0, new Vector3(p.Pos + new Vector2(0, rise + i * 6 * _ccK), 0), G.Linear);
                off.InsertKeyFrame(1, new Vector3(p.Pos, 0), G.Ease(CcCloseEase));
                off.Duration = Gfx.Ms(220);
                off.DelayTime = Gfx.Ms(i * 30);
                off.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                p.El.StartAnimation("Offset", off);
            }
        }
        else
        {
            foreach (var p in _ccPower)
            {
                p.El.StartAnimation("Opacity", G.Scalar(1, 0, 120, EaseOutCss));
                var off = G.C.CreateVector3KeyFrameAnimation();
                off.InsertKeyFrame(1, new Vector3(p.Pos + new Vector2(0, rise / 2), 0), G.Ease(EaseOutCss));
                off.Duration = Gfx.Ms(120);
                p.El.StartAnimation("Offset", off);
            }
            After(140, () => { if (v == _ccPowerVersion && !_ccPowerOpen) _ccPowerLayer.IsVisible = false; });
        }
    }

    private int _ccPowerVersion;

    private void CcPowerClose()
    {
        CcPowerShow(false);
        _ccZone = CcZone.Dock;
        _ccDockFocus = 0;
        CcChromeFocus();
    }

    // ---------------- 输入 ----------------

    /// <summary>方向键：返回 true = 外围接住了（滑条不动）。</summary>
    private bool CcChromeMove(Direction dir)
    {
        if (_ccChrome == null) return false;
        switch (_ccZone)
        {
            case CcZone.Row:
                if (dir != Direction.Down) return false;
                _ccZone = CcZone.Dock;
                CcChromeFocus();
                return true;
            case CcZone.Dock:
                if (dir == Direction.Up) _ccZone = CcZone.Row;
                else if (dir is Direction.Left or Direction.Right)
                    _ccDockFocus = Math.Clamp(_ccDockFocus + (dir == Direction.Left ? -1 : 1), 0, _ccDock.Count - 1);
                CcChromeFocus();
                return true;
            default:
                if (dir == Direction.Up) _ccPowerFocus = Math.Min(_ccPowerFocus + 1, _ccPower.Count - 1);
                else if (dir == Direction.Down)
                {
                    if (_ccPowerFocus == 0) { CcPowerClose(); return true; }
                    _ccPowerFocus--;
                }
                else { CcPowerClose(); return CcChromeMove(dir); }
                CcChromeFocus();
                return true;
        }
    }

    private bool CcChromeActivate()
    {
        if (_ccZone == CcZone.Dock) { CcRunDock(_ccDock[_ccDockFocus].Id); return true; }
        if (_ccZone == CcZone.Power) { CcRunPower(_ccPower[_ccPowerFocus].Id); return true; }
        return false;
    }

    private bool CcChromeBack()
    {
        if (_ccZone == CcZone.Power) { CcPowerClose(); return true; }
        if (_ccZone == CcZone.Dock) { _ccZone = CcZone.Row; CcChromeFocus(); return true; }
        return false;
    }

    /// <summary>点击：先看电源菜单，再看功能坞和头像；菜单开着点到别处 = 收起。</summary>
    private bool CcChromeClick(double sx, double sy)
    {
        if (_ccChrome == null) return false;
        static bool Hit(CcButton b, double x, double y) => x >= b.Pos.X && x <= b.Pos.X + b.Size.X && y >= b.Pos.Y && y <= b.Pos.Y + b.Size.Y;
        if (_ccPowerOpen)
        {
            for (int i = 0; i < _ccPower.Count; i++)
                if (Hit(_ccPower[i], sx, sy)) { _ccZone = CcZone.Power; _ccPowerFocus = i; CcChromeFocus(); CcRunPower(_ccPower[i].Id); return true; }
            if (!Hit(_ccDock[0], sx, sy)) { CcPowerShow(false); _ccZone = CcZone.Row; CcChromeFocus(); return true; }
        }
        for (int i = 0; i < _ccDock.Count; i++)
            if (Hit(_ccDock[i], sx, sy)) { _ccZone = CcZone.Dock; _ccDockFocus = i; CcChromeFocus(); CcRunDock(_ccDock[i].Id); return true; }
        return false;
    }

    private void CcRunDock(string id)
    {
        switch (id)
        {
            case "power":
                if (_ccPowerOpen) { CcPowerClose(); break; }
                CcPowerShow(true);
                _ccZone = CcZone.Power;
                _ccPowerFocus = 0;
                CcChromeFocus();
                break;
            case "files":
                SystemInfo.Open("explorer.exe");
                Toast("正在打开文件管理器");
                break;
            case "browser":
                _ = Task.Run(SystemInfo.OpenBrowser);
                Toast("正在打开浏览器");
                break;
            case "shots":
                Toast("截图管理页面还没有做");
                break;
            case "avatar":
                SystemInfo.Open("ms-settings:");
                Toast("正在打开系统设置");
                break;
        }
        Log.Info($"control center dock: {id}");
    }

    /// <summary>电源动作：先给提示，0.4 s 后执行（提示看得见）。--power-dry 只记日志（测试用，不真的关机）。</summary>
    private void CcRunPower(string id)
    {
        var item = _ccPower.First(p => p.Id == id);
        Toast($"正在{item.Label}…");
        Log.Info($"control center power: {id}");
        if (Program.Args.Contains("--power-dry")) return;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        After(400, () =>
        {
            switch (id)
            {
                case "sleep": SystemInfo.Sleep(hwnd); break;
                case "hibernate": SystemInfo.Hibernate(); break;
                case "restart": SystemInfo.Restart(); break;
                case "shutdown": SystemInfo.Shutdown(); break;
            }
        });
        CcPowerShow(false);
        _ccZone = CcZone.Dock;
        _ccDockFocus = 0;
        CcChromeFocus();
    }
}
