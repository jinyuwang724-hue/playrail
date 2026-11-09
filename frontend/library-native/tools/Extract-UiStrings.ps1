param([string]$Root, [string]$Out)
# Collects display strings (Chinese literals) from the library's C# sources.
# Interpolated strings become templates: every {expr} turns into {0}, {1}, ...
$keys = [ordered]@{}
$cjk = '[\u3400-\u9fff\uff00-\uffef\u3000-\u303f]'
foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -Filter *.cs) {
    if ($file.Name -in 'LineIcons.cs', 'TextKit.cs') { continue }
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        $code = $line
        $trim = $code.TrimStart()
        if ($trim.StartsWith('//') -or $trim.StartsWith('///') -or $trim.StartsWith('*')) { continue }
        if ($code -match 'Log\.(Info|Warn|Error|Debug)|throw new|GeneratedRegex|Exception\(') { continue }
        # strip trailing // comment (naive: only when not inside a string)
        $i = 0; $inStr = $false; $cut = -1
        while ($i -lt $code.Length - 1) {
            $c = $code[$i]
            if ($c -eq '"' -and ($i -eq 0 -or $code[$i - 1] -ne '\')) { $inStr = -not $inStr }
            elseif (-not $inStr -and $c -eq '/' -and $code[$i + 1] -eq '/') { $cut = $i; break }
            $i++
        }
        if ($cut -ge 0) { $code = $code.Substring(0, $cut) }
        foreach ($m in [regex]::Matches($code, '(\$@|@\$|\$|@)?"((?:[^"\\]|\\.)*)"')) {
            $prefix = $m.Groups[1].Value; $body = $m.Groups[2].Value
            if ($body -notmatch $cjk) { continue }
            $body = $body -replace '\\n', "`n" -replace '\\"', '"' -replace '\\\\', '\'
            if ($prefix -like '*$*') {
                $n = 0
                $body = [regex]::Replace($body, '\{\{|\}\}|\{[^{}]*\}', { param($x) if ($x.Value -eq '{{') { '{' } elseif ($x.Value -eq '}}') { '}' } else { '{' + ($script:n++) + '}' } })
                $script:n = 0
            }
            if ($body.Contains("`n") -or $body.Contains("`t")) { $body = $body -replace "`n", '\n' -replace "`t", '\t' }
            if (-not $keys.Contains($body)) { $keys[$body] = $file.Name }
        }
    }
}
$sb = New-Object Text.StringBuilder
foreach ($k in $keys.Keys) { [void]$sb.AppendLine($k + "`t" + $keys[$k]) }
[IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
"$($keys.Count) strings"
