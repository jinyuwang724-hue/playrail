// DWM 合成计数（DwmGetCompositionTimingInfo）：全屏时屏幕上只有我们在动，合成帧数 ≈ 我们的出帧数，用来量帧率与掉帧。
using System.Runtime.InteropServices;

namespace ClawLibrary.Stage;

public static unsafe class Dwm
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TimingInfo
    {
        public uint cbSize;
        public uint rateRefreshNum, rateRefreshDen;
        public ulong qpcRefreshPeriod;
        public uint rateComposeNum, rateComposeDen;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, TimingInfo* info);

    public static uint InfoSize => (uint)sizeof(TimingInfo);

    public readonly record struct Sample(long Ticks, ulong Frame, ulong Refresh, ulong Missed, ulong Dropped, double RefreshHz);

    public static Sample Read()
    {
        var info = new TimingInfo { cbSize = (uint)sizeof(TimingInfo) };
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, &info) != 0) return new Sample(now, 0, 0, 0, 0, 0);
        }
        catch { return new Sample(now, 0, 0, 0, 0, 0); }
        double hz = info.rateRefreshDen != 0 ? info.rateRefreshNum / (double)info.rateRefreshDen : 0;
        return new Sample(now, info.cFrame, info.cRefresh, info.cFramesMissed, info.cFramesDropped, hz);
    }

    /// <summary>两次读数之间：合成帧率与掉帧（cFramesMissed + cFramesDropped 的增量）。</summary>
    public static (double Fps, long Missed) Rate(Sample a, Sample b)
    {
        double s = (b.Ticks - a.Ticks) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (s <= 0) return (0, 0);
        return ((b.Frame - a.Frame) / s, (long)(b.Missed - a.Missed + b.Dropped - a.Dropped));
    }
}
