// 游戏模式下从游戏库（或盖在别的程序上的控制中心 / 切换器）回到某个窗口：
// 先把那个窗口的 DWM 实时画面按它原来的位置和大小淡进来，淡满了再把游戏库收到它后面。
// 这样窗口是「浮回来」的，不是一下子换掉；画面位置和尺寸就是窗口本身，不缩放不挪位。
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private DwmThumb? _revealThumb;
    private int _revealVersion;
    private const double RevealMs = 200;

    /// <summary>淡入 target 的画面后调用 done。target 不在 / 最小化 / 降低动态效果时直接调用。</summary>
    internal void RevealWindowThen(nint target, Action done) => FadeWindow(target, true, done);

    /// <summary>反过来：盖到 target 上面唤出时，先让它的画面原样留着，再在控制中心进场的同时淡掉，不是一下子没了。</summary>
    internal void FadeWindowAway(nint target) => FadeWindow(target, false, () => { });

    private void FadeWindow(nint target, bool reveal, Action done)
    {
        int v = ++_revealVersion;
        _revealThumb?.Dispose(); _revealThumb = null;
        if (Reduced || target == 0 || !OpenWindows.Alive(target) || ToolboxWindow.IsIconic(target) || !GetWindowRect(target, out var r))
        { done(); return; }
        _revealThumb = DwmThumb.Register(WinRT.Interop.WindowNative.GetWindowHandle(_window), target);
        if (_revealThumb == null) { done(); return; }
        var thumb = _revealThumb;
        long start = NowMs;
        var timer = _dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.Tick += (_, _) =>
        {
            if (v != _revealVersion) { timer.Stop(); return; }
            double t = Math.Clamp((NowMs - start) / RevealMs, 0, 1);
            double ease = 1 - Math.Pow(1 - t, 3);
            thumb.Place(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, reveal ? ease : 1 - ease);
            if (t < 1) return;
            timer.Stop();
            done();
            // 淡入的那种：窗口真的到前面以后再拆，免得中间露一帧游戏库
            After(reveal ? 120 : 0, () => { if (v == _revealVersion) { _revealThumb?.Dispose(); _revealThumb = null; } });
        };
        thumb.Place(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, reveal ? 0 : 1);
        timer.Start();
    }
}
