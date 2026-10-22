// 給搜尋引擎和 AI 讀的資料（docs/website.md §14）：JSON-LD 結構化資料、/llms.txt。

import { content, LANGS, GITHUB } from './content.mjs';
import { ROUTES } from './pages.mjs';

// IndexNow（docs/website.md §14.1）：金鑰是公開的，建置時放在根目錄，讓搜尋引擎確認網站是我們的
export const INDEXNOW_KEY = 'fb4f61486ebf3a373e2b3da60a37f158';

const AUTHOR = { '@type': 'Person', name: 'juinwei7', url: 'https://github.com/juinwei7' };

// JSON-LD 是資料區塊，不會執行；把 < 換掉，內容裡的 </script> 才不會提前關掉標籤
export function jsonLd(data) {
  return `<script type="application/ld+json">${JSON.stringify(data).replace(/</g, '\\u003c')}</script>\n`;
}

const plain = (html) => String(html).replace(/<br\s*\/?>/g, ' ').replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim();

/** 首頁：網站、app、常見問題（faq：頁面上的常見問題，有 Windows 版時答案不一樣，§16.2） */
export function homeSchema({ lang, site, latest, faq = content[lang].faq.items }) {
  const c = content[lang];
  const url = `${site}${ROUTES.home(lang)}`;
  return jsonLd({
    '@context': 'https://schema.org',
    '@graph': [
      {
        '@type': 'WebSite',
        '@id': `${site}/#website`,
        url: `${site}/`,
        name: '拾音 Shiyin',
        alternateName: ['Shiyin', '拾音'],
        inLanguage: LANGS.map((l) => content[l].lang),
        publisher: AUTHOR,
      },
      {
        '@type': 'SoftwareApplication',
        '@id': `${site}/#app`,
        name: c.seo.appName,
        alternateName: ['Shiyin', '拾音'],
        description: c.meta.description,
        url,
        image: `${site}/icon.png`,
        screenshot: `${site}${c.images}/summary.png`,
        applicationCategory: 'BusinessApplication',
        applicationSubCategory: c.seo.category,
        operatingSystem: 'macOS 26 or later (Apple Silicon)',
        softwareVersion: latest.version,
        datePublished: latest.date.slice(0, 10),
        downloadUrl: latest.dmg.url,
        installUrl: `${site}${c.prefix}/download`,
        releaseNotes: `${site}${ROUTES.changelog(lang)}`,
        softwareRequirements: 'macOS 26, Apple Silicon (M1 or later), 8 GB RAM',
        featureList: c.seo.features,
        inLanguage: ['zh-Hant', 'en'],
        isAccessibleForFree: true,
        license: 'https://opensource.org/licenses/MIT',
        offers: { '@type': 'Offer', price: '0', priceCurrency: 'USD' },
        author: AUTHOR,
        codeRepository: GITHUB,
        sameAs: [GITHUB],
      },
      {
        '@type': 'VideoObject',
        '@id': `${url}#video`,
        name: c.video.name,
        description: c.video.description,
        thumbnailUrl: `${site}/videos/shiyin-promo-${c.video.file}.jpg`,
        contentUrl: `${site}/videos/shiyin-promo-${c.video.file}.mp4`,
        uploadDate: '2026-10-21',
        duration: 'PT52S',
        inLanguage: c.video.file === 'zh' ? 'zh-Hant' : 'en',
        about: { '@id': `${site}/#app` },
      },
      {
        '@type': 'FAQPage',
        '@id': `${url}#faq`,
        inLanguage: c.lang,
        mainEntity: faq.map(([q, a]) => ({
          '@type': 'Question',
          name: plain(q),
          acceptedAnswer: { '@type': 'Answer', text: plain(a) },
        })),
      },
    ],
  });
}

/** Windows 版的頁面（docs/website.md §16.1）：Windows 版的 app、常見問題 */
export function windowsSchema({ lang, site, win }) {
  const c = content[lang];
  const w = c.windowsPage;
  const url = `${site}${ROUTES.windows(lang)}`;
  return jsonLd({
    '@context': 'https://schema.org',
    '@graph': [
      {
        '@type': 'SoftwareApplication',
        '@id': `${site}/#app-windows`,
        name: `${c.seo.appName} for Windows`,
        alternateName: ['Shiyin for Windows', '拾音 Windows 版'],
        description: w.description,
        url,
        image: `${site}/icon.png`,
        screenshot: `${site}${c.images}/windows/summary.png`,
        applicationCategory: 'BusinessApplication',
        applicationSubCategory: c.seo.category,
        operatingSystem: 'Windows 11 (x64, ARM64)',
        softwareVersion: win.version,
        datePublished: win.date.slice(0, 10),
        downloadUrl: win.x64.url,
        installUrl: `${site}${c.prefix}/download/windows`,
        softwareRequirements: 'Windows 11 (x64 or ARM64), 8 GB RAM; an NVIDIA graphics card is optional and speeds up transcription',
        featureList: c.seo.features,
        inLanguage: ['zh-Hant', 'en'],
        isAccessibleForFree: true,
        license: 'https://opensource.org/licenses/MIT',
        offers: { '@type': 'Offer', price: '0', priceCurrency: 'USD' },
        author: AUTHOR,
        codeRepository: GITHUB,
        sameAs: [GITHUB],
      },
      {
        '@type': 'FAQPage',
        '@id': `${url}#faq`,
        inLanguage: c.lang,
        mainEntity: w.faq.map(([q, a]) => ({ '@type': 'Question', name: plain(q), acceptedAnswer: { '@type': 'Answer', text: plain(a) } })),
      },
    ],
  });
}

/** 一般頁面（版本說明、指南列表）：url 沒給時用 route 算 */
export function pageSchema({ lang, site, route, url, type = 'WebPage', title, description, modified }) {
  return jsonLd({
    '@context': 'https://schema.org',
    '@type': type,
    url: url ?? `${site}${ROUTES[route](lang)}`,
    name: title,
    description,
    inLanguage: content[lang].lang,
    dateModified: modified.slice(0, 10),
    isPartOf: { '@id': `${site}/#website` },
    about: { '@id': `${site}/#app` },
  });
}

/** 指南的一篇（§14.2）：文章、麵包屑、常見問題 */
export function guideSchema({ lang, site, guide, url }) {
  const c = content[lang];
  const g = guide[lang];
  const graph = [
    {
      '@type': 'Article',
      '@id': `${url}#article`,
      headline: plain(g.h1),
      description: plain(g.description),
      inLanguage: c.lang,
      datePublished: guide.published,
      dateModified: guide.updated,
      author: AUTHOR,
      publisher: AUTHOR,
      image: `${site}/${c.og}`,
      mainEntityOfPage: url,
      isPartOf: { '@id': `${site}/#website` },
      about: { '@id': `${site}/#app` },
    },
    {
      '@type': 'BreadcrumbList',
      itemListElement: [
        { '@type': 'ListItem', position: 1, name: c.appName, item: `${site}${ROUTES.home(lang)}` },
        { '@type': 'ListItem', position: 2, name: c.guides.nav, item: `${site}${ROUTES.guides(lang)}` },
        { '@type': 'ListItem', position: 3, name: plain(g.h1), item: url },
      ],
    },
  ];
  if (g.faq) {
    graph.push({
      '@type': 'FAQPage',
      '@id': `${url}#faq`,
      inLanguage: c.lang,
      mainEntity: g.faq.map(([q, a]) => ({ '@type': 'Question', name: plain(q), acceptedAnswer: { '@type': 'Answer', text: plain(a) } })),
    });
  }
  return jsonLd({ '@context': 'https://schema.org', '@graph': graph });
}

/** /llms.txt（llmstxt.org）：英文為主，附繁中摘要。功能有變要跟著改（docs/website.md §14.1） */
export function llmsTxt({ site, latest, windows = null, guides = [] }) {
  const guideLinks = guides.filter((g) => g.en).map((g) => `- [${g.en.h1}](${site}/en/guides/${g.slug}/): ${plain(g.en.description)}`);
  const guideLinksZh = guides.filter((g) => g.zh && !g.en).map((g) => `- [${g.zh.h1}](${site}/guides/${g.slug}/): ${plain(g.zh.description)}`);
  const date = latest.date.slice(0, 10);
  // Windows 版（docs/website.md §16.2）：有 release 才寫出版本和頁面，不然寫開發中
  const windowsLine = windows
    ? `Also available for Windows 11 (x64 and ARM64): Shiyin for Windows ${windows.version} (released ${windows.date.slice(0, 10)}), with the same features; transcription uses an NVIDIA graphics card automatically when there is one. See ${site}/en/windows/.`
    : 'A Windows version is in development.';
  const windowsPage = windows ? `- [Shiyin for Windows](${site}/en/windows/): requirements, download and install steps for Windows 11\n- [Download the latest Windows installer (x64)](${site}/download/windows)\n` : '';
  const windowsZh = windows ? `也有 Windows 版（Windows 11，x64 和 ARM）：${site}/windows/。` : 'Windows 版開發中。';
  return `# Shiyin (拾音)

> Shiyin is a free, open-source (MIT) meeting-notes app for macOS. It records in-person and online meetings (Zoom, Google Meet, Microsoft Teams) without a bot joining the call, shows a live transcript, transcribes the recording locally on the Mac, identifies speakers, and writes meeting notes where every item has a timestamp that plays the original audio. Recordings never leave the Mac.

Latest version: ${latest.version} (released ${date}). Requires macOS 26 or later on Apple Silicon (M1 or later) and 8 GB of RAM. ${windowsLine} Made by an independent developer in Taiwan.

## What makes it different

- Local transcription: recordings are transcribed on the Mac with whisper.cpp. No audio is uploaded, and transcription works offline.
- No meeting bot: it records the microphone and the computer's audio directly, so the other side of Zoom, Meet or Teams is captured without a bot joining.
- Built for Taiwanese Mandarin mixed with English: Chinese and English meetings use Breeze ASR 25 (MediaTek Research), tuned for code-switching such as "deploy" or "Redis" inside Mandarin sentences. Japanese meetings use Whisper large-v3-turbo. The meeting language is detected automatically.
- Bring your own AI: notes are written by the Claude or ChatGPT subscription you already have (through Claude Code or Codex), or with an Anthropic, OpenAI, Gemini or OpenAI-compatible API key (Groq, OpenRouter). For a fully offline setup, point it at a local model in Ollama or LM Studio, and nothing leaves the Mac at all. Only transcript text is sent, and only to the provider you pick. You can also choose not to summarize and keep just the transcript.
- Notes you can check: a summary, decisions, action items and open questions, each with a timestamp. Names and terms the AI is unsure about are listed under "To confirm" instead of being guessed.
- Speaker identification with voiceprints: name a speaker once and Shiyin recognizes the voice in later meetings. Speakers can be merged or reassigned line by line.
- Ask your meetings: ask a question about one meeting or all meetings and get answers with timestamps.
- Price: free. No account, no subscription, no usage limits. The only cost is your own AI subscription or API usage, if you use one.

## Pages

- [Home (Traditional Chinese)](${site}/): features, privacy, download, FAQ
- [Home (English)](${site}/en/)
- [Home (Japanese)](${site}/ja/)
- [Release notes](${site}/en/changelog/): what changed in each version
- [Download the latest DMG](${site}/download)
${windowsPage}- [Source code on GitHub](${GITHUB})
- [README (English)](${GITHUB}/blob/main/.github/README.md)
${guideLinks.length || guideLinksZh.length ? `\n## Guides and comparisons\n\n${[...guideLinks, ...guideLinksZh].join('\n')}\n` : ''}
## 繁體中文摘要

拾音是 Mac 上免費、開源（MIT）的會議記錄 app：錄現場或線上會議（Zoom、Google Meet、Teams，不用派機器人進會議），錄音中有即時逐字稿，錄完在 Mac 本機轉錄（錄音不上傳、可離線），分出說話者，再用你自己的 Claude、ChatGPT 訂閱、API，或本機的 Ollama、LM Studio（完全不連網）整理成會議紀錄（摘要、決議、待辦），每一條都附時間點，點了就聽到原話。中文轉錄用 Breeze ASR 25，針對台灣中文夾英文；也支援英文、日文會議。需要 macOS 26 和 Apple Silicon，${windowsZh}
`;
}
