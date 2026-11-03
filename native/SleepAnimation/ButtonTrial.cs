using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Diagnostics.Tracing.Session;

namespace ClawSleepAnimation;

internal sealed record TrialOwner(int Pid, long StartTicks, DateTime Deadline);

// One finite experiment, guarded by an independent process. Never installed.
internal static class ButtonTrial
{
    public static int Guard(string state)
    {
        var backup = PowerPolicy.Load(Path.Combine(state, "policy-before.json"));
        var owner = JsonSerializer.Deserialize<TrialOwner>(File.ReadAllText(Path.Combine(state, "owner.json")))!;
        File.WriteAllText(Path.Combine(state, "guard.ready"), Environment.ProcessId.ToString());
        string reason = "deadline";
        DateTime lastLoop = DateTime.UtcNow, resumeGrace = DateTime.MinValue;
        try
        {
            while (DateTime.UtcNow < owner.Deadline)
            {
                DateTime now = DateTime.UtcNow;
                if (now - lastLoop > TimeSpan.FromSeconds(3)) resumeGrace = now.AddSeconds(6);
                lastLoop = now;
                if (File.Exists(Path.Combine(state, "trial.stop"))) { reason = "stop-requested"; break; }
                try
                {
                    using var parent = Process.GetProcessById(owner.Pid);
                    if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != owner.StartTicks) { reason = "owner-exited"; break; }
                }
                catch (ArgumentException) { reason = "owner-exited"; break; }
                string heartbeat = Path.Combine(state, "heartbeat");
                if (now > resumeGrace && File.Exists(heartbeat) && now - File.GetLastWriteTimeUtc(heartbeat) > TimeSpan.FromSeconds(6))
                { reason = "owner-unresponsive"; break; }
                Thread.Sleep(250);
            }
        }
        finally
        {
            PowerPolicy.Restore(backup);
            try { using var leftover = new TraceEventSession("ClawSleepDiagnostic-" + owner.Pid, TraceEventSessionOptions.Attach); leftover.Stop(); } catch { }
            File.WriteAllText(Path.Combine(state, "restored.json"), JsonSerializer.Serialize(new { at = DateTime.UtcNow, reason, policy = PowerPolicy.Read() }));
            Program.Log("trial-guard-restored", new { reason });
        }
        return 0;
    }

    public static async Task PrepareAsync(string state, Application app)
    {
        string verification = Path.Combine(state, "guard-verification"); Directory.CreateDirectory(verification);
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--verify-trial-guard --state-dir \"" + verification + "\"")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        await child.WaitForExitAsync();
        if (child.ExitCode != 71) throw new InvalidOperationException("Guard verification child did not reach the deliberate crash");
        string result = Path.Combine(verification, "restored.json");
        for (int i = 0; i < 160 && !File.Exists(result); i++) await Task.Delay(50);
        if (!File.Exists(result) || PowerPolicy.Read() != PowerPolicy.Load(Path.Combine(verification, "policy-before.json")))
            throw new InvalidOperationException("Independent policy restoration did not verify; trial not started");
        Program.Log("independent-guard-verified", new { originalPolicy = PowerPolicy.Read() });
        string trial = Path.Combine(state, "trial"); Directory.CreateDirectory(trial);
        using var live = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--button-trial --state-dir \"" + trial + "\"")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        app.Shutdown();
    }

    public static async Task RunAsync(string state, Application app, bool verifyCrash = false, bool animate = false)
    {
        if (animate) await SleepOverlay.WarmUpAsync();
        using var trace = new PowerTrace(Program.Log, animate);
        if (!trace.HasKernelPower) throw new UnauthorizedAccessException("Kernel power tracing requires elevation");
        var backup = PowerPolicy.Read();
        if (backup.Ac != 2 || backup.Dc != 2) throw new InvalidOperationException("Expected existing power-button hibernate policy; no changes made");
        string backupPath = Path.Combine(state, "policy-before.json");
        PowerPolicy.Save(backupPath, backup);
        using var owner = Process.GetCurrentProcess();
        var deadline = DateTime.UtcNow.AddMinutes(10);
        File.WriteAllText(Path.Combine(state, "owner.json"), JsonSerializer.Serialize(new TrialOwner(owner.Id, owner.StartTime.ToUniversalTime().Ticks, deadline)));
        File.WriteAllText(Path.Combine(state, "heartbeat"), "ready");
        using var guard = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--trial-guard --state-dir \"" + state + "\"")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new InvalidOperationException("Guard did not start");
        for (int i = 0; i < 80 && !File.Exists(Path.Combine(state, "guard.ready")); i++) await Task.Delay(50);
        if (!File.Exists(Path.Combine(state, "guard.ready")) || guard.HasExited) throw new InvalidOperationException("Guard is not ready; no policy changes made");
        PowerPolicy.Claim(backup);
        Program.Log("button-trial-armed", new { policyBefore = backup, guardPid = guard.Id, deadline });
        File.WriteAllText(Path.Combine(state, "trial.ready"), "Ready: temporary do-nothing power policy; independent restore guard active.");
        if (verifyCrash) Environment.Exit(71); // Explicit failure-recovery test, no machine suspend.
        var message = new TextBlock { Text = animate ? "休眠动画测试已就绪。\n短按电源键：播放动画，然后休眠。" : "请短按一次电源键。\n这次用于记录按键信号，暂时不会休眠。", Foreground = Brushes.White,
            FontSize = 20, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
        var countdown = new TextBlock { Foreground = Brushes.LightGray, FontSize = 14, Margin = new Thickness(24, 0, 24, 16) };
        var end = new Button { Content = "结束测试并恢复电源键", Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(24, 0, 24, 24) };
        var panel = new StackPanel(); panel.Children.Add(message); panel.Children.Add(countdown); panel.Children.Add(end);
        var window = new Window { Title = "Claw · 电源键诊断", Width = 480, Height = 270, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = new SolidColorBrush(Color.FromRgb(22, 24, 29)),
            Topmost = true, Content = panel };
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buttonGate = new PowerButtonGate(Stopwatch.Frequency); buttonGate.Arm(Stopwatch.GetTimestamp());
        if (animate) trace.PowerButton += signal =>
        {
            if (!buttonGate.TryBegin(signal)) return;
            app.Dispatcher.BeginInvoke(async () =>
            {
                var blackReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? playing = null;
                try
                {
                    if (finished.Task.IsCompleted) return;
                    Program.Log("button-animation-start");
                    playing = SleepOverlay.PlayAsync(false, null, () => blackReady.TrySetResult(), resumed.Task);
                    if (await Task.WhenAny(blackReady.Task, playing) != blackReady.Task) { await playing; return; }
                    if (finished.Task.IsCompleted) return;
                    Program.Log("button-hibernate-requested");
                    await Task.Run(Hibernate.Enter);
                    Program.Log("button-hibernate-returned");
                }
                catch (Exception ex) { Program.Log("button-animation-error", ex.ToString()); }
                finally
                {
                    resumed.TrySetResult(); if (playing != null) try { await playing; } catch { }
                    buttonGate.End(Stopwatch.GetTimestamp()); finished.TrySetResult();
                }
            });
        };
        end.Click += (_, _) => finished.TrySetResult(); window.Closed += (_, _) => finished.TrySetResult();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            File.WriteAllText(Path.Combine(state, "heartbeat"), DateTime.UtcNow.ToString("o"));
            var remaining = deadline - DateTime.UtcNow;
            countdown.Text = $"最迟 {Math.Max(0, (int)remaining.TotalSeconds)} 秒后自动恢复原来的休眠功能。";
            if (remaining <= TimeSpan.Zero || guard.HasExited || trace.Completion.IsCompleted || !PowerPolicy.StillClaimed(backup) || File.Exists(Path.Combine(state, "trial.stop"))) finished.TrySetResult();
        };
        try
        {
            window.Show();
            var source = (HwndSource)PresentationSource.FromVisual(window);
            source.AddHook((nint hwnd, int msg, nint w, nint l, ref bool handled) =>
            {
                if (msg == 0x218 && w == 4 && !animate) finished.TrySetResult();
                return 0;
            });
            timer.Start(); await finished.Task;
        }
        finally
        {
            timer.Stop(); PowerPolicy.Restore(backup);
            File.WriteAllText(Path.Combine(state, "trial.stop"), "done");
            Program.Log("button-trial-restored", PowerPolicy.Read()); window.Close();
        }
        app.Shutdown();
    }
}
