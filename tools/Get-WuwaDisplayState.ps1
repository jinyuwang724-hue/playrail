$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WuwaDisplayDiagnostic {
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
  [DllImport("shcore.dll")] public static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr process);
}
'@
$taskOldContext = [WuwaDisplayDiagnostic]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
try {
  foreach ($taskProcess in (Get-Process -Name 'Client-Win64-Shipping' -ErrorAction SilentlyContinue)) {
    $taskWindow = $taskProcess.MainWindowHandle
    $taskRect = [WuwaDisplayDiagnostic+Rect]::new()
    $taskMonitor = [WuwaDisplayDiagnostic+MonitorInfo]::new()
    $taskMonitor.Size = [Runtime.InteropServices.Marshal]::SizeOf($taskMonitor)
    $taskAwareness = 0
    $taskHandle = [WuwaDisplayDiagnostic]::OpenProcess(0x1000, $false, $taskProcess.Id)
    $taskAwarenessResult = if ($taskHandle -ne [IntPtr]::Zero) {
      try { [WuwaDisplayDiagnostic]::GetProcessDpiAwareness($taskHandle, [ref]$taskAwareness) }
      finally { [WuwaDisplayDiagnostic]::CloseHandle($taskHandle) | Out-Null }
    } else { -1 }
    $taskRectValid = [WuwaDisplayDiagnostic]::GetWindowRect($taskWindow, [ref]$taskRect)
    $taskMonitorValid = [WuwaDisplayDiagnostic]::GetMonitorInfo([WuwaDisplayDiagnostic]::MonitorFromWindow($taskWindow, 2), [ref]$taskMonitor)
    [pscustomobject]@{
      PID=$taskProcess.Id; Window=$taskWindow.ToInt64(); WindowDpi=[WuwaDisplayDiagnostic]::GetDpiForWindow($taskWindow)
      ProcessAwareness=$taskAwareness; AwarenessResult=$taskAwarenessResult
      WindowRect=if($taskRectValid){$taskRect}else{$null}; MonitorRect=if($taskMonitorValid){$taskMonitor.Monitor}else{$null}
    } | ConvertTo-Json -Depth 3
  }
} finally { [WuwaDisplayDiagnostic]::SetThreadDpiAwarenessContext($taskOldContext) | Out-Null }
