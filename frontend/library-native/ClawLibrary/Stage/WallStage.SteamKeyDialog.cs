// 填密钥的小窗（11-26 jinyu：做成「切换游戏」那种弹窗；Steam Web API Key 和 IGDB 的 Client ID / Secret 共用）。
// 白底圆角卡片（同 RqPaint）：标题栏「B ←  标题」、一段说明、去哪里拿密钥的网址（Y 打开）、
// 一到两个大的灰色输入框（真的 TextBox 盖在上面，打开就拿焦点，触屏弹键盘；两个时 ↑↓ 切换），最下面两个按钮：
// 白色「Ⓑ 取消」和主题色「Ⓐ 保存」：主题色是确认（11-26 jinyu：按钮用主题色）；有一个框格式不对时保存按钮变淡，
// 框下面提示还差什么，对了提示变主题色、框描一圈主题色。Enter = 下一个框 / 保存，Esc = 取消。
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
    /// <summary>一个输入框：框上方的小标题（只有一个框时不画）、占位字、初始值、校验（返回 null = 格式正确，否则是提示）。</summary>
    private sealed record AkField(string Label, string Placeholder, string Initial, Func<string, string?> Check);

    /// <summary>一个小窗的内容：标题、说明、取密钥的提示和网址、输入框、保存（拿到每个框去掉首尾空格后的字）。</summary>
    private sealed record AkSpec(string Title, string Body, string Hint, string Url, AkField[] Fields, Action<string[]> Save);

    private sealed class AkState
    {
        public required ContainerVisual Root, Parent;
        public required SpriteVisual Scrim, Card;
        public CompositionDrawingSurface? Surf;
        public required AkSpec Spec;
        public float H, LinkY, BtnY;
        public float[] FieldY = Array.Empty<float>();
        public int Focus;
        public bool Closing;
    }

    private AkState? _ak;
    private readonly List<TextBox> _akBoxes = new();

    private const float AkW = 920, AkCardPad = 40, AkFieldH = 104, AkLinkH = 84, AkLabelH = 44;
    private static Color AkFieldBg => Ck(Color.FromArgb(255, 0xE6, 0xE7, 0xEC));

    private string AkText(int i) => i < _akBoxes.Count ? _akBoxes[i].Text.Trim() : "";
    private string? AkCheck(AkState ak, int i) => ak.Spec.Fields[i].Check(AkText(i));
    private bool AkAllValid(AkState ak) => Enumerable.Range(0, ak.Spec.Fields.Length).All(i => AkCheck(ak, i) == null);

    // ---------------- 两种小窗 ----------------

    /// <summary>Steam 云端同步：32 位 Web API Key。</summary>
    private bool AkShowSteam(string accountId)
    {
        string existing = "";
        try { if (_steamCloud.HasKey(accountId)) existing = _steamCloud.ReadKey(accountId); } catch (ArgumentException) { }
        return AkShow(new AkSpec("Steam 云端同步",
            "填入 Steam Web API Key 后，游戏库能读到这个账号里的全部已购游戏，包括没有安装的。",
            "登录 Steam 后在这个网页申请，复制 32 位的 Key：",
            "https://steamcommunity.com/dev/apikey",
            new[] { new AkField("API Key", "粘贴或输入 API Key", existing,
                s => SteamCloudClient.ValidKey(s) ? null : s.Length == 0 ? "需要 32 位字母和数字" : $"已输入 {s.Length} 位，需要 32 位") },
            v => _ = SaveSteamCloudKey(accountId, v[0])));
    }

    /// <summary>刮削来源 IGDB：Client ID + Client Secret（在 Twitch 开发者后台建一个应用拿到）。</summary>
    private bool AkShowIgdb()
    {
        var p = ScLoadPrefs();
        static string? Check(string s) => s.Length == 0 ? "还没填" : s.Any(char.IsWhiteSpace) ? "不能有空格" : s.Length < 20 || s.Length > 64 ? $"已输入 {s.Length} 位，一般是 30 位" : null;
        return AkShow(new AkSpec("IGDB 刮削密钥",
            "刮削游戏时从 IGDB 读游戏资料（简介、开发商、发行日期）。不填也能刮削，只是少了这个来源。",
            "登录 Twitch 开发者后台，新建一个应用，复制 Client ID 和 Client Secret：",
            "https://dev.twitch.tv/console/apps",
            new[] { new AkField("Client ID", "粘贴或输入 Client ID", p.IgdbId, Check), new AkField("Client Secret", "粘贴或输入 Client Secret", p.IgdbSecret, Check) },
            v =>
            {
                p.IgdbId = v[0]; p.IgdbSecret = v[1];
                ScSavePrefs();
                Toast("IGDB 刮削密钥存好了");
                Repaint();
            }));
    }

    /// <summary>SteamGridDB：一个 API Key（32 位）。已经配置过时框里留空（后台不回传密钥），空着保存 = 不改。</summary>
    private bool AkShowSgdb(bool already)
    {
        static string? Check(string s) => s.Length == 0 ? "需要 32 位字母和数字" : s.Any(char.IsWhiteSpace) ? "不能有空格" : s.Length != 32 || !s.All(char.IsLetterOrDigit) ? $"已输入 {s.Length} 位，需要 32 位" : null;
        return AkShow(new AkSpec("SteamGridDB 密钥",
            already ? "已经配置过。重新填一个会替换掉原来的。SteamGridDB 用来给本地游戏下载封面、横图、背景和标志。"
                    : "SteamGridDB 用来给本地游戏下载封面、横图、背景和标志。不填也能用，填了下载更稳定。",
            "登录 SteamGridDB 后在偏好设置的 API 页面生成，复制 32 位的 Key：",
            "https://www.steamgriddb.com/profile/preferences/api",
            new[] { new AkField("API Key", "粘贴或输入 API Key", "", Check) },
            v => _ = LgJob("set_steamgriddb_key", System.Text.Json.Nodes.JsonValue.Create(v[0]))));
    }

    // ---------------- 小窗本身 ----------------

    /// <summary>打开小窗（只在 Cocoon 样式的设置页里；别的样式返回 false，由调用方用原来的办法）。</summary>
    private bool AkShow(AkSpec spec)
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
        var ak = new AkState { Root = root, Parent = v.Root, Scrim = scrim, Card = card, Spec = spec };
        _ak = ak;
        AkLayout(ak);
        for (int i = 0; i < spec.Fields.Length; i++)
        {
            var box = AkBox(i);
            box.PlaceholderText = spec.Fields[i].Placeholder;
            box.Text = spec.Fields[i].Initial;
            AkPlaceBox(ak, i);
        }
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
        // 卡片弹出来以后再显示输入框、拿焦点（不然 TextBox 先于卡片出现）；焦点给第一个还没填对的框
        After(Reduced ? 1 : 120, () =>
        {
            if (_ak != ak) return;
            for (int i = 0; i < spec.Fields.Length; i++) { _akBoxes[i].Visibility = Visibility.Visible; AkHideClear(_akBoxes[i]); }
            int first = Enumerable.Range(0, spec.Fields.Length).FirstOrDefault(i => AkCheck(ak, i) != null);
            AkFocus(ak, first);
        });
        return true;
    }

    private void AkFocus(AkState ak, int i)
    {
        if (i < 0 || i >= ak.Spec.Fields.Length) return;
        ak.Focus = i;
        var box = _akBoxes[i];
        box.Focus(FocusState.Programmatic);
        box.SelectionStart = box.Text.Length;
        AkPaint(ak);
    }

    /// <summary>排版（卡片内坐标）：标题栏 37–111；说明 148 起；提示 + 网址胶囊；每个输入框（两个时上面有小标题）+ 提示；按钮。</summary>
    private void AkLayout(AkState ak)
    {
        float y = 148;
        using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 34, WordWrapping = CanvasWordWrapping.Wrap })
        using (var lay = new CanvasTextLayout(G.Device, ak.Spec.Body, tf, AkW - 120, 400) { LineSpacingMode = CanvasLineSpacingMode.Proportional, LineSpacing = 1.3f })
            y += (float)lay.LayoutBounds.Height;
        y += 30;
        using (var hf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 30, WordWrapping = CanvasWordWrapping.Wrap })
        using (var lay = new CanvasTextLayout(G.Device, ak.Spec.Hint, hf, AkW - 120, 400))
            y += MathF.Max(44, (float)lay.LayoutBounds.Height + 4);
        ak.LinkY = y + 8;
        y = ak.LinkY + AkLinkH + 28;
        int n = ak.Spec.Fields.Length;
        ak.FieldY = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (n > 1) y += AkLabelH;
            ak.FieldY[i] = y;
            y += AkFieldH + 8 + 44 + (i < n - 1 ? 12 : 0);
        }
        ak.BtnY = y + 28;
        ak.H = ak.BtnY + RqBtnH + 40;
    }

    private (float X, float Y) AkOrigin(AkState ak) => ((CsW - AkW) / 2, (CsH - ak.H) / 2);

    private void AkPaint(AkState ak)
    {
        var spec = ak.Spec;
        var (x0, y0) = AkOrigin(ak);
        float R = CsRaster, w = AkW + AkCardPad * 2, hh = ak.H + AkCardPad * 2;
        int pw = (int)Math.Ceiling(w * R), ph = (int)Math.Ceiling(hh * R);
        if (ak.Surf == null || (int)ak.Surf.Size.Width != pw || (int)ak.Surf.Size.Height != ph) { ak.Surf?.Dispose(); ak.Surf = G.Surface(pw, ph); }
        bool all = AkAllValid(ak);
        using (var ds = CanvasComposition.CreateDrawingSession(ak.Surf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(AkCardPad, AkCardPad) * Matrix3x2.CreateScale(R);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            RsSoftShadow(ds, 0, 12, AkW, ak.H, 60, 22, 0.35f);
            ds.FillRoundedRectangle(0, 0, AkW, ak.H, 60, 60, RsBg);
            RqHeaderPill(ds, 37, 37);
            using (var tf = new CanvasTextFormat { FontFamily = RmFont(spec.Title), FontSize = 48, FontWeight = RsWt(spec.Title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(spec.Title, new Windows.Foundation.Rect(192, 37, 680, 74), RmInk, tf);
            float y = 148;
            using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 34, WordWrapping = CanvasWordWrapping.Wrap })
            using (var lay = new CanvasTextLayout(ds.Device, spec.Body, tf, AkW - 120, 400) { LineSpacingMode = CanvasLineSpacingMode.Proportional, LineSpacing = 1.3f })
            {
                ds.DrawTextLayout(lay, 60, y, RmInk);
                y += (float)lay.LayoutBounds.Height + 30;
            }
            using (var hf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 30, FontWeight = new Windows.UI.Text.FontWeight(500), WordWrapping = CanvasWordWrapping.Wrap })
            using (var lay = new CanvasTextLayout(ds.Device, spec.Hint, hf, AkW - 120, 400))
                ds.DrawTextLayout(lay, 60, y + 4, RsSub);
            // 网址胶囊：灰底、左边 Y 圆章、紫色网址 + 右边外链箭头（Y 或点它打开网页）
            float ly = ak.LinkY, lx = 37, lw = AkW - 74;
            ds.FillRoundedRectangle(lx, ly, lw, AkLinkH, AkLinkH / 2, AkLinkH / 2, Ck(Color.FromArgb(255, 0xEE, 0xEF, 0xF3)));
            float cy = ly + AkLinkH / 2;
            ds.FillCircle(lx + 50, cy, 22, Ck(Color.FromArgb(255, 0x9A, 0x9C, 0xA3)));
            using (var kf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
                ds.DrawText("Y", new Windows.Foundation.Rect(lx + 28, cy - 22, 44, 44), Color.FromArgb(255, 0xF2, 0xF2, 0xF4), kf);
            string url = spec.Url.Replace("https://", "");
            using (var uf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 32, FontWeight = new Windows.UI.Text.FontWeight(600), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(url, new Windows.Foundation.Rect(lx + 92, ly, lw - 170, AkLinkH), CpAccentInk, uf);
            {
                float ax = lx + lw - 48, ay = cy;
                ds.DrawLine(ax - 10, ay + 10, ax + 10, ay - 10, CpAccentInk, 5, RwRoundStroke);
                ds.DrawLine(ax - 4, ay - 10, ax + 10, ay - 10, CpAccentInk, 5, RwRoundStroke);
                ds.DrawLine(ax + 10, ay - 10, ax + 10, ay + 4, CpAccentInk, 5, RwRoundStroke);
            }
            // 输入框：大的灰色圆角框，里面的字是盖在上面的 TextBox；格式对了描一圈主题色；两个框时正在输入的那个描灰边
            int n = spec.Fields.Length;
            for (int i = 0; i < n; i++)
            {
                float fy = ak.FieldY[i];
                string? err = AkCheck(ak, i);
                if (n > 1)
                    using (var lf = new CanvasTextFormat { FontFamily = RmFont(spec.Fields[i].Label), FontSize = 30, FontWeight = RsWt(spec.Fields[i].Label, 600), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                        ds.DrawText(spec.Fields[i].Label, new Windows.Foundation.Rect(60, fy - AkLabelH, AkW - 120, AkLabelH - 4), RmInk, lf);
                ds.FillRoundedRectangle(37, fy, AkW - 74, AkFieldH, AkFieldH / 2, AkFieldH / 2, AkFieldBg);
                if (err == null) ds.DrawRoundedRectangle(38.5f, fy + 1.5f, AkW - 77, AkFieldH - 3, AkFieldH / 2, AkFieldH / 2, CpAccentInk, 3);
                else if (n > 1 && i == ak.Focus) ds.DrawRoundedRectangle(38.5f, fy + 1.5f, AkW - 77, AkFieldH - 3, AkFieldH / 2, AkFieldH / 2, Ck(Color.FromArgb(255, 0xB4, 0xB6, 0xBE)), 3);
                using var cf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 30, FontWeight = new Windows.UI.Text.FontWeight(500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
                ds.DrawText(err ?? "格式正确", new Windows.Foundation.Rect(0, fy + AkFieldH + 8, AkW, 44), err == null ? CpAccentInk : RsSub, cf);
            }
            // 按钮：白色取消、主题色保存（有框不对时保存变淡）
            float bw = (AkW - 74 - 38) / 2;
            RqButton(ds, 37, ak.BtnY, bw, "B", "取消", green: false, busy: false);
            using (all ? null : ds.CreateLayer(0.45f))
                RqButton(ds, 37 + bw + 38, ak.BtnY, bw, "A", _steamRefreshing ? "正在保存…" : "保存", green: true, busy: _steamRefreshing, accent: true);
        }
        ((CompositionSurfaceBrush)ak.Card.Brush).Surface = ak.Surf;
        ak.Card.Size = new Vector2(w, hh);
        ak.Card.Offset = new Vector3(x0 - AkCardPad, y0 - AkCardPad, 0);
        ak.Card.CenterPoint = new Vector3(w / 2, hh / 2, 0);
    }

    /// <summary>第 i 个输入框（不够就新建一个，建好后一直留着复用）。</summary>
    private TextBox AkBox(int i)
    {
        while (_akBoxes.Count <= i)
        {
            int idx = _akBoxes.Count;
            var box = new TextBox
            {
                MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0), RequestedTheme = CocoonPalette.Dark ? ElementTheme.Dark : ElementTheme.Light,
                IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, MaxLength = 64,
                VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                FontFamily = new FontFamily(TextKit.XamlUi),
            };
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" }) box.Resources[key] = clear;
            var ink = new SolidColorBrush(RmInk);
            foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) box.Resources[key] = ink;
            var ph = new SolidColorBrush(Ck(Color.FromArgb(255, 0x8E, 0x90, 0x98)));
            foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" }) box.Resources[key] = ph;
            box.Foreground = ink;
            box.Background = clear;
            box.Padding = new Thickness(0);
            box.KeyDown += (_, e) =>
            {
                if (_ak is not { } a) return;
                if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; if (idx < a.Spec.Fields.Length - 1) AkFocus(a, idx + 1); else AkSave(); }
                else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; AkClose(); }
            };
            box.TextChanged += (_, _) => { if (_ak is { } a) AkPaint(a); };
            box.GotFocus += (_, _) => { AkHideClear(box); if (_ak is { } a && a.Focus != idx) { a.Focus = idx; AkPaint(a); } };
            box.Loaded += (_, _) => AkHideClear(box);
            box.LayoutUpdated += (_, _) => AkHideClear(box);
            HookTouchKeyboard(box);
            _rootEl.Overlay.Children.Add(box);
            _akBoxes.Add(box);
        }
        return _akBoxes[i];
    }

    /// <summary>TextBox 自带的 ✕ 清除按钮不要（模板加载晚，多处补一次）。</summary>
    private void AkHideClear(TextBox box)
    {
        if (FindChild(box, "DeleteButton") is FrameworkElement x && x.MaxWidth != 0) { x.MaxWidth = 0; x.Opacity = 0; x.IsHitTestVisible = false; }
    }

    /// <summary>TextBox 放到第 i 个输入框里（设计单位 × 页面缩放 = 窗口坐标）。</summary>
    private void AkPlaceBox(AkState ak, int i)
    {
        var box = _akBoxes[i];
        var (x0, y0) = AkOrigin(ak);
        float s = _ccK / 1.5f;
        box.Width = (AkW - 74 - 96) * s;
        box.Height = 56 * s;   // 一行字高，竖着放在输入框正中（TextBox 自己的竖直居中不可靠）
        box.FontSize = 34 * s;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(box, (x0 + 37 + 48) * s);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(box, (y0 + ak.FieldY[i] + (AkFieldH - 56) / 2) * s);
    }

    private void AkClose()
    {
        if (_ak is not { } ak || ak.Closing) return;
        ak.Closing = true;
        _ak = null;
        Cue(Sfx.Back);
        foreach (var b in _akBoxes) { b.Visibility = Visibility.Collapsed; b.Text = ""; }
        HideTouchKeyboardSoon();
        void Drop() { try { ak.Parent.Children.Remove(ak.Root); ak.Surf?.Dispose(); ak.Root.Dispose(); } catch (ObjectDisposedException) { } }
        if (Reduced) { Drop(); return; }
        ak.Root.StartAnimation("Opacity", CsS(0, 90, Bezier.EaseOut));
        After(110, Drop);
    }

    private void AkSave()
    {
        if (_ak is not { } ak || _steamRefreshing) return;
        for (int i = 0; i < ak.Spec.Fields.Length; i++)
            if (AkCheck(ak, i) is { } err) { Cue(Sfx.Bump); AkFocus(ak, i); CpToast($"{ak.Spec.Fields[i].Label}：{err}"); return; }
        var values = Enumerable.Range(0, ak.Spec.Fields.Length).Select(AkText).ToArray();
        Cue(Sfx.Activate);
        AkClose();
        ak.Spec.Save(values);
    }

    private void AkOpenUrl()
    {
        if (_ak is not { } ak) return;
        Cue(Sfx.Activate);
        PlatformClients.Open(ak.Spec.Url);
    }

    // ---------------- 输入（Cp 页面的钩子先问这里） ----------------

    /// <summary>手柄：A 保存、B 取消、Y 打开网页、↑↓ 在两个框之间切换。</summary>
    private bool AkPad(string key)
    {
        if (_ak is not { } ak) return false;
        switch (key)
        {
            case "A": AkSave(); break;
            case "B": AkClose(); break;
            case "Y": AkOpenUrl(); break;
            case "Up": if (ak.Focus > 0) { Cue(Sfx.Nav); AkFocus(ak, ak.Focus - 1); } else Cue(Sfx.Bump); break;
            case "Down": if (ak.Focus < ak.Spec.Fields.Length - 1) { Cue(Sfx.Nav); AkFocus(ak, ak.Focus + 1); } else Cue(Sfx.Bump); break;
            default: Cue(Sfx.Bump); break;
        }
        return true;
    }

    /// <summary>点：取消 / 保存 / 网址胶囊 / 输入框；点卡片外面 = 取消。</summary>
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
        for (int i = 0; i < ak.FieldY.Length; i++)
            if (ly >= ak.FieldY[i] && ly <= ak.FieldY[i] + AkFieldH) { AkFocus(ak, i); break; }
        return true;
    }
}
