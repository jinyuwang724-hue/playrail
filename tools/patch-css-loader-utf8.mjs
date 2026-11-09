import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const root = fs.realpathSync(process.argv[2]);
const backup = process.argv[3];
const patches = [
  ['css_loader.py', 'open(themeDataPath, "r")'],
  ['css_inject.py', 'open(self.cssPath, "r")'],
  ['css_theme.py', 'open(self.configJsonPath, "r")'],
].map(([name, before]) => {
  const file = path.join(root, name);
  const original = fs.readFileSync(file, 'utf8');
  if (original.split(before).length !== 2) throw new Error(`Expected one encoding patch in ${name}`);
  const updated = original.replace(before, before.slice(0, -1) + ', encoding="utf-8-sig")');
  return {name, file, original, updated};
});
if (backup) {
  fs.mkdirSync(backup, {recursive: true});
  for (const p of patches) fs.copyFileSync(p.file, path.join(backup, p.name));
}
for (const p of patches) fs.writeFileSync(p.file, p.updated, 'utf8');
const report = patches.map(p => ({file: p.name, sha256: crypto.createHash('sha256').update(p.updated).digest('hex')}));
console.log(JSON.stringify({utf8ReadsPatched: report}));
