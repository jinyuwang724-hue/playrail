// 手柄线稿素材工具（游戏库原生版 Assets/Pad 用）。
//   clean <src.png> <dst.png>
//       去掉 MSI 示意图里的半透明红色填充，得到干净线稿（见 Clean.cs）。
//   regions <spec.txt> <regions.txt> <preview-dir>
//       按 spec 每行「名字 面 图 方式 种子x,种子y 外扩 最小y」描高亮区域：在 4 倍放大的图上从种子点漫水填充
//       （clear = 透明像素、碰到线条停；solid = 不透明的实心块；red = MSI 涂红的那块），外扩一点盖住线的半边，
//       描外轮廓 → Douglas–Peucker 抽稀 → Chaikin 平滑，输出原图像素坐标的闭合多边形；运行时用 Win2D 抗锯齿填充。
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

const int K = 4;

if (args[0] == "clean") { Clean.Run(args[1], args[2]); return; }

string specDir = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
var sb = new StringBuilder("# 手柄线稿高亮区域：名字 面 之后是该面原图像素坐标的闭合轮廓 x,y（tools/PadRegions 按 regions.spec 生成）\n");
var images = new Dictionary<string, (byte[] Px, int W, int H)>();
var previews = new Dictionary<string, List<List<Point>>>();
var faceImage = new Dictionary<string, string>();
foreach (var raw in File.ReadAllLines(args[1]))
{
    var line = raw.Trim();
    if (line.Length == 0 || line[0] == '#') continue;
    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    string name = f[0], face = f[1], image = Path.Combine(specDir, f[2]), mode = f[3];
    var seed = f[4].Split(',').Select(int.Parse).ToArray();
    int grow = int.Parse(f[5]), minY = int.Parse(f[6]);
    if (!images.TryGetValue(image, out var img)) images[image] = img = Load(image);
    faceImage[face] = image;
    var m = Fill(img.Px, img.W, img.H, seed[0] * K, seed[1] * K, mode, grow, minY * K);
    var pts = Smooth(Simplify(Outline(m, img.W, img.H), 1.2), 2).Select(p => new Point(p.X / K, p.Y / K)).ToList();
    sb.Append(name).Append(' ').Append(face);
    foreach (var p in pts) sb.Append(' ').Append(p.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(',').Append(p.Y.ToString("0.##", CultureInfo.InvariantCulture));
    sb.Append('\n');
    (previews.TryGetValue(face, out var list) ? list : previews[face] = new()).Add(pts);
    Console.WriteLine($"{name} ({face}): {pts.Count} pts");
}
File.WriteAllText(args[2], sb.ToString(), new UTF8Encoding(false));

// 预览：每个面一张，线稿 2 倍 + 区域半透明青色
Directory.CreateDirectory(args[3]);
foreach (var (face, polys) in previews)
{
    var src = new BitmapImage(new Uri(faceImage[face]));
    int w = src.PixelWidth * 2, h = src.PixelHeight * 2;
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 34)), null, new Rect(0, 0, w, h));
        dc.DrawImage(src, new Rect(0, 0, w, h));
        var brush = new SolidColorBrush(Color.FromArgb(110, 100, 220, 220));
        foreach (var pts in polys)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(pts[0].X * 2, pts[0].Y * 2), true, true);
                c.PolyLineTo(pts.Skip(1).Select(p => new Point(p.X * 2, p.Y * 2)).ToList(), true, true);
            }
            dc.DrawGeometry(brush, null, g);
        }
    }
    var rt = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    rt.Render(dv);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(rt));
    using var fs = File.Create(Path.Combine(args[3], $"preview-{face}.png"));
    enc.Save(fs);
}

// 图放大 K 倍（高质量插值）后取 BGRA 像素
static (byte[] Px, int W, int H) Load(string path)
{
    var src = new BitmapImage(new Uri(Path.GetFullPath(path)));
    int w = src.PixelWidth * K, h = src.PixelHeight * K;
    var dv = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
    using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w, h));
    var rt = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    rt.Render(dv);
    var px = new byte[w * h * 4];
    rt.CopyPixels(px, w * 4, 0);
    return (px, w, h);
}

static bool[] Fill(byte[] px, int w, int h, int sx, int sy, string mode, int grow, int minY)
{
    // 像素是预乘过的 BGRA：红块判断要先除回 alpha
    bool Open(int i)
    {
        byte a = px[i * 4 + 3];
        return mode switch
        {
            "clear" => a < 70,
            "solid" => a >= 70,
            _ => a > 8 && Clean.IsRed((byte)Math.Min(255, px[i * 4] * 255 / a), (byte)Math.Min(255, px[i * 4 + 1] * 255 / a), (byte)Math.Min(255, px[i * 4 + 2] * 255 / a), a),
        };
    }
    var m = new bool[w * h];
    var q = new Queue<int>();
    int s0 = sy * w + sx;
    if (!Open(s0)) throw new InvalidOperationException($"种子点 {sx / K},{sy / K} 不在区域里");
    m[s0] = true; q.Enqueue(s0);
    while (q.Count > 0)
    {
        int i = q.Dequeue(), x = i % w, y = i / w;
        foreach (int j in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
            if (j >= 0 && !m[j] && Open(j)) { m[j] = true; q.Enqueue(j); }
    }
    for (int g = 0; g < grow; g++)
    {
        var n = (bool[])m.Clone();
        for (int y = 1; y < h - 1; y++) for (int x = 1; x < w - 1; x++) { int i = y * w + x; if (!m[i] && (m[i - 1] || m[i + 1] || m[i - w] || m[i + w])) n[i] = true; }
        m = n;
    }
    Array.Clear(m, 0, Math.Min(m.Length, minY * w));
    return m;
}

// Moore 邻域描外边界（顺时针），返回像素中心点
static List<Point> Outline(bool[] m, int w, int h)
{
    int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 }, dy = { 0, 1, 1, 1, 0, -1, -1, -1 };
    bool On(int a, int b) => a >= 0 && b >= 0 && a < w && b < h && m[b * w + a];
    int start = Array.IndexOf(m, true), sx = start % w, sy = start / w, x = sx, y = sy, back = 4;
    var pts = new List<Point>();
    do
    {
        pts.Add(new Point(x + 0.5, y + 0.5));
        int found = -1;
        for (int k = 1; k <= 8; k++) { int d = (back + k) % 8; if (On(x + dx[d], y + dy[d])) { found = d; break; } }
        if (found < 0) break;
        int prev = (found + 7) % 8, bx = x + dx[prev], by = y + dy[prev];
        x += dx[found]; y += dy[found];
        for (int d = 0; d < 8; d++) if (x + dx[d] == bx && y + dy[d] == by) { back = d; break; }
    } while (!(x == sx && y == sy) && pts.Count < 400000);
    return pts;
}

// Douglas–Peucker 抽稀
static List<Point> Simplify(List<Point> p, double eps)
{
    if (p.Count < 3) return p;
    var keep = new bool[p.Count]; keep[0] = keep[^1] = true;
    var st = new Stack<(int A, int B)>(); st.Push((0, p.Count - 1));
    while (st.Count > 0)
    {
        var (a, b) = st.Pop();
        double dmax = 0; int idx = -1;
        for (int i = a + 1; i < b; i++)
        {
            double ex = p[b].X - p[a].X, ey = p[b].Y - p[a].Y, len = Math.Sqrt(ex * ex + ey * ey);
            double dist = len < 1e-9 ? (p[i] - p[a]).Length : Math.Abs(ey * p[i].X - ex * p[i].Y + p[b].X * p[a].Y - p[b].Y * p[a].X) / len;
            if (dist > dmax) { dmax = dist; idx = i; }
        }
        if (dmax > eps && idx > 0) { keep[idx] = true; st.Push((a, idx)); st.Push((idx, b)); }
    }
    return p.Where((_, i) => keep[i]).ToList();
}

// Chaikin 切角：闭合折线变平滑曲线
static List<Point> Smooth(List<Point> p, int rounds)
{
    for (int r = 0; r < rounds; r++)
    {
        var n = new List<Point>(p.Count * 2);
        for (int i = 0; i < p.Count; i++)
        {
            Point a = p[i], b = p[(i + 1) % p.Count];
            n.Add(new Point(a.X * 0.75 + b.X * 0.25, a.Y * 0.75 + b.Y * 0.25));
            n.Add(new Point(a.X * 0.25 + b.X * 0.75, a.Y * 0.25 + b.Y * 0.75));
        }
        p = n;
    }
    return p;
}
