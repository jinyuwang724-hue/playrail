# Restore RTSS's normal default detection, retaining game overrides and limiter settings.
# Without -Apply this only reads the effective profiles.
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDir = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\WOW6432Node\Unwinder\RTSS').InstallDir
$taskProfiles = Join-Path $taskDir 'Profiles'
if ($taskDir -ne 'C:\Software\RivaTuner Statistics Server') { throw 'Rebase the SDK import for this RTSS installation first.' }
if (-not ('ClawRtssAutoDetection' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ClawRtssAutoDetection {
 const string Dll = @"C:\Software\RivaTuner Statistics Server\RTSSHooks64.dll";
 [DllImport(Dll, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi)]
 static extern void LoadProfile(string name);
 [DllImport(Dll, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi)]
 static extern void SaveProfile(string name);
 [DllImport(Dll, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi)]
 static extern bool GetProfileProperty(string name, out uint data, uint size);
 [DllImport(Dll, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi)]
 static extern bool SetProfileProperty(string name, ref uint data, uint size);
 [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
 public static extern void UpdateProfiles();
 static uint ReadInt(string name) {
  uint value;
  if (!GetProfileProperty(name, out value, 4)) throw new Exception("RTSS does not expose " + name);
  return value;
 }
 public static Dictionary<string,object> Read(string profile) {
  LoadProfile(profile);
  return new Dictionary<string,object> {
   {"profile",profile.Length==0?"Global":profile},
   {"detection",ReadInt("AppDetectionLevel")},{"osd",ReadInt("EnableOSD")},
   {"fpsLimit",ReadInt("FramerateLimit")}, {"zoom",ReadInt("ZoomRatio")}
  };
 }
 public static void Set(string profile, uint detection, uint osd) {
  LoadProfile(profile);
  if (!SetProfileProperty("AppDetectionLevel",ref detection,4) ||
      !SetProfileProperty("EnableOSD",ref osd,4)) throw new Exception("RTSS profile update failed");
  SaveProfile(profile);
 }
}
'@
}
$taskBefore = [ClawRtssAutoDetection]::Read('')
$taskBackup = $null
if ($Apply) {
    $taskBackup = Join-Path $taskRoot ('diagnostics\rtss-auto-detection-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    [IO.Directory]::CreateDirectory($taskBackup) | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskProfiles 'Global') -Destination $taskBackup
    $taskLibraryProfiles = @('ClawLibrary.exe','ClawLibrary.exe.cfg')
    $taskExistingLibrary = @($taskLibraryProfiles | Where-Object { Test-Path -LiteralPath (Join-Path $taskProfiles $_) })
    foreach ($taskName in $taskExistingLibrary) { Copy-Item -LiteralPath (Join-Path $taskProfiles $taskName) -Destination $taskBackup }
    # The library uses Direct3D for its own interface; it should not receive a game OSD.
    if ($taskExistingLibrary.Count -eq 0) { [ClawRtssAutoDetection]::Set('ClawLibrary.exe', 0, 0) }
    [ClawRtssAutoDetection]::Set('', 1, 1)
    [ClawRtssAutoDetection]::UpdateProfiles()
}
$taskAfter = [ClawRtssAutoDetection]::Read('')
if ($Apply -and ($taskAfter.detection -ne 1 -or $taskAfter.osd -ne 1 -or $taskAfter.fpsLimit -ne $taskBefore.fpsLimit -or $taskAfter.zoom -ne $taskBefore.zoom)) {
    throw 'RTSS readback did not match the scoped change; inspect the backup.'
}
$taskReport = @{before=$taskBefore;after=$taskAfter;backup=$taskBackup;
    endfield=[ClawRtssAutoDetection]::Read('Endfield.exe');library=[ClawRtssAutoDetection]::Read('ClawLibrary.exe')}
if ($Apply) {
    $taskReport['previousLibraryProfiles'] = @($taskExistingLibrary)
    $taskReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskBackup 'deployment.json') -Encoding utf8
}
$taskReport | ConvertTo-Json -Depth 5
