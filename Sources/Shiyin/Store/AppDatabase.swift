import Foundation
import GRDB

/// 列表用：會議加上推算狀態所需的資料
struct MeetingRow: Identifiable, Hashable {
    var meeting: Meeting
    var latestJob: Job?
    var reviewCount: Int
    var id: Int64 { meeting.id ?? 0 }
}

struct ReplaceResult {
    var segmentsChanged: Int
    var notesChanged: Bool
}

/// 全文搜尋的一筆結果（R9.1）
struct SearchHit: Identifiable, Hashable {
    enum Kind: String { case title, segment, notes, userNotes = "user_notes" }
    var meetingId: Int64
    var kind: Kind
    var ref: Int64            // segment 的 id；其他種類是 0
    var snippet: String
    var id: String { "\(meetingId)-\(kind.rawValue)-\(ref)" }
}

final class AppDatabase: @unchecked Sendable {
    let pool: DatabasePool

    /// 開啟資料庫；有待執行的 migration 時，先把舊檔備份到 backups/（N3）
    init(path: String) throws {
        var config = Configuration()
        config.foreignKeysEnabled = true
        pool = try DatabasePool(path: path, configuration: config)
        let migrator = Self.migrator
        let applied = try pool.read { try migrator.appliedIdentifiers($0) }
        let pending = migrator.migrations.filter { !applied.contains($0) }
        if !applied.isEmpty && !pending.isEmpty {
            _ = try pool.writeWithoutTransaction { try $0.checkpoint(.truncate) }
            try FileManager.default.createDirectory(at: Paths.backupsDir, withIntermediateDirectories: true)
            let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "")
            let backup = Paths.backupsDir.appendingPathComponent("shiyin-\(stamp)-before-\(pending[0]).sqlite")
            try FileManager.default.copyItem(at: URL(fileURLWithPath: path), to: backup)
            log.info("database backed up before migration \(pending[0])")
        }
        try migrator.migrate(pool)
    }

    private static var migrator: DatabaseMigrator {
        var migrator = DatabaseMigrator()
        migrator.registerMigration("v1") { db in
            try db.execute(sql: """
            CREATE TABLE meetings (
              id               INTEGER PRIMARY KEY AUTOINCREMENT,
              title            TEXT NOT NULL DEFAULT '',
              title_locked     INTEGER NOT NULL DEFAULT 0,
              meeting_date     TEXT NOT NULL,
              participants     TEXT NOT NULL DEFAULT '',
              source_filename  TEXT NOT NULL,
              audio_file       TEXT NOT NULL,
              duration_seconds INTEGER,
              notes            TEXT,
              notes_by         TEXT,
              created_at       TEXT NOT NULL,
              updated_at       TEXT NOT NULL
            );
            CREATE TABLE segments (
              id             INTEGER PRIMARY KEY AUTOINCREMENT,
              meeting_id     INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              seq            INTEGER NOT NULL,
              start          REAL NOT NULL,
              end            REAL NOT NULL,
              text           TEXT NOT NULL,
              low_confidence INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX segments_meeting ON segments(meeting_id, seq);
            CREATE TABLE notes_versions (
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              notes      TEXT NOT NULL,
              reason     TEXT NOT NULL,
              created_at TEXT NOT NULL
            );
            CREATE TABLE jobs (
              id          INTEGER PRIMARY KEY AUTOINCREMENT,
              meeting_id  INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              kind        TEXT NOT NULL,
              status      TEXT NOT NULL,
              payload     TEXT,
              error       TEXT,
              created_at  TEXT NOT NULL,
              started_at  TEXT,
              finished_at TEXT
            );
            CREATE INDEX jobs_status ON jobs(status, id);
            CREATE INDEX jobs_meeting ON jobs(meeting_id, id);
            CREATE TABLE glossary (
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              term       TEXT NOT NULL UNIQUE,
              created_at TEXT NOT NULL
            );
            """)
        }
        // 第三版（design §4）
        migrator.registerMigration("v2") { db in
            try db.execute(sql: """
            ALTER TABLE meetings ADD COLUMN template_id TEXT NOT NULL DEFAULT 'general';
            ALTER TABLE meetings ADD COLUMN user_notes  TEXT NOT NULL DEFAULT '';
            ALTER TABLE meetings ADD COLUMN source      TEXT NOT NULL DEFAULT 'import';
            ALTER TABLE meetings ADD COLUMN channels    TEXT NOT NULL DEFAULT 'mic';
            ALTER TABLE meetings ADD COLUMN deleted_at  TEXT;
            ALTER TABLE meetings ADD COLUMN folder      TEXT NOT NULL DEFAULT '';
            ALTER TABLE segments ADD COLUMN speaker INTEGER;
            ALTER TABLE segments ADD COLUMN kind TEXT NOT NULL DEFAULT 'final';
            CREATE TABLE speakers (
              meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              idx        INTEGER NOT NULL,
              name       TEXT NOT NULL,
              PRIMARY KEY (meeting_id, idx)
            );
            CREATE TABLE bookmarks (
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              time       REAL NOT NULL,
              label      TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE templates (
              id         TEXT PRIMARY KEY,
              name       TEXT NOT NULL,
              builtin    INTEGER NOT NULL,
              definition TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE search USING fts5(meeting_id UNINDEXED, kind UNINDEXED, ref UNINDEXED, text, tokenize = 'trigram');
            """)
            // 會議紀錄轉成 v2 格式（字串陣列 → 帶時間點的條目，時間點先留空）
            for row in try Row.fetchAll(db, sql: "SELECT id, notes FROM meetings WHERE notes IS NOT NULL") {
                let id: Int64 = row["id"]
                guard let json: String = row["notes"], let notes = try? JSONDecoder().decode(MeetingNotes.self, from: Data(json.utf8))
                else { continue }
                try db.execute(sql: "UPDATE meetings SET notes = ? WHERE id = ?", arguments: [notes.jsonString(), id])
            }
            try Self.rebuildSearch(db)
        }
        // v3（design §16.3）：會議語言，舊會議都是中文
        migrator.registerMigration("v3") { db in
            try db.execute(sql: "ALTER TABLE meetings ADD COLUMN language TEXT NOT NULL DEFAULT 'zh'")
        }
        // 3.2：分聲道轉錄記錄每段來自哪個聲道（design §17.3）
        migrator.registerMigration("v4") { db in
            try db.execute(sql: "ALTER TABLE segments ADD COLUMN channel INTEGER")
        }
        // 3.2：逐字稿校正建議（design §17.4），段落重新轉錄時一起刪掉；詞彙表建議被忽略的詞（§17.5）
        migrator.registerMigration("v5") { db in
            try db.execute(sql: """
                CREATE TABLE corrections (
                  id         INTEGER PRIMARY KEY AUTOINCREMENT,
                  meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
                  segment_id INTEGER NOT NULL REFERENCES segments(id) ON DELETE CASCADE,
                  original   TEXT NOT NULL,
                  suggestion TEXT NOT NULL,
                  reason     TEXT NOT NULL DEFAULT '',
                  status     TEXT NOT NULL DEFAULT 'pending',
                  created_at TEXT NOT NULL
                );
                CREATE INDEX corrections_meeting ON corrections(meeting_id);
                CREATE TABLE glossary_dismissed (term TEXT PRIMARY KEY COLLATE NOCASE);
                """)
        }
        // 3.4.1：工作執行時拾音意外結束的次數（design §20.5）；搜尋表的 rowid 改成可以推算，重建索引（§20.11）
        migrator.registerMigration("v6") { db in
            try db.execute(sql: "ALTER TABLE jobs ADD COLUMN crashes INTEGER NOT NULL DEFAULT 0")
            try Self.rebuildSearch(db)
        }
        // 3.5（design §21.1）：草稿紀錄、聲紋、猜說話者、提問、編輯草稿、回報問題用的錯誤種類
        migrator.registerMigration("v7") { db in
            try db.execute(sql: """
                ALTER TABLE meetings ADD COLUMN notes_draft INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE meetings ADD COLUMN transcribed_with TEXT NOT NULL DEFAULT '';
                ALTER TABLE jobs ADD COLUMN error_kind TEXT;
                CREATE TABLE people (
                  id         INTEGER PRIMARY KEY AUTOINCREMENT,
                  name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
                  created_at TEXT NOT NULL
                );
                ALTER TABLE speakers ADD COLUMN name_source TEXT NOT NULL DEFAULT 'default';
                ALTER TABLE speakers ADD COLUMN person_id INTEGER REFERENCES people(id) ON DELETE SET NULL;
                ALTER TABLE speakers ADD COLUMN embedding BLOB;
                ALTER TABLE speakers ADD COLUMN speech_seconds REAL NOT NULL DEFAULT 0;
                ALTER TABLE speakers ADD COLUMN audio_source TEXT NOT NULL DEFAULT 'mix';
                ALTER TABLE speakers ADD COLUMN suggested_name TEXT;
                ALTER TABLE speakers ADD COLUMN suggestion_reason TEXT;
                ALTER TABLE speakers ADD COLUMN suggestion_time TEXT;
                UPDATE speakers SET name_source = 'manual'
                  WHERE name NOT GLOB '說話者 [0-9]*' AND name NOT GLOB 'Speaker [0-9]*' AND name NOT GLOB '話者 [0-9]*';
                CREATE TABLE voiceprints (
                  id           INTEGER PRIMARY KEY AUTOINCREMENT,
                  person_id    INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
                  embedding    BLOB NOT NULL,
                  audio_source TEXT NOT NULL,
                  meeting_id   INTEGER REFERENCES meetings(id) ON DELETE SET NULL,
                  speaker_idx  INTEGER,
                  created_at   TEXT NOT NULL
                );
                CREATE INDEX voiceprints_person ON voiceprints(person_id);
                CREATE TABLE questions (
                  id         INTEGER PRIMARY KEY AUTOINCREMENT,
                  meeting_id INTEGER REFERENCES meetings(id) ON DELETE CASCADE,
                  scope      TEXT NOT NULL DEFAULT '',
                  question   TEXT NOT NULL,
                  answer     TEXT NOT NULL,
                  provider   TEXT NOT NULL,
                  created_at TEXT NOT NULL
                );
                CREATE INDEX questions_meeting ON questions(meeting_id, id);
                CREATE TABLE drafts (
                  meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
                  key        TEXT NOT NULL,
                  text       TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  PRIMARY KEY (meeting_id, key)
                );
                """)
        }
        // 說話者辨識至少分出幾個人（design §23.2、R22.3）
        migrator.registerMigration("v8") { db in
            try db.execute(sql: "ALTER TABLE meetings ADD COLUMN min_speakers INTEGER")
        }
        // 說話者的修正、分不準的提示（design §24.1）
        migrator.registerMigration("v9") { db in
            try db.execute(sql: """
                ALTER TABLE segments ADD COLUMN speaker_manual INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE meetings ADD COLUMN speaker_check REAL;
                ALTER TABLE meetings ADD COLUMN speaker_hint_dismissed INTEGER NOT NULL DEFAULT 0;
                CREATE TABLE speaker_undo (
                  meeting_id INTEGER PRIMARY KEY REFERENCES meetings(id) ON DELETE CASCADE,
                  snapshot   TEXT NOT NULL,
                  label      TEXT NOT NULL,
                  created_at TEXT NOT NULL
                );
                """)
        }
        return migrator
    }

    // MARK: 會議

    func meetingRows(trash: Bool = false) throws -> [MeetingRow] {
        try pool.read { db in
            let meetings = try Meeting
                .filter(trash ? Column("deleted_at") != nil : Column("deleted_at") == nil)
                .order(sql: "meeting_date DESC, id DESC").fetchAll(db)
            // 背景的輔助工作（壓縮、逐字稿校正、猜說話者、重新辨識說話者）不算會議的狀態（design §21.2、§23.3）
            let auxiliary = JobKind.allCases.filter(\.isAuxiliary).map { "'\($0.rawValue)'" }.joined(separator: ", ")
            let latest = try Job.fetchAll(db, sql: "SELECT * FROM jobs WHERE id IN (SELECT MAX(id) FROM jobs WHERE kind NOT IN (\(auxiliary)) GROUP BY meeting_id)")
            let byMeeting = Dictionary(uniqueKeysWithValues: latest.map { ($0.meetingId, $0) })
            // 待確認的題數也算說話者的建議（R33.3）
            let speakerQuestions = Dictionary(uniqueKeysWithValues: try Row.fetchAll(db, sql: """
                SELECT meeting_id, COUNT(*) AS n FROM speakers WHERE name_source = 'default' AND suggested_name IS NOT NULL GROUP BY meeting_id
                """).map { (Int64($0["meeting_id"] as Int64), Int($0["n"] as Int64)) })
            return meetings.map { m in
                MeetingRow(meeting: m, latestJob: byMeeting[m.id ?? 0],
                           reviewCount: (m.decodedNotes?.reviewItems.count ?? 0) + (speakerQuestions[m.id ?? 0] ?? 0))
            }
        }
    }

    func meeting(_ id: Int64) throws -> Meeting? {
        try pool.read { db in try Meeting.fetchOne(db, key: id) }
    }

    func segments(_ meetingId: Int64, kind: String = "final") throws -> [Segment] {
        try pool.read { db in
            try Segment.filter(Column("meeting_id") == meetingId && Column("kind") == kind).order(Column("seq")).fetchAll(db)
        }
    }

    /// 只讀這幾段（改一段文字、接受校正之後，design §21.12）
    func segments(ids: [Int64]) throws -> [Segment] {
        try pool.read { db in try Segment.filter(ids.contains(Column("id"))).fetchAll(db) }
    }

    func versionCount(_ meetingId: Int64) throws -> Int {
        try pool.read { db in try NotesVersion.filter(Column("meeting_id") == meetingId).fetchCount(db) }
    }

    func latestJob(_ meetingId: Int64) throws -> Job? {
        try pool.read { db in
            try Job.filter(Column("meeting_id") == meetingId && !JobKind.allCases.filter(\.isAuxiliary).map(\.rawValue).contains(Column("kind")))
                .order(Column("id").desc).fetchOne(db)
        }
    }

    /// 建立會議並給定錄音檔名（需要先拿到 id 才能命名，所以在同一個交易裡做兩步）
    func insertMeeting(_ meeting: Meeting, audioExtension: String?) throws -> Meeting {
        try pool.write { db in
            var m = meeting
            try m.insert(db)
            if let audioExtension { m.audioFile = "\(m.id!).\(audioExtension)" }
            try m.update(db)
            try Self.index(db, meetingId: m.id!, kind: .title, ref: 0, text: m.displayTitle)
            return m
        }
    }

    func updateMeeting(_ meeting: Meeting) throws {
        try pool.write { db in
            var m = meeting
            m.updatedAt = Clock.now()
            try m.update(db)
            try Self.index(db, meetingId: m.id!, kind: .title, ref: 0, text: m.displayTitle)
            try Self.index(db, meetingId: m.id!, kind: .userNotes, ref: 0, text: m.userNotes)
        }
    }

    /// 資料夾（R9.2）：資料夾就是 meetings.folder 的值，空字串表示不在任何資料夾
    func setFolder(_ ids: [Int64], folder: String) throws {
        guard !ids.isEmpty else { return }
        try pool.write { db in
            for id in ids {
                try db.execute(sql: "UPDATE meetings SET folder = ?, updated_at = ? WHERE id = ?", arguments: [folder, Clock.now(), id])
            }
        }
    }

    /// 改名或刪除（newName 為空字串）一個資料夾，垃圾桶裡的會議也一起改
    func renameFolder(_ old: String, to newName: String) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET folder = ?, updated_at = ? WHERE folder = ?", arguments: [newName, Clock.now(), old])
        }
    }

    /// 錄音中途當掉、連錄音檔都沒有的會議（design §20.6）
    func emptyRecordingMeetings() throws -> [Int64] {
        try pool.read { db in
            try Int64.fetchAll(db, sql: "SELECT id FROM meetings WHERE source = 'recording' AND audio_file = '' AND deleted_at IS NULL")
        }
    }

    /// 永久刪除（垃圾桶清除或使用者確認）
    func deleteMeeting(_ id: Int64) throws {
        try pool.write { db in
            // 段落會跟著會議刪掉（ON DELETE CASCADE），所以先刪索引
            try Self.unindexSegments(db, meetingId: id)
            try Self.unindexMeeting(db, meetingId: id)
            _ = try Meeting.deleteOne(db, key: id)
        }
    }

    /// 移到垃圾桶或還原（R9.3）
    func setTrashed(_ id: Int64, _ trashed: Bool) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET deleted_at = ? WHERE id = ?", arguments: [trashed ? Clock.now() : nil, id])
        }
    }

    func expiredTrash(olderThan days: Int) throws -> [Meeting] {
        let cutoff = ISO8601DateFormatter().string(from: Date().addingTimeInterval(-Double(days) * 86_400))
        return try pool.read { db in
            try Meeting.filter(Column("deleted_at") != nil && Column("deleted_at") < cutoff).fetchAll(db)
        }
    }

    /// 只改一個欄位（範本、會議語言）。不要用 updateMeeting 整列寫回：畫面上的 Meeting 可能是舊的（例如筆記另外存過），會把別的欄位蓋回去
    func setTemplate(_ id: Int64, templateId: String) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET template_id = ?, updated_at = ? WHERE id = ?", arguments: [templateId, Clock.now(), id])
        }
    }

    func setLanguage(_ id: Int64, _ language: MeetingLanguage) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET language = ?, updated_at = ? WHERE id = ?", arguments: [language.rawValue, Clock.now(), id])
        }
    }

    func updateInfo(_ id: Int64, title: String, date: String, participants: String) throws {
        try pool.write { db in
            guard var m = try Meeting.fetchOne(db, key: id) else { return }
            if m.title != title { m.titleLocked = true }
            m.title = title
            m.meetingDate = date
            m.participants = participants
            m.updatedAt = Clock.now()
            try m.update(db)
            try Self.index(db, meetingId: id, kind: .title, ref: 0, text: m.displayTitle)
        }
    }

    // MARK: 逐字稿

    /// 「說話者 1」「Speaker 1」「話者 1」這種預設名稱
    static func isDefaultSpeakerName(_ name: String) -> Bool {
        (try? #/^(說話者|Speaker|話者) \d+$/#.wholeMatch(in: name)) != nil
    }

    /// 寫入正式逐字稿：取代舊的正式版，並刪掉即時預覽（design §5.3）
    /// voices：每位說話者的聲紋、說話秒數、收音（design §21.4），寫進 speakers
    /// transcribedWith：用哪個引擎轉錄的（breeze、japanese、system，design §21.17）；匯入的逐字稿不寫
    func saveTranscript(_ meetingId: Int64, segments: [Segment], duration: Int, speakerCount: Int,
                        voices: [Int: SpeakerVoice] = [:], transcribedWith: String? = nil) throws {
        try pool.write { db in
            try Self.unindexSegments(db, meetingId: meetingId)
            try Segment.filter(Column("meeting_id") == meetingId).deleteAll(db)
            // 逐字稿換掉了，說話者的復原資料對不上（design §24.2）
            try db.execute(sql: "DELETE FROM speaker_undo WHERE meeting_id = ?", arguments: [meetingId])
            for var s in segments {
                try s.insert(db)
                try Self.indexSegment(db, meetingId: meetingId, id: s.id!, text: s.text)
            }
            // 說話者：保留已改過的名字，新的編號補上預設名字（跟著會議語言）
            let language = try Meeting.fetchOne(db, key: meetingId)?.meetingLanguage ?? .zh
            let existing = try SpeakerName.filter(Column("meeting_id") == meetingId).fetchAll(db)
            // 預設名稱（任何語言的「說話者 1」）不算改過，換了會議語言後要用新語言的預設名稱
            let named = Dictionary(uniqueKeysWithValues: existing.filter { !Self.isDefaultSpeakerName($0.name) }.map { ($0.idx, $0) })
            try SpeakerName.filter(Column("meeting_id") == meetingId).deleteAll(db)
            for idx in stride(from: 1, through: speakerCount, by: 1) {
                var speaker = SpeakerName(meetingId: meetingId, idx: idx,
                                          name: language.text(zh: "說話者 \(idx)", en: "Speaker \(idx)", ja: "話者 \(idx)"))
                if let old = named[idx] {
                    speaker.name = old.name
                    speaker.nameSource = old.nameSource == "default" ? "manual" : old.nameSource
                    speaker.personId = old.personId
                }
                if let voice = voices[idx] {
                    speaker.embedding = Voiceprint.data(voice.embedding)
                    speaker.speechSeconds = voice.seconds
                    speaker.audioSource = voice.source
                }
                try speaker.insert(db)
            }
            if var m = try Meeting.fetchOne(db, key: meetingId) {
                m.durationSeconds = duration
                if let transcribedWith { m.transcribedWith = transcribedWith }
                m.updatedAt = Clock.now()
                try m.update(db)
            }
        }
    }

    /// 錄音中的即時預覽段落（kind = live）
    func appendLiveSegment(_ meetingId: Int64, start: Double, end: Double, text: String) throws {
        try pool.write { db in
            let seq = try Int.fetchOne(db, sql: "SELECT COALESCE(MAX(seq), -1) + 1 FROM segments WHERE meeting_id = ?",
                                       arguments: [meetingId]) ?? 0
            var segment = Segment(meetingId: meetingId, seq: seq, start: start, end: end, text: text, lowConfidence: false,
                                  speaker: nil, kind: "live")
            try segment.insert(db)
        }
    }

    func updateSegmentText(_ id: Int64, text: String) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE segments SET text = ? WHERE id = ?", arguments: [text, id])
            try db.execute(sql: "UPDATE search SET text = ? WHERE rowid = ?", arguments: [text, id])
        }
    }

    // MARK: 編輯中的草稿（design §21.15、R20.6）

    /// key：review:<題目>、review-skip:<題目>、segment:<段落 id>
    func drafts(_ meetingId: Int64) throws -> [String: String] {
        try pool.read { db in
            Dictionary(try Row.fetchAll(db, sql: "SELECT key, text FROM drafts WHERE meeting_id = ?", arguments: [meetingId])
                .map { ($0["key"] as String, $0["text"] as String) }, uniquingKeysWith: { a, _ in a })
        }
    }

    /// 最近改過、key 以 prefix 開頭的草稿
    func latestDraft(_ meetingId: Int64, prefix: String) throws -> (key: String, text: String)? {
        try pool.read { db in
            try Row.fetchOne(db, sql: "SELECT key, text FROM drafts WHERE meeting_id = ? AND substr(key, 1, ?) = ? ORDER BY updated_at DESC LIMIT 1",
                             arguments: [meetingId, prefix.count, prefix]).map { ($0["key"], $0["text"]) }
        }
    }

    /// 存草稿；text 是空的就刪掉
    func setDraft(_ meetingId: Int64, key: String, text: String) throws {
        try pool.write { db in
            if text.isEmpty {
                try db.execute(sql: "DELETE FROM drafts WHERE meeting_id = ? AND key = ?", arguments: [meetingId, key])
            } else {
                try db.execute(sql: """
                    INSERT INTO drafts (meeting_id, key, text, updated_at) VALUES (?, ?, ?, ?)
                    ON CONFLICT (meeting_id, key) DO UPDATE SET text = excluded.text, updated_at = excluded.updated_at
                    """, arguments: [meetingId, key, text, Clock.now()])
            }
        }
    }

    func deleteDrafts(_ meetingId: Int64, prefix: String) throws {
        try pool.write { db in
            try db.execute(sql: "DELETE FROM drafts WHERE meeting_id = ? AND substr(key, 1, ?) = ?", arguments: [meetingId, prefix.count, prefix])
        }
    }

    // MARK: 逐字稿校正（design §17.4）

    func corrections(_ meetingId: Int64) throws -> [Correction] {
        try pool.read { db in try Correction.filter(Column("meeting_id") == meetingId).order(Column("id")).fetchAll(db) }
    }

    /// 存一次校正的結果：之前還沒處理的建議換成這次的，已接受、已拒絕的保留
    func saveCorrections(_ meetingId: Int64, _ items: [Correction]) throws {
        try pool.write { db in
            try db.execute(sql: "DELETE FROM corrections WHERE meeting_id = ? AND status = 'pending'", arguments: [meetingId])
            for var item in items { try item.insert(db) }
        }
    }

    /// 接受：改段落文字（含搜尋索引）。段落之後被改過、找不到原文時回傳 false
    @discardableResult
    func acceptCorrection(_ id: Int64) throws -> Bool {
        try pool.write { db in try Self.acceptCorrection(db, id) }
    }

    /// 一次接受多則（同一個詞的每一處，或全部，design §21.11）：同一個交易；回傳沒套用的數量（段落之後改過）
    func acceptCorrections(_ ids: [Int64]) throws -> Int {
        try pool.write { db in try ids.filter { try !Self.acceptCorrection(db, $0) }.count }
    }

    private static func acceptCorrection(_ db: Database, _ id: Int64) throws -> Bool {
        guard var correction = try Correction.fetchOne(db, key: id), correction.status == "pending",
              let segment = try Segment.fetchOne(db, key: correction.segmentId),
              let text = TranscriptCorrection.apply(correction.suggestionValue, to: segment.text) else { return false }
        try db.execute(sql: "UPDATE segments SET text = ? WHERE id = ?", arguments: [text, segment.id])
        try db.execute(sql: "UPDATE search SET text = ? WHERE rowid = ?", arguments: [text, segment.id])
        correction.status = "accepted"
        try correction.update(db)
        return true
    }

    /// 還原已接受的建議：改回原文，回到待確認
    @discardableResult
    func revertCorrection(_ id: Int64) throws -> Bool {
        try pool.write { db in
            guard var correction = try Correction.fetchOne(db, key: id), correction.status == "accepted",
                  let segment = try Segment.fetchOne(db, key: correction.segmentId),
                  let text = TranscriptCorrection.revert(correction.suggestionValue, in: segment.text) else { return false }
            try db.execute(sql: "UPDATE segments SET text = ? WHERE id = ?", arguments: [text, segment.id])
            try db.execute(sql: "UPDATE search SET text = ? WHERE rowid = ?", arguments: [text, segment.id])
            correction.status = "pending"
            try correction.update(db)
            return true
        }
    }

    func rejectCorrection(_ id: Int64) throws {
        try rejectCorrections([id])
    }

    func rejectCorrections(_ ids: [Int64]) throws {
        try pool.write { db in
            for id in ids {
                try db.execute(sql: "UPDATE corrections SET status = 'rejected' WHERE id = ? AND status = 'pending'", arguments: [id])
            }
        }
    }

    func speakers(_ meetingId: Int64) throws -> [SpeakerName] {
        try pool.read { db in try SpeakerName.filter(Column("meeting_id") == meetingId).order(Column("idx")).fetchAll(db) }
    }

    /// 只改名字和名字的來源，不動聲紋等其他欄位（design §21.1）
    func renameSpeaker(_ meetingId: Int64, idx: Int, name: String) throws {
        try pool.write { db in
            try db.execute(sql: """
                INSERT INTO speakers (meeting_id, idx, name, name_source) VALUES (?, ?, ?, ?)
                ON CONFLICT (meeting_id, idx) DO UPDATE SET name = excluded.name, name_source = excluded.name_source
                """, arguments: [meetingId, idx, name, Self.isDefaultSpeakerName(name) ? "default" : "manual"])
            // 改名之後不能再復原合併、改句子（復原會把名字一起換回去，跟會議紀錄對不上，design §24.4）
            try db.execute(sql: "DELETE FROM speaker_undo WHERE meeting_id = ?", arguments: [meetingId])
        }
    }

    // MARK: 重點與筆記

    func bookmarks(_ meetingId: Int64) throws -> [Bookmark] {
        try pool.read { db in try Bookmark.filter(Column("meeting_id") == meetingId).order(Column("time")).fetchAll(db) }
    }

    func addBookmark(_ meetingId: Int64, time: Double, label: String = "") throws {
        try pool.write { db in
            var bookmark = Bookmark(meetingId: meetingId, time: time, label: label)
            try bookmark.insert(db)
        }
    }

    func deleteBookmark(_ id: Int64) throws {
        _ = try pool.write { db in try Bookmark.deleteOne(db, key: id) }
    }

    func updateUserNotes(_ meetingId: Int64, _ text: String) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET user_notes = ?, updated_at = ? WHERE id = ?",
                           arguments: [text, Clock.now(), meetingId])
            try Self.index(db, meetingId: meetingId, kind: .userNotes, ref: 0, text: text)
        }
    }

    // MARK: 會議紀錄

    /// 寫入新的會議紀錄，舊的那一版存進 notes_versions（R6.7）
    /// 會議紀錄怎麼來的，決定草稿狀態和要不要留版本（design §21.3）
    enum NotesSource {
        case edit        // 手動編輯、取代、略過待確認、匯入：草稿被改過就標成 2（改過的草稿）
        case draft       // 用即時逐字稿整理的草稿：notes_draft = 1，不留版本
        case generated   // 正式整理：取代沒改過的草稿不留版本；取代改過的草稿留一版（原因「草稿（改過）」）
        case revised     // 校稿：在草稿上校稿，結果仍然是草稿（2）
    }

    /// 回傳是否取代了使用者改過的草稿（畫面要提示，design §21.3）
    @discardableResult
    func saveNotes(_ meetingId: Int64, notes: MeetingNotes, by provider: String?, reason: String, source: NotesSource = .edit) throws -> Bool {
        try pool.write { db in try Self.saveNotes(db, meetingId, notes: notes, by: provider, reason: reason, source: source) }
    }

    @discardableResult
    static func saveNotes(_ db: Database, _ meetingId: Int64, notes: MeetingNotes, by provider: String?, reason: String,
                                  source: NotesSource = .edit) throws -> Bool {
        guard var m = try Meeting.fetchOne(db, key: meetingId) else { return false }
        let wasDraft = m.notesDraft
        var keepVersion = true
        var versionReason = reason
        switch source {
        case .edit: if wasDraft != 0 { m.notesDraft = 2 }
        case .draft:
            keepVersion = false
            m.notesDraft = 1
        case .generated:
            if wasDraft == 1 { keepVersion = false }
            if wasDraft == 2 { versionReason = String(localized: "草稿（改過）") }
            m.notesDraft = 0
        case .revised: if wasDraft != 0 { m.notesDraft = 2 }
        }
        if keepVersion, let old = m.notes {
            var version = NotesVersion(meetingId: meetingId, notes: old, reason: versionReason)
            try version.insert(db)
        }
        m.notes = notes.jsonString()
        if let provider { m.notesBy = provider }
        if !m.titleLocked && !notes.title.trimmingCharacters(in: .whitespaces).isEmpty {
            m.title = notes.title.trimmingCharacters(in: .whitespaces)
        }
        m.updatedAt = Clock.now()
        try m.update(db)
        try index(db, meetingId: meetingId, kind: .notes, ref: 0, text: notes.plainText)
        try index(db, meetingId: meetingId, kind: .title, ref: 0, text: m.displayTitle)
        return source == .generated && wasDraft == 2
    }

    /// 復原上一版：成功回傳 true
    func undoNotes(_ meetingId: Int64) throws -> Bool {
        try pool.write { db in try Self.undoNotes(db, meetingId) }
    }

    static func undoNotes(_ db: Database, _ meetingId: Int64) throws -> Bool {
        guard let version = try NotesVersion.filter(Column("meeting_id") == meetingId).order(Column("id").desc).fetchOne(db),
              var m = try Meeting.fetchOne(db, key: meetingId) else { return false }
        m.notes = version.notes
        m.notesDraft = 0   // 復原的是之前的某一版，不再當草稿自動更新
        if !m.titleLocked, let title = m.decodedNotes?.title, !title.isEmpty { m.title = title }
        m.updatedAt = Clock.now()
        try m.update(db)
        _ = try version.delete(db)
        try index(db, meetingId: meetingId, kind: .notes, ref: 0, text: m.decodedNotes?.plainText ?? "")
        try index(db, meetingId: meetingId, kind: .title, ref: 0, text: m.displayTitle)
        return true
    }

    /// 全文取代：逐字稿＋會議紀錄（沿用第二版）
    func replace(_ meetingId: Int64, find: String, with replacement: String) throws -> ReplaceResult {
        try pool.write { db in
            var changed = 0
            for var s in try Segment.filter(Column("meeting_id") == meetingId).fetchAll(db) where s.text.contains(find) {
                s.text = s.text.replacingOccurrences(of: find, with: replacement)
                try s.update(db)
                try db.execute(sql: "UPDATE search SET text = ? WHERE rowid = ?", arguments: [s.text, s.id])
                changed += 1
            }
            var notesChanged = false
            if let m = try Meeting.fetchOne(db, key: meetingId), let notes = m.decodedNotes {
                let updated = notes.replacing(find, with: replacement)
                if updated != notes {
                    try Self.saveNotes(db, meetingId, notes: updated, by: nil, reason: String(localized: "取代「\(find)」→「\(replacement)」"))
                    notesChanged = true
                }
            }
            return ReplaceResult(segmentsChanged: changed, notesChanged: notesChanged)
        }
    }

    /// 只略過、不需要 LLM 的待確認題目：直接從會議紀錄移除
    func dismissReviewItems(_ meetingId: Int64, indexes: Set<Int>) throws {
        try resolveReviewItems(meetingId, confirmed: [], dismissed: indexes)
    }

    /// 確認（是非題回答「是」）和略過的題目直接從紀錄移除（R41.4、design §31.3），留一個版本；兩種都沒有就不動
    func resolveReviewItems(_ meetingId: Int64, confirmed: Set<Int>, dismissed: Set<Int>) throws {
        let indexes = confirmed.union(dismissed)
        guard !indexes.isEmpty else { return }
        let reason = dismissed.isEmpty ? String(localized: "確認待確認")
            : confirmed.isEmpty ? String(localized: "略過待確認") : String(localized: "確認、略過待確認")
        try pool.write { db in
            guard let m = try Meeting.fetchOne(db, key: meetingId), var notes = m.decodedNotes else { return }
            notes.reviewItems = notes.reviewItems.enumerated().filter { !indexes.contains($0.offset) }.map(\.element)
            try Self.saveNotes(db, meetingId, notes: notes, by: nil, reason: reason)
        }
    }

    // MARK: 搜尋（R9.1）

    /// 搜尋表的 rowid（design §20.11、D27）：段落用段落的 id；標題、會議紀錄、筆記用 2^50 + 會議 id × 4 + 種類，
    /// 不會跟段落重疊。FTS5 只能用 MATCH 或 rowid 找列，用其他欄位刪改每次都要掃整張表，所以一律用 rowid。
    /// 新增時一定要指定 rowid：自動編號會接在最大的 rowid 後面，之後可能跟推算出來的值撞在一起
    private static let metaRowidBase: Int64 = 1 << 50

    private static func searchRowid(meetingId: Int64, kind: SearchHit.Kind) -> Int64 {
        let code: Int64 = switch kind {
        case .title: 1
        case .notes: 2
        case .userNotes: 3
        case .segment: preconditionFailure("段落的 rowid 就是段落的 id")   // l10n-skip
        }
        return metaRowidBase + meetingId * 4 + code
    }

    private static func indexSegment(_ db: Database, meetingId: Int64, id: Int64, text: String) throws {
        try db.execute(sql: "INSERT INTO search (rowid, meeting_id, kind, ref, text) VALUES (?, ?, 'segment', ?, ?)",
                       arguments: [id, meetingId, id, text])
    }

    /// 刪掉這場會議所有段落的索引（段落還在資料庫裡的時候呼叫）
    private static func unindexSegments(_ db: Database, meetingId: Int64) throws {
        try db.execute(sql: "DELETE FROM search WHERE rowid IN (SELECT id FROM segments WHERE meeting_id = ?)", arguments: [meetingId])
    }

    /// 刪掉這場會議的標題、會議紀錄、筆記的索引
    private static func unindexMeeting(_ db: Database, meetingId: Int64) throws {
        let ids = [SearchHit.Kind.title, .notes, .userNotes].map { searchRowid(meetingId: meetingId, kind: $0) }
        try db.execute(sql: "DELETE FROM search WHERE rowid IN (?, ?, ?)", arguments: StatementArguments(ids))
    }

    /// 標題、會議紀錄、筆記（ref 是 0）
    private static func index(_ db: Database, meetingId: Int64, kind: SearchHit.Kind, ref: Int64, text: String) throws {
        let rowid = searchRowid(meetingId: meetingId, kind: kind)
        try db.execute(sql: "DELETE FROM search WHERE rowid = ?", arguments: [rowid])
        guard !text.isEmpty else { return }
        try db.execute(sql: "INSERT INTO search (rowid, meeting_id, kind, ref, text) VALUES (?, ?, ?, ?, ?)",
                       arguments: [rowid, meetingId, kind.rawValue, ref, text])
    }

    private static func rebuildSearch(_ db: Database) throws {
        try db.execute(sql: "DELETE FROM search")
        try db.execute(sql: "INSERT INTO search (rowid, meeting_id, kind, ref, text) SELECT id, meeting_id, 'segment', id, text FROM segments")
        // 只讀需要的欄位，不要整筆解碼成 Meeting：migration 裡呼叫時，後面版本才加的欄位還不存在（design §21.1）
        let rows = try Row.fetchAll(db, sql: "SELECT id, title, source_filename, user_notes, notes FROM meetings")
        for row in rows {
            let m = Meeting(id: row["id"], title: row["title"], meetingDate: "", sourceFilename: row["source_filename"],
                            notes: row["notes"], userNotes: row["user_notes"])
            try index(db, meetingId: m.id!, kind: .title, ref: 0, text: m.displayTitle)
            try index(db, meetingId: m.id!, kind: .userNotes, ref: 0, text: m.userNotes)
            if let notes = m.decodedNotes { try index(db, meetingId: m.id!, kind: .notes, ref: 0, text: notes.plainText) }
        }
    }

    func rebuildSearchIndex() throws {
        try pool.write { try Self.rebuildSearch($0) }
    }

    /// trigram 至少要 3 個字；1～2 個字改用 LIKE
    func search(_ query: String, limit: Int = 200) throws -> [SearchHit] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return [] }
        return try pool.read { db in
            let rows: [Row]
            if q.count >= 3 {
                let phrase = "\"" + q.replacingOccurrences(of: "\"", with: "\"\"") + "\""
                rows = try Row.fetchAll(db, sql: """
                    SELECT s.meeting_id, s.kind, s.ref, s.text FROM search s
                    JOIN meetings m ON m.id = s.meeting_id AND m.deleted_at IS NULL
                    WHERE search MATCH ? ORDER BY m.meeting_date DESC, s.meeting_id DESC, s.ref LIMIT ?
                    """, arguments: [phrase, limit])
            } else {
                rows = try Row.fetchAll(db, sql: """
                    SELECT s.meeting_id, s.kind, s.ref, s.text FROM search s
                    JOIN meetings m ON m.id = s.meeting_id AND m.deleted_at IS NULL
                    WHERE s.text LIKE ? ESCAPE '\\' ORDER BY m.meeting_date DESC, s.meeting_id DESC, s.ref LIMIT ?
                    """, arguments: ["%" + q.replacingOccurrences(of: "%", with: "\\%").replacingOccurrences(of: "_", with: "\\_") + "%", limit])
            }
            return rows.compactMap { row in
                guard let kind = SearchHit.Kind(rawValue: row["kind"]) else { return nil }
                let text: String = row["text"]
                return SearchHit(meetingId: row["meeting_id"], kind: kind, ref: row["ref"], snippet: Self.snippet(text, around: q))
            }
        }
    }

    private static func snippet(_ text: String, around query: String) -> String {
        guard let range = text.range(of: query, options: .caseInsensitive) else { return String(text.prefix(80)) }
        let start = text.index(range.lowerBound, offsetBy: -30, limitedBy: text.startIndex) ?? text.startIndex
        let end = text.index(range.upperBound, offsetBy: 50, limitedBy: text.endIndex) ?? text.endIndex
        return (start > text.startIndex ? "…" : "") + text[start..<end].replacingOccurrences(of: "\n", with: " ")
            + (end < text.endIndex ? "…" : "")
    }

    // MARK: 範本

    /// 內建範本依固定順序（一般會議在最前面），自訂範本接在後面依名稱排序
    func templates() throws -> [TemplateRecord] {
        let order = BuiltinTemplates.all.map(\.id)
        return try pool.read { db in try TemplateRecord.fetchAll(db) }.sorted { a, b in
            switch (order.firstIndex(of: a.id), order.firstIndex(of: b.id)) {
            case let (x?, y?): return x < y
            case (_?, nil): return true
            case (nil, _?): return false
            default: return a.name < b.name
            }
        }
    }

    func saveTemplate(_ template: TemplateRecord) throws {
        try pool.write { db in
            var t = template
            t.updatedAt = Clock.now()
            try t.save(db)
        }
    }

    func deleteTemplate(_ id: String) throws {
        _ = try pool.write { db in try TemplateRecord.deleteOne(db, key: id) }
    }

    // MARK: 工作

    @discardableResult
    func enqueue(_ meetingId: Int64, kind: JobKind, payload: String? = nil) throws -> Job {
        #if DEBUG
        // 啟動參數 -debugFailEnqueue YES：排工作失敗，驗證失敗時不會刪掉錄音（design §20.6）
        if UserDefaults.standard.bool(forKey: "debugFailEnqueue") {
            throw DatabaseError(resultCode: .SQLITE_FULL, message: "debugFailEnqueue")
        }
        #endif
        return try pool.write { db in
            var job = Job(meetingId: meetingId, kind: kind, payload: payload)
            try job.insert(db)
            return job
        }
    }

    /// 取出下一個排隊的工作，改成 running。模型還沒下載好的語言也照樣轉錄（先用 macOS 內建辨識，design §21.17）
    func claimNext(lane: JobLane) throws -> Job? {
        let kinds = JobKind.allCases.filter { $0.lane == lane }.map(\.rawValue)
        return try pool.write { db in
            guard var job = try Job.filter(Column("status") == JobStatus.queued.rawValue && kinds.contains(Column("kind")))
                .order(Column("id")).fetchOne(db) else { return nil }
            job.status = .running
            job.startedAt = Clock.now()
            try job.update(db)
            return job
        }
    }

    /// errorKind：錯誤的種類（Error.logSummary），回報問題時用，不含內容（design §21.9）
    func finish(_ jobId: Int64, status: JobStatus, error: String? = nil, errorKind: String? = nil) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE jobs SET status = ?, error = ?, error_kind = ?, finished_at = ? WHERE id = ?",
                           arguments: [status.rawValue, error, errorKind, Clock.now(), jobId])
        }
    }

    /// app 啟動時：上次沒跑完的工作改回排隊
    /// 啟動時：還是 running 的工作表示上次執行時拾音意外結束（design §20.5、D26）。
    /// 意外結束累計 2 次就標成失敗，不再自動重跑；不到 2 次才改回排隊
    func requeueRunning() throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE jobs SET crashes = crashes + 1 WHERE status = 'running'")
            try db.execute(sql: "UPDATE jobs SET status = 'failed', error = ?, finished_at = ? WHERE status = 'running' AND crashes >= 2",
                           arguments: [String(localized: "執行這個工作時拾音意外結束了 2 次，已經停止自動重試。可以按「重試」再試一次。"), Clock.now()])
            try db.execute(sql: "UPDATE jobs SET status = 'queued', started_at = NULL WHERE status = 'running'")
        }
    }

    /// 正常結束拾音時：執行中的工作改回排隊，下次開啟從頭重跑，不算意外結束（design §20.5）
    func releaseRunning() throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE jobs SET status = 'queued', started_at = NULL WHERE status = 'running'")
        }
    }

    /// 取消這場會議所有排隊中、執行中的工作，回傳原本執行中的工作 id
    func cancelJobs(_ meetingId: Int64) throws -> [Int64] {
        try pool.write { db in
            let running = try Int64.fetchAll(db, sql: "SELECT id FROM jobs WHERE meeting_id = ? AND status = 'running'",
                                              arguments: [meetingId])
            try db.execute(sql: """
                UPDATE jobs SET status = 'cancelled', finished_at = ?
                WHERE meeting_id = ? AND status IN ('queued', 'running')
                """, arguments: [Clock.now(), meetingId])
            return running
        }
    }

    /// 這場會議有沒有排隊中或執行中的某種工作
    func hasActiveJob(_ meetingId: Int64, kind: JobKind) throws -> Bool {
        try pool.read { db in
            try Job.filter(Column("meeting_id") == meetingId && Column("kind") == kind.rawValue
                           && [JobStatus.queued.rawValue, JobStatus.running.rawValue].contains(Column("status"))).fetchCount(db) > 0
        }
    }

    /// 有沒有某個語言的會議在排隊等轉錄
    func hasQueuedTranscribe(language: MeetingLanguage) throws -> Bool {
        try pool.read { db in
            try Bool.fetchOne(db, sql: """
                SELECT EXISTS (SELECT 1 FROM jobs j JOIN meetings m ON m.id = j.meeting_id
                               WHERE j.status = 'queued' AND j.kind = 'transcribe' AND m.language = ?)
                """, arguments: [language.rawValue]) ?? false
        }
    }

    func jobStatus(_ jobId: Int64) throws -> JobStatus? {
        try pool.read { db in try Job.fetchOne(db, key: jobId)?.status }
    }

    func activeJobs() throws -> [Job] {
        try pool.read { db in
            try Job.filter(["queued", "running"].contains(Column("status"))).order(Column("id")).fetchAll(db)
        }
    }

    // MARK: 詞彙表

    func glossary() throws -> [GlossaryTerm] {
        try pool.read { db in try GlossaryTerm.order(Column("id")).fetchAll(db) }
    }

    func addTerm(_ term: String) throws {
        let trimmed = term.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        try pool.write { db in
            try db.execute(sql: "INSERT OR IGNORE INTO glossary (term, created_at) VALUES (?, ?)", arguments: [trimmed, Clock.now()])
        }
    }

    func deleteTerm(_ id: Int64) throws {
        _ = try pool.write { db in try GlossaryTerm.deleteOne(db, key: id) }
    }

    /// 詞彙表建議（design §17.5、R14.5）：從參加者、改過名字的說話者、接受過的校正、中文會議標題裡的英文詞找。
    /// 已經在詞彙表裡、被忽略過的不建議；出現越多次排越前面
    func glossarySuggestions(limit: Int = 12) throws -> [GlossarySuggestion] {
        try pool.read { db in
            var found: [String: (term: String, source: GlossarySuggestion.Source, count: Int)] = [:]
            func add(_ raw: String, _ source: GlossarySuggestion.Source) {
                let term = raw.trimmingCharacters(in: .whitespacesAndNewlines)
                guard (2...40).contains(term.count) else { return }
                let key = term.lowercased()
                if let existing = found[key] { found[key]?.count = existing.count + 1 } else { found[key] = (term, source, 1) }
            }
            let meetings = try Meeting.filter(Column("deleted_at") == nil).fetchAll(db)
            for meeting in meetings { meeting.participantList.forEach { add($0, .participant) } }
            let names = try String.fetchAll(db, sql: """
                SELECT s.name FROM speakers s JOIN meetings m ON m.id = s.meeting_id WHERE m.deleted_at IS NULL
                """)
            for name in names where !Self.isDefaultSpeakerName(name) { add(name, .speaker) }
            let accepted = try String.fetchAll(db, sql: "SELECT suggestion FROM corrections WHERE status = 'accepted'")
            for term in accepted where term.contains(where: { $0.isASCII && $0.isLetter }) { add(term, .correction) }
            // 中文、日文會議標題裡的英文詞通常是產品名、專案名；英文會議的標題就不拆了
            let word = #/[A-Za-z][A-Za-z0-9.+\-]{2,}/#
            for meeting in meetings where meeting.meetingLanguage != .en {
                for match in meeting.title.matches(of: word) { add(String(match.output), .title) }
            }
            let existing = Set(try String.fetchAll(db, sql: "SELECT term FROM glossary").map { $0.lowercased() })
            let dismissed = Set(try String.fetchAll(db, sql: "SELECT term FROM glossary_dismissed").map { $0.lowercased() })
            return found.filter { !existing.contains($0.key) && !dismissed.contains($0.key) }
                .values.sorted { $0.count != $1.count ? $0.count > $1.count : $0.term < $1.term }
                .prefix(limit).map { GlossarySuggestion(term: $0.term, source: $0.source) }
        }
    }

    func dismissGlossarySuggestion(_ term: String) throws {
        try pool.write { db in
            try db.execute(sql: "INSERT OR IGNORE INTO glossary_dismissed (term) VALUES (?)", arguments: [term])
        }
    }

    // MARK: 儲存空間

    func setAudioFile(_ meetingId: Int64, _ filename: String) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET audio_file = ? WHERE id = ?", arguments: [filename, meetingId])
        }
    }
}
