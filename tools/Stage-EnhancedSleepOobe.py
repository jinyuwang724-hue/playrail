"""Bundle the self-contained sleep host and add it to the existing OOBE agent step."""
import hashlib
import json
from pathlib import Path
import shutil
import sys

ROOT = Path(__file__).resolve().parents[1]
out = Path(sys.argv[1]).resolve()
ui = out / "src/frontend/library-native/ClawLibrary"
sleep = ui / "Payload/sleep"
sleep.mkdir(parents=True, exist_ok=True)
shutil.copytree(out / "app", sleep / "app", dirs_exist_ok=True)
shutil.copy2(ROOT / "tools/Install-SleepAnimation.ps1", sleep / "Install-SleepAnimation.ps1")
hashes = {str(p.relative_to(sleep / "app")).replace("\\", "/"): hashlib.sha256(p.read_bytes()).hexdigest()
          for p in sorted((sleep / "app").rglob("*")) if p.is_file() and p.name != "enhanced-bundle.json"}
bundle = hashlib.sha256(json.dumps(hashes, sort_keys=True).encode()).hexdigest()
(sleep / "app/enhanced-bundle.json").write_text(json.dumps({"bundle": bundle, "files": hashes}, indent=2), encoding="utf-8")


def once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError("Ambiguous OOBE merge: " + old[:80])
    return text.replace(old, new, 1)


script = ui / "Payload/Install-Components.ps1"
text = script.read_text(encoding="utf-8-sig")
if "function Install-SleepStep" not in text:
    function = r'''
function Install-SleepStep {
    $source = Join-Path $payload 'sleep\app'
    $marker = Read-Json (Join-Path $source 'enhanced-bundle.json')
    $state = Join-Path $env:ProgramData ('ClawToolbox\SleepAnimation\' + $UserSid)
    $record = Join-Path $state 'installation.json'
    if (Test-Path -LiteralPath $record) {
        $installed = Read-Json $record
        $oldMarker = Join-Path (Split-Path -Parent $installed.executable) 'enhanced-bundle.json'
        if ((Test-Path -LiteralPath $oldMarker) -and (Read-Json $oldMarker).bundle -eq $marker.bundle) { return 'kept' }
    }
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $UserSid) {
        throw 'Sleep installation must run as the same interactive account.'
    }
    & (Join-Path $payload 'sleep\Install-SleepAnimation.ps1') -Source $source -Report (Join-Path $statusFolder 'sleep-installation.json')
    return 'installed'
}
'''
    text = once(text, "$code = 0", function + "\n$code = 0")
    text = once(text, "            'agent' { Install-AgentStep }", "            'agent' { Install-AgentStep }\n            'sleep' { Install-SleepStep }")
    text = text.replace("fonts,agent,pawnio,fan,widget", "fonts,agent,sleep,pawnio,fan,widget")
    script.write_text(text, encoding="utf-8-sig")

page = ui / "Stage/WallStage.Oobe.cs"
text = page.read_text(encoding="utf-8-sig")
if 'ObInstallStepAsync("sleep")' not in text:
    text = once(text, '        bool installed = await ObInstallStepAsync("agent");',
                '        bool installed = await ObInstallStepAsync("agent");\n        if (installed && !await ObInstallStepAsync("sleep")) return false;')
    text = text.replace("fonts,agent,pawnio,fan,widget", "fonts,agent,sleep,pawnio,fan,widget").replace("fonts,agent,widget", "fonts,agent,sleep,widget")
    page.write_text(text, encoding="utf-8")
print("Staged sleep host in OOBE; background setup waits for it")
