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
import { loadReleases, loadStars } from './src/releases.mjs';
import { ROUTES, homePage, changelogPage, notFoundPage, guidePage, guidesIndexPage, guidePath, guideLangs, guidesLangs } from './src/pages.mjs';
import { GUIDES } from './src/guides.mjs';
import { content, LANGS } from './src/content.mjs';
import { llmsTxt, INDEXNOW_KEY } from './src/seo.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..');
const { values: args } = parseArgs({ options: { releases: { type: 'string' }, repo: { type: 'string' }, out: { type: 'string' } } });
const out = path.resolve(args.out ?? path.join(here, 'dist'));
const site = (process.env.SITE_URL || 'https://shiyin.weibuilds.com').replace(/\/+$/, '');
const SHOTS = ['docs/images/summary.png', 'docs/images/recording.png', 'docs/images/transcript.png',
  'docs/images/en/summary.png', 'docs/images/en/recording.png', 'docs/images/en/transcript.png'];

try {
  const releases = await loadReleases({ file: args.releases });
  // GitHub 數據（docs/website.md §11）：星星數、所有版本的 DMG 下載次數加總
  const stats = {
    stars: await loadStars({ file: args.repo, offline: Boolean(args.releases) }),
    downloads: releases.reduce((sum, r) => sum + r.downloads, 0),
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

  // 介紹影片的雜湊（docs/website.md §15）：網址加 ?v=，換影片後瀏覽器不會用快取裡的舊檔
  const videos = {};
  for (const lang of ['zh', 'en']) {
    const file = path.join(here, `public/videos/shiyin-promo-${lang}.mp4`);
    videos[lang] = createHash('sha256').update(await readFile(file)).digest('hex').slice(0, 10);
  }
  const page = { site, css, releases, stats, videos };
  for (const lang of LANGS) {
    await write(ROUTES.home(lang) + 'index.html', homePage({ lang, ...page, demo }));
    await write(ROUTES.changelog(lang) + 'index.html', changelogPage({ lang, ...page }));
    await write(ROUTES.home(lang) + '404.html', notFoundPage({ lang, ...page }));
  }
  // 指南（docs/website.md §14.2）
  for (const guide of GUIDES) {
    for (const lang of guideLangs(guide)) await write(guidePath(lang, guide.slug) + 'index.html', guidePage({ lang, ...page, guide }));
  }
  for (const lang of guidesLangs()) await write(ROUTES.guides(lang) + 'index.html', guidesIndexPage({ lang, ...page }));

  // /download 永遠轉到最新版的 DMG，給 README、社群貼文用
  const dmg = releases[0].dmg.url;
  await write('/_redirects', LANGS.map((lang) => `${content[lang].prefix}/download ${dmg} 302\n`).join(''));
  await write('/robots.txt', `User-agent: *\nAllow: /\n\nSitemap: ${site}/sitemap.xml\n`);
  await write('/sitemap.xml', sitemap(releases[0].date));
  await write('/llms.txt', llmsTxt({ site, latest: releases[0], guides: GUIDES }));
  await write(`/${INDEXNOW_KEY}.txt`, INDEXNOW_KEY);

  console.log(`網站已產生：${path.relative(process.cwd(), out) || '.'}`);
  console.log(`網址 ${site}，共 ${releases.length} 個版本，最新 ${releases[0].version}（${releases[0].dmg.name}）`);
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

// lastmod：首頁和版本說明跟著最新版變，指南用各篇的 updated（docs/website.md §14.1）
function sitemap(updated) {
  // paths：各語言的網址（只列有的語言）
  const entries = (paths, lastmod) => {
    const langs = LANGS.filter((l) => paths[l]);
    const fallback = paths.zh ?? paths[langs[0]];
    return langs.map((lang) => `  <url>
    <loc>${site}${paths[lang]}</loc>
    <lastmod>${lastmod}</lastmod>
${langs.map((l) => `    <xhtml:link rel="alternate" hreflang="${content[l].lang}" href="${site}${paths[l]}"/>`).join('\n')}
    <xhtml:link rel="alternate" hreflang="x-default" href="${site}${fallback}"/>
  </url>`);
  };
  const all = (route, langs = LANGS) => Object.fromEntries(langs.map((l) => [l, ROUTES[route](l)]));
  const urls = [
    ...entries(all('home'), updated.slice(0, 10)),
    ...entries(all('changelog'), updated.slice(0, 10)),
    ...(GUIDES.length ? entries(all('guides', guidesLangs()), GUIDES.map((g) => g.updated).sort().at(-1)) : []),
    ...GUIDES.flatMap((g) => entries(Object.fromEntries(guideLangs(g).map((l) => [l, guidePath(l, g.slug)])), g.updated)),
  ];
  return `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">
${urls.join('\n')}
</urlset>
`;
}
