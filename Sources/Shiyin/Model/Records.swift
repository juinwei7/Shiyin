import Foundation
import GRDB

/// 資料表對應（design §3）。欄位名稱在資料庫是 snake_case。
protocol SnakeCaseRecord: Codable, FetchableRecord, MutablePersistableRecord {}

extension SnakeCaseRecord {
    static var databaseColumnDecodingStrategy: DatabaseColumnDecodingStrategy { .convertFromSnakeCase }
    static var databaseColumnEncodingStrategy: DatabaseColumnEncodingStrategy { .convertToSnakeCase }
}

enum Clock {
    static func now() -> String { ISO8601DateFormatter().string(from: Date()) }
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
    var createdAt: String = Clock.now()
    var updatedAt: String = Clock.now()
    // v2（design §4）
    var templateId: String = "general"
    var userNotes: String = ""
    var source: String = "import"        // recording / import / package / transcript
    var channels: String = "mic"         // mic / system / both
    var deletedAt: String?
    var folder: String = ""
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

struct NotesVersion: SnakeCaseRecord, Identifiable {
    static let databaseTableName = "notes_versions"
    var id: Int64?
    var meetingId: Int64
    var notes: String
    var reason: String
    var createdAt: String = Clock.now()

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

enum JobKind: String, Codable, CaseIterable {
    case transcribe, summarize, revise, compress
    case correct   // 逐字稿校正（design §17.4）
    case nameSpeakers   // 猜說話者（design §21.5）
    case rediarize   // 只重新辨識說話者（design §23）

    /// 轉錄線、整理線、輔助線（design §21.2）：校正、猜說話者在輔助線，跟整理同時跑
    var lane: JobLane {
        switch self {
        case .transcribe, .compress, .rediarize: return .transcribe
        case .summarize, .revise: return .llm
        case .correct, .nameSpeakers: return .assist
        }
    }

    /// 要用整理工具的工作（design §34）
    var usesSummarizer: Bool { self == .summarize || self == .revise || self == .correct || self == .nameSpeakers }

    /// 背景的輔助工作：不發通知、不改會議的狀態
    var isAuxiliary: Bool { self == .compress || self == .correct || self == .nameSpeakers || self == .rediarize }
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

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

struct GlossaryTerm: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "glossary"
    var id: Int64?
    var term: String
    var createdAt: String = Clock.now()

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}
