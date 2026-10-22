import Foundation
import UniformTypeIdentifiers

/// 匯入的選填資料（R1.2）
struct ImportOptions {
    var title: String = ""
    var date: String? = nil
    var participants: String = ""
    /// 顯示用的來源名稱；沒給就用檔名
    var sourceName: String? = nil
    var templateId: String? = nil
    /// 放進哪個資料夾（R9.2）；空字串是不放
    var folder: String = ""
    /// 會議語言（design §16.3）
    var language: MeetingLanguage = AppSettings.defaultMeetingLanguage
    /// 大約幾個人（design §26.2）：nil 是自動判斷
    var minSpeakers: Int? = nil
}

enum ImportError: LocalizedError {
    case copyFailed(String, String)

    var errorDescription: String? {
        switch self {
        case .copyFailed(let name, let reason): return String(localized: "無法匯入「\(name)」：\(reason)")
        }
    }
}

/// 統一處理四種匯入入口（design §7）
enum Importer {
    static let contentTypes: [UTType] = [.audio, .movie, .audiovisualContent]
    /// 逐字稿、Markdown 筆記、.shiyin 封包（R3.3～R3.5）
    static let otherTypes: [UTType] = [UTType(filenameExtension: "shiyin"), UTType(filenameExtension: "srt"),
                                       UTType(filenameExtension: "vtt"), .plainText,
                                       UTType(filenameExtension: "md")].compactMap { $0 }

    /// 會議日期預設值（R1.4）：檔名有日期就用檔名，年份取不晚於今天的最近一年；否則用檔案修改日期
    static func inferDate(filename: String, modified: Date?, today: Date = Date()) -> String {
        let name = (filename as NSString).deletingPathExtension
        let calendar = Calendar(identifier: .gregorian)
        let todayParts = calendar.dateComponents([.year, .month, .day], from: today)

        func valid(_ y: Int, _ m: Int, _ d: Int) -> String? {
            guard (1...12).contains(m), (1...31).contains(d),
                  let date = calendar.date(from: DateComponents(year: y, month: m, day: d)),
                  calendar.component(.day, from: date) == d else { return nil }
            return DateText.string(date)
        }
        func withNearestYear(_ m: Int, _ d: Int) -> String? {
            let year = todayParts.year!
            let isFuture = (m, d) > (todayParts.month!, todayParts.day!)
            return valid(isFuture ? year - 1 : year, m, d)
        }
        func match(_ pattern: String) -> [Int]? {
            guard let regex = try? NSRegularExpression(pattern: pattern),
                  let m = regex.firstMatch(in: name, range: NSRange(name.startIndex..., in: name)) else { return nil }
            return (1..<m.numberOfRanges).compactMap { Range(m.range(at: $0), in: name).flatMap { Int(name[$0]) } }
        }

        // 2026-08-27、2026_08_27、20260827
        if let p = match(#"(?<!\d)(20\d{2})[-_./]?(\d{1,2})[-_./]?(\d{1,2})(?!\d)"#), p.count == 3,
           let date = valid(p[0], p[1], p[2]) { return date }
        // 8_27、08-27、8.27
        if let p = match(#"(?<!\d)(\d{1,2})[-_./](\d{1,2})(?!\d)"#), p.count == 2,
           let date = withNearestYear(p[0], p[1]) { return date }
        // 0827
        if let p = match(#"(?<!\d)(\d{2})(\d{2})(?!\d)"#), p.count == 2,
           let date = withNearestYear(p[0], p[1]) { return date }
        return DateText.string(modified ?? today)
    }

    static func modificationDate(_ url: URL) -> Date? {
        (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
    }

    /// 只保留英數字的副檔名，避免路徑穿越（N1）
    static func safeExtension(_ url: URL) -> String {
        let ext = url.pathExtension.lowercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
        return ext.isEmpty ? "bin" : String(ext.prefix(8))
    }

    /// 建立會議、複製錄音、排轉錄工作。複製失敗會刪掉剛建立的會議。
    @discardableResult
    static func importFile(_ url: URL, options: ImportOptions, db: AppDatabase,
                           move: Bool = false) throws -> Meeting {
        let accessing = url.startAccessingSecurityScopedResource()
        defer { if accessing { url.stopAccessingSecurityScopedResource() } }

        let title = options.title.trimmingCharacters(in: .whitespacesAndNewlines)
        var draft = Meeting(
            title: title, titleLocked: !title.isEmpty,
            meetingDate: options.date ?? inferDate(filename: url.lastPathComponent, modified: modificationDate(url)),
            participants: options.participants.trimmingCharacters(in: .whitespacesAndNewlines),
            sourceFilename: options.sourceName ?? url.lastPathComponent,
            templateId: options.templateId ?? AppSettings.defaultTemplate, source: "import", folder: options.folder,
            language: options.language.rawValue)
        draft.minSpeakers = options.minSpeakers
        let meeting = try db.insertMeeting(draft, audioExtension: safeExtension(url))
        var moved = false
        do {
            try Paths.ensureDirectories()
            // 同一個 APFS 磁碟區時，copyItem 會自動用 clone，不佔額外空間
            if move {
                try FileManager.default.moveItem(at: url, to: meeting.audioURL)
                moved = true
            } else {
                try FileManager.default.copyItem(at: url, to: meeting.audioURL)
            }
            try db.enqueue(meeting.id!, kind: .transcribe)
            // 匿名使用統計（design §30.1）：復原當掉的錄音（move）不算匯入
            if !move { UsageStats.shared.imported() }
        } catch {
            try? db.deleteMeeting(meeting.id!)
            if moved {
                // 復原的錄音是唯一的一份：搬回原處，下次開啟再試（design §20.6、R18.6）
                try? FileManager.default.moveItem(at: meeting.audioURL, to: url)
            } else {
                try? FileManager.default.removeItem(at: meeting.audioURL)
            }
            throw ImportError.copyFailed(url.lastPathComponent, error.localizedDescription)
        }
        return meeting
    }
}
