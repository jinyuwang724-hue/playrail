using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClawSleepAnimation;

// Native callbacks keep running when WPF is showing/holding a sleep animation.
// They only enqueue signals; the single consumer owns all mutable sleep state.
internal sealed class EnhancedSleepNotifications : IDisposable
{
    private delegate uint Callback(nint context, uint type, nint setting);
    [StructLayout(LayoutKind.Sequential)] private struct Subscription { public Callback Callback; public nint Context; }
    [DllImport("powrprof.dll")] private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref Subscription subscription, out nint registration);
    [DllImport("powrprof.dll")] private static extern uint PowerSettingUnregisterNotification(nint registration);
    [DllImport("powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Subscription subscription, out nint registration);
    [DllImport("powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(nint registration);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder text, int length, out int needed);
    private readonly Callback power, setting;
    private readonly List<nint> settings = [];
    private nint resume;
    private bool? lid, present;
    public static bool SessionLocked()
    {
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return true;
        try
        {
            var name = new StringBuilder(256);
            return !GetUserObjectInformationW(desktop, 2, name, name.Capacity * 2, out _) || !name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
    public EnhancedSleepNotifications(Action<SleepSignal, bool> signal)
    {
        Guid display = DisplayPower.Session;
        Guid lidGuid = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
        Guid presence = new("3c0f4548-c03f-4c4d-b9f2-237ede686376");
        power = (_, type, _) =>
        {
            try
            {
                if (type == 4) signal(SleepSignal.Suspend, true);
                if (type == 18) signal(SleepSignal.ResumeAutomatic, false);
                if (type == 7) signal(SleepSignal.ResumeInteractive, false);
            }
            catch (Exception ex) { Program.Log("enhanced-notification-error", ex.Message); }
            return 0;
        };
        setting = (_, type, data) =>
        {
            try
            {
                if (type != 0x8013 || data == 0 || Marshal.ReadInt32(data, 16) != 4) return 0;
                Guid id = Marshal.PtrToStructure<Guid>(data); int value = Marshal.ReadInt32(data, 20);
                if (id == display) signal(value == 0 ? SleepSignal.DisplayOff : SleepSignal.DisplayOn, false);
                else if (id == lidGuid) { bool next = value != 0; if (lid == false && next) signal(SleepSignal.LidOpened, false); lid = next; }
                else if (id == presence) { bool next = value == 0; if (present == false && next) signal(SleepSignal.UserPresent, false); present = next; }
            }
            catch (Exception ex) { Program.Log("enhanced-notification-error", ex.Message); }
            return 0;
        };
        try
        {
            var p = new Subscription { Callback = power };
            uint result = PowerRegisterSuspendResumeNotification(2, ref p, out resume);
            if (result != 0) throw new Win32Exception((int)result);
            foreach (Guid item in new[] { display, lidGuid, presence })
            {
                Guid guid = item; var s = new Subscription { Callback = setting };
                result = PowerSettingRegisterNotification(ref guid, 2, ref s, out nint handle);
                if (result != 0) throw new Win32Exception((int)result);
                settings.Add(handle);
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (resume != 0) { PowerUnregisterSuspendResumeNotification(resume); resume = 0; }
        foreach (nint handle in settings) PowerSettingUnregisterNotification(handle);
        settings.Clear(); GC.KeepAlive(power); GC.KeepAlive(setting);
    }
}
