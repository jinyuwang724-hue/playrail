using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClawSleepAnimation;

// A bounded experiment using the existing suspend notice. It never suppresses
// a button, changes a power policy, or requests another suspend on resume.
internal sealed class SuspendAnimation : IDisposable
{
    private readonly Dispatcher ui;
    private readonly Action<string, object?> log;
    private readonly Thread thread;
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? observer;
    private HwndSource? source;
    private nint registration;
    private TaskCompletionSource? resume;
    private int busy;
    private DateTime ignoreUntil;
    [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterSuspendResumeNotification(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterSuspendResumeNotification(nint handle);

    public SuspendAnimation(Dispatcher ui, Action<string, object?> log)
    {
        this.ui = ui; this.log = log;
        thread = new Thread(Run) { IsBackground = true, Name = "Claw suspend notification" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!initialized.Task.Wait(5000)) throw new TimeoutException("Suspend observer did not start");
        initialized.Task.GetAwaiter().GetResult();
    }
    private void Run()
    {
        try
        {
            observer = Dispatcher.CurrentDispatcher;
            source = new HwndSource(new HwndSourceParameters("Claw suspend observer") { Width = 0, Height = 0, WindowStyle = 0 });
            source.AddHook(OnMessage);
            registration = RegisterSuspendResumeNotification(source.Handle, 0);
            if (registration == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            log("suspend-observer-ready", new { maxWaitMs = 1450, policyUnchanged = true });
            initialized.TrySetResult(); Dispatcher.Run();
        }
        catch (Exception ex) { initialized.TrySetException(ex); log("suspend-observer-error", ex.ToString()); }
        finally
        {
            if (registration != 0) UnregisterSuspendResumeNotification(registration);
            source?.Dispose();
        }
    }
    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x218) return 0;
        int type = wParam.ToInt32();
        if (type == 7 || type == 18)
        {
            ignoreUntil = DateTime.UtcNow.AddSeconds(3);
            resume?.TrySetResult(); log("resume-notice", new { type });
        }
        if (type != 4 || DateTime.UtcNow < ignoreUntil || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return 0;
        DateTime notice = DateTime.UtcNow;
        var blackReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); resume = resumed;
        log("suspend-notice", null);
        ui.BeginInvoke(async () =>
        {
            try
            {
                if (resumed.Task.IsCompleted || DateTime.UtcNow - notice > TimeSpan.FromMilliseconds(1200)) return;
                log("suspend-animation-start", new { noticeDelayMs = (DateTime.UtcNow - notice).TotalMilliseconds });
                await SleepOverlay.PlayAsync(false, null, () =>
                {
                    log("suspend-animation-black", new { noticeDelayMs = (DateTime.UtcNow - notice).TotalMilliseconds });
                    blackReady.TrySetResult(true);
                }, resumed.Task);
                log("suspend-animation-cleared", null);
            }
            catch (Exception ex) { log("suspend-animation-error", ex.ToString()); }
            finally { blackReady.TrySetResult(false); Interlocked.Exchange(ref busy, 0); }
        });
        bool completed = blackReady.Task.Wait(1450) && blackReady.Task.Result;
        log("suspend-acknowledged", new { completed, elapsedMs = (DateTime.UtcNow - notice).TotalMilliseconds });
        // Windows controls the transition and enforces its own timeout too.
        return 1;
    }
    public void Dispose()
    {
        resume?.TrySetResult(); observer?.BeginInvokeShutdown(DispatcherPriority.Send);
    }
    public async Task TestAsync()
    {
        if (observer == null) throw new InvalidOperationException("Observer not ready");
        await observer.InvokeAsync(() => { bool handled = false; OnMessage(0, 0x218, 4, 0, ref handled); });
        await Task.Delay(200);
        await observer.InvokeAsync(() => { bool handled = false; OnMessage(0, 0x218, 18, 0, ref handled); });
        await Task.Delay(200);
    }
    public async Task TestBlockedUiAsync()
    {
        if (observer == null) throw new InvalidOperationException("Observer not ready");
        var pending = Task.Run(() => observer.Invoke(() => { bool handled = false; OnMessage(0, 0x218, 4, 0, ref handled); }));
        // Only this explicit synthetic test stalls its own UI; no Windows power
        // broadcast or machine suspend is generated.
        Thread.Sleep(1900);
        await pending;
        await observer.InvokeAsync(() => { bool handled = false; OnMessage(0, 0x218, 18, 0, ref handled); });
        await Task.Delay(250);
    }
}
