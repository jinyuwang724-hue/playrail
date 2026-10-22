// 控制中心 → 手柄 → 按键映射（照 Steam 的控制器布局页）：C8EX 线稿居中，
// 扳机 / 肩键 / View / 快速设置 / 背键挂在线稿左侧，右侧对称；线稿下面四栏：左摇杆、十字键、右摇杆、正面按键。
// 每行 = 这个键的图标（Steam 的按键图标，Assets/Pad/Glyphs）+ 它现在映射到什么，用字写（「Ctrl 键」「鼠标左键点击」「十字上键」）。
// 焦点到 M1 / M2 时线稿翻到背面（GpPageFaces）。焦点那行白字、图标后面一圈淡光，线稿上它管的那块染强调色，不加框。
// A 进这个键所在的分组页（WallStage.GamepadGroups.cs）。游戏设置 › 手柄用同一套画法，按右栏宽度排（GpMapGeo）。
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // ---- 几何（设计 px） ----

    /// <summary>
    /// 映射图的排法（设计 px）：线稿整层的比例和中心；两侧（左边图标右缘贴 LeftX，右边图标从 RightX 起，第一行 SideY，行距 SideStep，
    /// 一行宽 SideW）；下面四栏（栏标题 ColHeadY，第一行 ColY，行距 ColStep，从 ColX 起每栏 ColW）。
    /// </summary>
    private sealed record GpMapGeo(float ArtScale, float ArtCx, float ArtCy, float LeftX, float RightX, float SideY, float SideStep, float SideW,
        float ColHeadY, float ColY, float ColStep, float ColX, float ColW);

    /// <summary>
    /// 手柄页（分类右边 220…1240）：线稿 0.80（608 宽），中心 (730, 300)，426…1034 × 161…439；
    /// 两侧行距 44、一行 182 宽，贴着线稿；下面四栏从 250 起每栏 245，栏标题 478，行距 34。
    /// </summary>
    private const float GpMapScale = 0.80f, GpMapCx = 730, GpMapCy = 300;
    private static readonly GpMapGeo GpMapPage = new(GpMapScale, GpMapCx, GpMapCy, 402, 1058, 196, 44, 182, 478, 512, 34, 250, 245);
    /// <summary>映射行的图标框（Steam 图标画在 28×28 里）和字号（照 Steam 控制器页：映射 16、栏标题 15，都按 1280 宽的设计 px）。</summary>
    private const float GpMapGlyph = 28, GpMapFont = 16;
    /// <summary>下面四栏每栏的内容（栏标题 + 这一栏最宽的一行）在栏里居中：栏左边 x → 往右挪多少。画之前按字宽算一次。</summary>
    private readonly Dictionary<float, float> _gpMapColDx = new();

    private void GpMapCenterColumns(CanvasDrawingSession ds, IEnumerable<GpItem> items, List<(string Title, float X, float Y, float W)> heads)
    {
        _gpMapColDx.Clear();
        var head = TextKit.Ui(600);
        foreach (var (title, x, _, w) in heads)
        {
            float widest = PWidth(ds, title, head, 15);
            foreach (var it in items)
            {
                if (it.Col < 0 || it.X != x) continue;
                string text = GpFit(ds, it.Value, TextKit.Ui(600), GpMapFont, Math.Max(30, it.MapW - GpMapGlyph - 10));
                widest = Math.Max(widest, GpMapGlyph + 10 + PWidth(ds, text, TextKit.Ui(600), GpMapFont));
            }
            _gpMapColDx[x] = Math.Max(0, (w - widest) / 2);
        }
    }
    /// <summary>映射页往上滚了多少（设计 px）：现在一屏放得下，保持 0；焦点格子的下沿不低于 GpMapViewBottom。</summary>
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

    private static readonly string[] GpMapColTitles = { "左摇杆", "十字键", "右摇杆", "正面按键" };
    /// <summary>手柄模式：两侧（从上到下）和下面四栏。</summary>
    private static readonly string[] GpMapSideL = { "LT", "LB", "Back", "QS", "M1" };
    private static readonly string[] GpMapSideR = { "RT", "RB", "Start", "Claw", "M2" };
    private static readonly string[][] GpMapCols = { new[] { "LStick", "LS" }, new[] { "Up", "Down", "Left", "Right" }, new[] { "RStick", "RS" }, new[] { "A", "B", "X", "Y" } };
    /// <summary>桌面模式：只有固件管得到的键。</summary>
    private static readonly string[] GpDeskSideL = { "LT", "LB" };
    private static readonly string[] GpDeskSideR = { "RT", "RB" };
    private static readonly string[][] GpDeskCols = { new[] { "LS" }, new[] { "Up", "Down", "Left", "Right" }, new[] { "RS" }, new[] { "A", "B", "X", "Y" } };
    /// <summary>手柄页映射图的栏标题（标题、左边、中线 y、宽）。</summary>
    private readonly List<(string Title, float X, float Y, float W)> _gpMapHeads = new();

    /// <summary>映射值里的一块：Cap = 键帽，Mouse = 鼠标图标 + 字，Pad = 手柄键图标，Plus = 组合键之间的「+」，Trig = 触发方式小字，Text = 字。</summary>
    private enum GpTokKind { Cap, Mouse, Pad, Plus, Trig, Text }
    private readonly record struct GpTok(GpTokKind Kind, string Text, string Id = "");

    // ---------------- 布局 ----------------

    /// <summary>
    /// 按键映射页的格子：两侧 + 下面四栏，位置写进 GpItem（X / Y 是图标那头的锚点和中线：左侧是图标右缘，其余是图标左缘；
    /// MapW / MapH 是这一行占的宽高，Cx / Cy 是按方位找焦点用的中心），栏标题写进 heads。
    /// keep 筛掉不要的键（游戏设置里没有快速设置键、Claw 键、摇杆互换），剩下的排紧。
    /// </summary>
    private void BuildGpMapItems(bool desktop, GpMapGeo? geo = null, Func<string, bool>? keep = null, List<(string Title, float X, float Y, float W)>? heads = null)
    {
        var g = geo ?? GpMapPage;
        heads ??= _gpMapHeads;
        heads.Clear();
        bool Keep(string k) => keep == null || keep(k);
        var sideL = (desktop ? GpDeskSideL : GpMapSideL).Where(Keep).ToArray();
        var sideR = (desktop ? GpDeskSideR : GpMapSideR).Where(Keep).ToArray();
        var cols = (desktop ? GpDeskCols : GpMapCols).Select(c => c.Where(Keep).ToArray()).ToArray();
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
            it.Value = GpMapText(it, src, desktop);
            _gpItems.Add(it);
        }
        for (int i = 0; i < sideL.Length; i++) Add(sideL[i], -1, g.LeftX, g.SideY + i * g.SideStep, g.SideW, g.SideStep);
        for (int i = 0; i < sideR.Length; i++) Add(sideR[i], -2, g.RightX, g.SideY + i * g.SideStep, g.SideW, g.SideStep);
        for (int c = 0; c < cols.Length; c++)
        {
            if (cols[c].Length == 0) continue;
            heads.Add((GpMapColTitles[c], g.ColX + c * g.ColW, g.ColHeadY, g.ColW));
            for (int r = 0; r < cols[c].Length; r++)
                Add(cols[c][r], c, g.ColX + c * g.ColW, g.ColY + r * g.ColStep, g.ColW - 12, g.ColStep);
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

    /// <summary>栏标题（映射页下面四栏）。</summary>
    private void PaintGpMapHeads(CanvasDrawingSession ds, List<(string Title, float X, float Y, float W)>? heads = null)
    {
        var f = TextKit.Ui(600);
        foreach (var (title, x, y, w) in heads ?? _gpMapHeads)
            PText(ds, title, f, 15, Gfx.WithAlpha(CcText, 0.5f), x + (_gpMapColDx.TryGetValue(x, out var dx) ? dx : 0), y, w);
    }

    /// <summary>
    /// 一行：键的图标 + 映射到什么（字）。左侧一列右对齐（字在外、图标挨着线稿），右侧和下面四栏左对齐（图标在前）。
    /// 焦点白字、图标后面一圈淡光；调节中（摇杆互换、快速设置键 / Claw 键）字两边 ‹ ›。
    /// </summary>
    private void PaintGpMapRow(CanvasDrawingSession ds, GpItem it, bool focused)
    {
        bool editing = focused && _gpEditing;
        var ui = focused ? TextKit.Ui(600) : TextKit.Ui(400);
        float alpha = it.Disabled ? 0.38f : focused ? 1f : 0.8f;
        var col = Gfx.WithAlpha(CcText, alpha);
        var glyphCol = Gfx.WithAlpha(CcText, it.Disabled ? 0.38f : focused ? 1f : 0.86f);
        string text = editing && it.Choices != null ? it.Choices[it.Choice] : it.Value;
        string glyph = it.Key.Length > 0 ? it.Key : it.Lights?[0] == "RS" ? "RStick" : "LStick";
        const float gap = 10;
        float chev = editing ? 16 : 0;
        text = GpFit(ds, text, ui, GpMapFont, Math.Max(30, it.MapW - GpMapGlyph - gap - chev * 2));
        float tw = PWidth(ds, text, ui, GpMapFont);
        float y = it.Y;
        if (it.Col == -1)
        {
            GpPaintGlyph(ds, glyph, it.X - GpMapGlyph, y, GpMapGlyph, glyphCol, focused);
            float tx = it.X - GpMapGlyph - gap - chev - tw;
            PText(ds, text, ui, GpMapFont, col, tx, y, tw + 4);
            if (editing) { DrawChevron(ds, tx - 12, y, true, col); DrawChevron(ds, tx + tw + 4, y, false, col); }
        }
        else
        {
            float x = it.X + (it.Col >= 0 && _gpMapColDx.TryGetValue(it.X, out var cdx) ? cdx : 0);   // 下面四栏：在栏里居中
            GpPaintGlyph(ds, glyph, x, y, GpMapGlyph, glyphCol, focused);
            float tx = x + GpMapGlyph + gap + chev;
            PText(ds, text, ui, GpMapFont, col, tx, y, tw + 4);
            if (editing) { DrawChevron(ds, tx - 12, y, true, col); DrawChevron(ds, tx + tw + 4, y, false, col); }
        }
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
