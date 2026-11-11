# Lists hard-coded sizes and spacing in the Game Bar widget source. Every size must come from
# the "Layout tokens" dictionary in native/GameBarWidget/App.xaml (spec: "Game Bar 快捷面板 · 栅格系统规范").
# Allowed literals: 0, star sizes (1*, 2*), Auto. Exit code 1 when anything else is found.
param([string]$Root = (Join-Path (Split-Path -Parent $PSScriptRoot) 'native\GameBarWidget'),
      [string[]]$Files)
$ErrorActionPreference = 'Stop'
$targets = if ($Files) { $Files | ForEach-Object { Get-Item $_ } } else {
    Get-ChildItem $Root -File -Include *.xaml, *.cpp, *.h -Recurse | Where-Object { $_.FullName -notmatch '\\(Generated Files|obj|bin)\\' }
}
$xamlAttr = '\b(Margin|Padding|Width|Height|MinWidth|MinHeight|MaxWidth|MaxHeight|Spacing|RowSpacing|ColumnSpacing|FontSize|LineHeight|CornerRadius)="([^"{]*)"'
$cppCalls = '\b(Thickness\s*\{[^}]*\}|(?:Margin|Padding|Width|Height|MinWidth|MinHeight|MaxWidth|MaxHeight|Spacing|RowSpacing|ColumnSpacing|FontSize|LineHeight)\(\s*[-0-9.]+\s*\)|FromPixels\(\s*[-0-9.]+\s*\)|FromUniformRadius\(\s*[-0-9.]+\s*\)|Canvas::Set(Left|Top)\()'
$found = foreach ($file in $targets) {
    $n = 0
    foreach ($line in Get-Content $file.FullName -Encoding UTF8) {
        $n++
        if ($line -match '^\s*(//|<!--)') { continue }
        $hits = if ($file.Extension -eq '.xaml') {
            [regex]::Matches($line, $xamlAttr) | Where-Object { $_.Groups[2].Value -notmatch '^(0(,0)*|Auto|\d*\*)$' }
        } else { [regex]::Matches($line, $cppCalls) | Where-Object { $_.Value -notmatch '\(\s*0\s*\)|\{\s*\}' } }
        foreach ($hit in $hits) { [pscustomobject]@{ File = $file.Name; Line = $n; Literal = $hit.Value.Trim() } }
    }
}
if ($found) {
    $found | Format-Table -AutoSize | Out-String -Width 220 | Write-Output
    Write-Output ("{0} hard-coded size(s) outside the layout tokens." -f @($found).Count)
    exit 1
}
Write-Output 'No hard-coded sizes.'
