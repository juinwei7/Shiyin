// 首頁、版本說明、404 的 HTML（docs/website.md §4）。深色模式跟著系統；只有首頁的示範用 script（§10）。

import { content, LANGS, GITHUB, KOFI } from './content.mjs';
import { demoSection } from './demo-markup.mjs';
import { homeSchema, pageSchema, guideSchema, windowsSchema } from './seo.mjs';
import { GUIDES } from './guides.mjs';

export const ROUTES = {
  home: (lang) => `${content[lang].prefix}/`,
  changelog: (lang) => `${content[lang].prefix}/changelog/`,
  guides: (lang) => `${content[lang].prefix}/guides/`,
  // 有 Windows 版的 release 才產生（docs/website.md §16）
  windows: (lang) => `${content[lang].prefix}/windows/`,
};

// 指南（§14.2）：每一篇只有寫了的語言才有頁面
export const guidePath = (lang, slug) => `${content[lang].prefix}/guides/${slug}/`;
export const guideLangs = (guide) => LANGS.filter((l) => guide[l]);
export const guidePaths = (guide) => Object.fromEntries(guideLangs(guide).map((l) => [l, guidePath(l, guide.slug)]));
// 指南列表：有至少一篇的語言才有
export const guidesLangs = () => LANGS.filter((l) => GUIDES.some((g) => g[l]));

export function escape(text) {
  return String(text).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

const ICON = {
  globe: '<svg width="15" height="15" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" aria-hidden="true"><circle cx="8" cy="8" r="6.5"/><path d="M1.5 8h13M8 1.5c1.8 1.9 2.7 4 2.7 6.5S9.8 12.6 8 14.5M8 1.5C6.2 3.4 5.3 5.5 5.3 8s.9 4.6 2.7 6.5"/></svg>',
  chevron: '<svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true"><path d="M2 3.5l3 3 3-3"/></svg>',
  check: '<svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M2.5 6.2l2.3 2.3 4.7-5"/></svg>',
  play: '<svg width="12" height="12" viewBox="0 0 12 12" fill="currentColor" aria-hidden="true"><path d="M3 1.8v8.4L10 6z"/></svg>',
  star: '<svg width="18" height="18" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path d="M8 .9l2.2 4.5 4.9.7-3.6 3.5.9 4.9L8 12.2l-4.4 2.3.9-4.9L.9 6.1l4.9-.7z"/></svg>',
  coffee: '<svg width="18" height="18" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" aria-hidden="true"><path d="M2.5 6h9v3.5a3.5 3.5 0 0 1-3.5 3.5H6a3.5 3.5 0 0 1-3.5-3.5z"/><path d="M11.5 7h1a1.75 1.75 0 0 1 0 3.5h-1.2M5.5 1.5v2.2M8.5 1.5v2.2"/></svg>',
};

// 語言選單：跟 app 的語言選單一樣（地球、目前的語言、箭頭），目前的語言打勾。用 <details>，不需要 script
function languageMenu(lang, href) {
  const c = content[lang];
  const items = LANGS.map((l) => {
    const current = l === lang;
    return `<li><a href="${href(l)}" lang="${content[l].lang}" hreflang="${content[l].lang}"${current ? ' aria-current="page"' : ''}>${current ? ICON.check : ''}${content[l].label}</a></li>`;
  });
  return `<details class="lang">
    <summary aria-label="${c.nav.languages}：${c.label}">${ICON.globe}<span>${c.label}</span>${ICON.chevron}</summary>
    <ul>${items.join('')}</ul>
  </details>`;
}

// 共用的外框：head、頁首、頁尾。route 是 ROUTES 的鍵；404 頁沒有 route，不放 canonical，語言選單連到各語言首頁
// paths：沒有 route 的頁面（教學、比較，§14.2）各語言的網址，只列有這一頁的語言；沒有的語言，語言選單連到那個語言的首頁
// head：首頁的示範樣式、各頁的結構化資料；scripts：只有首頁用（示範）
function layout({ lang, site, css, route, paths, title, description, body, head = '', scripts = '', ogType = 'website', windows = null }) {
  const c = content[lang];
  const others = LANGS.filter((l) => l !== lang);
  const target = route || 'home';
  const href = (l) => (paths ? paths[l] ?? ROUTES.home(l) : ROUTES[target](l));
  const alternates = paths ? LANGS.filter((l) => paths[l]) : LANGS;
  const indexable = Boolean(route || paths);
  // 切換平台（§16.2）：Windows 頁顯示「Mac」連回首頁，其他頁顯示「Windows」
  const platformHref = route === 'windows' ? ROUTES.home(lang) : ROUTES.windows(lang);
  const platformLabel = route === 'windows' ? c.nav.mac : c.nav.windows;
  const seo = indexable
    ? `<link rel="canonical" href="${site}${href(lang)}">
${alternates.map((l) => `<link rel="alternate" hreflang="${content[l].lang}" href="${site}${href(l)}">`).join('\n')}
<link rel="alternate" hreflang="x-default" href="${site}${href(alternates.includes('zh') ? 'zh' : alternates[0])}">
<meta property="og:type" content="${ogType}">
<meta property="og:site_name" content="拾音 Shiyin">
<meta property="og:title" content="${escape(title)}">
<meta property="og:description" content="${escape(description)}">
<meta property="og:url" content="${site}${href(lang)}">
<meta property="og:image" content="${site}/${c.og}">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta property="og:image:alt" content="${escape(c.intro.h1)}">
<meta property="og:locale" content="${c.ogLocale}">
${alternates.filter((l) => l !== lang).map((l) => `<meta property="og:locale:alternate" content="${content[l].ogLocale}">`).join('\n')}
<meta name="twitter:card" content="summary_large_image">`
    : '<meta name="robots" content="noindex">';
  return `<!doctype html>
<html lang="${c.lang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escape(title)}</title>
<meta name="description" content="${escape(description)}">
${seo}
<meta name="theme-color" content="#FFFFFF" media="(prefers-color-scheme: light)">
<meta name="theme-color" content="#1C1C1E" media="(prefers-color-scheme: dark)">
<link rel="icon" type="image/png" sizes="64x64" href="/favicon.png">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<style>
${css}</style>
${head}</head>
<body>
<header class="top">
  <a class="home" href="${ROUTES.home(lang)}"><img src="/favicon.png" alt="" width="26" height="26">${c.appName}</a>
  <nav aria-label="${c.nav.label}">
    <a class="sub" href="${ROUTES.home(lang)}#how">${c.nav.features}</a>
    <a class="sub" href="${ROUTES.home(lang)}#privacy">${c.nav.privacy}</a>
    <a href="${ROUTES.changelog(lang)}">${c.nav.changelog}</a>
${guidesLangs().includes(lang) ? `    <a class="sub" href="${ROUTES.guides(lang)}">${c.guides.nav}</a>\n` : ''}${windows ? `    <a class="sub" href="${platformHref}">${platformLabel}</a>\n` : ''}  </nav>
  ${languageMenu(lang, href)}
</header>
<main>
${body}
</main>
<footer>
  <div class="wide">
    <span>${c.footer.tagline}</span>
    <a href="${GITHUB}">GitHub</a>
    <a href="${KOFI}">${c.footer.sponsor}</a>
    <a href="${ROUTES.changelog(lang)}">${c.nav.changelog}</a>
${windows ? `    <a href="${platformHref}">${route === 'windows' ? c.footer.mac : c.footer.windows}</a>\n` : ''}${guidesLangs().includes(lang) ? `    <a href="${ROUTES.guides(lang)}">${c.guides.nav}</a>\n` : ''}${others.map((l) => `    <a href="${href(l)}" lang="${content[l].lang}" hreflang="${content[l].lang}">${content[l].label}</a>`).join('\n')}
  </div>
</footer>
${scripts}</body>
</html>
`;
}

// 截圖：優先用 AVIF（960、1920 寬），PNG 當備援。first 是第一屏那張，不延後載入
// win：Windows 版的截圖（docs/images/windows，1418×946，§16.1）
function shot(lang, name, alt, { first = false, caption = '', win = false } = {}) {
  const dir = content[lang].images + (win ? '/windows' : '');
  const [width, height] = win ? [1418, 946] : [1920, 1344];
  const loading = first ? 'fetchpriority="high"' : 'loading="lazy" decoding="async"';
  return `<picture>
  <source type="image/avif" srcset="${dir}/${name}-960.avif 960w, ${dir}/${name}.avif ${width}w" sizes="(max-width: 1000px) calc(100vw - 40px), 960px">
  <img class="shot" src="${dir}/${name}.png" width="${width}" height="${height}" alt="${escape(alt)}" ${loading}>
</picture>${caption ? `\n<figcaption>${caption}</figcaption>` : ''}`;
}

// 介紹影片（docs/website.md §15）：不自動播放、不先下載，按了才載入。
// versions：各影片的雜湊（build.mjs 算），接在網址後面，換了影片瀏覽器才不會用快取裡的舊檔
export const videoPath = (lang) => `/videos/shiyin-promo-${content[lang].video.file}`;
function videoSection(lang, versions = {}) {
  const v = content[lang].video;
  const version = versions[v.file] ? `?v=${versions[v.file]}` : '';
  return `<section class="wide part video" id="video">
<h2>${v.h2}</h2>
<video controls preload="none" playsinline width="1920" height="1080" poster="${videoPath(lang)}.jpg" aria-label="${escape(v.name)}">
  <source src="${videoPath(lang)}.mp4${version}" type="video/mp4">
</video>
</section>`;
}

// GitHub 數據（docs/website.md §11、§12）：點星按鈕裡放星星數（讀不到就不放），下載次數另外一行（0 就不顯示）
const formatNumber = (lang, n) => new Intl.NumberFormat({ zh: 'zh-TW', en: 'en-US', ja: 'ja-JP' }[lang]).format(n);

// 贊助（§13）：連到 Ko-fi，不嵌入它的面板和 script（CSP 只允許自己的 script）
function coffeeButton(lang) {
  return `<a class="star coffee" href="${KOFI}">${ICON.coffee}${content[lang].download.coffee}</a>`;
}

function starButton(lang, stats) {
  const count = stats?.stars != null ? `<span class="count">${formatNumber(lang, stats.stars)}</span>` : '';
  return `<a class="star" href="${GITHUB}">${ICON.star}${content[lang].intro.star}${count}</a>`;
}

// 首頁下載區的 Windows 版（docs/website.md §16.2）：一張卡片，需求和安裝步驟在 Windows 版的頁面。還沒有 Windows 版時是空的
function windowsDownload(lang, win) {
  if (!win) return '';
  const w = content[lang].download.windows;
  return `<div class="windows-card">
<div><h3>${w.h3}</h3><p>${w.p(win.version)}</p></div>
<p class="links"><a class="download" href="${escape(win.x64.url)}" title="${escape(win.x64.name)}">${w.button}</a><a href="${ROUTES.windows(lang)}#download">${w.more}</a></p>
</div>
`;
}

// 常見問題：「有 Windows 版嗎」的答案看有沒有 Windows 版（§16.2）
export function faqItems(lang, win) {
  const f = content[lang].faq;
  return [...f.items, [f.windows.q, win ? f.windows.available(ROUTES.windows(lang)) : f.windows.dev]];
}

// SmartScreen 的示意圖（§16.3）：兩格，左邊標出「其他資訊」，右邊標出「仍要執行」。純 CSS，不用截圖（三種語言都能用）
function smartScreen(lang, win) {
  const t = content[lang].windowsPage.download_.smartscreen;
  const box = (expanded) => `<div class="ss-box${expanded ? ' expanded' : ''}" aria-hidden="true">
  <p class="ss-title">${t.title}</p>
  <p class="ss-body">${t.body}</p>
  ${expanded
    ? `<p class="ss-info">${t.app}${escape(win.x64.name)}<br>${t.publisher}</p>
  <p class="ss-buttons"><span class="ss-run mark">${t.run}</span><span>${t.dont}</span></p>`
    : `<p class="ss-more"><span class="mark">${t.more}</span></p>
  <p class="ss-buttons"><span>${t.dont}</span></p>`}
</div>`;
  return `<figure class="smartscreen">
<div class="ss-pair">${box(false)}<span class="ss-arrow" aria-hidden="true">→</span>${box(true)}</div>
<figcaption>${t.caption}</figcaption>
</figure>`;
}

function downloadsLine(lang, stats) {
  return stats?.downloads > 0 ? `<small class="stats">${content[lang].intro.downloads(formatNumber(lang, stats.downloads))}</small>` : '';
}

const paragraphs = (ps) => ps.map((p) => `<p>${p}</p>`).join('\n');
const list = (items) => `<ul>${items.map((i) => `<li>${i}</li>`).join('')}</ul>`;

// 版本說明是從 GitHub 讀來的，要跳脫；日文版用英文那一段，標上 lang
function releaseNotes(lang, release) {
  const c = content[lang];
  const notes = release[c.notesLang];
  const attr = c.notesLang === lang ? '' : ` lang="${content[c.notesLang].lang}"`;
  return {
    subtitle: notes.subtitle ? `<h3${attr}>${escape(notes.subtitle)}</h3>` : '',
    items: `<ul${attr}>${notes.items.map((i) => `<li>${escape(i)}</li>`).join('')}</ul>`,
  };
}

// 首頁最後連到各篇指南（§14.2），從首頁連過去，新頁面比較快被搜尋引擎找到
function guidesSection(lang) {
  const guides = GUIDES.filter((g) => g[lang]);
  if (!guides.length) return '';
  return `\n\n<section class="col part guides-home">
<h2>${content[lang].guides.home}</h2>
<ul>${guides.map((g) => `<li><a href="${guidePath(lang, g.slug)}">${g[lang].h1}</a></li>`).join('')}</ul>
</section>`;
}

// demo：示範的樣式和 script 網址（build.mjs 讀進來）；platform：用 Windows 的訪客提示（§16.2）的 script 網址
export function homePage({ lang, site, css, releases, windows, demo, platform, stats, videos }) {
  const c = content[lang];
  const latest = releases[0];
  const notesHref = `${ROUTES.changelog(lang)}#${latest.tag}`;
  const recent = releases.slice(0, 2).map((r) => `<div class="release">
  <h3><a href="${ROUTES.changelog(lang)}#${r.tag}">${r.version}</a><span>${escape(c.date(r.date))}</span></h3>
  ${releaseNotes(lang, r).items}
</div>`);

  // 有 Windows 版時（§16.2）：第一屏多一個連結；用 Windows 的訪客多一行提示（/platform.js 打開，預設藏起來）
  const alsoWindows = windows ? `<span class="also">・<a href="${ROUTES.windows(lang)}">${c.intro.alsoWindows}</a></span>` : '';
  const onWindows = windows ? `<p class="on-windows" data-on-windows hidden><a href="${ROUTES.windows(lang)}">${c.intro.onWindows} →</a></p>\n` : '';
  const faq = faqItems(lang, windows);
  const body = `${onWindows}<section class="intro">
  <img src="/icon.png" alt="${c.intro.iconAlt}" width="96" height="96">
  <h1>${c.intro.h1}</h1>
  <p>${c.intro.lead}</p>
  <div class="actions">
    <a class="download" href="#download">${c.intro.download(latest.version)}</a>
    ${starButton(lang, stats)}
  </div>
  <small>${c.intro.small}${alsoWindows}</small>
  ${downloadsLine(lang, stats)}
  <a class="watch" href="#video">${ICON.play}${c.video.link}</a>
</section>

${demoSection(lang, { fallback: shot(lang, 'summary', c.shots.summary, { first: true }) })}

${videoSection(lang, videos)}

<section class="col part" id="how">
<h2>${c.live.h2}</h2>
${paragraphs(c.live.ps)}
</section>
<figure class="wide part">
${shot(lang, 'recording', c.shots.recording)}
</figure>

<section class="col part">
<h2>${c.transcribe.h2}</h2>
${paragraphs(c.transcribe.ps)}
</section>
<figure class="wide part">
${shot(lang, 'transcript', c.shots.transcript, { caption: c.shots.credit })}
</figure>

<section class="col part">
<h2>${c.notes.h2}</h2>
${paragraphs(c.notes.ps)}
<p class="note">${c.notes.note}</p>
</section>

<section class="col part" id="privacy">
<h2>${c.privacy.h2}</h2>
<p>${c.privacy.lead}</p>
<div class="two">
  <div><h3>${c.privacy.local.h3}</h3>${list(c.privacy.local.items)}</div>
  <div><h3>${c.privacy.sent.h3}</h3>${list(c.privacy.sent.items)}</div>
</div>
<p>${c.privacy.tools}</p>
</section>

<section class="col part" id="download">
<h2>${c.download.h2}</h2>
<dl class="req">
  <div><dt>${c.download.version}</dt><dd>${latest.version}<a href="${notesHref}">${c.download.whatsNew}</a></dd></div>
${c.download.rows.map(([k, v]) => `  <div><dt>${k}</dt><dd>${v}</dd></div>`).join('\n')}
</dl>
<p class="get"><a class="download" href="${escape(latest.dmg.url)}">${c.download.button(latest.dmg.name)}</a></p>
<p class="first">${c.download.firstOpen}</p>
<ol class="steps">
${c.download.steps.map((s) => `  <li>${s}</li>`).join('\n')}
</ol>
<p class="note">${c.download.note}</p>
${windowsDownload(lang, windows)}<div class="star-ask">
  <p>${c.download.starAsk}</p>
  <div class="asks">
    ${starButton(lang, stats)}
    ${coffeeButton(lang)}
  </div>
</div>
</section>

<section class="col part">
<h2>${c.updates.h2}</h2>
${recent.join('\n')}
<p><a href="${ROUTES.changelog(lang)}">${c.updates.all}</a></p>
</section>

<section class="col part faq">
<h2>${c.faq.h2}</h2>
${faq.map(([q, a]) => `<details><summary>${q}</summary><p>${a}</p></details>`).join('\n')}
</section>${guidesSection(lang)}`;

  return layout({
    lang, site, css: css + demo.css, route: 'home', title: c.meta.title, description: c.meta.description, body, windows,
    // 沒有 JavaScript 時藏起示範，改顯示 <noscript> 裡的截圖
    head: '<noscript><style>.demo { display: none; }</style></noscript>\n' + homeSchema({ lang, site, latest, faq }),
    scripts: `<script src="${demo.script}"></script>\n` + (windows ? `<script src="${platform}"></script>\n` : ''),
  });
}

// Windows 版的頁面（docs/website.md §16.1）：有 Windows 版的 release 才產生
export function windowsPage({ lang, site, css, windows: win, stats }) {
  const c = content[lang];
  const w = c.windowsPage;
  const d = w.download_;
  const notes = releaseNotes(lang, win);
  const body = `<section class="intro">
  <img src="/icon.png" alt="${c.intro.iconAlt}" width="96" height="96">
  <h1>${w.h1}</h1>
  <p>${w.lead}</p>
  <div class="actions">
    <a class="download" href="#download">${w.download(win.version)}</a>
    ${starButton(lang, stats)}
  </div>
  <small>${w.small}<span class="also">・<a href="${ROUTES.home(lang)}">${w.alsoMac}</a></span></small>
  ${downloadsLine(lang, stats)}
</section>

<figure class="wide">
${shot(lang, 'summary', w.shots.summary, { first: true, win: true })}
</figure>

<section class="col part" id="how">
<h2>${w.live.h2}</h2>
${paragraphs(w.live.ps)}
</section>
<figure class="wide part">
${shot(lang, 'recording', w.shots.recording, { win: true })}
</figure>

<section class="col part">
<h2>${w.transcribe.h2}</h2>
${paragraphs(w.transcribe.ps)}
<p class="gpu">${w.transcribe.gpu}</p>
</section>
<figure class="wide part">
${shot(lang, 'transcript', w.shots.transcript, { win: true, caption: c.shots.credit })}
</figure>

<section class="col part">
<h2>${c.notes.h2}</h2>
${paragraphs(c.notes.ps)}
<p class="note">${c.notes.note}</p>
</section>

<section class="col part" id="privacy">
<h2>${w.privacy.h2}</h2>
<p>${w.privacy.lead}</p>
<div class="two">
  <div><h3>${w.privacy.local}</h3>${list(c.privacy.local.items)}</div>
  <div><h3>${c.privacy.sent.h3}</h3>${list(c.privacy.sent.items)}</div>
</div>
<p>${c.privacy.tools}</p>
</section>

<section class="col part" id="download">
<h2>${d.h2}</h2>
<dl class="req">
  <div><dt>${c.download.version}</dt><dd>${win.version}<a href="${escape(win.url)}">${d.notes}</a></dd></div>
${d.rows.map(([k, v]) => `  <div><dt>${k}</dt><dd>${v}</dd></div>`).join('\n')}
</dl>
<div class="perf">
<h3>${d.perf.h3}</h3>
${list(d.perf.items)}
</div>
<p class="get"><a class="download" href="${escape(win.x64.url)}">${d.button(win.x64.name)}</a></p>
<p class="arm"><a href="${escape(win.arm64.url)}" title="${escape(win.arm64.name)}">${d.arm}</a></p>
<p class="first">${d.firstOpen}</p>
<ol class="steps">
${d.steps.map((step) => `  <li>${step}</li>`).join('\n')}
</ol>
${smartScreen(lang, win)}
<p class="note">${d.sac}</p>
<p class="note">${d.note}</p>
<div class="star-ask">
  <p>${c.download.starAsk}</p>
  <div class="asks">
    ${starButton(lang, stats)}
    ${coffeeButton(lang)}
  </div>
</div>
</section>

<section class="col part">
<h2>${w.updates.h2}</h2>
<div class="release">
  <h3><a href="${escape(win.url)}">${win.version}</a><span>${escape(c.date(win.date))}</span></h3>
  ${notes.items}
</div>
</section>

<section class="col part faq">
<h2>${c.faq.h2}</h2>
${w.faq.map(([q, a]) => `<details><summary>${q}</summary><p>${a}</p></details>`).join('\n')}
</section>`;
  return layout({
    lang, site, css, route: 'windows', title: w.title, description: w.description, body, windows: win,
    head: windowsSchema({ lang, site, win }),
  });
}

export function changelogPage({ lang, site, css, releases, windows }) {
  const c = content[lang];
  const articles = releases.map((r, i) => {
    const notes = releaseNotes(lang, r);
    const links = [
      i === 0 && r.dmg ? `<a href="${escape(r.dmg.url)}">${c.changelog.download}</a>` : '',
      `<a href="${escape(r.url)}">${c.changelog.release}</a>`,
    ].filter(Boolean);
    return `<article id="${r.tag}">
  <h2>${c.changelog.name} ${r.version}</h2>
  <p class="meta"><span>${escape(c.date(r.date))}</span>${links.join('')}</p>
  ${notes.subtitle}
  ${notes.items}
</article>`;
  });
  const body = `<div class="col page log">
<h1>${c.changelog.h1}</h1>
<p class="lead">${c.changelog.lead}</p>
<p class="follow">${c.changelog.follow}</p>
${articles.join('\n')}
</div>`;
  const title = c.meta.changelogTitle;
  const description = c.meta.changelogDescription;
  const head = pageSchema({ lang, site, route: 'changelog', title, description, modified: releases[0].date });
  return layout({ lang, site, css, route: 'changelog', title, description, body, head, windows });
}

export function notFoundPage({ lang, site, css, windows }) {
  const c = content[lang];
  const body = `<div class="missing">
<h1>${c.notFound.h1}</h1>
<p>${c.notFound.p}</p>
<p class="links"><a href="${ROUTES.home(lang)}">${c.notFound.home}</a><a href="${ROUTES.changelog(lang)}">${c.notFound.changelog}</a></p>
</div>`;
  return layout({ lang, site, css, title: c.meta.notFoundTitle, description: c.meta.description, body, windows });
}

// 指南的一篇（§14.2）：麵包屑、標題、最後更新、內文、常見問題、下載、其他指南
export function guidePage({ lang, site, css, guide, windows }) {
  const c = content[lang];
  const g = guide[lang];
  const L = c.guides;
  const related = GUIDES.filter((x) => x !== guide && x[lang]).slice(0, 5);
  const body = `<article class="col page guide">
<nav class="crumbs" aria-label="${c.nav.label}"><a href="${ROUTES.home(lang)}">${c.appName}</a><span aria-hidden="true">›</span><a href="${ROUTES.guides(lang)}">${L.nav}</a></nav>
<h1>${g.h1}</h1>
<p class="lead">${g.lead}</p>
<p class="updated"><time datetime="${guide.updated}">${L.updated(c.date(guide.updated))}</time></p>
${g.body.trim()}
${g.faq ? `<section class="faq">
<h2>${L.faq}</h2>
${g.faq.map(([q, a]) => `<details><summary>${q}</summary><p>${a}</p></details>`).join('\n')}
</section>
` : ''}<aside class="cta">
<h2>${L.cta.h2}</h2>
<p>${L.cta.p}</p>
<p class="get"><a class="download" href="${ROUTES.home(lang)}#download">${L.cta.button}</a><a href="${ROUTES.home(lang)}">${L.cta.more}</a></p>
</aside>
${related.length ? `<section class="related">
<h2>${L.related}</h2>
<ul>${related.map((x) => `<li><a href="${guidePath(lang, x.slug)}">${x[lang].h1}</a></li>`).join('')}</ul>
</section>
` : ''}</article>`;
  return layout({
    lang, site, css, paths: guidePaths(guide), title: g.title, description: g.description, body, ogType: 'article', windows,
    head: guideSchema({ lang, site, guide, url: `${site}${guidePath(lang, guide.slug)}` }),
  });
}

// 指南列表（§14.2）
export function guidesIndexPage({ lang, site, css, windows }) {
  const c = content[lang];
  const L = c.guides;
  const items = GUIDES.filter((g) => g[lang]).map((g) => `<li>
  <h2><a href="${guidePath(lang, g.slug)}">${g[lang].h1}</a></h2>
  <p>${g[lang].description}</p>
</li>`);
  const body = `<div class="col page guides">
<h1>${L.h1}</h1>
<p class="lead">${L.lead}</p>
<ul class="list">
${items.join('\n')}
</ul>
</div>`;
  const modified = GUIDES.filter((g) => g[lang]).map((g) => g.updated).sort().at(-1);
  const paths = Object.fromEntries(guidesLangs().map((l) => [l, ROUTES.guides(l)]));
  return layout({
    lang, site, css, paths, title: L.title, description: L.description, body, windows,
    head: pageSchema({ lang, site, url: `${site}${ROUTES.guides(lang)}`, type: 'CollectionPage', title: L.title, description: L.description, modified }),
  });
}
