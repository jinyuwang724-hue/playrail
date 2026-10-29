// 游戏模式外壳护栏测试：ShellGuard 配一个假的系统，覆盖开机强制桌面、崩溃循环、切换前检查、异常兜底。
// 编译运行：tools\Build-ClawShell.ps1
using System;
using System.Collections.Generic;
using ClawShell;

internal sealed class FakeSystem : IShellSystem
{
    public DateTime Clock = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    public bool Explorer, Library, Steam, SteamInstalledFlag = true, SteamStarts = true;
    public bool Shift, Flag, ExplorerRespawns, ExplorerStartFails, Installed = true;
    public int LaunchFailuresLeft;            // 接下来几次激活游戏库失败
    public bool LaunchThrows, RunStartupThrows;
    public Func<int, bool> Combo = t => false; // 按 Sleep 次数决定手柄组合键是否按着
    public string Shell;
    public int Sleeps, Launches, ExplorerStarts, ExplorerStops, SupervisorEnsures, StartupRuns;
    public readonly List<string> States = new List<string>();
    public readonly List<string> Logs = new List<string>();

    public DateTime Now { get { return Clock; } }
    public void Sleep(int ms) { Sleeps++; Clock = Clock.AddMilliseconds(ms); }
    public void Log(string line) { Logs.Add(line); }
    public bool ExplorerRunning() { return Explorer; }
    public bool StartExplorer() { ExplorerStarts++; if (ExplorerStartFails) return false; Explorer = true; return true; }
    public bool StopExplorer() { ExplorerStops++; if (ExplorerRespawns) return false; Explorer = false; return true; }
    public string ReadShellValue() { return Shell; }
    public void WriteShellValue(string value) { Shell = value; }
    public void DeleteShellValue() { Shell = null; }
    public string GameShellCommand() { return Installed ? "\"C:\\x\\ClawShell.exe\" shell" : null; }
    public bool LibraryRunning() { return Library; }
    public bool LaunchLibrary()
    {
        Launches++;
        if (LaunchThrows) throw new InvalidOperationException("boom");
        if (LaunchFailuresLeft > 0) { LaunchFailuresLeft--; return false; }
        Library = true; return true;
    }
    public bool SteamInstalled() { return SteamInstalledFlag; }
    public bool SteamRunning() { return Steam; }
    public bool StartSteam() { if (SteamStarts) Steam = true; return SteamStarts; }
    public bool ShiftHeld() { return Shift; }
    public bool PadComboHeld() { return Combo(Sleeps); }
    public bool ForceFlagExists() { return Flag; }
    public void RunStartupApps() { StartupRuns++; if (RunStartupThrows) throw new InvalidOperationException("startup"); }
    public void EnsureSupervisor() { SupervisorEnsures++; }
    public void WriteState(string mode, string message) { States.Add(mode + "|" + message); }
    public string LastState { get { return States.Count == 0 ? "" : States[States.Count - 1]; } }
}

internal static class ClawShellTests
{
    private static int _failed, _passed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine("  ok   " + name); }
        else { _failed++; Console.WriteLine("  FAIL " + name + " " + detail); }
    }

    private static FakeSystem GameBoot() { return new FakeSystem { Shell = "\"C:\\x\\ClawShell.exe\" shell" }; }

    /// <summary>跑 n 个监视周期（每个 100 ms）。</summary>
    private static void Run(ShellGuard guard, FakeSystem sys, int ticks)
    {
        int left = ticks;
        guard.Supervise(() => left-- > 0);
    }

    private static int Main()
    {
        Console.WriteLine("ClawShell guard tests");

        // 1 正常开机：不开 explorer，游戏库起来，跑了登录项，Shell 值保留
        {
            var s = GameBoot(); var g = new ShellGuard(s);
            bool game = g.Boot();
            Check("boot: game mode", game && s.Library && !s.Explorer && s.ExplorerStarts == 0);
            Check("boot: startup apps run once", s.StartupRuns == 1);
            Check("boot: shell value kept", s.Shell != null);
            Check("boot: waits ~2 s for shift / pad", s.Clock >= new DateTime(2026, 10, 29, 12, 0, 2, DateTimeKind.Utc));
        }
        // 2 标志文件 / Shift / 手柄组合键：进桌面并删 Shell 值，不启动游戏库
        foreach (var c in new[] { "flag", "shift", "pad" })
        {
            var s = GameBoot();
            if (c == "flag") s.Flag = true;
            if (c == "shift") s.Shift = true;
            if (c == "pad") s.Combo = t => t >= 5;   // 开机 0.5 秒后按下
            var g = new ShellGuard(s);
            bool game = g.Boot();
            Check("force desktop (" + c + "): explorer, no library", !game && s.Explorer && s.Launches == 0);
            Check("force desktop (" + c + "): shell value removed", s.Shell == null);
            Check("force desktop (" + c + "): state", s.LastState.StartsWith("desktop|fallback:forced:"), s.LastState);
        }
        // 3 开机时游戏库连续 3 次起不来 → 桌面
        {
            var s = GameBoot(); s.LaunchFailuresLeft = 3; var g = new ShellGuard(s);
            bool game = g.Boot();
            Check("boot launch fails x3: desktop", !game && s.Explorer && s.Shell == null && s.Launches == 3);
        }
        // 4 开机时 2 次失败第 3 次成功 → 仍是游戏模式
        {
            var s = GameBoot(); s.LaunchFailuresLeft = 2; var g = new ShellGuard(s);
            Check("boot launch fails x2 then ok: game", g.Boot() && !s.Explorer && s.Shell != null);
        }
        // 5 开机过程抛异常 → 回 explorer
        {
            var s = GameBoot(); s.LaunchThrows = true; var g = new ShellGuard(s);
            Check("boot exception: desktop", !g.Boot() && s.Explorer && s.Shell == null && s.LastState.Contains("boot-exception"));
        }
        // 6 登录项出错不影响进游戏模式
        {
            var s = GameBoot(); s.RunStartupThrows = true; var g = new ShellGuard(s);
            Check("startup app error ignored", g.Boot() && s.Library && !s.Explorer);
        }
        // 7 游戏库崩溃一次：重启它，不回桌面
        {
            var s = GameBoot(); var g = new ShellGuard(s); g.Boot();
            s.Library = false;
            Run(g, s, 11);
            Check("one crash: relaunched", s.Library && !s.Explorer && s.Shell != null && !g.GaveUp);
        }
        // 8 60 秒内崩溃 3 次 → explorer + 删 Shell 值，之后不再重启游戏库
        {
            var s = GameBoot(); var g = new ShellGuard(s); g.Boot();
            int launchesBefore = s.Launches;
            for (int i = 0; i < 3; i++) { s.Library = false; Run(g, s, 10); }
            Check("crash loop: desktop", s.Explorer && s.Shell == null && g.GaveUp, s.LastState);
            Check("crash loop: state", s.LastState == "desktop|fallback:crash-loop", s.LastState);
            Check("crash loop: only 2 relaunches", s.Launches - launchesBefore == 2, (s.Launches - launchesBefore).ToString());
        }
        // 9 崩溃间隔超过 60 秒：一直重启，不回桌面
        {
            var s = GameBoot(); var g = new ShellGuard(s); g.Boot();
            for (int i = 0; i < 5; i++) { s.Library = false; Run(g, s, 10); s.Clock = s.Clock.AddSeconds(35); }
            Check("spread crashes: stays in game mode", !s.Explorer && s.Shell != null && s.Library && !g.GaveUp);
        }
        // 10 重启时也起不来：算失败，很快回桌面
        {
            var s = GameBoot(); var g = new ShellGuard(s); g.Boot();
            s.Library = false; s.LaunchFailuresLeft = 99;
            Run(g, s, 40);
            Check("relaunch keeps failing: desktop", s.Explorer && s.Shell == null && g.GaveUp);
        }
        // 11 桌面模式（有 explorer）游戏库被关掉：不管
        {
            var s = new FakeSystem { Explorer = true }; var g = new ShellGuard(s);
            Run(g, s, 50);
            Check("desktop: library closed is fine", s.Launches == 0 && s.ExplorerStarts == 0);
        }
        // 12 放弃之后又切回游戏模式：重新看着
        {
            var s = GameBoot(); var g = new ShellGuard(s); g.Boot();
            for (int i = 0; i < 3; i++) { s.Library = false; Run(g, s, 10); }
            Run(g, s, 10);              // 看到 explorer
            s.Library = true; s.Explorer = false;   // 别的进程切回游戏模式
            Run(g, s, 10);
            s.Library = false;
            Run(g, s, 10);
            Check("re-armed after switching back", !g.GaveUp && s.Library && !s.Explorer);
        }
        // 13 切到游戏模式：游戏库起不来 → 不关 explorer
        {
            var s = new FakeSystem { Explorer = true, Steam = true, LaunchFailuresLeft = 1 }; var g = new ShellGuard(s);
            Check("to-game, library fails: refused", g.ToGame() == SwitchResult.Refused && s.Explorer && s.ExplorerStops == 0);
        }
        // 14 Steam 起不来 → 不关 explorer
        {
            var s = new FakeSystem { Explorer = true, Library = true, SteamStarts = false }; var g = new ShellGuard(s);
            Check("to-game, steam fails: refused", g.ToGame() == SwitchResult.Refused && s.Explorer && s.ExplorerStops == 0 && s.LastState == "desktop|refused:steam");
        }
        // 15 没装 Steam：不等它
        {
            var s = new FakeSystem { Explorer = true, Library = true, SteamInstalledFlag = false }; var g = new ShellGuard(s);
            Check("to-game, no steam installed: ok", g.ToGame() == SwitchResult.Ok && !s.Explorer);
        }
        // 16 正常切到游戏模式：先确保监视进程，再关 explorer
        {
            var s = new FakeSystem { Explorer = true }; var g = new ShellGuard(s);
            Check("to-game: ok", g.ToGame() == SwitchResult.Ok && !s.Explorer && s.Library && s.Steam && s.SupervisorEnsures == 1);
        }
        // 17 explorer 被 Winlogon 拉回来关不掉 → 恢复 explorer，报失败
        {
            var s = new FakeSystem { Explorer = true, Library = true, Steam = true, ExplorerRespawns = true }; var g = new ShellGuard(s);
            Check("to-game, explorer respawns: failed + explorer", g.ToGame() == SwitchResult.Failed && s.Explorer);
        }
        // 18 强制桌面标志在：拒绝切到游戏模式，也拒绝设默认
        {
            var s = new FakeSystem { Explorer = true, Flag = true }; var g = new ShellGuard(s);
            Check("flag: to-game refused", g.ToGame() == SwitchResult.Refused && s.Explorer);
            Check("flag: set-default game refused", g.SetDefault(true) == SwitchResult.Refused && s.Shell == null);
        }
        // 19 设默认：游戏模式写 Shell 值；没装好拒绝；桌面模式删掉
        {
            var s = new FakeSystem { Explorer = true }; var g = new ShellGuard(s);
            Check("set-default game", g.SetDefault(true) == SwitchResult.Ok && s.Shell == "\"C:\\x\\ClawShell.exe\" shell" && s.SupervisorEnsures == 1);
            Check("set-default desktop", g.SetDefault(false) == SwitchResult.Ok && s.Shell == null);
            s.Installed = false;
            Check("set-default game, not installed: refused", g.SetDefault(true) == SwitchResult.Refused && s.Shell == null);
        }
        // 20 回桌面：只开 explorer，游戏库不动
        {
            var s = new FakeSystem { Library = true }; var g = new ShellGuard(s);
            Check("to-desktop", g.ToDesktop() == SwitchResult.Ok && s.Explorer && s.Library && s.Launches == 0);
            s.Explorer = false; s.ExplorerStartFails = true;
            Check("to-desktop, explorer fails: failed", g.ToDesktop() == SwitchResult.Failed);
        }
        // 21 手柄 视图+菜单 按住 2 秒：游戏模式 → 桌面；再按住 2 秒 → 游戏模式；按不满 2 秒不动
        {
            var s = GameBoot(); var g = new ShellGuard(s); g.Boot(); s.Steam = true;
            int start = s.Sleeps;
            s.Combo = t => t - start >= 0 && t - start < 15;   // 1.5 秒
            Run(g, s, 30);
            Check("combo 1.5 s: no switch", !s.Explorer);
            start = s.Sleeps;
            s.Combo = t => t - start >= 0 && t - start < 25;   // 2.5 秒
            Run(g, s, 30);
            Check("combo 2 s in game: desktop", s.Explorer && s.Library);
            start = s.Sleeps;
            Run(g, s, 30);
            Check("combo 2 s on desktop: game", !s.Explorer && s.Library);
        }
        // 22 回退时 explorer 也起不来：不抛异常，状态里记下来
        {
            var s = GameBoot(); s.ExplorerStartFails = true; s.Flag = true; var g = new ShellGuard(s);
            bool threw = false;
            try { g.Boot(); } catch { threw = true; }
            Check("fallback with explorer failing: no throw, recorded", !threw && s.Shell == null && s.LastState.EndsWith(":explorer-failed"), s.LastState);
        }
        // 23 命令行拆分（登录项）
        {
            string exe, args;
            WindowsShellSystem.SplitCommand("\"C:\\a b\\x.exe\" -s", out exe, out args);
            bool a = exe == "C:\\a b\\x.exe" && args == "-s";
            WindowsShellSystem.SplitCommand("C:\\Windows\\system32\\SecurityHealthSystray.exe", out exe, out args);
            bool b = exe == "C:\\Windows\\system32\\SecurityHealthSystray.exe" && args == "";
            WindowsShellSystem.SplitCommand("C:\\Program Files\\Intel\\x.exe --bg", out exe, out args);
            bool c = exe == "C:\\Program Files\\Intel\\x.exe" && args == "--bg";
            Check("split startup command", a && b && c);
        }

        Console.WriteLine(_passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }
}
