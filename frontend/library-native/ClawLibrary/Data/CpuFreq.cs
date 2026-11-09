// 实时 CPU 频率：和任务管理器的「速度」同一个算法 —— PDH 的 Processor Information(_Total) 里
// 「Processor Frequency」（标称 MHz）×「% Processor Performance」/ 100。后者是比率计数器，要两次采样之间才有值，
// 所以第一次 Sample() 之后 Mhz 还是 null，第二次起才有数。只在性能页开着时隔几秒采一次，开销是几十微秒。
using System.Runtime.InteropServices;

namespace ClawLibrary.Data;

public sealed class CpuFreq : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhValue value);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Double;
    }

    private const uint PdhFmtDouble = 0x00000200, PdhFmtNoCap100 = 0x00008000;
    private IntPtr _query, _perf, _base;
    private bool _failed;

    /// <summary>最近一次算出的频率（MHz）；还没两次采样或 PDH 不可用时是 null。</summary>
    public double? Mhz { get; private set; }

    public void Sample()
    {
        if (_failed) return;
        try
        {
            if (_query == IntPtr.Zero)
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0
                    || PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _perf) != 0
                    || PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _base) != 0)
                    throw new InvalidOperationException("PDH 计数器打不开");
                PdhCollectQueryData(_query);
                return;
            }
            if (PdhCollectQueryData(_query) != 0) return;
            if (PdhGetFormattedCounterValue(_perf, PdhFmtDouble | PdhFmtNoCap100, IntPtr.Zero, out var perf) != 0 || perf.Status > 1) return;
            if (PdhGetFormattedCounterValue(_base, PdhFmtDouble, IntPtr.Zero, out var nominal) != 0 || nominal.Status > 1) return;
            if (nominal.Double > 0) Mhz = nominal.Double * perf.Double / 100;
        }
        catch (Exception ex)
        {
            _failed = true;
            Log.Warn($"cpu freq: {ex.Message}");
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
