// 首頁、版本說明、404 的 HTML（docs/website.md §4）。深色模式跟著系統；只有首頁的示範用 script（§10）。

import { content, LANGS, GITHUB } from './content.mjs';
import { demoSection } from './demo-markup.mjs';

export const ROUTES = {
  home: (lang) => `${content[lang].prefix}/`,
  changelog: (lang) => `${content[lang].prefix}/changelog/`,
};

export function escape(text) {
  return String(text).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

const ICON = {
  globe: '<svg width="15" height="15" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" aria-hidden="true"><circle cx="8" cy="8" r="6.5"/><path d="M1.5 8h13M8 1.5c1.8 1.9 2.7 4 2.7 6.5S9.8 12.6 8 14.5M8 1.5C6.2 3.4 5.3 5.5 5.3 8s.9 4.6 2.7 6.5"/></svg>',
  chevron: '<svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true"><path d="M2 3.5l3 3 3-3"/></svg>',
  check: '<svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M2.5 6.2l2.3 2.3 4.7-5"/></svg>',
  star: '<svg width="18" height="18" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path d="M8 .9l2.2 4.5 4.9.7-3.6 3.5.9 4.9L8 12.2l-4.4 2.3.9-4.9L.9 6.1l4.9-.7z"/></svg>',
};

// 語言選單：跟 app 的語言選單一樣（地球、目前的語言、箭頭），目前的語言打勾。用 <details>，不需要 script
function languageMenu(lang, route) {
  const c = content[lang];
  const items = LANGS.map((l) => {
    const current = l === lang;
    return `<li><a href="${ROUTES[route](l)}" lang="${content[l].lang}" hreflang="${content[l].lang}"${current ? ' aria-current="page"' : ''}>${current ? ICON.check : ''}${content[l].label}</a></li>`;
  });
  return `<details class="lang">
    <summary aria-label="${c.nav.languages}：${c.label}">${ICON.globe}<span>${c.label}</span>${ICON.chevron}</summary>
    <ul>${items.join('')}</ul>
  </details>`;
}

// 共用的外框：head、頁首、頁尾。route 是 ROUTES 的鍵；404 頁沒有 route，不放 canonical，語言選單連到各語言首頁
// head、scripts：只有首頁用（示範的 noscript 樣式和 script）
function layout({ lang, site, css, route, title, description, body, head = '', scripts = '' }) {
  const c = content[lang];
  const others = LANGS.filter((l) => l !== lang);
  const target = route || 'home';
  const seo = route
    ? `<link rel="canonical" href="${site}${ROUTES[route](lang)}">
${LANGS.map((l) => `<link rel="alternate" hreflang="${content[l].lang}" href="${site}${ROUTES[route](l)}">`).join('\n')}
<link rel="alternate" hreflang="x-default" href="${site}${ROUTES[route]('zh')}">
<meta property="og:type" content="website">
<meta property="og:site_name" content="拾音 Shiyin">
<meta property="og:title" content="${escape(title)}">
<meta property="og:description" content="${escape(description)}">
<meta property="og:url" content="${site}${ROUTES[route](lang)}">
<meta property="og:image" content="${site}/${c.og}">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta property="og:image:alt" content="${escape(c.intro.h1)}">
<meta property="og:locale" content="${c.ogLocale}">
${others.map((l) => `<meta property="og:locale:alternate" content="${content[l].ogLocale}">`).join('\n')}
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
  </nav>
  ${languageMenu(lang, target)}
</header>
<main>
${body}
</main>
<footer>
  <div class="wide">
    <span>${c.footer.tagline}</span>
    <a href="${GITHUB}">GitHub</a>
    <a href="${ROUTES.changelog(lang)}">${c.nav.changelog}</a>
${others.map((l) => `    <a href="${ROUTES[target](l)}" lang="${content[l].lang}" hreflang="${content[l].lang}">${content[l].label}</a>`).join('\n')}
  </div>
</footer>
${scripts}</body>
</html>
`;
}

// 截圖：優先用 AVIF（960、1920 寬），PNG 當備援。first 是第一屏那張，不延後載入
function shot(lang, name, alt, { first = false, caption = '' } = {}) {
  const dir = content[lang].images;
  const loading = first ? 'fetchpriority="high"' : 'loading="lazy" decoding="async"';
  return `<picture>
  <source type="image/avif" srcset="${dir}/${name}-960.avif 960w, ${dir}/${name}.avif 1920w" sizes="(max-width: 1000px) calc(100vw - 40px), 960px">
  <img class="shot" src="${dir}/${name}.png" width="1920" height="1344" alt="${escape(alt)}" ${loading}>
</picture>${caption ? `\n<figcaption>${caption}</figcaption>` : ''}`;
}

// GitHub 數據（docs/website.md §11、§12）：點星按鈕裡放星星數（讀不到就不放），下載次數另外一行（0 就不顯示）
const formatNumber = (lang, n) => new Intl.NumberFormat({ zh: 'zh-TW', en: 'en-US', ja: 'ja-JP' }[lang]).format(n);

function starButton(lang, stats) {
  const count = stats?.stars != null ? `<span class="count">${formatNumber(lang, stats.stars)}</span>` : '';
  return `<a class="star" href="${GITHUB}">${ICON.star}${content[lang].intro.star}${count}</a>`;
}

// Windows 版的下載（docs/website.md §7）：還沒有 Windows 版時是空的
function windowsDownload(lang, win) {
  if (!win) return '';
  const w = content[lang].download.windows;
  return `<div class="windows">
<h3>${w.h3}</h3>
<p class="get"><a class="download" href="${escape(win.x64.url)}" title="${escape(win.x64.name)}">${w.button}</a></p>
<p class="arm"><a href="${escape(win.arm64.url)}" title="${escape(win.arm64.name)}">${w.arm}</a></p>
<p class="note">${w.note(win.version)}</p>
<p class="note">${w.sac}</p>
</div>
`;
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

// demo：示範的樣式和 script 網址（build.mjs 讀進來）
export function homePage({ lang, site, css, releases, windows, demo, stats }) {
  const c = content[lang];
  const latest = releases[0];
  const notesHref = `${ROUTES.changelog(lang)}#${latest.tag}`;
  const recent = releases.slice(0, 2).map((r) => `<div class="release">
  <h3><a href="${ROUTES.changelog(lang)}#${r.tag}">${r.version}</a><span>${escape(c.date(r.date))}</span></h3>
  ${releaseNotes(lang, r).items}
</div>`);

  const body = `<section class="intro">
  <img src="/icon.png" alt="${c.intro.iconAlt}" width="96" height="96">
  <h1>${c.intro.h1}</h1>
  <p>${c.intro.lead}</p>
  <div class="actions">
    <a class="download" href="#download">${c.intro.download(latest.version)}</a>
    ${starButton(lang, stats)}
  </div>
  <small>${c.intro.small}</small>
  ${downloadsLine(lang, stats)}
</section>

${demoSection(lang, { fallback: shot(lang, 'summary', c.shots.summary, { first: true }) })}

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
  ${starButton(lang, stats)}
</div>
</section>

<section class="col part">
<h2>${c.updates.h2}</h2>
${recent.join('\n')}
<p><a href="${ROUTES.changelog(lang)}">${c.updates.all}</a></p>
</section>

<section class="col part faq">
<h2>${c.faq.h2}</h2>
${c.faq.items.map(([q, a]) => `<details><summary>${q}</summary><p>${a}</p></details>`).join('\n')}
</section>`;

  return layout({
    lang, site, css: css + demo.css, route: 'home', title: c.meta.title, description: c.meta.description, body,
    // 沒有 JavaScript 時藏起示範，改顯示 <noscript> 裡的截圖
    head: '<noscript><style>.demo { display: none; }</style></noscript>\n',
    scripts: `<script src="${demo.script}"></script>\n`,
  });
}

export function changelogPage({ lang, site, css, releases }) {
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
  return layout({ lang, site, css, route: 'changelog', title: c.meta.changelogTitle, description: c.meta.changelogDescription, body });
}

export function notFoundPage({ lang, site, css }) {
  const c = content[lang];
  const body = `<div class="missing">
<h1>${c.notFound.h1}</h1>
<p>${c.notFound.p}</p>
<p class="links"><a href="${ROUTES.home(lang)}">${c.notFound.home}</a><a href="${ROUTES.changelog(lang)}">${c.notFound.changelog}</a></p>
</div>`;
  return layout({ lang, site, css, title: c.meta.notFoundTitle, description: c.meta.description, body });
}
