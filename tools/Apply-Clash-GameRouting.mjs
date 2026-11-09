import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { clashRoot, readYaml, YAML, coreRequest, firstDomainRule } from './clash-routing-common.mjs';

const root = path.resolve(import.meta.dirname, '..');
const runtimePath = path.join(clashRoot, 'clash-verge.yaml');
const scriptPath = path.join(clashRoot, 'profiles', 'Script.js');
const candidateScript = fs.readFileSync(path.join(root, 'repairs/clash-routing/Script.js'), 'utf8');
const originalRuntime = fs.readFileSync(runtimePath);
const originalScript = fs.readFileSync(scriptPath);
const original = YAML.parse(originalRuntime.toString('utf8'));
const profiles = readYaml(path.join(clashRoot, 'profiles.yaml'));
const current = profiles.items.find(item => item.uid === profiles.current);
assert(current && profiles.items.some(item => item.uid === 'Script' && item.file === 'Script.js'));
const originalSubscriptionHash = hash(fs.readFileSync(path.join(clashRoot, 'profiles', current.file)));
const liveBefore = await coreRequest('/configs');
assert.equal(liveBefore.mode, 'rule', 'Inspect the current mode before changing routing.');
assert.equal(original.mode, 'rule');
assert.equal(original.dns?.['fake-ip-filter-mode'] || 'blacklist', 'blacklist');
assert(/function main\(config, profileName\)\s*\{\s*return config;\s*\}/.test(originalScript.toString('utf8')) ||
  originalScript.toString('utf8') === candidateScript, 'Global script already customized; inspect before replacing.');
for (const processRule of candidateScript.matchAll(/'PROCESS-PATH,([^']+),DIRECT'/g)) {
  const executable = processRule[1].replaceAll('\\\\', '\\');
  assert(fs.statSync(executable).isFile(), 'Expected domestic game executable is missing.');
}
const sandbox = {};
vm.createContext(sandbox);
vm.runInContext(candidateScript, sandbox);
const updated = JSON.parse(JSON.stringify(sandbox.main(structuredClone(original), current.name)));
assert.deepEqual(updated.proxies, original.proxies, 'Proxy nodes changed.');
assert.deepEqual(updated['proxy-groups'], original['proxy-groups'], 'Proxy groups changed.');
const unrelated = structuredClone(updated);
unrelated.rules = original.rules;
unrelated.dns = original.dns;
if (original['find-process-mode'] === undefined) delete unrelated['find-process-mode'];
else unrelated['find-process-mode'] = original['find-process-mode'];
assert.deepEqual(unrelated, original, 'Unrelated settings changed.');
for (const rule of original.rules) assert(updated.rules.includes(rule), 'An original routing rule was removed.');
for (const [key, value] of Object.entries(original.dns)) {
  if (!['nameserver-policy', 'fake-ip-filter'].includes(key)) assert.deepEqual(updated.dns[key], value, 'Existing DNS setting changed.');
}
for (const value of original.dns['fake-ip-filter'] || []) assert(updated.dns['fake-ip-filter'].includes(value));
assert.deepEqual(JSON.parse(JSON.stringify(sandbox.main(structuredClone(updated), current.name))), updated, 'Extension is not idempotent.');

const directHosts = ['steampipe.akamaized.net', 'cache1-hkg1.steamcontent.com', 'st.dl.eccdnx.com',
  'dl.steam.clngaa.com', 'gstore.val.manlaxy.com', 'xz.pphimalayanrt.com', 'www.mihoyo.com', 'www.kurogame.com'];
const routingChecks = directHosts.map(host => ({ host, ...firstDomainRule(updated.rules, host) }));
routingChecks.forEach(check => assert.equal(check.policy, 'DIRECT', check.host));
for (const host of ['store.steampowered.com', 'steamcommunity.com', 'chatgpt.com', 'github.com']) {
  assert.equal(firstDomainRule(updated.rules, host)?.policy, firstDomainRule(original.rules, host)?.policy, host);
}
const backupDir = path.join(root, 'diagnostics', 'clash-routing-backup', new Date().toISOString().replaceAll(':', '-'));
fs.mkdirSync(backupDir, { recursive: true });
fs.writeFileSync(path.join(backupDir, 'Script.js'), originalScript);
fs.writeFileSync(path.join(backupDir, 'clash-verge.yaml'), originalRuntime);
const candidatePath = path.join(backupDir, 'validated-config.yaml');
const candidate = YAML.stringify(updated, { lineWidth: 0 });
fs.writeFileSync(candidatePath, candidate);
const check = spawnSync('C:\\Software\\Clash Verge\\verge-mihomo.exe', ['-t', '-d', clashRoot, '-f', candidatePath], { encoding: 'utf8', windowsHide: true });
assert.equal(check.status, 0, 'Mihomo configuration validation failed; nothing installed.');

if (!process.argv.includes('--apply')) {
  console.log(JSON.stringify({ prepared: true, configurationValidated: true, addedPriorityRules: updated.rules.length - original.rules.length, routingChecks, backupDir }, null, 2));
  process.exit(0);
}
assert.equal(hash(fs.readFileSync(runtimePath)), hash(originalRuntime), 'Runtime configuration changed during preparation.');
assert.equal(hash(fs.readFileSync(scriptPath)), hash(originalScript), 'Global extension changed during preparation.');
fs.writeFileSync(scriptPath, candidateScript);
fs.writeFileSync(runtimePath, candidate);
let applied = false;
try {
  await coreRequest('/configs?force=true', 'PUT', { path: runtimePath });
  const [live, liveRules] = await Promise.all([coreRequest('/configs'), coreRequest('/rules')]);
  assert.equal(live.mode, 'rule');
  assert.equal(live.tun?.enable, liveBefore.tun?.enable);
  assert.equal(liveRules.rules.length, updated.rules.length);
  const expectedHead = updated.rules.slice(0, 38);
  expectedHead.forEach((rule, index) => {
    const [, payload, policy] = rule.split(',');
    assert.equal(liveRules.rules[index].payload, payload, 'Loaded rule order differs.');
    assert.equal(liveRules.rules[index].proxy, policy, 'Loaded routing policy differs.');
  });
  assert.equal(hash(fs.readFileSync(path.join(clashRoot, 'profiles', current.file))), originalSubscriptionHash, 'Subscription source changed.');
  const report = {
    appliedAt: new Date().toISOString(), backupDir, currentProfileUid: current.uid,
    configurationValidated: true, loadedRuleCount: liveRules.rules.length,
    mode: live.mode, tunEnabled: live.tun?.enable, persistentGlobalExtension: scriptPath,
    subscriptionSourcePreserved: true, proxyNodesAndGroupsPreserved: true,
    scriptHash: hash(candidateScript), runtimeHash: hash(candidate), routingChecks,
  };
  fs.writeFileSync(path.join(root, 'diagnostics', 'clash-routing-result.json'), JSON.stringify(report, null, 2) + '\n');
  applied = true;
  console.log(JSON.stringify(report, null, 2));
} finally {
  if (!applied) {
    fs.writeFileSync(scriptPath, originalScript);
    fs.writeFileSync(runtimePath, originalRuntime);
    await coreRequest('/configs?force=true', 'PUT', { path: runtimePath });
  }
}

function hash(value) { return createHash('sha256').update(value).digest('hex'); }
