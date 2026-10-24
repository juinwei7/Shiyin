// 首屏示範（docs/website.md §10）：照劇本播一場會議，從錄音、草稿、正式版（說話者名字）到待確認、提問。
// 文字都在頁面的 #demo-data（site/src/demo-content.mjs 產生），這裡只管播放。
//
// 每一章有 play（播動畫）和 jump（直接變成這一章結束的樣子）。跳到第 n 章＝重設畫面、依序 jump 前面幾章，再從第 n 章播。
// 所有等待都走同一個可以暫停的時鐘，暫停時整段劇本一起停。
// 檢查用：網址加 ?ch=n 直接顯示第 n 章結束的樣子（n = 5 是播完、可以自己點）。
(() => {
  const demo = document.getElementById('demo');
  if (!demo) return;
  const D = JSON.parse(document.getElementById('demo-data').textContent);
  const frame = demo.querySelector('.demo-frame');
  const panBox = demo.querySelector('.demo-pan');
  const stage = demo.querySelector('.stage');
  const cap = demo.querySelector('.demo-cap');
  const playBtn = demo.querySelector('.d-play');
  const chBtns = [...demo.querySelectorAll('.chapters button')];
  const initial = stage.innerHTML;
  const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
  const LENGTH = D.length;
  const RATE = 260; // 錄音時，會議裡的一秒＝幾毫秒

  // ---- 縮放；手機上放大到看得清楚，畫面跟著游標左右移動 ----
  const PAN_K = 0.72;
  let k = 1;
  let focusX = 0;
  function fit() {
    const avail = demo.clientWidth - 40;
    const pan = avail / 960 < PAN_K;
    demo.classList.toggle('pan', pan);
    k = pan ? PAN_K : avail / 960;
    stage.style.setProperty('--k', k);
    panBox.style.width = `${960 * k}px`;
    panBox.style.height = `${640 * k}px`;
    frame.style.height = pan ? `${640 * k}px` : '';
    if (pan) camera(focusX, false);
    else panBox.style.transform = '';
  }
  function camera(x, animate = true) {
    focusX = x;
    if (!demo.classList.contains('pan')) return;
    const fw = frame.clientWidth;
    const tx = Math.max(fw - 960 * k, Math.min(0, fw / 2 - x * k));
    if (demo.classList.contains('swipe')) { frame.scrollTo({ left: -tx, behavior: animate ? 'smooth' : 'auto' }); return; }
    panBox.style.transition = animate ? '' : 'none';
    panBox.style.transform = `translateX(${tx}px)`;
  }
  new ResizeObserver(fit).observe(demo);
  fit();

  // ---- 可以暫停的時鐘 ----
  const ABORT = new Error('abort');
  const clock = { t: 0, last: 0, paused: false, waiters: [] };
  function onFrame(now) {
    if (clock.last && !clock.paused) clock.t += Math.min(now - clock.last, 64);
    clock.last = now;
    if (clock.waiters.length) {
      const due = clock.waiters.filter((w) => w.run.dead || clock.t >= w.at);
      if (due.length) {
        clock.waiters = clock.waiters.filter((w) => !due.includes(w));
        for (const w of due) (w.run.dead ? w.reject(ABORT) : w.resolve());
      }
    }
    progress();
    requestAnimationFrame(onFrame);
  }
  requestAnimationFrame(onFrame);

  const ctx = (run) => ({
    wait: (ms) => (run.dead ? Promise.reject(ABORT)
      : ms <= 0 ? Promise.resolve()
        : new Promise((resolve, reject) => clock.waiters.push({ at: clock.t + ms, resolve, reject, run }))),
  });
  const quiet = (p) => p.catch((e) => { if (e !== ABORT) console.error(e); });
  async function until(r, cond) { while (!cond()) await r.wait(40); }

  // ---- 畫面上的東西 ----
  let win;
  const $ = (key) => stage.querySelector(`[data-k="${key}"]`);
  const pad = (s) => `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
  const secs = (text) => { const [m, s] = text.split(':').map(Number); return m * 60 + s; };
  const fill = (text, values) => text.replace(/\{(\w)\}/g, (_, key) => values[key]);
  const T = D.text;
  const M = D.meeting;

  function setMode(mode) { win.dataset.mode = mode; }
  function setTab(name) { win.querySelectorAll('.sg').forEach((s) => s.classList.toggle('on', s.dataset.tab === name)); }
  function showPane(name) {
    let shown;
    win.querySelectorAll('.pane').forEach((p) => { const on = p.classList.contains(`p-${name}`); p.classList.toggle('on', on); if (on) shown = p; });
    return shown;
  }
  const tabEl = (name) => win.querySelector(`.sg[data-tab="${name}"]`);
  // 紀錄頂部「還有 N 題待確認」（app 的 ReviewBanner）：展開／收起
  function openReview(on) { $('rv').classList.toggle('open', on); $('rvbt').textContent = on ? T.collapse : T.expand; }
  function rowStatus(text, cls = '') { const el = $('rowst'); el.textContent = text; el.className = cls; }
  function banner(kind, text, value) {
    const b = $('banner');
    if (!kind) { b.classList.remove('on'); return; }
    b.classList.add('on');
    b.classList.toggle('prog', kind === 'prog');
    $('bantext').textContent = text;
    if (value != null) $('banbar').firstElementChild.style.width = `${value}%`;
  }
  // 標題下面的提示列（草稿、背景工作、猜到的說話者）
  function hint(key, on) { $(key).classList.toggle('on', on); }
  // 轉錄進度：提示列和側邊欄一起動
  const tx = { p: 0 };
  function showTx(p) {
    tx.p = p;
    banner('prog', fill(T.banner.transcribing, { p }) + (p >= 10 ? T.banner.almost : ''), p);
    rowStatus(fill(T.status.transcribing, { p }));
    $('rowbar').firstElementChild.style.width = `${p}%`;
  }
  async function transcribeTo(r, to, ms) {
    while (tx.p < to) { showTx(Math.min(to, tx.p + 2)); await r.wait(ms); }
  }
  // 「查看」的彈出視窗：在按鈕下面，箭頭對準按鈕；右邊超出視窗時往左移（跟 macOS 的 popover 一樣）
  function popover(on) {
    const pop = $('popv');
    if (on) {
      const main = win.querySelector('.main');
      const mr = main.getBoundingClientRect();
      const br = $('suggestbtn').getBoundingClientRect();
      const center = (br.left - mr.left + br.width / 2) / k;
      const left = Math.max(12, Math.min(main.clientWidth - pop.offsetWidth - 12, center - pop.offsetWidth / 2));
      pop.style.left = `${left}px`;
      pop.style.top = `${(br.bottom - mr.top) / k + 10}px`;
      pop.style.setProperty('--ax', `${center - left}px`);
    }
    pop.classList.toggle('on', on);
  }
  // 一段一段出現（會議紀錄、換成正式版）
  async function reveal(r, pane, ms) {
    pane.classList.add('hidden-in');
    for (const el of pane.querySelectorAll('.in')) {
      el.classList.add('shown');
      await r.wait(ms);
    }
    pane.classList.remove('hidden-in');
    pane.querySelectorAll('.shown').forEach((el) => el.classList.remove('shown'));
  }
  function toast(text) {
    const t = $('toast');
    t.textContent = text;
    t.classList.add('on');
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => t.classList.remove('on'), 2400);
  }
  // 換成會議標題：標題的字形跟著會議語言
  function setTitle(text, lang) {
    for (const key of ['mtitle', 'rowtitle', 'wintitle']) {
      const el = $(key);
      el.textContent = text;
      if (lang) el.lang = lang; else el.removeAttribute('lang');
    }
  }

  const pl = { t: 0, playing: false, timer: 0 };
  function setPlayer(t) {
    pl.t = Math.max(0, Math.min(LENGTH, t));
    $('track').style.setProperty('--p', `${(pl.t / LENGTH) * 100}%`);
    $('pltime').textContent = pad(pl.t);
    const rows = [...$('tx').children];
    const current = rows.filter((row) => Number(row.dataset.at) <= pl.t).pop();
    rows.forEach((row) => row.classList.toggle('now', pl.t > 0 && row === current));
  }
  function setPlaying(on) { pl.playing = on; $('plbtn').classList.toggle('playing', on); }
  // 資訊列加上說話者後放不下時，範本、語言選單改到下一行
  function fitHeader() {
    const head = win.querySelector('.head');
    head.classList.remove('wrap');
    const room = head.clientWidth - 64;
    if (head.querySelector('.meta').offsetWidth + 12 + head.querySelector('.pickers').offsetWidth > room) head.classList.add('wrap');
  }

  // ---- 游標 ----
  function cursorTo(x, y, ms) {
    const c = $('cursor');
    c.style.setProperty('--dur', `${ms}ms`);
    c.style.transform = `translate(${x}px, ${y}px)`;
  }
  function pointOf(el, ox = 0.5, oy = 0.5) {
    const sr = stage.getBoundingClientRect();
    const er = el.getBoundingClientRect();
    return { x: (er.left - sr.left + er.width * ox) / k, y: (er.top - sr.top + er.height * oy) / k };
  }
  async function move(r, el, { dur = 750, ox = 0.5, oy = 0.5 } = {}) {
    const p = pointOf(el, ox, oy);
    $('cursor').classList.add('on');
    cursorTo(p.x, p.y, dur);
    camera(p.x);
    await r.wait(dur + 60);
  }
  async function click(r, el) {
    const c = $('cursor');
    c.classList.add('down');
    el.classList.add('pressed');
    await r.wait(140);
    c.classList.remove('down');
    el.classList.remove('pressed');
  }
  async function type(r, el, text, ms) {
    for (const ch of text) {
      el.textContent += ch;
      await r.wait(ms * (0.6 + Math.random() * 0.8));
    }
  }
  // 摘要分頁放不下時（英文比較長），捲到看得到這一格
  function scrollInto(el) {
    const pane = el.closest('.pane');
    const over = (el.getBoundingClientRect().bottom - pane.getBoundingClientRect().bottom) / k + 24;
    if (over > 0) pane.scrollTo({ top: pane.scrollTop + over, behavior: reduce ? 'auto' : 'smooth' });
  }

  // ---- 錄音：即時逐字稿、音量表 ----
  const STAR = '<svg viewBox="0 0 16 16"><path class="f" d="m8 1.9 1.85 3.85 4.2.55-3.07 2.92.78 4.16L8 11.35 4.24 13.38l.78-4.16L1.95 6.3l4.2-.55z"/></svg>';
  function liveRow() {
    const tx = $('livetx');
    tx.querySelector('.live-hint')?.remove();
    const row = document.createElement('div');
    row.className = 'lrow';
    row.innerHTML = `<span class="t v">…</span><p><span class="fin"></span><span class="vol"></span></p><span class="star">${STAR}</span>`;
    tx.append(row);
    return { t: row.querySelector('.t'), fin: row.querySelector('.fin'), vol: row.querySelector('.vol'), star: row.querySelector('.star') };
  }
  function setTimes(s) { $('tbtime').textContent = pad(s); $('rbtime').textContent = pad(s); }

  async function vuLoop(r, st) {
    const meters = { mic: [...$('vu1').children], sys: [...$('vu2').children] };
    const level = { mic: 0, sys: 0 };
    while (st.on) {
      for (const src of ['mic', 'sys']) {
        const target = st.speaking === src ? 0.45 + Math.random() * 0.5 : 0.04 + Math.random() * 0.08;
        level[src] += (target - level[src]) * 0.55;
        meters[src].forEach((seg, i) => {
          const lit = i < level[src] * 14;
          seg.classList.toggle('on', lit);
          seg.classList.toggle('hi', lit && i >= 11);
        });
      }
      await r.wait(90);
    }
    Object.values(meters).flat().forEach((seg) => seg.classList.remove('on', 'hi'));
  }

  function submitLabel() {
    const answered = $('ans1v').textContent.trim() !== '';
    const skipped = $('skip2').classList.contains('on');
    const b = $('submit');
    b.textContent = answered && skipped ? T.submit.both : answered ? T.submit.answered : skipped ? T.submit.skipped : T.submit.none;
    b.classList.toggle('dis', !answered && !skipped);
  }

  const CH = [
    { // 錄音
      dur: 15500,
      async play(r) {
        cursorTo(600, 470, 0);
        camera(128, false);
        await r.wait(400);
        await move(r, $('recbtn'), { dur: 1000 });
        await click(r, $('recbtn'));
        setMode('rec');
        $('wintitle').textContent = T.recTitle;
        camera(470);
        const st = { sec: 0, speaking: null, on: true, txDone: false, done: false };
        const vu = quiet(vuLoop(r, st));
        const timer = (async () => {
          for (let s = 0; ; s++) {
            st.sec = s;
            setTimes(s);
            if (s >= 50 && st.txDone) break;
            await r.wait(RATE);
          }
          st.done = true;
        })();
        let starRow = null;
        const stream = (async () => {
          for (const [i, line] of M.live.entries()) {
            await until(r, () => st.sec >= line.at);
            st.speaking = line.who;
            const row = liveRow();
            if (i === 2) starRow = row;
            for (const [guess, final] of line.chunks) {
              for (const tok of guess.match(/[A-Za-z’']+|./gu)) {
                row.vol.textContent += tok;
                await r.wait(tok.length > 1 ? D.speed.word : D.speed.char);
              }
              await r.wait(170);
              row.fin.textContent += final;
              row.vol.textContent = '';
              row.t.textContent = pad(line.at);
              row.t.classList.remove('v');
            }
            st.speaking = null;
          }
          st.txDone = true;
        })();
        const user = (async () => {
          await until(r, () => st.sec >= 3);
          const box = $('nbox');
          await move(r, box, { dur: 900, ox: 0.12, oy: 0.06 });
          await click(r, box);
          box.classList.add('focus');
          $('ntext').textContent = `[${pad(st.sec)}] `;
          await type(r, $('ntext'), M.note, 95);
          await until(r, () => st.sec >= 27);
          box.classList.remove('focus');
          await move(r, $('markbtn'), { dur: 800 });
          await click(r, $('markbtn'));
          starRow?.star.classList.add('on');
          toast(fill(T.bookmarked, { t: pad(st.sec) }));
          await until(r, () => st.sec >= 47);
          await move(r, $('stopbtn'), { dur: 800 });
          await until(r, () => st.done);
          await r.wait(200);
          await click(r, $('stopbtn'));
        })();
        await Promise.all([timer, stream, user]);
        st.on = false;
        await vu;
        $('notes').textContent = $('ntext').textContent;
      },
      jump() {},
    },
    { // 草稿：停止後馬上用即時逐字稿整理，正式轉錄同時在跑
      dur: 4400,
      async play(r) {
        this.stopped();
        camera(520);
        banner('spin', T.banner.preparing);
        rowStatus(fill(T.status.transcribing, { p: 0 }));
        await r.wait(700);
        $('rowbar').classList.add('on');
        showTx(0);
        const bar = quiet(transcribeTo(r, 24, 150));
        await r.wait(800);
        $('mtitle').classList.add('out');
        await r.wait(260);
        this.drafted();
        $('mtitle').classList.remove('out');
        camera(560);
        await reveal(r, showPane('summary'), 105);
        await bar;
        await r.wait(1100);
      },
      stopped() {
        setMode('busy');
        $('wintitle').textContent = T.recTitle;
        setTab('summary');
        showPane('wait');
        $('toast').classList.remove('on');
        $('cursor').classList.remove('on');
      },
      drafted() {
        win.classList.add('drafted');
        setTitle(M.title, D.meetingLang !== win.lang ? D.meetingLang : '');
        hint('draft', true);
        $('revlabel').textContent = T.reviewTab;
        setTab('summary');
        showPane('summary');
        setPlayer(0);
      },
      jump() {
        this.stopped();
        this.drafted();
        $('rowbar').classList.add('on');
        showTx(24);
      },
    },
    { // 正式版：轉錄完帶入認得的名字，整理完換掉草稿，再猜其他人的名字
      dur: 12600,
      async play(r) {
        await transcribeTo(r, 100, 32);
        $('rowbar').classList.remove('on');
        banner('spin', T.banner.identifying);
        rowStatus(T.status.identifying);
        await r.wait(1000);
        this.speakers();
        banner('spin', T.banner.summarizing);
        rowStatus(T.status.summarizing);
        await r.wait(1700);
        banner(null);
        hint('draft', false);
        this.final();
        await reveal(r, showPane('summary'), 80);
        await r.wait(400);
        hint('helper', true);
        await r.wait(1400);
        hint('helper', false);
        hint('suggest', true);
        await r.wait(600);
        const open = $('suggestbtn');
        await move(r, open, { dur: 900 });
        await click(r, open);
        popover(true);
        await r.wait(1900);
        const apply = $('applybtn');
        await move(r, apply, { dur: 700 });
        await click(r, apply);
        this.applied();
        await r.wait(1100);
      },
      speakers() {
        win.classList.add('named');
        fitHeader();
      },
      final() {
        setMode('done');
        rowStatus(T.status.review);
      },
      applied() {
        popover(false);
        hint('suggest', false);
        stage.querySelectorAll(`[data-spk="${M.guess.speaker}"]`).forEach((el) => { el.textContent = M.guess.name; });
        fitHeader();
      },
      jump() {
        banner(null);
        $('rowbar').classList.remove('on');
        hint('draft', false);
        this.speakers();
        this.final();
        this.applied();
        setTab('summary');
        showPane('summary');
        setPlayer(0);
      },
    },
    { // 待確認
      dur: 12500,
      async play(r) {
        setTab('summary');
        showPane('summary').scrollTo({ top: 0 });
        await move(r, $('rvb'), { dur: 800 });
        await click(r, $('rvb'));
        openReview(true);
        await r.wait(1000);
        const field = $('ans1');
        await move(r, field, { dur: 800, ox: 0.25 });
        await click(r, field);
        field.classList.add('focus');
        await r.wait(250);
        field.classList.add('typed');
        await type(r, $('ans1v'), M.answer, 130);
        submitLabel();
        await r.wait(500);
        await move(r, $('skip2'), { dur: 700, ox: 0.15 });
        field.classList.remove('focus');
        await click(r, $('skip2'));
        $('skip2').classList.add('on');
        $('q2').classList.add('skipped');
        submitLabel();
        await r.wait(700);
        await move(r, $('submit'), { dur: 800 });
        await click(r, $('submit'));
        banner('spin', T.banner.revising);
        rowStatus(T.status.summarizing);
        await r.wait(1600);
        banner(null);
        this.reviewed();
        await r.wait(900);
        scrollInto($('due1'));
        camera(560);
        await r.wait(400);
        this.fixDue(true);
        await r.wait(1400);
      },
      reviewed() {
        // 送出後題目歸零：條消失（app 的 ReviewBanner 在 reviewCount 0 時不顯示）
        $('rvb').classList.add('gone');
        $('rv').classList.remove('open');
        $('revlabel').textContent = T.reviewLabel;
        rowStatus('');
        win.querySelectorAll('.marks i').forEach((m) => m.remove());
      },
      fixDue(flash) {
        for (const [el, text] of [[$('due0'), M.due.after], [$('due1'), M.dueAfter]]) {
          el.textContent = text;
          if (flash) { el.classList.add('flash'); void el.offsetWidth; requestAnimationFrame(() => requestAnimationFrame(() => el.classList.remove('flash'))); }
        }
      },
      jump() {
        $('ans1').classList.add('typed');
        $('ans1v').textContent = M.answer;
        $('skip2').classList.add('on');
        $('q2').classList.add('skipped');
        submitLabel();
        this.reviewed();
        setTab('summary');
        showPane('summary');
        this.fixDue(false);
      },
    },
    { // 提問：回答附時間點，點了從那裡播，逐字稿標出正在播的那段
      dur: 13600,
      async play(r) {
        await move(r, tabEl('ask'), { dur: 800 });
        await click(r, tabEl('ask'));
        setTab('ask');
        showPane('ask');
        await r.wait(900);
        const field = $('askfield');
        await move(r, field, { dur: 800, ox: 0.2 });
        await click(r, field);
        field.classList.add('focus');
        await r.wait(250);
        field.classList.add('typed');
        await type(r, $('askval'), M.question, D.meetingLang === 'en' ? 50 : 105);
        $('askbtn').classList.remove('dis');
        await r.wait(400);
        await move(r, $('askbtn'), { dur: 600 });
        await click(r, $('askbtn'));
        $('askval').textContent = '';
        field.classList.remove('typed', 'focus');
        $('askbtn').classList.add('dis');
        $('ask').classList.add('busy');
        camera(480);
        await r.wait(1700);
        this.answered();
        await r.wait(1300);
        const cite = $('cite1');
        await move(r, cite, { dur: 800, ox: 0.4 });
        cite.classList.add('hover');
        await r.wait(250);
        await click(r, cite);
        cite.classList.remove('hover');
        setPlayer(secs(cite.textContent));
        setPlaying(true);
        const playing = quiet((async () => {
          while (pl.playing) { await r.wait(100); if (pl.playing) setPlayer(pl.t + 0.1); }
        })());
        await r.wait(1200);
        await move(r, tabEl('transcript'), { dur: 700 });
        await click(r, tabEl('transcript'));
        setTab('transcript');
        showPane('transcript');
        await r.wait(2200);
        await move(r, $('plbtn'), { dur: 900 });
        await click(r, $('plbtn'));
        setPlaying(false);
        await playing;
        $('cursor').classList.remove('on');
      },
      answered() {
        $('ask').classList.remove('busy');
        $('ask').classList.add('answered');
      },
      jump() {
        this.answered();
        setTab('transcript');
        showPane('transcript');
        setPlayer(14);
        setPlaying(false);
      },
    },
  ];

  // ---- 播放控制 ----
  let run = null;
  let chapter = -1;
  let chStart = 0;

  function reset() {
    stopFree();
    stage.innerHTML = initial;
    win = stage.querySelector('.win');
    demo.classList.remove('swipe');
    frame.scrollLeft = 0;
    camera(128, false);
  }

  function setChapter(i) {
    chapter = i;
    chStart = clock.t;
    chBtns.forEach((b, j) => {
      b.classList.toggle('on', j === i);
      b.classList.toggle('past', j < i);
      b.style.setProperty('--fill', j < i ? '100%' : '0%');
      if (j === i) b.setAttribute('aria-current', 'step'); else b.removeAttribute('aria-current');
    });
    const text = i < 0 ? '' : i < CH.length ? D.captions[i] : D.endCaption;
    if (cap.textContent !== text) {
      clearTimeout(setChapter.timer);
      cap.classList.add('swap');
      setChapter.timer = setTimeout(() => { cap.textContent = text; cap.classList.remove('swap'); }, cap.textContent ? 250 : 0);
    }
    demo.classList.toggle('ended', i >= CH.length);
    playBtn.setAttribute('aria-label', i >= CH.length ? D.controls.replay : D.controls.pause);
  }

  function progress() {
    if (chapter < 0 || chapter >= CH.length || !run || run.dead) return;
    const f = Math.min(0.97, (clock.t - chStart) / CH[chapter].dur);
    chBtns[chapter].style.setProperty('--fill', `${f * 100}%`);
  }

  // 從第 i 章開始播；animate = false 時直接顯示第 i 章結束的樣子（i = CH.length 是播完）
  async function runFrom(i, { animate = !reduce } = {}) {
    if (run) run.dead = true;
    const me = { dead: false };
    run = me;
    pause(false);
    reset();
    stage.classList.add('instant');
    const last = animate ? i - 1 : Math.min(i, CH.length - 1);
    for (let j = 0; j <= last; j++) CH[j].jump();
    void stage.offsetWidth;
    stage.classList.remove('instant');
    if (!animate) {
      if (last >= 1) camera(560, false);
      setChapter(i >= CH.length ? CH.length : i);
      chBtns.forEach((b, j) => b.style.setProperty('--fill', j <= last ? '100%' : '0%'));
      enableFree();
      return;
    }
    try {
      for (let j = i; j < CH.length; j++) {
        setChapter(j);
        await CH[j].play(ctx(me));
      }
      setChapter(CH.length);
      chBtns.forEach((b) => b.style.setProperty('--fill', '100%'));
      enableFree();
    } catch (e) {
      if (e !== ABORT) console.error(e);
    }
  }

  function pause(on) {
    clock.paused = on;
    demo.classList.toggle('paused', on);
    stage.classList.toggle('paused', on);
    if (!demo.classList.contains('ended')) playBtn.setAttribute('aria-label', on ? D.controls.resume : D.controls.pause);
  }

  // ---- 播完以後可以自己點 ----
  function enableFree() {
    win.classList.add('free');
    $('cursor').classList.remove('on');
    if (demo.classList.contains('pan')) {
      // 手機：改成可以自己左右滑
      const x = focusX;
      demo.classList.add('swipe');
      panBox.style.transform = '';
      camera(x, false);
    }
  }
  function stopFree() { clearInterval(pl.timer); pl.timer = 0; }
  function freePlay(on) {
    stopFree();
    setPlaying(on);
    if (!on) return;
    if (pl.t >= LENGTH) setPlayer(0);
    pl.timer = setInterval(() => {
      setPlayer(pl.t + 0.1);
      if (pl.t >= LENGTH) freePlay(false);
    }, 100);
  }

  stage.addEventListener('click', (e) => {
    if (!win.classList.contains('free')) {
      // 示範播到一半點了視窗：直接跳到最後，換使用者自己點
      runFrom(CH.length, { animate: false });
      return;
    }
    const sg = e.target.closest('.sg');
    if (sg) { setTab(sg.dataset.tab); showPane(sg.dataset.tab); return; }
    const ts = e.target.closest('a.ts');
    if (ts) { setPlayer(secs(ts.textContent)); freePlay(true); return; }
    if (e.target.closest('.pl-btn')) { freePlay(!pl.playing); return; }
    const track = e.target.closest('.track');
    if (track) {
      const rect = track.getBoundingClientRect();
      setPlayer(((e.clientX - rect.left) / rect.width) * LENGTH);
      freePlay(true);
    }
  });

  let started = false;
  let userPaused = false;
  playBtn.addEventListener('click', () => {
    started = true;
    if (demo.classList.contains('ended')) { userPaused = false; runFrom(0); return; }
    if (chapter < 0) { runFrom(0); return; }
    pause(!clock.paused);
    userPaused = clock.paused;
  });
  chBtns.forEach((b, i) => b.addEventListener('click', () => { started = true; userPaused = false; runFrom(i); }));

  // 跟 app 一樣按 ⌃⌥⌘R 開始錄音
  addEventListener('keydown', (e) => {
    if (e.code === 'KeyR' && e.ctrlKey && e.altKey && e.metaKey) {
      e.preventDefault();
      started = true;
      userPaused = false;
      runFrom(0);
    }
  });

  // 捲到看得到才開始播；捲走就暫停，捲回來接著播（使用者自己按的暫停不算）
  reset();
  setChapter(-1);
  const only = new URLSearchParams(location.search).get('ch');
  if (only != null) {
    started = true;
    runFrom(Math.max(0, Math.min(CH.length, Number(only) || 0)), { animate: false });
  } else if (reduce) {
    // 減少動態效果：不播動畫，直接顯示播完的樣子；章節按鈕切換每一章結束的畫面
    started = true;
    playBtn.hidden = true;
    runFrom(CH.length, { animate: false });
  }
  new IntersectionObserver(([entry]) => {
    if (entry.isIntersecting) {
      if (!started) { started = true; runFrom(0); } else if (!userPaused && !demo.classList.contains('ended')) pause(false);
    } else if (started && !demo.classList.contains('ended') && chapter >= 0) {
      pause(true);
    }
  }, { threshold: 0.35 }).observe(frame);
})();
