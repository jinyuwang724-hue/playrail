import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';

const root = path.resolve(import.meta.dirname, '..');
const installed = path.join(process.env.USERPROFILE, 'homebrew/plugins/claw-button-router');
const settings = path.join(process.env.USERPROFILE, 'homebrew/settings/claw-button-router');
const oldName = 'Claw 按键切换', newName = 'Claw 掌机工具箱';
const manifest = JSON.parse(fs.readFileSync(path.join(installed, 'plugin.json'), 'utf8'));
assert.equal(manifest.name, oldName, 'This migration only upgrades the original Claw plugin.');
assert.equal(manifest.version, '0.2.4');
assert.equal(JSON.parse(fs.readFileSync(path.join(root, 'plugin/plugin.json'), 'utf8')).name, newName);
const probe = expression => {
  const result = execFileSync(process.execPath, [path.join(root, 'tools/steam-probe.mjs'), expression],
    { encoding: 'utf8', windowsHide: true, timeout: 20000 });
  return JSON.parse(result.trim()).result.value;
};
const resume = process.argv[2] === '--resume';
const backup = resume ? path.resolve(root, process.argv[3]) : path.join(root, 'artifacts', 'toolbox-upgrade-' + new Date().toISOString().replace(/[:.]/g, '-'));
assert(backup.startsWith(path.join(root, 'artifacts') + path.sep));
const before = resume ? JSON.parse(fs.readFileSync(path.join(backup, 'state.json'), 'utf8')) : probe(`Promise.all([window.DeckyBackend.call("loader/call_plugin_method",${JSON.stringify(oldName)},"get_state"),
  ...["pluginOrder","hiddenPlugins","frozenPlugins","disabled_plugins"].map(k=>window.DeckyBackend.call("utilities/settings/get",k,[]))])`);
assert.equal(before[0].status.restorePending, false, 'Complete any pending helper restoration before migration.');
if (!resume) {
fs.mkdirSync(backup, { recursive: true });
fs.cpSync(installed, path.join(backup, 'plugin'), { recursive: true });
fs.cpSync(settings, path.join(backup, 'settings'), { recursive: true });
fs.writeFileSync(path.join(backup, 'state.json'), JSON.stringify(before, null, 2));
// Decky's reload matches by display name; a rename needs graceful removal
// of the old runtime so that two native input owners cannot coexist.
probe(`window.DeckyBackend.call("utilities/uninstall_plugin",${JSON.stringify(oldName)}).then(()=>({removed:true}))`);
}
// Windows may retain installation files after unloading; runtime ownership
// is what must be absent before starting the renamed plugin.
const unloaded = probe('window.DeckyBackend.call("loader/get_plugins")');
assert(!unloaded.some(plugin => plugin.name === oldName || plugin.name === newName),
  'Old runtime removal did not complete; do not load a second owner.');
const files = ['bin/ClawRouter-0.3.0.exe', 'main.py', 'claw_navigation_haptics.py', 'dist/index.js', 'package.json', 'plugin.json'];
for (const file of files) {
  const target = path.join(installed, file);
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.copyFileSync(path.join(root, 'plugin', file), target);
}
fs.mkdirSync(settings, { recursive: true });
const originalSettings = JSON.parse(fs.readFileSync(path.join(backup, 'settings/settings.json'), 'utf8'));
fs.writeFileSync(path.join(settings, 'settings.json'), JSON.stringify({ ...originalSettings, hapticsEnabled: true }));
probe(`window.DeckyBackend.call("utilities/enable_plugin",${JSON.stringify(newName)}).then(()=>({loaded:true}))`);
for (const [i, key] of ['pluginOrder', 'hiddenPlugins', 'frozenPlugins', 'disabled_plugins'].entries()) {
  if (!before[i + 1].includes(oldName)) continue;
  const migrated = [...new Set(before[i + 1].map(name => name === oldName ? newName : name))];
  probe(`window.DeckyBackend.call("utilities/settings/set",${JSON.stringify(key)},${JSON.stringify(migrated)})`);
}
const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
assert(files.every(file => hash(path.join(root, 'plugin', file)) === hash(path.join(installed, file))));
const report = { upgradedAt: new Date().toISOString(), backup, name: newName, version: '0.3.0',
  settingsPreserved: originalSettings, hapticsEnabled: true, installedFilesVerified: files };
fs.writeFileSync(path.join(root, 'diagnostics/claw-toolbox-upgrade.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify(report));
