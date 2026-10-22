// 指南：教學和比較頁的內容（docs/website.md §14.2）。這裡的 HTML 是自己寫的，會原樣輸出。
//
// 每一篇：slug（網址，三種語言共用）、published／updated（YYYY-MM-DD，updated 會顯示在頁面上、寫進 sitemap）、
// 各語言的 title（<title>）、description、h1、lead、body（HTML，用 h2、p、ul、ol、表格用 table()）、
// faq（[問題, 回答]，選填）。沒有寫的語言就沒有那一頁。內容改了要更新 updated。
// 寫法照 §14.2：每個事實附來源或查證日期、比較要公平、跟 app 用同樣的詞。

const MEET_HELP = 'https://support.google.com/meet/answer/12849897';
const GEMINI_NOTES_HELP = 'https://support.google.com/meet/answer/14925782';
const BREEZE = 'https://huggingface.co/MediaTek-Research/Breeze-ASR-25';
const BREEZE_NEWS = 'https://inside.com.tw/article/38853-mediatek-research-mr-breeze-asr25';
const GITHUB = 'https://github.com/juinwei7/Shiyin';
// 比較頁的來源（2026-10-21 查證）
const SRC = {
  notta: 'https://www.notta.ai/zh-tw/pricing',
  otter: 'https://otter.ai/pricing',
  granola: 'https://www.granola.ai/pricing',
  granolaSecurity: 'https://www.granola.ai/security',
  yating: 'https://asr.yating.tw/',
  meetily: 'https://meetily.ai/llms.txt',
  meetilyRepo: 'https://github.com/Zackriya-Solutions/meetily',
  anarlog: 'https://anarlog.so/pricing',
  minutes: 'https://useminutes.app',
};

const table = (head, rows) => `<div class="table"><table>
<thead><tr>${head.map((h) => `<th scope="col">${h}</th>`).join('')}</tr></thead>
<tbody>
${rows.map(([first, ...rest]) => `<tr><th scope="row">${first}</th>${rest.map((c) => `<td>${c}</td>`).join('')}</tr>`).join('\n')}
</tbody>
</table></div>`;

export const GUIDES = [
  {
    slug: 'google-meet-transcript',
    published: '2026-10-21',
    updated: '2026-10-21',
    zh: {
      title: 'Google Meet 中文逐字稿怎麼做？內建不支援中文時的做法｜拾音 Shiyin',
      description: 'Google Meet 內建的逐字稿和 Gemini 筆記目前不支援中文。這篇說明在 Mac 上錄下 Meet 會議，轉成中文逐字稿和會議記錄的方法，錄音不用上傳。',
      h1: 'Google Meet 中文逐字稿和會議記錄怎麼做',
      lead: 'Meet 內建的逐字稿不支援中文。在 Mac 上可以直接錄下電腦播出的聲音，自己轉成中文逐字稿，再整理成會議記錄。',
      body: `
<h2>Meet 內建的逐字稿能用中文嗎？</h2>
<p>目前不能。Google 的說明列出 Meet 逐字稿支援英文、法文、德文、義大利文、日文、韓文、葡萄牙文和西班牙文，沒有中文（<a href="${MEET_HELP}">Google Meet 說明：使用逐字稿</a>）。Gemini 的「幫我做筆記」也是同樣幾種語言（<a href="${GEMINI_NOTES_HELP}">Google Meet 說明：語言支援</a>）。以上是 2026 年 10 月查證的結果。</p>
<p>就算以後支援中文，內建逐字稿還有幾個限制：</p>
<ul>
<li>只有部分 Workspace 付費方案能用（Business Standard 以上、Enterprise、Workspace Individual 等），個人的 Gmail 帳號沒有。</li>
<li>要由會議的主辦人或共同主辦人開啟，參加別人的會議時自己開不了。</li>
<li>iPhone、iPad 上不能開啟。</li>
</ul>

<h2>做法：在 Mac 上錄下電腦的聲音</h2>
<p>不管 Meet 有沒有中文逐字稿，你在自己電腦上聽得到的聲音都可以錄下來，轉逐字稿交給 Mac 自己做。這樣不用主辦人開權限，也不用請會議機器人加入，其他人的畫面上不會多出一個「筆記助理」。</p>
<p>用拾音的步驟：</p>
<ol>
<li>開會前打開拾音，錄音來源選「麥克風＋電腦聲音」。</li>
<li>按「錄音」（⌘R）。第一次錄電腦聲音時，macOS 會詢問「錄製系統音訊」的權限，按允許。拾音偵測到 Google Meet 開始用麥克風時，也會提醒你要不要開始錄音。</li>
<li>開會時左邊是即時逐字稿。右邊可以做筆記，每換一行會記下當時的錄音時間；重要的地方按「標記重點」。</li>
<li>會議結束按停止。拾音先用即時逐字稿整理一份草稿，同時在 Mac 上重新轉錄、分出說話者，完成後換成正式的會議紀錄：摘要、決議、待辦和未決問題，每一條都附時間點。</li>
</ol>
<p>你的聲音和電腦播出的聲音會分別錄在左右聲道，戴耳機開會也錄得到其他人。</p>

<h2>中文夾英文也轉得出來</h2>
<p>拾音轉錄中文用的是聯發創新基地開源的 Breeze ASR 25，針對台灣中文和中英夾雜訓練過。在中英夾雜的測試資料上，它的錯誤率比原本的 Whisper large-v2 少了約 56%（<a href="${BREEZE}">模型說明</a>）。「deploy」「Q3 roadmap」這類詞比較不會被聽成中文字。詳細的比較在<a href="/guides/mandarin-english-transcription/">中英夾雜的會議逐字稿</a>這篇。</p>

<h2>錄音會上傳嗎？</h2>
<p>不會。錄音、轉錄和說話者辨識都在你的 Mac 上完成。只有整理會議紀錄時，逐字稿的文字會送到你選的整理工具（例如你自己的 ChatGPT 或 Claude 訂閱）；也可以選本機的模型，或「不整理」只留逐字稿。哪些資料會送出去，在<a href="/guides/offline-transcription-mac/">離線轉錄</a>這篇有完整的表。</p>

<h2>幾種做法比較</h2>
${table(['做法', '中文', '費用', '錄音在哪裡', '其他人看得到嗎'], [
  ['Meet 內建逐字稿', '不支援', 'Workspace 付費方案', 'Google 雲端', '會顯示逐字稿已開啟'],
  ['Gemini「幫我做筆記」', '不支援', 'Workspace 付費方案', 'Google 雲端', '會顯示'],
  ['雲端會議記錄服務（機器人加入會議）', '多數支援', '多為月費制', '服務商的雲端', '會看到機器人加入'],
  ['在 Mac 上錄電腦聲音（拾音）', '支援，含中英夾雜', '免費', '你的 Mac', '不會出現在會議裡'],
])}
<p class="note">雲端服務的好處是手機、網頁都能用，不用自己的電腦跑轉錄。要選哪一種，看你在不在意錄音上傳，和有沒有一台 Mac。</p>
`,
      faq: [
        ['其他與會者會知道我在錄音嗎？', '拾音不會加入會議，Meet 裡不會顯示。不過錄音前還是建議先告知與會者，這是基本禮貌，有些公司也有規定。'],
        ['Meet 錄影存到雲端硬碟的檔案可以轉嗎？', '可以。把下載下來的 mp4 拖進拾音的視窗就會轉錄，一樣會分出說話者、整理成會議紀錄。'],
        ['要付費嗎？', '拾音免費、開源。整理會議紀錄用的是你自己的 ChatGPT、Claude 訂閱或 API 額度；選「不整理」就沒有任何費用。'],
        ['Windows 可以用嗎？', '目前只有 Mac 版（macOS 26 以上、Apple Silicon），Windows 版正在開發。'],
      ],
    },
  },

  {
    slug: 'record-online-meetings-mac',
    published: '2026-10-21',
    updated: '2026-10-21',
    zh: {
      title: 'Mac 怎麼錄 Zoom、Teams、Google Meet 會議？不用 BlackHole、不用機器人｜拾音 Shiyin',
      description: '在 Mac 上錄線上會議的幾種方法比較：會議軟體內建錄影、BlackHole 虛擬音效卡、會議機器人，以及直接錄系統音訊、邊錄邊出逐字稿的做法。',
      h1: '在 Mac 上錄 Zoom、Teams、Google Meet 會議',
      lead: '以前在 Mac 上錄對方的聲音，要裝 BlackHole 這類虛擬音效卡。現在 macOS 可以讓 app 直接錄電腦播出的聲音。這篇比較幾種錄線上會議的方法，並說明用拾音邊錄邊出逐字稿的步驟。',
      body: `
<h2>幾種常見的方法</h2>
${table(['方法', '要做什麼', '缺點'], [
  ['會議軟體內建的錄影', '在 Zoom、Teams、Meet 裡按錄影', '通常要主辦人允許，有的要付費方案；錄完是影片檔，逐字稿要另外處理'],
  ['虛擬音效卡（BlackHole、Loopback）', '安裝音效驅動程式，在「音訊 MIDI 設定」建立多重輸出裝置', '設定步驟多；用多重輸出裝置時，鍵盤的音量鍵不能調音量'],
  ['雲端會議記錄服務的機器人', '把服務的機器人邀進會議', '所有人都看得到機器人；錄音上傳到服務商；有些公司不允許'],
  ['直接錄系統音訊（拾音）', '第一次錄音時允許「錄製系統音訊」', '只有 Mac 版，要 macOS 26 和 Apple Silicon'],
])}

<h2>用拾音錄線上會議</h2>
<ol>
<li><a href="/#download">下載拾音</a>，第一次打開時照引導允許麥克風、下載轉錄模型（約 1.7 GB；下載好之前會先用 macOS 內建的辨識）。</li>
<li>錄音來源選「麥克風＋電腦聲音」。只想錄對方時選「只錄電腦聲音」。</li>
<li>會議開始時按「錄音」（⌘R），在其他 app 裡按 ⌃⌥⌘R 也可以。第一次錄電腦聲音時，macOS 會詢問「錄製系統音訊」的權限，按允許。</li>
<li>錄音中可以把主視窗關掉，選單列會顯示錄音中，可以暫停或停止。</li>
<li>會議結束按停止。拾音在 Mac 上轉錄、分出說話者，整理成會議紀錄。</li>
</ol>
<p>拾音偵測到 Zoom、Teams、Google Meet 開始用麥克風時，會提醒你要不要開始錄音；會議好像結束時提醒你停止。它只會提醒，不會自己開始錄。</p>

<h2>錄音時要注意的地方</h2>
<ul>
<li><strong>戴耳機也錄得到</strong>：錄的是電腦播出的聲音，不是喇叭的聲音再被麥克風收進去。</li>
<li><strong>你和對方分開錄</strong>：你的聲音和電腦的聲音錄在左右兩個聲道，轉錄、分說話者時比較準。</li>
<li><strong>整台 Mac 的聲音都會錄到</strong>：通知音、音樂也會進去，開會時先關掉。</li>
<li><strong>沒收到聲音會提醒</strong>：超過 15 秒沒收到電腦的聲音，或麥克風沒收到聲音、聲音太小、背景太吵，錄音畫面都會提醒你。</li>
<li><strong>中途換耳機沒關係</strong>：錄音中接上或拔掉耳機、換了輸出裝置，會自動接上繼續錄。</li>
<li><strong>先告知與會者</strong>：錄音前跟大家說一聲。</li>
</ul>

<h2>錄完之後</h2>
<p>停止後馬上會有一份用即時逐字稿整理的草稿，正式轉錄完再換成正式版：摘要、決議、待辦、未決問題，每一條都附時間點，點了就從那裡播。會議紀錄可以匯出成 Markdown、PDF、Word；逐字稿可以存成 txt、srt、vtt 或 Word。</p>
<p>Google Meet 的中文逐字稿有另外的限制，請看<a href="/guides/google-meet-transcript/">Google Meet 中文逐字稿</a>這篇。</p>
`,
      faq: [
        ['會議軟體會通知其他人我在錄音嗎？', '不會。拾音不加入會議，也不經過會議軟體，所以會議裡不會顯示錄音中。錄音前請自己告知與會者。'],
        ['支援哪些會議軟體？', '只要聲音是從這台 Mac 播出來的都可以：Zoom、Microsoft Teams、Google Meet、Webex、Slack、LINE 通話都行。'],
        ['可以錄現場的會議嗎？', '可以，錄音來源選「只錄麥克風」。人多、比較吵時，可以先選「大約幾個人」，說話者會分得比較準。'],
        ['需要哪個版本的 macOS？', 'macOS 26 以上，Apple Silicon（M1 以上），記憶體 8 GB 以上。'],
      ],
    },
    en: {
      title: 'How to Record Zoom, Teams and Google Meet on a Mac (No BlackHole, No Bot) | Shiyin',
      description: 'Four ways to record online meetings on a Mac compared: built-in recording, BlackHole, meeting bots, and capturing the system audio directly with a live transcript.',
      h1: 'How to record Zoom, Teams and Google Meet on a Mac',
      lead: 'Recording the other side of a call on a Mac used to mean installing a virtual audio driver like BlackHole. macOS now lets apps capture the computer’s audio directly. Here is how the common methods compare, and how to record with a live transcript in Shiyin.',
      body: `
<h2>The common methods</h2>
${table(['Method', 'What you do', 'Downsides'], [
  ['Built-in recording', 'Click Record in Zoom, Teams or Meet', 'Usually needs the host’s permission, sometimes a paid plan; you get a video file and still need a transcript'],
  ['Virtual audio driver (BlackHole, Loopback)', 'Install an audio driver and build a Multi-Output Device in Audio MIDI Setup', 'Fiddly setup; the volume keys don’t work while a Multi-Output Device is selected'],
  ['A meeting bot from a cloud note taker', 'Invite the bot to the call', 'Everyone sees the bot; audio goes to the vendor; some companies don’t allow it'],
  ['Capture system audio directly (Shiyin)', 'Allow “System Audio Recording” the first time', 'Mac only; needs macOS 26 and Apple Silicon'],
])}

<h2>Recording a call with Shiyin</h2>
<ol>
<li><a href="/en/#download">Download Shiyin</a>. On first launch, allow the microphone and download the transcription models (about 1.7 GB; macOS built-in recognition is used until they’re ready).</li>
<li>Set the source to “Microphone + computer audio”. To record only the other side, choose “Computer audio only”.</li>
<li>When the meeting starts, click Record (⌘R), or press ⌃⌥⌘R from any app. The first time, macOS asks for System Audio Recording permission: click Allow.</li>
<li>You can close the main window while recording. The menu bar shows that you’re recording and lets you pause or stop.</li>
<li>Click Stop when the meeting ends. Shiyin transcribes on your Mac, tells the speakers apart and writes the meeting notes.</li>
</ol>
<p>When Zoom, Teams or Google Meet starts using the microphone, Shiyin asks whether to record, and reminds you to stop when the meeting seems to be over. It only asks; it never starts recording on its own.</p>

<h2>Things to know</h2>
<ul>
<li><strong>Headphones are fine.</strong> Shiyin records what the Mac plays, not what the speakers put back into the microphone.</li>
<li><strong>You and the others are on separate channels.</strong> Your microphone and the computer audio are recorded to the left and right channels, which helps transcription and speaker identification.</li>
<li><strong>All of the Mac’s audio is captured.</strong> Notification sounds and music get recorded too, so mute them during the call.</li>
<li><strong>Silence gets flagged.</strong> If no computer audio arrives for 15 seconds, or the microphone hears nothing, is too quiet or the room is too noisy, the recording screen tells you.</li>
<li><strong>Switching headphones mid-call is fine.</strong> Shiyin reconnects to the new output and keeps recording.</li>
<li><strong>Tell people you’re recording.</strong></li>
</ul>

<h2>After the meeting</h2>
<p>Draft notes appear right after you stop, written from the live transcript, and are replaced when the final transcript is ready: a summary, decisions, action items and open questions, each with a timestamp that plays the moment it came from. Export notes as Markdown, PDF or Word, and transcripts as txt, srt, vtt or Word.</p>
`,
      faq: [
        ['Will the meeting app tell others I’m recording?', 'No. Shiyin doesn’t join the meeting or go through the meeting app, so nothing shows up in the call. Tell the other participants yourself.'],
        ['Which meeting apps work?', 'Anything that plays audio on your Mac: Zoom, Microsoft Teams, Google Meet, Webex, Slack huddles and so on.'],
        ['Can it record in-person meetings?', 'Yes. Choose “Microphone only”. In a crowded or noisy room, tell Shiyin roughly how many people there are and speakers are separated more accurately.'],
        ['Which macOS version do I need?', 'macOS 26 or later on Apple Silicon (M1 or later), with 8 GB of memory or more.'],
      ],
    },
  },

  {
    slug: 'mandarin-english-transcription',
    published: '2026-10-21',
    updated: '2026-10-21',
    zh: {
      title: '中英夾雜的會議逐字稿怎麼轉才準？在 Mac 本機用 Breeze ASR 25｜拾音 Shiyin',
      description: '台灣會議常常中文夾英文，一般語音辨識容易把英文聽成中文字。這篇整理 Breeze ASR 25 和 Whisper 的錯誤率比較，以及在 Mac 上用它轉錄、校正專有名詞的方法。',
      h1: '中英夾雜的會議逐字稿怎麼轉才準',
      lead: '台灣的會議常常一句話裡夾英文，語音辨識很容易把英文聽成中文字。這篇說明原因、Breeze ASR 25 改善了多少，以及在 Mac 上用它轉錄的方法。',
      body: `
<h2>為什麼中英夾雜容易轉錯</h2>
<p>多數語音辨識模型的訓練資料以單一語言為主。一句中文講到一半換成英文，模型常常照前面的語言繼續猜，把「deploy」寫成發音相近的中文字，或乾脆換成中文的說法。產品名、縮寫、人名這類訓練資料裡少見的詞，錯得更多。</p>

<h2>Breeze ASR 25 是什麼</h2>
<p>Breeze ASR 25 是聯發創新基地（MediaTek Research）在 2025 年 6 月開源的語音辨識模型，以 OpenAI 的 Whisper large-v2 為基礎，加強台灣中文和中英夾雜，授權是 Apache 2.0（<a href="${BREEZE}">Hugging Face 模型頁</a>、<a href="${BREEZE_NEWS}">INSIDE 報導</a>）。</p>
<p>模型頁公布的錯誤率（WER，越低越好）：</p>
${table(['測試資料', 'Whisper large-v2', 'Breeze ASR 25', '差別'], [
  ['CSZS-zh-en（中英夾雜）', '29.49', '13.01', '少 56%'],
  ['ASCEND（中英混合對話）', '21.14', '17.74', '少 16%'],
  ['Common Voice 16 zh-TW（台灣中文）', '9.84', '7.97', '少 19%'],
])}
<p class="note">數字取自模型頁，2026 年 10 月查證。實際會議的準確度還會受收音距離、雜音、搶話影響。</p>

<h2>在 Mac 上用 Breeze ASR 25</h2>
<p>模型本身要自己架環境才能跑。拾音把它包進 Mac app，用 whisper.cpp 在本機執行，不用裝 Python、Homebrew 或 ffmpeg：</p>
<ol>
<li><a href="/#download">下載拾音</a>，第一次打開時下載轉錄模型（約 1.7 GB）。</li>
<li>按「錄音」開始錄，或把已經錄好的音檔、影片（m4a、mp3、wav、mp4、mov 等）拖進視窗。</li>
<li>停止後拾音在本機轉錄，逐字稿每一段都標上說話者和時間點。</li>
</ol>
<p>英文會議同樣用 Breeze ASR 25，日文會議改用 Whisper large-v3-turbo。開始錄音後拾音會自己判斷是哪一種語言。</p>

<h2>讓專有名詞也轉對</h2>
<ul>
<li><strong>詞彙表</strong>：把產品名、人名、縮寫加進詞彙表，轉錄時會當成提示。</li>
<li><strong>逐字稿校正</strong>：轉錄完，整理工具會依詞彙表和上下文，找出可能聽錯的字（同音字、專有名詞）標在逐字稿裡，你按「接受」才會改，也可以還原。</li>
<li><strong>用詞修正</strong>：同一個錯字可以整場一起改，逐字稿和會議紀錄一起換。</li>
<li><strong>詞彙表建議</strong>：拾音會從參加者、說話者名字、校正過的字建議要加進詞彙表的詞。</li>
</ul>

<h2>不適合的情況</h2>
<p>拾音目前不支援台語和粵語。會議裡台語很多的話，可以考慮專門支援台語的服務，例如雅婷逐字稿。</p>
`,
      faq: [
        ['一定要網路嗎？', '只有第一次下載模型時要。之後轉錄、分說話者都在本機，不需要網路。'],
        ['可以只用 macOS 內建的辨識嗎？', '可以，在設定裡改用 macOS 內建辨識，不用下載模型、速度快很多，但不會參考詞彙表，中英夾雜的準確度也比較差。'],
        ['錄音會上傳嗎？', '不會。轉錄在你的 Mac 上完成，只有整理會議紀錄時，逐字稿的文字會送到你選的整理工具。'],
        ['支援台語嗎？', '目前不支援。會議語言可以選中文、英文或日文。'],
      ],
    },
  },

  {
    slug: 'offline-transcription-mac',
    published: '2026-10-21',
    updated: '2026-10-21',
    zh: {
      title: '會議錄音不想上傳？Mac 離線轉逐字稿的方法｜拾音 Shiyin',
      description: '大部分逐字稿服務要把錄音上傳到雲端。這篇說明在 Mac 上離線轉錄會議、分說話者的方法，哪些步驟會用到網路，以及完全不連網的設定。',
      h1: '會議錄音不想上傳？在 Mac 上離線轉逐字稿',
      lead: '大部分逐字稿服務要把錄音上傳到雲端。如果會議內容不能外流，可以讓轉錄完全在自己的 Mac 上完成。這篇說明哪些步驟可以離線、哪些會用到網路。',
      body: `
<h2>什麼時候需要離線轉錄</h2>
<ul>
<li>公司規定不能用雲端錄音服務，或不能讓會議機器人加入。</li>
<li>跟客戶、律師、醫師的對話，或人資面談這類要保密的內容。</li>
<li>不想讓錄音存在別人的伺服器上。雲端服務會不會保留錄音、拿去改進模型，各家規定不同，要看各自的服務條款。</li>
</ul>

<h2>拾音的每個步驟在哪裡做</h2>
${table(['步驟', '在哪裡做', '要網路嗎'], [
  ['錄音', '你的 Mac', '不用'],
  ['錄音中的即時逐字稿', '你的 Mac（macOS 內建的語音辨識）', '不用'],
  ['正式轉錄', '你的 Mac（whisper.cpp＋Breeze ASR 25）', '只有第一次下載模型時要'],
  ['說話者辨識、記住聲紋', '你的 Mac', '不用'],
  ['整理會議紀錄、對會議提問', '你選的整理工具', '用 ChatGPT、Claude 或 API 時要；用本機的 Ollama、LM Studio，或選「不整理」就不用'],
])}
<p>用 ChatGPT、Claude 或 API 整理時，送出去的只有文字：逐字稿、你的筆記和詞彙表、會議標題和日期。錄音檔不會送出去。</p>

<h2>完全不連網的設定</h2>
<ol>
<li>在有網路的時候先把轉錄模型下載好（第一次打開的引導，或設定的「模型與儲存空間」）。</li>
<li>在 Mac 上裝好 Ollama 或 LM Studio，下載一個本機模型。</li>
<li>拾音的設定 → 整理工具，選「OpenAI 相容 API」，網址填 Ollama 或 LM Studio 的本機網址（不用 key）。</li>
<li>或者整理工具選「不整理」，只留逐字稿。</li>
</ol>
<p class="note">本機模型整理出來的會議紀錄好不好，要看模型大小和你的 Mac 記憶體。小模型可能漏掉細節，建議先拿一場會議試試。</p>

<h2>資料存在哪裡</h2>
<p>錄音、逐字稿、會議紀錄和聲紋都存在這台 Mac 的「~/Library/Application Support/Shiyin」裡。刪除的會議會先放進垃圾桶，30 天內可以還原。「輔助說明 → 回報問題…」只會附上版本和環境，不含會議內容。</p>
<p>拾音是開源的（MIT 授權），原始碼在 <a href="${GITHUB}">GitHub</a>，想確認它有沒有偷偷上傳資料，可以自己看。</p>

<h2>跟雲端服務比</h2>
<p>雲端服務的好處是手機、網頁都能用，不用自己的電腦轉錄，也比較容易跟同事共用。離線轉錄的代價是要有一台夠力的 Mac（Apple Silicon、8 GB 以上記憶體），轉錄速度看電腦而定。</p>
`,
      faq: [
        ['即時逐字稿會上傳嗎？', '不會。錄音中的即時逐字稿用的是 macOS 內建、在裝置上執行的語音辨識。'],
        ['聲紋會傳到哪裡嗎？', '不會。聲紋只存在這台 Mac，可以在設定裡查看、刪除，或整個關掉。'],
        ['離線轉錄會比較不準嗎？', '不一定。拾音用的 Breeze ASR 25 針對台灣中文和中英夾雜訓練過，中英夾雜的錯誤率比原本的 Whisper 少約 56%。'],
        ['可以給同事看嗎？', '可以匯出成 Markdown、PDF、Word，或把整場會議打包成 .shiyin 傳給同事，對方用拾音雙擊就能匯入。'],
      ],
    },
    en: {
      title: 'Local Meeting Transcription on a Mac: Transcribe Meetings Offline | Shiyin',
      description: 'Most transcription services upload your recordings. Here is how to transcribe meetings and identify speakers entirely on your Mac, which steps use the internet, and how to set up a fully offline workflow.',
      h1: 'Local meeting transcription on a Mac',
      lead: 'Most transcription services upload your recordings to the cloud. If a meeting can’t leave your hands, the whole transcript can be made on your own Mac. Here is which steps stay offline and which use the internet.',
      body: `
<h2>When local transcription matters</h2>
<ul>
<li>Your company doesn’t allow cloud recording services or meeting bots.</li>
<li>Conversations with clients, lawyers or doctors, or HR interviews that must stay confidential.</li>
<li>You’d rather not keep recordings on someone else’s servers. Whether a cloud service keeps audio or uses it to improve its models depends on its terms.</li>
</ul>

<h2>Where each step happens in Shiyin</h2>
${table(['Step', 'Where', 'Internet?'], [
  ['Recording', 'Your Mac', 'No'],
  ['Live transcript while recording', 'Your Mac (macOS on-device speech recognition)', 'No'],
  ['Final transcription', 'Your Mac (whisper.cpp with Breeze ASR 25, or Whisper large-v3-turbo for Japanese)', 'Only to download the models once'],
  ['Speaker identification and voiceprints', 'Your Mac', 'No'],
  ['Meeting notes and questions', 'The summarizer you choose', 'Yes with ChatGPT, Claude or an API; no with a local model in Ollama or LM Studio, or with “Don’t summarize”'],
])}
<p>When notes are written with ChatGPT, Claude or an API, only text is sent: the transcript, your notes and glossary, and the meeting title and date. The audio never leaves your Mac.</p>

<h2>A fully offline setup</h2>
<ol>
<li>Download the transcription models while you’re online (during first launch, or in Settings → Models & Storage).</li>
<li>Install Ollama or LM Studio and download a local model.</li>
<li>In Shiyin, open Settings → Summarizer, choose “OpenAI-compatible API” and enter the local Ollama or LM Studio address (no key needed).</li>
<li>Or choose “Don’t summarize” and keep just the transcript.</li>
</ol>
<p class="note">How good the notes are depends on the local model and your Mac’s memory. Small models can miss details, so try one meeting first.</p>

<h2>Where your data lives</h2>
<p>Recordings, transcripts, notes and voiceprints are stored in ~/Library/Application Support/Shiyin on your Mac. Deleted meetings go to a trash you can restore from for 30 days. Help → Report a Problem only includes your version and setup, never meeting content.</p>
<p>Shiyin is open source under the MIT License. The code is on <a href="${GITHUB}">GitHub</a> if you want to check for yourself that nothing is uploaded.</p>

<h2>Compared with cloud services</h2>
<p>Cloud services work on phones and in the browser, don’t need a powerful computer, and make sharing with colleagues easier. Local transcription needs a capable Mac (Apple Silicon, 8 GB of memory or more), and its speed depends on that Mac.</p>
`,
      faq: [
        ['Is the live transcript uploaded?', 'No. The live transcript uses the speech recognition built into macOS, which runs on the device.'],
        ['Where do voiceprints go?', 'Nowhere. They stay on your Mac, and you can view, delete or turn them off in Settings.'],
        ['Which languages can it transcribe?', 'English, Chinese (including Mandarin mixed with English) and Japanese. Shiyin detects the language after you start recording.'],
        ['Can I share notes with colleagues?', 'Export them as Markdown, PDF or Word, or send the whole meeting as a .shiyin file that opens in Shiyin with a double-click.'],
      ],
    },
    ja: {
      title: 'Mac で会議をローカル文字起こし（ボット不要・無料・オープンソース）｜拾音 Shiyin',
      description: '録音をクラウドにアップロードせず、Mac の中で会議を文字起こしして話者を識別する方法。インターネットが必要な場面と、完全にオフラインで使う設定も説明します。',
      h1: 'Mac で会議をローカル文字起こしする',
      lead: '多くの文字起こしサービスは録音をクラウドにアップロードします。拾音なら、録音から文字起こし、話者の識別まで Mac の中で完結します。日本語の会議にも対応しています。',
      body: `
<h2>日本語の会議の文字起こし</h2>
<ul>
<li>日本語の会議は Whisper large-v3-turbo で文字起こしします。初めて日本語の会議を文字起こしするときに、モデル（約 570 MB）をダウンロードします。</li>
<li>録音中のリアルタイム文字起こしは、macOS に内蔵された端末上の音声認識を使います。</li>
<li>録音を始めて約 10 秒で、日本語・英語・中国語のどれかを自動で判定します。</li>
<li>議事録と文字起こしは日本語で作られ、PDF と Word には日本語のフォントが使われます。</li>
</ul>
<p class="note">アプリの表示は英語と繁体字中国語のみです。日本語の macOS では英語で表示されます。</p>

<h2>どこで処理されるか</h2>
${table(['処理', '場所', 'インターネット'], [
  ['録音', 'あなたの Mac', '不要'],
  ['録音中のリアルタイム文字起こし', 'あなたの Mac（macOS 内蔵の音声認識）', '不要'],
  ['文字起こし', 'あなたの Mac（whisper.cpp）', '最初にモデルをダウンロードするときだけ必要'],
  ['話者の識別、声の記憶', 'あなたの Mac', '不要'],
  ['議事録の作成、会議への質問', '選んだまとめツール', 'ChatGPT、Claude、API を使うときは必要。ローカルの Ollama、LM Studio、または「Don’t summarize」なら不要'],
])}
<p>ChatGPT、Claude、API で議事録を作るときに送られるのはテキストだけです（文字起こし、メモと用語集、会議のタイトルと日付）。録音ファイルは送られません。</p>

<h2>オンライン会議もボットなしで録音</h2>
<p>録音ソースを「Microphone + computer audio」にすると、Zoom、Teams、Google Meet の相手の声も録音されます。会議にボットを参加させる必要はなく、ほかの参加者の画面には何も表示されません。初めてコンピュータの音声を録音するとき、macOS が「システムオーディオ録音」の許可を求めるので、許可してください。</p>

<h2>完全にオフラインで使う</h2>
<ol>
<li>インターネットにつながっているときに、文字起こしのモデルをダウンロードしておきます。</li>
<li>Ollama か LM Studio をインストールして、ローカルのモデルをダウンロードします。</li>
<li>拾音の Settings → Summarizer で「OpenAI-compatible API」を選び、Ollama か LM Studio のローカルのアドレスを入力します（キーは不要）。</li>
<li>または「Don’t summarize」を選んで、文字起こしだけを残します。</li>
</ol>
<p class="note">ローカルのモデルで作る議事録の質は、モデルの大きさと Mac のメモリによって変わります。まず 1 回の会議で試してください。</p>

<h2>データの保存場所</h2>
<p>録音、文字起こし、議事録、声のデータはすべて Mac の「~/Library/Application Support/Shiyin」に保存されます。拾音は MIT ライセンスのオープンソースで、コードは <a href="${GITHUB}">GitHub</a> で確認できます。</p>
`,
      faq: [
        ['料金はかかりますか？', '拾音は無料です。議事録の作成には、手持ちの ChatGPT や Claude のサブスクリプション、または API の利用料がかかります。「Don’t summarize」を選べば費用はかかりません。'],
        ['必要な環境は？', 'macOS 26 以降、Apple シリコン（M1 以降）、メモリ 8 GB 以上です。Windows 版は開発中です。'],
        ['話者は分けられますか？', 'はい。話者を自動で識別し、名前を付けると声を覚えて次の会議から自動で名前を入れます。間違いは統合や行ごとの修正で直せます。'],
        ['アプリは日本語で表示されますか？', 'いいえ。アプリの表示は英語と繁体字中国語のみです。会議の言語は日本語を選べます。'],
      ],
    },
  },

  {
    slug: 'chatgpt-claude-meeting-notes',
    published: '2026-10-21',
    updated: '2026-10-21',
    zh: {
      title: '用 ChatGPT、Claude 訂閱整理會議記錄，不用再多付一份月費｜拾音 Shiyin',
      description: '已經有 ChatGPT 或 Claude 的訂閱，就不一定要再訂會議記錄工具。這篇比較手動把逐字稿貼給 ChatGPT，和用拾音自動整理、每一條附時間點的差別。',
      h1: '用自己的 ChatGPT、Claude 訂閱整理會議記錄',
      lead: '很多人已經有 ChatGPT 或 Claude 的訂閱。與其再付一份會議記錄工具的月費，可以讓它們直接幫你整理。這篇比較手動貼逐字稿，和讓拾音自動整理的差別。',
      body: `
<h2>手動把逐字稿貼給 ChatGPT 的麻煩</h2>
<ul>
<li>要先有逐字稿。錄音、轉逐字稿要另外處理。</li>
<li>一小時的會議逐字稿很長，有時要分段貼。</li>
<li>整理出來的內容沒有時間點，看到可疑的地方，要回頭在錄音裡找。</li>
<li>每次都要重打一樣的提示詞，格式也不一定每次一樣。</li>
<li>人名、專有名詞聽錯了，AI 也只能照錯的寫。</li>
</ul>

<h2>拾音怎麼用你的訂閱</h2>
<p>Claude Code（claude 指令）和 Codex（codex 指令）是 Anthropic、OpenAI 官方的命令列工具，登入後用的是你的 Claude、ChatGPT 訂閱。拾音錄完、轉錄完以後，把逐字稿、你的筆記和詞彙表交給它，照範本整理成會議紀錄：</p>
<ul>
<li><strong>摘要、決議、待辦、未決問題</strong>，每一條後面都有時間點，點了從錄音那裡開始播。</li>
<li><strong>待確認</strong>：AI 不確定的人名、專有名詞、負責人和期限會列出來問你，你回答後再依答案修正紀錄，不會自己亂猜。</li>
<li><strong>範本</strong>：一般會議、一對一、訪談、課堂／演講、腦力激盪，也可以自己做。</li>
<li><strong>對會議提問</strong>：例如「預算最後決定多少？」，回答附時間點，也可以一次問所有會議。</li>
</ul>

<h2>設定步驟</h2>
<ol>
<li>照官方說明安裝 Claude Code 或 Codex，在終端機登入一次。</li>
<li>打開拾音的設定 → 整理工具，選 Claude Code 或 Codex。</li>
<li>之後每場會議停止錄音，就會自動整理。</li>
</ol>
<p>沒有訂閱也可以用 API key（Anthropic、OpenAI、Gemini，或 Groq、OpenRouter 這類 OpenAI 相容的服務），或本機的 Ollama、LM Studio。</p>

<h2>會送出哪些資料、要多少錢</h2>
<p>送給整理工具的只有文字：逐字稿、你的筆記和詞彙表、會議標題和日期，錄音檔不會送出去。拾音本身免費；整理用到的是你訂閱方案的額度，或 API 的用量。各方案能用多少，以 OpenAI、Anthropic 當時的規定為準。</p>
<p>不想讓任何文字離開 Mac 的話，看<a href="/guides/offline-transcription-mac/">離線轉錄</a>這篇。</p>
`,
      faq: [
        ['一定要訂閱嗎？', '不用。可以用 API key 按用量付費、用本機模型，或選「不整理」只留逐字稿，完全免費。'],
        ['整理得好不好？', '看你選的模型。拾音會把不確定的地方列在「待確認」，每一條也都有時間點可以回頭聽，方便你檢查。'],
        ['可以改整理的格式嗎？', '可以。設定裡的範本可以修改，也可以自己做新的範本。'],
        ['會議紀錄可以改嗎？', '可以直接編輯，每次修改都會留版本，可以復原。'],
      ],
    },
    en: {
      title: 'Meeting Notes with the ChatGPT or Claude Subscription You Already Have | Shiyin',
      description: 'You may not need another meeting-notes subscription. Compare pasting a transcript into ChatGPT by hand with letting Shiyin write timestamped notes through Claude Code or Codex.',
      h1: 'Meeting notes with your own ChatGPT or Claude subscription',
      lead: 'Plenty of people already pay for ChatGPT or Claude. Instead of adding another monthly plan for a meeting note taker, you can have them write the notes. Here is how pasting a transcript by hand compares with letting Shiyin do it.',
      body: `
<h2>The trouble with pasting a transcript into ChatGPT</h2>
<ul>
<li>You need a transcript first, which means recording and transcribing somewhere else.</li>
<li>An hour-long transcript is long, and sometimes has to be pasted in parts.</li>
<li>The notes come back without timestamps, so checking a doubtful line means hunting through the recording.</li>
<li>You type the same prompt every time, and the format drifts.</li>
<li>If a name or term was misheard, the AI repeats the mistake.</li>
</ul>

<h2>How Shiyin uses your subscription</h2>
<p>Claude Code (the <code>claude</code> command) and Codex (the <code>codex</code> command) are the official command-line tools from Anthropic and OpenAI. Once you sign in, they run on your Claude or ChatGPT plan. After a meeting is transcribed, Shiyin hands them the transcript, your notes and your glossary, and they write the notes from a template:</p>
<ul>
<li><strong>Summary, decisions, action items and open questions</strong>, each with a timestamp that plays the original audio.</li>
<li><strong>To confirm</strong>: names, terms, owners and due dates the AI isn’t sure about are listed for you to answer, and the notes are revised from your answers instead of guessed.</li>
<li><strong>Templates</strong>: General meeting, One-on-one, Interview, Lecture / talk, Brainstorm, or your own.</li>
<li><strong>Ask your meetings</strong>: “What budget did we settle on?” gets an answer with timestamps, across one meeting or all of them.</li>
</ul>

<h2>Setup</h2>
<ol>
<li>Install Claude Code or Codex following the official instructions and sign in once in Terminal.</li>
<li>In Shiyin, open Settings → Summarizer and choose Claude Code or Codex.</li>
<li>From then on, notes are written automatically when you stop recording.</li>
</ol>
<p>No subscription? Use an API key (Anthropic, OpenAI, Gemini, or OpenAI-compatible services such as Groq and OpenRouter), or a local model in Ollama or LM Studio.</p>

<h2>What is sent, and what it costs</h2>
<p>Only text goes to the summarizer: the transcript, your notes and glossary, and the meeting title and date. The audio never does. Shiyin itself is free; writing notes uses your plan’s allowance or your API usage, subject to whatever limits OpenAI and Anthropic set for your plan.</p>
<p>To keep every word on your Mac, see <a href="/en/guides/offline-transcription-mac/">local meeting transcription</a>.</p>
`,
      faq: [
        ['Do I need a subscription?', 'No. You can pay per use with an API key, run a local model, or choose “Don’t summarize” and keep just the transcript for free.'],
        ['How good are the notes?', 'That depends on the model. Shiyin lists what it isn’t sure about under “To confirm”, and every item has a timestamp so you can check it against the recording.'],
        ['Can I change the format?', 'Yes. Edit the templates in Settings or make your own.'],
        ['Can I edit the notes?', 'Yes. Edit them directly; every change is versioned and can be undone.'],
      ],
    },
  },

  {
    slug: 'meeting-notes-apps-compared',
    published: '2026-10-21',
    updated: '2026-10-21',
    zh: {
      title: '會議記錄工具比較 2026：拾音、Notta、Otter、Granola、雅婷逐字稿、Meetily｜拾音 Shiyin',
      description: '整理幾款常見的會議記錄、逐字稿工具：價格、錄音在哪裡轉、要不要會議機器人、中文支援和平台，也寫出拾音不適合的情況。2026 年 10 月查證。',
      h1: '會議記錄工具比較：拾音和其他常見工具差在哪',
      lead: '選會議記錄工具，最常卡在三件事：錄音能不能上傳、中文夾英文轉得準不準、要不要再付一份月費。下面整理幾款常見工具的差別，也寫出拾音不適合的情況。',
      body: `
<h2>先說結論</h2>
<p>在 Mac 上開會、錄音不想上傳、會議常常中文夾英文，拾音是免費的選擇：錄音、轉錄、分說話者都在本機，整理用你自己的 ChatGPT 或 Claude 訂閱。如果要在手機上錄、沒有 Mac，或會議裡常講台語，雲端服務會比較合適。</p>

<h2>一覽表</h2>
${table(['工具', '價格', '錄音在哪裡轉', '會議機器人', '中文', '平台'], [
  ['拾音', '免費，MIT 開源', '你的 Mac', '不用', '支援，針對台灣中文夾英文', 'Mac（macOS 26、Apple Silicon）；Windows 開發中'],
  ['Notta', '免費每月 120 分鐘（每次最多 3 分鐘）；Pro 年繳每月 US$8.17', '雲端', '有，可錄 Zoom、Teams、Meet、Webex', '支援', '網頁、手機、電腦'],
  ['Otter', '免費每月 300 分鐘；Pro 年繳每月 US$8.33', '雲端', '有，會加入 Zoom、Teams、Meet', '支援', '網頁、手機'],
  ['Granola', '免費版只保留 30 天紀錄；Business 每月 US$14', '錄音串流到轉錄服務（Deepgram、AssemblyAI），不保留錄音', '不用', '有多語言支援', '電腦、iOS、Android'],
  ['雅婷逐字稿', '按時數購買或訂閱，見官網', '雲端', '用 Chrome 外掛錄 Google Meet', '支援，強項是台語', '網頁、iOS、Chrome 外掛'],
  ['Meetily', '社群版免費（MIT）；Pro 年繳每月 US$10', '你的電腦', '不用', 'Whisper 支援的語言，含中文', 'macOS、Windows'],
])}
<p class="note">價格和功能取自各家的官網或說明文件，2026 年 10 月 21 日查證，之後可能會變，決定前請看各家官網。</p>

<h2>拾音的不同之處</h2>
<ul>
<li><strong>錄音不出 Mac</strong>：轉錄、說話者辨識、聲紋都在本機。整理時只送文字，也可以選本機的 Ollama、LM Studio，完全不連網（<a href="/guides/offline-transcription-mac/">離線轉錄</a>）。</li>
<li><strong>台灣中文夾英文</strong>：用 Breeze ASR 25，在中英夾雜的測試資料上，錯誤率比 Whisper large-v2 少約 56%（<a href="/guides/mandarin-english-transcription/">詳細比較</a>）。</li>
<li><strong>說話者辨識免費</strong>：自動分出說話者，記住聲音，下次自動帶名字；分錯了可以合併、改某一句。Meetily 的說話者辨識要付費版才有（<a href="${SRC.meetilyRepo}">Meetily 的說明</a>）。</li>
<li><strong>不用再付月費</strong>：整理用你已經有的 ChatGPT、Claude 訂閱（<a href="/guides/chatgpt-claude-meeting-notes/">怎麼設定</a>）。</li>
<li><strong>紀錄查得回原話</strong>：每一條都附時間點，AI 不確定的人名和期限會列在「待確認」問你。</li>
<li><strong>不用機器人</strong>：直接錄電腦的聲音，會議裡不會多一個筆記助理（<a href="/guides/record-online-meetings-mac/">錄線上會議的方法</a>）。</li>
</ul>

<h2>其他工具比較適合的情況</h2>
<ul>
<li><strong>要用手機錄，或沒有 Mac</strong>：Notta、Otter、雅婷逐字稿都有手機 app。</li>
<li><strong>會議裡常講台語</strong>：雅婷逐字稿支援台語，拾音不支援。</li>
<li><strong>要用 Windows</strong>：Meetily 有 Windows 版；拾音的 Windows 版還在開發。</li>
<li><strong>團隊要在網頁上一起看、一起改</strong>：雲端服務比較方便。</li>
<li><strong>Mac 比較舊</strong>：拾音要 macOS 26 和 Apple Silicon（M1 以上），Intel Mac 不能用。</li>
</ul>

<h2>資料來源</h2>
<ul>
<li>Notta：<a href="${SRC.notta}">價格方案</a></li>
<li>Otter：<a href="${SRC.otter}">價格方案</a></li>
<li>Granola：<a href="${SRC.granola}">價格方案</a>、<a href="${SRC.granolaSecurity}">安全性說明</a></li>
<li>雅婷逐字稿：<a href="${SRC.yating}">官網</a></li>
<li>Meetily：<a href="${SRC.meetily}">產品說明</a>、<a href="${SRC.meetilyRepo}">GitHub</a></li>
</ul>
`,
      faq: [
        ['拾音真的免費嗎？', '是。拾音免費、開源，沒有帳號也沒有用量限制。只有選擇用 ChatGPT、Claude 訂閱或 API 整理時，會用到你自己的額度。'],
        ['哪一個中文最準？', '我們沒有用同一批錄音測過所有工具，所以不排名次。拾音用的 Breeze ASR 25 在中英夾雜的測試資料上，錯誤率比 Whisper large-v2 少約 56%。最準的方法是拿你自己的一場會議試試。'],
        ['可以把其他工具的逐字稿匯進拾音嗎？', '可以匯入 srt、vtt、txt 逐字稿，拾音會照樣整理成會議紀錄。'],
        ['拾音會一直免費嗎？', '拾音是 MIT 授權的開源專案，原始碼在 GitHub，任何人都可以下載、修改。'],
      ],
    },
    en: {
      title: 'Open-Source Granola and Otter Alternatives for Mac, Compared (2026) | Shiyin',
      description: 'Shiyin, Meetily, Anarlog and Minutes compared with Granola and Otter: price, where transcription runs, speaker identification, meeting bots and platforms. Checked October 2026.',
      h1: 'Open-source alternatives to Granola and Otter for Mac',
      lead: 'Granola and Otter are the best-known AI meeting note takers, but both process audio in the cloud and charge for their full plans. These open-source apps record and transcribe on your own computer. Here is how they compare, including where Shiyin isn’t the best fit.',
      body: `
<h2>The short answer</h2>
<p>For free, local meeting notes on a Mac with automatic speaker identification, in English, Chinese or Japanese, Shiyin fits well. If you need Windows or many more languages, Meetily and Minutes run on more platforms. Anarlog adds hosted sync and collaboration on its paid plan.</p>

<h2>Open-source apps that run locally</h2>
${table(['App', 'Price', 'Transcription', 'Speaker identification', 'Notes written by', 'Platforms'], [
  ['Shiyin', 'Free (MIT)', 'On your Mac: Breeze ASR 25, or Whisper large-v3-turbo for Japanese', 'Automatic and free; remembers voices across meetings', 'Your Claude or ChatGPT subscription (Claude Code, Codex), an API key, or a local model', 'macOS 26, Apple Silicon; Windows in development'],
  ['Meetily', 'Community edition free (MIT); Pro $10/user/month billed annually', 'On your computer: Whisper or Parakeet', 'Pro only', 'Claude, Groq, OpenAI API or local Ollama', 'macOS, Windows; Linux from source'],
  ['Anarlog (formerly Hyprnote)', 'Free (MIT); Pro $14/person/month', 'On-device on the free plan; cloud transcription on Pro', 'Manual labels free; automatic on Pro', 'On-device models or your own API keys', 'Apple Silicon Mac'],
  ['Minutes', 'Free (MIT)', 'On your computer: Whisper or Parakeet', 'Yes (pyannote)', 'Any LLM; can use your Claude subscription through MCP', 'Mac, Windows'],
])}

<h2>The cloud tools they replace</h2>
${table(['App', 'Price', 'Where audio is processed', 'Meeting bot'], [
  ['Granola', 'Free with 30 days of history; Business $14/user/month', 'Streamed to transcription providers (Deepgram, AssemblyAI); audio isn’t kept', 'No'],
  ['Otter', 'Free with 300 minutes a month; Pro $8.33/month billed annually', 'Otter’s cloud', 'Yes, joins Zoom, Teams and Meet'],
])}
<p class="note">Prices and features come from each vendor’s own pricing page, documentation or llms.txt, checked on 21 October 2026. They change, so check the links below before deciding.</p>

<h2>Where Shiyin is different</h2>
<ul>
<li><strong>Speaker identification is free.</strong> Speakers are separated automatically, voices are remembered for later meetings, and you can merge speakers or reassign single lines.</li>
<li><strong>Built for Mandarin mixed with English.</strong> Breeze ASR 25 cuts the error rate by about 56% against Whisper large-v2 on a code-switching test set (<a href="${BREEZE}">model card</a>).</li>
<li><strong>Notes you can check.</strong> Every item has a timestamp that plays the original audio, and names or dates the AI isn’t sure about are listed under “To confirm” instead of guessed.</li>
<li><strong>Your own subscription.</strong> Notes are written by the Claude or ChatGPT plan you already pay for (<a href="/en/guides/chatgpt-claude-meeting-notes/">how it works</a>).</li>
<li><strong>No bot, no virtual audio driver.</strong> It records the Mac’s audio directly (<a href="/en/guides/record-online-meetings-mac/">how to record calls</a>).</li>
</ul>

<h2>When another app fits better</h2>
<ul>
<li><strong>You use Windows or Linux.</strong> Meetily and Minutes support Windows; Shiyin’s Windows version is still in development.</li>
<li><strong>You meet in languages other than English, Chinese or Japanese.</strong> Meetily and Minutes use Whisper or Parakeet, which cover many more languages.</li>
<li><strong>You have an Intel Mac or an older macOS.</strong> Shiyin needs macOS 26 on Apple Silicon.</li>
<li><strong>Your team wants shared, synced notes.</strong> Anarlog Pro and Granola are built for that.</li>
<li><strong>You record on your phone.</strong> Granola and Otter have mobile apps.</li>
</ul>

<h2>Sources</h2>
<ul>
<li>Meetily: <a href="${SRC.meetily}">llms.txt</a>, <a href="${SRC.meetilyRepo}">GitHub</a></li>
<li>Anarlog: <a href="${SRC.anarlog}">pricing</a></li>
<li>Minutes: <a href="${SRC.minutes}">website</a></li>
<li>Granola: <a href="${SRC.granola}">pricing</a>, <a href="${SRC.granolaSecurity}">security</a></li>
<li>Otter: <a href="${SRC.otter}">pricing</a></li>
</ul>
`,
      faq: [
        ['Is Shiyin really free?', 'Yes. It’s free and open source with no account and no usage limits. The only cost is your own AI subscription or API usage, if you choose to use one.'],
        ['Which is the most accurate?', 'We haven’t tested every app on the same recordings, so we don’t rank them. Try one of your own meetings in each.'],
        ['Can I import transcripts from another app?', 'Yes. Shiyin imports srt, vtt and txt transcripts and writes meeting notes from them.'],
        ['Does Shiyin work on Windows?', 'Not yet. A Windows version is in development.'],
      ],
    },
  },
];
