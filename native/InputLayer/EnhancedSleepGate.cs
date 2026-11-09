using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClawInput {
    // Read-only shared signal: keep the existing virtual controller and elevated
    // engine alive while parking high-frequency XInput/device polling.
    public sealed class EnhancedSleepGate : IDisposable {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenEventW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        readonly string name;
        IntPtr awake;
        long nextOpen;
        public EnhancedSleepGate() : this("Global\\ClawEnhancedSleep.Awake-" + Process.GetCurrentProcess().SessionId) { }
        public EnhancedSleepGate(string name) { this.name = name; }
        public bool Sleeping {
            get {
                if (awake == IntPtr.Zero && DateTime.UtcNow.Ticks >= nextOpen) {
                    nextOpen = DateTime.UtcNow.AddSeconds(2).Ticks;
                    awake = OpenEventW(0x00100000, false, name); // SYNCHRONIZE only
                }
                return awake != IntPtr.Zero && WaitForSingleObject(awake, 0) == 258;
            }
        }
        public void Wait(int milliseconds) { if (awake != IntPtr.Zero) WaitForSingleObject(awake, (uint)milliseconds); }
        public void Dispose() { if (awake != IntPtr.Zero) { CloseHandle(awake); awake = IntPtr.Zero; } }
    }
}
