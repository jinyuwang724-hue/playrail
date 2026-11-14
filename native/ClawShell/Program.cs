// ClawShell.exe：游戏模式的外壳启动器。极小、只用 .NET Framework、不依赖工具箱后台。
//
//   shell                    Winlogon 外壳（HKCU Winlogon Shell）。开机检查强制桌面，拉起游戏库并一直看着它。
//   watch                    登录项里的监视进程：桌面登录时也在，负责手柄 视图+菜单 切换和游戏模式下的崩溃保护。
//   to-game | to-desktop     不重启立即切换（只启停 explorer，游戏库常驻）。
//   set-default game|desktop 下次登录进哪个模式。
//   status | install | uninstall
//
// 从游戏库（MSIX）里启动时还在应用包里：注册表写入会被虚拟化、explorer 也会跑在包里。
// 游戏库用「子进程脱离应用包」的策略启动我们，我们再把自己启动一次，真正干活的是包外的那一份。
// 结果：退出码（0 成功 / 1 失败 / 2 拒绝）+ %LOCALAPPDATA%\ClawToolbox\shell\state.json。
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

// 文件版本 = Program.Version：游戏库打包时按它核对 Payload 里的外壳是不是源码当前版本
[assembly: System.Reflection.AssemblyFileVersion(ClawShell.Program.Version)]
[assembly: System.Reflection.AssemblyVersion(ClawShell.Program.Version)]

namespace ClawShell
{
    public static class Program
    {
        public const string Version = "1.0.19";
        private static WindowsShellSystem _sys;
        private static string _verb = "";

        [STAThread]
        public static int Main(string[] args)
        {
            _verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            args = args.Where(a => a.Length > 0).ToArray();
            if (_verb == "edge-apply") return WindowsShellSystem.ApplyEdgeSwipe(args);
            if (_verb == "preview-mode" && args.Length == 2 && (args[1] == "game" || args[1] == "desktop"))
            {
                using (ModeTransition.Show(args[1] == "game")) Thread.Sleep(900);
                return 0;
            }
            _sys = new WindowsShellSystem();
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                _sys.Log("unhandled: " + e.ExceptionObject);
                if (_verb == "shell" || _verb == "watch") EmergencyDesktop("unhandled");
            };
            int code = 1;
            try { code = Run(args); }
            catch (Exception ex)
            {
                _sys.Log(_verb + " failed: " + ex);
                if (_verb == "shell" || _verb == "to-game") EmergencyDesktop("exception");
                if (_verb == "shell" || _verb == "watch") IdleForever();
            }
            ReportDone(args, code);
            return code;
        }

        /// <summary>经计划任务跑的那一份：把退出码写到 shell\req-&lt;id&gt;.txt，包里等着的那一份读它。</summary>
        private static void ReportDone(string[] args, int code)
        {
            int i = Array.IndexOf(args, "--req");
            if (i < 0 || i + 1 >= args.Length) return;
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(WindowsShellSystem.ShellDir, "req-" + args[i + 1] + ".txt"), code.ToString()); }
            catch (Exception ex) { _sys.Log("report done: " + ex.Message); }
        }

        private static int Run(string[] args)
        {
            // 游戏库总是带 --task 调用：经计划任务跑一份完全在包外的（应用包身份检测不可靠，见 Hop）
            if ((args.Contains("--task") || WindowsShellSystem.InPackage()) && !args.Contains("--hop"))
            {
                var forwarded = args.Where(a => a != "--task");
                // Paint while the scheduled task starts. The task does the shell work and never opens a second overlay.
                bool modeSwitch = _verb == "to-game" || _verb == "to-desktop";
                using (var transition = modeSwitch ? _sys.BeginModeTransition(_verb == "to-game", false) : null)
                {
                    if (transition != null) forwarded = forwarded.Concat(new[] { "--external-transition" });
                    int result = Hop(forwarded.ToArray());
                    if (result == 0 && transition != null) transition.Complete();
                    return result;
                }
            }
            if (_verb != "shell" && _verb != "watch")
            {
                try { _sys.InstallSelf(); } catch (Exception ex) { _sys.Log("install self: " + ex.Message); }
            }
            var guard = new ShellGuard(_sys);
            switch (_verb)
            {
                case "shell":
                {
                    _sys.Log("shell start " + Version);
                    bool owner;
                    var mutex = new Mutex(true, WindowsShellSystem.SupervisorMutex, out owner);
                    guard.Boot();
                    if (owner) guard.Supervise(() => true);
                    GC.KeepAlive(mutex);
                    IdleForever();   // 外壳进程不退出：Winlogon 会把退出的外壳再拉起来
                    return 0;
                }
                case "watch":
                {
                    bool owner;
                    var mutex = new Mutex(true, WindowsShellSystem.SupervisorMutex, out owner);
                    if (!owner) return 0;
                    _sys.Log("watch start " + Version);
                    if (_sys.ExplorerRunning() && !WindowsShellSystem.HasEdgeSwipeSession())
                    {
                        _sys.WriteState("desktop", "watch-start");
                        _sys.ShowDesktopUi();
                    }
                    guard.Supervise(() => true);
                    GC.KeepAlive(mutex);
                    return 0;
                }
                case "set-covered": _sys.SetCoverWanted(args.Length > 1 && args[1].ToLowerInvariant() == "on"); _sys.WriteState(_sys.CoveredActive() || !_sys.ExplorerRunning() ? "game" : "desktop", "covered-setting"); return 0;
                case "ensure-surface": return _sys.EnsureLibrarySurface(args.Contains("--activate")) ? 0 : 1;
                case "to-game": return (int)guard.ToGame(!args.Contains("--external-transition"));
                case "to-desktop": return (int)guard.ToDesktop(!args.Contains("--external-transition"));
                case "set-default":
                {
                    string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    if (mode != "game" && mode != "desktop") return 1;
                    return (int)guard.SetDefault(mode == "game");
                }
                case "probe":
                    // 诊断：写一个 HKCU 值，在包外看得到说明这一份没有被应用包虚拟化
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\ClawToolbox\ClawShell"))
                        key.SetValue("probe", DateTime.Now.ToString("s") + (WindowsShellSystem.InPackage() ? " packaged" : ""));
                    return 0;
                case "status":
                    _sys.WriteState(_sys.ExplorerRunning() && !_sys.CoveredActive() ? "desktop" : "game", "status");
                    return 0;
                case "install":
                    _sys.EnsureRunEntry();
                    _sys.WriteState(_sys.ExplorerRunning() && !_sys.CoveredActive() ? "desktop" : "game", "installed");
                    return 0;
                case "uninstall":
                    if (_sys.IsOurShellValue()) _sys.DeleteShellValue();
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowsShellSystem.RunKey, true))
                        if (key != null) key.DeleteValue(WindowsShellSystem.RunValue, false);
                    try { System.IO.File.Delete(WindowsShellSystem.ShortcutPath); } catch { }
                    if (!_sys.ExplorerRunning()) _sys.StartExplorer();
                    _sys.WriteState("desktop", "uninstalled");
                    return 0;
                default:
                    return 1;
            }
        }

        /// <summary>
        /// 在应用包里：经计划任务 \ClawToolbox\Shell 再跑一次自己，等它结束，返回它的退出码。
        /// 「子进程脱离应用包」起的进程仍带着包身份，是否还被虚拟化不好判断；计划任务起的进程确定完全在包外（实测 HKCU 写到真实位置）。
        /// </summary>
        private static int Hop(string[] args)
        {
            string self = Process.GetCurrentProcess().MainModule.FileName;
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            string line = string.Join(" ", args.Select(a => a.Replace("\"", "").Replace("<", "").Replace("&", ""))) + " --hop --req " + id;
            // 改名的副本：ClawShell.exe 可能正被监视进程占着；这一份跑起来后 InstallSelf 再把自己装成 ClawShell.exe
            string hopExe = System.IO.Path.Combine(WindowsShellSystem.ShellDir, "ClawShell-hop.exe");
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
                + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<RegistrationInfo><Description>Claw toolbox game/desktop mode switch (ClawShell)</Description></RegistrationInfo>"
                + "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"
                + "<Settings><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT2M</ExecutionTimeLimit><Enabled>true</Enabled></Settings>"
                // 计划任务不让直接运行应用包目录（WindowsApps）里的 exe（拒绝访问）：先复制到 %LOCALAPPDATA%\ClawToolbox\shell 再跑那一份。
                // conhost --headless：不弹控制台窗口。
                + "<Actions Context=\"Author\"><Exec><Command>conhost.exe</Command><Arguments>"
                + System.Security.SecurityElement.Escape("--headless cmd.exe /d /c (if not exist \"" + WindowsShellSystem.ShellDir + "\" mkdir \"" + WindowsShellSystem.ShellDir + "\")"
                    + " & copy /y \"" + self + "\" \"" + hopExe + "\" >nul"
                    + " & \"" + hopExe + "\" " + line)
                + "</Arguments></Exec></Actions></Task>";
            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClawShell-" + id + ".xml");
            System.IO.File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            try
            {
                if (Schtasks("/create /tn \"\\ClawToolbox\\Shell\" /xml \"" + file + "\" /f") != 0) { _sys.Log("hop: create task failed"); return 1; }
                if (Schtasks("/run /tn \"\\ClawToolbox\\Shell\"") != 0) { _sys.Log("hop: run task failed"); return 1; }
            }
            finally { try { System.IO.File.Delete(file); } catch { } }
            string done = System.IO.Path.Combine(WindowsShellSystem.ShellDir, "req-" + id + ".txt");
            for (int i = 0; i < 1200; i++)
            {
                Thread.Sleep(100);
                if (!System.IO.File.Exists(done)) continue;
                int code;
                string text = "";
                try { text = System.IO.File.ReadAllText(done).Trim(); System.IO.File.Delete(done); } catch { }
                return int.TryParse(text, out code) ? code : 1;
            }
            _sys.Log("hop task timed out: " + line);
            return 1;
        }

        private static int Schtasks(string arguments)
        {
            using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) _sys.Log("schtasks " + arguments + " -> " + p.ExitCode + " " + output.Trim());
                return p.ExitCode;
            }
        }

        private static void EmergencyDesktop(string why)
        {
            try { new ShellGuard(_sys).FallbackToDesktop(why); } catch { }
        }

        private static void IdleForever() { Thread.Sleep(Timeout.Infinite); }
    }
}
