import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';

const source = path.resolve('artifacts/playhub-haptics-backup/index.js');
const output = path.resolve('repairs/playhub-haptics/index.js');
const original = fs.readFileSync(source, 'utf8');
const anchor = 'async function nativePattern(index, action) {';
const bodyAnchor = '                pattern: action,\n                requestId: nextNativeRequestId(),';
if (original.split(anchor).length !== 2 || original.split(bodyAnchor).length !== 2) {
  throw new Error('Installed Playhub haptics implementation changed; inspect before patching.');
}

// Preserve the exact native first-step duration, motor side and gain.
// Playhub 2.1.0's ControllerHapticsService.PlayPattern sends pattern[0].
const helper = `function msiClawNavigationPulse(controller, action, intensity) {
    if (Number(controller?.unVendorID) !== 0x0db0 || Number(controller?.unProductID) !== 0x1901)
        return { pattern: action };
    const patterns = {
        moveUp: { durationMs: 16, scale: 0.86 },
        moveDown: { durationMs: 16, scale: 0.86 },
        moveLeft: { durationMs: 16, scale: 0.86 },
        moveRight: { durationMs: 16, scale: 0.86 },
        tabPrevious: { durationMs: 18, scale: 1 },
        tabNext: { durationMs: 18, scale: 1 },
        sliderDecrease: { durationMs: 12, scale: 0.68 },
        sliderIncrease: { durationMs: 12, scale: 0.68 },
        toggleOn: { durationMs: 13, side: 0, scale: 0.42 },
        toggleOff: { durationMs: 13, side: 1, scale: 0.42 },
        confirm: { durationMs: 21, side: 2, scale: 1 },
        back: { durationMs: 18, side: 0, scale: 0.88 },
        options: { durationMs: 11, side: 0, scale: 0.48 },
        menu: { durationMs: 11, side: 0, scale: 0.48 },
        dropdown: { durationMs: 12, side: 2, scale: 0.40 },
        letter: { durationMs: 19, side: 2, scale: 0.90 },
    };
    const step = patterns[action] ?? { durationMs: 16, scale: 0.82 };
    const strength = Math.min(1, Math.max(0.05, Math.pow(intensity / 100, 1.35) * 0.95));
    return {
        durationMs: step.durationMs,
        side: step.side ?? actionSide(action),
        magnitude: Math.min(1, Math.max(0, strength * step.scale)),
    };
}
`;
const repaired = original.replace(anchor, helper + anchor)
  .replace(bodyAnchor, '                ...msiClawNavigationPulse(controller, action, config.intensity),\n                requestId: nextNativeRequestId(),')
  .replace('    const magnitude = Math.min(1, Math.max(0.05, Math.pow(config.intensity / 100, 1.35) * 0.95));', `    if (vendorId === 0x0db0 && productId === 0x1901) {
        try {
            const result = await call("play_claw_haptic", {
                vendorId, productId, side: actionSide(action),
                ...msiClawNavigationPulse(controller, action, config.intensity),
                requestId: nextNativeRequestId(),
            });
            return result?.handled === true;
        } catch { return false; }
    }
    const magnitude = Math.min(1, Math.max(0.05, Math.pow(config.intensity / 100, 1.35) * 0.95));`)
  .replace('function stopNativeHaptics() {', `function stopNativeHaptics() {
    void call("play_claw_haptic", { stop: true, requestId: nextNativeRequestId() }).catch(() => undefined);`);
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, repaired);
const mainSource = fs.readFileSync(path.resolve('artifacts/playhub-haptics-backup/main.py'), 'utf8');
const importAnchor = 'class Plugin(QuickSettingsPlugin):';
const initAnchor = '        self._decky_ipc_health = None';
const unloadAnchor = '        logging.getLogger(__name__).info("Playhub backend unloading pid=%s", os.getpid())';
for (const anchor of [importAnchor, initAnchor, unloadAnchor]) {
  if (mainSource.split(anchor).length !== 2) throw new Error('Playhub backend structure changed.');
}
const mainRepair = mainSource.replace(importAnchor, `_claw_haptic_spec = importlib.util.spec_from_file_location("playhub_claw_haptics", os.path.join(os.path.dirname(__file__), "claw_navigation_haptics.py"))
_claw_haptic_module = importlib.util.module_from_spec(_claw_haptic_spec)
_claw_haptic_spec.loader.exec_module(_claw_haptic_module)


class Plugin(QuickSettingsPlugin):
    async def play_claw_haptic(self, request):
        return self._claw_navigation_haptics.request(request)
`)
  .replace(initAnchor, '        self._claw_navigation_haptics = _claw_haptic_module.ClawNavigationHaptics()\n' + initAnchor)
  .replace(unloadAnchor, unloadAnchor + '\n        self._claw_navigation_haptics.close()');
fs.writeFileSync(path.join(path.dirname(output), 'main.py'), mainRepair);
fs.writeFileSync(path.join(path.dirname(output), 'patch-info.json'), JSON.stringify({
  plugin: 'Playhub', version: '2.1.0', hardware: '0DB0:1901',
  sourceHash: createHash('sha256').update(original).digest('hex'),
  outputHash: createHash('sha256').update(repaired).digest('hex'),
  sourceMainHash: createHash('sha256').update(mainSource).digest('hex'),
  outputMainHash: createHash('sha256').update(mainRepair).digest('hex'),
  moduleHash: createHash('sha256').update(fs.readFileSync(path.join(path.dirname(output), 'claw_navigation_haptics.py'))).digest('hex'),
  changes: 'Use verified XInput output with official Playhub 2.1.0 native motor selection, intensity scaling and action durations.',
}, null, 2) + '\n');
console.log('Prepared MSI Claw navigation feedback repair.');
