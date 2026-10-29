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

namespace ClawShell
{
    public static class Program
    {
        public const string Version = "1.0.0";
        private static WindowsShellSystem _sys;
        private static string _verb = "";

        [STAThread]
        public static int Main(string[] args)
        {
            _verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            args = args.Where(a => a.Length > 0).ToArray();
            _sys = new WindowsShellSystem();
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                _sys.Log("unhandled: " + e.ExceptionObject);
                if (_verb == "shell" || _verb == "watch") EmergencyDesktop("unhandled");
            };
            try { return Run(args); }
            catch (Exception ex)
            {
                _sys.Log(_verb + " failed: " + ex);
                if (_verb == "shell" || _verb == "to-game") EmergencyDesktop("exception");
                if (_verb == "shell" || _verb == "watch") IdleForever();
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            // 跳过一次后子进程仍带着应用包身份，但已经在包的虚拟化环境外（注册表、文件写到真实位置，见 probe）
            // 游戏库总是带 --task 调用：经计划任务跑一份完全在包外的（应用包身份检测不可靠，见 Hop）
            if ((args.Contains("--task") || WindowsShellSystem.InPackage()) && !args.Contains("--hop")) return Hop(args.Where(a => a != "--task").ToArray());
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
                    guard.Supervise(() => true);
                    GC.KeepAlive(mutex);
                    return 0;
                }
                case "to-game": return (int)guard.ToGame();
                case "to-desktop": return (int)guard.ToDesktop();
                case "set-default":
                {
                    string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    if (mode != "game" && mode != "desktop") return 1;
                    return (int)guard.SetDefault(mode == "game");
                }
                case "probe":
                    // 诊断：写一个 HKCU 值，在包外看得到说明这一份没有被应用包虚拟化
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SoftwareClawToolboxClawShell"))
                        key.SetValue("probe", DateTime.Now.ToString("s") + (WindowsShellSystem.InPackage() ? " packaged" : ""));
                    return 0;
                case "status":
                    _sys.WriteState(_sys.ExplorerRunning() ? "desktop" : "game", "status");
                    return 0;
                case "install":
                    _sys.EnsureRunEntry();
                    _sys.WriteState(_sys.ExplorerRunning() ? "desktop" : "game", "installed");
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
        /// 只靠「子进程脱离应用包」不够：实测孙进程仍带包身份，HKCU 写入照样被虚拟化；计划任务起的进程完全在包外。
        /// </summary>
        private static int Hop(string[] args)
        {
            string self = Process.GetCurrentProcess().MainModule.FileName;
            string verb = args.Length > 0 ? args[0] : "";
            string arg = args.Length > 1 ? args[1] : "";
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            dynamic folder;
            try { folder = service.GetFolder(@"\ClawToolbox"); }
            catch { folder = service.GetFolder(@"\").CreateFolder("ClawToolbox"); }
            dynamic def = service.NewTask(0);
            def.RegistrationInfo.Description = "Claw 工具箱：游戏模式 / 桌面模式切换（ClawShell）";
            def.Settings.DisallowStartIfOnBatteries = false;
            def.Settings.StopIfGoingOnBatteries = false;
            def.Settings.ExecutionTimeLimit = "PT2M";
            def.Settings.MultipleInstances = 0;   // 并行
            def.Principal.LogonType = 3;           // 交互式令牌：在当前登录会话里跑
            dynamic action = def.Actions.Create(0);
            action.Path = self;
            action.Arguments = "$(Arg0) $(Arg1) --hop";
            dynamic task = folder.RegisterTaskDefinition("Shell", def, 6 /* 创建或更新 */, null, null, 3);
            DateTime started = DateTime.Now;
            dynamic running = task.Run(new object[] { verb, arg });
            // 等这一次跑完：LastRunTime 是这次的、并且不再有运行中的实例
            for (int i = 0; i < 1200; i++)
            {
                Thread.Sleep(100);
                dynamic t = folder.GetTask("Shell");
                if (t.State != 4 /* running */ && ((DateTime)t.LastRunTime) >= started.AddSeconds(-2) && i > 2)
                    return (int)t.LastTaskResult;
            }
            _sys.Log("hop task timed out: " + verb);
            return 1;
        }

        private static void EmergencyDesktop(string why)
        {
            try { new ShellGuard(_sys).FallbackToDesktop(why); } catch { }
        }

        private static void IdleForever() { Thread.Sleep(Timeout.Infinite); }
    }
}
