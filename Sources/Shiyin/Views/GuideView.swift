import SwiftUI

/// 使用說明（R10.4、v2 R14）。提到的按鈕、分頁、選單名稱都要和介面上的文字一致。
struct GuideView: View {
    @Environment(\.snapshotMode) private var snapshotMode

    private struct Step {
        let symbol: String
        let title: String
        let body: String
    }

    private let steps: [Step] = [
        Step(symbol: "record.circle", title: String(localized: "錄音或匯入"),
             body: String(localized: "按工具列的「錄音」（⌘R）開始錄；點按鈕旁的箭頭可以選錄音來源，線上會議請選「麥克風＋電腦聲音（線上會議）」，對方的聲音也會錄進來。已經有錄音檔的話，拖進視窗、按「匯入」（⌘O），或拖到 Dock 上的拾音圖示都可以。")),
        Step(symbol: "text.bubble", title: String(localized: "邊錄邊看、邊記"),
             body: String(localized: "錄音中畫面左邊是即時逐字稿，講完幾秒內就會出現；右邊「我的筆記」每換一行會自動記下當時的錄音時間。重要的地方按「標記重點」（⌘B），在其他 app 裡也可以按 ⌃⌥⌘B。需要中斷時按「暫停」（⇧⌘P），暫停的時間不會錄進去。")),
        Step(symbol: "waveform", title: String(localized: "正式轉錄、分辨說話者"),
             body: String(localized: "按「停止並轉錄」後，拾音會在這台 Mac 上重新轉錄（中文、英文用 Breeze ASR 25，日文用 Whisper large-v3-turbo），並分辨不同的說話者。等待的時候，會議紀錄會先用即時逐字稿整理一份草稿，正式版好了會自動換掉。逐字稿上點「說話者 1」可以改成人名，整場都會跟著改；拾音會記住這個人的聲音，之後的會議自動帶入名字（標「自動」，認錯了點一下就能改）。")),
        Step(symbol: "doc.text", title: String(localized: "看會議紀錄"),
             body: String(localized: "轉錄完會自動整理。「摘要」有重點、決議、待辦和未決問題；「詳細」依議題分段；每一條後面的時間點點一下，就從錄音那裡開始播放。右上角的範本選單可以換成一對一、訪談、課堂／演講、腦力激盪等格式，換完從「更多」選「重新整理會議紀錄」。想找某件事，可以在「提問」分頁直接問，例如「預算最後決定多少？」，回答會附上時間點；工具列的「提問」可以一次問所有會議。")),
        Step(symbol: "questionmark.bubble", title: String(localized: "回答待確認、修改"),
             body: String(localized: "「待確認」列出整理時沒把握的人名、專有名詞、負責人和期限。在「你的回答」欄位直接回答，按「送出」，會議紀錄就會依你的答案修正；看不懂或不重要的勾「略過」。也可以在「摘要」按「編輯」直接改。改錯了可以從「更多」選「復原上一版會議紀錄」。")),
        Step(symbol: "square.and.arrow.up", title: String(localized: "匯出、分享"),
             body: String(localized: "從「更多」選「匯出…」（⌘E）：會議紀錄可以存成 Markdown、PDF、Word；逐字稿可以存成 txt、srt、vtt、Word；錄音可以存成 m4a、wav；「整場會議（.shiyin）」可以傳給同事，對方雙擊就能匯入。「複製會議紀錄」可以直接貼到 Notion、Slack 或郵件。")),
    ]

    var body: some View {
        if snapshotMode {
            content
        } else {
            ScrollView { content }
        }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 30) {
            VStack(alignment: .leading, spacing: 8) {
                Text("拾音使用說明").font(.songti(30))
                Text("拾音把會議變成逐字稿和會議紀錄。錄音、轉錄、說話者辨識都在這台 Mac 上完成。")
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 22) {
                ForEach(Array(steps.enumerated()), id: \.offset) { index, step in
                    HStack(alignment: .firstTextBaseline, spacing: 14) {
                        Text("\(index + 1)")
                            .font(.callout.monospacedDigit().weight(.semibold))
                            // 淺色模式白字、深色模式深色字，在青瓷綠上都看得清楚
                            .foregroundStyle(Color(nsColor: .textBackgroundColor))
                            .frame(width: 24, height: 24)
                            .background(Color.celadon, in: Circle())
                            .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 5 }
                        VStack(alignment: .leading, spacing: 6) {
                            Label(step.title, systemImage: step.symbol)
                                .font(.songti(18))
                                .labelStyle(GuideTitleLabelStyle())
                            Text(step.body)
                                .fixedSize(horizontal: false, vertical: true)
                                .lineSpacing(3)
                        }
                    }
                }
            }

            GuideSection(title: String(localized: "找會議、播放")) {
                GuideBullet(String(localized: "側欄上方的「搜尋所有會議」會同時搜標題、逐字稿、會議紀錄和你的筆記，點結果就跳到那一段。"))
                GuideBullet(String(localized: "點任何時間點或視窗底部的時間軸就從那裡播放，空白鍵播放或暫停；播放列右邊可以選 0.75×～2× 的速度。"))
                GuideBullet(String(localized: "逐字稿裡琥珀色的段落辨識信心比較低，值得回頭聽；時間軸上的三角形是待確認題目、圓點是你標的重點。"))
                GuideBullet(String(localized: "會議可以分資料夾：在會議上按右鍵（或「更多」）選「移到資料夾」→「新資料夾…」。側欄上方的「資料夾」可以切換只看某個資料夾；正在看某個資料夾時，新錄的和匯入的會議都會放進去。資料夾按右鍵可以重新命名或刪除（會議會保留）。"))
                GuideBullet(String(localized: "刪除的會議會先放進側欄最下面的「垃圾桶」，30 天內都可以還原。"))
                GuideBullet(String(localized: "說話者分得太少（人多、吵雜、手機放在桌上遠距收音）時，從「更多」選「重新辨識說話者…」，告訴拾音大約幾個人。只會重新分辨誰在說話，逐字稿的文字不會變。錄音中（會議標題右邊的「人數」）或匯入時也可以先選。"))
                GuideBullet(String(localized: "說話者分錯了：同一個人被分成兩位，點說話者的名字選「合併到…」；某幾句分錯人，在逐字稿按右鍵選「這句是誰說的」。改錯了可以從「更多」選「復原說話者的調整」。"))
                GuideBullet(String(localized: "開 Zoom、Teams、Google Meet 等線上會議時，拾音會提醒你開始錄音；會議好像結束時提醒停止。不想被某個 app 提醒，在提醒上按「不要再提醒」。"))
                GuideBullet(String(localized: "遇到問題，可以從「輔助說明」選單選「回報問題…」，會附上版本和環境（不含會議內容）。"))
            }

            GuideSection(title: String(localized: "設定（⌘,）")) {
                GuideBullet(String(localized: "「整理工具」：選 Claude、Codex（需要先安裝並登入對應的 CLI），或填 Anthropic、OpenAI 的 API key。覺得整理太慢，可以在「模型」填比較快的模型，例如 sonnet。"))
                GuideBullet(String(localized: "「範本」：內建五種範本；也可以複製一份來改，或自己新增段落與指示。"))
                GuideBullet(String(localized: "「詞彙表」：加入系統名、產品名、人名的正確寫法。這對專有名詞的正確率影響很大。"))
                GuideBullet(String(localized: "「說話者」：拾音認識的人和他們的聲紋，可以改名、刪除，或關掉記住聲音的功能。"))
                GuideBullet(String(localized: "「錄音」：選錄音來源、麥克風，以及要不要即時逐字稿和說話者辨識。「正式轉錄」可以選 Breeze ASR 25（建議，會參考詞彙表）或 macOS 內建（不用下載模型、速度快，但英文專有名詞比較容易錯）。"))
                GuideBullet(String(localized: "「模型與儲存空間」：下載或刪除模型、看錄音佔用的空間。"))
                GuideBullet(String(localized: "「進階」：填更新網址、檢查更新；有新版時拾音會詢問，同意後自動下載、驗證並重新打開。也可以從「拾音」選單選「檢查更新…」。"))
            }

            GuideSection(title: String(localized: "資料與隱私")) {
                GuideBullet(String(localized: "錄音、逐字稿和會議紀錄都只存在這台 Mac 上（~/Library/Application Support/Shiyin），不會被 iCloud 同步。"))
                GuideBullet(String(localized: "即時逐字稿、正式轉錄、說話者辨識都在本機進行，不需要網路。"))
                GuideBullet(String(localized: "整理、回答待確認、提問、猜說話者時，會把逐字稿文字、你的筆記和詞彙表送到你選的整理工具，錄音檔不會送出。選「不整理」就完全離線。"))
                GuideBullet(String(localized: "聲紋（說話者的聲音特徵，一組數字，不是錄音）只存在這台 Mac，不會放進匯出的 .shiyin 封包。"))
                GuideBullet(String(localized: "API key 存在 macOS 鑰匙圈，不會寫進任何檔案。"))
                // 匿名使用統計（design §30.5）：有統計網址的版本才寫
                if UsageStats.isOffered {
                    GuideBullet(String(localized: "每天送一份匿名的使用統計（版本、系統、錄音和轉錄的分鐘數、用了哪些功能、失敗次數），不含任何會議內容。在設定 → 進階可以關掉、看實際送出的內容。"))
                }
            }

            GuideSection(title: String(localized: "系統需求")) {
                Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 18, verticalSpacing: 9) {
                    requirement(String(localized: "晶片"), String(localized: "Apple Silicon（M1 以上）"))
                    requirement(String(localized: "系統"), String(localized: "macOS 26 以上"))
                    requirement(String(localized: "記憶體"), String(localized: "8 GB 以上。轉錄加說話者辨識時最高約 2.9 GB，長錄音分段轉錄，會議長短影響不大（4 小時的會議 2.9 GB，M3 Pro 實測）"))
                    requirement(String(localized: "磁碟空間"), String(localized: "約 1.7 GB 給模型（轉錄 1.66 GB、說話者辨識 21 MB；開日文會議另外 574 MB、自動偵測語言 60 MB），另外加上錄音：轉錄完會壓縮成 m4a，每小時約 10～30 MB"))
                    requirement(String(localized: "先安裝"), String(localized: "不需要另外安裝任何工具。要整理會議紀錄的話，安裝並登入 claude 或 codex，或準備 Anthropic、OpenAI 的 API key"))
                    requirement(String(localized: "網路"), String(localized: "第一次使用下載模型時需要（約 1.7 GB）；整理會議紀錄時需要。錄音、轉錄不需要"))
                }
            }

            GuideSection(title: String(localized: "快捷鍵")) {
                Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 18, verticalSpacing: 7) {
                    shortcut("⌘R", String(localized: "開始或停止錄音"))
                    shortcut("⌃⌥⌘R", String(localized: "在其他 app 裡也能開始或停止錄音（可以在設定 → 錄音關掉）"))
                    shortcut("⇧⌘P", String(localized: "暫停或繼續錄音"))
                    shortcut("⌘B", String(localized: "錄音中標記重點"))
                    shortcut("⌃⌥⌘B", String(localized: "錄音中在其他 app 裡也能標記重點"))
                    shortcut("⌘O", String(localized: "匯入"))
                    shortcut("⌘E", String(localized: "匯出"))
                    shortcut(String(localized: "空白鍵"), String(localized: "播放或暫停"))
                    shortcut(String(localized: "⌘1～⌘6"), String(localized: "切換摘要、詳細、逐字稿、待確認、筆記、提問"))
                    shortcut("⌘F", String(localized: "搜尋逐字稿（在「逐字稿」）"))
                    shortcut("⌘,", String(localized: "設定"))
                    shortcut("⌘?", String(localized: "打開這份說明"))
                }
            }

            Text("之後隨時可以從「輔助說明」選單（⌘?）或選單列的拾音圖示，再打開這份說明。")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .padding(36)
        .frame(maxWidth: 680, alignment: .leading)
        .frame(maxWidth: .infinity, alignment: .leading)
        .textSelection(.enabled)
    }

    private func requirement(_ label: String, _ value: String) -> some View {
        GridRow {
            Text(label).foregroundStyle(.secondary)
            Text(value).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func shortcut(_ keys: String, _ action: String) -> some View {
        GridRow {
            Text(keys).monospaced().foregroundStyle(Color.celadon)
            Text(action)
        }
    }
}

private struct GuideTitleLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 8) {
            configuration.icon.foregroundStyle(Color.celadon).font(.body)
            configuration.title
        }
    }
}

private struct GuideSection<Content: View>: View {
    let title: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.songti(18))
            content
        }
    }
}

private struct GuideBullet: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text("•").foregroundStyle(.secondary)
            Text(text).fixedSize(horizontal: false, vertical: true).lineSpacing(3)
        }
    }
}
