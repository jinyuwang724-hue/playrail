import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const [hookPath, literalPath] = process.argv.slice(2);
if (!hookPath || !literalPath) throw new Error('Provide the staged browser hook and Playhub compatibility literals.');
const literals = JSON.parse(fs.readFileSync(literalPath, 'utf8').replace(/^\uFEFF/, ''));
if (literals.length !== 37 || literals[0] !== '2.1.2' || literals[1] !== 'css_browserhook.py') {
  throw new Error('Unexpected Playhub compatibility routine.');
}
const normalize = text => text.replace(/\r\n/g, '\n');
let source = normalize(fs.readFileSync(hookPath, 'utf8'));
const hash = text => crypto.createHash('sha256').update(text).digest('hex');
const before = hash(fs.readFileSync(hookPath));
const changes = [];

function findBlock(lines, block) {
  const candidates = [];
  const blockIndent = block[0].match(/^ */)[0].length;
  for (let start = 0; start <= lines.length - block.length; start++) {
    const indent = lines[start].match(/^ */)[0].length - blockIndent;
    if (indent < 0) continue;
    const prefix = ' '.repeat(indent);
    const match = block.every((line, offset) => {
      const actual = lines[start + offset].trimEnd();
      return line.trim() === '' ? actual === '' : actual === (prefix + line).trimEnd();
    });
    if (match) candidates.push({ start, indent });
  }
  return candidates;
}

for (let index = 4; index < literals.length; index += 3) {
  const oldLines = normalize(literals[index]).split('\n');
  const newLines = normalize(literals[index + 1]).split('\n');
  const lines = source.split('\n');
  const matches = findBlock(lines, oldLines);
  if (matches.length !== 1) throw new Error(`Compatibility block ${index}: expected one match, found ${matches.length}.`);
  const { start, indent } = matches[0];
  const replacement = newLines.map(line => line.trim() === '' ? '' : ' '.repeat(indent) + line);
  lines.splice(start, oldLines.length, ...replacement);
  source = lines.join('\n');
  changes.push({ block: index, line: start + 1, indentationAdjustment: indent });
}

fs.writeFileSync(hookPath, source);
const report = { source: hookPath, originalSha256: before, outputSha256: hash(source), changes };
fs.writeFileSync(path.join(path.dirname(literalPath), 'browser-hook-patch-report.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify({ patched: true, blocks: changes.length, sha256: report.outputSha256 }));
