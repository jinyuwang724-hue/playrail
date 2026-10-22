// 控制中心 → 手柄 → 按键映射的布局：C8EX 线稿放在上面当主角（和其他分类一样大，不缩小），
// 每个键映射到什么排在线稿下面，按组分好：肩键与扳机、功能键、背键、左摇杆、十字键、右摇杆、正面按键，每组两格一行。
// 一屏放不下：焦点往下走到屏幕外的格子时，线稿和格子一起往上滚（_gpMapScroll，线稿整层走弹簧），回到上面再滚回来。
// 每格 = 这个键的小图标 + 它现在映射到什么（键盘画键帽、鼠标画鼠标、手柄键画手柄键的图标），尽量不用字；没改过的写一个淡淡的「原键」。
// 掌机上要看得清：格子里的图标、键帽、字整体放大 GpMapInk 倍，组标题也一样放大。
// 焦点到 M1 / M2 时线稿翻到背面（GpPageFaces）。焦点那格白字，线稿上它管的那块染强调色（这就是键在哪的提示），不加框。
// 游戏设置 › 手柄用同一套排法，按右栏宽度排（GpMapGeo）。
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // ---- 几何（设计 px） ----

    /// <summary>映射格里的图标、键帽、字放大多少（以格子的锚点为中心整体放大；选择指令页用它自己的尺寸）。</summary>
    private const float GpMapInk = 1.35f;

    /// <summary>
    /// 映射图的排法（设计 px）：线稿整层的比例和中心；线稿下面的分组从 GridTop 起、在 GridX…GridX+GridW 里每行放 Groups 组
    /// （一行不满就居中），每组两格一行，格子行距 Step，组标题占 HeadH，两行组之间空 RowGap。
    /// </summary>
    private sealed record GpMapGeo(float ArtScale, float ArtCx, float ArtCy, float GridX, float GridTop, float GridW, int Groups,
        float Step, float HeadH, float RowGap);

    /// <summary>
    /// 手柄页（分类右边 220…1240）：线稿和原来一样 0.99（752 宽），中心 (730, 332)，354…1106 × 160…504；
    /// 分组从 512 起铺满 230…1230，一行三组（每组 333、每格 166，映射值放得下），三行到 850，靠滚动看到下面。
    /// </summary>
    private const float GpMapScale = 0.99f, GpMapCx = 730, GpMapCy = 332;
    private static readonly GpMapGeo GpMapPage = new(GpMapScale, GpMapCx, GpMapCy, 230, 512, 1000, 3, 40, 28, 6);
    /// <summary>映射页往上滚了多少（设计 px）；焦点格子的下沿不低于 GpMapViewBottom（底下是按键提示）。</summary>
    private float _gpMapScroll;
    private const float GpMapViewBottom = 712;

    /// <summary>按焦点算滚动：焦点在格子上且格子在可视范围下面 → 往上滚到刚好露出；焦点在上面（模式）→ 回到顶。</summary>
    private void GpUpdateMapScroll()
    {
        float want = 0;
        if (GpIsMapPage && !_gpInSide && GpFocusItem is { Map: true } f)
            want = Math.Max(0, f.Y + f.MapH / 2 + 6 - GpMapViewBottom);
        else if (GpIsMapPage && _gpInSide) want = _gpMapScroll;   // 回到分类：页面停在原处
        if (!GpIsMapPage) want = 0;
        if (Math.Abs(want - _gpMapScroll) < 0.5f) return;
        _gpMapScroll = want;
        GpPlaceArt();
    }

    /// <summary>手柄模式的分组（每组两格一行，按手柄上的位置：左边的在左格）。</summary>
    private static readonly (string Title, string[] Keys)[] GpMapGroups =
    {
        ("肩键与扳机", new[] { "LT", "RT", "LB", "RB" }), ("功能键", new[] { "Back", "Start", "QS", "Claw" }), ("背键", new[] { "M1", "M2" }),
        ("左摇杆", new[] { "LStick", "LS" }), ("十字键", new[] { "Up", "Down", "Left", "Right" }), ("右摇杆", new[] { "RStick", "RS" }),
        ("正面按键", new[] { "A", "B", "X", "Y" }),
    };
    /// <summary>桌面模式：只有固件管得到的键。</summary>
    private static readonly (string Title, string[] Keys)[] GpDeskGroups =
    {
        ("肩键与扳机", new[] { "LT", "RT", "LB", "RB" }), ("左摇杆", new[] { "LS" }), ("十字键", new[] { "Up", "Down", "Left", "Right" }),
        ("右摇杆", new[] { "RS" }), ("正面按键", new[] { "A", "B", "X", "Y" }),
    };
    /// <summary>手柄页映射图的组标题（标题、左边、中线 y、宽）。</summary>
    private readonly List<(string Title, float X, float Y, float W)> _gpMapHeads = new();

    /// <summary>映射值里的一块：Cap = 键帽，Mouse = 鼠标图标 + 字，Pad = 手柄键图标，Plus = 组合键之间的「+」，Trig = 触发方式小字，Text = 字。</summary>
    private enum GpTokKind { Cap, Mouse, Pad, Plus, Trig, Text }
    private readonly record struct GpTok(GpTokKind Kind, string Text, string Id = "");

    // ---------------- 布局 ----------------

    /// <summary>
    /// 按键映射页的格子：线稿下面按组排，位置写进 GpItem（X / Y 是这一格图标的左边和中线，MapW / MapH 是这一格占的宽高，
    /// Cx / Cy 是按方位找焦点用的中心），组标题写进 heads。keep 筛掉不要的键（游戏设置里没有快速设置键、Claw 键、摇杆互换），剩下的排紧。
    /// </summary>
    private void BuildGpMapItems(bool desktop, GpMapGeo? geo = null, Func<string, bool>? keep = null, List<(string Title, float X, float Y, float W)>? heads = null)
    {
        var g = geo ?? GpMapPage;
        heads ??= _gpMapHeads;
        heads.Clear();
        var groups = (desktop ? GpDeskGroups : GpMapGroups)
            .Select(gr => (gr.Title, Keys: gr.Keys.Where(k => keep == null || keep(k)).ToArray())).Where(gr => gr.Keys.Length > 0).ToList();
        float gw = g.GridW / g.Groups, cw = gw / 2;
        float y = g.GridTop;
        int index = 0;
        for (int i = 0; i < groups.Count; i += g.Groups)
        {
            var row = groups.Skip(i).Take(g.Groups).ToList();
            int lines = row.Max(gr => (gr.Keys.Length + 1) / 2);
            float x0 = g.GridX + (g.Groups - row.Count) * gw / 2;   // 一行不满：居中
            for (int j = 0; j < row.Count; j++)
            {
                float gx = x0 + j * gw;
                heads.Add((row[j].Title, gx, y + g.HeadH / 2 - 4, gw));
                for (int k = 0; k < row[j].Keys.Length; k++)
                {
                    var it = GpMapCell(row[j].Keys[k], desktop);
                    it.Map = true;
                    it.Col = index;
                    it.Compact = true;
                    it.Side = 1;
                    it.X = gx + k % 2 * cw;
                    it.Y = y + g.HeadH + k / 2 * g.Step + g.Step / 2;
                    it.MapW = cw;
                    it.MapH = g.Step;
                    it.Cx = it.X + cw / 2;
                    it.Cy = it.Y;
                    it.Tokens = GpMapTokens(row[j].Keys[k], desktop, it);
                    _gpItems.Add(it);
                }
                index++;
            }
            y += g.HeadH + lines * g.Step + g.RowGap;
        }
    }

    /// <summary>线稿整层的位置：映射页缩小上移，别的分类原位。弹簧，打断时从当前值接着走。</summary>
    private void GpPlaceArt(bool instant = false)
    {
        if (_gp == null) return;
        float k = _ccK, m = GpIsMapPage ? GpMapScale : 1, cx = GpIsMapPage ? GpMapCx : GpCx, cy = GpIsMapPage ? GpMapCy - _gpMapScroll : GpCy;
        var scale = new Vector3(m, m, 1);
        var off = new Vector3((cx - GpCx * m) * k, (cy - GpCy * m) * k, 0);
        if (instant || Reduced)
        {
            _gpArtPage.StopAnimation("Scale"); _gpArtPage.StopAnimation("Offset");
            _gpArtPage.Scale = scale; _gpArtPage.Offset = off;
            return;
        }
        _gpArtPage.StartAnimation("Scale", G.SpringV3(scale, Spring.Frame));
        _gpArtPage.StartAnimation("Offset", G.SpringV3(off, Spring.Frame));
    }

    // ---------------- 映射值 → 图标 ----------------

    /// <summary>
    /// 这个键现在映射到什么，拆成图标块。软件层布局里的键：每个触发方式一段（只有一个普通按压时不写触发方式），最多两段；
    /// 固件管的键：固件里的键码；没改过 = 「原键」（Stock，画得淡）。摇杆 / 快速设置键 / Claw 键这种几选一的直接写选项。
    /// </summary>
    private List<GpTok> GpMapTokens(string src, bool desktop, GpItem it)
    {
        var toks = new List<GpTok>();
        if (GpIsSpecial(src)) { if (_gpSettings?["buttonMappings"]?[src] == null) toks.Add(new(GpTokKind.Text, "原功能")); else GpCommandTokens(GpSpecialCommand(src, false), toks); return toks; }
        if (it.Choices != null) { toks.Add(new(GpTokKind.Text, it.Value)); return toks; }
        if (desktop)
        {
            if (_gpFw == null || _gpFw["desktopKeys"] == null) toks.Add(new(GpTokKind.Text, "—"));
            else if (GpDesktopStock(src)) toks.Add(new(GpTokKind.Text, "原键"));
            else GpCodesTokens(GpDesktopCodes(src)!, toks);
            return toks;
        }
        if (GpActs(src) is JsonArray acts)
        {
            if (acts.Count == 0) { toks.Add(new(GpTokKind.Text, "禁用")); return toks; }
            bool plain = acts.Count == 1 && (Str(acts[0]?["trigger"]) ?? "regular") == "regular";
            for (int i = 0; i < Math.Min(2, acts.Count); i++)
            {
                if (!plain)
                {
                    int t = Math.Max(0, Array.IndexOf(GpActIds, Str(acts[i]?["trigger"]) ?? "regular"));
                    toks.Add(new(GpTokKind.Trig, t == 0 ? "按" : GpActTypes[t]));
                }
                GpCommandTokens(acts[i]?["command"], toks);
            }
            if (acts.Count > 2) toks.Add(new(GpTokKind.Text, "…"));
            return toks;
        }
        var codes = GpFirmwareCodes(src);
        if (codes.Length == 0 && _gpFw == null && GpFirmwareSource(src)) toks.Add(new(GpTokKind.Text, "—"));
        else if (codes.Length == 0 || codes.Length == 1 && codes[0] == GpNativeCode(src)) toks.Add(new(GpTokKind.Text, "原键"));
        else GpCodesTokens(codes, toks);
        return toks;
    }

    /// <summary>固件键码 → 图标块（几个键盘键之间加「+」）。</summary>
    private static void GpCodesTokens(int[] codes, List<GpTok> toks)
    {
        if (codes.Length == 1 && codes[0] == 0xFF) { toks.Add(new(GpTokKind.Text, "禁用")); return; }
        for (int i = 0; i < codes.Length; i++)
        {
            if (i > 0) toks.Add(new(GpTokKind.Plus, "+"));
            var cell = GpAllCells.FirstOrDefault(x => x.Code == codes[i]);
            if (cell.Cmd is { Length: > 0 }) GpCmdTokens(cell.Cmd, cell.Label, toks);
            else toks.Add(new(GpTokKind.Cap, GpCodeName(codes[i])));
        }
    }

    /// <summary>软件层指令（JSON）→ 图标块。</summary>
    private static void GpCommandTokens(JsonNode? cmd, List<GpTok> toks)
    {
        string Label(string c, string fallback) => GpAllCells.Where(x => x.Cmd == c).Select(x => x.Label).FirstOrDefault() ?? fallback;
        switch (Str(cmd?["type"]))
        {
            case "keyboard":
            {
                var keys = (cmd!["keys"] as JsonArray ?? new JsonArray()).Select(k => Str(k) ?? "").ToArray();
                for (int i = 0; i < keys.Length; i++)
                {
                    if (i > 0) toks.Add(new(GpTokKind.Plus, "+"));
                    toks.Add(new(GpTokKind.Cap, Label("key:" + keys[i], keys[i])));
                }
                break;
            }
            case "mouse":
                if (cmd!["wheel"] != null) GpCmdTokens("wheel:" + Str(cmd["wheel"]), "", toks);
                else GpCmdTokens("mouse:" + Str(cmd["button"]), "", toks);
                break;
            case "gamepad": GpCmdTokens("pad:" + Str(cmd!["button"]), "", toks); break;
            case "system": GpCmdTokens("sys:" + Str(cmd!["action"]), "", toks); break;
            default: toks.Add(new(GpTokKind.Text, "无")); break;
        }
    }

    /// <summary>一条指令（"key:Tab" "mouse:Left" "wheel:Up" "pad:B" "sys:mute"）→ 一块图标。</summary>
    private static void GpCmdTokens(string cmd, string label, List<GpTok> toks)
    {
        var parts = cmd.Split(':', 2);
        string arg = parts.Length > 1 ? parts[1] : "";
        if (label.Length == 0) label = GpAllCells.Where(x => x.Cmd == cmd).Select(x => x.Label).FirstOrDefault() ?? arg;
        switch (parts[0])
        {
            case "key": toks.Add(new(GpTokKind.Cap, label)); break;
            case "mouse":
                toks.Add(new(GpTokKind.Mouse, arg switch { "Left" => "左键", "Right" => "右键", "Middle" => "中键", "Back" => "后退", "Forward" => "前进", _ => arg }, arg));
                break;
            case "wheel":
                toks.Add(new(GpTokKind.Mouse, arg switch { "Up" => "滚轮 ↑", "Down" => "滚轮 ↓", "Left" => "滚轮 ←", _ => "滚轮 →" }, "Wheel"));
                break;
            case "pad":
                // 摇杆方向（pad:LStickUp）画摇杆图标加方向；其余手柄键画它自己的图标
                toks.Add(new(GpTokKind.Pad, label, arg));
                break;
            default: toks.Add(new(GpTokKind.Text, label)); break;
        }
    }

    // ---------------- 绘制 ----------------

    /// <summary>组标题，和映射格一起放大。</summary>
    private void PaintGpMapHeads(CanvasDrawingSession ds, List<(string Title, float X, float Y, float W)>? heads = null)
    {
        var f = TextKit.Ui(600);
        foreach (var (title, x, y, w) in heads ?? _gpMapHeads)
            PText(ds, title, f, 13 * GpMapInk, Gfx.WithAlpha(CcText, 0.45f), x, y, w);
    }

    /// <summary>
    /// 一行：键的小图标 + 映射值。左侧一列右对齐（值在外、图标挨着线稿），右侧和底部左对齐（图标在前）。
    /// 焦点白字、键帽实心；没改过的（原键）淡；调节中（摇杆 / 快速设置键 / Claw 键）值两边 ‹ ›。
    /// </summary>
    private void PaintGpMapRow(CanvasDrawingSession ds, GpItem it, bool focused)
    {
        bool editing = focused && _gpEditing;
        float alpha = it.Disabled ? 0.38f : focused ? 1f : it.Stock ? 0.4f : 0.82f;
        var col = Gfx.WithAlpha(CcText, alpha);
        var glyphCol = Gfx.WithAlpha(CcText, focused ? 1f : it.Disabled ? 0.38f : 0.7f);
        var toks = it.Tokens ?? new List<GpTok>();
        if (editing && it.Choices != null) toks = new List<GpTok> { new(GpTokKind.Text, it.Choices[it.Choice]) };

        // 摇杆那格（Key 为空，点亮 LS / RS）画摇杆图标
        string glyph = it.Key.Length > 0 ? it.Key : it.Lights?[0] == "RS" ? "RStick" : "LStick";
        float gw = GpGlyphWidth(glyph);
        float chev = editing ? 16 : 0;
        const float gap = 10;
        // 这一行放大前能用的宽：映射值放不下就截掉、加「…」
        toks = GpFitTokens(ds, toks, Math.Max(24, (it.MapW - 8) / GpMapInk - gw - gap - chev * 2));
        float tw = GpTokensWidth(ds, toks);
        float y = it.Y;
        // 以锚点为中心整体放大（图标、键帽、字一起）
        var old = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(-it.X, -y) * Matrix3x2.CreateScale(GpMapInk) * Matrix3x2.CreateTranslation(it.X, y) * old;
        if (it.Col == -1)
        {
            // 左侧：…值 图标| （图标右边缘贴着锚点）
            GpDrawGlyph(ds, glyph, it.X - gw, y, glyphCol, focused);
            float tx = it.X - gw - gap - tw - chev;
            GpDrawTokens(ds, toks, tx, y, col, focused);
            if (editing) { DrawChevron(ds, tx - 4, y, true, col); DrawChevron(ds, tx + tw + chev - 2, y, false, col); }
        }
        else
        {
            GpDrawGlyph(ds, glyph, it.X, y, glyphCol, focused);
            float tx = it.X + gw + gap + chev;
            GpDrawTokens(ds, toks, tx, y, col, focused);
            if (editing) { DrawChevron(ds, tx - chev + 6, y, true, col); DrawChevron(ds, tx + tw + 10, y, false, col); }
        }
        ds.Transform = old;
    }

    private static readonly FontSpec GpCapFont = new("Segoe UI Variable Text", 600);
    private const float GpCapH = 22, GpTokGap = 6;

    private static float GpTokWidth(CanvasDrawingSession ds, GpTok t) => t.Kind switch
    {
        GpTokKind.Cap => Math.Max(GpCapH, PWidth(ds, t.Text, GpCapFont, 12) + 13),
        GpTokKind.Mouse => 14 + 5 + PWidth(ds, t.Text, TextKit.Ui(400), 13),
        GpTokKind.Pad => GpGlyphWidth(t.Id),
        GpTokKind.Plus => PWidth(ds, "+", TextKit.Ui(400), 12),
        GpTokKind.Trig => PWidth(ds, t.Text, TextKit.Ui(400), 11),
        _ => PWidth(ds, t.Text, TextKit.Ui(400), 14),
    };

    private static float GpTokensWidth(CanvasDrawingSession ds, List<GpTok> toks)
    {
        float w = 0;
        for (int i = 0; i < toks.Count; i++) w += GpTokWidth(ds, toks[i]) + (i > 0 ? GpTokGap : 0);
        return w;
    }

    /// <summary>放不下就从尾巴丢块，最后补一个「…」。</summary>
    private static List<GpTok> GpFitTokens(CanvasDrawingSession ds, List<GpTok> toks, float max)
    {
        if (GpTokensWidth(ds, toks) <= max) return toks;
        var list = new List<GpTok>(toks);
        while (list.Count > 1)
        {
            list.RemoveAt(list.Count - 1);
            while (list.Count > 1 && list[^1].Kind is GpTokKind.Plus or GpTokKind.Trig) list.RemoveAt(list.Count - 1);
            var tryList = new List<GpTok>(list) { new(GpTokKind.Text, "…") };
            if (GpTokensWidth(ds, tryList) <= max) return tryList;
        }
        if (list.Count == 1 && list[0].Kind == GpTokKind.Text)
            return new List<GpTok> { new(GpTokKind.Text, GpFit(ds, list[0].Text, TextKit.Ui(400), 14, max)) };
        return list;
    }

    private void GpDrawTokens(CanvasDrawingSession ds, List<GpTok> toks, float x, float cy, Color col, bool focused)
    {
        var ui = TextKit.Ui(400);
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            float w = GpTokWidth(ds, t);
            switch (t.Kind)
            {
                case GpTokKind.Cap:
                {
                    var r = new Windows.Foundation.Rect(x, cy - GpCapH / 2, w, GpCapH);
                    if (focused)
                    {
                        ds.FillRoundedRectangle(r, 5, 5, col);
                        PText(ds, t.Text, GpCapFont, 12, Gfx.Rgba(0x0b, 0x0b, 0x0d), x, cy, w, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                    }
                    else
                    {
                        ds.DrawRoundedRectangle(r, 5, 5, col, 1.3f);
                        PText(ds, t.Text, GpCapFont, 12, col, x, cy, w, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                    }
                    break;
                }
                case GpTokKind.Mouse:
                    GpDrawMouse(ds, x, cy, t.Id, col);
                    PText(ds, t.Text, ui, 13, col, x + 19, cy, w);
                    break;
                case GpTokKind.Pad:
                    GpDrawGlyph(ds, t.Id, x, cy, col, false);
                    break;
                case GpTokKind.Plus:
                    PText(ds, "+", ui, 12, Gfx.WithAlpha(col, col.A / 255f * 0.6f), x, cy, w + 4);
                    break;
                case GpTokKind.Trig:
                    PText(ds, t.Text, ui, 11, Gfx.WithAlpha(col, col.A / 255f * 0.6f), x, cy, w + 4);
                    break;
                default:
                    PText(ds, t.Text, ui, 14, col, x, cy, w + 4);
                    break;
            }
            x += w + GpTokGap;
        }
    }

    /// <summary>鼠标图标（14×20）：按的那半边实心；滚轮画中间一道。</summary>
    private static void GpDrawMouse(CanvasDrawingSession ds, float x, float cy, string btn, Color col)
    {
        float top = cy - 10, cx = x + 7;
        ds.DrawRoundedRectangle(x + 0.6f, top + 0.6f, 12.8f, 18.8f, 6.4f, 6.4f, col, 1.2f);
        ds.DrawLine(x + 0.6f, top + 8, x + 13.4f, top + 8, col, 1.2f);
        ds.DrawLine(cx, top + 0.6f, cx, top + 8, col, 1.2f);
        using var pb = new CanvasPathBuilder(ds);
        switch (btn)
        {
            case "Left":
                pb.BeginFigure(cx, top + 1.2f); pb.AddArc(new Vector2(x + 1.2f, top + 7.4f), 5.8f, 5.8f, 0, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
                pb.AddLine(cx, top + 7.4f); pb.EndFigure(CanvasFigureLoop.Closed);
                break;
            case "Right":
                pb.BeginFigure(cx, top + 1.2f); pb.AddArc(new Vector2(x + 12.8f, top + 7.4f), 5.8f, 5.8f, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                pb.AddLine(cx, top + 7.4f); pb.EndFigure(CanvasFigureLoop.Closed);
                break;
            default:
                // 中键 / 滚轮 / 侧键：中间一小条
                pb.BeginFigure(cx - 1.4f, top + 2.6f); pb.AddLine(cx + 1.4f, top + 2.6f); pb.AddLine(cx + 1.4f, top + 6.2f); pb.AddLine(cx - 1.4f, top + 6.2f); pb.EndFigure(CanvasFigureLoop.Closed);
                break;
        }
        using var g = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(g, col);
    }

    // ---------------- 键的小图标 ----------------

    /// <summary>每个键图标的宽（高都是 22）。</summary>
    private static float GpGlyphWidth(string id) => id switch
    {
        "LB" or "RB" or "M1" or "M2" or "QS" => 28,
        "Claw" or "Guide" => 34,
        _ => 22,
    };

    /// <summary>
    /// 手柄键的小图标（22 高，左边缘 x、竖向中线 cy）：A B X Y 圆圈字母，LB / RB / 背键 / 快速设置 / Claw 胶囊，LT / RT 扳机形，
    /// View / Menu 照 Xbox 的两个图标，摇杆是圈里写 L / R（按下 = 上面一个小三角压着实心的圈；方向 = 那边一个小三角），十字键是十字、那一臂实心。
    /// </summary>
    private static void GpDrawGlyph(CanvasDrawingSession ds, string id, float x, float cy, Color col, bool glow)
    {
        var bold = TextKit.Ui(700);
        if (glow) ds.FillCircle(x + GpGlyphWidth(id) / 2, cy, 15, Gfx.WithAlpha(col, 0.12f));
        float cx = x + GpGlyphWidth(id) / 2;
        switch (id)
        {
            case "A" or "B" or "X" or "Y":
                ds.DrawCircle(cx, cy, 9.6f, col, 1.4f);
                PText(ds, id, bold, 11, col, x, cy, 22, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                break;
            case "LB" or "RB" or "M1" or "M2" or "QS" or "Claw" or "Guide":
            {
                float w = GpGlyphWidth(id) - 2;
                ds.DrawRoundedRectangle(x + 1, cy - 6.5f, w, 13, 6.5f, 6.5f, col, 1.4f);
                PText(ds, id == "Guide" ? "Xbox" : id, bold, 8.5f, col, x + 1, cy, w, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                break;
            }
            case "LT" or "RT":
            {
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(x + 3, cy - 7);
                pb.AddCubicBezier(new Vector2(x + 7, cy - 9.5f), new Vector2(x + 15, cy - 9.5f), new Vector2(x + 19, cy - 7));
                pb.AddLine(x + 19, cy + 7); pb.AddLine(x + 3, cy + 7);
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(g, col, 1.4f);
                PText(ds, id, bold, 8.5f, col, x, cy + 1, 22, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                break;
            }
            case "Back":   // View：两个错开的方框
                ds.DrawRoundedRectangle(x + 2.5f, cy - 7, 10, 8, 1.6f, 1.6f, col, 1.4f);
                ds.DrawRoundedRectangle(x + 8.5f, cy - 2, 10, 8, 1.6f, 1.6f, col, 1.4f);
                break;
            case "Start":  // Menu：三道横线
                for (int i = -1; i <= 1; i++) ds.DrawLine(x + 3, cy + i * 5.5f, x + 19, cy + i * 5.5f, col, 1.5f);
                break;
            case "LStick" or "RStick" or "LS" or "RS":
            {
                string l = id[0] == 'L' ? "L" : "R";
                if (id is "LS" or "RS")
                {
                    // 按下：上面小三角，圈实心
                    using var pb = new CanvasPathBuilder(ds);
                    pb.BeginFigure(cx - 5, cy - 10); pb.AddLine(cx + 5, cy - 10); pb.AddLine(cx, cy - 6); pb.EndFigure(CanvasFigureLoop.Closed);
                    using var g = CanvasGeometry.CreatePath(pb);
                    ds.FillGeometry(g, col);
                    ds.FillCircle(cx, cy + 2.5f, 7.5f, col);
                    PText(ds, l, bold, 9.5f, Gfx.Rgba(0x0b, 0x0b, 0x0d), x, cy + 2.5f, 22, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                }
                else
                {
                    ds.DrawCircle(cx, cy, 8, col, 1.4f);
                    PText(ds, l, bold, 9.5f, col, x, cy, 22, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                }
                break;
            }
            case var s when s.StartsWith("LStick") || s.StartsWith("RStick"):
            {
                // 摇杆方向（映射值里用）：圈 + 那边一个小三角
                string l = s[0] == 'L' ? "L" : "R", dir = s[6..];
                ds.DrawCircle(cx, cy, 7, col, 1.3f);
                PText(ds, l, bold, 8.5f, col, x, cy, 22, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                var (dx, dy) = dir switch { "Up" => (0f, -1f), "Down" => (0f, 1f), "Left" => (-1f, 0f), _ => (1f, 0f) };
                var tip = new Vector2(cx + dx * 11, cy + dy * 11);
                var b1 = new Vector2(cx + dx * 8.2f - dy * 3, cy + dy * 8.2f + dx * 3);
                var b2 = new Vector2(cx + dx * 8.2f + dy * 3, cy + dy * 8.2f - dx * 3);
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(tip); pb.AddLine(b1); pb.AddLine(b2); pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(g, col);
                break;
            }
            case "Up" or "Down" or "Left" or "Right":
            {
                float a = 3.2f, L = 9.5f;   // 臂半宽、臂长
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(cx - a, cy - L); pb.AddLine(cx + a, cy - L); pb.AddLine(cx + a, cy - a); pb.AddLine(cx + L, cy - a); pb.AddLine(cx + L, cy + a);
                pb.AddLine(cx + a, cy + a); pb.AddLine(cx + a, cy + L); pb.AddLine(cx - a, cy + L); pb.AddLine(cx - a, cy + a); pb.AddLine(cx - L, cy + a);
                pb.AddLine(cx - L, cy - a); pb.AddLine(cx - a, cy - a); pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(g, col, 1.3f);
                var arm = id switch
                {
                    "Up" => new Windows.Foundation.Rect(cx - a, cy - L, a * 2, L - a),
                    "Down" => new Windows.Foundation.Rect(cx - a, cy + a, a * 2, L - a),
                    "Left" => new Windows.Foundation.Rect(cx - L, cy - a, L - a, a * 2),
                    _ => new Windows.Foundation.Rect(cx + a, cy - a, L - a, a * 2),
                };
                ds.FillRectangle(arm, col);
                break;
            }
            case "Share":  // 截图：相机
            {
                ds.DrawRoundedRectangle(x + 3, cy - 5.5f, 16, 12, 2.5f, 2.5f, col, 1.4f);
                ds.DrawLine(x + 8, cy - 5.5f, x + 9.5f, cy - 8, col, 1.4f);
                ds.DrawLine(x + 9.5f, cy - 8, x + 12.5f, cy - 8, col, 1.4f);
                ds.DrawLine(x + 12.5f, cy - 8, x + 14, cy - 5.5f, col, 1.4f);
                ds.DrawCircle(cx, cy + 0.5f, 3.4f, col, 1.4f);
                break;
            }
            default:
                PText(ds, id, bold, 10, col, x, cy, 22, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                break;
        }
    }
}
