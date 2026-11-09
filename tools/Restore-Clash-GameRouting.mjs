import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { clashRoot, coreRequest } from './clash-routing-common.mjs';

const root = path.resolve(import.meta.dirname, '..');
const report = JSON.parse(fs.readFileSync(path.join(root, 'diagnostics/clash-routing-result.json'), 'utf8'));
const script = path.join(clashRoot, 'profiles', 'Script.js');
const runtime = path.join(clashRoot, 'clash-verge.yaml');
const currentScript = fs.readFileSync(script);
const currentRuntime = fs.readFileSync(runtime);
const hash = value => createHash('sha256').update(value).digest('hex');
assert.equal(hash(currentScript), report.scriptHash, 'Extension changed later; inspect before restoring.');
assert.equal(hash(currentRuntime), report.runtimeHash, 'Runtime changed later; regenerate from the restored extension instead of overwriting it.');
fs.writeFileSync(script, fs.readFileSync(path.join(report.backupDir, 'Script.js')));
fs.writeFileSync(runtime, fs.readFileSync(path.join(report.backupDir, 'clash-verge.yaml')));
try {
  await coreRequest('/configs?force=true', 'PUT', { path: runtime });
  console.log('Original Clash global extension and routing restored.');
} catch (error) {
  fs.writeFileSync(script, currentScript);
  fs.writeFileSync(runtime, currentRuntime);
  await coreRequest('/configs?force=true', 'PUT', { path: runtime });
  throw error;
}
