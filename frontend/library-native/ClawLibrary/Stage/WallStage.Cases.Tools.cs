// 游戏库里留下的入口（11-14 jinyu）：搜索、Steam、截图，和「开始游戏 / 游戏设置」放在同一排按钮里（收藏的位置让给 Steam）。
// 添加游戏、设置在控制中心左下功能坞，头像在控制中心右下；海报墙的竖条同样只留搜索、Steam、截图。
// 搜索：框从按钮排里那颗搜索圆钮长出来，弹到顶栏左边（页签让开），这样屏幕键盘挡不住；收起时原路缩回圆钮。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const float FieldW = 640, FieldH = 60, FieldIconBox = 72;

    private CanvasSvgDocument? _csSteamSvg;
    private bool _csSearchOpen;
    private TextBox? _csSearchBox;
    /// <summary>搜索结果页签（tab 4）的搜索词；null = 没有这个页签。</summary>
    private string? _csQuery;

    private SpriteVisual? _csFieldIcon;
    private ShapeVisual? _csFieldShape;
    private CompositionRoundedRectangleGeometry? _csFieldGeo;
    private CompositionColorBrush? _csFieldStroke;
    /// <summary>搜索框在外面（展开中、展开着或正在收回）：按钮排里的搜索圆钮交给框自己画。</summary>
    private bool _csFieldOut;
    private int _csFieldVer;
    /// <summary>截图页是从游戏库按钮直接进的：B 退出截图页时一起收起控制中心，回到游戏库。</summary>
    private bool _shFromLibrary;

    // ---------------- 入口动作（卡盒按钮排、海报墙竖条共用） ----------------

    private void OpenSteamShortcut()
    {
        string uri = _prefs.SteamShortcut == SteamShortcutTarget.BigPicture ? "steam://open/bigpicture" : "steam://open/games";
        try { PlatformClients.Open(uri); Log.Info($"library Steam shortcut: {uri}"); }
        catch (Exception ex) { Log.Warn($"library Steam shortcut: {ex.Message}"); Toast("无法打开 Steam，请确认已安装 Steam"); }
    }

    private void OpenShotsFromLibrary()
    {
        if (_ccOpen) { OpenShots(); return; }
        OpenControlCenter(quiet: true);
        OpenShots();
        _shFromLibrary = true;
    }

    // ---------------- 搜索框 ----------------
    // 起点是按钮排里的搜索圆钮（72 × 72，整圆），终点是顶栏左边 640 × 60 的胶囊；位置、大小、圆角都用合成层弹簧，
    // 展开途中按 B / 收回途中再按 A 都从此刻的值和速度接着走、原路折返。页签向左让 24 px 并淡出；输入框等框到位才放上去，
    // 收回时第一帧就拿掉。
    private static readonly Spring FieldSpring = new(320, 28, 1);   // ζ ≈ 0.78，约 380 ms 落定，带一点点回弹
    private const double FieldTextDelay = 300;
    private const double FieldFadeMs = 120;

    /// <summary>搜索圆钮（框的收起状态），卡盒层坐标。按钮排不在（分类页 / 没有游戏）时用顶栏左边的一个点。</summary>
    private (float X, float Y, float W, float H) CsFieldClosed()
    {
        if (_cs?.Page is { Tab: 0 } p && p.Sel < p.Items.Count)
        {
            bool running = RunningGame(p.Items[p.Sel].Id) != null;
            foreach (var r in p.ActRects)
                if (r.Act > 0 && CsActionAt(r.Act, running) == CsAction.Search)
                {
                    var o = p.Content.Offset;
                    return (r.X + o.X, r.Y + o.Y, r.W, r.H);
                }
        }
        var f = CsFieldOpen();
        return (f.X, f.Y, FieldH, FieldH);
    }

    /// <summary>展开后的框：顶栏左边，页签让出来的位置，和页签同一条中线。</summary>
    private (float X, float Y, float W, float H) CsFieldOpen() => (CsEdge, HdTop + HdMid - FieldH / 2, FieldW, FieldH);

    private void CsEnsureField()
    {
        if (_cs == null || _csFieldShape != null) return;
        _csFieldGeo = G.C.CreateRoundedRectangleGeometry();
        var shape = G.C.CreateSpriteShape(_csFieldGeo);
        // 和按钮排的圆钮同一个底（14 % 白），收起到圆钮时看不出换手；描边只在展开时淡进来
        shape.FillBrush = G.C.CreateColorBrush(Gfx.WithAlpha(CcText, 0.14));
        _csFieldStroke = G.C.CreateColorBrush(Gfx.WithAlpha(CcText, 0));
        shape.StrokeBrush = _csFieldStroke;
        shape.StrokeThickness = 2;
        _csFieldShape = G.C.CreateShapeVisual();
        _csFieldShape.Size = new Vector2(CsW, CsH);
        _csFieldShape.Shapes.Add(shape);
        _csFieldShape.Opacity = 0;
        _cs.Root.Children.InsertAtTop(_csFieldShape);
        _csFieldIcon = CsSprite(FieldIconBox, FieldIconBox, ds => LineIcons.Draw(ds, "", FieldIconBox / 2, FieldIconBox / 2, 27, Gfx.Rgba(242, 242, 242)));
        _csFieldIcon.Opacity = 0;
        _cs.Root.Children.InsertAtTop(_csFieldIcon);
    }

    private SpringVector2NaturalMotionAnimation CsSpringV2(Vector2 to)
    {
        var a = G.C.CreateSpringVector2Animation();
        a.FinalValue = to;
        a.DampingRatio = FieldSpring.Zeta;
        a.Period = FieldSpring.Period;
        return a;
    }

    /// <summary>框和放大镜一起走到 open / closed。</summary>
    private void CsMorphField(bool open)
    {
        if (_csFieldGeo == null || _csFieldShape == null || _csFieldIcon == null) return;
        var r = open ? CsFieldOpen() : CsFieldClosed();
        var off = new Vector2(r.X, r.Y);
        var size = new Vector2(r.W, r.H);
        var radius = new Vector2(r.H / 2);   // 两头都是整圆
        var iconOff = new Vector3(open ? r.X + 2 : r.X + r.W / 2 - FieldIconBox / 2, r.Y + r.H / 2 - FieldIconBox / 2, 0);
        if (Reduced)
        {
            _csFieldGeo.StopAnimation("Offset"); _csFieldGeo.StopAnimation("Size"); _csFieldGeo.StopAnimation("CornerRadius");
            _csFieldGeo.Offset = off; _csFieldGeo.Size = size; _csFieldGeo.CornerRadius = radius;
            _csFieldIcon.StopAnimation("Offset"); _csFieldIcon.Offset = iconOff;
        }
        else
        {
            _csFieldGeo.StartAnimation("Offset", CsSpringV2(off));
            _csFieldGeo.StartAnimation("Size", CsSpringV2(size));
            _csFieldGeo.StartAnimation("CornerRadius", CsSpringV2(radius));
            _csFieldIcon.StartAnimation("Offset", G.SpringV3(iconOff, FieldSpring));
        }
        CsFieldStrokeTo(open ? 0.36f : 0, open ? 220 : 160);
    }

    private void CsFieldStrokeTo(float alpha, double ms)
    {
        if (_csFieldStroke == null) return;
        var a = G.C.CreateColorKeyFrameAnimation();
        a.InsertKeyFrame(1, Gfx.WithAlpha(CcText, alpha), G.Ease(CsQuart));
        a.Duration = Gfx.Ms(Math.Max(1, CsMs(ms)));
        _csFieldStroke.StartAnimation("Color", a);
    }

    /// <summary>页签让位：展开时向左 24 px 并淡出，收回时回原位（透明度回到 1 / 0.6）。</summary>
    private void CsTabsMakeWay(bool away)
    {
        if (_cs == null) return;
        foreach (var t in _cs.Tabs)
        {
            var to = new Vector3(t.X - (away ? 24 : 0), HdTop, 0);
            if (Reduced) { t.V.StopAnimation("Offset"); t.V.Offset = to; }
            else t.V.StartAnimation("Offset", G.SpringV3(to, FieldSpring));
            t.V.StartAnimation("Opacity", CsS(away ? 0 : t.Tab == _cs.Tab ? 1 : 0.6f, CsMs(away ? 160 : 260), CsQuart, away ? 0 : 80));
        }
        _cs.Underline.StartAnimation("Opacity", CsS(away ? 0 : 1, CsMs(away ? 160 : 260), CsQuart, away ? 0 : 80));
    }

    /// <summary>打字时描边亮一点。</summary>
    private void CsPaintField()
    {
        if (!_csSearchOpen) return;
        bool typing = _csSearchBox?.FocusState is FocusState.Keyboard or FocusState.Programmatic or FocusState.Pointer;
        CsFieldStrokeTo(typing ? 0.6f : 0.36f, 160);
    }

    private void CsRepaintActs()
    {
        if (_cs?.Page is { Tab: 0 } p && p.ActsV != null)
        {
            var g = p.Sel < p.Items.Count ? p.Items[p.Sel] : null;
            CsPaintActs(p, g, CsActsWidth(g));
        }
    }

    private void CsOpenSearch()
    {
        if (_cs == null || _csSearchOpen) return;
        CsEnsureSearchBox();
        CsEnsureField();
        int ver = ++_csFieldVer;
        bool wasOut = _csFieldOut;
        _csSearchOpen = true;
        _csFieldOut = true;
        if (!wasOut)
        {
            // 从收起状态出发：框先叠在搜索圆钮上（不动画），再弹出去
            var c = CsFieldClosed();
            _csFieldGeo!.StopAnimation("Offset"); _csFieldGeo.StopAnimation("Size"); _csFieldGeo.StopAnimation("CornerRadius");
            _csFieldGeo.Offset = new Vector2(c.X, c.Y); _csFieldGeo.Size = new Vector2(c.W, c.H); _csFieldGeo.CornerRadius = new Vector2(c.H / 2);
            _csFieldIcon!.StopAnimation("Offset"); _csFieldIcon.Offset = new Vector3(c.X + c.W / 2 - FieldIconBox / 2, c.Y + c.H / 2 - FieldIconBox / 2, 0);
            _csFieldShape!.StopAnimation("Opacity"); _csFieldShape.Opacity = 1;
        }
        else _csFieldShape!.StartAnimation("Opacity", CsS(1, CsMs(FieldFadeMs), CsQuart));
        _csFieldIcon!.StopAnimation("Opacity"); _csFieldIcon.Opacity = 1;
        CsRepaintActs();   // 圆钮和焦点环交给框（同一帧）
        CsMorphField(open: true);
        CsTabsMakeWay(away: true);
        _csSearchBox!.Text = "";
        Cue(Sfx.ModalShow);
        After(Reduced ? 1 : FieldTextDelay, () =>
        {
            if (ver != _csFieldVer || !_csSearchOpen) return;
            CsPlaceSearchBox();
            CsFocusSearchBox();
        });
    }

    /// <summary>收起。back = true：原路缩回搜索圆钮；false（提交后换到结果页）：原地淡掉。</summary>
    private void CsCloseSearch(bool back = true)
    {
        if (!_csSearchOpen || _cs == null) return;
        _csSearchOpen = false;
        int ver = ++_csFieldVer;
        if (_csSearchBox != null) _csSearchBox.Visibility = Visibility.Collapsed;   // 第一帧就拿掉文字
        _rootEl.Focus(FocusState.Programmatic);
        CsTabsMakeWay(away: false);
        double settle;
        if (back)
        {
            CsMorphField(open: false);
            settle = Reduced ? 1 : 300;
            _csFieldShape?.StartAnimation("Opacity", CsS(0, CsMs(FieldFadeMs), CsQuart, settle));
        }
        else
        {
            CsFieldStrokeTo(0, 160);
            settle = Reduced ? 1 : 200;
            _csFieldShape?.StartAnimation("Opacity", CsS(0, CsMs(200), CsQuart));
            _csFieldIcon?.StartAnimation("Opacity", CsS(0, CsMs(200), CsQuart));
        }
        After(settle + (Reduced ? 0 : FieldFadeMs * 0.5), () =>
        {
            if (ver != _csFieldVer || _csSearchOpen) return;
            _csFieldOut = false;
            if (_csFieldIcon != null) { _csFieldIcon.StopAnimation("Opacity"); _csFieldIcon.Opacity = 0; }
            CsRepaintActs();   // 圆钮接回来（位置、大小、底色和框落定时一样）
        });
    }

    /// <summary>搜索框开着时点屏幕：点在框上 = 打字，点在别处 = 收起。返回 true = 这次点按已处理。</summary>
    private bool CsSearchFieldClick(float x, float y)
    {
        if (!_csSearchOpen) return false;
        var f = CsFieldOpen();
        if (x >= f.X && x <= f.X + f.W && y >= f.Y - 16 && y <= f.Y + f.H + 16) { CsFocusSearchBox(); return true; }
        CsCloseSearch();
        return true;
    }

    private void CsEnsureSearchBox()
    {
        if (_csSearchBox != null) return;
        _csSearchBox = new TextBox
        {
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            RequestedTheme = ElementTheme.Dark,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            Visibility = Visibility.Collapsed,
        };
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var ink = new SolidColorBrush(CcText);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            _csSearchBox.Resources[key] = clear;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            _csSearchBox.Resources[key] = ink;
        _csSearchBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Enter: e.Handled = true; CsSubmitSearch(); break;
                case Windows.System.VirtualKey.Escape: e.Handled = true; CsCloseSearch(); break;
            }
        };
        void HideClear()
        {
            if (FindChild(_csSearchBox!, "DeleteButton") is FrameworkElement b && b.MaxWidth != 0) { b.MaxWidth = 0; b.Opacity = 0; b.IsHitTestVisible = false; }
        }
        _csSearchBox.Loaded += (_, _) => HideClear();
        _csSearchBox.TextChanged += (_, _) => HideClear();
        _csSearchBox.GotFocus += (_, _) => { HideClear(); CsPaintField(); };
        _csSearchBox.LostFocus += (_, _) => CsPaintField();
        HookTouchKeyboard(_csSearchBox);
        _rootEl.Overlay.Children.Add(_csSearchBox);
    }

    private void CsPlaceSearchBox()
    {
        if (_cs == null || _csSearchBox == null) return;
        var o = _cs.Root.Offset;
        float k = _cs.K;
        var f = CsFieldOpen();
        float left = f.X + FieldIconBox + 2, width = f.X + f.W - left - 28;
        _csSearchBox.FontSize = Math.Max(12, 26 * k);
        _csSearchBox.Width = width * k;
        _csSearchBox.Height = FieldH * k * 0.8;
        Canvas.SetLeft(_csSearchBox, o.X + left * k);
        Canvas.SetTop(_csSearchBox, o.Y + (f.Y + FieldH * 0.1f + 4) * k);
        _csSearchBox.Visibility = Visibility.Visible;
    }

    private void CsFocusSearchBox()
    {
        if (_csSearchBox == null || !_csSearchOpen) return;
        FocusTextBox(_csSearchBox, () => _csSearchOpen);
    }

    private void CsSubmitSearch()
    {
        if (!_csSearchOpen || _csSearchBox == null || _cs == null) return;
        var q = _csSearchBox.Text.Trim();
        if (q.Length == 0) { CsFocusSearchBox(); return; }
        var items = SearchItems(q).Where(g => g.Kind == ItemKind.Game).ToList();
        Log.Info($"cases search \"{q}\": {items.Count}");
        if (items.Count == 0) { Toast($"没有找到「{q}」"); CsFocusSearchBox(); return; }
        _csQuery = q;
        CsCloseSearch(back: false);
        Cue(Sfx.DetailIn);
        _csSpotForce = items[0].Id;
        CsShowTab(4, 1, entry: true);
    }
}
