import Foundation

/// 會議語言（design §16.3、R12.4）：決定即時逐字稿、正式轉錄、會議紀錄和匯出用的語言。
/// 跟介面語言無關：英文介面也可以有中文會議，反之亦然。
enum MeetingLanguage: String, CaseIterable, Codable, Identifiable {
    case zh, en

    var id: String { rawValue }

    /// 選單上的名稱：用各自語言的寫法，兩種介面都一樣
    var name: String { self == .en ? "English" : "中文" }  // l10n-skip

    /// SpeechAnalyzer（即時逐字稿、macOS 內建辨識）用的語言
    var speechLocale: Locale { Locale(identifier: self == .en ? "en_US" : "zh_TW") }

    /// whisper.cpp 的 language 參數
    var whisperCode: String { rawValue }

    /// 跟著會議語言（不是介面語言）的文字，例如匯出的標題、預設的說話者名稱
    func text(zh: String, en: String) -> String { self == .en ? en : zh }

    /// 日期、星期的格式
    var locale: Locale { speechLocale }

    /// 同一個中文詞在英文要用不同說法的少數地方（例如「錄音」：按鈕是 Record、分頁是 Recording），
    /// 直接依介面語言給兩種寫法，不經過翻譯表
    static func ui(zh: String, en: String) -> String { interface.text(zh: zh, en: en) }

    /// 目前的介面語言（跟著 macOS 的語言設定）；也是新會議沒有設定時的預設會議語言
    static var interface: MeetingLanguage {
        Bundle.main.preferredLocalizations.first?.hasPrefix("zh") == true ? .zh : .en
    }
}
