// 网页版墙面的对比预览服务：不需要 npm / esbuild，用 Node 自带的 TypeScript 去类型（node:module stripTypeScriptTypes）
// 直接把 frontend/library/src 当 ES 模块发给浏览器；游戏库数据与封面用原生版导出的同一份（--export-web 与套件目录）。
//   node web-preview.mjs --src ../../library --lib <library.js> --art <套件目录> [--port 8765]
// 页面地址加 #expand / #collection / #styleb / #tab / #nav，约 1.8 秒后把墙摆到对应状态（与原生版 --state= 相同的选法）。
import { createServer } from 'node:http';
import { readFileSync, existsSync, statSync } from 'node:fs';
import { join, resolve, extname } from 'node:path';
import { stripTypeScriptTypes } from 'node:module';

const arg = (name, dflt) => { const i = process.argv.indexOf(`--${name}`); return i > 0 ? process.argv[i + 1] : dflt; };
const src = resolve(arg('src', '../../library'));
const lib = resolve(arg('lib', 'library.js'));
const art = resolve(arg('art', '.'));
const port = Number(arg('port', '8765'));

const STATE = `
<script>
addEventListener('load', () => setTimeout(() => {
  const s = window.stage; const st = location.hash.slice(1);
  if (!s || !st) return;
  if (st === 'expand') s.activate();
  else if (st === 'styleb') document.documentElement.dataset.title = 'b';
  else if (st === 'tab') s.switchTab(1);
  else if (st === 'nav') { let i = 0; const t = setInterval(() => { s.move('right'); if (++i >= 3) clearInterval(t); }, 250); }
  else if (st === 'collection') {
    const vis = s.visible(); let best = null, bd = Infinity;
    for (const t of s.tiles.values()) {
      const id = s.itemFor(t.slot); const it = id && t.lookup(id);
      const sl = t.slot;
      if (!it || it.kind !== 'collection') continue;
      if (!(vis.left < sl.cx && sl.cx < vis.right && vis.top < sl.cy && sl.cy < vis.bottom)) continue;
      const d = (sl.cx - s.center.x) ** 2 + (sl.cy - s.center.y) ** 2;
      if (d < bd) { bd = d; best = sl; }
    }
    if (best) { s.focus(best.key); setTimeout(() => s.activate(), 700); }
  }
}, 1800));
</script>`;

const page = () => `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Claw 游戏库（网页版对比）</title><link rel="stylesheet" href="/src/styles.css"></head>
<body><div id="app"></div><script src="/library.js"></script>${STATE}<script type="module" src="/src/main.ts"></script></body></html>`;

const send = (res, code, type, body) => { res.writeHead(code, { 'content-type': type, 'cache-control': 'no-store' }); res.end(body); };

createServer((req, res) => {
  try {
    const url = decodeURIComponent(new URL(req.url, 'http://x').pathname);
    if (url === '/' || url === '/index.html') return send(res, 200, 'text/html; charset=utf-8', page());
    if (url === '/library.js') return send(res, 200, 'text/javascript; charset=utf-8', readFileSync(lib));
    if (url.startsWith('/art/')) {
      // /art/<id>/<k>.webp → 套件里的 <k>.jpg（浏览器按内容识别格式）
      const [, , id, file] = url.split('/');
      const p = join(art, id, file.replace(/\.webp$/, '.jpg'));
      return existsSync(p) ? send(res, 200, 'image/jpeg', readFileSync(p)) : send(res, 404, 'text/plain', 'no');
    }
    if (url.startsWith('/src/')) {
      let p = join(src, url);
      if (extname(p) === '.css') return send(res, 200, 'text/css; charset=utf-8', readFileSync(p));
      if (!existsSync(p) || statSync(p).isDirectory()) {
        // 不带扩展名的导入：重定向到真实文件，浏览器才会按正确的目录解析它里面的相对导入
        const to = existsSync(p + '.ts') ? url + '.ts' : url.replace(/\/?$/, '/index.ts');
        res.writeHead(302, { location: to }); return res.end();
      }
      let code = readFileSync(p, 'utf8').replace(/^import '\.\/styles\.css';\s*$/m, '');
      code = stripTypeScriptTypes(code, { mode: 'transform', sourceUrl: url });
      return send(res, 200, 'text/javascript; charset=utf-8', code);
    }
    send(res, 404, 'text/plain', 'not found');
  } catch (e) {
    console.error(req.url, e);
    send(res, 500, 'text/plain', String(e));
  }
}).listen(port, '127.0.0.1', () => console.log(`web preview on http://127.0.0.1:${port}/`));
