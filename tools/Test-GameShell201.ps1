# 实机验收：切到游戏模式，逐项看游戏库窗口是否一直在（可见、不最小化、没别的窗口时在前台），最后切回原模式。
# 从打包应用里跑要经 tools\Invoke-OutsideApp.ps1。结果写 artifacts\game-shell-201\acceptance.json。
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'artifacts\game-shell-201'
$stable=Join-Path $env:LOCALAPPDATA 'ClawToolbox\shell\ClawShell.exe'
$statePath=Join-Path $env:LOCALAPPDATA 'ClawToolbox\shell\state.json'
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);
'@
function Shell([string[]]$a) { $c=Start-Process -FilePath $stable -ArgumentList $a -WindowStyle Hidden -PassThru; $c.WaitForExit(30000) | Out-Null; $c.ExitCode }
function Lib { Get-Process ClawLibrary -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
function Surface {
    $p=Lib; if (-not $p) { return @{running=[bool](Get-Process ClawLibrary -ErrorAction SilentlyContinue);visible=$false;iconic=$false;foreground=$false} }
    $h=$p.MainWindowHandle; $fg=[W.U]::GetForegroundWindow(); [uint32]$fp=0; [W.U]::GetWindowThreadProcessId($fg,[ref]$fp) | Out-Null
    @{running=$true;visible=[W.U]::IsWindowVisible($h);iconic=[W.U]::IsIconic($h);foreground=($fp -eq $p.Id)}
}
function WaitFor([scriptblock]$ok,[int]$seconds) { $t=[Diagnostics.Stopwatch]::StartNew(); while ($t.Elapsed.TotalSeconds -lt $seconds) { $s=Surface; if (& $ok $s) { return @{ok=$true;ms=$t.ElapsedMilliseconds} }; Start-Sleep -Milliseconds 200 }; @{ok=$false;ms=$t.ElapsedMilliseconds;last=(Surface)} }
$formScript=Join-Path $out 'test-window.ps1'
Set-Content -LiteralPath $formScript -Encoding ascii -Value "Add-Type -AssemblyName System.Windows.Forms; `$f=New-Object System.Windows.Forms.Form; `$f.Text='ClawShellTest'; `$f.TopMost=`$true; `$f.Add_Shown({ `$f.Activate(); `$f.TopMost=`$false }); [System.Windows.Forms.Application]::Run(`$f)"
# 普通 Win32 测试窗口（记事本是打包应用、进程和窗口对不上）；conhost --headless 不开终端窗口
function TestApp { Start-Process conhost -ArgumentList ('--headless powershell -NoProfile -ExecutionPolicy Bypass -File "'+$formScript+'"') | Out-Null; for ($i=0;$i -lt 50;$i++) { $p=Get-Process powershell -ErrorAction SilentlyContinue | Where-Object MainWindowTitle -eq 'ClawShellTest' | Select-Object -First 1; if ($p) { return $p }; Start-Sleep -Milliseconds 200 }; throw 'test window did not open' }
$okFront={ param($s) $s.visible -and -not $s.iconic -and $s.foreground }
$okUp={ param($s) $s.visible -and -not $s.iconic }
$results=[ordered]@{}
$start=Get-Content -Raw $statePath | ConvertFrom-Json
try {
    $results.toGame=Shell @('to-game')
    $results.entry=WaitFor $okFront 8
    # 1. 有人把游戏库最小化
    [W.U]::ShowWindow((Lib).MainWindowHandle,6) | Out-Null
    $results.minimized=WaitFor $okUp 5
    # 2. 开一个窗口再关掉：没窗口了游戏库回前台
    $np=TestApp; Start-Sleep 1; $results.testWindow=[bool]$np
    $results.otherAppForeground=-not (Surface).foreground
    $np | Stop-Process -Force
    $results.afterAppClosed=WaitFor $okFront 5
    # 3. 开一个窗口再最小化它
    $np=TestApp; Start-Sleep 1; $results.testWindow=[bool]$np
    $h=$np.MainWindowHandle; [W.U]::ShowWindow($h,6) | Out-Null
    $results.afterAppMinimized=WaitFor $okFront 5
    # 4. 长按切换器的路由（窗口切换器，wp=7）：最小化的记事本应在列表里
    $msg=[W.U]::RegisterWindowMessage('ClawToolbox.Library.Activation.v1')
    [W.U]::PostMessage((Lib).MainWindowHandle,$msg,[IntPtr]7,[IntPtr]0) | Out-Null; Start-Sleep 2
    $log=Join-Path $env:LOCALAPPDATA 'Packages\ClawToolbox.Library_ba3r68zr466za\LocalCache\logs\library.log'
    $results.switcherLog=(Get-Content $log -Tail 30 -Encoding UTF8 | Select-String 'window switcher open' | Select-Object -Last 1).Line
    [W.U]::PostMessage((Lib).MainWindowHandle,$msg,[IntPtr]8,[IntPtr]0) | Out-Null; Start-Sleep 1
    $np | Stop-Process -Force
    # 5. 游戏库崩了：监视进程重新拉起，不退回桌面
    Get-Process ClawLibrary | Stop-Process -Force
    $results.afterCrash=WaitFor $okUp 25
    $results.stillGameAfterCrash=-not [bool](Get-Process explorer -ErrorAction SilentlyContinue)
}
finally {
    if ($start.mode -ne 'game') { $results.toDesktop=Shell @('to-desktop') }
    $results.state=Get-Content -Raw $statePath | ConvertFrom-Json
    $results.shellLog=@(Get-Content (Join-Path $env:LOCALAPPDATA 'ClawToolbox\logs\shell.log') -Tail 25 -Encoding UTF8 | ForEach-Object { [string]$_ })
    $results | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $out 'acceptance.json')
}
Write-Output 'done'
