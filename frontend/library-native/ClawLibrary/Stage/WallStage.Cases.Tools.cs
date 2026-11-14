// 卡盒版顶栏右侧的入口（时间电量左边）：搜索、添加游戏、Steam、设置、头像 + 名字。
// 海报墙的竖条上原来就有这几项；卡盒版的页面不显示竖条，所以在顶栏里再放一份，动作和竖条完全一样。
// 手柄：页面最上面一行按 ↑ 进顶栏，← / → 在入口间走，↓ 或 B 回到原来的焦点，A 执行。触屏 / 鼠标直接点。
// 搜索：顶栏左边的页签让开，原地展开输入框（XAML TextBox，支持输入法），回车提交，结果作为一个页签铺成卡盒网格，B 回首页。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum CsTool { Search, AddGame, Steam, Settings, Account }
    private static readonly CsTool[] CsToolOrder = { CsTool.Search, CsTool.AddGame, CsTool.Steam, CsTool.Settings, CsTool.Account };

    // 尺寸按 1920 坐标：竖条工具格 40 → 54（与顶栏行高一样），图标 18 → 26，头像 44 → 40（和状态栏图标同一视觉高度）
    private const float TlBox = 54, TlGap = 10, TlIcon = 26, TlAvatar = 40, TlPad = 8, FieldW = 640, FieldH = 60;

    private SpriteVisual? _csToolsV, _csFieldV;
    private float _csToolsX;
    private readonly List<(float X, float Y, float W, float H, CsTool T)> _csToolRects = new();
    /// <summary>手柄焦点在顶栏入口上（CsToolOrder 的序号），-1 = 不在顶栏。</summary>
    private int _csHd = -1;
    private CanvasSvgDocument? _csSteamSvg;
    private bool _csSearchOpen;
    private TextBox? _csSearchBox;
    /// <summary>搜索结果页签（tab 4）的搜索词；null = 没有这个页签。</summary>
    private string? _csQuery;

    private bool CsHeaderFocused => _csHd >= 0;

    // ---------------- 画 ----------------

    private void CsPaintTools()
    {
        if (_cs == null) return;
        if (_csToolsV == null)
        {
            _csToolsV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _cs.Header.Children.InsertAtTop(_csToolsV);
        }
        string user = _railModel.User ?? "";
        float nameW;
        using (var l = TextKit.Layout(G.Device, user, TextKit.Ui(400), HdCount, 400, CanvasHorizontalAlignment.Left)) nameW = Math.Min(260, (float)l.LayoutBounds.Width);
        float accW = TlAvatar + 14 + nameW + 16;   // 头像、名字，右边留一点给焦点框
        float toolsW = 4 * TlBox + 3 * TlGap;
        float w = toolsW + 32 + accW;
        float right = _cs.Status.Offset.X - HdGap + 8;
        float x0 = right - w;
        _csToolsX = x0;
        int focus = _csSearchOpen ? -1 : _csHd;   // 搜索框开着时焦点在框里，入口上不再画
        var avatar = _railModel.Avatar;
        float mid = HdMid;
        _csToolRects.Clear();
        for (int i = 0; i < 4; i++) _csToolRects.Add((x0 + i * (TlBox + TlGap), HdTop + mid - TlBox / 2, TlBox, TlBox, CsToolOrder[i]));
        _csToolRects.Add((x0 + toolsW + 32 - 8, HdTop + mid - TlBox / 2, accW, TlBox, CsTool.Account));
        CsRepaint(_csToolsV, w + TlPad * 2, HdLine + TlPad * 2 + 20, ds =>
        {
            var ink = Gfx.WithAlpha(CcText, 0.9);
            float cy = TlPad + mid;
            for (int i = 0; i < 4; i++)
            {
                float bx = TlPad + i * (TlBox + TlGap), by = cy - TlBox / 2;
                bool on = focus == i;
                // 竖条工具格的做法：静止无底无框，焦点时才有 16 % 底 + 26 % 描边，外面 2 px 焦点环
                if (on)
                {
                    ds.FillRoundedRectangle(bx, by, TlBox, TlBox, 16, 16, Gfx.WithAlpha(CcText, 0.16));
                    ds.DrawRoundedRectangle(bx + 0.5f, by + 0.5f, TlBox - 1, TlBox - 1, 15.5f, 15.5f, Gfx.WithAlpha(CcText, 0.26), 1);
                    ds.DrawRoundedRectangle(bx - 3, by - 3, TlBox + 6, TlBox + 6, 18, 18, CcText, 2);
                }
                var c = on ? CcText : ink;
                var tool = CsToolOrder[i];
                if (tool == CsTool.Steam)
                {
                    _csSteamSvg ??= CanvasSvgDocument.LoadFromXml(G.Device, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Steam.svg")));
                    _csSteamSvg.Root.SetColorAttribute("fill", c);
                    ds.DrawSvg(_csSteamSvg, new Windows.Foundation.Size(TlIcon + 2, TlIcon + 2), bx + TlBox / 2 - (TlIcon + 2) / 2, cy - (TlIcon + 2) / 2);
                }
                else
                {
                    string glyph = tool switch { CsTool.Search => "", CsTool.AddGame => "", _ => "" };
                    LineIcons.Draw(ds, glyph, bx + TlBox / 2, cy, TlIcon, c);
                }
            }
            // 头像 + 名字
            float ax = TlPad + toolsW + 32;
            using (var geo = CanvasGeometry.CreateEllipse(ds, ax + TlAvatar / 2, cy, TlAvatar / 2, TlAvatar / 2))
            {
                if (avatar != null)
                    using (ds.CreateLayer(1, geo))
                        ds.DrawImage(avatar, new Windows.Foundation.Rect(ax, cy - TlAvatar / 2, TlAvatar, TlAvatar), avatar.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                else
                {
                    ds.FillGeometry(geo, Gfx.WithAlpha(CcText, 0.14));
                    using var ch = TextKit.Layout(ds, user.Length > 0 ? user[..1] : "?", TextKit.Ui(600), 18, TlAvatar, CanvasHorizontalAlignment.Center);
                    ds.DrawTextLayout(ch, ax, cy - (float)ch.LayoutBounds.Height / 2 - (float)ch.LayoutBounds.Y, CcText);
                }
                ds.DrawGeometry(geo, Gfx.WithAlpha(CcText, 0.18), 1);
            }
            if (user.Length > 0)
            {
                using var nl = TextKit.Layout(ds, user, TextKit.Ui(400), HdCount, nameW, CanvasHorizontalAlignment.Left);
                nl.WordWrapping = CanvasWordWrapping.NoWrap;
                nl.TrimmingGranularity = CanvasTextTrimmingGranularity.Character;
                nl.TrimmingSign = CanvasTrimmingSign.Ellipsis;
                var ib = nl.DrawBounds;
                ds.DrawTextLayout(nl, ax + TlAvatar + 14, cy - (float)(ib.Y + ib.Height / 2), focus == 4 ? CcText : ink);
            }
            if (focus == 4)
                ds.DrawRoundedRectangle(ax - 8, cy - TlBox / 2 - 3, accW + 2, TlBox + 6, 18, 18, CcText, 2);
        });
        _csToolsV.Offset = new Vector3(x0 - TlPad, HdTop - TlPad, 0);
    }

    // ---------------- 手柄 ----------------

    /// <summary>页面最上面一行按 ↑：进顶栏，落在搜索上。返回 true = 这次按键已经处理。</summary>
    private bool CsHeaderMove(CsPage p, Direction dir)
    {
        if (_csSearchOpen) { Cue(Sfx.None); return true; }
        if (CsHeaderFocused)
        {
            if (dir is Direction.Left or Direction.Right)
            {
                int j = _csHd + (dir == Direction.Left ? -1 : 1);
                if (j < 0 || j >= CsToolOrder.Length) { Cue(Sfx.Bump, pan: dir == Direction.Left ? -0.6f : 0.6f); return true; }
                _csHd = j;
                CuePan(dir == Direction.Left ? -0.3f : 0.3f);
                CsPaintTools();
                return true;
            }
            if (dir == Direction.Down) { CsLeaveHeader(p); return true; }
            Cue(Sfx.Bump);
            return true;
        }
        if (dir != Direction.Up) return false;
        bool top = p.Tab == 0
            ? (p.Style == HomeStyle.Stack ? !p.Acts : p.Acts || p.Sel >= p.Items.Count)
            : p.InRecent || (p.Recent.Count == 0 && p.Ai < GridCols);
        if (!top) return false;
        // 原来的焦点让开（一屏一个焦点）
        if (p.Tab == 0)
        {
            if (!p.Acts) CsHomeRing(p, false);
            else { var g = p.Sel < p.Items.Count ? p.Items[p.Sel] : null; _csHd = 0; CsPaintActs(p, g, CsActsWidth(g)); }
        }
        else if (p.InRecent) { if (p.Carts.TryGetValue(p.Ai, out var cart)) CsFocusCart(cart, false); }
        else if (p.Grid.TryGetValue(p.Ai, out var cell)) CsGridFocus(cell.Case, false, false);
        _csHd = 0;
        CsPaintTools();
        return true;
    }

    /// <summary>↓ / B：焦点回到进顶栏之前的地方。</summary>
    private void CsLeaveHeader(CsPage p, bool quiet = false)
    {
        if (!CsHeaderFocused) return;
        _csHd = -1;
        CsPaintTools();
        if (quiet) return;
        if (p.Tab == 0)
        {
            if (!p.Acts) CsHomeRing(p, true);
            else { var g = p.Sel < p.Items.Count ? p.Items[p.Sel] : null; CsPaintActs(p, g, CsActsWidth(g)); }
        }
        else CsCatFocus(p, instant: false);
    }

    /// <summary>首页卡盒上的焦点环（展台画在展台上的大卡盒，其余画在选中的卡盒）。</summary>
    private void CsHomeRing(CsPage p, bool on)
    {
        var anim = CsS(on ? 1 : 0, CsMs(on ? 300 : 250), CsQuart);
        if (p.Style == HomeStyle.Stand) { p.StandCase?.Ring.StartAnimation("Opacity", anim); return; }
        if (p.Cases.TryGetValue(p.Sel, out var c)) c.Ring.StartAnimation("Opacity", anim);
    }

    private void CsToolActivate(CsTool t)
    {
        switch (t)
        {
            case CsTool.Search: CsOpenSearch(); break;
            case CsTool.AddGame: _ = AddGameFromLibrary(); break;
            case CsTool.Steam:
                string uri = _prefs.SteamShortcut == SteamShortcutTarget.BigPicture ? "steam://open/bigpicture" : "steam://open/games";
                try { PlatformClients.Open(uri); Log.Info($"cases Steam shortcut: {uri}"); }
                catch (Exception ex) { Log.Warn($"cases Steam shortcut: {ex.Message}"); Toast("无法打开 Steam，请确认已安装 Steam"); }
                break;
            case CsTool.Settings:
                _backgroundSettingsOpen = false;
                OpenSettingsFromLibrary();
                break;
            case CsTool.Account: SystemInfo.Open("ms-settings:yourinfo"); break;
        }
    }

    /// <summary>点在顶栏右边的入口 / 搜索框上。点中了返回 true。</summary>
    private bool CsToolsClick(float x, float y)
    {
        if (_csSearchOpen)
        {
            if (x >= CsEdge && x <= CsEdge + FieldW && y >= HdTop - 16 && y <= HdTop + HdLine + 16) { CsFocusSearchBox(); return true; }
            CsCloseSearch();
            return x >= _csToolsX;   // 点在入口区以外：只收起搜索框
        }
        foreach (var r in _csToolRects)
            if (x >= r.X - 4 && x <= r.X + r.W + 4 && y >= r.Y - 10 && y <= r.Y + r.H + 10)
            {
                if (_cs?.Page is { } p) CsLeaveHeader(p, quiet: false);
                WithCue(Sfx.Activate, () => CsToolActivate(r.T));
                return true;
            }
        return false;
    }

    // ---------------- 搜索 ----------------

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

    private void CsPaintField()
    {
        if (_cs == null) return;
        if (_csFieldV == null)
        {
            _csFieldV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _csFieldV.Opacity = 0;
            _cs.Header.Children.InsertAtTop(_csFieldV);
        }
        bool typing = _csSearchBox?.FocusState is FocusState.Keyboard or FocusState.Programmatic or FocusState.Pointer;
        CsRepaint(_csFieldV, FieldW, FieldH, ds =>
        {
            float r = FieldH / 2;
            ds.FillRoundedRectangle(0, 0, FieldW, FieldH, r, r, Gfx.WithAlpha(CcText, 0.08));
            ds.DrawRoundedRectangle(1, 1, FieldW - 2, FieldH - 2, r - 1, r - 1, Gfx.WithAlpha(CcText, typing ? 0.6 : 0.26), 2);
            LineIcons.Draw(ds, "", 22 + 13, FieldH / 2, TlIcon, Gfx.WithAlpha(CcText, 0.7));
        });
        _csFieldV.Offset = new Vector3(CsEdge, HdTop + HdMid - FieldH / 2, 0);
    }

    private void CsOpenSearch()
    {
        if (_cs == null || _csSearchOpen) return;
        if (_cs.Page is { } p) CsLeaveHeader(p, quiet: true);
        _csHd = 0;   // 搜索打开时焦点算在放大镜上（关掉后还停在这）
        CsPaintTools();
        CsEnsureSearchBox();
        _csSearchOpen = true;
        CsPaintTools();
        _csSearchBox!.Text = "";
        foreach (var t in _cs.Tabs) t.V.StartAnimation("Opacity", CsS(0, CsMs(160), CsQuart));
        _cs.Underline.StartAnimation("Opacity", CsS(0, CsMs(160), CsQuart));
        CsPaintField();
        _csFieldV!.StartAnimation("Opacity", CsS(1, CsMs(220), CsQuart));
        CsPlaceSearchBox();
        Cue(Sfx.ModalShow);
        CsFocusSearchBox();
    }

    private void CsPlaceSearchBox()
    {
        if (_cs == null || _csSearchBox == null) return;
        var o = _cs.Root.Offset;
        float k = _cs.K;
        float left = CsEdge + 22 + 26 + 18, width = FieldW - (left - CsEdge) - 28;
        _csSearchBox.FontSize = Math.Max(12, 26 * k);
        _csSearchBox.Width = width * k;
        _csSearchBox.Height = FieldH * k * 0.8;
        Canvas.SetLeft(_csSearchBox, o.X + left * k);
        Canvas.SetTop(_csSearchBox, o.Y + (HdTop + HdMid - FieldH * 0.4f + 4) * k);
        _csSearchBox.Visibility = Visibility.Visible;
    }

    private void CsFocusSearchBox()
    {
        if (_csSearchBox == null || !_csSearchOpen) return;
        FocusTextBox(_csSearchBox, () => _csSearchOpen);
    }

    private void CsCloseSearch()
    {
        if (!_csSearchOpen || _cs == null) return;
        _csSearchOpen = false;
        if (_csSearchBox != null) _csSearchBox.Visibility = Visibility.Collapsed;
        _rootEl.Focus(FocusState.Programmatic);
        _csFieldV?.StartAnimation("Opacity", CsS(0, CsMs(160), CsQuart));
        CsPlaceUnderline(animate: false);   // 页签透明度也在这里回到 1 / 0.6
        _cs.Underline.Opacity = 0;
        _cs.Underline.StartAnimation("Opacity", CsS(1, CsMs(220), CsQuart));
        CsPaintTools();
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
        _csHd = -1;
        CsCloseSearch();
        Cue(Sfx.DetailIn);
        _csSpotForce = items[0].Id;
        CsShowTab(4, 1, entry: true);
    }
}
