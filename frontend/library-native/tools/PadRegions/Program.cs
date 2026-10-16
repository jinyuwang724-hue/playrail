// 手柄线稿高亮区域：在 4 倍放大的线稿上从种子点漫水填充（碰到线条停），外扩一点盖住线的半边，
// 描出外轮廓 → 抽稀 → Chaikin 平滑，输出原图像素坐标的闭合多边形（regions.txt），运行时用 Win2D 抗锯齿填充。
// 用法：tracer <front.png> <back.png> <regions.txt> <preview.png>
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

const int K = 4;
var front = Load(args[0], out int fw, out int fh);
var back = Load(args[1], out int bw, out int bh);
var jobs = new (string Name, bool Back, int X, int Y, bool Clear, int Grow, int MinY)[]
{
    ("GripL", false, 40, 240, true, 3, 150), ("GripR", false, 660, 240, true, 3, 150),
    ("LB", false, 75, 22, true, 3, 0), ("RB", false, 625, 22, true, 3, 0),
    ("M1", true, 112, 125, false, 2, 0), ("M2", true, 586, 125, false, 2, 0),
};
var sb = new StringBuilder("# 手柄线稿高亮区域：名字 面(front/back) 之后是原图像素坐标的闭合轮廓 x,y（tools 里的 tracer 从线稿生成）\n");
var polys = new List<(bool Back, List<Point> Pts)>();
foreach (var j in jobs)
{
    var px = j.Back ? back : front; int w = (j.Back ? bw : fw) * K, h = (j.Back ? bh : fh) * K;
    var m = Fill(px, w, h, j.X * K, j.Y * K, j.Clear, j.Grow, j.MinY * K);
    var pts = Smooth(Simplify(Outline(m, w, h), 1.2), 2).Select(p => new Point(p.X / K, p.Y / K)).ToList();
    sb.Append(j.Name).Append(' ').Append(j.Back ? "back" : "front");
    foreach (var p in pts) sb.Append(' ').Append(p.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(',').Append(p.Y.ToString("0.##", CultureInfo.InvariantCulture));
    sb.Append('\n');
    polys.Add((j.Back, pts));
    Console.WriteLine($"{j.Name}: {pts.Count} pts");
}
File.WriteAllText(args[2], sb.ToString(), new UTF8Encoding(false));

// 预览：线稿 2 倍 + 区域半透明青色
var dv = new DrawingVisual();
using (var dc = dv.RenderOpen())
{
    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 34)), null, new Rect(0, 0, fw * 2, fh * 4));
    dc.DrawImage(new BitmapImage(new Uri(Path.GetFullPath(args[0]))), new Rect(0, 0, fw * 2, fh * 2));
    dc.DrawImage(new BitmapImage(new Uri(Path.GetFullPath(args[1]))), new Rect(0, fh * 2, fw * 2, fh * 2));
    var brush = new SolidColorBrush(Color.FromArgb(110, 100, 220, 220));
    foreach (var (isBack, pts) in polys)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double oy = isBack ? fh * 2 : 0;
            c.BeginFigure(new Point(pts[0].X * 2, pts[0].Y * 2 + oy), true, true);
            c.PolyLineTo(pts.Skip(1).Select(p => new Point(p.X * 2, p.Y * 2 + oy)).ToList(), true, true);
        }
        dc.DrawGeometry(brush, null, g);
    }
}
var rt = new RenderTargetBitmap(fw * 2, fh * 4, 96, 96, PixelFormats.Pbgra32);
rt.Render(dv);
var enc = new PngBitmapEncoder();
enc.Frames.Add(BitmapFrame.Create(rt));
using (var fs = File.Create(args[3])) enc.Save(fs);

// 线稿放大 K 倍（高质量插值）后取 BGRA 像素
static byte[] Load(string path, out int w, out int h)
{
    var src = new BitmapImage(new Uri(Path.GetFullPath(path)));
    w = src.PixelWidth; h = src.PixelHeight;
    var dv = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
    using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w * K, h * K));
    var rt = new RenderTargetBitmap(w * K, h * K, 96, 96, PixelFormats.Pbgra32);
    rt.Render(dv);
    var px = new byte[w * K * h * K * 4];
    rt.CopyPixels(px, w * K * 4, 0);
    return px;
}

static bool[] Fill(byte[] px, int w, int h, int sx, int sy, bool clear, int grow, int minY)
{
    bool Open(int i) => clear ? px[i * 4 + 3] < 70 : px[i * 4 + 3] >= 70;
    var m = new bool[w * h];
    var q = new Queue<int>();
    int s0 = sy * w + sx; m[s0] = true; q.Enqueue(s0);
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
