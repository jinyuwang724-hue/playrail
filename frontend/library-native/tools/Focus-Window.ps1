# Test helper: bring a process's main window to the foreground (a process started from a background shell
# is not allowed to take focus, so the taskbar stays on top of the full-screen window).
param([Parameter(Mandatory)][int]$ProcessId)
if (-not ('FgWin' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FgWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static void Bring(IntPtr h) { keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); SetForegroundWindow(h); }
}
'@
}
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne 0) { [FgWin]::Bring($p.MainWindowHandle); return 'focused' }
    Start-Sleep -Milliseconds 100
}
'no window'
