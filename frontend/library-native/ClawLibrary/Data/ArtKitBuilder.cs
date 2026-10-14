// 封面套件（tools/art-kit.py 的 C# 版，用系统自带的 WIC 解码 / 缩放 / 编码，不需要 Python）：
// 从竖封面（600x900）和无字大背景（hero）裁出墙上各种卡片要用的图，尺寸与 art-kit.py 相同：
//   p  竖卡（2x3、3x4、2x4）：竖封面 400x600
//   h  横卡：背景图 1600 宽，前端按焦点取景
//   s  小方卡：背景图按焦点裁方 360
//   x  大方卡与展开的 6x6：同上 900
//   v / n  没有竖封面时的兜底：背景图按焦点裁 2:3（440x660）和约 1:2（320x650）
// 背景图一律从正中裁（不再用种子清单里的焦点 fx）；主色 c 用种子清单里的值。
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClawLibrary.Data;

public static class ArtKitBuilder
{
    private const int KitVersion = 3;   // 2：加 q（竖封面原尺寸，放大时用的同一张图）；3：方图 / 兜底竖图一律从正中裁

    public static ArtKit? TryLoad(string dir)
    {
        try
        {
            var kitPath = Path.Combine(dir, "kit.json");
            if (!File.Exists(kitPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(kitPath));
            var r = doc.RootElement;
            if (!r.TryGetProperty("version", out var ver) || ver.GetInt32() != KitVersion) return null;
            bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True && File.Exists(Path.Combine(dir, k + ".jpg"));
            double? D(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            var kit = new ArtKit { Dir = dir, P = B("p"), Q = B("q"), G = B("g"), V = B("v"), N = B("n"), H = B("h"), S = B("s"), X = B("x"), Fx = D("fx"), Ha = D("ha") };
            if (r.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.String) kit.C = c.GetString();
            return kit;
        }
        catch (Exception ex) { Log.Warn($"kit.json {dir}: {ex.Message}"); return null; }
    }

    public static async Task<ArtKit?> BuildAsync(string dir, string? portrait, string? hero, double fx, string? color)
    {
        if (portrait == null && hero == null) return null;
        fx = 0.5;   // 从正中裁：按焦点取景常把主体裁掉
        Directory.CreateDirectory(dir);
        var kit = new Dictionary<string, object> { ["version"] = KitVersion };
        if (portrait != null)
        {
            try
            {
                await ResizeAsync(portrait, Path.Combine(dir, "p.jpg"), 400, 600, null);
                kit["p"] = true;
                // q：同一张竖封面的原尺寸（600x900），卡片放大时用，和小卡是同一张图
                await ResizeAsync(portrait, Path.Combine(dir, "q.jpg"), 600, 900, null);
                kit["q"] = true;
            }
            catch (Exception ex) { Log.Warn($"art p {portrait}: {ex.Message}"); }
        }
        if (hero != null)
        {
            try
            {
                var (w, h) = await SizeAsync(hero);
                await ResizeAsync(hero, Path.Combine(dir, "h.jpg"), 1600, (int)Math.Round(h * 1600.0 / w), null);
                // g：同一张背景图的大尺寸（高 1080，不超过原图），横卡放大成方卡时用
                int gh = Math.Min(h, 1080);
                await ResizeAsync(hero, Path.Combine(dir, "g.jpg"), (int)Math.Round(w * gh / (double)h), gh, null);
                kit["g"] = true;
                await ResizeAsync(hero, Path.Combine(dir, "s.jpg"), 360, 360, fx);
                await ResizeAsync(hero, Path.Combine(dir, "x.jpg"), 900, 900, fx);
                if (portrait == null)
                {
                    await ResizeAsync(hero, Path.Combine(dir, "v.jpg"), 440, 660, fx);
                    await ResizeAsync(hero, Path.Combine(dir, "n.jpg"), 320, 650, fx);
                    kit["v"] = true; kit["n"] = true;
                }
                kit["h"] = true; kit["s"] = true; kit["x"] = true;
                kit["fx"] = fx;
                kit["ha"] = Math.Round(w / (double)h, 3);
            }
            catch (Exception ex) { Log.Warn($"art hero {hero}: {ex.Message}"); }
        }
        if (color != null) kit["c"] = color;
        await File.WriteAllTextAsync(Path.Combine(dir, "kit.json"), Serialize(kit));
        return TryLoad(dir);
    }

    private static string Serialize(Dictionary<string, object> kit)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (k, v) in kit)
                switch (v)
                {
                    case bool b: w.WriteBoolean(k, b); break;
                    case int i: w.WriteNumber(k, i); break;
                    case double d: w.WriteNumber(k, d); break;
                    case string s: w.WriteString(k, s); break;
                }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<(int w, int h)> SizeAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var dec = await BitmapDecoder.CreateAsync(stream);
        return ((int)dec.PixelWidth, (int)dec.PixelHeight);
    }

    /// <summary>缩放到 w×h。focal 为 null 时整张缩放（宽高比按目标）；否则按焦点从原图裁出 w:h 的整块（高度用满）再缩。</summary>
    private static async Task ResizeAsync(string src, string dst, int w, int h, double? focal)
    {
        var file = await StorageFile.GetFileFromPathAsync(src);
        using var input = await file.OpenReadAsync();
        var dec = await BitmapDecoder.CreateAsync(input);
        int sw = (int)dec.PixelWidth, sh = (int)dec.PixelHeight;
        var t = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        if (focal is double fx)
        {
            double aspect = w / (double)h;
            int cw = Math.Min(sw, (int)Math.Round(sh * aspect));
            int ch = (int)Math.Round(cw / aspect);
            int left = Math.Min(Math.Max(0, (int)Math.Round(fx * sw - cw / 2.0)), sw - cw);
            int top = (sh - ch) / 2;
            double k = w / (double)cw;
            t.ScaledWidth = (uint)Math.Max(1, Math.Round(sw * k));
            t.ScaledHeight = (uint)Math.Max(1, Math.Round(sh * k));
            uint bx = (uint)Math.Round(left * k), by = (uint)Math.Round(top * k);
            bx = Math.Min(bx, t.ScaledWidth - (uint)w);
            by = Math.Min(by, t.ScaledHeight - (uint)Math.Min(h, (int)t.ScaledHeight));
            t.Bounds = new BitmapBounds { X = bx, Y = by, Width = (uint)w, Height = (uint)Math.Min(h, (int)t.ScaledHeight - (int)by) };
        }
        else
        {
            t.ScaledWidth = (uint)w;
            t.ScaledHeight = (uint)h;
        }
        var pixels = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, t,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var bytes = pixels.DetachPixelData();
        uint ow = focal != null ? t.Bounds.Width : t.ScaledWidth;
        uint oh = focal != null ? t.Bounds.Height : t.ScaledHeight;
        var tmp = dst + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
        using (var output = fs.AsRandomAccessStream())
        {
            var props = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.9f, Windows.Foundation.PropertyType.Single) };
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, ow, oh, 96, 96, bytes);
            await enc.FlushAsync();
        }
        File.Move(tmp, dst, true);
    }
}
