$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ZzzTokenDiagnostic {
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int length, out int returned);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  public static string ReadElevation(int pid) {
    IntPtr process=OpenProcess(0x1000,false,pid), token=IntPtr.Zero;
    if(process==IntPtr.Zero) return "OpenProcess error="+Marshal.GetLastWin32Error();
    try {
      if(!OpenProcessToken(process,8,out token)) return "OpenProcessToken error="+Marshal.GetLastWin32Error();
      int value, returned;
      if(!GetTokenInformation(token,20,out value,4,out returned)) return "GetTokenInformation error="+Marshal.GetLastWin32Error();
      return value==0 ? "Not elevated" : "Elevated";
    } finally { if(token!=IntPtr.Zero) CloseHandle(token); CloseHandle(process); }
  }
}
'@
Get-Process | Where-Object { $_.ProcessName -match '^(steam|ZenlessZoneZero|Client-Win64-Shipping|launcher|Playhub.GameSession|gameoverlayui64|gameoverlayui)$' } | ForEach-Object {
    [pscustomobject]@{ Name=$_.ProcessName; ProcessId=$_.Id; Elevation=[ZzzTokenDiagnostic]::ReadElevation($_.Id); StartTime=$_.StartTime }
} | ConvertTo-Json
foreach ($compatKey in @('HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers','HKLM:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers')) {
    if (Test-Path -LiteralPath $compatKey) {
        (Get-ItemProperty -LiteralPath $compatKey).PSObject.Properties | Where-Object { $_.Name -match '(?i)(\\steam\.exe$|\\ZenlessZoneZero\.exe$)' } | ForEach-Object {
            [pscustomobject]@{ RegistryKey=$compatKey; Executable=$_.Name; Flags=$_.Value }
        } | ConvertTo-Json
    }
}
