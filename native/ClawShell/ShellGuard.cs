// 游戏模式外壳的决策逻辑：开机进哪个模式、游戏库崩溃怎么办、两种模式怎么切。
// 只依赖 IShellSystem，测试里换成假的系统（tests/ClawShellTests.cs）。
//
// 部分做法参考 Playhub v2.1.0 GamingMode.Services（ShellTools / SafeModeGuard / ProcessTools.StopExplorer），
// MIT License, Copyright (c) 2026 Andrea Sgarro (LoZazaMastro)，许可全文见同目录 PLAYHUB-LICENSE.txt。
using System;
using System.Collections.Generic;

namespace ClawShell
{
    public interface IGameSurface { bool EnsureLibrarySurface(bool activate); }

    /// <summary>保留 explorer 的游戏模式（试验）：见 WindowsShellSystem.Covered.cs。</summary>
    public interface ICoveredShell { bool CoverWanted(); bool CoveredActive(); void HideDesktopUi(); void ShowDesktopUi(); }

    public interface IDesktopSurface { bool FocusDesktop(); }

    public interface IShellSystem
    {
        DateTime Now { get; }
        void Sleep(int ms);
        void Log(string line);

        /// <summary>本会话里 explorer 是桌面外壳（GetShellWindow 属于 explorer）。</summary>
        bool ExplorerRunning();
        /// <summary>启动 explorer 并等它成为桌面外壳；Shell 值指向我们时先临时去掉它，否则 explorer 只开一个文件夹窗口。</summary>
        bool StartExplorer();
        /// <summary>先请 explorer 关窗，再结束本会话的 explorer，连续约 1 秒没有再出现才算停下。</summary>
        bool StopExplorer();

        string ReadShellValue();
        void WriteShellValue(string value);
        void DeleteShellValue();
        /// <summary>写进 Winlogon 的外壳命令（稳定位置的 ClawShell.exe shell）；启动器或游戏库不在时返回 null。</summary>
        string GameShellCommand();

        bool LibraryRunning();
        /// <summary>激活游戏库（不经 explorer），等它的进程出现。</summary>
        bool LaunchLibrary();

        bool SteamInstalled();
        bool SteamRunning();
        bool StartSteam();

        bool ShiftHeld();
        bool PadComboHeld();
        bool ForceFlagExists();

        void RunStartupApps();
        void EnsureSupervisor();
        void WriteState(string mode, string message);
    }

    public enum SwitchResult { Ok = 0, Failed = 1, Refused = 2 }

    public sealed class ShellGuard
    {
        public const int MaxFailures = 3;
        public static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);
        public const int BootProbeMs = 2000;
        public const int ComboHoldMs = 2000;
        public const int TickMs = 100;
        public const int LibraryCheckEveryTicks = 10;
        /// <summary>游戏库进程在、窗口却还没出来（刚启动 / 刚重启 / 正在切桌面）时先等这么久，再算一次失败。</summary>
        public static readonly TimeSpan SurfaceGrace = TimeSpan.FromSeconds(10);

        private readonly IShellSystem _sys;
        private readonly List<DateTime> _failures = new List<DateTime>();
        private DateTime? _surfaceBadSince;
        private bool _gaveUp;
        private bool _sawExplorer;

        public ShellGuard(IShellSystem sys) { _sys = sys; }

        public bool GaveUp { get { return _gaveUp; } }

        /// <summary>开机（Winlogon 外壳）检查：标志文件、按住 Shift、按住手柄 视图+菜单，任何一个就进桌面。</summary>
        public string ForceDesktopReason()
        {
            if (_sys.ForceFlagExists()) return "force-desktop.flag";
            for (int waited = 0; ; waited += TickMs)
            {
                if (_sys.ShiftHeld()) return "shift";
                if (_sys.PadComboHeld()) return "pad";
                if (waited >= BootProbeMs) return null;
                _sys.Sleep(TickMs);
            }
        }

        /// <summary>回桌面的兜底：删掉 Shell 值（下次登录也是桌面）、拉起 explorer。自己出错也不往外抛。</summary>
        public void FallbackToDesktop(string reason)
        {
            _sys.Log("fallback to desktop: " + reason);
            try { _sys.DeleteShellValue(); } catch (Exception ex) { _sys.Log("delete shell value failed: " + ex.Message); }
            bool started = false;
            try { started = _sys.ExplorerRunning() || _sys.StartExplorer(); } catch (Exception ex) { _sys.Log("start explorer failed: " + ex.Message); }
            try { if (started && Cover != null) Cover.ShowDesktopUi(); } catch (Exception ex) { _sys.Log("show taskbar failed: " + ex.Message); }
            try { _sys.WriteState("desktop", "fallback:" + reason + (started ? "" : ":explorer-failed")); } catch { }
        }

        /// <summary>作为 Winlogon 外壳启动时调用。返回 true 表示进了游戏模式，接着调用 Supervise。</summary>
        public bool Boot()
        {
            try
            {
                string why = ForceDesktopReason();
                if (why != null) { FallbackToDesktop("forced:" + why); return false; }
                try { _sys.RunStartupApps(); } catch (Exception ex) { _sys.Log("startup apps: " + ex.Message); }
                for (int i = 0; i < MaxFailures; i++)
                {
                    if (_sys.LaunchLibrary())
                    {
                        // 保留 explorer：游戏库起来以后再拉起 explorer，任务栏一出来就藏掉
                        if (Cover != null && Cover.CoverWanted() && _sys.StartExplorer()) Cover.HideDesktopUi();
                        _sys.WriteState("game", "boot");
                        // 进程和窗口都在：没抢到前台（开机动画还没走完等）不算失败，交给 Supervise 接着看
                        if (!EnsureSurface(true)) _sys.Log("library window not foreground at boot; supervising");
                        return true;
                    }
                    _failures.Add(_sys.Now);
                    _sys.Log("library launch failed at boot (" + (i + 1) + ")");
                    _sys.Sleep(1000);
                }
                FallbackToDesktop("library-launch-failed");
                return false;
            }
            catch (Exception ex)
            {
                FallbackToDesktop("boot-exception:" + ex.GetType().Name + ":" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 一个监视周期（约 100 ms）。没有 explorer 时游戏库不在就重启它，60 秒内失败 3 次回桌面；
        /// 手柄 视图+菜单 按住 2 秒在两种模式间切换。
        /// </summary>
        public void Tick(int tick, ref int comboMs)
        {
            if (_sys.PadComboHeld())
            {
                comboMs += TickMs;
                if (comboMs >= ComboHoldMs && comboMs < ComboHoldMs + TickMs)
                {
                    bool desktop = _sys.ExplorerRunning() && !CoveredNow;
                    _sys.Log("pad combo: switch to " + (desktop ? "game" : "desktop"));
                    if (desktop) ToGame(); else ToDesktop();
                }
            }
            else comboMs = 0;

            // 保留 explorer 的游戏模式：每 0.1 秒看一眼，任务栏冒出来就藏回去；游戏库照游戏模式看着
            bool covered = CoveredNow;
            if (covered) Cover.HideDesktopUi();
            if (tick % LibraryCheckEveryTicks != 0) return;
            bool explorer = _sys.ExplorerRunning();
            if (explorer && !covered) { _sawExplorer = true; _surfaceBadSince = null; return; }
            if (_sawExplorer)
            {
                // 桌面 → 游戏模式（别的进程切的）：重新开始计数，之前放弃过也重新看着
                _sawExplorer = false;
                _gaveUp = false;
                _failures.Clear();
            }
            if (_gaveUp) return;
            if (_sys.LibraryRunning())
            {
                if (EnsureSurface(false)) { _surfaceBadSince = null; return; }
                // EnsureSurface 每次都已请它显示；连续 SurfaceGrace 还不行才算一次失败并重新激活
                DateTime now = _sys.Now;
                if (_surfaceBadSince == null) { _surfaceBadSince = now; _sys.Log("library window not visible yet; waiting"); return; }
                if (now - _surfaceBadSince.Value < SurfaceGrace) return;
                _surfaceBadSince = now;
                RecordFailure("library-window-unavailable");
                if (!_gaveUp && !_sys.LaunchLibrary()) RecordFailure("library-window-recovery-failed");
                return;
            }
            _surfaceBadSince = null;
            RecordFailure("library-not-running");
            if (_gaveUp) return;
            if (!_sys.LaunchLibrary()) RecordFailure("library-relaunch-failed");
        }

        private ICoveredShell Cover { get { return _sys as ICoveredShell; } }
        private bool CoveredNow { get { return Cover != null && Cover.CoveredActive(); } }

        private bool EnsureSurface(bool activate)
        {
            var surface = _sys as IGameSurface;
            return surface == null || surface.EnsureLibrarySurface(activate);
        }

        private void RecordFailure(string what)
        {
            DateTime now = _sys.Now;
            _failures.Add(now);
            _failures.RemoveAll(t => now - t > FailureWindow);
            _sys.Log(what + " (" + _failures.Count + " in 60 s)");
            if (_failures.Count >= MaxFailures)
            {
                _gaveUp = true;
                FallbackToDesktop("crash-loop");
            }
        }

        /// <summary>监视循环；Winlogon 外壳和登录项里的 watch 实例都跑这个，直到注销。</summary>
        public void Supervise(Func<bool> keepRunning)
        {
            int comboMs = 0;
            for (int tick = 0; keepRunning(); tick++)
            {
                try { Tick(tick, ref comboMs); }
                catch (Exception ex)
                {
                    _sys.Log("supervise exception: " + ex.Message);
                    try { if (!_sys.ExplorerRunning()) FallbackToDesktop("supervise-exception"); } catch { }
                }
                _sys.Sleep(TickMs);
            }
        }

        /// <summary>桌面 → 游戏模式。游戏库和 Steam 都确认起来了才关 explorer；关不掉就恢复。</summary>
        public SwitchResult ToGame()
        {
            if (_sys.ForceFlagExists()) { _sys.WriteState("desktop", "refused:force-desktop.flag"); return SwitchResult.Refused; }
            if (CoveredNow)
            {
                if ((!_sys.LibraryRunning() && !_sys.LaunchLibrary()) || !EnsureSurface(true)) { FallbackToDesktop("library-window-unavailable"); return SwitchResult.Failed; }
                Cover.HideDesktopUi(); _sys.WriteState("game", "already"); return SwitchResult.Ok;
            }
            if (!_sys.ExplorerRunning())
            {
                if ((!_sys.LibraryRunning() && !_sys.LaunchLibrary()) || !EnsureSurface(true))
                { FallbackToDesktop("library-window-unavailable"); return SwitchResult.Failed; }
                _sys.WriteState("game", "already"); return SwitchResult.Ok;
            }
            if (!_sys.LibraryRunning() && !_sys.LaunchLibrary())
            {
                _sys.WriteState("desktop", "refused:library");
                return SwitchResult.Refused;
            }
            if (!EnsureSurface(true))
            { _sys.WriteState("desktop", "refused:library-window"); return SwitchResult.Refused; }
            if (_sys.SteamInstalled() && !_sys.SteamRunning() && !_sys.StartSteam())
            {
                _sys.WriteState("desktop", "refused:steam");
                return SwitchResult.Refused;
            }
            _sys.EnsureSupervisor();
            if (Cover != null && Cover.CoverWanted())
            {
                // 保留 explorer：不结束它，藏任务栏、工作区铺满，游戏库盖住桌面
                Cover.HideDesktopUi();
                _gaveUp = false;
                _failures.Clear();
                _sys.WriteState("game", "covered");
                if (!EnsureSurface(true)) { FallbackToDesktop("library-window-unavailable"); return SwitchResult.Failed; }
                return SwitchResult.Ok;
            }
            if (!_sys.StopExplorer())
            {
                _sys.Log("explorer did not stay stopped; restoring");
                if (!_sys.ExplorerRunning()) _sys.StartExplorer();
                _sys.WriteState("desktop", "failed:explorer-respawn");
                return SwitchResult.Failed;
            }
            if (!_sys.LibraryRunning() && !_sys.LaunchLibrary())
            {
                FallbackToDesktop("library-gone-after-switch");
                return SwitchResult.Failed;
            }
            _gaveUp = false;
            _failures.Clear();
            _sys.WriteState("game", "switched");
            if (!EnsureSurface(true))
            { FallbackToDesktop("library-window-unavailable"); return SwitchResult.Failed; }
            return SwitchResult.Ok;
        }

        /// <summary>游戏模式 → 桌面：只拉起 explorer，游戏库留着。</summary>
        public SwitchResult ToDesktop()
        {
            if (_sys.ExplorerRunning() || _sys.StartExplorer()) {
                if (Cover != null) Cover.ShowDesktopUi();
                var desktop = _sys as IDesktopSurface;
                if (desktop != null && !desktop.FocusDesktop()) _sys.Log("desktop focus request did not settle");
                _sys.WriteState("desktop", "switched"); return SwitchResult.Ok;
            }
            _sys.WriteState("game", "failed:explorer");
            return SwitchResult.Failed;
        }

        /// <summary>默认模式：游戏模式写 Winlogon Shell，桌面模式删掉它。</summary>
        public SwitchResult SetDefault(bool game)
        {
            if (!game)
            {
                _sys.DeleteShellValue();
                _sys.WriteState(_sys.ExplorerRunning() ? "desktop" : "game", "default:desktop");
                return SwitchResult.Ok;
            }
            if (_sys.ForceFlagExists()) { _sys.WriteState(Mode(), "refused:force-desktop.flag"); return SwitchResult.Refused; }
            string command = _sys.GameShellCommand();
            if (string.IsNullOrEmpty(command)) { _sys.WriteState(Mode(), "refused:not-installed"); return SwitchResult.Refused; }
            _sys.EnsureSupervisor();
            _sys.WriteShellValue(command);
            if (_sys.ReadShellValue() != command)
            {
                _sys.DeleteShellValue();
                _sys.WriteState(Mode(), "failed:shell-value");
                return SwitchResult.Failed;
            }
            _sys.WriteState(Mode(), "default:game");
            return SwitchResult.Ok;
        }

        private string Mode() { return _sys.ExplorerRunning() ? "desktop" : "game"; }
    }
}
