using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClawSleepAnimation;

internal sealed class ButtonMonitor : IDisposable
{
    private readonly Application app;
    private readonly string state;
    private PowerTrace? trace;
    private readonly PowerButtonGate gate = new(Stopwatch.Frequency);
    private readonly SemaphoreSlim operation = new(1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly EventWaitHandle stop = new(false, EventResetMode.ManualReset, @"Local\ClawSleepAnimation.Stop");
    private readonly RegisteredWaitHandle stopping;
    private readonly HwndSource messages;
    private readonly nint displayRegistration, resumeRegistration;
    private PowerLease? lease;
    private AnimationControl? control;
    private AnimationOptions options = new();
    private EnhancedSleep? enhanced;
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
    private TaskCompletionSource? resumed, displayOff, exitFinish;
    private volatile bool displayOn = true;
    private bool disposed, transitioning, wakeAnimating, pendingResume;
    private long lastResume;
    private string? captureError;
    [DllImport("user32.dll", SetLastError=true)] private static extern nint RegisterPowerSettingNotification(nint window, ref Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(nint registration);
    [DllImport("user32.dll", SetLastError=true)] private static extern nint RegisterSuspendResumeNotification(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterSuspendResumeNotification(nint registration);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    public ButtonMonitor(Application app, string state)
    {
        this.app = app; this.state = state;
        messages = new HwndSource(new HwndSourceParameters("Claw power animation lifecycle") { Width = 0, Height = 0, WindowStyle = 0 });
        messages.AddHook(OnMessage);
        Guid display = DisplayPower.Session;
        displayRegistration = RegisterPowerSettingNotification(messages.Handle, ref display, 0);
        resumeRegistration = RegisterSuspendResumeNotification(messages.Handle, 0);
        if (displayRegistration == 0 || resumeRegistration == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (!WTSRegisterSessionNotification(messages.Handle, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        stop.Reset();
        stopping = ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => app.Dispatcher.BeginInvoke(Shutdown), null, Timeout.Infinite, true);
        timer.Tick += async (_, _) =>
        {
            if (!operation.Wait(0)) return;
            try
            {
                if (lease != null && (!lease.Healthy || trace == null || trace.Completion.IsCompleted))
                {
                    StopCapture(); options = options with { SuspendEnabled = false }; options.Save(state);
                    captureError = "已恢复原电源键操作，可重新开启动画";
                }
                lease?.Heartbeat();
                enhanced?.Heartbeat();
                if (!transitioning) await ReconcileAsync();
            }
            catch (Exception ex) { Program.Log("monitor-health-error", ex.Message); StopCapture(); }
            finally { operation.Release(); }
        };
    }
    public async Task StartAsync()
    {
        enhanced = new EnhancedSleep(state);
        enhanced.WakeCompleted += () => app.Dispatcher.BeginInvoke(() => { if (displayOn) { pendingResume = true; TryWake(); } });
        await enhanced.StartAsync();
        if (!PowerLease.Recover(state)) throw new InvalidOperationException("另一会话正在使用电源键动画");
        options = AnimationOptions.Load(state); options.Save(state);
        await SleepOverlay.WarmUpAsync(); await ReconcileAsync();
        control = new AnimationControl(request => app.Dispatcher.InvokeAsync(() => HandleAsync(request)).Task.Unwrap());
        timer.Start();
        RecordMonitor();
        Program.Log("monitor-ready", Status());
    }
    private void RecordMonitor()
    {
        File.WriteAllText(Path.Combine(state, "monitor.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, guardPid = lease?.GuardPid,
            updated = DateTime.UtcNow, mode = "power-button-animation", originalPolicy = lease?.Original, active = lease != null }));
    }
    private async Task ReconcileAsync()
    {
        if (!options.SuspendEnabled) { StopCapture(); return; }
        if (lease != null || disposed) return;
        var policy = PowerPolicy.Read();
        string? unavailable = Environment.OSVersion.Version.Build != 26200 ? "当前系统版本尚未验证" :
            policy.Ac is not (1 or 2) || policy.Dc is not (1 or 2) ? "仅用于睡眠或休眠" : null;
        if (unavailable != null) { captureError = unavailable; return; }
        var candidate = new PowerTrace(Program.Log, true);
        try
        {
            if (!candidate.HasKernelPower) throw new UnauthorizedAccessException("无法接收电源键通知");
            lease = await PowerLease.StartAsync(state); trace = candidate;
            trace.PowerButton += OnButton; gate.Arm(Stopwatch.GetTimestamp()); captureError = null;
            RecordMonitor();
        }
        catch { candidate.Dispose(); throw; }
    }
    private void StopCapture()
    {
        bool wasActive = lease != null || trace != null;
        gate.Disarm();
        if (trace != null) trace.PowerButton -= OnButton;
        try { lease?.Dispose(); }
        finally { lease = null; trace?.Dispose(); trace = null; if (wasActive) RecordMonitor(); }
    }
    private object Status()
    {
        bool active = lease != null && lease.Healthy;
        var policy = active ? lease!.Original : PowerPolicy.Read();
        string Name(uint value) => value == 1 ? "sleep" : value == 2 ? "hibernate" : value == 3 ? "shutdown" : "nothing";
        return new { installed = true, available = true, suspendEnabled = options.SuspendEnabled, resumeEnabled = options.ResumeEnabled,
            active, effectiveButtonAc = Name(policy.Ac), effectiveButtonDc = Name(policy.Dc), error = captureError, enhancedSleep = enhanced?.Status };
    }
    private async Task<object> HandleAsync(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
            throw new ArgumentException("请求格式错误");
        string? name = command.GetString();
        if (name == "get" && request.EnumerateObject().Count() == 1) return Status();
        if (name is not ("set_suspend" or "set_resume" or "set_action" or "power_action" or "preview_exit" or "set_enhanced" or "set_sleep_games" or "set_sleep_fingerprint" or "set_game" or "set_game_session") || request.EnumerateObject().Count() != 2)
            throw new ArgumentException("不支持的设置");
        if (name is "set_game" or "set_game_session")
        {
            if (!request.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out int id) || id < 0) throw new ArgumentException("游戏进程格式错误");
            await enhanced!.SetGameAsync(id, name == "set_game_session"); return new { accepted = true };
        }
        if (transitioning || wakeAnimating) throw new InvalidOperationException("正在切换电源状态，请稍后再试");
        await operation.WaitAsync();
        try
        {
            if (name is "set_enhanced" or "set_sleep_games" or "set_sleep_fingerprint")
            {
                if (enhanced!.Sleeping) throw new InvalidOperationException("正在睡眠，请唤醒后修改设置");
                bool value = AnimationOptions.Flag(request, "enabled");
                if (name == "set_enhanced") await enhanced.ConfigureAsync(value);
                else if (name == "set_sleep_games") await enhanced.SetGamesAsync(value);
                else await enhanced.SetFingerprintAsync(value);
                return Status();
            }
            if (name is "power_action" or "preview_exit")
            {
                string? action = request.TryGetProperty("action", out var requested) && requested.ValueKind == JsonValueKind.String ? requested.GetString() : null;
                if (action is not ("shutdown" or "restart")) throw new ArgumentException("不支持的电源操作");
                if (!options.SuspendEnabled && name == "power_action") return new { handled = false, action };
                return await BeginExitAsync(action, name == "preview_exit");
            }
            if (name == "set_action")
            {
                if (enhanced?.Sleeping == true) throw new InvalidOperationException("正在睡眠，请唤醒后修改设置");
                string? action = request.TryGetProperty("action", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                uint index = action == "sleep" ? 1u : action == "hibernate" ? 2u : action == "shutdown" ? 3u : throw new ArgumentException("不支持的电源键动作");
                StopCapture(); PowerPolicy.SetAction(index); await ReconcileAsync();
            }
            else
            {
                bool value = AnimationOptions.Flag(request, "enabled");
                var before = options;
                captureError = null;
                options = name == "set_suspend" ? options with { SuspendEnabled = value } : options with { ResumeEnabled = value };
                try { options.Save(state); await ReconcileAsync(); }
                catch { options = before; options.Save(state); await ReconcileAsync(); throw; }
            }
            Program.Log("animation-settings-changed", options); return Status();
        }
        finally { operation.Release(); }
    }
    private async Task<object> BeginExitAsync(string action, bool preview)
    {
        transitioning = true; gate.Disarm();
        var black = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exitFinish = finish;
        Task playing = SleepOverlay.PlayAsync(false, retainUntil: finish.Task, readyWindow: window =>
        {
            int result = DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int));
            Program.Log("power-exit-window", new { result, cloaked });
            black.TrySetResult();
        });
        try
        {
            if (await Task.WhenAny(black.Task, playing) != black.Task) { await playing; throw new InvalidOperationException("动画未完成"); }
            Program.Log("power-exit-black", new { action, preview });
            if (!preview)
            {
                using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                    (action == "restart" ? "/r" : "/s") + " /t 0") { UseShellExecute = false, CreateNoWindow = true });
                Program.Log("power-exit-requested", new { action });
            }
            _ = CompleteExitAsync();
            return new { handled = true, action, preview };
        }
        catch { finish.TrySetResult(); await CompleteExitAsync(); throw; }

        async Task CompleteExitAsync()
        {
            try
            {
                await Task.WhenAny(finish.Task, Task.Delay(preview ? 350 : 8000));
                finish.TrySetResult(); await playing;
            }
            catch (Exception ex) { Program.Log("power-exit-animation-error", ex.Message); }
            finally
            {
                if (ReferenceEquals(exitFinish, finish)) exitFinish = null;
                transitioning = false;
                if (!disposed && lease != null) { gate.Arm(Stopwatch.GetTimestamp()); gate.Resume(Stopwatch.GetTimestamp()); }
                Program.Log("power-exit-animation-cleared", new { action, preview });
            }
        }
    }
    private nint OnMessage(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x2b1 && (int)l == Process.GetCurrentProcess().SessionId)
        {
            if (w == 7) enhanced?.Signal(SleepSignal.Locked);
            if (w == 8) enhanced?.Signal(SleepSignal.Unlocked);
        }
        if (msg == 0x218)
        {
            if (w == 0x8013 && l != 0 && Marshal.PtrToStructure<Guid>(l) == DisplayPower.Session && Marshal.ReadInt32(l, 16) == 4)
            {
                bool wasOn = displayOn; displayOn = Marshal.ReadInt32(l, 20) != 0;
                if (!displayOn) displayOff?.TrySetResult();
                else if (!wasOn) { gate.Resume(Stopwatch.GetTimestamp()); if (resumed != null && enhanced?.Sleeping != true) resumed.TrySetResult(); TryWake(); }
            }
            if (w == 7 || w == 18)
            {
                long now = Stopwatch.GetTimestamp(); gate.Resume(now);
                if (now - lastResume > 3 * Stopwatch.Frequency) { pendingResume = true; lastResume = now; }
                TryWake();
            }
        }
        // A query may later be cancelled by another application. Keep the
        // controller alive until Windows confirms the session is actually ending.
        if (msg == 0x11) { handled = true; return 1; }
        if (msg == 0x16 && w != 0) { Shutdown(); handled = true; return 0; }
        return 0;
    }
    private void TryWake()
    {
        if (!displayOn || !pendingResume || disposed || enhanced?.Sleeping == true) return;
        pendingResume = false;
        if (resumed != null) { resumed.TrySetResult(); return; }
        if (options.ResumeEnabled && !transitioning && !wakeAnimating) _ = StandaloneWakeAsync();
    }
    private async Task StandaloneWakeAsync()
    {
        wakeAnimating = true;
        try { Program.Log("wake-animation-start"); await SleepOverlay.PlayWakeAsync(); Program.Log("wake-animation-complete"); }
        catch (Exception ex) { Program.Log("wake-animation-error", ex.Message); }
        finally { wakeAnimating = false; }
    }
    private void OnButton(PowerButtonEvent signal)
    {
        if (disposed || !displayOn || !gate.TryBegin(signal)) return;
        app.Dispatcher.BeginInvoke(async () =>
        {
            Task? playing = null;
            var black = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var off = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resumed = wake; displayOff = off; transitioning = true;
            nint overlay = 0;
            try
            {
                if (disposed || lease == null || !lease.Healthy || Stopwatch.GetTimestamp() - signal.TimestampQpc > 2 * Stopwatch.Frequency) return;
                uint action = Hibernate.CurrentAction(lease.Original);
                Program.Log("button-animation-start", new { action });
                playing = SleepOverlay.PlayAsync(false, null, null, wake.Task, options.ResumeEnabled,
                    hwnd => { overlay = hwnd; black.TrySetResult(); });
                if (await Task.WhenAny(black.Task, playing) != black.Task) { await playing; return; }
                if (disposed || !lease.Healthy) return;
                Program.Log(action == 2 ? "button-hibernate-requested" : "button-sleep-requested");
                if (enhanced != null) await enhanced.RequestedAsync(action == 2);
                if (action == 2)
                {
                    await Task.Run(Hibernate.Enter);
                    Program.Log("button-hibernate-returned");
                    if (!wake.Task.IsCompleted) await Task.WhenAny(wake.Task, Task.Delay(2000));
                }
                else
                {
                    Hibernate.Sleep(overlay);
                    if (await Task.WhenAny(off.Task, Task.Delay(2500)) != off.Task) throw new InvalidOperationException("屏幕未进入睡眠状态");
                    await wake.Task;
                }
            }
            catch (Exception ex)
            {
                if (enhanced != null) await enhanced.CancelRequestedAsync();
                Program.Log("button-animation-error", ex.ToString()); StopCapture();
                options = options with { SuspendEnabled = false }; options.Save(state); captureError = "已恢复原电源键操作，可重新开启动画";
            }
            finally
            {
                wake.TrySetResult(); if (playing != null) try { await playing; } catch { }
                if (ReferenceEquals(resumed, wake)) resumed = null;
                if (ReferenceEquals(displayOff, off)) displayOff = null;
                pendingResume = false; transitioning = false; gate.End(Stopwatch.GetTimestamp());
                if (!disposed) Program.Log("button-cycle-complete");
            }
        });
    }
    private void Shutdown() { Dispose(); app.Shutdown(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); resumed?.TrySetResult(); exitFinish?.TrySetResult(); control?.Dispose();
        try { try { enhanced?.Dispose(); enhanced = null; } finally { StopCapture(); } }
        finally
        {
            WTSUnRegisterSessionNotification(messages.Handle);
            UnregisterPowerSettingNotification(displayRegistration); UnregisterSuspendResumeNotification(resumeRegistration);
            messages.Dispose(); stopping.Unregister(null); stop.Dispose();
        }
        Program.Log("monitor-stopped");
    }
}
