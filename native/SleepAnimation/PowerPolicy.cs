using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClawSleepAnimation;

internal sealed record PolicyBackup(Guid Scheme, uint Ac, uint Dc);

internal static class PowerPolicy
{
    private static Guid Subgroup = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static Guid Setting = new("7648efa3-dd9c-4e3e-b566-50f929386280");
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root, out nint scheme);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint root, ref Guid scheme);

    private static void Check(uint result) { if (result != 0) throw new Win32Exception((int)result); }
    public static Guid ActiveScheme()
    {
        Check(PowerGetActiveScheme(0, out nint pointer));
        try { return Marshal.PtrToStructure<Guid>(pointer); } finally { LocalFree(pointer); }
    }
    private static uint Read(Guid scheme, bool ac)
    {
        uint value; Check(ac ? PowerReadACValueIndex(0, ref scheme, ref Subgroup, ref Setting, out value) :
            PowerReadDCValueIndex(0, ref scheme, ref Subgroup, ref Setting, out value)); return value;
    }
    private static void Write(Guid scheme, bool ac, uint value) => Check(ac ?
        PowerWriteACValueIndex(0, ref scheme, ref Subgroup, ref Setting, value) :
        PowerWriteDCValueIndex(0, ref scheme, ref Subgroup, ref Setting, value));
    public static PolicyBackup Read() { Guid scheme = ActiveScheme(); return new(scheme, Read(scheme, true), Read(scheme, false)); }
    public static void SetAction(uint value)
    {
        if (value is < 1 or > 3) throw new ArgumentException("不支持的电源键动作");
        var previous = Read(); Guid scheme = previous.Scheme;
        try { Write(scheme, true, value); Write(scheme, false, value); Check(PowerSetActiveScheme(0, ref scheme)); }
        catch { Write(scheme, true, previous.Ac); Write(scheme, false, previous.Dc); PowerSetActiveScheme(0, ref scheme); throw; }
    }
    public static void Save(string path, PolicyBackup backup)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(backup)); File.Move(path + ".tmp", path, true);
    }
    public static PolicyBackup Load(string path)
    {
        var b = JsonSerializer.Deserialize<PolicyBackup>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing power policy backup");
        if (b.Scheme == Guid.Empty || b.Ac > 4 || b.Dc > 4) throw new InvalidDataException("Invalid power policy backup");
        return b;
    }
    public static void Claim(PolicyBackup backup)
    {
        if (Read() != backup) throw new InvalidOperationException("Power policy changed before activation");
        Guid scheme = backup.Scheme;
        try { Write(scheme, true, 0); Write(scheme, false, 0); Check(PowerSetActiveScheme(0, ref scheme)); }
        catch { Restore(backup); throw; }
    }
    public static bool StillClaimed(PolicyBackup backup, string? state = null)
    {
        var now = Read();
        bool ours = now.Scheme == backup.Scheme || state != null && new SleepJournal(state).IsOwnedPlan(backup.Scheme, now.Scheme);
        return ours && now.Ac == 0 && now.Dc == 0;
    }
    public static void Restore(PolicyBackup backup)
    {
        // Preserve an explicit change made in Settings while the helper was running.
        if (Read(backup.Scheme, true) == 0) Write(backup.Scheme, true, backup.Ac);
        if (Read(backup.Scheme, false) == 0) Write(backup.Scheme, false, backup.Dc);
        Guid scheme = backup.Scheme;
        if (ActiveScheme() == scheme) Check(PowerSetActiveScheme(0, ref scheme));
    }
}
