// PNG compression for RGBA exports; use an indexed palette only when an MSIX logo exceeds 200 KiB.
import fs from 'node:fs';
import zlib from 'node:zlib';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';

const crcTable = Array.from({ length: 256 }, (_, n) => {
  for (let k = 0; k < 8; k++) n = n & 1 ? 0xedb88320 ^ (n >>> 1) : n >>> 1;
  return n >>> 0;
});
function crc32(bytes) {
  let crc = 0xffffffff;
  for (const b of bytes) crc = crcTable[(crc ^ b) & 255] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const result = Buffer.alloc(data.length + 12);
  result.writeUInt32BE(data.length, 0); result.write(type, 4, 4, 'ascii');
  data.copy(result, 8);
  result.writeUInt32BE(crc32(result.subarray(4, -4)), result.length - 4);
  return result;
}
function paeth(a, b, c) {
  const p = a + b - c, da = Math.abs(p - a), db = Math.abs(p - b), dc = Math.abs(p - c);
  return da <= db && da <= dc ? a : db <= dc ? b : c;
}
function predictor(type, a, b, c) {
  return type === 0 ? 0 : type === 1 ? a : type === 2 ? b : type === 3 ? (a + b) >>> 1 : paeth(a, b, c);
}
function decode(scanlines, width, height) {
  const stride = width * 4, pixels = Buffer.alloc(stride * height);
  if (scanlines.length !== (stride + 1) * height) throw new Error('Unexpected PNG scanline length');
  for (let y = 0; y < height; y++) {
    const type = scanlines[y * (stride + 1)];
    if (type > 4) throw new Error('Unsupported PNG filter');
    for (let x = 0; x < stride; x++) {
      const i = y * stride + x;
      pixels[i] = (scanlines[y * (stride + 1) + 1 + x] + predictor(type,
        x >= 4 ? pixels[i - 4] : 0, y ? pixels[i - stride] : 0,
        y && x >= 4 ? pixels[i - stride - 4] : 0)) & 255;
    }
  }
  return pixels;
}
function encode(pixels, width, height) {
  const stride = width * 4, result = Buffer.alloc((stride + 1) * height);
  const candidates = Array.from({ length: 5 }, () => Buffer.alloc(stride));
  for (let y = 0; y < height; y++) {
    let best = 0, bestScore = Infinity;
    for (let type = 0; type < 5; type++) {
      let score = 0;
      for (let x = 0; x < stride; x++) {
        const i = y * stride + x;
        const value = (pixels[i] - predictor(type, x >= 4 ? pixels[i - 4] : 0,
          y ? pixels[i - stride] : 0, y && x >= 4 ? pixels[i - stride - 4] : 0)) & 255;
        candidates[type][x] = value;
        score += Math.min(value, 256 - value);
      }
      if (score < bestScore) { best = type; bestScore = score; }
    }
    result[y * (stride + 1)] = best;
    candidates[best].copy(result, y * (stride + 1) + 1);
  }
  return result;
}

for (const path of process.argv.slice(2)) {
  const original = fs.readFileSync(path), chunks = [], idat = [];
  if (!original.subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10]))) throw new Error('Not a PNG');
  for (let offset = 8; offset < original.length;) {
    const size = original.readUInt32BE(offset), type = original.toString('ascii', offset + 4, offset + 8);
    const data = original.subarray(offset + 8, offset + 8 + size);
    chunks.push({ type, data });
    if (type === 'IDAT') idat.push(data);
    offset += size + 12;
  }
  const header = chunks.find(c => c.type === 'IHDR').data;
  if (header[8] !== 8 || header[9] !== 6 || header[12] !== 0) throw new Error('Expected non-interlaced 8-bit RGBA PNG');
  const width = header.readUInt32BE(0), height = header.readUInt32BE(4);
  const oldScanlines = zlib.inflateSync(Buffer.concat(idat));
  const pixels = decode(oldScanlines, width, height), newScanlines = encode(pixels, width, height);
  if (!decode(newScanlines, width, height).equals(pixels)) throw new Error('PNG pixel verification failed');
  const candidates = [oldScanlines, newScanlines].map(data => zlib.deflateSync(data, { level: 9 }));
  const packed = candidates.reduce((a, b) => a.length <= b.length ? a : b);
  let inserted = false;
  const parts = [original.subarray(0, 8)];
  for (const c of chunks) {
    if (c.type !== 'IDAT') parts.push(chunk(c.type, c.data));
    else if (!inserted) { parts.push(chunk('IDAT', packed)); inserted = true; }
  }
  let optimized = Buffer.concat(parts), indexedPalette = false;
  if (/Logo/i.test(path) && Math.min(original.length, optimized.length) > 204800) {
    const require = createRequire(import.meta.url);
    let sharp;
    try { sharp = require('sharp'); }
    catch { sharp = require(require.resolve('sharp', { paths: [resolve(dirname(process.execPath), '..', 'node_modules')] })); }
    optimized = await sharp(original).png({ palette: true, colours: 256, dither: 0, effort: 10, compressionLevel: 9 }).toBuffer();
    indexedPalette = true;
    if (optimized.length > 204800) throw new Error('MSIX logo still exceeds 200 KiB: ' + path);
  }
  if (optimized.length < original.length) fs.writeFileSync(path, optimized);
  console.log(JSON.stringify({ path, before: original.length, after: Math.min(original.length, optimized.length), indexedPalette }));
}
