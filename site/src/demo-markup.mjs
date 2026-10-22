// 首屏示範的 HTML（docs/website.md §10）：用 HTML 重刻拾音的視窗，尺寸照 app 截圖量的 pt（視窗 960×640）。
// 這裡產生的是「還沒開始錄音」的畫面；劇本由 src/demo.js 照 JSON 資料播。沒有 JavaScript 時改顯示截圖。

import { demoContent } from './demo-content.mjs';

const esc = (text) => String(text).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

// 圖示照 SF Symbols 的樣子畫
const I = {
  record: '<svg viewBox="0 0 16 16"><circle cx="8" cy="8" r="6.4"/><circle cx="8" cy="8" r="2.7" class="f"/></svg>',
  chevron: '<svg viewBox="0 0 16 16"><path d="M4.5 6.5 8 10l3.5-3.5"/></svg>',
  // 問所有會議（questionmark.bubble）；匯入在側邊欄最下面（app 的 design §22.2）
  askAll: '<svg viewBox="0 0 16 16"><path d="M3.4 2.4h9.2a1.6 1.6 0 0 1 1.6 1.6v6.2a1.6 1.6 0 0 1-1.6 1.6H7.4L4.6 14v-2.2H3.4a1.6 1.6 0 0 1-1.6-1.6V4a1.6 1.6 0 0 1 1.6-1.6z"/><path d="M6.5 5.5a1.5 1.5 0 1 1 2.2 1.3c-.5.3-.7.6-.7 1.1v.2"/><circle cx="8" cy="9.6" r=".6" class="f"/></svg>',
  stopSquare: '<svg viewBox="0 0 16 16"><rect x="3.4" y="3.4" width="9.2" height="9.2" rx="1.6" class="f"/></svg>',
  sidebar: '<svg viewBox="0 0 16 16"><rect x="1.6" y="2.6" width="12.8" height="10.8" rx="2.4"/><path d="M6 2.6v10.8M3.4 5.2h1M3.4 7.2h1M3.4 9.2h1"/></svg>',
  search: '<svg viewBox="0 0 16 16"><circle cx="7" cy="7" r="4.6"/><path d="m10.4 10.4 3.4 3.4"/></svg>',
  importFile: '<svg viewBox="0 0 16 16"><path d="M8 1.8v8.4M5.2 7.6 8 10.4l2.8-2.8"/><path d="M5.3 5H4.4A1.6 1.6 0 0 0 2.8 6.6v5.8A1.6 1.6 0 0 0 4.4 14h7.2a1.6 1.6 0 0 0 1.6-1.6V6.6A1.6 1.6 0 0 0 11.6 5h-.9"/></svg>',
  trash: '<svg viewBox="0 0 16 16"><path d="M2.8 4.4h10.4M6.3 4.4V2.8h3.4v1.6M4.2 4.4l.7 9.2h6.2l.7-9.2M6.7 6.9v4.6M9.3 6.9v4.6"/></svg>',
  more: '<svg viewBox="0 0 16 16"><circle cx="8" cy="8" r="6.4"/><circle cx="5.3" cy="8" r=".75" class="f"/><circle cx="8" cy="8" r=".75" class="f"/><circle cx="10.7" cy="8" r=".75" class="f"/></svg>',
  waveform: '<svg viewBox="0 0 40 40"><path d="M6 17v6M11 12v16M16 8v24M21 14v12M26 10v20M31 15v10M36 18v4"/></svg>',
  doc: '<svg viewBox="0 0 16 16"><path d="M4 1.8h5.2l3 3V14.2H4z"/><path d="M9 1.8v3.2h3.2M6 8.2h4.2M6 10.8h4.2"/></svg>',
  globe: '<svg viewBox="0 0 16 16"><circle cx="8" cy="8" r="6.3"/><path d="M1.7 8h12.6M8 1.7c1.8 1.9 2.7 4 2.7 6.3S9.8 12.4 8 14.3M8 1.7C6.2 3.6 5.3 5.7 5.3 8s.9 4.4 2.7 6.3"/></svg>',
  mic: '<svg viewBox="0 0 16 16"><rect x="5.9" y="1.6" width="4.2" height="8" rx="2.1"/><path d="M3.6 7.6a4.4 4.4 0 0 0 8.8 0M8 12v2.3"/></svg>',
  speaker: '<svg viewBox="0 0 16 16"><path d="M2.4 6h2.4L8 3.2v9.6L4.8 10H2.4z"/><path d="M10.4 6a2.8 2.8 0 0 1 0 4M12.1 4.2a5.3 5.3 0 0 1 0 7.6"/></svg>',
  pause: '<svg viewBox="0 0 16 16"><rect x="4" y="3" width="2.7" height="10" rx=".8" class="f"/><rect x="9.3" y="3" width="2.7" height="10" rx=".8" class="f"/></svg>',
  star: '<svg viewBox="0 0 16 16"><path d="m8 1.9 1.85 3.85 4.2.55-3.07 2.92.78 4.16L8 11.35 4.24 13.38l.78-4.16L1.95 6.3l4.2-.55z"/></svg>',
  stop: '<svg viewBox="0 0 16 16"><rect x="3.6" y="3.6" width="8.8" height="8.8" rx="1.6" class="f"/></svg>',
  pencil: '<svg viewBox="0 0 16 16"><path d="m10.9 2.6 2.5 2.5-7.9 7.9-3.2.7.7-3.2z"/></svg>',
  updown: '<svg class="pc" viewBox="0 0 16 16"><path d="M5 6.2 8 3.4l3 2.8M5 9.8l3 2.8 3-2.8"/></svg>',
  ok: '<svg viewBox="0 0 16 16"><circle cx="8" cy="8" r="7" class="f"/><path d="m4.9 8.2 2.1 2.1 4.1-4.4" class="w"/></svg>',
  play: '<svg class="i-play" viewBox="0 0 16 16"><path d="M4.6 2.6v10.8L13.2 8z" class="f"/></svg>',
  // doc.badge.clock、person.crop.circle.badge.questionmark、arrow.right
  draft: '<svg viewBox="0 0 16 16"><path d="M8.4 14.2H3.6V1.8h5.2l3 3v2.4"/><path d="M8.6 1.8V5h3.2"/><circle cx="11.6" cy="11.6" r="3"/><path d="M11.6 10.2v1.5l.9.7"/></svg>',
  guess: '<svg viewBox="0 0 16 16"><circle cx="7.2" cy="8" r="5.8"/><circle cx="7.2" cy="6.5" r="1.9"/><path d="M3.7 12.1c.8-1.4 2-2.1 3.5-2.1s2.7.7 3.5 2.1"/><circle cx="12.6" cy="11.4" r="3.1" class="f"/><path d="M11.7 10.6a.95.95 0 1 1 1.3.9c-.3.1-.4.3-.4.6M12.6 13.1v0" class="w"/></svg>',
  arrow: '<svg viewBox="0 0 16 16"><path d="M2.8 8h10.4M9.4 4.4 13 8l-3.6 3.6"/></svg>',
  cursor: '<svg class="cursor" data-k="cursor" viewBox="0 0 20 26" width="20" height="26"><path d="M2 1.5v19.3l4.6-4.4 3 7 3.3-1.4-3-6.8h6.4z" fill="#000" stroke="#fff" stroke-width="1.4" stroke-linejoin="round"/></svg>',
};
const pop = (icon, label, chevron = true) => `<span class="pop">${icon}${esc(label)}${chevron ? I.chevron.replace('<svg', '<svg class="pc"') : ''}</span>`;
const ts = (time, key = '') => `<a class="ts"${key ? ` data-k="${key}"` : ''}>${time}</a>`;

// 播放列的波形：一段一段的說話（秒數，lo 是比較小聲的地方），段落之間有空白
const BLOCKS = [[3, ''], [0.5, 'gap'], [4.5, ''], [1, 'lo'], [3.5, ''], [0.5, 'gap'], [5, ''], [2, 'lo'], [3.5, ''], [0.5, 'gap'], [4, ''], [1, 'lo'], [4.5, ''], [0.5, 'gap'],
  [3, ''], [2.5, 'lo'], [4, ''], [0.5, 'gap'], [5.5, ''], [1.5, 'lo'], [1, 'lo']];
const LENGTH = 52;
const blocks = BLOCKS.map(([d, c]) => `<i class="${c}" style="flex:${d} 0 0"></i>`).join('');
const seconds = (text) => { const [m, s] = text.split(':').map(Number); return m * 60 + s; };

function windowMarkup(d) {
  const { ui: u, meeting: m } = d;
  const ml = ` lang="${d.meetingLang}"`;
  // 說話者 1 的聲紋之前記過：轉錄完就帶入名字，後面標「自動」；說話者 2 是猜的，套用後才換（demo.js）
  const speakerName = (n) => (n === m.auto.speaker ? m.auto.name : m.speakers[n - 1]);
  const speaker = (n) => `<i class="s${n}" data-spk="${n}"${ml}>${esc(speakerName(n))}</i>${n === m.auto.speaker ? `<span class="auto">${esc(u.auto)}</span>` : ''}`;
  const t = u.tabs;
  const due = `<span class="due${m.due.pad ? ' pad' : ''}" data-k="due0">${esc(m.due.before)}</span>`;
  // 英文放不下時，「暫停」「標記重點」只留圖示，「停止並轉錄」保留文字（跟 app 的 ViewThatFits 一樣）
  const btn = (icon, label, key = '') => {
    const k = key ? ` data-k="${key}btn"` : '';
    if (u.compactButtons && key !== 'stop') return `<span class="btn icon"${k}>${icon}</span>`;
    return `<span class="btn${key === 'stop' ? ' btn-stop' : ''}"${k}>${icon}${esc(label)}</span>`;
  };
  return `<div class="win" data-mode="idle" lang="${u.lang}">
  <aside class="side">
    <div class="tb tb-left">
      <span class="lights"><i></i><i></i><i></i></span>
      <span class="tb-idle"><span class="ib tb-rec" data-k="recbtn">${I.record}</span><span class="tb-sep"></span><span class="ib tb-chev">${I.chevron}</span><span class="ib tb-ask">${I.askAll}</span></span>
      <span class="tb-live"><i class="dot"></i><span class="tb-time" data-k="tbtime">00:00</span><span class="ib tb-stop">${I.stopSquare}</span></span>
      <span class="ib tb-side">${I.sidebar}</span>
    </div>
    <div class="search">${I.search}${esc(u.search)}</div>
    <ul class="mlist">
      <li class="m m-new"><b data-k="rowtitle">${esc(u.recTitle)}</b><span><time>10/19</time><em data-k="rowst" class="st-rec">${esc(u.status.recording)}</em></span><i class="mini-bar" data-k="rowbar"><i></i></i></li>
${m.others.map(([title, date, status]) => `      <li class="m"><b${ml}>${esc(title)}</b><span><time>${date}</time><em>${esc(status)}</em></span></li>`).join('\n')}
    </ul>
    <div class="trash">${I.trash}${esc(u.trash)}<span class="import">${I.importFile}${esc(u.importFile)}</span></div>
  </aside>

  <section class="main">
    <div class="tb tb-main">
      <span class="tb-title" data-k="wintitle">${esc(u.appTitle)}</span>
      <div class="segs${u.equalTabs ? ' eq' : ''}">${Object.entries(t).map(([key, label]) => `<span class="sg${key === 'summary' ? ' on' : ''}" data-tab="${key}"><span${key === 'review' ? ' data-k="revlabel"' : ''}>${esc(label)}</span></span>`).join('')}</div>
      <span class="more">${I.more}${I.chevron.replace('<svg', '<svg class="mchev"')}</span>
    </div>

    <div class="empty">${I.waveform}<b>${esc(u.empty[0])}</b><span>${esc(u.empty[1])}</span></div>

    <div class="detail">
      <div class="head">
        <div class="h-row"><h3 class="mtitle" data-k="mtitle">${esc(u.recTitle)}</h3><span class="lnk">${esc(u.edit)}</span></div>
        <div class="meta"><span>${esc(u.date)}</span><span class="m-dur">${esc(u.duration)}</span><span class="m-spk">${m.speakers.map((_, i) => speaker(i + 1)).join('')}</span></div>
        <div class="pickers">${pop(I.doc, u.template)}${pop(I.globe, m.language)}</div>
        <div class="hb hb-status" data-k="banner"><span class="spin"></span><span data-k="bantext"></span><span class="bar" data-k="banbar"><i></i></span><span class="bbtn">${esc(u.cancel)}</span></div>
        <div class="hb hb-helper" data-k="helper"><span class="spin"></span><span>${esc(u.banner.naming)}</span><span class="bbtn">${esc(u.cancel)}</span></div>
        <div class="hb hb-draft" data-k="draft">${I.draft}<span>${esc(u.draft)}</span></div>
        <div class="hb hb-suggest" data-k="suggest">${I.guess}<span>${esc(u.suggest)}</span><span class="btn" data-k="suggestbtn">${esc(u.suggestOpen)}</span></div>
      </div>

      <div class="recbar">
        <span class="rb-state"><i class="dot"></i>${esc(u.recording)}<span class="rb-time" data-k="rbtime">00:00</span></span>
        <span class="vu-wrap">${I.mic}<span class="vu" data-k="vu1">${'<i></i>'.repeat(14)}</span></span>
        <span class="vu-wrap">${I.speaker}<span class="vu" data-k="vu2">${'<i></i>'.repeat(14)}</span></span>
        <span class="rb-btns">${btn(I.pause, u.pause)}${btn(I.star, u.bookmark, 'mark')}${btn(I.stop, u.stop, 'stop')}</span>
      </div>

      <div class="live">
        <div class="live-tx" data-k="livetx"${ml}><p class="live-hint" lang="${u.lang}">${esc(u.liveHint)}</p></div>
        <div class="mynotes">
          <b>${esc(u.myNotes)}</b>
          <small>${esc(u.notesHint)}</small>
          <div class="nbox" data-k="nbox"${ml}><span data-k="ntext"></span><i class="caret"></i></div>
        </div>
      </div>

      <div class="panes">
        <div class="pane p-wait"><p class="dim">${esc(u.waiting)}</p></div>

        <div class="pane p-summary">
          <span class="lnk edit-r">${I.pencil}${esc(u.edit)}</span>
          <h4 class="in">${esc(u.headings[0])}</h4>
          <ul>
${m.summary.map(([text, time], i) => `            <li class="in"${ml}>${esc(text).replace('{due}', due)}${ts(time, i === 1 ? 'ts13' : '')}</li>`).join('\n')}
          </ul>
          <h4 class="in">${esc(u.headings[1])}</h4>
          <ul>
${m.decisions.map(([text, time]) => `            <li class="in"${ml}>${esc(text)}${ts(time)}</li>`).join('\n')}
          </ul>
          <h4 class="in">${esc(u.headings[2])}</h4>
          <table class="in">
            <tr><th>${u.tableHead.map(esc).join('</th><th>')}</th></tr>
${m.actions.map(([owner, item, time, dueText], i) => `            <tr><td${ml}>${esc(owner)}</td><td${ml}>${esc(item)}${ts(time)}</td><td>${i === 0 ? `<span class="due" data-k="due1">${esc(dueText)}</span>` : esc(dueText)}</td></tr>`).join('\n')}
          </table>
        </div>

        <div class="pane p-detail"${ml}>
${m.details.map(([h, p, time]) => `          <h4>${esc(h)}</h4>\n          <p>${esc(p)}${ts(time)}</p>`).join('\n')}
        </div>

        <div class="pane p-transcript">
          <div class="tx-tools"><span class="tfield">${I.search}${esc(u.txSearch)}</span>${pop('', u.allSpeakers, false).replace('</span>', `${I.updown}</span>`)}${pop('', u.fixWording, false)}</div>
          <div class="tx" data-k="tx">
${m.transcript.map(([at, who, text]) => `            <div class="row" data-at="${at}">${ts(`00:${String(at).padStart(2, '0')}`)}<i class="s${who}" data-spk="${who}"${ml}>${esc(speakerName(who))}</i><p${ml}>${esc(text)}</p></div>`).join('\n')}
          </div>
        </div>

        <div class="pane p-review">
          <div class="rv-open">
            <p class="dim">${esc(u.reviewIntro)}</p>
${m.questions.map(([time, q], i) => `            <div class="q" data-k="q${i + 1}">
              <div class="q-line"><b>${i + 1}</b>${ts(time)}<span class="qt"${ml}>${esc(q)}</span></div>
              <div class="q-ans">${i === 0 ? `<span class="yn"><span class="yb" data-k="yes1">${esc(u.yes)}</span><span class="yb">${esc(u.no)}</span></span>` : `<span class="field"><span class="ph">${esc(u.answer)}</span><span class="val"></span><i class="caret"></i></span>`}<span class="chk"${i === 1 ? ' data-k="skip2"' : ''}><i></i>${esc(u.skip)}</span></div>
            </div>`).join('\n')}
          </div>
          <div class="rv-done">
            <span class="ok">${I.ok}${esc(u.doneTitle)}</span>
            <p class="dim">${esc(u.doneText)}</p>
          </div>
          <div class="rv-foot"><span class="dim">${esc(u.footnote)}</span><span class="btn prim dis" data-k="submit">${esc(u.submit.none)}</span></div>
        </div>

        <div class="pane p-notes"${ml}><p data-k="notes">[00:06] ${esc(m.note)}</p></div>

        <div class="pane p-ask" data-k="ask">
          <div class="ask-list">
            <div class="ask-empty"><h4>${esc(u.askTitle)}</h4><p class="dim">${esc(u.askIntro)}</p></div>
            <div class="ask-busy"><span class="spin"></span><span class="dim">${esc(u.answering)}</span><span class="lk">${esc(u.cancel)}</span></div>
            <div class="qa">
              <b${ml}>${esc(m.ask.question)}</b>
              <p${ml}>${esc(m.ask.answer)}</p>
${m.ask.cites.map(([time, quote], i) => `              <div class="cite">${ts(time, i === 0 ? 'cite1' : '')}<span${ml}>${esc(quote)}</span></div>`).join('\n')}
            </div>
          </div>
          <div class="ask-bar"><span class="field" data-k="askfield"><span class="ph">${esc(u.askPlaceholder)}</span><span class="val" data-k="askval"${ml}></span><i class="caret"></i></span><span class="btn prim dis" data-k="askbtn">${esc(u.askButton)}</span></div>
        </div>
      </div>

      <div class="player">
        <span class="pl-btn" data-k="plbtn">${I.play}${I.pause.replace('<svg', '<svg class="i-pause"')}</span>
        <span class="pl-time" data-k="pltime">00:00</span>
        <span class="track" data-k="track"><span class="marks">${m.questions.map(([time]) => `<i style="left:${((seconds(time) / LENGTH) * 100).toFixed(1)}%"></i>`).join('')}</span><span class="blocks">${blocks}</span><span class="blocks played">${blocks}</span><i class="phead"></i></span>
        <span class="pl-time">00:${LENGTH}</span>
        <span class="pl-rate">1x${I.chevron}</span>
      </div>
    </div>

    <div class="toast" data-k="toast"></div>
    <div class="popv" data-k="popv">
      <i class="pv-arrow"></i>
      <b class="pv-title">${esc(u.suggestTitle)}</b>
      <div class="pv-row"><i class="s${m.guess.speaker}"${ml}>${esc(m.speakers[m.guess.speaker - 1])}</i>${I.arrow}<b${ml}>${esc(m.guess.name)}</b><span class="pv-links"><span class="lk">${esc(u.ignore)}</span><span class="lk" data-k="applybtn">${esc(u.apply)}</span></span></div>
      <div class="pv-why">${ts(m.guess.time)}<small${ml}>${esc(m.guess.reason)}</small></div>
      <div class="pv-foot"><small>${esc(u.applyHint)}</small><span class="btn prim">${esc(u.applyAll)}</span></div>
    </div>
  </section>
</div>
${I.cursor}`;
}

// fallback：沒有 JavaScript 時顯示的截圖（<noscript>）
export function demoSection(lang, { fallback }) {
  const d = demoContent[lang];
  const data = {
    meetingLang: d.meetingLang,
    length: LENGTH,
    speed: d.speed,
    text: { status: d.ui.status, banner: d.ui.banner, bookmarked: d.ui.bookmarked, reviewTab: d.ui.reviewTab, reviewLabel: d.ui.tabs.review, submit: d.ui.submit, recTitle: d.ui.recTitle },
    meeting: { title: d.meeting.title, live: d.meeting.live, note: d.meeting.note, due: d.meeting.due, dueAfter: d.meeting.dueAfter,
      guess: { speaker: d.meeting.guess.speaker, name: d.meeting.guess.name }, question: d.meeting.ask.question },
    captions: d.captions,
    endCaption: d.endCaption,
    controls: d.controls,
  };
  return `<div class="demo" id="demo">
<div class="demo-frame"><div class="demo-pan"><div class="stage" aria-hidden="true">
${windowMarkup(d)}
</div></div></div>
<div class="demo-bar" role="group" aria-label="${esc(d.controls.label)}">
  <button class="d-play" type="button" aria-label="${esc(d.controls.pause)}"><svg class="i-pause" viewBox="0 0 16 16" aria-hidden="true"><rect x="4" y="3" width="2.7" height="10" rx=".8"/><rect x="9.3" y="3" width="2.7" height="10" rx=".8"/></svg><svg class="i-play" viewBox="0 0 16 16" aria-hidden="true"><path d="M4.6 2.6v10.8L13.2 8z"/></svg><svg class="i-replay" viewBox="0 0 16 16" aria-hidden="true"><path d="M3.2 8.6a4.9 4.9 0 1 0 1.5-3.9" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/><path d="M2.6 2.4v3.4H6z"/></svg></button>
  <ol class="chapters" aria-label="${esc(d.controls.chapters)}">
${d.chapters.map((label, i) => `    <li><button type="button" data-ch="${i}"><i></i>${esc(label)}</button></li>`).join('\n')}
  </ol>
</div>
<p class="demo-cap"></p>
<script type="application/json" id="demo-data">${JSON.stringify(data).replace(/</g, '\\u003c')}</script>
</div>
<noscript><figure class="wide">
${fallback}
</figure></noscript>`;
}
