using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClawSleepAnimation;

internal static class SleepPowerPlan
{
    internal sealed record Setting(Guid Group, Guid Id, uint Ac, uint Dc, string Name, bool Required = true);
    private static readonly Guid Sleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    internal static readonly Setting[] Settings =
    [
        new(Sleep, new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d"), 0, 0, "Wake timers"),
        new(new("2a737441-1930-4402-8d77-b2bebba308a3"), new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"), 1, 1, "USB selective suspend"),
        new(new("54533251-82be-4824-96c1-47b60b740d00"), new("893dee8e-2bef-41e0-89c6-b55d0929964c"), 5, 2, "Minimum processor"),
        new(Sleep, new("9fe527be-1b70-48da-930d-7bcf17b44990"), 100, 100, "Standby budget", false),
        new(new("501a4d13-42af-4429-9fd1-a8218c268e20"), new("ee12f906-d277-404b-b6da-e5fa1a576df5"), 2, 2, "PCIe ASPM"),
        new(Sleep, new("9d7815a6-7ee4-497e-8888-515a05f02364"), EnhancedSleepOptions.HibernateSeconds, EnhancedSleepOptions.HibernateSeconds, "Hibernate after 48 hours"),
        new(Sleep, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), 1800, 300, "Standby idle"),
        // The legacy connectivity preference is advisory on current Windows;
        // ACS owns real network quiescing. Never disable a network device.
        new(new("fea3413e-7e05-4911-9a71-700331f1c294"), new("f15576e8-98b7-4186-b944-eafa664402d9"), 0, 0, "Standby connectivity", false)
    ];
    [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(nint root, ref Guid source, ref nint destination);
    [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(nint root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(nint root, nint scheme, nint subgroup, uint access, uint index, out Guid value, ref uint size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    private static void Check(uint value) { if (value != 0) throw new Win32Exception((int)value); }
    public static void Create(Guid original, Guid destination)
    {
        // Supply our already-journalled GUID; a crash during duplication cannot
        // leave an unidentifiable scheme behind.
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        nint supplied = pointer;
        try
        {
            Marshal.StructureToPtr(destination, pointer, false);
            Check(PowerDuplicateScheme(0, ref original, ref pointer));
            if (Marshal.PtrToStructure<Guid>(pointer) != destination) throw new InvalidOperationException("电源方案标识不一致");
        }
        finally { if (pointer != supplied) LocalFree(pointer); Marshal.FreeHGlobal(supplied); }
        foreach (var entry in Settings)
        {
            Guid group = entry.Group, setting = entry.Id, plan = destination;
            try
            {
                Check(PowerWriteACValueIndex(0, ref plan, ref group, ref setting, entry.Ac));
                Check(PowerWriteDCValueIndex(0, ref plan, ref group, ref setting, entry.Dc));
                Check(PowerReadACValueIndex(0, ref plan, ref group, ref setting, out uint ac));
                Check(PowerReadDCValueIndex(0, ref plan, ref group, ref setting, out uint dc));
                if (ac != entry.Ac || dc != entry.Dc) throw new InvalidOperationException("电源设置回读失败：" + entry.Name);
            }
            catch (Exception ex) when (!entry.Required) { Program.Log("enhanced-plan-optional", new { entry.Name, error = ex.Message }); }
        }
    }
    public static void Activate(Guid plan)
    {
        Check(PowerSetActiveScheme(0, ref plan));
        if (PowerPolicy.ActiveScheme() != plan) throw new InvalidOperationException("电源方案切换未生效");
    }
    public static bool ShouldRestore(Guid current, Guid owned) => current == owned;
    private static bool Exists(Guid scheme)
    {
        for (uint i = 0; ; i++)
        {
            uint size = 16; uint result = PowerEnumerate(0, 0, 0, 16, i, out Guid value, ref size);
            if (result == 259) return false;
            Check(result); if (value == scheme) return true;
        }
    }
    public static void Restore(Guid original, Guid owned)
    {
        if (ShouldRestore(PowerPolicy.ActiveScheme(), owned)) Activate(original);
        // ERROR_FILE_NOT_FOUND is the expected result after a crash before clone.
        if (Exists(owned)) Check(PowerDeleteScheme(0, ref owned));
    }
}
