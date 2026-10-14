// 封面图缓存：LoadedImageSurface 在后台线程按显示尺寸解码（不进 UI 线程），同一张图所有卡共用；
// 引用计数归零后留在 LRU 里，超过上限才释放。文件不存在 / 解码失败只记日志，卡片保持底色。
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ClawLibrary.Stage;

public sealed class ImageCache
{
    private sealed class Entry
    {
        public required LoadedImageSurface Surface;
        public int Refs;
        public long LastUse;
        public bool Failed;
    }

    private readonly Dictionary<string, Entry> _map = new();
    private long _tick;
    private int _logged;
    private const int KeepUnused = 90;

    public int Count => _map.Count;
    public int Pending;
    public event Action? AllLoaded;

    /// <summary>取一张图。maxPx 是解码后的长边上限（物理像素）。</summary>
    public LoadedImageSurface? Acquire(string path, int maxW, int maxH)
    {
        var key = $"{path}|{maxW}x{maxH}";
        if (!_map.TryGetValue(key, out var e))
        {
            if (!File.Exists(path)) { Log.Warn($"image missing: {path}"); return null; }
            LoadedImageSurface surface;
            try
            {
                surface = LoadedImageSurface.StartLoadFromUri(new Uri(path), new Size(maxW, maxH));
            }
            catch (Exception ex) { Log.Warn($"image load {path}: {ex.Message}"); return null; }
            e = new Entry { Surface = surface };
            Pending++;
            var entry = e;
            surface.LoadCompleted += (s, a) =>
            {
                Pending--;
                if (a.Status != LoadedImageSourceLoadStatus.Success) { entry.Failed = true; Log.Warn($"image decode {a.Status}: {path}"); }
                else if (_logged++ < 12) Log.Info($"decoded {Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)} want {maxW}x{maxH} got {s.DecodedPhysicalSize.Width}x{s.DecodedPhysicalSize.Height} (dip {s.DecodedSize.Width}x{s.DecodedSize.Height}, natural {s.NaturalSize.Width}x{s.NaturalSize.Height})");
                if (Pending == 0) AllLoaded?.Invoke();
            };
            _map[key] = e;
        }
        e.Refs++;
        e.LastUse = ++_tick;
        return e.Failed ? null : e.Surface;
    }

    public void Release(LoadedImageSurface? surface)
    {
        if (surface == null) return;
        foreach (var e in _map.Values)
            if (ReferenceEquals(e.Surface, surface)) { e.Refs--; break; }
        Trim();
    }

    private void Trim()
    {
        int unused = 0;
        foreach (var e in _map.Values) if (e.Refs <= 0) unused++;
        if (unused <= KeepUnused) return;
        foreach (var (k, e) in _map.Where(kv => kv.Value.Refs <= 0).OrderBy(kv => kv.Value.LastUse).Take(unused - KeepUnused).ToList())
        {
            _map.Remove(k);
            try { e.Surface.Dispose(); } catch { }
        }
    }
}
