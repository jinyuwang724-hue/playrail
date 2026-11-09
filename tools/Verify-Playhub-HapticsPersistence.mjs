import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

const root = path.resolve(import.meta.dirname, '..');
const receipt = JSON.parse(fs.readFileSync(path.join(root, 'diagnostics/playhub-haptics-persistence.json'), 'utf8').replace(/^\uFEFF/, ''));
const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const files = receipt.files.map(file => ({ path: file.path, matchesRepair: hash(file.path) === file.hash }));
assert(files.every(file => file.matchesRepair), 'Bundled and installed feedback files must match the repair.');

// Playhub 2.1.0 GamingModeService.NeedsDeckyPluginUpdate uses presence,
// length, and whether the installed write time predates the bundled file.
const differences = [];
function inspect(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const source = path.join(directory, entry.name);
    if (entry.isDirectory()) { inspect(source); continue; }
    if (!entry.isFile()) continue;
    const relative = path.relative(receipt.bundleRoot, source);
    const installed = path.join(receipt.installedRoot, relative);
    if (!fs.existsSync(installed)) { differences.push({ relative, reason: 'missing' }); continue; }
    const sourceStat = fs.statSync(source);
    const installedStat = fs.statSync(installed);
    if (sourceStat.size !== installedStat.size || installedStat.mtimeMs < sourceStat.mtimeMs) {
      differences.push({ relative, reason: sourceStat.size !== installedStat.size ? 'length' : 'timestamp' });
    }
  }
}
inspect(receipt.bundleRoot);
const report = { checkedAt: new Date().toISOString(), files,
  startupSyncWouldCopy: differences.length > 0, differences,
  startupSyncSourceContainsRepair: true };
fs.writeFileSync(path.join(root, 'diagnostics/playhub-haptics-persistence-validation.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify(report));
