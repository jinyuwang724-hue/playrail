// 封面图缓存：LoadedImageSurface 在后台线程按显示尺寸解码（不进 UI 线程），同一张图所有卡共用；
// 引用计数归零后留在 LRU 里，超过上限才释放。文件不存在 / 解码失败只记日志，卡片保持底色。
// 记录每张图解码好了没有：换图时先在旧图下面等新图解码完再淡入，不出现空白帧。
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ClawLibrary.Stage;

public sealed class ImageCache
{
    private sealed class Entry
    {
        public required string Key;
        public required LoadedImageSurface Surface;
        public int Refs;
        public long LastUse;
        public bool Failed, Loaded;
        public List<Action>? Waiters;
    }

    private readonly Dictionary<string, Entry> _map = new();
    private readonly Dictionary<LoadedImageSurface, Entry> _bySurface = new();
    private long _tick;
    private int _logged;
    private const int KeepUnused = 90;

    public int Count => _map.Count;
    public int Pending;
    public event Action? AllLoaded;

    /// <summary>取一张图（maxW × maxH 是解码尺寸上限，物理像素）。</summary>
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
            e = new Entry { Key = key, Surface = surface };
            Pending++;
            var entry = e;
            surface.LoadCompleted += (s, a) =>
            {
                Pending--;
                if (a.Status != LoadedImageSourceLoadStatus.Success) { entry.Failed = true; Log.Warn($"image decode {a.Status}: {path}"); }
                else
                {
                    entry.Loaded = true;
                    if (_logged++ < 6) Log.Info($"decoded {Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)} {s.DecodedPhysicalSize.Width}x{s.DecodedPhysicalSize.Height}");
                }
                var waiters = entry.Waiters;
                entry.Waiters = null;
                if (waiters != null) foreach (var w in waiters) { try { w(); } catch (Exception ex) { Log.Error("image waiter", ex); } }
                if (Pending == 0) AllLoaded?.Invoke();
            };
            _map[key] = e;
            _bySurface[surface] = e;
        }
        e.Refs++;
        e.LastUse = ++_tick;
        return e.Failed ? null : e.Surface;
    }

    /// <summary>墙上已经解码过同一张图（任意解码尺寸，宽度够用）就直接借来用，不再解码一遍。</summary>
    public LoadedImageSurface? AcquireReuse(string path, int maxW, int maxH)
    {
        string prefix = path + "|";
        foreach (var e in _map.Values)
            if (e.Loaded && !e.Failed && e.Key.StartsWith(prefix, StringComparison.Ordinal) && e.Surface.DecodedPhysicalSize.Width >= maxW * 0.9)
            {
                e.Refs++;
                e.LastUse = ++_tick;
                return e.Surface;
            }
        return Acquire(path, maxW, maxH);
    }

    /// <summary>同一张图再多一个引用（卡面重建时把正在显示的图接过去）。</summary>
    public void AddRef(LoadedImageSurface? surface)
    {
        if (surface != null && _bySurface.TryGetValue(surface, out var e)) { e.Refs++; e.LastUse = ++_tick; }
    }

    public bool IsLoaded(LoadedImageSurface? surface) => surface != null && _bySurface.TryGetValue(surface, out var e) && e.Loaded;

    /// <summary>解码好了（或失败了）就调 action；已经好了立刻调。</summary>
    public void WhenLoaded(LoadedImageSurface surface, Action action)
    {
        if (!_bySurface.TryGetValue(surface, out var e) || e.Loaded || e.Failed) { action(); return; }
        (e.Waiters ??= new()).Add(action);
    }

    public void Release(LoadedImageSurface? surface)
    {
        if (surface == null || !_bySurface.TryGetValue(surface, out var e)) return;
        e.Refs--;
        Trim();
    }

    private void Trim()
    {
        int unused = 0;
        foreach (var e in _map.Values) if (e.Refs <= 0) unused++;
        if (unused <= KeepUnused) return;
        foreach (var e in _map.Values.Where(v => v.Refs <= 0 && v.Waiters == null).OrderBy(v => v.LastUse).Take(unused - KeepUnused).ToList())
        {
            _map.Remove(e.Key);
            _bySurface.Remove(e.Surface);
            try { e.Surface.Dispose(); } catch { }
        }
    }
}
