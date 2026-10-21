// 添加游戏 › 匹配游戏弹窗：选完 exe 进添加页时弹出（之后「匹配」那一行 / Y 再打开）。
// 样子照截图页的筛选面板：整页压暗，背后的页面糊一层；中间一块强调色压暗的圆角面板，顶上一行小字，下面是搜索框（真输入框，
// 默认 exe 名，打字停半秒自动重搜，搜索中下划线上有一道光来回走），再下面是下拉列表：一行一款，左名称、右年份，
// 焦点那行近白底深色字。弹出时面板从 0.96 弹到 1、压暗层淡入。A / 回车用焦点那款，B 关掉（不配图也行）。
// 搜索栏三种样子（给用户挑，--searchstyle=0/1/2 或 ui.ini searchStyle）：
//   0 胶囊：面板里一条圆角胶囊输入框，放大镜在胶囊里；
//   1 大字：没有框，28 px 大字直接写在面板上，下面一道细线；
//   2 顶栏：整条贴在屏幕顶上的横栏（从上面滑下来），下拉列表挂在它下面左侧。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const float AgPopW = 560, AgPopX = (1280 - AgPopW) / 2, AgPopY = 150, AgPopRow = 48, AgPopRows = 8;

    private bool _agPopOpen;
    /// <summary>焦点：-1 = 输入框，0.. = 列表里那一款。</summary>
    private int _agPopIndex = -1, _agPopFirst, _agPopVersion, _agTypeVersion;
    private SpriteVisual? _agPopScrim, _agPopPanel, _agPopBar;
    private TextBox? _agPopBox;

    /// <summary>搜索栏样子（0 胶囊 / 1 大字 / 2 顶栏）。</summary>
    private int AgPopStyle => Program.Args.FirstOrDefault(a => a.StartsWith("--searchstyle=")) is { } a && int.TryParse(a[14..], out var v) ? Math.Clamp(v, 0, 2) : Math.Clamp(_prefs.SearchStyle, 0, 2);

    /// <summary>弹窗的几何（舞台坐标）：整块图的范围、输入框、搜索中那道光走的线、列表的位置。</summary>
    private sealed record AgPopG(Rect Area, Rect Input, float Font, Rect Line, float ListTop, float ListX, float ListW);

    private AgPopG AgPopGeo()
    {
        int rows = (int)Math.Min(AgPopRows, _agDraft?.Matches.Count ?? 0);
        float listH = rows * AgPopRow + (rows > 0 ? 12 : 0);
        switch (AgPopStyle)
        {
            case 1:
            {
                var input = new Rect(AgPopX + 28, AgPopY + 24, AgPopW - 56 - 48, 64);
                float listTop = AgPopY + 104;
                return new AgPopG(new Rect(AgPopX, AgPopY, AgPopW, listTop - AgPopY + listH + (rows > 0 ? 0 : 4)), input, 28,
                    new Rect(AgPopX + 28, AgPopY + 90, AgPopW - 56, 1), listTop, AgPopX, AgPopW);
            }
            case 2:
            {
                // 顶栏：标题下面整条（40–1240）的横栏，列表挂在它下面左侧
                var input = new Rect(40 + 56, 96 + 6, 1200 - 56 - 80, 52);
                float listTop = 96 + 72;
                return new AgPopG(new Rect(40, 96, 1200, rows > 0 ? 72 + listH + 4 : 64), input, 20,
                    new Rect(40 + 24, 96 + 62, 1200 - 48, 2), listTop, 40, 640);
            }
            default:
            {
                var pill = new Rect(AgPopX + 20, AgPopY + 48, AgPopW - 40, 48);
                float listTop = AgPopY + 108;
                return new AgPopG(new Rect(AgPopX, AgPopY, AgPopW, listTop - AgPopY + listH + (rows > 0 ? 0 : -4)),
                    new Rect(pill.X + 46, pill.Y, pill.Width - 60, pill.Height), 17,
                    new Rect(pill.X + 20, pill.Y + pill.Height - 2, pill.Width - 40, 2), listTop, AgPopX, AgPopW);
            }
        }
    }

    private void AgOpenPop()
    {
        var d = _agDraft;
        if (d == null || _ag == null) return;
        if (_agEditing != null) AgEndEdit(true);
        if (_agPopScrim == null)
        {
            _agPopScrim = G.Sprite(G.Solid(Microsoft.UI.Colors.Black), relative: false);
            _agPopPanel = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _agPopBar = G.Sprite(G.Solid(Microsoft.UI.Colors.White), relative: false);
            _ag.Children.InsertAtTop(_agPopScrim);
            _ag.Children.InsertAtTop(_agPopPanel);
            _ag.Children.InsertAtTop(_agPopBar);
        }
        _agPopOpen = true;
        int v = ++_agPopVersion;
        _agPopIndex = d.Matches.Count > 0 ? Math.Max(0, d.Match) : -1;
        _agPopFirst = 0;
        float k = _ccK;
        _agPopScrim.Size = new Vector2(1280 * k, 800 * k);
        _agPopScrim.IsVisible = true;
        _agPopPanel!.IsVisible = true;
        AgPaint();   // 页面糊一层，再画面板
        if (Reduced)
        {
            _agPopScrim.Opacity = 0.45f; _agPopPanel.Opacity = 1; _agPopPanel.Scale = Vector3.One;
        }
        else
        {
            _agPopScrim.Opacity = 0;
            _agPopScrim.StartAnimation("Opacity", ToScalar(0.45f, 220, EaseOutCss));
            _agPopPanel.Opacity = 0;
            _agPopPanel.StartAnimation("Opacity", ToScalar(1, 180, EaseOutCss));
            if (AgPopStyle == 2)
            {
                // 顶栏：从屏幕上边滑下来
                _agPopPanel.Scale = Vector3.One;
                var to = _agPopPanel.Offset;
                _agPopPanel.Offset = to with { Y = to.Y - 24 * k };
                _agPopPanel.StartAnimation("Offset", G.SpringV3(to, Spring.Button));
            }
            else
            {
                _agPopPanel.Scale = new Vector3(0.96f, 0.96f, 1);
                _agPopPanel.StartAnimation("Scale", G.SpringV3(Vector3.One, Spring.Button));
            }
        }
        // 输入框等面板弹出来再放（不然它比面板先到位）。不抢键盘焦点：一抢 Windows 就把任务栏叫出来；
        // 要改字时按 A / Y 或点一下输入框，那时才给焦点、叫触摸键盘
        After(Reduced ? 0 : 140, () => { if (_agPopOpen && v == _agPopVersion) AgPlacePopBox(); });
        if (d.Matches.Count == 0 && !d.Searching) _ = AgSearch(d);
        Log.Info("add game: match popup open");
    }

    private void AgClosePop(bool instant = false)
    {
        if (!_agPopOpen) return;
        _agPopOpen = false;
        int v = ++_agPopVersion;
        if (_agPopBox != null)
        {
            if (_agPopBox.FocusState != FocusState.Unfocused) _rootEl.Focus(FocusState.Programmatic);
            _agPopBox.Visibility = Visibility.Collapsed;
        }
        if (_agPopBar != null) { _agPopBar.StopAnimation("Offset.X"); _agPopBar.IsVisible = false; }
        if (_agPopScrim == null || _agPopPanel == null) return;
        if (instant || Reduced)
        {
            _agPopScrim.StopAnimation("Opacity"); _agPopScrim.IsVisible = false;
            _agPopPanel.StopAnimation("Opacity"); _agPopPanel.IsVisible = false;
        }
        else
        {
            _agPopScrim.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
            _agPopPanel.StartAnimation("Opacity", ToScalar(0, 140, EaseOutCss));
            if (AgPopStyle != 2) _agPopPanel.StartAnimation("Scale", G.SpringV3(new Vector3(0.97f, 0.97f, 1), Spring.Button));
            After(180, () => { if (!_agPopOpen && v == _agPopVersion) { _agPopScrim.IsVisible = false; _agPopPanel.IsVisible = false; } });
        }
        if (!instant && _agOpen) AgPaint();
    }

    // ---------------- 输入 ----------------

    private void AgPopMove(Direction dir)
    {
        var d = _agDraft;
        if (d == null) return;
        int n = d.Matches.Count;
        if (dir == Direction.Down && _agPopIndex < n - 1) _agPopIndex++;
        else if (dir == Direction.Up && _agPopIndex >= 0) _agPopIndex--;
        else return;
        NavHaptic("move");
        AgPaintPop();
    }

    /// <summary>A / 回车：焦点在列表里就用那款；在输入框上就马上搜（打字停半秒也会自己搜）。</summary>
    private void AgPopEnter()
    {
        var d = _agDraft;
        if (d == null) return;
        NavHaptic("select");
        if (_agPopIndex >= 0 && _agPopIndex < d.Matches.Count && !d.Searching) { AgChooseMatch(d, _agPopIndex); return; }
        if (_agPopIndex < 0 && _agPopBox?.FocusState == FocusState.Unfocused) { AgPopFocusBox(); return; }
        string q = (_agPopBox?.Text ?? d.Query).Trim();
        if (q.Length == 0) return;
        d.Query = q;
        _ = AgSearch(d);
    }

    private void AgPopClick(Point p)
    {
        var d = _agDraft;
        if (d == null) return;
        var g = AgPopGeo();
        bool inList = p.X >= g.ListX && p.X <= g.ListX + g.ListW && p.Y >= g.ListTop && p.Y <= g.Area.Y + g.Area.Height;
        if (!inList && !(g.Area.Contains(p) && p.Y < g.ListTop)) { AgClosePop(); return; }
        float listTop = g.ListTop;
        if (p.Y < listTop) { _agPopIndex = -1; AgPopFocusBox(); AgPaintPop(); return; }
        int r = (int)Math.Floor((p.Y - listTop) / AgPopRow);
        int i = _agPopFirst + r;
        if (r >= 0 && i < d.Matches.Count && !d.Searching) AgChooseMatch(d, i);
    }

    private void AgPopFocusBox()
    {
        AgPlacePopBox();
        _agPopBox!.Focus(FocusState.Programmatic);
        _agPopBox.Select(_agPopBox.Text.Length, 0);
        ShowTouchKeyboard();
    }

    private void AgPlacePopBox()
    {
        if (_agPopBox == null)
        {
            _agPopBox = new TextBox
            {
                MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0), RequestedTheme = ElementTheme.Dark,
                IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextAlignment = TextAlignment.Left,
                FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), Visibility = Visibility.Collapsed,
            };
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
                _agPopBox.Resources[key] = clear;
            var ink = new SolidColorBrush(Theme.Ink);
            foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
                _agPopBox.Resources[key] = ink;
            _agPopBox.KeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case Windows.System.VirtualKey.Enter: e.Handled = true; AgPopEnter(); break;
                    case Windows.System.VirtualKey.Escape: e.Handled = true; AgClosePop(); break;
                    case Windows.System.VirtualKey.Down: e.Handled = true; AgPopMove(Direction.Down); break;
                    case Windows.System.VirtualKey.Up: e.Handled = true; AgPopMove(Direction.Up); break;
                }
            };
            // 打字停半秒就重搜
            _agPopBox.TextChanged += (_, _) =>
            {
                var d = _agDraft;
                if (!_agPopOpen || d == null) return;
                string q = _agPopBox.Text.Trim();
                if (q.Length == 0 || q == d.Query) return;
                int tv = ++_agTypeVersion;
                After(500, () =>
                {
                    if (tv != _agTypeVersion || !_agPopOpen || _agDraft != d) return;
                    d.Query = q;
                    _ = AgSearch(d);
                });
            };
            _agPopBox.Loaded += (_, _) =>
            {
                if (FindChild(_agPopBox, "DeleteButton") is FrameworkElement x) { x.MaxWidth = 0; x.Opacity = 0; x.IsHitTestVisible = false; }
            };
            _rootEl.Overlay.Children.Add(_agPopBox);
        }
        float k = _ccK;
        var g = AgPopGeo();
        var r = g.Input;
        if (_agPopBox.Text != _agDraft?.Query) _agPopBox.Text = _agDraft?.Query ?? "";
        _agPopBox.FontSize = g.Font * k;
        _agPopBox.Width = r.Width * k;
        _agPopBox.Height = r.Height * k;
        _agPopBox.Padding = new Thickness(0, Math.Max(0, (r.Height - g.Font * 1.33) / 2 * k - 1), 0, 0);
        Canvas.SetLeft(_agPopBox, r.X * k);
        Canvas.SetTop(_agPopBox, r.Y * k);
        _agPopBox.Visibility = Visibility.Visible;
    }

    // ---------------- 绘制 ----------------

    private void AgPaintPop()
    {
        var d = _agDraft;
        if (!_agPopOpen || d == null || _agPopPanel == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        var g = AgPopGeo();
        int n = d.Matches.Count, rows = (int)Math.Min(AgPopRows, n), style = AgPopStyle;
        if (_agPopIndex >= 0) _agPopFirst = Math.Clamp(_agPopFirst, Math.Max(0, _agPopIndex - rows + 1), Math.Min(_agPopIndex, Math.Max(0, n - rows)));
        var ui = TextKit.Ui(400);
        var dark = Gfx.Rgba(9, 9, 11);
        // 底色：强调色压暗（和控制中心的封面取色一致），不是纯黑
        var fill = Gfx.WithAlpha(Gfx.Mix(Theme.Bg, 0.78, PerfAccent), 0.96);
        float ox = (float)g.Area.X, oy = (float)g.Area.Y, w = (float)g.Area.Width, h = (float)g.Area.Height;
        bool inputFocus = _agPopIndex < 0;
        // 右上：几款；连不上 / 密钥无效时换成这两个字
        string count = d.Searching ? "" : d.Status is "连不上" or "密钥无效" ? d.Status : n.ToString();
        CcSet(_agPopPanel, CcDraw(w, h, raster, ds =>
        {
            void Count(float right, float cy)
            {
                if (count.Length == 0) return;
                var cf = char.IsDigit(count[0]) ? AgMono : TextKit.Ui(500);
                float cw = PWidth(ds, count, cf, 13);
                PText(ds, count, cf, 13, Gfx.WithAlpha(CcText, 0.5), right - cw, cy, cw + 4);
            }
            var line = new Rect(g.Line.X - ox, g.Line.Y - oy, g.Line.Width, g.Line.Height);
            var input = new Rect(g.Input.X - ox, g.Input.Y - oy, g.Input.Width, g.Input.Height);
            switch (style)
            {
                case 1:   // 大字：没有框，大字 + 细线，右头数字
                    ds.FillRoundedRectangle(0, 0, w, h, 18, 18, fill);
                    ds.FillRectangle(line, inputFocus ? Gfx.WithAlpha(CcText, 0.9f) : Gfx.WithAlpha(CcText, 0.2f));
                    Count(w - 28, (float)(input.Y + input.Height / 2));
                    break;
                case 2:   // 顶栏：整条横栏，放大镜 + 字，右头数字；列表是挂在下面左侧的一块
                    ds.FillRoundedRectangle(0, 0, w, 64, 18, 18, fill);
                    AgPopSearchIcon(ds, 24, (float)(input.Y + input.Height / 2), inputFocus ? CcText : Gfx.WithAlpha(CcText, 0.7f));
                    ds.FillRectangle(line, inputFocus ? Gfx.WithAlpha(CcText, 0.9f) : Gfx.WithAlpha(CcText, 0.16f));
                    Count(w - 24, (float)(input.Y + input.Height / 2));
                    if (rows > 0) ds.FillRoundedRectangle(g.ListX - ox, g.ListTop - oy - 4, g.ListW, h - (g.ListTop - oy) + 4, 18, 18, fill);
                    break;
                default:  // 胶囊：面板里一条圆角输入框，放大镜在里面
                    ds.FillRoundedRectangle(0, 0, w, h, 18, 18, fill);
                    PText(ds, "匹配游戏", TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.42), 24, 28, 300);
                    Count(w - 24, 29);
                    var pill = new Rect(20, input.Y, w - 40, input.Height);
                    ds.FillRoundedRectangle(pill, 24, 24, Gfx.Rgba(0xfa, 0xfa, 0xfa, inputFocus ? 0.16 : 0.08));
                    if (inputFocus) ds.DrawRoundedRectangle(pill, 24, 24, Gfx.WithAlpha(CcText, 0.9f), 2);
                    AgPopSearchIcon(ds, 38, (float)(pill.Y + pill.Height / 2), inputFocus ? CcText : Gfx.WithAlpha(CcText, 0.7f));
                    break;
            }
            // 下拉列表：左名称、右年份
            float lx = g.ListX - ox, lw = g.ListW;
            for (int r = 0; r < rows; r++)
            {
                int i = _agPopFirst + r;
                var m = d.Matches[i];
                float ry = g.ListTop - oy + r * AgPopRow, cy = ry + AgPopRow / 2;
                bool focus = i == _agPopIndex;
                if (focus) ds.FillRoundedRectangle(lx + 10, ry + 2, lw - 20, AgPopRow - 4, 12, 12, Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96));
                var ink = focus ? dark : Gfx.WithAlpha(CcText, 0.88);
                int year = Int(m["year"]);
                string ys = year > 0 ? year.ToString() : "";
                float yw = ys.Length > 0 ? PWidth(ds, ys, AgMono, 13) : 0;
                if (i == d.Match) ds.FillCircle(lx + 30, cy, 3, ink);
                PText(ds, Str(m["name"]) ?? "", ui, 17, ink, lx + 42, cy, lw - 42 - 24 - yw - 16);
                if (ys.Length > 0) PText(ds, ys, AgMono, 13, focus ? Gfx.Rgba(9, 9, 11, 0.55) : Gfx.WithAlpha(CcText, 0.45), lx + lw - 24 - yw, cy + 1, yw + 4);
            }
        }));
        _agPopPanel.Size = new Vector2(w * k, h * k);
        _agPopPanel.Offset = new Vector3(ox * k, oy * k, 0);
        _agPopPanel.CenterPoint = new Vector3(w * k / 2, h * k / 2, 0);
        AgPopSearching(d.Searching);
    }

    /// <summary>搜索中：输入框下划线上一道白光来回走。</summary>
    private void AgPopSearching(bool on)
    {
        if (_agPopBar == null) return;
        if (!on || !_agPopOpen) { _agPopBar.StopAnimation("Offset.X"); _agPopBar.IsVisible = false; return; }
        if (_agPopBar.IsVisible) return;
        var g = AgPopGeo();
        float k = _ccK, x0 = (float)g.Line.X * k, x1 = (float)(g.Line.X + g.Line.Width - 96) * k;
        _agPopBar.Size = new Vector2(96 * k, 2 * k);
        _agPopBar.Offset = new Vector3(x0, (float)g.Line.Y * k, 0);
        _agPopBar.IsVisible = true;
        if (Reduced) return;
        var a = G.C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, x0);
        a.InsertKeyFrame(0.5f, x1, G.C.CreateCubicBezierEasingFunction(new Vector2(0.65f, 0), new Vector2(0.35f, 1)));
        a.InsertKeyFrame(1, x0, G.C.CreateCubicBezierEasingFunction(new Vector2(0.65f, 0), new Vector2(0.35f, 1)));
        a.Duration = TimeSpan.FromMilliseconds(1400);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        _agPopBar.StartAnimation("Offset.X", a);
    }

    private static void AgPopSearchIcon(CanvasDrawingSession ds, float x, float cy, Color c)
    {
        ds.DrawCircle(x + 7, cy - 1.5f, 6, c, 1.6f);
        using var stroke = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        ds.DrawLine(x + 11.5f, cy + 3, x + 15.5f, cy + 7, c, 1.6f, stroke);
    }
}
