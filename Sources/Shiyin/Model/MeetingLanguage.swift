import Foundation

/// 手寫、調過的提示詞和標籤有三份（design §41.2、D140）：每種會議語言用其中一份（粵語用中文、韓文用英文）
enum PromptBase: String, Decodable {
    case zh, en, ja
}

/// 語言檔裡的一筆（design §41.1、D139）：一種會議語言的所有做法
struct LanguageProfile: Decodable {
    enum Tier: String, Decodable { case tuned, general }

    struct Names: Decodable { let zh: String; let en: String }
    struct Live: Decodable { let mac: String; let windows: String; let windowsConvert: String? }
    struct Transcription: Decodable {
        /// 預設的正式轉錄模型：breeze、turbo（Whisper large-v3-turbo）、qwen
        let model: String
        /// 跟著設定的「Breeze／Qwen3-ASR」
        let engineSetting: Bool
        /// whisper.cpp 的 language、Qwen3-ASR 的語言名稱；nil 是那個引擎不能用
        let whisper: String?
        let qwen: String?
        /// Qwen3-ASR 的輸出轉台灣繁體（design §32.5）
        let traditional: Bool
    }
    struct WhisperPrompt: Decodable {
        let base: String
        /// %@ 是用 separator 接起來的詞彙
        let glossary: String
        let separator: String
        let limit: Int
        /// 評估用的口語寫法（design §17.1）；沒有就用一般的寫法
        let natural: String?
        let naturalList: String?
    }
    struct OCR: Decodable { let mac: [String]; let windows: [String]; let windowsUserProfile: Bool }
    struct HTMLFonts: Decodable { let body: String; let heading: String }
    struct Fonts: Decodable {
        let typesetting: String
        let lang: String
        let titleMac: String
        let titleWindows: String
        let contentWindows: String
        let htmlMac: HTMLFonts
        let htmlWindows: HTMLFonts
    }
    struct Notes: Decodable { let notes: String; let correction: String }
    /// 簡轉繁之後的修正：from 換成 to，除非那個字在 keep 的詞裡（design §41.3）
    struct ScriptFix: Decodable { let from: String; let to: String; let keep: [String] }

    let code: String
    let name: String
    let uiName: Names
    let tier: Tier
    let base: PromptBase
    /// 一般語言：通用提示詞裡「用 … 寫」的語言名稱（英文）
    let outputName: String?
    /// 精調語言共用別的語言的提示詞時，整理、逐字稿校正的 system 最後補的說明（粵語）
    let transcriptNote: Notes?
    let locale: String
    let live: Live
    let transcription: Transcription
    /// 粵語：Qwen3-ASR 的簡體輸出轉繁體後，「系」「繫」多半是「係」、「曬」多半是「晒」
    let scriptFixes: [ScriptFix]?
    /// 用空格分詞（英文類）：詞彙表的分隔、上限、幻覺防線的字數算法
    let spaced: Bool
    let whisperPrompt: WhisperPrompt
    /// 自動偵測時 Whisper base 的語言代碼；nil 是不能偵測
    let detect: String?
    /// 語系：同一個語系的語言自動偵測時不互相切換（design §41.4）
    let family: String
    let ocr: OCR
    let fonts: Fonts
    /// 預設的說話者名稱，%lld 是編號
    let speaker: String
    /// 一般語言的字串表：鍵是英文那一份的字（含 %@、%lld）
    let strings: [String: String]?

    /// 套用 scriptFixes：一個字一個字看，落在 keep 的詞裡就不換
    func fixScript(_ text: String) -> String {
        guard let scriptFixes, !scriptFixes.isEmpty else { return text }
        var chars = Array(text)
        for fix in scriptFixes {
            guard let from = fix.from.first, let to = fix.to.first else { continue }
            let keeps = fix.keep.map(Array.init)
            for i in chars.indices where chars[i] == from {
                let kept = keeps.contains { word in
                    word.indices.contains { j in
                        word[j] == from && i - j >= 0 && i - j + word.count <= chars.count && Array(chars[(i - j)..<(i - j + word.count)]) == word
                    }
                }
                if !kept { chars[i] = to }
            }
        }
        return String(chars)
    }
}

/// 語言檔（`Resources/languages.json`）：app 的 bundle 裡一份；直接跑建置輸出（沒有 bundle）時讀原始碼樹的那份
enum LanguageProfiles {
    private struct File: Decodable { let languages: [LanguageProfile] }

    static let all: [LanguageProfile] = {
        let source = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("Resources/languages.json")
        let url = Bundle.main.url(forResource: "languages", withExtension: "json") ?? source
        do {
            return try JSONDecoder().decode(File.self, from: Data(contentsOf: url)).languages
        } catch {
            fatalError("languages.json: \(error)")  // l10n-skip
        }
    }()

    static let byCode: [String: LanguageProfile] = Dictionary(uniqueKeysWithValues: all.map { ($0.code, $0) })
}

/// 會議語言（design §16.3、§18.1、§41、R12.4、R15.1、R63）：決定即時逐字稿、正式轉錄、會議紀錄和匯出用的語言。
/// 跟介面語言無關：英文介面也可以有中文會議，反之亦然。做法寫在語言檔（D139），這裡只問它；不要再寫 `language == .ja`
struct MeetingLanguage: RawRepresentable, Hashable, Codable, Identifiable, CaseIterable {
    let rawValue: String

    /// 語言檔裡沒有的代碼回 nil
    init?(rawValue: String) {
        guard LanguageProfiles.byCode[rawValue] != nil else { return nil }
        self.rawValue = rawValue
    }

    private init(code: String) { rawValue = code }

    static let zh = MeetingLanguage(code: "zh")
    static let en = MeetingLanguage(code: "en")
    static let ja = MeetingLanguage(code: "ja")

    /// 語言檔的順序（選單的順序）
    static var allCases: [MeetingLanguage] { LanguageProfiles.all.map { MeetingLanguage(code: $0.code) } }

    var id: String { rawValue }

    var profile: LanguageProfile { LanguageProfiles.byCode[rawValue] ?? LanguageProfiles.byCode["zh"]! }

    /// 選單上的名稱：用各自語言的寫法，兩種介面都一樣；一般語言接「（實驗性）」（design §41.2）
    var name: String {
        profile.tier == .general ? profile.name + MeetingLanguage.ui(zh: "（實驗性）", en: " (Experimental)") : profile.name  // l10n-skip
    }

    /// 介面上提到這個語言時的名稱（跟著介面語言），例如「已切換成日文」
    var uiName: String { MeetingLanguage.interface == .en ? profile.uiName.en : profile.uiName.zh }

    /// SpeechAnalyzer（即時逐字稿、macOS 內建辨識）用的語言
    var speechLocale: Locale { Locale(identifier: profile.live.mac) }

    /// whisper.cpp 的 language 參數（語言檔的 transcription.whisper）
    var whisperCode: String { profile.transcription.whisper ?? rawValue }

    /// 用哪一份手寫的提示詞、標籤
    var base: PromptBase { profile.base }

    /// 跟著會議語言（不是介面語言）、給人看的文字，例如匯出的標題、待確認的是非題（design §41.1）：
    /// 中文、日文那一份照用；英文那一份在一般語言查字串表，查不到用英文
    func text(zh: String, en: String, ja: String) -> String {
        switch profile.base {
        case .zh: return zh
        case .ja: return ja
        case .en: return profile.strings?[en] ?? en
        }
    }

    /// 提示詞裡的文字：只看用哪一份，不查字串表（一般語言的提示詞維持英文，D140）
    func prompt(zh: String, en: String, ja: String) -> String {
        switch profile.base {
        case .zh: return zh
        case .en: return en
        case .ja: return ja
        }
    }

    /// 有插值的文字：三份都是格式字串（%@、%lld），一般語言查字串表的格式
    func format(zh: String, en: String, ja: String, _ arguments: CVarArg...) -> String {
        String(format: text(zh: zh, en: en, ja: ja), arguments: arguments)
    }

    /// 預設的說話者名稱：「說話者 1」「Speaker 1」「話者 1」
    func defaultSpeaker(_ idx: Int) -> String { String(format: profile.speaker, idx) }

    /// 日期、星期的格式
    var locale: Locale { Locale(identifier: profile.locale) }

    /// 正式轉錄的 whisper 用 large-v3-turbo（日文、粵語、韓文…）還是 Breeze（design §41.3）
    var usesTurbo: Bool { profile.transcription.model == "turbo" }

    /// 介面上列出用某個轉錄模型的語言，例如「日文和韓文」（設定的模型名稱、環境檢查）
    static func uiNames(model: String) -> String { uiNames(allCases.filter { $0.profile.transcription.model == model }) }

    /// 自動偵測認得的語言（語言檔的 detect 不是 null，design §41.4）
    static var detectable: [MeetingLanguage] { allCases.filter { $0.profile.detect != nil } }

    /// 介面上列出幾種語言：「中文、英文、日文和韓文」「Chinese, English, Japanese, and Korean」
    static func uiNames(_ languages: [MeetingLanguage]) -> String {
        let formatter = ListFormatter()
        formatter.locale = Locale(identifier: interface == .en ? "en" : "zh-Hant")  // l10n-skip
        return formatter.string(from: languages.map(\.uiName)) ?? ""
    }

    /// 同一個中文詞在英文要用不同說法的少數地方（例如「錄音」：按鈕是 Record、分頁是 Recording），
    /// 直接依介面語言給兩種寫法，不經過翻譯表
    static func ui(zh: String, en: String) -> String { interface == .en ? en : zh }

    /// 目前的介面語言（跟著 macOS 的語言設定），只會是中文或英文
    static var interface: MeetingLanguage {
        Bundle.main.preferredLocalizations.first?.hasPrefix("zh") == true ? .zh : .en
    }

    /// 新會議沒有設定時的預設會議語言：跟著介面語言；macOS 的第一個語言是語言檔裡中文、英文以外的語言
    /// （日文、韓文）時用那個語言（介面是英文）。香港的中文（zh-HK）仍然預設中文，粵語要自己選（design §41.6）
    static var systemDefault: MeetingLanguage {
        guard interface == .en, let first = Locale.preferredLanguages.first else { return interface }
        let other = allCases.first { $0 != .en && $0.profile.family != "zh" && first.hasPrefix($0.rawValue) }
        return other ?? interface
    }
}
