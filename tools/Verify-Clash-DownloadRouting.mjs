import fs from 'node:fs';
import http from 'node:http';
import assert from 'node:assert/strict';
import { coreRequest } from './clash-routing-common.mjs';

const live = await coreRequest('/configs');
assert.equal(live.mode, 'rule');
const port = live['mixed-port'];
assert.equal(port, 7897, 'Unexpected local proxy port.');
const hosts = ['st.dl.eccdnx.com', 'dl.steam.clngaa.com', 'gstore.val.manlaxy.com'];
const results = await Promise.all(hosts.map(async host => {
  let socket;
  try {
    socket = await new Promise((resolve, reject) => {
      const request = http.request({ hostname: '127.0.0.1', port, method: 'CONNECT', path: `${host}:80`, headers: { Host: `${host}:80` } });
      request.setTimeout(10000, () => request.destroy(new Error('Timed out.')));
      request.on('error', reject);
      request.on('connect', (response, connection) => {
        if (response.statusCode !== 200) {
          connection.destroy();
          reject(new Error(`HTTP ${response.statusCode}`));
        } else {
          connection.on('error', () => {});
          resolve(connection);
        }
      });
      request.end();
    });
    const localPort = socket.localPort;
    let observed;
    for (let attempt = 0; attempt < 8; attempt++) {
      const current = await coreRequest('/connections');
      observed = current.connections.find(connection => connection.metadata.host === host &&
        String(connection.metadata.sourcePort) === String(localPort));
      if (observed) break;
      await new Promise(resolve => setTimeout(resolve, 150));
    }
    assert(observed, 'Test connection was not visible to the core.');
    assert(observed.chains.includes('DIRECT'), 'Test connection did not use DIRECT.');
    return { host, connected: true, chains: observed.chains, rule: observed.rule, rulePayload: observed.rulePayload };
  } catch (error) {
    return { host, connected: false, error: error.message };
  } finally {
    socket?.destroy();
  }
}));
fs.writeFileSync('diagnostics/clash-routing-live-verification.json', JSON.stringify({ checkedAt: new Date().toISOString(), results }, null, 2) + '\n');
console.log(JSON.stringify(results, null, 2));
if (results.some(result => !result.connected)) process.exitCode = 1;
