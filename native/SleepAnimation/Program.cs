using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;

namespace ClawSleepAnimation;

internal static class Program
{
    private static readonly object LogGate = new();
    private static string logPath = "";
    internal static void Log(string kind, object? data = null)
    {
        try
        {
            lock (LogGate)
            {
                string destination = kind == "power-trace" ? Path.Combine(Path.GetDirectoryName(logPath)!, "diagnostic-trace.jsonl") : logPath;
                if (File.Exists(destination) && new FileInfo(destination).Length > 1024 * 1024)
                    File.Move(destination, destination + ".previous", true);
                File.AppendAllText(destination, JsonSerializer.Serialize(new { at = DateTime.UtcNow, kind, data }) + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string state = Option(args, "--state-dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawToolbox",
            args.Contains("--boot-loading") ? "startup-animation" : "sleep-animation");
        Directory.CreateDirectory(state);
        logPath = Path.Combine(state, args.Contains("--trace-probe") ? "acpi-probe.jsonl" : "animation.jsonl");
        try
        {
            using var single = args.Contains("--monitor") ? new Mutex(false, @"Local\ClawSleepAnimation.Monitor") : null;
            if (single != null && !single.WaitOne(0)) { Log("monitor-already-running"); return 0; }
            if (args.Contains("--lease-guard")) return PowerLease.Guard(state, Option(args, "--lease-id") ?? "");
            if (args.Contains("--enhanced-guard")) return EnhancedSleepGuard.Run(state, Option(args, "--guard-id") ?? "");
            if (args.Contains("--startup-probe"))
            {
                File.WriteAllText(Path.Combine(state, "startup-probe.json"), JsonSerializer.Serialize(new { eligible = StartupLoading.Enabled(), readyWindow = (long)StartupLoading.FindReadyWindow() }));
                return 0;
            }
            if (args.Contains("--inspect-gamebar"))
            {
                File.WriteAllText(Path.Combine(state, "gamebar-inspection.json"), JsonSerializer.Serialize(GameBarDismiss.Inspect()));
                return 0;
            }
            foreach (string name in new[] { "suspend", "resume", "action", "enhanced", "sleep_games", "sleep_fingerprint" })
                if (Option(args, "--set-" + name) is string value)
                {
                    AnimationControl.ConfigureAsync("set_" + name, name == "action" ? value : (object)bool.Parse(value)).GetAwaiter().GetResult();
                    return 0;
                }
            if (args.Contains("--recover-only"))
            {
                bool recovered = true;
                try { recovered &= new SleepJournal(state).Recover(); } catch (Exception ex) { recovered = false; Log("enhanced-boot-recovery-error", ex.Message); }
                try { recovered &= PowerLease.Recover(state); } catch (Exception ex) { recovered = false; Log("button-boot-recovery-error", ex.Message); }
                return recovered ? 0 : 2;
            }
            if (args.Contains("--stop"))
            {
                try { using var signal = EventWaitHandle.OpenExisting(@"Local\ClawSleepAnimation.Stop"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
            if (args.Contains("--trial-guard")) return ButtonTrial.Guard(state);
            if (args.Contains("--trace-probe"))
            {
                using var identity = WindowsIdentity.GetCurrent();
                bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                Log("trace-probe-start", new { pid = Environment.ProcessId, elevated, readOnly = true });
                using var buttons = new AcpiButtons(Log);
                while (!File.Exists(Path.Combine(state, "trace-probe.stop")) && !buttons.Completion.IsCompleted)
                    Thread.Sleep(500);
                Log("trace-probe-stopped");
                return 0;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SuspendAnimation? transition = null;
            PowerTrace? wide = null;
            ButtonMonitor? monitor = null;
            app.DispatcherUnhandledException += (_, e) => { Log("ui-error", e.Exception.ToString()); e.Handled = true; app.Shutdown(1); };
            app.Startup += async (_, _) =>
            {
                if (args.Contains("--self-test-loading-timeout"))
                {
                    try
                    {
                        await SleepOverlay.PlayLoadingAsync(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return "unexpected"; }, TimeSpan.FromSeconds(3));
                        app.Shutdown();
                    }
                    catch (Exception ex) { Log("startup-overlay-error", ex.ToString()); app.Shutdown(1); }
                    return;
                }
                if (args.Contains("--boot-loading") || args.Contains("--preview-loading"))
                {
                    try { await StartupLoading.RunAsync(args.Contains("--preview-loading")); app.Shutdown(); }
                    catch (Exception ex) { Log("startup-overlay-error", ex.ToString()); app.Shutdown(1); }
                    return;
                }
                if (args.Contains("--monitor"))
                {
                    try { monitor = new ButtonMonitor(app, state); await monitor.StartAsync(); }
                    catch (Exception ex) { Log("monitor-start-error", ex.ToString()); monitor?.Dispose(); app.Shutdown(1); }
                    return;
                }
                if (args.Contains("--prepare-button-trial"))
                {
                    try { await ButtonTrial.PrepareAsync(state, app); }
                    catch (Exception ex) { Log("trial-prepare-error", ex.ToString()); app.Shutdown(1); }
                    return;
                }
                if (args.Contains("--button-trial") || args.Contains("--verify-trial-guard") || args.Contains("--animated-button-trial"))
                {
                    try { await ButtonTrial.RunAsync(state, app, args.Contains("--verify-trial-guard"), args.Contains("--animated-button-trial")); }
                    catch (Exception ex) { Log("button-trial-error", ex.ToString()); app.Shutdown(1); }
                    return;
                }
                if (args.Contains("--transition-probe") || args.Contains("--self-test-transition") || args.Contains("--self-test-blocked-ui"))
                {
                    try
                    {
                        await SleepOverlay.WarmUpAsync();
                        if (args.Contains("--transition-probe"))
                        {
                            try { wide = new PowerTrace(Log); }
                            catch (Exception ex) { Log("diagnostic-trace-unavailable", ex.Message); }
                        }
                        transition = new SuspendAnimation(app.Dispatcher, Log);
                        if (args.Contains("--self-test-blocked-ui"))
                        {
                            await transition.TestBlockedUiAsync();
                            Log("blocked-ui-test-complete", new { actualSuspend = false });
                            app.Shutdown(); return;
                        }
                        if (args.Contains("--self-test-transition"))
                        {
                            await transition.TestAsync();
                            Log("synthetic-transition-test-complete", new { actualSuspend = false });
                            app.Shutdown(); return;
                        }
                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                        timer.Tick += (_, _) => { if (File.Exists(Path.Combine(state, "transition-probe.stop"))) app.Shutdown(); };
                        timer.Start();
                        return;
                    }
                    catch (Exception ex) { Log("transition-probe-error", ex.ToString()); app.Shutdown(1); return; }
                }
                Log("preview-start");
                if (Option(args, "--render-startup-mark") is string markPath) SleepOverlay.RenderStartupMark(markPath);
                else if (Option(args, "--render-loading") is string loadingDirectory) SleepOverlay.RenderLoading(loadingDirectory);
                else if (Option(args, "--render-motion") is string renderDirectory) SleepOverlay.RenderPreview(renderDirectory);
                else if (args.Contains("--preview-pair")) { await SleepOverlay.WarmUpAsync(); await SleepOverlay.PlayAsync(preview: true, wakeAnimation: true); }
                else if (args.Contains("--preview-wake")) { await SleepOverlay.WarmUpAsync(); await SleepOverlay.PlayWakeAsync(); }
                else await SleepOverlay.PlayAsync(preview: true, capturePath: Option(args, "--capture"));
                Log("preview-complete");
                app.Shutdown();
            };
            try { return app.Run(); }
            finally
            {
                monitor?.Dispose(); transition?.Dispose(); wide?.Dispose();
                if (single != null) single.ReleaseMutex();
            }
        }
        catch (Exception ex) { Log("fatal-error", new { error = ex.ToString() }); return 1; }
    }

    private static string? Option(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
