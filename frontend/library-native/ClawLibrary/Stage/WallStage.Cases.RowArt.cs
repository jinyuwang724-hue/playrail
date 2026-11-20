// 「单行」样式 › 编辑 › 封面管理（照 Cocoon 的 Game artwork 页）：
//   标题栏中间是 LB / RB 切换的分段胶囊「封面 / 背景 / 标志」；
//   左栏选来源（SteamGridDB、Steam 官方、本地图片），选中的那项是一张浮起来的白卡；
//   右栏上方灰底条里是现在用着的那张，下面是候选网格；在网格里按 A 换上，Y ✓ 保存（Steam 游戏设成 Steam 的自定义图，
//   本地游戏存进本地游戏再同步到 Steam——和原来游戏设置页 › 封面同一套 AgDraft / AgEmbedSave）。
// 本地图片不弹 Windows 选文件窗口：直接列出图片、视频、下载、桌面这几个文件夹里的图。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class RaState
    {
        public AgDraft? D;
        public int Tab;                // 0 封面、1 背景、2 标志
        public int Zone;               // 0 来源、1 网格
        public int Src;                // 来源下标（RaSources）
        public int Idx;                // 网格里的焦点
        public int Scroll;             // 网格第一行
        public bool Busy;
        public string Status = "选好来源按 A 获取图片";
        public List<string>? Local;
        public readonly Dictionary<string, int> Orig = new();
        public readonly HashSet<string> Searched = new();
    }

    private static readonly (string Kind, string Title)[] RaKinds = { ("p", "封面"), ("header", "横封面"), ("hero", "背景"), ("logo", "标志") };   // 横封面 = Steam 的横版封面（460×215）
    private const float RaLeftX = 124, RaLeftW = 584, RaRightX = 728, RaRightW = 1068, RaTop = 196, RaBottom = 1100;
    private const float RaPrevH = 250;
    private const int RaCols = 5;

    private List<string> RaSources(RaState a)
    {
        var list = new List<string> { "SteamGridDB" };
        if (a.D is { } d && d.Official.ContainsKey(RaKinds[a.Tab].Kind)) list.Add("Steam 官方");
        list.Add("本地图片");
        return list;
    }

    private RsPage RaPage(LibraryItem g)
    {
        var a = new RaState();
        RsPage page = null!;
        page = new RsPage
        {
            Title = "封面管理",
            Build = () => new(),
            Paint = ds => RaPaint(ds, a),
            PaintHeaderExtra = ds => RaPaintTabs(ds, a),
            Move = dir => RaMove(a, dir),
            Activate = () => RaActivate(a, g),
            Tab = delta => { a.Tab = (a.Tab + delta + RaKinds.Length) % RaKinds.Length; a.Idx = 0; a.Scroll = 0; a.Src = Math.Min(a.Src, RaSources(a).Count - 1); },
            Dirty = () => a.D is { } d && RaKinds.Any(k => d.Pick.GetValueOrDefault(k.Kind, -1) != a.Orig.GetValueOrDefault(k.Kind, -1)),
            Save = () =>
            {
                if (a.D is not { } d) return;
                int n = 0;
                foreach (var (kind, _) in RaKinds)
                    if (d.Pick.GetValueOrDefault(kind, -1) != a.Orig.GetValueOrDefault(kind, -1)) { AgEmbedSave(d, kind); n++; }
                if (n > 0) Toast($"「{g.Title}」的封面在后台换上了");
            },
        };
        _ = RaLoad(a, g);
        return page;
    }

    /// <summary>草稿：本地游戏用本地游戏那份；Steam 游戏读现在用着的图（和 GsArtLoad 一样）。</summary>
    private async Task RaLoad(RaState a, LibraryItem item)
    {
        a.Busy = true;
        a.Status = "正在读取…";
        try
        {
            await LgRefresh();
            var local = LgGames().FirstOrDefault(x => Str(x["id"]) == item.Id);
            AgDraft d;
            if (local != null) d = AgDraftOf(local);
            else
            {
                d = new AgDraft { Id = item.Id, Name = item.Title, Exe = "", Query = item.Title, SteamArt = true, Steam = true };
                foreach (var (kind, _) in AgKinds) { d.Cands[kind] = new(); d.Pick[kind] = -1; }
                _agent ??= new AgentClient();
                if (await _agent.CallLong("game_art_get", 20, new JsonObject { ["id"] = item.Id, ["platform"] = item.Platform, ["title"] = item.Title }) is JsonObject r && r["art"] is JsonObject art)
                    foreach (var (kind, _) in AgKinds)
                        if (Str(art[kind]) is { Length: > 0 } path) { d.Cands[kind].Add((null, path)); d.Pick[kind] = 0; }
            }
            foreach (var (kind, _) in RaKinds) a.Orig[kind] = d.Pick.GetValueOrDefault(kind, -1);
            a.D = d;
            a.Status = "选好来源按 A 获取图片";
        }
        catch (Exception ex) { Log.Warn($"art page load: {ex.Message}"); a.Status = "没读到这款游戏的图"; }
        a.Busy = false;
        RsPaintSoon();
    }

    // ---------------- 画 ----------------

    /// <summary>标题栏中间：灰色胶囊里 LB · 封面 背景 标志 · RB，当前的那个是白底紫字。</summary>
    private void RaPaintTabs(CanvasDrawingSession ds, RaState a)
    {
        float x = 880, y = 104, h = 74, w = 740;
        ds.FillRoundedRectangle(x, y, w, h, 37, 37, Color.FromArgb(255, 0xE2, 0xE3, 0xE7));
        RaShoulder(ds, x + 20, y + 19, "LB");
        RaShoulder(ds, x + w - 56, y + 19, "RB");
        float cellW = (w - 160) / RaKinds.Length;
        for (int i = 0; i < RaKinds.Length; i++)
        {
            float cx = x + 80 + i * cellW;
            bool on = i == a.Tab;
            if (on) ds.FillRoundedRectangle(cx + 4, y + 8, cellW - 8, h - 16, 29, 29, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
            using var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 34, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
            ds.DrawText(RaKinds[i].Title, new Windows.Foundation.Rect(cx, y, cellW, h), on ? Color.FromArgb(255, 0x7A, 0x4C, 0xFF) : RsSub, f);
        }
    }

    private static void RaShoulder(CanvasDrawingSession ds, float x, float y, string label)
    {
        ds.FillRoundedRectangle(x, y, 36, 36, 9, 9, Color.FromArgb(255, 0x9A, 0x9C, 0xA3));
        using var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 16, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        ds.DrawText(label, new Windows.Foundation.Rect(x, y, 36, 36), Color.FromArgb(255, 0xF2, 0xF2, 0xF4), f);
    }

    private void RaPaint(CanvasDrawingSession ds, RaState a)
    {
        var dev = ds.Device;
        string kind = RaKinds[a.Tab].Kind;
        // 左栏：来源
        var srcs = RaSources(a);
        for (int i = 0; i < srcs.Count; i++)
        {
            float y = RaTop + i * 140;
            bool sel = i == a.Src;
            bool focus = sel && a.Zone == 0;
            if (sel)
            {
                using (var sh = new CanvasCommandList(dev))
                {
                    using (var s = sh.CreateDrawingSession()) { s.Transform = ds.Transform; s.FillRoundedRectangle(RaLeftX, y + 6, RaLeftW, 128, 36, 36, Colors.Black); }
                    using var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect { Source = sh, BlurAmount = 10 * ds.Transform.M11 };
                    using var op = new Microsoft.Graphics.Canvas.Effects.OpacityEffect { Source = blur, Opacity = 0.18f };
                    var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(op); ds.Transform = t;
                }
                ds.FillRoundedRectangle(RaLeftX, y, RaLeftW, 128, 36, 36, Color.FromArgb(255, 0xFC, 0xFC, 0xFD));
                if (focus) RaFocusRect(ds, RaLeftX, y, RaLeftW, 128, 36);
            }
            RaSourceIcon(ds, srcs[i], RaLeftX + 70, y + 64, sel);
            using var f = new CanvasTextFormat { FontFamily = RmFont(srcs[i]), FontSize = 42, FontWeight = new Windows.UI.Text.FontWeight(600), VerticalAlignment = CanvasVerticalAlignment.Center };
            ds.DrawText(srcs[i], new Windows.Foundation.Rect(RaLeftX + 140, y, RaLeftW - 150, 128), sel ? Color.FromArgb(255, 0x7A, 0x4C, 0xFF) : RmInk, f);
        }
        // 右栏：灰底卡
        ds.FillRoundedRectangle(RaRightX, RaTop - 10, RaRightW, RaBottom - RaTop + 10, 36, 36, Color.FromArgb(255, 0xF1, 0xF2, 0xF5));
        using (var top = CanvasGeometry.CreateRoundedRectangle(dev, RaRightX, RaTop - 10, RaRightW, RaPrevH, 36, 36))
        using (var cut = CanvasGeometry.CreateRectangle(dev, RaRightX, RaTop - 10 + 40, RaRightW, RaPrevH - 40))
        using (var band = top.CombineWith(cut, Matrix3x2.Identity, CanvasGeometryCombine.Union))
        using (var br = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xEA, 0xEB, 0xEF), Color.FromArgb(255, 0xD8, 0xD9, 0xE0)) { StartPoint = new Vector2(0, RaTop), EndPoint = new Vector2(0, RaTop + RaPrevH) })
            ds.FillGeometry(band, br);
        // 现在用着的那张
        if (a.D is { } d && d.Pick.GetValueOrDefault(kind, -1) is var pick and >= 0 && pick < d.Cands[kind].Count)
        {
            var bmp = AgBitmapOf(d.Cands[kind][pick].Thumb);
            if (bmp != null)
            {
                float maxW = RaRightW - 220, maxH = RaPrevH - 50;
                double k = Math.Min(maxW / bmp.Size.Width, maxH / bmp.Size.Height);
                float iw = (float)(bmp.Size.Width * k), ih = (float)(bmp.Size.Height * k);
                float ix = RaRightX + RaRightW / 2 - iw / 2, iy = RaTop - 10 + RaPrevH / 2 - ih / 2;
                using var rr = CanvasGeometry.CreateRoundedRectangle(dev, ix, iy, iw, ih, kind == "logo" ? 0 : 18, kind == "logo" ? 0 : 18);
                using (ds.CreateLayer(1, rr)) ds.DrawImage(bmp, new Windows.Foundation.Rect(ix, iy, iw, ih), bmp.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
            }
        }
        else RaEmpty(ds, RaRightX + RaRightW / 2, RaTop - 10 + RaPrevH / 2, "还没有" + RaKinds[a.Tab].Title, 0.7f);
        // 候选网格
        var cands = RaCands(a);
        float gx = RaRightX + 36, gy = RaTop - 10 + RaPrevH + 30, gw = RaRightW - 72;
        float cellW = (gw - (RaCols - 1) * 20) / RaCols;
        float aspect = kind switch { "hero" => 0.32f, "logo" => 0.45f, "header" => 0.467f, _ => 1.5f };
        float cellH = cellW * aspect;
        int rowsVisible = Math.Max(1, (int)((RaBottom - 20 - gy) / (cellH + 20)));
        if (cands.Count == 0)
        {
            RaEmpty(ds, RaRightX + RaRightW / 2, (gy + RaBottom) / 2, a.Busy ? "正在获取…" : a.Status, 1);
            return;
        }
        int focusRow = a.Idx / RaCols;
        if (focusRow < a.Scroll) a.Scroll = focusRow;
        if (focusRow >= a.Scroll + rowsVisible) a.Scroll = focusRow - rowsVisible + 1;
        using var clip = CanvasGeometry.CreateRectangle(dev, RaRightX, gy - 12, RaRightW, RaBottom - gy + 8);
        using (ds.CreateLayer(1, clip))
            for (int i = a.Scroll * RaCols; i < cands.Count && i < (a.Scroll + rowsVisible) * RaCols; i++)
            {
                float x = gx + i % RaCols * (cellW + 20), y = gy + (i / RaCols - a.Scroll) * (cellH + 20);
                var bmp = AgBitmapOf(cands[i].Thumb);
                using var rr = CanvasGeometry.CreateRoundedRectangle(dev, x, y, cellW, cellH, 14, 14);
                ds.FillGeometry(rr, Color.FromArgb(255, 0xE0, 0xE1, 0xE6));
                if (bmp != null)
                    using (ds.CreateLayer(1, rr))
                    {
                        double k = kind == "logo" ? Math.Min(cellW / bmp.Size.Width, cellH / bmp.Size.Height) : Math.Max(cellW / bmp.Size.Width, cellH / bmp.Size.Height);
                        float iw = (float)(bmp.Size.Width * k), ih = (float)(bmp.Size.Height * k);
                        ds.DrawImage(bmp, new Windows.Foundation.Rect(x + cellW / 2 - iw / 2, y + cellH / 2 - ih / 2, iw, ih), bmp.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                    }
                if (a.D is { } dd && RaIsCurrent(dd, kind, cands[i].Url)) RaCheck(ds, x + cellW - 26, y + 26);
                if (a.Zone == 1 && i == a.Idx) RaFocusRect(ds, x, y, cellW, cellH, 14);
            }
    }

    private static bool RaIsCurrent(AgDraft d, string kind, string? url)
    {
        int pick = d.Pick.GetValueOrDefault(kind, -1);
        return pick >= 0 && pick < d.Cands[kind].Count && d.Cands[kind][pick].Url == url && url != null;
    }

    /// <summary>
    /// 焦点：和其他地方同一种四角括号（圆角、短腿、四边中间断开），主题两色斜向渐变 + 第二色光晕 + 40 % 白边光。
    /// 这一页的焦点画在面板里（静态），不单独做会动的精灵。
    /// </summary>
    private static void RaFocusRect(CanvasDrawingSession ds, float x, float y, float w, float h, float r)
    {
        var dev = ds.Device;
        float W = w + 14, H = h + 20, rr = MathF.Min(r + 18, MathF.Min(W, H) / 2 - 8);
        float leg = MathF.Max(0, MathF.Min(18 * RwDp, (MathF.Min(W, H) - 2 * rr - 13) / 2));
        var (segs, _) = RwBracketPointsRaw(W, H, rr, leg, x - 7, y - 10);
        using var path = RwBracketPath(dev, segs);
        using (var glow = new CanvasCommandList(dev))
        {
            using (var g = glow.CreateDrawingSession()) { g.Transform = ds.Transform; g.DrawGeometry(path, RwC2, RwSw * 2, RwRoundStroke); }
            using var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect { Source = glow, BlurAmount = 7 * RwDp / 2 * ds.Transform.M11 };
            var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(blur); ds.Transform = t;
        }
        using var br = new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x + w, y + h) };
        ds.DrawGeometry(path, br, RwSw, RwRoundStroke);
        using var outer = path.Stroke(RwSw + 2 * RwDp, RwRoundStroke);
        using var inner = path.Stroke(MathF.Max(0.1f, RwSw - 2 * RwDp), RwRoundStroke);
        using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        ds.FillGeometry(ring, Color.FromArgb(102, 0xfa, 0xfa, 0xfa));
    }

    private static void RaCheck(CanvasDrawingSession ds, float cx, float cy)
    {
        ds.FillCircle(cx, cy, 18, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
        ds.FillCircle(cx, cy, 15, Color.FromArgb(255, 0x2B, 0xC7, 0x8A));
        using var pb = new CanvasPathBuilder(ds.Device);
        pb.BeginFigure(cx - 7, cy); pb.AddLine(cx - 2, cy + 5); pb.AddLine(cx + 7, cy - 5); pb.EndFigure(CanvasFigureLoop.Open);
        using var tick = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(tick, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 4, RwRoundStroke);
    }

    /// <summary>空状态：灰色图片图标 + 一行说明（Cocoon 的「Select a source and press A to scrape」）。</summary>
    private static void RaEmpty(CanvasDrawingSession ds, float cx, float cy, string text, float k)
    {
        float s = 112 * k;
        ds.FillRoundedRectangle(cx - s / 2, cy - s / 2 - 50 * k, s, s, 14 * k, 14 * k, Color.FromArgb(255, 0x8E, 0x90, 0x98));
        using (var pb = new CanvasPathBuilder(ds.Device))
        {
            float bx = cx - s / 2, by = cy - s / 2 - 50 * k;
            pb.BeginFigure(bx + s * 0.14f, by + s * 0.84f); pb.AddLine(bx + s * 0.40f, by + s * 0.52f); pb.AddLine(bx + s * 0.56f, by + s * 0.70f);
            pb.AddLine(bx + s * 0.68f, by + s * 0.58f); pb.AddLine(bx + s * 0.86f, by + s * 0.84f); pb.EndFigure(CanvasFigureLoop.Closed);
            using var mtn = CanvasGeometry.CreatePath(pb);
            ds.FillGeometry(mtn, Color.FromArgb(255, 0xF7, 0xF8, 0xFA));
        }
        using var f = new CanvasTextFormat { FontFamily = RmFont(text), FontSize = 34 * k, HorizontalAlignment = CanvasHorizontalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        ds.DrawText(text, new Windows.Foundation.Rect(cx - 600, cy + s / 2 - 10 * k, 1200, 60), RsSub, f);
    }

    private void RaSourceIcon(CanvasDrawingSession ds, string src, float cx, float cy, bool sel)
    {
        var ink = sel ? Color.FromArgb(255, 0x7A, 0x4C, 0xFF) : RmInk;
        if (src == "SteamGridDB")
        {
            for (int k = 2; k >= 0; k--)
            {
                float o = k * 6;
                ds.FillRoundedRectangle(cx - 30 + o, cy - 22 + o, 52, 34, 8, 8, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                ds.DrawRoundedRectangle(cx - 30 + o, cy - 22 + o, 52, 34, 8, 8, k == 0 && sel ? ink : Color.FromArgb(255, 0x9A, 0x9C, 0xA3), 3);
            }
            if (sel) ds.FillRoundedRectangle(cx - 30, cy - 22, 52, 34, 8, 8, ink);
            return;
        }
        if (src == "Steam 官方")
        {
            ds.FillCircle(cx, cy, 30, ink);
            _rwSteamSvg ??= Microsoft.Graphics.Canvas.Svg.CanvasSvgDocument.LoadFromXml(ds.Device, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Steam.svg")));
            _rwSteamSvg.Root.SetColorAttribute("fill", Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
            ds.DrawSvg(_rwSteamSvg, new Windows.Foundation.Size(40, 40), cx - 20, cy - 20);
            return;
        }
        // 本地图片：上传箭头 + 托盘
        ds.FillRoundedRectangle(cx - 30, cy + 2, 60, 28, 8, 8, ink);
        ds.FillRectangle(cx - 6, cy - 22, 12, 34, ink);
        using var pb = new CanvasPathBuilder(ds.Device);
        pb.BeginFigure(cx - 20, cy - 14); pb.AddLine(cx, cy - 34); pb.AddLine(cx + 20, cy - 14); pb.EndFigure(CanvasFigureLoop.Closed);
        using var head = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(head, ink);
        ds.FillRectangle(cx - 6, cy + 2, 12, 10, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
    }

    // ---------------- 候选 ----------------

    private List<(string? Url, string Thumb)> RaCands(RaState a)
    {
        if (a.D is not { } d) return new();
        string kind = RaKinds[a.Tab].Kind;
        string src = RaSources(a)[Math.Clamp(a.Src, 0, RaSources(a).Count - 1)];
        switch (src)
        {
            case "Steam 官方":
                return d.Official.TryGetValue(kind, out var o) ? new() { (o.Url, o.Thumb) } : new();
            case "本地图片":
                return (a.Local ?? new()).Select(p => ((string?)("file:" + p), p)).ToList();
            default:
                return a.Searched.Contains(kind) ? d.Cands[kind].Where(c => c.Url != null).ToList() : new();
        }
    }

    /// <summary>本地图片：图片、视频（封面图常放这里）、下载、桌面四个文件夹第一层的图，按修改时间新的在前，最多 80 张。</summary>
    private static List<string> RaLocalImages()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
        var files = new List<FileInfo>();
        foreach (var dir in dirs.Where(Directory.Exists))
            try { files.AddRange(new DirectoryInfo(dir).EnumerateFiles().Where(f => exts.Contains(f.Extension))); } catch { }
        return files.OrderByDescending(f => f.LastWriteTimeUtc).Take(80).Select(f => f.FullName).ToList();
    }

    // ---------------- 输入 ----------------

    private bool RaMove(RaState a, Direction dir)
    {
        var srcs = RaSources(a);
        if (a.Zone == 0)
        {
            if (dir == Direction.Up && a.Src > 0) { a.Src--; a.Idx = 0; a.Scroll = 0; return true; }
            if (dir == Direction.Down && a.Src < srcs.Count - 1) { a.Src++; a.Idx = 0; a.Scroll = 0; return true; }
            if (dir == Direction.Right && RaCands(a).Count > 0) { a.Zone = 1; return true; }
            return false;
        }
        int n = RaCands(a).Count;
        int j = a.Idx;
        if (dir == Direction.Left) { if (a.Idx % RaCols == 0) { a.Zone = 0; return true; } j--; }
        else if (dir == Direction.Right) j = a.Idx % RaCols == RaCols - 1 ? a.Idx : a.Idx + 1;
        else if (dir == Direction.Up) j -= RaCols;
        else j += RaCols;
        if (j < 0 || j >= n || j == a.Idx) return false;
        a.Idx = j;
        return true;
    }

    private void RaActivate(RaState a, LibraryItem g)
    {
        if (a.D is not { } d) { Cue(Sfx.Bump); return; }
        string kind = RaKinds[a.Tab].Kind;
        if (a.Zone == 0)
        {
            string src = RaSources(a)[Math.Clamp(a.Src, 0, RaSources(a).Count - 1)];
            Cue(Sfx.Activate);
            if (src == "本地图片")
            {
                a.Local ??= RaLocalImages();
                a.Status = a.Local.Count == 0 ? "图片、视频、下载、桌面里没有图片" : "";
                if (a.Local.Count > 0) { a.Zone = 1; a.Idx = 0; }
                return;
            }
            if (src == "Steam 官方") { if (RaCands(a).Count > 0) { a.Zone = 1; a.Idx = 0; } return; }
            if (!a.Searched.Contains(kind) || d.Cands[kind].All(c => c.Url == null)) _ = RaSearch(a, g);
            else { a.Zone = 1; a.Idx = 0; }
            return;
        }
        // 网格：换成这张（还没保存，Y ✓ 才存）
        var cands = RaCands(a);
        if (a.Idx >= cands.Count) return;
        var c = cands[a.Idx];
        var list = d.Cands[kind];
        int at = list.FindIndex(x => x.Url == c.Url);
        if (at < 0) { list.Add(c); at = list.Count - 1; }
        d.Pick[kind] = at;
        Cue(Sfx.Positive);
    }

    /// <summary>SteamGridDB：按游戏名搜，取第一款的候选（三种图一起取），取好留在网格里。</summary>
    private async Task RaSearch(RaState a, LibraryItem g)
    {
        if (a.D is not { } d || a.Busy) return;
        a.Busy = true;
        a.Status = "正在获取…";
        RsPaintSoon();
        try
        {
            if (d.Matches.Count == 0 && d.Match < 0)
            {
                d.Query = g.Title;
                await AgSearch(d);
            }
            var match = d.Match >= 0 && d.Match < d.Matches.Count ? d.Matches[d.Match] : d.Matches.FirstOrDefault();
            if (match == null) a.Status = "SteamGridDB 上没找到这款游戏";
            else
            {
                d.Match = d.Matches.IndexOf(match);
                if (Str(match["source"]) == "sgdb") d.Sgdb = Str(match["id"]);
                d.SteamAppId ??= Str(match["steamAppId"]);
                var keepPick = d.Pick.ToDictionary(kv => kv.Key, kv => kv.Value);
                await AgLoadCandidates(d, match);
                // 取候选会把每种默认选成第一张：改回原来选着的（还没按 A 换）
                foreach (var kv in keepPick) d.Pick[kv.Key] = kv.Value;
                if (d.SteamAppId != null) await AgLoadOfficial(d);
                foreach (var (kind, _) in RaKinds) a.Searched.Add(kind);
                a.Status = "没有候选图";
                if (RaCands(a).Count > 0) { a.Zone = 1; a.Idx = 0; }
            }
        }
        catch (Exception ex) { Log.Warn($"art page search: {ex.Message}"); a.Status = "连不上 SteamGridDB"; }
        a.Busy = false;
        RsPaintSoon();
    }
}
