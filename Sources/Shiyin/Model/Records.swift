import Foundation
import GRDB

/// 資料表對應（design §3）。欄位名稱在資料庫是 snake_case。
protocol SnakeCaseRecord: Codable, FetchableRecord, MutablePersistableRecord {}

extension SnakeCaseRecord {
    static var databaseColumnDecodingStrategy: DatabaseColumnDecodingStrategy { .convertFromSnakeCase }
    static var databaseColumnEncodingStrategy: DatabaseColumnEncodingStrategy { .convertToSnakeCase }
}

enum Clock {
    /// 格式同 ISO8601DateFormatter 的預設（2026-11-17T01:02:03Z）。不要每次新建 ISO8601DateFormatter：
    /// 資料表的預設值（createdAt = Clock.now()）讀每一筆都會跑一次，長會議的校正建議光建格式器就要 0.1 秒（2026-11-17 量到）
    static func now() -> String { Date().ISO8601Format() }
}

struct Meeting: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "meetings"
    var id: Int64?
    var title: String = ""
    var titleLocked: Bool = false
    var meetingDate: String
    var participants: String = ""
    var sourceFilename: String
    var audioFile: String = ""
    var durationSeconds: Int?
    var notes: String?
    var notesBy: String?
    /// 目前的會議紀錄是用哪個範本整理的（migration v17、design §89）：整理、草稿時記下，舊的紀錄是 nil
    var notesTemplate: String?
    var createdAt: String = Clock.now()
    var updatedAt: String = Clock.now()
    // v2（design §4）
    var templateId: String = "general"
    var userNotes: String = ""
    var source: String = "import"        // recording / import / package / transcript
    var channels: String = "mic"         // mic / system / both
    var deletedAt: String?
    // v18（design §92、R118）：在哪個資料夾，nil 是不在資料夾；名字問 AppModel.folderName
    var folderId: Int64?
    // v3（design §16.3）
    var language: String = "zh"
    // v7（design §21.1）：1 是用即時逐字稿整理的草稿，2 是使用者改過的草稿；轉錄引擎（breeze / japanese / system）
    var notesDraft: Int = 0
    var transcribedWith: String = ""
    // v8（design §23.2）：說話者辨識至少分出幾個人，nil 是自動判斷
    var minSpeakers: Int?
    // v9（design §24.1）：說話者辨識的自我檢查分數（越大越可能把不同的人併在一起），nil 是沒算過；使用者按了「不用了」
    var speakerCheck: Double?
    var speakerHintDismissed: Bool = false
    // v12（design §72）：資料夾已經決定了（自動放過、自己移過、建議按了不用）。定期會議的 series_id 欄位留著不用（D177）
    var folderDecided: Bool = false

    var meetingLanguage: MeetingLanguage { MeetingLanguage(rawValue: language) ?? .zh }

    /// 說話者辨識的自我檢查覺得可能把不同的人併在一起（design §24.5）
    var mayHaveMergedSpeakers: Bool { (speakerCheck ?? 0) >= Diarizer.Quality.hintThreshold }

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }

    var displayTitle: String {
        if !title.isEmpty { return title }
        return (sourceFilename as NSString).deletingPathExtension
    }

    var decodedNotes: MeetingNotes? {
        guard let notes, let data = notes.data(using: .utf8) else { return nil }
        return try? JSONDecoder().decode(MeetingNotes.self, from: data)
    }

    var audioURL: URL { Paths.audioDir.appendingPathComponent(audioFile) }
    var hasAudio: Bool { !audioFile.isEmpty && FileManager.default.fileExists(atPath: audioURL.path) }

    var participantList: [String] {
        participants.split(whereSeparator: { $0 == "," || $0 == "，" || $0 == "、" })  // l10n-skip
            .map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }
}

struct Segment: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "segments"
    var id: Int64?
    var meetingId: Int64
    var seq: Int
    var start: Double
    var end: Double
    var text: String
    var lowConfidence: Bool
    var speaker: Int?
    var kind: String = "final"           // live（即時預覽）/ final
    var channel: Int? = nil              // 分聲道轉錄時：0 麥克風、1 電腦聲音（design §17.3）
    var speakerManual: Bool = false      // v9：使用者改過這句的說話者（design §24.2）

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

extension Segment {
    /// 手寫的讀取：GRDB 用 Codable 逐筆解碼時每一筆都要重建欄位對照，2,000 段以上的會議光讀逐字稿就要上百毫秒（design §21.12）
    init(row: Row) throws {
        id = row["id"]
        meetingId = row["meeting_id"]
        seq = row["seq"]
        start = row["start"]
        end = row["end"]
        text = row["text"]
        lowConfidence = row["low_confidence"]
        speaker = row["speaker"]
        kind = row["kind"]
        channel = row["channel"]
        speakerManual = row["speaker_manual"]
    }
}

/// 說話者名字（R5.2），沒改過名字時顯示「說話者 n」
struct SpeakerName: SnakeCaseRecord, Hashable {
    static let databaseTableName = "speakers"
    var meetingId: Int64
    var idx: Int
    var name: String
    // v7（design §21.1）
    var nameSource: String = "default"     // default / manual / voiceprint / suggestion
    var personId: Int64?
    var embedding: Data?                   // 聲紋，Float32×256
    var speechSeconds: Double = 0
    var audioSource: String = "mix"        // mic / system / mix
    var suggestedName: String?
    var suggestionReason: String?
    var suggestionTime: String?
}

/// 錄音中按 ⌘B 標的重點（R2.4）
struct Bookmark: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "bookmarks"
    var id: Int64?
    var meetingId: Int64
    var time: Double
    var label: String = ""

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

/// 逐字稿校正建議（design §17.4、R14.4）：pending 待確認、accepted 已接受（段落文字已改）、rejected 已拒絕
struct Correction: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "corrections"
    var id: Int64?
    var meetingId: Int64
    var segmentId: Int64
    var original: String
    var suggestion: String
    var reason: String
    var status: String = "pending"
    var createdAt: String = Clock.now()

    var suggestionValue: CorrectionSuggestion {
        CorrectionSuggestion(segment: 0, original: original, suggestion: suggestion, reason: reason)
    }

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

/// 詞彙表建議（design §17.5）：使用者確認才加進詞彙表
struct GlossarySuggestion: Hashable, Identifiable {
    enum Source { case participant, speaker, correction, title }
    var term: String
    var source: Source
    var id: String { term.lowercased() }

    var sourceLabel: String {
        switch source {
        case .participant: String(localized: "參加者")
        case .speaker: String(localized: "說話者名字")
        case .correction: String(localized: "校正過的字")
        case .title: String(localized: "會議標題")
        }
    }
}

/// 會議紀錄範本（R6.2、design §8.2）
struct TemplateRecord: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "templates"
    var id: String
    var name: String
    var builtin: Bool
    var definition: String
    var updatedAt: String = Clock.now()
}

/// 版本紀錄的一版（R115、design §89）：versionId 是 nil 的是目前的紀錄；origin、time 是這一版怎麼來的、什麼時候（最舊的一版不知道）
struct NotesHistoryEntry: Equatable {
    var versionId: Int64?
    var notes: MeetingNotes
    var templateId: String?
    var origin: String?
    var time: String?
    var isCurrent: Bool { versionId == nil }
}

struct NotesVersion: SnakeCaseRecord, Identifiable {
    static let databaseTableName = "notes_versions"
    var id: Int64?
    var meetingId: Int64
    var notes: String
    /// 換掉這一版的那次存檔是什麼（重新整理、校稿、手動編輯…）
    var reason: String
    var createdAt: String = Clock.now()
    /// 這一版是用哪個範本整理的（migration v17、design §89）；不知道是 nil
    var templateId: String?

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

enum JobKind: String, Codable, CaseIterable {
    case transcribe, summarize, revise, compress
    case correct   // 逐字稿校正（design §17.4）
    case nameSpeakers   // 猜說話者（design §21.5）
    case rediarize   // 只重新辨識說話者（design §23）
    case clean   // 精簡逐字稿（design §70）

    /// 轉錄線、整理線、輔助線（design §21.2）：校正、猜說話者在輔助線，跟整理同時跑
    var lane: JobLane {
        switch self {
        case .transcribe, .compress, .rediarize: return .transcribe
        case .summarize, .revise: return .llm
        case .correct, .nameSpeakers, .clean: return .assist
        }
    }

    /// 要用整理工具的工作（design §34）
    var usesSummarizer: Bool { self == .summarize || self == .revise || self == .correct || self == .nameSpeakers || self == .clean }

    /// 背景的輔助工作：不發通知、不改會議的狀態
    var isAuxiliary: Bool { self == .compress || self == .correct || self == .nameSpeakers || self == .rediarize || self == .clean }
}

enum JobLane: String, CaseIterable { case transcribe, llm, assist }

enum JobStatus: String, Codable {
    case queued, running, done, failed, cancelled
}

struct Job: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "jobs"
    var id: Int64?
    var meetingId: Int64
    var kind: JobKind
    var status: JobStatus = .queued
    var payload: String?
    var error: String?
    var createdAt: String = Clock.now()
    var startedAt: String?
    var finishedAt: String?
    /// 執行時拾音意外結束的次數（design §20.5）
    var crashes = 0
    /// 失敗的種類（Error.logSummary，design §21.9）
    var errorKind: String?
    /// 排隊的順序（migration v19、design §96.1）：大的先拿，一樣的照 id；平常是 0
    var priority = 0

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

/// 資料夾（migration v18、design §92、R118）：其他地方都記 id，名字只用來顯示
struct Folder: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "folders"
    var id: Int64?
    var name: String
    var createdAt: String = Clock.now()

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

struct GlossaryTerm: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "glossary"
    var id: Int64?
    var term: String
    var createdAt: String = Clock.now()

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

/// 詞彙表的群組（design §100.1、R126）：像標籤，一個詞可以在好幾組
struct GlossaryGroup: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "glossary_groups"
    var id: Int64?
    var name: String
    var createdAt: String = Clock.now()

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

/// 會議的參考文件的種類（design §39.2）：PDF、投影片有頁碼
enum DocumentKind: String, Codable, CaseIterable {
    case pdf, docx, pptx, text, image

    var isPaged: Bool { self == .pdf || self == .pptx }
}

/// 會議的參考文件（design §39.1、R61）：原始檔在 documents/<file>，讀出來的文字每頁一項
struct MeetingDocument: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "documents"
    var id: Int64?
    var meetingId: Int64
    var name: String
    var file: String = ""
    var kind: String
    var pages: String                    // JSON 字串陣列
    var ocr: Bool = false
    var createdAt: String = Clock.now()
    /// 會議中的畫面（design §83.1、R109）：錄音時間（秒）；nil 是一般的參考文件
    var at: Double?

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }

    var isScreen: Bool { at != nil }
    static let screenFileName = "screen.jpg"
    /// 畫面辨識出的文字（沒有字是空字串）
    var screenText: String { pageTexts.joined(separator: "\n") }
    var documentKind: DocumentKind { DocumentKind(rawValue: kind) ?? .text }
    var pageTexts: [String] { (try? JSONDecoder().decode([String].self, from: Data(pages.utf8))) ?? [] }
    var fileURL: URL { Paths.documentsDir.appendingPathComponent(file) }

    /// 頁的文字存成 JSON（同會議紀錄的寫法，Windows 用 SwiftJson 寫出一樣的字串）
    static func encodePages(_ pages: [String]) -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        return String(data: (try? encoder.encode(pages)) ?? Data(), encoding: .utf8) ?? "[]"
    }
}
