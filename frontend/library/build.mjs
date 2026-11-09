// 打包：dist/ 下是 index.html + main.js + main.css；--single 另外产出一个内联的 dist/library.html（预览用）。
import { build } from 'esbuild';
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';

mkdirSync('dist', { recursive: true });
await build({
    entryPoints: ['src/main.ts'],
    bundle: true,
    format: 'esm',
    target: ['chrome120'],
    minify: !process.argv.includes('--dev'),
    sourcemap: process.argv.includes('--dev'),
    outdir: 'dist',
    logLevel: 'info',
});
copyFileSync('index.html', 'dist/index.html');
if (process.argv.includes('--single')) {
    const js = readFileSync('dist/main.js', 'utf8').replace(/<\/script/g, '<\\/script');
    const css = readFileSync('dist/main.css', 'utf8');
    const html = readFileSync('index.html', 'utf8')
        .replace('<link rel="stylesheet" href="./main.css">', `<style>${css}</style>`)
        .replace('<script type="module" src="./main.js"></script>', `<script type="module">${js}</script>`);
    writeFileSync('dist/library.html', html);
}
