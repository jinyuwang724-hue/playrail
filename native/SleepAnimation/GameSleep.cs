using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClawSleepAnimation;

internal static class GameSleep
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    { "system", "registry", "smss", "csrss", "wininit", "services", "lsass", "winlogon", "svchost", "dwm", "explorer", "sihost", "fontdrvhost",
      "steam", "steamwebhelper", "gameoverlayui", "gameoverlayui64", "eadesktop", "epicgameslauncher", "epicwebhelper", "xboxpcapp",
      "cmd", "powershell", "pwsh", "conhost", "openconsole", "windowsterminal", "codex", "chrome", "msedge", "firefox", "taskmgr",
      "lockapp", "logonui", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "runtimebroker", "applicationframehost",
      "gamebar", "gamebarftserver", "gamingservices", "gamingservicesnet", "rtss", "rtsshooksloader64", "losslessscaling", "magpie" };
    internal static bool NeverFreeze(string name) => Protected.Contains(name) || name.StartsWith("Claw", StringComparison.OrdinalIgnoreCase) || AntiCheat(name);
    private static bool AntiCheat(string name) => name.Contains("anticheat", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("BEService", StringComparison.OrdinalIgnoreCase) || name.StartsWith("EasyAnti", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("vgc", StringComparison.OrdinalIgnoreCase) || name.Equals("vgtray", StringComparison.OrdinalIgnoreCase) || name.Equals("ACE-Service64", StringComparison.OrdinalIgnoreCase);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry
    {
        public uint Size, Usage, Pid; public nuint Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref Entry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CheckRemoteDebuggerPresent(SafeProcessHandle process, out bool present);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(SafeProcessHandle process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(SafeProcessHandle process);
    private static FrozenGame Identity(SafeProcessHandle handle, int pid)
    {
        uint size = 32768; var path = new StringBuilder((int)size);
        if (!GetProcessTimes(handle, out long created, out _, out _, out _) || !QueryFullProcessImageNameW(handle, 0, path, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(pid, DateTime.FromFileTimeUtc(created).Ticks, path.ToString());
    }
    public static FrozenGame Validate(int pid)
    {
        using var process = Process.GetProcessById(pid);
        if (process.SessionId != Process.GetCurrentProcess().SessionId || NeverFreeze(process.ProcessName)) throw new InvalidOperationException("该进程不支持游戏暂停：" + process.ProcessName);
        using var handle = OpenProcess(0x1c00, false, pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!OpenProcessToken(handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var self = WindowsIdentity.GetCurrent())
            if (identity.User != self.User) throw new InvalidOperationException("游戏不属于当前用户");
        if (!IsProcessCritical(handle, out bool critical) || !CheckRemoteDebuggerPresent(handle, out bool debugged))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (critical || debugged) throw new InvalidOperationException("该进程不能安全暂停");
        var record = Identity(handle, pid);
        if (record.Image.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能暂停系统程序");
        return record;
    }
    internal static bool Matches(FrozenGame expected, FrozenGame actual) => expected.Pid == actual.Pid && expected.Started == actual.Started && expected.Image.Equals(actual.Image, StringComparison.OrdinalIgnoreCase);
    public static void Suspend(FrozenGame root, SleepJournal journal, Func<bool> cancelled)
    {
        var started = Stopwatch.StartNew();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entries = new List<Entry>(); var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>() };
        if (Process32FirstW(snapshot, ref entry)) do { entries.Add(entry); } while (Process32NextW(snapshot, ref entry));
        if (entries.Any(e => AntiCheat(System.IO.Path.GetFileNameWithoutExtension(e.Exe))))
            throw new InvalidOperationException("检测到反作弊服务，本次跳过游戏暂停");
        var ids = new List<int> { root.Pid };
        for (int i = 0; i < ids.Count; i++)
            foreach (var child in entries.Where(e => e.Parent == ids[i] && !ids.Contains((int)e.Pid)))
            {
                if (NeverFreeze(System.IO.Path.GetFileNameWithoutExtension(child.Exe))) continue;
                if (ids.Count >= 128) throw new InvalidOperationException("游戏进程树过大"); ids.Add((int)child.Pid);
            }
        // Protected hosts are excluded before traversal. Every eligible game
        // process must validate before the first mutation.
        var candidates = ids.Select(Validate).ToArray();
        if (!Matches(root, candidates[0]) || candidates.Any(c => c.Started < root.Started)) throw new InvalidOperationException("游戏进程已变化");
        try
        {
            foreach (var game in candidates)
            {
                if (cancelled() || started.Elapsed > TimeSpan.FromSeconds(2)) throw new OperationCanceledException("游戏暂停已过期");
                using var handle = OpenProcess(0x1800, false, game.Pid);
                if (handle.IsInvalid || !Matches(game, Identity(handle, game.Pid))) throw new InvalidOperationException("游戏进程已退出");
                var r = journal.Read() ?? throw new InvalidOperationException("恢复记录丢失");
                journal.Write(r with { Games = [..r.Games, game] }); // Intent precedes native mutation.
                if (cancelled()) { journal.Write(r); throw new OperationCanceledException("游戏暂停已过期"); }
                int result = NtSuspendProcess(handle);
                if (result != 0)
                {
                    journal.Write(r); // Native call failed; this process was never ours to resume.
                    throw new InvalidOperationException($"游戏暂停失败（{result:X}）");
                }
            }
        }
        catch
        {
            var r = journal.Read();
            if (r != null)
                foreach (var game in r.Games.Reverse())
                    try { Resume(game); r = r with { Games = r.Games.Where(p => p != game).ToArray() }; journal.Write(r); }
                    catch (Exception ex) { Program.Log("enhanced-game-rollback-pending", ex.Message); }
            throw;
        }
    }
    public static void Resume(FrozenGame game)
    {
        using var handle = OpenProcess(0x1800, false, game.Pid);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 87) return; // Process exited.
            throw new Win32Exception(error);
        }
        if (!Matches(game, Identity(handle, game.Pid))) return; // PID reused.
        int result = NtResumeProcess(handle);
        if (result != 0) throw new InvalidOperationException($"游戏恢复失败（{result:X}）");
    }
}
