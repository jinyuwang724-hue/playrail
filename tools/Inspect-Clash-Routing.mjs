import fs from 'node:fs';
import path from 'node:path';
import { clashRoot, readYaml, coreRequest, firstDomainRule } from './clash-routing-common.mjs';

const config = readYaml(path.join(clashRoot, 'clash-verge.yaml'));
const profiles = readYaml(path.join(clashRoot, 'profiles.yaml'));
const current = profiles.items.find(item => item.uid === profiles.current);
const [live, liveRules, proxies] = await Promise.all([
  coreRequest('/configs'), coreRequest('/rules'), coreRequest('/proxies'),
]);
const hosts = ['steampipe.akamaized.net', 'cache1-hkg1.steamcontent.com', 'st.dl.eccdnx.com',
  'dl.steam.clngaa.com', 'gstore.val.manlaxy.com', 'store.steampowered.com', 'steamcommunity.com',
  'sdk.mihoyo.com', 'www.kurogame.com'];
const selected = Object.entries(proxies.proxies).filter(([name, value]) =>
  name.includes('Steam') && value.type === 'Selector').map(([name, value]) => ({ name, now: value.now }));
const contentLog = fs.readFileSync('C:/Software/logs/content_log.txt', 'utf8');
const steamContentHosts = [...new Set([...contentLog.matchAll(/Created download interface[^\r\n]*? to host ([a-z0-9.-]+)/gi)].map(match => match[1]))];
console.log(JSON.stringify({
  live: { mode: live.mode, tunEnabled: live.tun?.enable, processMode: live['find-process-mode'], ruleCount: liveRules.rules.length },
  persistentExtensions: { currentUid: current.uid, rules: current.option?.rules, script: current.option?.script },
  steamGroups: selected,
  firstDomainMatches: hosts.map(host => ({ host, ...firstDomainRule(config.rules, host) })),
  steamContentHosts,
}, null, 2));
