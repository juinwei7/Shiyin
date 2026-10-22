// 用 Windows 的訪客（docs/website.md §16.2）：打開首頁上預設藏起來的「Windows 版在這裡」。沒有 JavaScript 時不出現
if (/Windows/.test(navigator.userAgent)) {
  for (const el of document.querySelectorAll('[data-on-windows]')) el.hidden = false;
}
