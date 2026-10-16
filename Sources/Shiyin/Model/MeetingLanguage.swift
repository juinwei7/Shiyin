import Foundation

/// 會議語言（design §16.3、§18.1、R12.4、R15.1）：決定即時逐字稿、正式轉錄、會議紀錄和匯出用的語言。
/// 跟介面語言無關：英文介面也可以有中文會議，反之亦然。日文只有會議語言，沒有日文介面
enum MeetingLanguage: String, CaseIterable, Codable, Identifiable {
    case zh, en, ja

    var id: String { rawValue }

    /// 選單上的名稱：用各自語言的寫法，兩種介面都一樣
    var name: String {
        switch self {
        case .zh: return "中文"  // l10n-skip
        case .en: return "English"
        case .ja: return "日本語"  // l10n-skip
        }
    }

    /// 介面上提到這個語言時的名稱（跟著介面語言），例如「已切換成日文」
    var uiName: String {
        switch self {
        case .zh: return MeetingLanguage.ui(zh: "中文", en: "Chinese")  // l10n-skip
        case .en: return MeetingLanguage.ui(zh: "英文", en: "English")  // l10n-skip
        case .ja: return MeetingLanguage.ui(zh: "日文", en: "Japanese")  // l10n-skip
        }
    }

    /// SpeechAnalyzer（即時逐字稿、macOS 內建辨識）用的語言
    var speechLocale: Locale { Locale(identifier: text(zh: "zh_TW", en: "en_US", ja: "ja_JP")) }

    /// whisper.cpp 的 language 參數
    var whisperCode: String { rawValue }

    /// 跟著會議語言（不是介面語言）的文字，例如匯出的標題、預設的說話者名稱
    func text(zh: String, en: String, ja: String) -> String {
        switch self {
        case .zh: return zh
        case .en: return en
        case .ja: return ja
        }
    }

    /// 日期、星期的格式
    var locale: Locale { speechLocale }

    /// 同一個中文詞在英文要用不同說法的少數地方（例如「錄音」：按鈕是 Record、分頁是 Recording），
    /// 直接依介面語言給兩種寫法，不經過翻譯表
    static func ui(zh: String, en: String) -> String { interface == .en ? en : zh }

    /// 目前的介面語言（跟著 macOS 的語言設定），只會是中文或英文
    static var interface: MeetingLanguage {
        Bundle.main.preferredLocalizations.first?.hasPrefix("zh") == true ? .zh : .en
    }

    /// 新會議沒有設定時的預設會議語言：跟著介面語言；macOS 的第一個語言是日文時用日文（介面是英文）
    static var systemDefault: MeetingLanguage {
        if interface == .en, Locale.preferredLanguages.first?.hasPrefix("ja") == true { return .ja }
        return interface
    }
}
