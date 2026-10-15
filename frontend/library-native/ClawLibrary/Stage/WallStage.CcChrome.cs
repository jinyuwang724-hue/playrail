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
    private const float CcEdgeX = 40, CcEdgeBottom = 36, CcDockBtn = 60, CcDockGap = 14, CcDockIcon = 24;
    private const float CcPowerH = 56, CcPowerW = 164, CcPowerGap = 12;
    private const float CcAvatar = 48, CcPillPad = 6;
    private const float CcStatusH = 40, CcStatusPillH = 36;
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

    /// <summary>头像胶囊：p-1.5 + 头像 + 名字（最多 120）+ 分隔线 + 返回箭头那一格（folia 的 ChevronRight）。</summary>
    private float CcAvatarPillW()
    {
        using var l = TextKit.Layout(G.Device, _ccUser ?? "", TextKit.Ui(600), 15, 120, CanvasHorizontalAlignment.Left);
        float nameW = MathF.Min(120, (float)l.LayoutBounds.Width);
        return CcPillPad + CcAvatar + 10 + nameW + 12 + 1 + CcAvatar + CcPillPad;
    }

    private void CcPaintButton(CcButton b)
    {
        float k = _ccK, w = b.Size.X / k, h = b.Size.Y / k;
        float raster = k * Chrome.Dpi * 1.1f;   // 焦点放大 1.08× 也不糊
        CcSet(b.Normal, CcDraw(w, h, raster, ds => CcDrawChip(ds, b, w, h, false)));
        CcSet(b.Focused, CcDraw(w, h, raster, ds => CcDrawChip(ds, b, w, h, true)));
    }

    /// <summary>按钮：平时 zinc-950 半透 + 1 px 白 10 % 描边、白字；焦点 = 白底黑字（folia 首页选中页签的白胶囊）。</summary>
    private void CcDrawChip(CanvasDrawingSession ds, CcButton b, float w, float h, bool focus)
    {
        var fill = focus ? Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96) : _ccPower.Contains(b) ? Gfx.WithAlpha(CcChip, 0.96) : CcChip;   // 电源菜单浮在卡上，不透字
        var fg = focus ? Theme.Bg : Gfx.WithAlpha(CcText, 0.92);
        float r = h / 2;
        ds.FillRoundedRectangle(0, 0, w, h, r, r, fill);
        if (!focus) ds.DrawRoundedRectangle(0.5f, 0.5f, w - 1, h - 1, r - 0.5f, r - 0.5f, Gfx.WithAlpha(CcText, 0.10), 1);
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
                    CcGlyph(ds, "", ax + a / 2, ay + a / 2, 22, Gfx.WithAlpha(CcText, 0.8));   // Contact
                }
            }
            using var name = TextKit.Layout(ds, _ccUser ?? "", TextKit.Ui(600), 15, 120, CanvasHorizontalAlignment.Left);
            float nx = ax + a + 10;
            ds.DrawTextLayout(name, nx, (h - (float)name.LayoutBounds.Height) / 2 - (float)name.LayoutBounds.Y, fg);
            float dx = w - CcPillPad - CcAvatar - 1;
            ds.FillRectangle(dx, h / 2 - 12, 1, 24, focus ? Gfx.WithAlpha(Theme.Bg, 0.15) : Gfx.WithAlpha(CcText, 0.15));
            CcGlyph(ds, "", dx + 1 + CcAvatar / 2, h / 2, 20, fg);
            return;
        }
        if (_ccPower.Contains(b))
        {
            CcGlyph(ds, b.Glyph, h / 2, h / 2, 22, fg);
            using var label = TextKit.Layout(ds, b.Label, TextKit.Ui(600), 17, w - h - 12, CanvasHorizontalAlignment.Left);
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

    /// <summary>右上状态栏：[Wi-Fi 蓝牙 电量] 胶囊 · 日期 · 时间。内容（到分钟）没变就不重画。</summary>
    private void CcPaintStatus(bool force = false)
    {
        if (_ccChrome == null) return;
        var now = DateTime.Now;
        var bat = SystemInfo.Battery();
        string key = $"{now:yyyyMMddHHmm}|{bat}|{_ccNet}|{_ccBt}|{_ccK}|{Chrome.Dpi}";
        if (!force && key == _ccStatusDrawn) return;
        _ccStatusDrawn = key;

        var icons = new List<(string glyph, string? under, double alpha)>();
        if (_ccNet.Wlan)
        {
            string bars = _ccNet.Bars >= 4 ? "" : _ccNet.Bars == 3 ? "" : _ccNet.Bars == 2 ? "" : "";
            icons.Add((bars, "", _ccNet.Online ? 0.92 : 0.5));
        }
        else if (_ccNet.Wired) icons.Add(("", null, _ccNet.Online ? 0.92 : 0.5));
        else icons.Add(("", null, 0.5));
        if (_ccBt is bool bt) icons.Add(("", null, bt ? 0.92 : 0.3));
        string? pct = null;
        if (bat.HasBattery)
        {
            int n = Math.Clamp((bat.Percent + 5) / 10, 0, 10);
            bool plug = bat.Charging || bat.OnAc;
            string g = plug ? (n == 10 ? "" : ((char)(0xE85A + n)).ToString()) : (n == 10 ? "" : ((char)(0xE850 + n)).ToString());
            icons.Add((g, null, 0.92));
            pct = $"{bat.Percent}%";
        }

        const float iconSize = 18, iconGap = 14, padX = 14;
        using var pctL = TextKit.Layout(G.Device, pct ?? "", TextKit.Ui(600), 14, 200, CanvasHorizontalAlignment.Left);
        string date = $"{now.Month}月{now.Day}日 周{"日一二三四五六"[(int)now.DayOfWeek]}";
        using var dateL = TextKit.Layout(G.Device, date, TextKit.Ui(500), 15, 400, CanvasHorizontalAlignment.Left);
        using var timeL = TextKit.Layout(G.Device, now.ToString("HH:mm"), TextKit.Ui(700), 24, 400, CanvasHorizontalAlignment.Left);
        float pctW = pct != null ? (float)pctL.LayoutBounds.Width + 6 : 0;
        float pillW = padX * 2 + icons.Count * iconSize + (icons.Count - 1) * iconGap + pctW;
        float dateW = (float)dateL.LayoutBounds.Width, timeW = (float)timeL.LayoutBounds.Width;
        float W = pillW + 18 + dateW + 14 + timeW + 2, H = CcStatusH;
        float raster = _ccK * Chrome.Dpi;
        CcSet(_ccStatus, CcDraw(W, H, raster, ds =>
        {
            float py = (H - CcStatusPillH) / 2, r = CcStatusPillH / 2;
            ds.FillRoundedRectangle(0, py, pillW, CcStatusPillH, r, r, CcChip);
            ds.DrawRoundedRectangle(0.5f, py + 0.5f, pillW - 1, CcStatusPillH - 1, r - 0.5f, r - 0.5f, Gfx.WithAlpha(CcText, 0.10), 1);
            float x = padX;
            foreach (var (glyph, under, alpha) in icons)
            {
                if (under != null) CcGlyph(ds, under, x + iconSize / 2, H / 2, iconSize, Gfx.WithAlpha(CcText, 0.22));
                // 信号格数不同的 Wi-Fi 字形墨迹框不一样大：画在满格那个字形的位置上（底部的点对齐）
                if (under != null && glyph != under) CcGlyphAligned(ds, glyph, under, x + iconSize / 2, H / 2, iconSize, Gfx.WithAlpha(CcText, alpha));
                else CcGlyph(ds, glyph, x + iconSize / 2, H / 2, iconSize, Gfx.WithAlpha(CcText, alpha));
                x += iconSize + iconGap;
            }
            if (pct != null) ds.DrawTextLayout(pctL, x - iconGap + 6, (H - (float)pctL.LayoutBounds.Height) / 2 - (float)pctL.LayoutBounds.Y, Gfx.WithAlpha(CcText, 0.92));
            float dx = pillW + 18;
            ds.DrawTextLayout(dateL, dx, (H - (float)dateL.LayoutBounds.Height) / 2 - (float)dateL.LayoutBounds.Y, Gfx.WithAlpha(CcText, 0.62));
            ds.DrawTextLayout(timeL, dx + dateW + 14, (H - (float)timeL.LayoutBounds.Height) / 2 - (float)timeL.LayoutBounds.Y, CcText);
        }));
        float k = _ccK;
        _ccStatus.Size = new Vector2(W * k, H * k);
        // 和左上「控制中心」标题（24 px 粗体，上沿 32）同一条中线
        _ccStatus.Offset = new Vector3((float)View.Width - (CcEdgeX + W) * k, (32 + 16 - H / 2) * k, 0);
    }

    /// <summary>画 glyph，但按 frame（同一套字形里墨迹框最大的那个）的位置定位：信号格字形和满格字形叠在一起。</summary>
    private static void CcGlyphAligned(CanvasDrawingSession ds, string glyph, string frame, float cx, float cy, float size, Color c)
    {
        var font = new FontSpec(CcIconFont, 400);
        using var f = TextKit.Layout(ds, frame, font, size, 400, CanvasHorizontalAlignment.Left);
        using var l = TextKit.Layout(ds, glyph, font, size, 400, CanvasHorizontalAlignment.Left);
        var fb = f.DrawBounds;
        ds.DrawTextLayout(l, cx - (float)(fb.X + fb.Width / 2), cy - (float)(fb.Y + fb.Height / 2), c);
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
