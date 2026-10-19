// 本機預覽 site/dist：照 _redirects 轉址、資料夾補斜線、找不到時回 404 頁，跟 Cloudflare Pages 一樣。
//   node site/serve.mjs [port]（預設 4321）

import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), 'dist');
const port = Number(process.argv[2] || 4321);
const TYPES = {
  '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.png': 'image/png', '.avif': 'image/avif',
  '.xml': 'application/xml', '.txt': 'text/plain; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
};

const redirects = new Map(
  (await readFile(path.join(root, '_redirects'), 'utf8').catch(() => ''))
    .split('\n').filter(Boolean)
    .map((line) => line.split(/\s+/))
    .map(([from, to, status = '302']) => [from, { to, status: Number(status) }]),
);

const isFile = (p) => stat(p).then((s) => s.isFile(), () => false);
const isDir = (p) => stat(p).then((s) => s.isDirectory(), () => false);

createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const pathname = decodeURIComponent(url.pathname);
  const rule = redirects.get(pathname);
  if (rule) return send(res, rule.status, { Location: rule.to });

  const file = path.join(root, pathname);
  if (!file.startsWith(root)) return send(res, 400);
  if (pathname.endsWith('/') && (await isFile(path.join(file, 'index.html')))) return sendFile(res, 200, path.join(file, 'index.html'));
  if (!pathname.endsWith('/') && (await isDir(file))) return send(res, 308, { Location: `${pathname}/${url.search}` });
  if (await isFile(file)) return sendFile(res, 200, file);
  const lang = pathname.match(/^\/(en|ja)\//)?.[1];
  sendFile(res, 404, path.join(root, lang ? `${lang}/404.html` : '404.html'));
}).listen(port, () => console.log(`http://localhost:${port}/`));

function send(res, status, headers = {}) {
  res.writeHead(status, headers).end();
}

async function sendFile(res, status, file) {
  res.writeHead(status, { 'Content-Type': TYPES[path.extname(file)] || 'application/octet-stream' });
  res.end(await readFile(file));
}
