import fs from 'node:fs';
const path = 'C:/Software/userdata/836107557/config/shortcuts.vdf';
const bytes = fs.readFileSync(path);
let offset = 0;
function cstring() {
  const end = bytes.indexOf(0, offset);
  if (end < 0) throw new Error('Unterminated VDF string');
  const value = bytes.toString('utf8', offset, end);
  offset = end + 1;
  return value;
}
function object() {
  const value = {};
  while (offset < bytes.length) {
    const type = bytes[offset++];
    if (type === 8) return value;
    const key = cstring();
    if (type === 0) value[key] = object();
    else if (type === 1) value[key] = cstring();
    else if (type === 2) { value[key] = bytes.readUInt32LE(offset); offset += 4; }
    else if (type === 7) { value[key] = bytes.readBigUInt64LE(offset).toString(); offset += 8; }
    else throw new Error(`Unsupported VDF type ${type} at ${offset}`);
  }
  return value;
}
const shortcuts = object().shortcuts;
const gamePattern = new RegExp(process.argv[2] ?? 'Zenless|绝区零', 'i');
for (const [index, shortcut] of Object.entries(shortcuts)) {
  if (gamePattern.test(shortcut.AppName ?? shortcut.appname ?? '') &&
      (!process.argv[3] || Number(process.argv[3]) === shortcut.appid)) {
    const gameID = ((BigInt(shortcut.appid) << 32n) | 0x02000000n).toString();
    console.log(JSON.stringify({ index, gameID, ...shortcut }, null, 2));
  }
}
