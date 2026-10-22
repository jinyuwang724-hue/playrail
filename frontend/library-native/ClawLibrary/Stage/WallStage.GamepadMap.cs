// 控制中心 → 手柄 → 按键映射的布局（照 Steam 的控制器布局页）：C8EX 线稿放在上面当主角，
// 肩键 / 扳机 / View / Menu / 快速设置 / Claw / 背键挂在线稿左右两侧，底下压紧的四栏：左摇杆、十字键、右摇杆、正面按键。
// 每行 = 这个键的小图标 + 它现在映射到什么（键盘画键帽、鼠标画鼠标、手柄键画手柄键的图标），尽量不用字；没改过的写一个淡淡的「原键」。
// 焦点到 M1 / M2 时线稿翻到背面（GpPageFaces）。焦点那行白字，线稿上它管的那块染强调色。
// 掌机上要看得清：映射行（键的图标、键帽、字）整体放大 GpMapInk 倍，线稿整层（_gpArtPage）在映射页缩到 0.70 给它们让位
// （合成层弹簧），换回别的分类再回到原位。焦点的样子不变：白字 + 线稿上那块染色，不加框。
// 底部四栏各两行，整页内容竖向居中。游戏设置 › 手柄用同一套画法，按右栏宽度单独排（GpMapGeo）。
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // ---- 几何（设计 px） ----
    /// <summary>映射行的图标、键帽、字放大多少（以行的锚点为中心整体放大；选择指令页用它自己的尺寸）。</summary>
    private const float GpMapInk = 1.35f;

    /// <summary>
    /// 映射图的排法（设计 px）：线稿整层的比例和中心；两侧一列（左边图标右缘贴 LeftX，右边图标从 RightX 起，第一行 SideY，
    /// 行距 SideStep，一行宽 SideW）；底部四栏（标题 ColHeadY，第一行 ColY，行距 ColStep，从 ColX 起每栏 ColW，一行放 PerRow 格）。
    /// </summary>
    private sealed record GpMapGeo(float ArtScale, float ArtCx, float ArtCy, float LeftX, float RightX, float SideY, float SideStep, float SideW,
        float ColHeadY, float ColY, float ColStep, float ColX, float ColW, int PerRow);

    /// <summary>
    /// 手柄页（分类右边 220…1240）：线稿 0.70（532 宽），中心 (730, 320)，464…996 × 198…442；
    /// 两侧行距 48、一行 220 宽，贴着线稿；底部四栏铺满 230…1230，每栏两格一行，行距 44。
    /// </summary>
    private const float GpMapScale = 0.70f, GpMapCx = 730, GpMapCy = 320;
    private static readonly GpMapGeo GpMapPage = new(GpMapScale, GpMapCx, GpMapCy, 452, 1008, 214, 48, 220, 490, 532, 44, 230, 250, 2);

    private static readonly string[] GpMapColTitles = { "左摇杆", "十字键", "右摇杆", "正面按键" };

    /// <summary>手柄模式：两侧（从上到下）和底部四栏。</summary>
    private static readonly string[] GpMapSideL = { "LT", "LB", "Back", "QS", "M1" };
    private static readonly string[] GpMapSideR = { "RT", "RB", "Start", "Claw", "M2" };
    private static readonly string[][] GpMapCols = { new[] { "LStick", "LS" }, new[] { "Up", "Down", "Left", "Right" }, new[] { "RStick", "RS" }, new[] { "A", "B", "X", "Y" } };
    /// <summary>桌面模式：只有固件管得到的键。</summary>
    private static readonly string[] GpDeskSideL = { "LT", "LB" };
    private static readonly string[] GpDeskSideR = { "RT", "RB" };
    private static readonly string[][] GpDeskCols = { new[] { "LS" }, new[] { "Up", "Down", "Left", "Right" }, new[] { "RS" }, new[] { "A", "B", "X", "Y" } };

    /// <summary>映射值里的一块：Cap = 键帽，Mouse = 鼠标图标 + 字，Pad = 手柄键图标，Plus = 组合键之间的「+」，Trig = 触发方式小字，Text = 字。</summary>
    private enum GpTokKind { Cap, Mouse, Pad, Plus, Trig, Text }
    private readonly record struct GpTok(GpTokKind Kind, string Text, string Id = "");

    // ---------------- 布局 ----------------

    /// <summary>
    /// 按键映射页的格子：两侧 + 底部四栏，位置写进 GpItem（X / Y 是这一行的锚点，MapW / MapH 是这一行占的宽高，
    /// Cx / Cy 是按方位找焦点用的中心）。keep 筛掉不要的键（游戏设置里没有快速设置键、Claw 键、摇杆互换），剩下的排紧。
    /// </summary>
    private void BuildGpMapItems(bool desktop, GpMapGeo? geo = null, Func<string, bool>? keep = null)
    {
        var g = geo ?? GpMapPage;
        bool Keep(string s) => keep == null || keep(s);
        var sideL = (desktop ? GpDeskSideL : GpMapSideL).Where(Keep).ToArray();
        var sideR = (desktop ? GpDeskSideR : GpMapSideR).Where(Keep).ToArray();
        var cols = (desktop ? GpDeskCols : GpMapCols).Select(c => c.Where(Keep).ToArray()).ToArray();
        float subW = g.ColW / g.PerRow;
        void Add(string src, int col, float x, float y, float w, float h)
        {
            var it = GpMapCell(src, desktop);
            it.Map = true;
            it.Col = col;
            it.Compact = true;
            it.Side = col == -1 ? -1 : 1;
            it.X = x;
            it.Y = y;
            it.MapW = w;
            it.MapH = h;
            it.Cx = col == -1 ? x - w / 2 : x + w / 2;
            it.Cy = y;
            it.Tokens = GpMapTokens(src, desktop, it);
            _gpItems.Add(it);
        }
        for (int i = 0; i < sideL.Length; i++) Add(sideL[i], -1, g.LeftX, g.SideY + i * g.SideStep, g.SideW, g.SideStep);
        for (int i = 0; i < sideR.Length; i++) Add(sideR[i], -2, g.RightX, g.SideY + i * g.SideStep, g.SideW, g.SideStep);
        // 手柄页每栏两个小格一行：摇杆（方向、按下）一行；十字键 ↑ ↓ / ← →、正面按键 A B / X Y 各两行
        for (int c = 0; c < cols.Length; c++)
            for (int r = 0; r < cols[c].Length; r++)
                Add(cols[c][r], c, g.ColX + c * g.ColW + r % g.PerRow * subW, g.ColY + r / g.PerRow * g.ColStep, subW, g.ColStep);
    }

    /// <summary>线稿整层的位置：映射页缩小上移，别的分类原位。弹簧，打断时从当前值接着走。</summary>
    private void GpPlaceArt(bool instant = false)
    {
        if (_gp == null) return;
        float k = _ccK, m = GpIsMapPage ? GpMapScale : 1, cx = GpIsMapPage ? GpMapCx : GpCx, cy = GpIsMapPage ? GpMapCy : GpCy;
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

    /// <summary>栏标题（映射页底部四栏），和映射行一起放大。</summary>
    private void PaintGpMapHeads(CanvasDrawingSession ds, GpMapGeo? geo = null)
    {
        var g = geo ?? GpMapPage;
        var f = TextKit.Ui(600);
        var cols = GpCatId == "dkmap" && geo == null ? GpDeskCols : GpMapCols;
        for (int c = 0; c < cols.Length; c++)
            PText(ds, GpMapColTitles[c], f, 13 * GpMapInk, Gfx.WithAlpha(CcText, 0.45f), g.ColX + c * g.ColW, g.ColHeadY, g.ColW);
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
