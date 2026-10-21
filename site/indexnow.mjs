// 部署後把 sitemap 裡的網址送給 IndexNow（Bing 等搜尋引擎，docs/website.md §14.1）。
// 先跑過 build.mjs。送失敗只印警告，不讓部署失敗。
//
// 環境變數：SITE_URL（預設 https://shiyin.weibuilds.com）

import { readFile } from 'node:fs/promises';
import { INDEXNOW_KEY } from './src/seo.mjs';

const site = (process.env.SITE_URL || 'https://shiyin.weibuilds.com').replace(/\/+$/, '');
const sitemap = await readFile(new URL('./dist/sitemap.xml', import.meta.url), 'utf8');
const urlList = [...sitemap.matchAll(/<loc>([^<]+)<\/loc>/g)].map((m) => m[1]);

try {
  const res = await fetch('https://api.indexnow.org/indexnow', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=utf-8' },
    body: JSON.stringify({ host: new URL(site).host, key: INDEXNOW_KEY, keyLocation: `${site}/${INDEXNOW_KEY}.txt`, urlList }),
  });
  console.log(`IndexNow：送出 ${urlList.length} 個網址，回應 ${res.status}`);
  if (res.status !== 200 && res.status !== 202) console.log(`::warning::IndexNow 回應 ${res.status}：${await res.text()}`);
} catch (e) {
  console.log(`::warning::IndexNow 送不出去：${e.message}`);
}
