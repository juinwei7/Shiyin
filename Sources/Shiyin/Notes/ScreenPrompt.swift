import Foundation
import ImageIO

/// 送給整理工具的一張會議中的畫面（design §83.5）：時間、辨識出的文字、圖檔（送圖片本身時用）
struct ScreenText: Equatable {
    var at: Double
    var text: String
    var file: URL?

    init(at: Double, text: String, file: URL? = nil) {
        self.at = at
        self.text = text
        self.file = file
    }

    init(_ document: MeetingDocument) {
        self.init(at: document.at ?? 0, text: document.screenText, file: document.fileURL)
    }
}

/// 送給整理工具的一張圖片：前面那一句說明（「[12:34] 的畫面」）和 JPEG
struct LLMImage: Equatable, Sendable {
    var label: String
    var jpeg: Data
}

/// 會議中的畫面（design §83.5、R109.6、R109.7、D188）：提示詞裡的區塊、整理和提問多的規則、要送的圖片
enum ScreenPrompt {
    /// 每張、全部最多幾個字（跟參考文件的 40,000 分開算）
    static let perScreen = 1_500
    static let limit = 20_000
    /// 一次最多送幾張圖片、長邊縮到多少（每張大約 1,600 token）
    static let maxImages = 20
    static let imagePixels = 1568

    // MARK: 提示詞裡的區塊（放在重點標記之後、參考文件之前）

    static func block(_ screens: [ScreenText], language: MeetingLanguage) -> String {
        var lines = [language.prompt(zh: "會議中的畫面（[時間] 是加入畫面時的錄音時間）：",
                                   en: "Screens shown during the meeting ([time] is the recording time when the screen was added):",
                                   ja: "会議中の画面（[時刻] は画面を追加したときの録音時刻）：")]  // l10n-skip
        var used = 0
        for screen in screens {
            lines.append("[\(TimeFormat.string(screen.at))]")
            let text = screen.text.trimmingCharacters(in: .whitespacesAndNewlines)
            if text.isEmpty {
                lines.append(language.prompt(zh: "（沒有辨識出文字）", en: "(no text recognized)", ja: "（文字は認識されませんでした）"))  // l10n-skip
                continue
            }
            let budget = min(perScreen, limit - used)
            if budget <= 0 {
                lines.append(language.prompt(zh: "（文字太長，沒有放進來）", en: "(text too long; not included)", ja: "（文字が長すぎるため省略）"))  // l10n-skip
                continue
            }
            let cut = String(text.prefix(budget))
            used += cut.count
            lines.append(cut.count < text.count ? cut + "…" : cut)
        }
        return lines.joined(separator: "\n")
    }

    // MARK: 規則（有畫面時才接在原本的規則後面；編號接著前面最後一條）

    static func notesRules(_ language: MeetingLanguage, start n: Int) -> String {
        language.prompt(zh: """
        \(n). 「會議中的畫面」是會議當時螢幕上的內容（分享的簡報、表格、照片），[時間] 是那時候的錄音時間；有附上圖片時，圖片前面的「[12:34] 的畫面」說明是哪一張。用它看懂當時在討論什麼，補上正確的數字、名稱、專有名詞。
        \(n + 1). 紀錄以會議中實際討論的內容為準。畫面上有、但會議沒有討論的，不要寫進紀錄；會議中的說法跟畫面不一致時，照會議中的說法寫，並列進 reviewItems 請使用者確認。
        \(n + 2). 紀錄用到畫面上的資訊時，在該條的句尾註明「（12:34 的畫面）」；time 仍填逐字稿裡討論到它的時間點。
        """, en: """
        \(n). "Screens shown during the meeting" are what was on screen at the time (shared slides, tables, photos); [time] is the recording time then. When images are attached, the text "Screen at [12:34]" before each image tells which one it is. Use them to understand what was being discussed and to get numbers, names and terms right.
        \(n + 1). The notes follow what was actually discussed in the meeting. Do not include content that is only on a screen and was not discussed; if what was said differs from a screen, write what was said and add it to reviewItems for the user to confirm.
        \(n + 2). When an item uses information from a screen, add "(screen at 12:34)" at the end of that item; time is still when it was discussed in the transcript.
        """, ja: """
        \(n). 「会議中の画面」は会議中に画面に映っていた内容（共有されたスライド、表、写真）で、[時刻] はそのときの録音時刻である。画像が添付されている場合、画像の前の「[12:34] の画面」がどの画面かを示す。議論の内容を理解し、数字・名前・専門用語を正しく書くために使う。
        \(n + 1). 議事録は会議で実際に議論された内容を基準とする。画面にあっても会議で議論されなかった内容は書かない。会議での発言と画面の内容が食い違う場合は、発言どおりに書き、reviewItems に入れてユーザーに確認してもらう。
        \(n + 2). 画面の情報を使った項目には、文末に「（12:34 の画面）」と書く。time は引き続き文字起こしで議論された時刻を入れる。
        """)
    }

    /// 對一場會議提問：接在最後一條後面
    static func askRule(_ language: MeetingLanguage, number n: Int) -> String {
        language.prompt(zh: """
        \(n). 「會議中的畫面」是會議當時螢幕上的內容（[時間] 是錄音時間），也可以用來回答；用到時寫出時間，例如「12:34 的畫面」，citations 的 time 填那個時間。
        """, en: """
        \(n). "Screens shown during the meeting" are what was on screen at the time ([time] is the recording time) and can also be used to answer; when you use one, mention its time, for example "the screen at 12:34", and use that time in the citation.
        """, ja: """
        \(n). 「会議中の画面」は会議中に画面に映っていた内容（[時刻] は録音時刻）で、回答に使ってもよい。使う場合は「12:34 の画面」のように時刻を書き、citations の time にその時刻を入れる。
        """)
    }

    /// 規則的編號接著前面最後一條（「12. …」這種行開頭的最大數字 + 1）
    static func nextRuleNumber(after text: String) -> Int {
        var last = 0
        for line in text.split(separator: "\n") {
            let digits = line.drop { $0 == " " }.prefix { $0.isNumber }
            if let n = Int(digits), line.drop(while: { $0 == " " }).dropFirst(digits.count).first == "." { last = max(last, n) }
        }
        return last + 1
    }

    // MARK: 圖片（D188）

    static func imageLabel(_ at: Double, language: MeetingLanguage) -> String {
        let time = TimeFormat.string(at)
        return language.prompt(zh: "[\(time)] 的畫面", en: "Screen at [\(time)]", ja: "[\(time)] の画面")  // l10n-skip
    }

    /// 最多 maxImages 張：超過時照時間平均挑（第一張、最後一張一定在）
    static func pick(_ screens: [ScreenText]) -> [ScreenText] {
        guard screens.count > maxImages else { return screens }
        let n = screens.count
        return (0..<maxImages).map { screens[Int((Double($0) * Double(n - 1) / Double(maxImages - 1)).rounded())] }
    }

    /// 讀圖檔、縮到 imagePixels、JPEG 0.8；讀不到的略過（文字照樣送）
    static func images(_ screens: [ScreenText], language: MeetingLanguage) -> [LLMImage] {
        pick(screens).compactMap { screen in
            guard let file = screen.file, let source = CGImageSourceCreateWithURL(file as CFURL, nil) else { return nil }
            let options: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                            kCGImageSourceCreateThumbnailWithTransform: true,
                                            kCGImageSourceThumbnailMaxPixelSize: imagePixels]
            guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary),
                  let jpeg = ScreenImages.jpeg(image, maxPixels: imagePixels, quality: 0.8) else { return nil }
            return LLMImage(label: imageLabel(screen.at, language: language), jpeg: jpeg)
        }
    }
}

extension LLMError {
    /// 模型不能看圖時 API、CLI 回的錯誤（design §83.5）：只看「失敗」「格式不對」這兩種，訊息提到 image、vision、圖片
    var rejectsImages: Bool {
        let message: String
        switch self {
        case .failed(let text, _), .badOutput(let text): message = text.lowercased()
        default: return false
        }
        return ["image", "vision", "multimodal", "圖片"].contains { message.contains($0) }  // l10n-skip
    }
}
