import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
export const YAML = require('./.clash-runtime/node_modules/yaml');
export const clashRoot = path.join(process.env.APPDATA, 'io.github.clash-verge-rev.clash-verge-rev');
export const readYaml = file => YAML.parse(fs.readFileSync(file, 'utf8'));

export function coreRequest(route, method = 'GET', body) {
  const config = readYaml(path.join(clashRoot, 'clash-verge.yaml'));
  const pipe = config['external-controller-pipe'];
  if (pipe !== '\\\\.\\pipe\\verge-mihomo') throw new Error('Unexpected local controller pipe.');
  const payload = body === undefined ? undefined : JSON.stringify(body);
  return new Promise((resolve, reject) => {
    const headers = { Host: 'localhost' };
    if (config.secret) headers.Authorization = `Bearer ${config.secret}`;
    if (payload !== undefined) {
      headers['Content-Type'] = 'application/json';
      headers['Content-Length'] = Buffer.byteLength(payload);
    }
    const request = http.request({ socketPath: pipe, path: route, method, headers }, response => {
      let data = '';
      response.setEncoding('utf8');
      response.on('data', chunk => { data += chunk; });
      response.on('end', () => {
        if (response.statusCode < 200 || response.statusCode >= 300) {
          // Do not print raw controller errors: they can contain configuration data.
          reject(new Error(`Local controller ${method} ${route.split('?')[0]} returned HTTP ${response.statusCode}.`));
          return;
        }
        try { resolve(data ? JSON.parse(data) : { status: response.statusCode }); }
        catch { reject(new Error('Local controller returned invalid JSON.')); }
      });
    });
    request.setTimeout(15000, () => request.destroy(new Error('Local controller timed out.')));
    request.on('error', reject);
    request.end(payload);
  });
}

export function firstDomainRule(rules, host) {
  const name = host.toLowerCase();
  for (let index = 0; index < rules.length; index++) {
    const rule = rules[index];
    const [type, payload, policy] = rule.split(',');
    const value = payload?.toLowerCase();
    if ((type === 'DOMAIN' && name === value) ||
        (type === 'DOMAIN-SUFFIX' && (name === value || name.endsWith(`.${value}`))) ||
        (type === 'DOMAIN-KEYWORD' && name.includes(value))) {
      return { index, rule, policy };
    }
    if (type === 'MATCH') return { index, rule, policy: payload };
  }
  return null;
}
