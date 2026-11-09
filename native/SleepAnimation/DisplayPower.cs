using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClawSleepAnimation;

// Read-only notifications. Never asserts a display/system execution request.
internal sealed class DisplayPower : IDisposable
{
    internal static Guid Session = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    private delegate uint Callback(nint context, uint type, nint setting);
    [StructLayout(LayoutKind.Sequential)] private struct Subscription { public Callback Callback; public nint Context; }
    [DllImport("powrprof.dll")] private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref Subscription recipient, out nint registration);
    [DllImport("powrprof.dll")] private static extern uint PowerSettingUnregisterNotification(nint registration);
    private readonly Callback callback;
    private readonly nint registration;
    public volatile bool On = true;
    public DateTime OnAt = DateTime.UtcNow;
    public DisplayPower()
    {
        callback = (_, type, setting) =>
        {
            if (type == 0x8013 && setting != 0 && Marshal.ReadInt32(setting, 16) == 4)
            {
                bool next = Marshal.ReadInt32(setting, 20) != 0;
                if (next) OnAt = DateTime.UtcNow;
                On = next;
            }
            return 0;
        };
        var subscription = new Subscription { Callback = callback };
        Guid guid = Process.GetCurrentProcess().SessionId == 0 ? new("6fe69556-704a-47a0-8f24-c28d936fda47") : Session;
        uint result = PowerSettingRegisterNotification(ref guid, 2, ref subscription, out registration);
        if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
    }
    public void Dispose() { PowerSettingUnregisterNotification(registration); GC.KeepAlive(callback); }
}
