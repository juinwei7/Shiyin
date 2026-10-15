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
        participants.split(whereSeparator: { $0 == "," || $0 == "，" || $0 == "、" })
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

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

/// 說話者名字（R5.2），沒改過名字時顯示「說話者 n」
struct SpeakerName: SnakeCaseRecord, Hashable {
    static let databaseTableName = "speakers"
    var meetingId: Int64
    var idx: Int
    var name: String
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

enum JobKind: String, Codable {
    case transcribe, summarize, revise, compress

    var lane: JobLane { self == .transcribe || self == .compress ? .transcribe : .llm }
}

enum JobLane: String { case transcribe, llm }

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

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

struct GlossaryTerm: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "glossary"
    var id: Int64?
    var term: String
    var createdAt: String = Clock.now()

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}
