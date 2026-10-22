// 網站上所有的文字（中文、英文、日文）。這裡的 HTML 是自己寫的，會原樣輸出；從 GitHub 讀來的版本說明另外跳脫。
// 用詞跟 app 一致：整理工具、待確認、逐字稿、標記重點、不整理。

export const GITHUB = 'https://github.com/juinwei7/Shiyin';
export const KOFI = 'https://ko-fi.com/weiwei_chang';
const AMI = 'https://groups.inf.ed.ac.uk/ami/corpus/';

export const content = {
  zh: {
    lang: 'zh-Hant',
    label: '中文',
    ogLocale: 'zh_TW',
    prefix: '',
    images: '/images',
    og: 'og.png',
    notesLang: 'zh',
    appName: '拾音',
    meta: {
      title: '拾音 Shiyin｜Mac 會議記錄、逐字稿 app，免費開源、錄音不上傳',
      description: '在 Mac 上錄音、轉逐字稿、整理成會議紀錄。Zoom、Meet、Teams 不用機器人也錄得到，中文夾英文也轉得準，錄音不會上傳。每一條紀錄都附時間點，點一下就聽得到原話。免費、開源。',
      changelogTitle: '版本說明｜拾音 Shiyin',
      changelogDescription: '拾音每一版改了什麼，最新的在最上面。',
      notFoundTitle: '找不到這個頁面｜拾音 Shiyin',
    },
    seo: {
      appName: '拾音 Shiyin',
      category: '會議記錄、逐字稿',
      features: ['錄音中即時逐字稿', '錄麥克風和電腦聲音（Zoom、Google Meet、Teams），不用機器人進會議', '在 Mac 本機轉錄，錄音不上傳', '台灣中文夾英文的轉錄（Breeze ASR 25）', '說話者辨識、記住聲紋', '用自己的 Claude、ChatGPT 訂閱或 API 整理會議紀錄', '每一條紀錄附時間點，點了聽原話', '對會議提問', '中文、英文、日文會議'],
    },
    nav: { features: '功能', privacy: '隱私', changelog: '版本說明', languages: '語言', label: '網站' },
    intro: {
      iconAlt: '拾音圖示',
      h1: 'Mac 上的會議紀錄 app',
      lead: '錄音、轉錄、整理成會議紀錄，都在你的 Mac 上完成。<br>每一條紀錄都附時間點，點一下就聽得到原話。',
      download: (v) => `下載拾音 ${v}`,
      small: '免費、開源，需要 macOS 26 和 Apple Silicon',
      star: '在 GitHub 點星',
      downloads: (n) => `已下載 ${n} 次`,
    },
    video: {
      file: 'zh',
      h2: '看 52 秒的介紹影片',
      link: '看 52 秒的介紹影片',
      name: '拾音 Shiyin 介紹影片',
      description: '52 秒看懂拾音：按下錄音，邊講邊出逐字稿；停止後自動整理成會議紀錄、分出說話者，沒把握的地方先問你，還可以直接問會議，答案附時間點。',
    },
    shots: {
      summary: '拾音的會議紀錄畫面：摘要、決議、待辦，每一條後面都有時間點',
      recording: '錄音中的畫面：左邊是即時逐字稿，右邊是我的筆記',
      transcript: '逐字稿畫面：每一段都標上說話者和時間點',
      credit: `逐字稿截圖的示範錄音取自 <a href="${AMI}">AMI Meeting Corpus</a>（ES2004a，CC BY 4.0）`,
    },
    live: {
      h2: '開會時，邊錄邊看逐字稿',
      ps: [
        '按「錄音」就開始，講完幾秒內逐字稿就會出現。線上會議把錄音來源選「麥克風＋電腦聲音」，Zoom、Meet、Teams 對方的聲音也會錄進來，不用派機器人加入會議。開線上會議時，拾音會提醒你要不要開始錄音。麥克風沒收到聲音、聲音太小或背景太吵時，錄音畫面也會提醒你。',
        '右邊可以做筆記，每換一行會自動記下當時的錄音時間；重要的地方按「標記重點」，在其他 app 裡按 ⌃⌥⌘B 也可以。整理時這些都會被當成重點。',
      ],
    },
    transcribe: {
      h2: '停止後，在 Mac 上正式轉錄',
      ps: [
        '停止錄音後，拾音會在你的 Mac 上重新轉錄一次，比即時逐字稿準，並分出不同的說話者。把「說話者 1」改成人名，整場都會跟著改；拾音會記住這個人的聲音，之後的會議自動帶入名字。人多、吵雜時分錯了也好改：可以先告訴拾音大約幾個人，也可以把兩位說話者合併成一位，或直接改某一句是誰說的。',
        '中文和英文用 Breeze ASR 25，針對台灣中文和中英夾雜調過，「deploy」「Redis」這種詞不會被聽成別的字；日文會議改用 Whisper。產品名、人名可以先加進詞彙表，轉錄時會參考。',
      ],
    },
    notes: {
      h2: '整理成會議紀錄，不確定的地方會問你',
      ps: [
        '停止錄音後，先用即時逐字稿整理出一份草稿，正式轉錄完自動換成正式版：摘要、決議、待辦和未決問題。每一條後面都有時間點，覺得不對就點一下，從錄音那裡開始聽。',
        'AI 不確定的人名、專有名詞、負責人和期限，會列在「待確認」讓你回答，回答後再依你的答案修正紀錄。逐字稿裡可能聽錯的字也會標出來，你按「接受」才會改。',
        '想找某件事，可以直接問會議：「預算最後決定多少？」回答會附上時間點；也可以一次問所有會議。',
      ],
      note: '範本有一般會議、一對一、訪談、課堂／演講、腦力激盪，也可以自己做。紀錄可以直接編輯，每次修改都留版本。',
    },
    privacy: {
      h2: '錄音不會離開你的 Mac',
      lead: '轉錄和說話者辨識都在本機完成，不需要網路。只有整理會議紀錄、對會議提問時，文字會送到你選的整理工具。',
      local: { h3: '留在你的 Mac', items: ['錄音檔', '轉錄、說話者辨識', '記住的說話者聲音', '所有會議和紀錄'] },
      sent: { h3: '送到你選的整理工具', items: ['逐字稿文字', '你的筆記、詞彙表', '會議標題、日期'] },
      tools: '整理工具可以選 Claude Code 或 Codex，直接用你已經有的 Claude、ChatGPT 訂閱；也可以用 Anthropic、OpenAI 的 API，或選「不整理」只留逐字稿。',
    },
    download: {
      h2: '下載',
      version: '版本',
      whatsNew: '這一版改了什麼',
      rows: [
        ['系統', 'macOS 26 以上，Apple Silicon（M1 以上）'],
        ['記憶體', '8 GB 以上'],
        ['磁碟空間', '轉錄模型約 1.7 GB，第一次使用時下載（下載好之前先用 macOS 內建辨識）；日文會議另外約 570 MB'],
        ['費用', '免費，MIT 授權開源'],
      ],
      button: (file) => `下載 ${file}`,
      firstOpen: '第一次打開：',
      starAsk: '拾音是個人的開源專案。覺得好用的話，到 GitHub 點一顆星，能讓更多人看到它；也可以請我喝杯咖啡。',
      coffee: '請我喝杯咖啡',
      steps: [
        '打開 DMG，把拾音拖進「應用程式」。',
        '第一次打開時 macOS 會擋下來，因為拾音沒有 Apple 開發者簽章。',
        '到「系統設定 → 隱私權與安全性」，按「仍要打開」。',
      ],
      note: '之後有新版時，拾音會在 app 裡問你要不要更新。',
      windows: {
        h3: 'Windows 版',
        button: '下載 Windows 版',
        arm: 'ARM 的電腦（例如 Snapdragon）下載這個',
        note: (version) => `Windows ${version}，要 Windows 11（x64 或 ARM）。第一次執行時如果出現「Windows 已保護您的電腦」，按「其他資訊」→「仍要執行」。`,
        sac: '開著「智慧型應用程式控制」時，沒有簽章的 app 會被直接擋下、沒有「仍要執行」可以按：到「Windows 安全性 → 應用程式與瀏覽器控制 → 智慧型應用程式控制設定」關掉它，或等有正式簽章的版本。',
      },
    },
    updates: { h2: '最近更新', all: '所有版本' },
    faq: {
      h2: '常見問題',
      items: [
        ['要付費嗎？', '拾音免費。整理會議紀錄用的是你自己的 Claude、ChatGPT 訂閱或 API 額度；選「不整理」就只有逐字稿，不會有任何費用。'],
        ['需要網路嗎？', '錄音、轉錄、說話者辨識都不需要。第一次下載轉錄模型、整理會議紀錄和對會議提問時需要。'],
        ['支援哪些語言？', '介面有繁體中文和英文；會議可以是中文、英文或日文，開始錄音後會自動判斷。'],
        ['可以轉錄已經錄好的檔案嗎？', '可以。把錄音或影片拖進視窗就會轉錄，也可以匯入 srt、vtt、txt 逐字稿。'],
        ['有 Windows 版或手機版嗎？', '目前只有 Mac 版。Windows 版正在開發，功能會跟 Mac 版一樣。'],
      ],
    },
    footer: { tagline: '拾音 Shiyin，個人開源專案', sponsor: '贊助' },
    guides: {
      nav: '指南',
      title: '指南｜拾音 Shiyin',
      description: '用 Mac 做會議記錄、逐字稿的教學，以及拾音和其他會議記錄工具的比較。',
      h1: '指南',
      lead: '用 Mac 做會議記錄和逐字稿的方法，以及拾音跟其他工具的差別。',
      updated: (d) => `最後更新：${d}`,
      related: '其他指南',
      home: '更多說明',
      faq: '常見問題',
      cta: { h2: '試試拾音', p: '免費、開源，錄音不會離開你的 Mac。需要 macOS 26 和 Apple Silicon。', button: '下載拾音', more: '看所有功能' },
    },
    changelog: {
      h1: '版本說明',
      lead: '每一版改了什麼。有新版時，拾音會在 app 裡問你要不要更新。',
      follow: `想第一時間知道新版本，可以到 <a href="${GITHUB}">GitHub</a> 點星或按 Watch。`,
      name: '拾音',
      download: '下載',
      release: 'GitHub Release',
    },
    notFound: {
      h1: '找不到這個頁面',
      p: '網址可能打錯了，或這個頁面已經移走。',
      home: '回首頁',
      changelog: '看版本說明',
    },
    date: (iso) => formatDate(iso, 'zh'),
  },

  en: {
    lang: 'en',
    label: 'English',
    ogLocale: 'en_US',
    prefix: '/en',
    images: '/images/en',
    og: 'og-en.png',
    notesLang: 'en',
    appName: 'Shiyin',
    meta: {
      title: 'Shiyin: Free Meeting Notes App for Mac, Transcribed Locally',
      description: 'Record Zoom, Meet and Teams without a bot, transcribe on your Mac, and get meeting notes where every item has a timestamp that plays what was said. Recordings are never uploaded. Free and open source.',
      changelogTitle: 'Release notes | Shiyin',
      changelogDescription: 'What changed in each version of Shiyin, newest first.',
      notFoundTitle: 'Page not found | Shiyin',
    },
    seo: {
      appName: 'Shiyin',
      category: 'Meeting notes, transcription',
      features: ['Live transcript while recording', 'Records the microphone and computer audio (Zoom, Google Meet, Teams) with no bot', 'Local transcription on your Mac; recordings are never uploaded', 'Transcription tuned for Mandarin mixed with English (Breeze ASR 25)', 'Speaker identification with voiceprints', 'Meeting notes from your own Claude or ChatGPT subscription or API key', 'Every note has a timestamp that plays the original audio', 'Ask your meetings questions', 'English, Chinese and Japanese meetings'],
    },
    nav: { features: 'Features', privacy: 'Privacy', changelog: 'Release notes', languages: 'Language', label: 'Site' },
    intro: {
      iconAlt: 'Shiyin icon',
      h1: 'A meeting-notes app for your Mac',
      lead: 'Record, transcribe and write up meetings, all on your Mac. <br>Every note has a timestamp: click it to hear what was actually said.',
      download: (v) => `Download Shiyin ${v}`,
      small: 'Free and open source. Requires macOS 26 and Apple Silicon.',
      star: 'Star on GitHub',
      downloads: (n) => `${n} downloads`,
    },
    video: {
      file: 'en',
      h2: 'Watch the 52-second intro',
      link: 'Watch the 52-second intro',
      name: 'Shiyin intro video',
      description: 'Shiyin in 52 seconds: press Record and read the live transcript, then get meeting notes with speakers and timestamps. Shiyin asks when it isn’t sure, and you can ask the meeting questions.',
    },
    shots: {
      summary: 'Meeting notes in Shiyin: summary, decisions and action items, each with a timestamp',
      recording: 'Recording: the live transcript on the left, my notes on the right',
      transcript: 'The transcript: every segment labeled with its speaker and time',
      credit: `The demo meetings in these screenshots are excerpts from the <a href="${AMI}">AMI Meeting Corpus</a> (ES2004a, CC BY 4.0).`,
    },
    live: {
      h2: 'Read the transcript while you meet',
      ps: [
        'Click Record and the transcript appears a few seconds after people speak. For online meetings, set the source to “Microphone + computer audio” and the other side of Zoom, Meet or Teams is recorded too, with no bot joining your call. When an online meeting starts, Shiyin asks whether to record. If the microphone isn’t picking up sound, the sound is too quiet or the room is too noisy, the recording screen tells you.',
        'Take notes on the right: each new line is stamped with the recording time. Click Bookmark at important moments, or press ⌃⌥⌘B from any other app. Both are treated as key points when the notes are written.',
      ],
    },
    transcribe: {
      h2: 'When you stop, your Mac transcribes it properly',
      ps: [
        'After you stop, Shiyin transcribes the recording again on your Mac. It’s more accurate than the live transcript, and it tells the speakers apart. Rename “Speaker 1” once and the whole meeting follows; Shiyin remembers the voice and fills in the name in later meetings. Crowded or noisy room? Tell Shiyin roughly how many people there are, merge two speakers into one, or change who said a line.',
        'English and Chinese meetings use Breeze ASR 25, which is tuned for Taiwanese Mandarin mixed with English; Japanese meetings use Whisper. Add product names and people to the glossary and transcription takes them into account.',
      ],
    },
    notes: {
      h2: 'Meeting notes that ask when they’re unsure',
      ps: [
        'As soon as you stop, draft notes are written from the live transcript, then replaced when the final transcript is ready: a summary, decisions, action items and open questions. Each item has a timestamp: if something looks wrong, click it and listen from that point.',
        'Names, terms, owners and due dates the summarizer isn’t sure about are listed under “To confirm”. Answer them and the notes are revised. Words in the transcript that were probably misheard are marked too, and nothing changes until you click Accept.',
        'Looking for something? Ask the meeting, like “What budget did we settle on?”, and the answer comes with timestamps. You can ask all your meetings at once, too.',
      ],
      note: 'Templates: General meeting, One-on-one, Interview, Lecture / talk, Brainstorm, or your own. You can edit the notes directly, and every change is versioned.',
    },
    privacy: {
      h2: 'Your recordings stay on your Mac',
      lead: 'Transcription and speaker identification run locally and don’t need the internet. Only when the notes are written or you ask a meeting a question is text sent to the summarizer you choose.',
      local: { h3: 'Stays on your Mac', items: ['Recordings', 'Transcription and speaker identification', 'The voices of speakers Shiyin remembers', 'All your meetings and notes'] },
      sent: { h3: 'Sent to the summarizer you choose', items: ['The transcript text', 'Your notes and glossary', 'Meeting title and date'] },
      tools: 'The summarizer can be Claude Code or Codex, using the Claude or ChatGPT subscription you already have, or the Anthropic or OpenAI API. Or choose “Don’t summarize” and keep just the transcript.',
    },
    download: {
      h2: 'Download',
      version: 'Version',
      whatsNew: 'What’s new in this version',
      rows: [
        ['System', 'macOS 26 or later, Apple Silicon (M1 or later)'],
        ['Memory', '8 GB or more'],
        ['Disk space', 'About 1.7 GB of transcription models, downloaded on first use (macOS built-in recognition is used until then), plus about 570 MB for Japanese meetings'],
        ['Price', 'Free, open source under the MIT License'],
      ],
      button: (file) => `Download ${file}`,
      firstOpen: 'Opening it the first time:',
      starAsk: 'Shiyin is a personal open-source project. If you find it useful, a star on GitHub helps more people find it, or you can buy me a coffee.',
      coffee: 'Buy me a coffee',
      steps: [
        'Open the DMG and drag Shiyin to Applications.',
        'macOS blocks Shiyin the first time you open it, because it isn’t notarized by Apple.',
        'Go to System Settings → Privacy & Security and click Open Anyway.',
      ],
      note: 'When a new version is out, Shiyin asks you in the app whether to update.',
      windows: {
        h3: 'Windows',
        button: 'Download for Windows',
        arm: 'For ARM PCs (such as Snapdragon), download this one',
        note: (version) => `Windows ${version}, for Windows 11 (x64 or ARM). If “Windows protected your PC” appears the first time, click “More info” → “Run anyway”.`,
        sac: 'With Smart App Control on, unsigned apps are blocked outright, with no “Run anyway”. Turn it off in Windows Security → App & browser control → Smart App Control settings, or wait for a signed release.',
      },
    },
    updates: { h2: 'What’s new', all: 'All versions' },
    faq: {
      h2: 'Questions',
      items: [
        ['Does it cost anything?', 'Shiyin is free. Meeting notes are written with your own Claude or ChatGPT subscription or API credit. Choose “Don’t summarize” and you get just the transcript, at no cost.'],
        ['Does it need the internet?', 'Recording, transcription and speaker identification don’t. Downloading the transcription models the first time, writing the notes and asking your meetings questions do.'],
        ['Which languages does it support?', 'The interface is in English and Traditional Chinese. Meetings can be in English, Chinese or Japanese, and Shiyin detects which after you start recording.'],
        ['Can it transcribe a recording I already have?', 'Yes. Drag the audio or video file into the window and it’s transcribed. You can also import srt, vtt or txt transcripts.'],
        ['Is there a Windows or phone version?', 'Not yet. Shiyin is Mac only for now; a Windows version with the same features is in development.'],
      ],
    },
    footer: { tagline: 'Shiyin, a personal open-source project', sponsor: 'Sponsor' },
    guides: {
      nav: 'Guides',
      title: 'Guides | Shiyin',
      description: 'How to take meeting notes and transcribe meetings on a Mac, and how Shiyin compares with other meeting-notes tools.',
      h1: 'Guides',
      lead: 'How to take meeting notes and transcripts on a Mac, and how Shiyin differs from other tools.',
      updated: (d) => `Last updated: ${d}`,
      related: 'More guides',
      home: 'Guides',
      faq: 'Questions',
      cta: { h2: 'Try Shiyin', p: 'Free and open source. Your recordings never leave your Mac. Requires macOS 26 and Apple Silicon.', button: 'Download Shiyin', more: 'See all features' },
    },
    changelog: {
      h1: 'Release notes',
      lead: 'What changed in each version. When a new version is out, Shiyin asks you in the app whether to update.',
      follow: `To hear about new versions first, star or watch Shiyin on <a href="${GITHUB}">GitHub</a>.`,
      name: 'Shiyin',
      download: 'Download',
      release: 'GitHub Release',
    },
    notFound: {
      h1: 'Page not found',
      p: 'The address may be mistyped, or the page has moved.',
      home: 'Go to the home page',
      changelog: 'See the release notes',
    },
    date: (iso) => formatDate(iso, 'en'),
  },

  // app 沒有日文介面（日本的 macOS 會顯示英文），所以按鈕名稱照英文寫
  ja: {
    lang: 'ja',
    label: '日本語',
    ogLocale: 'ja_JP',
    prefix: '/ja',
    images: '/images/en',
    og: 'og-ja.png',
    notesLang: 'en',
    appName: '拾音',
    meta: {
      title: '拾音 Shiyin｜Mac の議事録・文字起こしアプリ（無料、ローカル処理）',
      description: 'Mac で録音して文字起こしし、議事録にまとめます。Zoom、Meet、Teams もボットなしで録音でき、録音はアップロードされません。議事録の各項目のタイムスタンプをクリックすれば実際の発言を聞けます。無料、オープンソース。',
      changelogTitle: 'リリースノート｜拾音 Shiyin',
      changelogDescription: '拾音の各バージョンの変更点です。新しいものが上にあります。',
      notFoundTitle: 'ページが見つかりません｜拾音 Shiyin',
    },
    seo: {
      appName: '拾音 Shiyin',
      category: '會議記錄、逐字稿',
      features: ['錄音中即時逐字稿', '錄麥克風和電腦聲音（Zoom、Google Meet、Teams），不用機器人進會議', '在 Mac 本機轉錄，錄音不上傳', '台灣中文夾英文的轉錄（Breeze ASR 25）', '說話者辨識、記住聲紋', '用自己的 Claude、ChatGPT 訂閱或 API 整理會議紀錄', '每一條紀錄附時間點，點了聽原話', '對會議提問', '中文、英文、日文會議'],
    },
    nav: { features: '機能', privacy: 'プライバシー', changelog: 'リリースノート', languages: '言語', label: 'サイト' },
    intro: {
      iconAlt: '拾音のアイコン',
      h1: 'Mac の議事録アプリ',
      lead: '録音、文字起こし、議事録の作成まで、すべて Mac の中で。<br>各項目のタイムスタンプをクリックすれば、実際の発言を聞けます。',
      download: (v) => `拾音 ${v} をダウンロード`,
      small: '無料、オープンソース。macOS 26 と Apple シリコンが必要です',
      star: 'GitHub でスター',
      downloads: (n) => `ダウンロード ${n} 回`,
    },
    video: {
      file: 'en',
      h2: '52 秒の紹介動画（英語）',
      link: '52 秒の紹介動画を見る',
      name: '拾音 Shiyin 紹介動画',
      description: '52 秒でわかる拾音：録音を押すと話しながら文字起こしが表示され、停止すると話者を分けた議事録が自動でまとまります。自信がないところは先に質問し、会議に直接質問することもできます。（英語版の動画です）',
    },
    shots: {
      summary: '拾音の議事録画面：要約、決定事項、ToDo の各項目にタイムスタンプが付いている',
      recording: '録音中の画面：左がリアルタイムの文字起こし、右が自分のメモ',
      transcript: '文字起こしの画面：段落ごとに話者と時間が付いている',
      credit: `画面のデモ会議は <a href="${AMI}">AMI Meeting Corpus</a>（ES2004a、CC BY 4.0）の抜粋です`,
    },
    live: {
      h2: '会議中は、文字起こしを見ながら録音',
      ps: [
        '「Record」を押すと録音が始まり、話してから数秒で文字起こしが表示されます。オンライン会議では録音ソースを「Microphone + computer audio」にすると、Zoom、Meet、Teams の相手の声も録音されます。会議にボットを参加させる必要はありません。オンライン会議が始まると、録音するかどうか拾音が確認します。マイクに音が入っていない、声が小さすぎる、周りがうるさすぎるときも、録音画面でお知らせします。',
        '右側にはメモを書けます。改行するたびにその時点の録音時間が記録され、大事な場面では「Bookmark」を押します。ほかのアプリを使っていても ⌃⌥⌘B で記録できます。議事録をまとめるときは、これらが重要なポイントとして扱われます。',
      ],
    },
    transcribe: {
      h2: '録音を止めたら、Mac で正式に文字起こし',
      ps: [
        '録音を止めると、拾音が Mac の中で録音をもう一度文字起こしします。リアルタイムの文字起こしより正確で、話者も聞き分けます。「Speaker 1」を名前に変えれば、会議全体に反映されます。拾音はその人の声を覚え、次の会議から自動で名前を入れます。人が多い会議や騒がしい場所で話者を間違えても、すぐに直せます。おおよその人数を拾音に伝えたり、2 人の話者を 1 人にまとめたり（Merge Into…）、ある発言の話者を変えたり（Who Said This Line?）できます。',
        '日本語の会議は Whisper large-v3-turbo で文字起こしします。英語と中国語の会議には、台湾華語と英語の混ざった会話に合わせて調整された Breeze ASR 25 を使います。製品名や人名を用語集に登録しておくと、文字起こしのときに参考にされます。',
      ],
    },
    notes: {
      h2: '議事録にまとめ、不確かな点は質問してくれる',
      ps: [
        '録音を止めるとすぐ、リアルタイムの文字起こしから下書きをまとめ、正式な文字起こしが終わると正式版に置き換えます。要約、決定事項、ToDo、未解決の課題に分かれています。各項目にはタイムスタンプが付いていて、気になったらクリックしてその場面から聞き直せます。',
        'AI が確信を持てない人名、固有名詞、担当者、期限は「To confirm」に一覧され、答えるとその内容で議事録が修正されます。聞き間違えた可能性のある語も文字起こしに印が付き、「Accept」を押すまで変更されません。',
        '探したいことがあれば、「予算はいくらに決まった？」のように会議に質問できます（「Ask」タブ）。答えにはタイムスタンプが付き、すべての会議にまとめて質問することもできます。',
      ],
      note: 'テンプレートは一般的な会議、1on1、インタビュー、講義・講演、ブレインストーミングのほか、自分で作ることもできます。議事録は直接編集でき、変更のたびに版が残ります。',
    },
    privacy: {
      h2: '録音は Mac の外に出ません',
      lead: '文字起こしと話者の識別は Mac の中で行われ、インターネット接続は不要です。議事録をまとめるときと会議に質問するときだけ、選んだまとめツールにテキストが送られます。',
      local: { h3: 'Mac に残るもの', items: ['録音ファイル', '文字起こし、話者の識別', '覚えた話者の声', 'すべての会議と議事録'] },
      sent: { h3: '選んだまとめツールに送られるもの', items: ['文字起こしのテキスト', '自分のメモ、用語集', '会議のタイトル、日付'] },
      tools: 'まとめツールに Claude Code か Codex を選べば、お使いの Claude や ChatGPT のサブスクリプションをそのまま使えます。Anthropic や OpenAI の API も使えます。「Don’t summarize」を選べば文字起こしだけを残します。',
    },
    download: {
      h2: 'ダウンロード',
      version: 'バージョン',
      whatsNew: 'このバージョンの変更点',
      rows: [
        ['システム', 'macOS 26 以降、Apple シリコン（M1 以降）'],
        ['メモリ', '8 GB 以上'],
        ['ディスク容量', '文字起こしモデル約 1.7 GB（初回使用時にダウンロード。それまでは macOS の音声認識を使います）。日本語の会議用にさらに約 570 MB'],
        ['表示言語', '英語、繁体字中国語（日本語表示には対応していません）'],
        ['価格', '無料、MIT ライセンスのオープンソース'],
      ],
      button: (file) => `${file} をダウンロード`,
      firstOpen: '初めて開くとき：',
      starAsk: '拾音は個人のオープンソースプロジェクトです。役に立ったら GitHub でスターを付けてください。もっと多くの人に届きます。コーヒーをおごっていただくこともできます。',
      coffee: 'コーヒーをおごる',
      steps: [
        'DMG を開き、拾音を「アプリケーション」フォルダにドラッグします。',
        '拾音は Apple の公証を受けていないため、初めて開くときは macOS にブロックされます。',
        '「システム設定」→「プライバシーとセキュリティ」で「このまま開く」をクリックします。',
      ],
      note: '新しいバージョンが出ると、アプリの中で更新するかどうか確認されます。',
      windows: {
        h3: 'Windows 版',
        button: 'Windows 版をダウンロード',
        arm: 'ARM の PC（Snapdragon など）はこちら',
        note: (version) => `Windows ${version}、Windows 11（x64 または ARM）が必要です。初回起動時に「Windows によって PC が保護されました」と表示されたら「詳細情報」→「実行」を押してください。`,
        sac: '「スマート アプリ コントロール」がオンのときは、署名のないアプリはそのままブロックされ、「実行」ボタンも出ません。「Windows セキュリティ」→「アプリとブラウザーの制御」→「スマート アプリ コントロールの設定」でオフにするか、署名付きのバージョンをお待ちください。',
      },
    },
    updates: { h2: '最近のアップデート', all: 'すべてのバージョン' },
    faq: {
      h2: 'よくある質問',
      items: [
        ['料金はかかりますか？', '拾音は無料です。議事録のまとめには、ご自身の Claude、ChatGPT のサブスクリプションか API の利用枠を使います。「Don’t summarize」を選べば文字起こしだけになり、費用はかかりません。'],
        ['インターネット接続は必要ですか？', '録音、文字起こし、話者の識別には不要です。文字起こしモデルの初回ダウンロード、議事録のまとめ、会議への質問には必要です。'],
        ['どの言語に対応していますか？', '会議は日本語、英語、中国語に対応し、録音を始めると自動で判別します。アプリの表示は英語と繁体字中国語で、日本語表示には対応していません。'],
        ['録音済みのファイルも文字起こしできますか？', 'できます。音声や動画のファイルをウインドウにドラッグすると文字起こしされます。srt、vtt、txt の文字起こしも読み込めます。'],
        ['Windows 版やスマートフォン版はありますか？', '今のところ Mac 版だけです。Mac 版と同じ機能の Windows 版を開発中です。'],
      ],
    },
    footer: { tagline: '拾音 Shiyin、個人のオープンソースプロジェクト', sponsor: '支援する' },
    guides: {
      nav: 'ガイド',
      title: 'ガイド｜拾音 Shiyin',
      description: 'Mac で議事録と文字起こしを作る方法と、拾音とほかの議事録ツールの比較。',
      h1: 'ガイド',
      lead: 'Mac で議事録と文字起こしを作る方法と、拾音とほかのツールの違い。',
      updated: (d) => `最終更新：${d}`,
      related: 'ほかのガイド',
      home: 'ガイド',
      faq: 'よくある質問',
      cta: { h2: '拾音を試す', p: '無料、オープンソース。録音は Mac から出ません。macOS 26 と Apple シリコンが必要です。', button: '拾音をダウンロード', more: 'すべての機能を見る' },
    },
    changelog: {
      h1: 'リリースノート',
      lead: '各バージョンの変更点です（英語で記載しています）。新しいバージョンが出ると、アプリの中で更新するかどうか確認されます。',
      follow: `新しいバージョンをいち早く知りたい方は、<a href="${GITHUB}">GitHub</a> でスターまたは Watch してください。`,
      name: '拾音',
      download: 'ダウンロード',
      release: 'GitHub Release',
    },
    notFound: {
      h1: 'ページが見つかりません',
      p: 'URL が間違っているか、ページが移動した可能性があります。',
      home: 'ホームに戻る',
      changelog: 'リリースノートを見る',
    },
    date: (iso) => formatDate(iso, 'ja'),
  },
};

export const LANGS = Object.keys(content);

// 發布日期用台灣時間；中文 2026-10-16，英文 October 16, 2026，日文 2026年10月16日
function formatDate(iso, lang) {
  const d = new Date(iso);
  if (lang === 'ja') {
    return new Intl.DateTimeFormat('ja-JP', { timeZone: 'Asia/Taipei', year: 'numeric', month: 'long', day: 'numeric' }).format(d);
  }
  if (lang === 'en') {
    return new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Taipei', year: 'numeric', month: 'long', day: 'numeric' }).format(d);
  }
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Taipei', year: 'numeric', month: '2-digit', day: '2-digit' }).format(d);
}
