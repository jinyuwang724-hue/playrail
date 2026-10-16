// 去红：MSI 的示意图把要高亮的区域涂成半透明红（≈ 235,55,35），把红去掉得到干净线稿；红色区域本身就是那块的形状。
// clean <src.png> <dst.png>                         去掉红色填充，线和红混在一起的像素改成灰
// red <src.png> <regions.txt> <name> <x,y> [...]    从种子点把红色那块描成闭合多边形，追加到 regions.txt（面写 side）
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Clean
{
    public static bool IsRed(byte b, byte g, byte r, byte a) => a > 8 && r > 120 && r > g + 90 && r > b + 90;

    public static void Run(string src, string dst)
    {
        var bmp = new FormatConvertedBitmap(new BitmapImage(new Uri(Path.GetFullPath(src))), PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        int removed = 0, greyed = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            if (IsRed(b, g, r, a))
            {
                // 纯红填充：透明；线压在红上（偏灰偏亮）：按绿蓝通道还原成灰
                if (g < 110) { px[i + 3] = 0; removed++; }
                else { byte v = (byte)Math.Min(255, (g + b) / 2 + 20); px[i] = px[i + 1] = px[i + 2] = v; greyed++; }
            }
            else if (a > 0 && r > Math.Max(g, b) + 12)
            {
                // 线的抗锯齿边和红混过，带一点粉：去色成同亮度的灰
                byte v = (byte)Math.Min(255, (g + b) / 2 + (r - (g + b) / 2) / 4);
                px[i] = px[i + 1] = px[i + 2] = v; greyed++;
            }
        }
        var outBmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(outBmp));
        using var fs = File.Create(dst);
        enc.Save(fs);
        Console.WriteLine($"{dst}: removed {removed}, greyed {greyed}");
    }
}
