using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClawSleepAnimation;

internal static class Hibernate
{
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Flag, Percent, Status; public uint Life, FullLife; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint w, nint l);
    public static uint CurrentAction(PolicyBackup policy)
    {
        if (policy.Ac == policy.Dc) return policy.Ac;
        if (!GetSystemPowerStatus(out var status) || status.Ac > 1) throw new InvalidOperationException("暂时无法确认供电状态");
        return status.Ac == 1 ? policy.Ac : policy.Dc;
    }
    public static void Sleep(nint overlay)
    {
        // This device uses Modern Standby. Turning its visible overlay's display
        // off enters the same S0 path used by the existing library's Sleep action.
        if (overlay == 0 || !PostMessage(overlay, 0x112, 0xf170, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Privilege { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref Privilege state, int length, out Privilege previous, out int returned);
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);
    public static void Enter()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out Luid luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested = new Privilege { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref requested, Marshal.SizeOf<Privilege>(), out Privilege previous, out _) || Marshal.GetLastWin32Error() == 1300)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { if (!SetSuspendState(true, false, false)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            finally { AdjustTokenPrivileges(token, false, ref previous, 0, out _, out _); }
        }
    }
}
