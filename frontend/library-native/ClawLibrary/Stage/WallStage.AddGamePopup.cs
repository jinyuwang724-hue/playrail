// 添加游戏 › 匹配游戏弹窗：选完 exe 进添加页时弹出（之后「匹配」那一行 / Y 再打开）。
// 样子照截图页的筛选面板：整页压暗，背后的页面糊一层；中间一块强调色压暗的圆角面板，顶上一行小字，下面是搜索框（真输入框，
// 默认 exe 名，打字停半秒自动重搜，搜索中下划线上有一道光来回走），再下面是下拉列表：一行一款，左名称、右年份，
// 焦点那行近白底深色字。弹出时面板从 0.96 弹到 1、压暗层淡入。A / 回车用焦点那款，B 关掉（不配图也行）。
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
    private const float AgPopW = 560, AgPopX = (1280 - AgPopW) / 2, AgPopY = 150, AgPopHead = 52, AgPopInput = 56, AgPopRow = 48, AgPopRows = 8;

    private bool _agPopOpen;
    /// <summary>焦点：-1 = 输入框，0.. = 列表里那一款。</summary>
    private int _agPopIndex = -1, _agPopFirst, _agPopVersion, _agTypeVersion;
    private SpriteVisual? _agPopScrim, _agPopPanel, _agPopBar;
    private TextBox? _agPopBox;

    private float AgPopHeight()
    {
        int rows = (int)Math.Min(AgPopRows, _agDraft?.Matches.Count ?? 0);
        return AgPopHead + AgPopInput + rows * AgPopRow + (rows > 0 ? 12 : 0);
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
            _agPopPanel.Scale = new Vector3(0.96f, 0.96f, 1);
            _agPopPanel.StartAnimation("Opacity", ToScalar(1, 180, EaseOutCss));
            _agPopPanel.StartAnimation("Scale", G.SpringV3(Vector3.One, Spring.Button));
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
            _agPopPanel.StartAnimation("Scale", G.SpringV3(new Vector3(0.97f, 0.97f, 1), Spring.Button));
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
        float h = AgPopHeight();
        if (p.X < AgPopX || p.X > AgPopX + AgPopW || p.Y < AgPopY || p.Y > AgPopY + h) { AgClosePop(); return; }
        float listTop = AgPopY + AgPopHead + AgPopInput;
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
        var r = new Rect(AgPopX + 52, AgPopY + AgPopHead, AgPopW - 76, 44);
        if (_agPopBox.Text != _agDraft?.Query) _agPopBox.Text = _agDraft?.Query ?? "";
        _agPopBox.FontSize = 17 * k;
        _agPopBox.Width = r.Width * k;
        _agPopBox.Height = r.Height * k;
        _agPopBox.Padding = new Thickness(0, Math.Max(0, (r.Height - 17 * 1.33) / 2 * k - 1), 0, 0);
        Canvas.SetLeft(_agPopBox, r.X * k);
        Canvas.SetTop(_agPopBox, r.Y * k);
        _agPopBox.Visibility = Visibility.Visible;
    }

    // ---------------- 绘制 ----------------

    private void AgPaintPop()
    {
        var d = _agDraft;
        if (!_agPopOpen || d == null || _agPopPanel == null) return;
        float k = _ccK, raster = k * Chrome.Dpi, h = AgPopHeight();
        int n = d.Matches.Count, rows = (int)Math.Min(AgPopRows, n);
        if (_agPopIndex >= 0) _agPopFirst = Math.Clamp(_agPopFirst, Math.Max(0, _agPopIndex - rows + 1), Math.Min(_agPopIndex, Math.Max(0, n - rows)));
        var ui = TextKit.Ui(400);
        var dark = Gfx.Rgba(9, 9, 11);
        CcSet(_agPopPanel, CcDraw(AgPopW, h, raster, ds =>
        {
            // 底色：强调色压暗（和控制中心的封面取色一致），不是纯黑
            ds.FillRoundedRectangle(0, 0, AgPopW, h, 18, 18, Gfx.WithAlpha(Gfx.Mix(Theme.Bg, 0.78, PerfAccent), 0.96));
            PText(ds, "匹配游戏", TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.42), 24, 30, 300);
            // 右上：几款；连不上 / 密钥无效时换成这两个字
            string count = d.Searching ? "" : d.Status is "连不上" or "密钥无效" ? d.Status : n.ToString();
            if (count.Length > 0)
            {
                var cf = char.IsDigit(count[0]) ? AgMono : TextKit.Ui(500);
                float cw = PWidth(ds, count, cf, 13);
                PText(ds, count, cf, 13, Gfx.WithAlpha(CcText, 0.42), AgPopW - 24 - cw, 31, cw + 4);
            }
            // 输入框：放大镜 + （XAML 输入框）+ 下划线；焦点在输入框上时线是白的
            bool inputFocus = _agPopIndex < 0;
            float iy = AgPopHead + 22;
            AgPopSearchIcon(ds, 24, iy, inputFocus ? CcText : Gfx.WithAlpha(CcText, 0.6f));
            ds.FillRectangle(24, AgPopHead + 44, AgPopW - 48, inputFocus ? 2 : 1, inputFocus ? Gfx.WithAlpha(CcText, 0.9f) : Gfx.WithAlpha(CcText, 0.18f));
            // 下拉列表：左名称、右年份
            for (int r = 0; r < rows; r++)
            {
                int i = _agPopFirst + r;
                var m = d.Matches[i];
                float ry = AgPopHead + AgPopInput + r * AgPopRow, cy = ry + AgPopRow / 2;
                bool focus = i == _agPopIndex;
                if (focus) ds.FillRoundedRectangle(10, ry + 2, AgPopW - 20, AgPopRow - 4, 12, 12, Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96));
                var ink = focus ? dark : Gfx.WithAlpha(CcText, 0.88);
                int year = Int(m["year"]);
                string ys = year > 0 ? year.ToString() : "";
                float yw = ys.Length > 0 ? PWidth(ds, ys, AgMono, 13) : 0;
                if (i == d.Match) ds.FillCircle(30, cy, 3, ink);
                PText(ds, Str(m["name"]) ?? "", ui, 17, ink, 42, cy, AgPopW - 42 - 24 - yw - 16);
                if (ys.Length > 0) PText(ds, ys, AgMono, 13, focus ? Gfx.Rgba(9, 9, 11, 0.55) : Gfx.WithAlpha(CcText, 0.45), AgPopW - 24 - yw, cy + 1, yw + 4);
            }
        }));
        _agPopPanel.Size = new Vector2(AgPopW * k, h * k);
        _agPopPanel.Offset = new Vector3(AgPopX * k, AgPopY * k, 0);
        _agPopPanel.CenterPoint = new Vector3(AgPopW * k / 2, h * k / 2, 0);
        AgPopSearching(d.Searching);
    }

    /// <summary>搜索中：输入框下划线上一道白光来回走。</summary>
    private void AgPopSearching(bool on)
    {
        if (_agPopBar == null) return;
        if (!on || !_agPopOpen) { _agPopBar.StopAnimation("Offset.X"); _agPopBar.IsVisible = false; return; }
        if (_agPopBar.IsVisible) return;
        float k = _ccK, x0 = (AgPopX + 24) * k, x1 = (AgPopX + AgPopW - 24 - 96) * k;
        _agPopBar.Size = new Vector2(96 * k, 2 * k);
        _agPopBar.Offset = new Vector3(x0, (AgPopY + AgPopHead + 44) * k, 0);
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
