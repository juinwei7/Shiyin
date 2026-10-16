// 讀 GitHub Releases、檢查版本說明的格式（CLAUDE.md「版本說明」、docs/website/design.md §7）。
// 格式不對就丟錯，讓建置失敗，不會部署壞掉的頁面。

import { readFile } from 'node:fs/promises';

const REPO = 'juinwei7/Shiyin';

/** 回傳正式發布的版本，版本號由新到舊；file 有給就讀這份 JSON（跟 API 同樣格式），不連網路 */
export async function loadReleases({ file } = {}) {
  const raw = file ? JSON.parse(await readFile(file, 'utf8')) : await fetchReleases();
  const errors = [];
  const releases = [];
  for (const r of raw) {
    if (r.draft || r.prerelease) continue;
    try {
      releases.push(parseRelease(r));
    } catch (e) {
      errors.push(e.message);
    }
  }
  releases.sort((a, b) => compareVersions(b.version, a.version));
  if (releases.length === 0 && errors.length === 0) errors.push('沒有任何正式發布的版本');
  if (releases[0] && !releases[0].dmg) {
    errors.push(`${releases[0].tag}：最新版沒有 Shiyin-${releases[0].version}.dmg，下載按鈕會壞掉`);
  }
  if (errors.length) throw new Error(`版本說明有問題：\n- ${errors.join('\n- ')}`);
  return releases;
}

async function fetchReleases() {
  const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'shiyin-site-build' };
  if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
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
  const blocks = (r.body || '')
    .replace(/\r\n?/g, '\n')
    .split(/\n[ \t]*\n/)
    .map((b) => b.trim())
    .filter(Boolean);
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
    zh: parseBlock(blocks[0], '拾音', version, tag),
    en: parseBlock(blocks[1], 'Shiyin', version, tag),
  };
}

// 第一行「拾音 3.1.0」或「拾音 3.1.0：副標」（英文「Shiyin 3.1.0: subtitle」），後面每行「- 一條」
function parseBlock(text, name, version, tag) {
  const [head, ...lines] = text.split('\n').map((l) => l.trim());
  const m = head.match(/^(\S+) (\d+\.\d+\.\d+)(?:\s*[：:]\s*(.+))?$/);
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
