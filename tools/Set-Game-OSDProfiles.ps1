param([string[]]$GameExecutableNames = @('Client-Win64-Shipping.exe','ZenlessZoneZero.exe'))
$ErrorActionPreference = 'Stop'
$taskRtssProfiles = 'C:\Software\RivaTuner Statistics Server\Profiles'
$taskBackupRoot = Join-Path $PSScriptRoot '..\diagnostics\motion-osd-backup-20261006'
[IO.Directory]::CreateDirectory($taskBackupRoot) | Out-Null
$taskManifestPath = Join-Path $taskBackupRoot 'game-profiles.json'
$taskManifest = @()
if (Test-Path -LiteralPath $taskManifestPath) { $taskManifest = @(Get-Content -Raw -LiteralPath $taskManifestPath | ConvertFrom-Json) }
foreach ($taskGameName in $GameExecutableNames | Sort-Object -Unique) {
    if ([IO.Path]::GetFileName($taskGameName) -ne $taskGameName -or $taskGameName -notmatch '\.exe$') { throw 'Expected an executable filename, not a path.' }
    $taskPath = Join-Path $taskRtssProfiles $taskGameName
    $taskExisting = Test-Path -LiteralPath $taskPath
    if (-not ($taskManifest | Where-Object { $_.Name -eq $taskGameName })) {
        if ($taskExisting) { Copy-Item -LiteralPath $taskPath -Destination (Join-Path $taskBackupRoot $taskGameName) }
        $taskManifest += [pscustomobject]@{Name=$taskGameName;PreviouslyExisted=$taskExisting}
    }
    if ($taskExisting) {
        # Existing profiles are edited through the SDK below; preserve unrelated settings.
    } else {
        [IO.File]::WriteAllText($taskPath, "[Hooking]`r`nEnableHooking=1`r`n[OSD]`r`nEnableOSD=1`r`n", (New-Object Text.UTF8Encoding($false)))
    }
}
$taskManifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $taskManifestPath -Encoding UTF8
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class GameOsdProfiles {
 const string Dll=@"C:\Software\RivaTuner Statistics Server\RTSSHooks64.dll";
 [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] public static extern void LoadProfile(string name);
 [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] static extern void SaveProfile(string name);
 [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] static extern bool SetProfileProperty(string name,ref uint data,uint size);
 [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] public static extern bool GetProfileProperty(string name,out uint data,uint size);
 [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] public static extern void UpdateProfiles();
 public static void Apply(string name){LoadProfile(name);uint value=1;if(!SetProfileProperty("AppDetectionLevel",ref value,4)||!SetProfileProperty("EnableOSD",ref value,4))throw new Exception("RTSS profile update failed: "+name);SaveProfile(name);}
 public static string Read(string name){LoadProfile(name);uint level,osd;GetProfileProperty("AppDetectionLevel",out level,4);GetProfileProperty("EnableOSD",out osd,4);return name+": Detection="+level+" OSD="+osd;}
}
'@
foreach ($taskGameName in $GameExecutableNames | Sort-Object -Unique) {
    [GameOsdProfiles]::Apply($taskGameName)
    [GameOsdProfiles]::Read($taskGameName)
}
[GameOsdProfiles]::UpdateProfiles()
