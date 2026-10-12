import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

const root = path.resolve(import.meta.dirname, '..');
const target = path.join(process.env.APPDATA, 'GamingMode', 'config.json');
const backupRoot = path.join(root, 'diagnostics', 'gaming-startup-backup');
const definitions = [
  {
    name: 'Clash Verge',
    path: 'C:\\Software\\Clash Verge\\clash-verge.exe',
    arguments: '',
    workingDirectory: 'C:\\Software\\Clash Verge',
    processName: 'clash-verge',
    enabled: true,
    startMinimized: true,
    delayAfterStartMs: 0,
  },
  {
    name: 'Lossless Scaling',
    path: 'C:\\Software\\steamapps\\common\\Lossless Scaling\\LosslessScaling.exe',
    arguments: '-StartMinimized',
    workingDirectory: 'C:\\Software\\steamapps\\common\\Lossless Scaling',
    processName: 'LosslessScaling',
    enabled: true,
    startMinimized: true,
    delayAfterStartMs: 0,
  },
];
const digest = text => createHash('sha256').update(text).digest('hex');
const normalize = value => String(value ?? '').toLowerCase();
const originalBytes = fs.readFileSync(target);
const original = JSON.parse(originalBytes.toString('utf8').replace(/^\uFEFF/, ''));
assert(Array.isArray(original.gaming?.customStartupApps), 'Gaming startup list must exist.');
const updated = structuredClone(original);
for (const definition of definitions) {
  assert(fs.statSync(definition.path).isFile(), `${definition.name} executable is missing.`);
  const matches = updated.gaming.customStartupApps.filter(app =>
    normalize(app.path) === normalize(definition.path) ||
    normalize(app.processName) === normalize(definition.processName));
  assert(matches.length <= 1, `${definition.name} already has duplicate entries; inspect first.`);
  if (matches.length === 1) Object.assign(matches[0], definition);
  else updated.gaming.customStartupApps.push(definition);
}
const changes = definitions.map(definition => ({
  name: definition.name, processName: definition.processName,
  path: definition.path, arguments: definition.arguments,
}));
if (!process.argv.includes('--apply')) {
  console.log(JSON.stringify({ target, changes, existingEntries: original.gaming.customStartupApps.length }, null, 2));
  process.exit(0);
}

if (JSON.stringify(original) === JSON.stringify(updated)) {
  console.log(JSON.stringify({ alreadyConfigured: true, changes }));
  process.exit(0);
}
fs.mkdirSync(backupRoot, { recursive: true });
const stamp = new Date().toISOString().replaceAll(':', '-');
const backup = path.join(backupRoot, `config-before-${stamp}.json`);
fs.writeFileSync(backup, originalBytes, { flag: 'wx' });
assert.equal(digest(fs.readFileSync(target)), digest(originalBytes), 'Configuration changed during preparation; retry.');
const output = JSON.stringify(updated, null, 2) + '\n';
const temporary = `${target}.startup-${process.pid}.tmp`;
try {
  fs.writeFileSync(temporary, output, { flag: 'wx' });
  assert.equal(digest(fs.readFileSync(target)), digest(originalBytes), 'Configuration changed before writing; retry.');
  fs.renameSync(temporary, target);
} finally {
  if (fs.existsSync(temporary)) fs.unlinkSync(temporary);
}
const installed = JSON.parse(fs.readFileSync(target, 'utf8'));
assert.deepEqual(installed, updated, 'Saved configuration differs from the planned change.');
const installedRest = structuredClone(installed);
installedRest.gaming.customStartupApps = original.gaming.customStartupApps;
assert.deepEqual(installedRest, original, 'Unrelated configuration changed.');
const report = {
  appliedAt: new Date().toISOString(), target, backup,
  originalHash: digest(originalBytes), installedHash: digest(fs.readFileSync(target)),
  changes, previousStartupCount: original.gaming.customStartupApps.length,
  installedStartupCount: installed.gaming.customStartupApps.length,
  unrelatedConfigurationPreserved: true,
};
fs.writeFileSync(path.join(backupRoot, 'latest-change.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify(report, null, 2));
