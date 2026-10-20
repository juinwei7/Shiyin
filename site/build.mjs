// 產生拾音官網到 site/dist/（docs/website.md §6）。只用 Node 內建功能，不用裝套件。
//
//   node site/build.mjs                       讀 GitHub Releases，產生網站
//   node site/build.mjs --releases <檔案>      改讀這份 JSON（跟 API 同樣格式），不連網路
//   node site/build.mjs --releases <檔案> --repo <檔案>   星星數也從檔案讀（repo API 的格式）
//
// 環境變數：SITE_URL（預設 https://shiyin.weibuilds.com）、GITHUB_TOKEN（選填，避免 API 次數限制）

import { cp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { loadAllReleases, loadStars } from './src/releases.mjs';
import { ROUTES, homePage, changelogPage, notFoundPage } from './src/pages.mjs';
import { content, LANGS } from './src/content.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..');
const { values: args } = parseArgs({ options: { releases: { type: 'string' }, repo: { type: 'string' }, out: { type: 'string' } } });
const out = path.resolve(args.out ?? path.join(here, 'dist'));
const site = (process.env.SITE_URL || 'https://shiyin.weibuilds.com').replace(/\/+$/, '');
const SHOTS = ['docs/images/summary.png', 'docs/images/recording.png', 'docs/images/transcript.png',
  'docs/images/en/summary.png', 'docs/images/en/recording.png', 'docs/images/en/transcript.png'];

try {
  const { mac: releases, windows } = await loadAllReleases({ file: args.releases });
  // GitHub 數據（docs/website.md §11）：星星數、所有版本的 DMG（和 Windows 安裝檔）下載次數加總
  const stats = {
    stars: await loadStars({ file: args.repo, offline: Boolean(args.releases) }),
    downloads: [...releases, ...windows].reduce((sum, r) => sum + r.downloads, 0),
  };
  await checkImages();
  const css = await readFile(path.join(here, 'src/style.css'), 'utf8');
  // 首頁的示範（docs/website.md §10）：樣式寫進首頁，script 另外放一個檔案（CSP 只允許自己網站的 script）
  const demoJs = await readFile(path.join(here, 'src/demo.js'));
  const demo = {
    css: await readFile(path.join(here, 'src/demo.css'), 'utf8'),
    script: `/demo.js?v=${createHash('sha256').update(demoJs).digest('hex').slice(0, 10)}`,
  };

  await rm(out, { recursive: true, force: true });
  await cp(path.join(here, 'public'), out, { recursive: true });
  await rm(path.join(out, 'images/sources.sha256'));
  // 截圖的 PNG 只有一份，在 docs/images（README 也用這份），當 AVIF 的備援
  for (const file of SHOTS) await cp(path.join(repo, file), path.join(out, file.replace('docs/', '')));
  await cp(path.join(repo, 'docs/images/icon.png'), path.join(out, 'icon.png'));
  await writeFile(path.join(out, 'demo.js'), demoJs);

  const page = { site, css, releases, windows: windows[0] ?? null, stats };
  for (const lang of LANGS) {
    await write(ROUTES.home(lang) + 'index.html', homePage({ lang, ...page, demo }));
    await write(ROUTES.changelog(lang) + 'index.html', changelogPage({ lang, ...page }));
    await write(ROUTES.home(lang) + '404.html', notFoundPage({ lang, ...page }));
  }

  // /download 永遠轉到最新版的 DMG，給 README、社群貼文用
  const dmg = releases[0].dmg.url;
  // Windows 版（docs/website.md §7）：/download/windows 是 x64、/download/windows-arm64 是 ARM64；還沒有 Windows 版時不加
  const win = windows[0];
  await write('/_redirects', LANGS.map((lang) => `${content[lang].prefix}/download ${dmg} 302\n`
    + (win ? `${content[lang].prefix}/download/windows ${win.x64.url} 302\n${content[lang].prefix}/download/windows-arm64 ${win.arm64.url} 302\n` : '')).join(''));
  await write('/robots.txt', `User-agent: *\nAllow: /\n\nSitemap: ${site}/sitemap.xml\n`);
  await write('/sitemap.xml', sitemap());

  console.log(`網站已產生：${path.relative(process.cwd(), out) || '.'}`);
  console.log(`網址 ${site}，共 ${releases.length} 個版本，最新 ${releases[0].version}（${releases[0].dmg.name}）`);
  if (win) console.log(`Windows 版：最新 ${win.version}（${win.x64.name}、${win.arm64.name}）`);
  console.log(`GitHub：${stats.stars ?? '（讀不到）'} 顆星，已下載 ${stats.downloads} 次`);
} catch (e) {
  console.error(e.message);
  process.exit(1);
}

async function write(file, text) {
  const target = path.join(out, file);
  await mkdir(path.dirname(target), { recursive: true });
  await writeFile(target, text);
}

// 截圖換過卻沒重新產生 AVIF 時，網頁會顯示舊圖，所以直接讓建置失敗
async function checkImages() {
  const recorded = await readFile(path.join(here, 'public/images/sources.sha256'), 'utf8').catch(() => '');
  const sums = new Map(recorded.trim().split('\n').map((line) => line.split(/\s+/).reverse()));
  const stale = [];
  for (const file of SHOTS) {
    const sum = createHash('sha256').update(await readFile(path.join(repo, file))).digest('hex');
    if (sums.get(file) !== sum) stale.push(file);
  }
  if (stale.length) {
    throw new Error(`這些截圖換過了，官網的 AVIF 還是舊的：${stale.join('、')}\n請在 Mac 上執行 site/make-images.sh，連同 site/public 一起 commit`);
  }
}

function sitemap() {
  const urls = ['home', 'changelog'].flatMap((route) => LANGS.map((lang) => `  <url>
    <loc>${site}${ROUTES[route](lang)}</loc>
${LANGS.map((l) => `    <xhtml:link rel="alternate" hreflang="${content[l].lang}" href="${site}${ROUTES[route](l)}"/>`).join('\n')}
  </url>`));
  return `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">
${urls.join('\n')}
</urlset>
`;
}
