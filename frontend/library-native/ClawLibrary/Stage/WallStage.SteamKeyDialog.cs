// Steam 云端同步：填 Web API Key 的小窗（11-26 jinyu：做成「切换游戏」那种弹窗）。
// 白底圆角卡片（同 RqPaint）：标题栏「B ←  Steam 云端同步」、一段说明、去哪里拿 Key 的网址（Y 打开）、
// 底部一个大的灰色输入框（真的 TextBox 盖在上面，打开就拿焦点，触屏弹键盘），最下面两个按钮：
// 白色「Ⓑ 取消」和绿色「Ⓐ 保存」：绿色是确认；Key 不是 32 位时绿色按钮变淡，输入框下面提示还差几位，
// 凑够 32 位提示变绿「格式正确」。Enter = 保存，Esc = 取消。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class AkState
    {
        public required ContainerVisual Root, Parent;
        public required SpriteVisual Scrim, Card;
        public CompositionDrawingSurface? Surf;
        public required string AccountId;
        public float H, FieldY, LinkY, BtnY;
        public bool Closing;
    }

    private AkState? _ak;
    private TextBox? _akBox;

    private const string AkUrl = "https://steamcommunity.com/dev/apikey";
    private const float AkW = 920, AkCardPad = 40, AkFieldH = 104, AkLinkH = 84;
    private const string AkBody = "填入 Steam Web API Key 后，游戏库能读到这个账号里的全部已购游戏，包括没有安装的。";
    private const string AkHint = "登录 Steam 后在这个网页申请，复制 32 位的 Key：";

    private static readonly Color AkFieldBg = Color.FromArgb(255, 0xE6, 0xE7, 0xEC);

    private string AkText => (_akBox?.Text ?? "").Trim();
    private bool AkValid => SteamCloudClient.ValidKey(AkText);

    /// <summary>打开小窗（只在 Cocoon 样式的设置页里；别的样式还用原来的行内输入）。</summary>
    private bool AkShow(string accountId)
    {
        if (_ak != null) return true;
        if (!CpActive || CpHost is not { } host || !_cpViews.TryGetValue(host, out var v)) return false;
        Cue(Sfx.ModalShow);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var scrim = G.Sprite(G.Solid(RmScrim), relative: false);
        scrim.Size = new Vector2(CsW, CsH);
        var card = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        root.Children.InsertAtTop(scrim);
        root.Children.InsertAtTop(card);
        v.Root.Children.InsertAtTop(root);
        var ak = new AkState { Root = root, Parent = v.Root, Scrim = scrim, Card = card, AccountId = accountId };
        _ak = ak;
        AkLayout(ak);
        AkEnsureBox();
        string existing = "";
        try { if (_steamCloud.HasKey(accountId)) existing = _steamCloud.ReadKey(accountId); } catch (ArgumentException) { }
        _akBox!.Text = existing;
        AkPlaceBox(ak);
        AkPaint(ak);
        if (!Reduced)
        {
            scrim.Opacity = 0;
            scrim.StartAnimation("Opacity", CsS(1, 230, Bezier.EaseOut));
            card.Opacity = 0;
            card.Scale = new Vector3(0.94f, 0.94f, 1);
            card.StartAnimation("Opacity", CsS(1, 80, Bezier.EaseOut));
            card.StartAnimation("Scale", CsV3(Vector3.One, 160, new Bezier(0.22f, 1f, 0.36f, 1f)));
        }
        // 卡片弹出来以后再显示输入框、拿焦点（不然 TextBox 先于卡片出现）
        After(Reduced ? 1 : 120, () =>
        {
            if (_ak != ak || _akBox == null) return;
            _akBox.Visibility = Visibility.Visible;
            AkHideClear();
            _akBox.Focus(FocusState.Programmatic);
            _akBox.SelectionStart = _akBox.Text.Length;
        });
        return true;
    }

    /// <summary>排版（卡片内坐标）：标题栏 37–111；说明 148 起；提示 + 网址胶囊；输入框；字数提示；按钮。</summary>
    private void AkLayout(AkState ak)
    {
        float y = 148;
        using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 34, WordWrapping = CanvasWordWrapping.Wrap })
        using (var lay = new CanvasTextLayout(G.Device, AkBody, tf, AkW - 120, 400) { LineSpacingMode = CanvasLineSpacingMode.Proportional, LineSpacing = 1.3f })
            y += (float)lay.LayoutBounds.Height;
        y += 30;
        y += 44;           // 提示一行
        ak.LinkY = y + 8;
        y = ak.LinkY + AkLinkH + 36;
        ak.FieldY = y;
        y += AkFieldH + 12 + 44;   // 输入框 + 字数提示
        ak.BtnY = y + 28;
        ak.H = ak.BtnY + RqBtnH + 40;
    }

    private (float X, float Y) AkOrigin(AkState ak) => ((CsW - AkW) / 2, (CsH - ak.H) / 2);

    private void AkPaint(AkState ak)
    {
        var (x0, y0) = AkOrigin(ak);
        float R = CsRaster, w = AkW + AkCardPad * 2, hh = ak.H + AkCardPad * 2;
        int pw = (int)Math.Ceiling(w * R), ph = (int)Math.Ceiling(hh * R);
        if (ak.Surf == null || (int)ak.Surf.Size.Width != pw || (int)ak.Surf.Size.Height != ph) { ak.Surf?.Dispose(); ak.Surf = G.Surface(pw, ph); }
        bool valid = AkValid;
        int n = AkText.Length;
        using (var ds = CanvasComposition.CreateDrawingSession(ak.Surf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(AkCardPad, AkCardPad) * Matrix3x2.CreateScale(R);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            RsSoftShadow(ds, 0, 12, AkW, ak.H, 60, 22, 0.35f);
            ds.FillRoundedRectangle(0, 0, AkW, ak.H, 60, 60, RsBg);
            RqHeaderPill(ds, 37, 37);
            const string title = "Steam 云端同步";
            using (var tf = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 48, FontWeight = RsWt(title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(title, new Windows.Foundation.Rect(192, 37, 680, 74), RmInk, tf);
            float y = 148;
            using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 34, WordWrapping = CanvasWordWrapping.Wrap })
            using (var lay = new CanvasTextLayout(ds.Device, AkBody, tf, AkW - 120, 400) { LineSpacingMode = CanvasLineSpacingMode.Proportional, LineSpacing = 1.3f })
            {
                ds.DrawTextLayout(lay, 60, y, RmInk);
                y += (float)lay.LayoutBounds.Height + 30;
            }
            using (var hf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 30, FontWeight = new Windows.UI.Text.FontWeight(500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(AkHint, new Windows.Foundation.Rect(60, y, AkW - 120, 44), RsSub, hf);
            // 网址胶囊：灰底、左边 Y 圆章、紫色网址 + 右边外链箭头（Y 或点它打开网页）
            float ly = ak.LinkY, lx = 37, lw = AkW - 74;
            ds.FillRoundedRectangle(lx, ly, lw, AkLinkH, AkLinkH / 2, AkLinkH / 2, Color.FromArgb(255, 0xEE, 0xEF, 0xF3));
            float cy = ly + AkLinkH / 2;
            ds.FillCircle(lx + 50, cy, 22, Color.FromArgb(255, 0x9A, 0x9C, 0xA3));
            using (var kf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
                ds.DrawText("Y", new Windows.Foundation.Rect(lx + 28, cy - 22, 44, 44), Color.FromArgb(255, 0xF2, 0xF2, 0xF4), kf);
            string url = AkUrl.Replace("https://", "");
            using (var uf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 32, FontWeight = new Windows.UI.Text.FontWeight(600), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(url, new Windows.Foundation.Rect(lx + 92, ly, lw - 170, AkLinkH), CpAccentInk, uf);
            {
                float ax = lx + lw - 48, ay = cy;
                ds.DrawLine(ax - 10, ay + 10, ax + 10, ay - 10, CpAccentInk, 5, RwRoundStroke);
                ds.DrawLine(ax - 4, ay - 10, ax + 10, ay - 10, CpAccentInk, 5, RwRoundStroke);
                ds.DrawLine(ax + 10, ay - 10, ax + 10, ay + 4, CpAccentInk, 5, RwRoundStroke);
            }
            // 输入框：大的灰色圆角框，里面的字是盖在上面的 TextBox；格式对了描一圈绿边
            float fy = ak.FieldY;
            ds.FillRoundedRectangle(37, fy, AkW - 74, AkFieldH, AkFieldH / 2, AkFieldH / 2, AkFieldBg);
            if (valid) ds.DrawRoundedRectangle(38.5f, fy + 1.5f, AkW - 77, AkFieldH - 3, AkFieldH / 2, AkFieldH / 2, RqGreenB, 3);
            string count = valid ? "格式正确" : n == 0 ? "需要 32 位字母和数字" : $"已输入 {n} 位，需要 32 位";
            using (var cf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 30, FontWeight = new Windows.UI.Text.FontWeight(500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
                ds.DrawText(count, new Windows.Foundation.Rect(0, fy + AkFieldH + 12, AkW, 44), valid ? RqGreenB : RsSub, cf);
            // 按钮：白色取消、绿色保存（Key 不对时保存变淡）
            float bw = (AkW - 74 - 38) / 2;
            RqButton(ds, 37, ak.BtnY, bw, "B", "取消", green: false, busy: false);
            using (valid ? null : ds.CreateLayer(0.45f))
                RqButton(ds, 37 + bw + 38, ak.BtnY, bw, "A", _steamRefreshing ? "正在保存…" : "保存", green: true, busy: _steamRefreshing);
        }
        ((CompositionSurfaceBrush)ak.Card.Brush).Surface = ak.Surf;
        ak.Card.Size = new Vector2(w, hh);
        ak.Card.Offset = new Vector3(x0 - AkCardPad, y0 - AkCardPad, 0);
        ak.Card.CenterPoint = new Vector3(w / 2, hh / 2, 0);
    }

    private void AkEnsureBox()
    {
        if (_akBox != null) return;
        _akBox = new TextBox
        {
            MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0), RequestedTheme = ElementTheme.Light,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, MaxLength = 64,
            VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            FontFamily = new FontFamily(TextKit.XamlUi), PlaceholderText = "粘贴或输入 API Key",
        };
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" }) _akBox.Resources[key] = clear;
        var ink = new SolidColorBrush(RmInk);
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) _akBox.Resources[key] = ink;
        var ph = new SolidColorBrush(Color.FromArgb(255, 0x8E, 0x90, 0x98));
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" }) _akBox.Resources[key] = ph;
        _akBox.Foreground = ink;
        _akBox.Background = clear;
        _akBox.Padding = new Thickness(0);
        _akBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; AkSave(); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; AkClose(); }
        };
        _akBox.TextChanged += (_, _) => { if (_ak is { } a) AkPaint(a); };
        _akBox.Loaded += (_, _) => AkHideClear();
        _akBox.GotFocus += (_, _) => AkHideClear();
        _akBox.LayoutUpdated += (_, _) => AkHideClear();
        HookTouchKeyboard(_akBox);
        _rootEl.Overlay.Children.Add(_akBox);
    }

    /// <summary>TextBox 自带的 ✕ 清除按钮不要（模板加载晚，多处补一次）。</summary>
    private void AkHideClear()
    {
        if (_akBox != null && FindChild(_akBox, "DeleteButton") is FrameworkElement x && x.MaxWidth != 0) { x.MaxWidth = 0; x.Opacity = 0; x.IsHitTestVisible = false; }
    }

    /// <summary>TextBox 放到输入框里（设计单位 × 页面缩放 = 窗口坐标）。</summary>
    private void AkPlaceBox(AkState ak)
    {
        if (_akBox == null) return;
        var (x0, y0) = AkOrigin(ak);
        float s = _ccK / 1.5f;
        _akBox.Width = (AkW - 74 - 96) * s;
        _akBox.Height = 56 * s;   // 一行字高，竖着放在输入框正中（TextBox 自己的竖直居中不可靠）
        _akBox.FontSize = 34 * s;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_akBox, (x0 + 37 + 48) * s);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_akBox, (y0 + ak.FieldY + (AkFieldH - 56) / 2) * s);
    }

    private void AkClose()
    {
        if (_ak is not { } ak || ak.Closing) return;
        ak.Closing = true;
        _ak = null;
        Cue(Sfx.Back);
        if (_akBox != null) { _akBox.Visibility = Visibility.Collapsed; _akBox.Text = ""; }
        HideTouchKeyboardSoon();
        void Drop() { try { ak.Parent.Children.Remove(ak.Root); ak.Surf?.Dispose(); ak.Root.Dispose(); } catch (ObjectDisposedException) { } }
        if (Reduced) { Drop(); return; }
        ak.Root.StartAnimation("Opacity", CsS(0, 90, Bezier.EaseOut));
        After(110, Drop);
    }

    private void AkSave()
    {
        if (_ak is not { } ak || _steamRefreshing) return;
        if (!AkValid) { Cue(Sfx.Bump); CpToast("Steam Web API Key 是 32 位字母和数字"); return; }
        string key = AkText, id = ak.AccountId;
        Cue(Sfx.Activate);
        AkClose();
        _ = SaveSteamCloudKey(id, key);
    }

    private void AkOpenUrl()
    {
        Cue(Sfx.Activate);
        PlatformClients.Open(AkUrl);
    }

    // ---------------- 输入（Cp 页面的钩子先问这里） ----------------

    /// <summary>手柄：A 保存、B 取消、Y 打开网页；方向键不动（焦点一直在输入框）。</summary>
    private bool AkPad(string key)
    {
        if (_ak == null) return false;
        switch (key)
        {
            case "A": AkSave(); break;
            case "B": AkClose(); break;
            case "Y": AkOpenUrl(); break;
            default: Cue(Sfx.Bump); break;
        }
        return true;
    }

    /// <summary>点：取消 / 保存 / 网址胶囊；点卡片外面 = 取消。</summary>
    private bool AkTap(float x, float y)
    {
        if (_ak is not { } ak) return false;
        var (x0, y0) = AkOrigin(ak);
        float lx = x - x0, ly = y - y0, bw = (AkW - 74 - 38) / 2;
        if (lx < 0 || lx > AkW || ly < 0 || ly > ak.H) { AkClose(); return true; }
        if (ly >= ak.BtnY && ly <= ak.BtnY + RqBtnH)
        {
            if (lx >= 37 && lx <= 37 + bw) AkClose();
            else if (lx >= 37 + bw + 38 && lx <= AkW - 37) AkSave();
            return true;
        }
        if (ly >= 37 && ly <= 111 && lx >= 37 && lx <= 171) { AkClose(); return true; }
        if (ly >= ak.LinkY && ly <= ak.LinkY + AkLinkH) { AkOpenUrl(); return true; }
        if (ly >= ak.FieldY && ly <= ak.FieldY + AkFieldH) _akBox?.Focus(FocusState.Pointer);
        return true;
    }
}
