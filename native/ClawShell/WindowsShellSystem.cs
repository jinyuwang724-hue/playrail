// IShellSystem 的真实实现：注册表 Winlogon Shell、explorer 启停、游戏库激活、Steam、Shift / 手柄、登录项。
// StopExplorer / IsExplorerShellRunning / Shell 值的写法参考 Playhub v2.1.0（MIT，见 PLAYHUB-LICENSE.txt）。
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace ClawShell
{
    public sealed class WindowsShellSystem : IShellSystem
    {
        public const string LibraryAumid = "ClawToolbox.Library_ba3r68zr466za!App";
        public const string LibraryFamily = "ClawToolbox.Library_ba3r68zr466za";
        public const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "ClawShell";
        public const string SupervisorMutex = @"Local\ClawToolbox.ClawShell.Supervisor";

        public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawToolbox");
        public static readonly string ShellDir = Path.Combine(Home, "shell");
        public static readonly string StableExe = Path.Combine(ShellDir, "ClawShell.exe");
        public static readonly string StatePath = Path.Combine(ShellDir, "state.json");
        public static readonly string FlagPath = Path.Combine(Home, "force-desktop.flag");
        public static readonly string LogPath = Path.Combine(Home, "logs", "shell.log");

        private readonly int _session = Process.GetCurrentProcess().SessionId;
        private readonly bool[] _padSeen = new bool[4];
        private int _padProbe;

        public DateTime Now { get { return DateTime.UtcNow; } }
        public void Sleep(int ms) { Thread.Sleep(ms); }

        public void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 512 * 1024) File.Delete(LogPath);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Process.GetCurrentProcess().Id + "] " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        // ---------- explorer ----------

        public bool ExplorerRunning()
        {
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(shell, out pid);
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                    return p.SessionId == _session && p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public bool StartExplorer()
        {
            if (ExplorerRunning()) return true;
            // Winlogon 外壳不是 explorer 时，手动启动的 explorer 只开文件夹窗口：先临时去掉 HKCU 的 Shell 值，等它成为外壳再写回。
            string saved = ReadShellValue();
            if (saved != null) DeleteShellValue();
            try
            {
                using (Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false })) { }
                for (int i = 0; i < 100; i++)
                {
                    if (ExplorerRunning()) { Log("explorer is the shell"); return true; }
                    Thread.Sleep(100);
                }
                Log("explorer started but did not become the shell");
                return false;
            }
            catch (Exception ex) { Log("start explorer: " + ex.Message); return false; }
            finally { if (saved != null) WriteShellValue(saved); }
        }

        public bool StopExplorer()
        {
            int quiet = 0;
            for (int attempt = 0; attempt < 25; attempt++)
            {
                bool found = false;
                foreach (Process p in Process.GetProcessesByName("explorer"))
                {
                    using (p)
                    {
                        try
                        {
                            if (p.SessionId != _session || p.HasExited) continue;
                            found = true;
                            if (attempt == 0) p.CloseMainWindow(); else p.Kill();
                        }
                        catch (Exception ex) { found = true; Log("stop explorer: " + ex.Message); }
                    }
                }
                quiet = found ? 0 : quiet + 1;
                if (quiet >= 5) { Log("explorer stopped"); return true; }
                Thread.Sleep(200);
            }
            return false;
        }

        // ---------- Winlogon Shell ----------

        public string ReadShellValue()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(WinlogonKey))
                return key == null ? null : key.GetValue("Shell") as string;
        }

        public void WriteShellValue(string value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(WinlogonKey))
                key.SetValue("Shell", value, RegistryValueKind.String);
            Log("shell value = " + value);
        }

        public void DeleteShellValue()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(WinlogonKey, true))
                if (key != null && key.GetValue("Shell") != null) { key.DeleteValue("Shell", false); Log("shell value deleted"); }
        }

        public static string ShellCommand() { return "\"" + StableExe + "\" shell"; }

        public string GameShellCommand()
        {
            return File.Exists(StableExe) && LibraryInstalled() ? ShellCommand() : null;
        }

        public bool IsOurShellValue()
        {
            string v = ReadShellValue();
            return v != null && v.IndexOf("ClawShell.exe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------- 游戏库 ----------

        public static bool LibraryInstalled()
        {
            uint count = 0, length = 0;
            int rc = GetPackagesByPackageFamily(LibraryFamily, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
            return (rc == 0 || rc == 122 /* ERROR_INSUFFICIENT_BUFFER */) && count > 0;
        }

        public bool LibraryRunning()
        {
            foreach (Process p in Process.GetProcessesByName("ClawLibrary"))
                using (p) { try { if (p.SessionId == _session) return true; } catch { } }
            return false;
        }

        public bool LaunchLibrary()
        {
            try
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                uint pid;
                int hr = manager.ActivateApplication(LibraryAumid, "", 0, out pid);
                Log("activate library hr=0x" + hr.ToString("x8") + " pid=" + pid);
                if (hr < 0) return false;
            }
            catch (Exception ex) { Log("activate library: " + ex.Message); return false; }
            for (int i = 0; i < 100; i++) { if (LibraryRunning()) return true; Thread.Sleep(100); }
            return false;
        }

        // ---------- Steam ----------

        private static string SteamExe()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string exe = key == null ? null : key.GetValue("SteamExe") as string;
                return !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : null;
            }
        }

        public bool SteamInstalled() { return SteamExe() != null; }

        public bool SteamRunning()
        {
            foreach (Process p in Process.GetProcessesByName("steam"))
                using (p) { try { if (p.SessionId == _session) return true; } catch { } } // 不查 HasExited：Steam 以管理员运行时拿不到它的句柄，会抛拒绝访问
            return false;
        }

        public bool StartSteam()
        {
            try
            {
                // 工具箱的「Steam 以管理员运行」登录任务在就走它，和平时开机一样
                var run = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"\\ClawToolbox\\SteamAdmin\"") { UseShellExecute = false, CreateNoWindow = true });
                run.WaitForExit(10000);
                if (run.ExitCode != 0) Process.Start(new ProcessStartInfo(SteamExe(), "-silent") { UseShellExecute = false }).Dispose();
            }
            catch (Exception ex) { Log("start steam: " + ex.Message); return false; }
            for (int i = 0; i < 200; i++) { if (SteamRunning()) return true; Thread.Sleep(100); }
            Log("steam did not start");
            return false;
        }

        // ---------- 强制桌面 ----------

        public bool ShiftHeld() { return (GetAsyncKeyState(0x10) & 0x8000) != 0; }

        /// <summary>任意手柄按住 视图（Back）+ 菜单（Start）。没接的槽位每 2 秒才探一次，避免 XInput 查空槽的开销。</summary>
        public bool PadComboHeld()
        {
            bool probeEmpty = (_padProbe++ % 20) == 0;
            for (uint i = 0; i < 4; i++)
            {
                if (!_padSeen[i] && !probeEmpty) continue;
                XINPUT_STATE state;
                int rc;
                try { rc = XInputGetState(i, out state); } catch { return false; }
                _padSeen[i] = rc == 0;
                if (rc == 0 && (state.Gamepad.wButtons & 0x0030) == 0x0030) return true;
            }
            return false;
        }

        public bool ForceFlagExists() { return File.Exists(FlagPath); }

        // ---------- 登录项（没有 explorer 时由我们代跑） ----------

        public void RunStartupApps()
        {
            RunRegistry(Registry.CurrentUser, RunKey, "Run");
            RunRegistry(Registry.LocalMachine, RunKey, "Run");
            RunRegistry(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32");
            RunFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser);
            RunFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine);
            // 记成「已经跑过」，之后切到桌面时 explorer 不会再跑一遍
            try
            {
                string info = @"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\" + _session;
                foreach (string name in new[] { "StartupHasBeenRun", "RunStuffHasBeenRun" })
                    using (Registry.CurrentUser.CreateSubKey(info + "\\" + name, RegistryKeyPermissionCheck.Default, RegistryOptions.Volatile)) { }
            }
            catch (Exception ex) { Log("mark startup run: " + ex.Message); }
        }

        private static bool Approved(RegistryKey hive, string kind, string name)
        {
            using (RegistryKey key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + kind))
            {
                byte[] data = key == null ? null : key.GetValue(name) as byte[];
                return data == null || data.Length == 0 || (data[0] & 1) == 0;
            }
        }

        private void RunRegistry(RegistryKey hive, string path, string kind)
        {
            using (RegistryKey key = hive.OpenSubKey(path))
            {
                if (key == null) return;
                foreach (string name in key.GetValueNames())
                {
                    if (name == RunValue || !Approved(hive, kind, name)) continue;
                    string command = key.GetValue(name) as string;
                    if (string.IsNullOrWhiteSpace(command)) continue;
                    try
                    {
                        string exe, args;
                        SplitCommand(Environment.ExpandEnvironmentVariables(command), out exe, out args);
                        using (Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" })) { }
                        Log("startup: " + name);
                    }
                    catch (Exception ex) { Log("startup " + name + ": " + ex.Message); }
                }
            }
        }

        private void RunFolder(string folder, RegistryKey hive)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (string file in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || !Approved(hive, "StartupFolder", name)) continue;
                try { using (Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })) { } Log("startup: " + name); }
                catch (Exception ex) { Log("startup " + name + ": " + ex.Message); }
            }
        }

        public static void SplitCommand(string command, out string exe, out string args)
        {
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                exe = command.Substring(1, end - 1);
                args = command.Substring(end + 1).Trim();
                return;
            }
            int dot = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (dot < 0) { int space = command.IndexOf(' '); exe = space < 0 ? command : command.Substring(0, space); args = space < 0 ? "" : command.Substring(space + 1).Trim(); return; }
            exe = command.Substring(0, dot + 4);
            args = command.Substring(dot + 4).Trim();
        }

        // ---------- 监视进程 / 状态 ----------

        public void EnsureRunEntry()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                string want = "\"" + StableExe + "\" watch";
                if (!want.Equals(key.GetValue(RunValue) as string)) { key.SetValue(RunValue, want, RegistryValueKind.String); Log("run entry set"); }
            }
            try { EnsureDesktopShortcut(); } catch (Exception ex) { Log("desktop shortcut: " + ex.Message); }
        }

        public static string ShortcutPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "返回游戏模式.lnk"); } }

        /// <summary>桌面上的「返回游戏模式」快捷方式（ClawShell.exe to-game），图标借游戏库的。</summary>
        private void EnsureDesktopShortcut()
        {
            string path = ShortcutPath;
            if (File.Exists(path)) return;
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            try
            {
                object link = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { StableExe });
                lt.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { "to-game" });
                lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { "返回游戏模式" });
                lt.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { Path.Combine(Environment.SystemDirectory, "imageres.dll") + ",-1009" });
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
                Marshal.FinalReleaseComObject(link);
                Log("desktop shortcut created");
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        public void EnsureSupervisor()
        {
            try { EnsureRunEntry(); } catch (Exception ex) { Log("run entry: " + ex.Message); }
            Mutex existing;
            if (Mutex.TryOpenExisting(SupervisorMutex, out existing)) { existing.Dispose(); return; }
            try { using (Process.Start(new ProcessStartInfo(StableExe, "watch") { UseShellExecute = false, CreateNoWindow = true })) { } Log("supervisor started"); }
            catch (Exception ex) { Log("start supervisor: " + ex.Message); }
        }

        public void WriteState(string mode, string message)
        {
            try
            {
                Directory.CreateDirectory(ShellDir);
                string json = "{\"mode\":\"" + mode + "\",\"default\":\"" + (IsOurShellValue() ? "game" : "desktop") + "\",\"explorer\":" + (ExplorerRunning() ? "true" : "false")
                    + ",\"forceFlag\":" + (ForceFlagExists() ? "true" : "false") + ",\"installed\":" + (File.Exists(StableExe) && LibraryInstalled() ? "true" : "false")
                    + ",\"message\":\"" + (message ?? "").Replace("\\", "\\\\").Replace("\"", "'") + "\",\"version\":\"" + Program.Version + "\",\"at\":\"" + DateTime.Now.ToString("s") + "\"}";
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null); else File.Move(tmp, StatePath);
                Log("state " + mode + " " + message);
            }
            catch (Exception ex) { Log("write state: " + ex.Message); }
        }

        /// <summary>把自己复制到 %LOCALAPPDATA%\ClawToolbox\shell（Shell 值和登录项都指向这里，应用包升级换目录也不受影响）。</summary>
        public void InstallSelf()
        {
            string self = Process.GetCurrentProcess().MainModule.FileName;
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(StableExe), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(ShellDir);
            if (File.Exists(StableExe) && SameFile(self, StableExe)) return;
            foreach (string old in Directory.GetFiles(ShellDir, "ClawShell.old-*")) { try { File.Delete(old); } catch { } }
            string temp = StableExe + ".new";
            File.Copy(self, temp, true);
            if (File.Exists(StableExe))
            {
                // 监视进程可能正在用旧文件：改名让开（运行中的 exe 可以改名，不能覆盖）
                File.Move(StableExe, StableExe.Replace("ClawShell.exe", "ClawShell.old-" + DateTime.Now.Ticks));
            }
            File.Move(temp, StableExe);
            Log("installed " + Program.Version + " to " + StableExe);
        }

        private static bool SameFile(string a, string b)
        {
            using (var sha = SHA256.Create())
            {
                byte[] x = sha.ComputeHash(File.ReadAllBytes(a)), y = sha.ComputeHash(File.ReadAllBytes(b));
                return Convert.ToBase64String(x) == Convert.ToBase64String(y);
            }
        }

        public static bool InPackage()
        {
            uint length = 0;
            return GetCurrentPackageFullName(ref length, null) != 15700 /* APPMODEL_ERROR_NO_PACKAGE */;
        }

        // ---------- Win32 ----------

        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);
        [DllImport("xinput1_4.dll")] private static extern int XInputGetState(uint index, out XINPUT_STATE state);

        [StructLayout(LayoutKind.Sequential)] private struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
        [StructLayout(LayoutKind.Sequential)] private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
        }
        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ApplicationActivationManager { }
    }
}
