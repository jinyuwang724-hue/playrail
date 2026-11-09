"""Stage the driver limiter over the source of the installed helper and live Python.

Does not edit that source checkout or install anything. Hashes make deployment refuse
an intervening change to the live baseline.
"""
import ast
import hashlib
import json
import os
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/driver-frame-limit'
BASE = Path.home() / '.codex/worktrees/native-gamebar-osd/Claw-Playhub-ButtonRouter-Handoff'
HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox'


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def replace_once(text, old, new):
    if text.count(old) != 1: raise RuntimeError('Missing or ambiguous anchor: ' + old[:100])
    return text.replace(old, new, 1)


def remove_method(text, declaration):
    start = text.index(declaration)
    opening = text.index('{', start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[:start] + text[end:]


def main():
    manifest = json.loads((HOME / 'app/plugin.json').read_text(encoding='utf-8-sig'))
    version, revision = manifest['version'], manifest['powerHelperRevision']
    old_binary = f'ClawPower-{version}-r{revision}.exe'
    if digest(HOME / 'app/bin' / old_binary) != digest(BASE / 'plugin/bin' / old_binary):
        raise RuntimeError('Deployed helper does not match the documented source baseline')
    native = OUT / 'native'
    native.mkdir(parents=True, exist_ok=False)
    files = ['ClawPower.cs', 'ClawSmallCores.cs', 'ClawMappedInput.cs', 'ClawLossless.cs',
             'ClawFanHardware.cs', 'ClawDisplay.cs', 'ClawGpu.cs', 'ClawSystem.cs', 'ClawUpdate.cs']
    for name in files: shutil.copy2(BASE / 'native' / name, native / name)
    shutil.copy2(ROOT / 'native/ClawFrameLimit.cs', native / 'ClawFrameLimit.cs')
    power = (native / 'ClawPower.cs').read_text(encoding='utf-8-sig')
    power = replace_once(power,
        'else if (command.StartsWith("rtss_game_", StringComparison.Ordinal)) reply["rtssProfile"] = Osd.GameFps(command, request);',
        'else if (command.StartsWith("driver_game_", StringComparison.Ordinal)) reply["fpsProfile"] = IntelFrameLimit.Session(command, request);')
    power = replace_once(power,
        'var hooks = Osd.HooksForWidget();\n            result["fpsLimit"] = hooks;\n        } catch (Exception) { result["fpsLimit"] = null; }',
        'foreach (var value in IntelFrameLimit.Read()) result[value.Key] = value.Value;\n        } catch (Exception error) { result["fpsLimit"] = null; result["fpsLimitError"] = error.Message; }')
    power = replace_once(power,
        'if (fps < 0 || fps > 360) throw new ArgumentException("帧率限制超出范围");\n        Osd.SetIntIn("FramerateLimit", fps, string.IsNullOrEmpty(exe) ? new[] { "" } : new[] { "", exe });\n        return new Dictionary<string, object> { { "fpsLimit", fps } };',
        'return IntelFrameLimit.Set(fps);')
    power = remove_method(power, '    internal static Dictionary<string, object> GameFps(')
    power = remove_method(power, '    internal static int HooksForWidget(')
    power = power.replace('RTSS frame limit', 'driver frame limit')
    (native / 'ClawPower.cs').write_text(power, encoding='utf-8')

    backend = OUT / 'backend'
    backend.mkdir(exist_ok=False)
    expected = {name: digest(HOME / 'app' / name) for name in ('claw_power.py', 'plugin.json')}
    py = (HOME / 'app/claw_power.py').read_text(encoding='utf-8-sig')
    py = py.replace('"game-fps-session.json"', '"driver-game-fps-session.json"')
    py = py.replace('rtss_game_', 'driver_game_').replace('reply.get("rtssProfile")', 'reply.get("fpsProfile")')
    py = py.replace('RTSS 帧率上限已在其他软件修改', '显卡驱动帧率上限已在其他软件修改')
    py = py.replace("Only touch the game's RTSS profile.", 'Temporarily use the driver global limit for the foreground game.')
    py = replace_once(py,
        'session = {"exe": exe, "previous": previous["fps"], "globalProfile": not previous["exists"], "written": fps, "applied": False}',
        'session = {"exe": exe, "previousEnabled": previous["enabled"], "previousValue": previous["value"], "written": fps, "applied": False}')
    py = py.replace('"refreshRates", "fpsLimit")',
                    '"refreshRates", "fpsLimit", "fpsLimitProvider", "fpsLimitMin", "fpsLimitMax", "fpsLimitStep", "fpsLimitLiveChange", "fpsLimitApiFlags", "fpsLimitEnabled", "fpsLimitValue", "fpsLimitError")')
    compile(py, 'claw_power.py', 'exec')
    (backend / 'claw_power.py').write_text(py, encoding='utf-8')
    manifest['powerHelperRevision'] = revision + 1
    (backend / 'plugin.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    report = {'expected': expected, 'oldHelper': old_binary,
              'newHelper': f'ClawPower-{version}-r{revision + 1}.exe',
              'sourceBaseline': str(BASE), 'baselineHelperSha256': digest(HOME / 'app/bin' / old_binary),
              'nativeSources': {name: digest(native / name) for name in [*files, 'ClawFrameLimit.cs']}}
    (OUT / 'preparation.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('Prepared Intel driver limit over the verified deployed helper and current backend')


if __name__ == '__main__': main()
