// 讀 GitHub Releases、檢查版本說明的格式（CLAUDE.md「版本說明」、docs/website.md §7）。
// 格式不對就丟錯，讓建置失敗，不會部署壞掉的頁面。

import { readFile } from 'node:fs/promises';

const REPO = 'juinwei7/Shiyin';

/** 回傳正式發布的 Mac 版，版本號由新到舊；file 有給就讀這份 JSON（跟 API 同樣格式），不連網路 */
export async function loadReleases({ file } = {}) {
  return (await loadAllReleases({ file })).mac;
}

/**
 * Mac 版和 Windows 版（docs/website.md §7）：tag 是 win-v 開頭的是 Windows 版，其他都是 Mac 版。
 * 沒有 Windows 版時 windows 是空陣列，網站跟以前一樣
 */
export async function loadAllReleases({ file } = {}) {
  const raw = file ? JSON.parse(await readFile(file, 'utf8')) : await fetchReleases();
  const errors = [];
  const mac = [];
  const windows = [];
  for (const r of raw) {
    if (r.draft || r.prerelease) continue;
    try {
      if (r.tag_name.startsWith('win-v')) windows.push(parseWindowsRelease(r));
      else mac.push(parseRelease(r));
    } catch (e) {
      errors.push(e.message);
    }
  }
  mac.sort((a, b) => compareVersions(b.version, a.version));
  windows.sort((a, b) => compareVersions(b.version, a.version));
  if (mac.length === 0 && errors.length === 0) errors.push('沒有任何正式發布的版本');
  if (mac[0] && !mac[0].dmg) {
    errors.push(`${mac[0].tag}：最新版沒有 Shiyin-${mac[0].version}.dmg，下載按鈕會壞掉`);
  }
  if (windows[0] && (!windows[0].x64 || !windows[0].arm64)) {
    errors.push(`${windows[0].tag}：最新的 Windows 版要有 Shiyin-${windows[0].version}-win-x64-Setup.exe 和 -win-arm64-Setup.exe`);
  }
  if (errors.length) throw new Error(`版本說明有問題：\n- ${errors.join('\n- ')}`);
  return { mac, windows };
}

/** Windows 版：tag 是 win-vX.Y.Z，標題是「拾音 Windows X.Y.Z」「Shiyin for Windows X.Y.Z」 */
function parseWindowsRelease(r) {
  const tag = r.tag_name;
  const version = tag.replace(/^win-v/, '');
  if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error(`${tag}：Windows 版的 tag 要是 win-vX.Y.Z`);
  const blocks = splitBlocks(r.body);
  if (blocks.length !== 2) {
    throw new Error(`${tag}：說明要是中文一段、空一行、英文一段，現在有 ${blocks.length} 段`);
  }
  const asset = (arch) => {
    const a = (r.assets || []).find((x) => x.name === `Shiyin-${version}-win-${arch}-Setup.exe`);
    return a ? { name: a.name, url: a.browser_download_url, downloads: a.download_count ?? 0 } : null;
  };
  const x64 = asset('x64');
  const arm64 = asset('arm64');
  return {
    version,
    tag,
    date: r.published_at,
    url: r.html_url,
    x64,
    arm64,
    downloads: (x64?.downloads ?? 0) + (arm64?.downloads ?? 0),
    zh: parseBlock(blocks[0], '拾音 Windows', version, tag),
    en: parseBlock(blocks[1], 'Shiyin for Windows', version, tag),
  };
}

function splitBlocks(body) {
  return (body || '')
    .replace(/\r\n?/g, '\n')
    .split(/\n[ \t]*\n/)
    .map((b) => b.trim())
    .filter(Boolean);
}

function apiHeaders() {
  const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'shiyin-site-build' };
  if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  return headers;
}

/**
 * GitHub 星星數（docs/website.md §11）。讀不到就回傳 null，不讓建置失敗；
 * file 有給就讀這份 JSON（跟 API 同樣格式），offline 為 true（用 --releases 測試）而且沒給 file 時不連網路
 */
export async function loadStars({ file, offline = false } = {}) {
  try {
    if (file) return JSON.parse(await readFile(file, 'utf8')).stargazers_count ?? null;
    if (offline) return null;
    const res = await fetch(`https://api.github.com/repos/${REPO}`, { headers: apiHeaders() });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return (await res.json()).stargazers_count ?? null;
  } catch (e) {
    console.warn(`讀不到 GitHub 星星數（${e.message}），首頁只顯示下載次數`);
    return null;
  }
}

async function fetchReleases() {
  const headers = apiHeaders();
  const all = [];
  for (let page = 1; ; page++) {
    const res = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=100&page=${page}`, { headers });
    if (!res.ok) throw new Error(`讀取 GitHub Releases 失敗：HTTP ${res.status} ${await res.text()}`);
    const batch = await res.json();
    all.push(...batch);
    if (batch.length < 100) return all;
  }
}

function parseRelease(r) {
  const tag = r.tag_name;
  const version = tag.replace(/^v/, '');
  if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error(`${tag}：tag 要是 vX.Y.Z`);
  const blocks = splitBlocks(r.body);
  if (blocks.length !== 2) {
    throw new Error(`${tag}：說明要是中文一段、空一行、英文一段，現在有 ${blocks.length} 段`);
  }
  const dmg = (r.assets || []).find((a) => a.name === `Shiyin-${version}.dmg`);
  return {
    version,
    tag,
    date: r.published_at,
    url: r.html_url,
    dmg: dmg ? { name: dmg.name, url: dmg.browser_download_url } : null,
    downloads: dmg?.download_count ?? 0,
    zh: parseBlock(blocks[0], '拾音', version, tag),
    en: parseBlock(blocks[1], 'Shiyin', version, tag),
  };
}

// 第一行「拾音 3.1.0」或「拾音 3.1.0：副標」（英文「Shiyin 3.1.0: subtitle」），後面每行「- 一條」
function parseBlock(text, name, version, tag) {
  const [head, ...lines] = text.split('\n').map((l) => l.trim());
  const m = head.match(/^(\S+(?: Windows| for Windows)?) (\d+\.\d+\.\d+)(?:\s*[：:]\s*(.+))?$/);
  if (!m || m[1] !== name) {
    throw new Error(`${tag}：第一行要是「${name} ${version}」（可以加「：副標」），現在是「${head}」`);
  }
  if (m[2] !== version) throw new Error(`${tag}：標題的版本 ${m[2]} 跟 tag 不一致`);
  const items = lines.map((line) => {
    const item = line.match(/^- (.+)$/);
    if (!item) throw new Error(`${tag}：「${line}」要用「- 」開頭`);
    return item[1];
  });
  if (items.length === 0) throw new Error(`${tag}：「${head}」底下沒有任何一條`);
  return { subtitle: m[3] || '', items };
}

function compareVersions(a, b) {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < 3; i++) if (pa[i] !== pb[i]) return pa[i] - pb[i];
  return 0;
}
