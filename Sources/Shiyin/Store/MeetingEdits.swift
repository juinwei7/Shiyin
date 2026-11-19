import Foundation
import GRDB

/// 編輯會議的快照（migration v15、design §81.3）：只留第一次編輯前的樣子，最後一次編輯後 30 天刪掉（D179）
struct MeetingEditRecord: SnakeCaseRecord {
    static let databaseTableName = "meeting_edits"
    var meetingId: Int64
    var snapshot: String
    /// 編輯前的錄音，audio/ 底下的相對路徑（edits/<id>.<副檔名>）；沒有錄音的會議是空字串
    var audioFile: String
    var splitInto: String = "[]"
    var mergedFrom: String = "[]"
    var createdAt: String = Clock.now()
    var editedAt: String = Clock.now()

    static let keepDays = 30

    var splitIds: [Int64] { (try? JSONDecoder().decode([Int64].self, from: Data(splitInto.utf8))) ?? [] }
    var mergedIds: [Int64] { (try? JSONDecoder().decode([Int64].self, from: Data(mergedFrom.utf8))) ?? [] }

    /// 還剩幾天自動刪掉（至少 1，當天到期也說 1 天）
    func daysLeft(now: Date = Date()) -> Int {
        guard let edited = ISO8601DateFormatter().date(from: editedAt) else { return Self.keepDays }
        let left = Double(Self.keepDays) - now.timeIntervalSince(edited) / 86_400
        return max(1, Int(left.rounded(.up)))
    }

    static func encodeIds(_ ids: [Int64]) -> String {
        String(decoding: (try? JSONEncoder().encode(ids)) ?? Data("[]".utf8), as: UTF8.self)
    }
}

/// 編輯前這場會議的樣子（§81.3）：欄位、所有句子（含精簡版、校正建議）、標記重點、說話者、參考文件的 id
struct EditSnapshot: Codable {
    struct Line: Codable {
        var segment: Segment
        var clean: String?
        var corrections: [Correction]
    }
    var audioFile: String
    var durationSeconds: Int?
    var userNotes: String
    var notes: String?
    var notesBy: String?
    var notesDraft: Int
    var createdAt: String
    var meetingDate: String
    var participants: String
    var lines: [Line]
    var bookmarks: [Bookmark]
    var speakers: [SpeakerName]
    var documentIds: [Int64]
    /// 會議中的畫面的時間（design §83.7）：還原時放回這場、改回原本的時間；沒有畫面時不寫（快照跟以前一樣）
    var screens: [ScreenTime]? = nil

    struct ScreenTime: Codable { var id: Int64; var at: Double }
}

extension AppDatabase {
    struct EditOutcome {
        var meeting: Meeting
        /// 分成兩場時分出去的那場
        var created: Meeting?
        /// 第一次編輯：原本的錄音要搬到 audio/edits/（之後的編輯，舊的錄音直接刪）
        var firstEdit: Bool
        /// 合併時複製過來的參考文件：documents/ 底下的來源 → 目的
        var documentCopies: [(from: String, to: String)]
    }

    struct RestoreOutcome {
        /// 現在的錄音（要刪），audio/ 底下的檔名
        var currentAudio: String
        /// 快照的錄音（audio/edits/…）要搬回 audio/<這個名字>；沒有錄音是空字串
        var editsAudio: String
        var restoredAudio: String
        var trashed: [Int64]
        var untrashed: [Int64]
        var removedDocuments: [String]
    }

    func editRecord(_ meetingId: Int64) throws -> MeetingEditRecord? {
        try pool.read { db in try MeetingEditRecord.fetchOne(db, key: meetingId) }
    }

    func expiredEdits(olderThan days: Int) throws -> [MeetingEditRecord] {
        let cutoff = Date().addingTimeInterval(-Double(days) * 86_400).ISO8601Format()
        return try pool.read { db in try MeetingEditRecord.filter(Column("edited_at") < cutoff).fetchAll(db) }
    }

    func deleteEditRecord(_ meetingId: Int64) throws {
        try pool.write { db in _ = try MeetingEditRecord.deleteOne(db, key: meetingId) }
    }

    #if DEBUG
    /// DEBUG 的 editage：把最後編輯的時間改早，測 30 天的清除
    func ageEdit(_ meetingId: Int64, days: Double) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meeting_edits SET edited_at = ? WHERE meeting_id = ?",
                           arguments: [Date().addingTimeInterval(-days * 86_400).ISO8601Format(), meetingId])
        }
    }
    #endif

    /// 還在用的編輯前錄音（audio/ 底下的相對路徑），清 audio/edits/ 時留著
    func editAudioFiles() throws -> Set<String> {
        try pool.read { db in Set(try String.fetchAll(db, sql: "SELECT audio_file FROM meeting_edits WHERE audio_file <> ''")) }
    }

    /// 套用編輯（R107.7、design §81.1）：一個交易改這場的句子、標記重點、說話者、筆記、會議紀錄的時間；
    /// 合併時另一場的資料複製過來、那場移到垃圾桶；cut 有值時接著分成兩場（新時間軸上的時間）。
    /// audioExtension 是新錄音的副檔名（沒有錄音是 nil）；錄音檔由呼叫的人搬
    func applyEdit(_ id: Int64, plan: EditPlan, cut: Double?, splitTitle: String, audioExtension: String?) throws -> EditOutcome? {
        try pool.write { db -> EditOutcome? in
            guard var meeting = try Meeting.fetchOne(db, key: id) else { return nil }
            let now = Clock.now()
            let others = plan.pieces.map(\.meetingId).reduce(into: [Int64]()) { if $1 != id && !$0.contains($1) { $0.append($1) } }

            // 快照：第一次編輯才存（§81.3）
            var record = try MeetingEditRecord.fetchOne(db, key: id)
            let firstEdit = record == nil
            if record == nil {
                let ext = (meeting.audioFile as NSString).pathExtension
                record = MeetingEditRecord(meetingId: id, snapshot: try Self.snapshot(db, meeting),
                                           audioFile: meeting.audioFile.isEmpty ? "" : "edits/\(id).\(ext)", createdAt: now, editedAt: now)
            }
            record!.editedAt = now
            record!.mergedFrom = MeetingEditRecord.encodeIds(record!.mergedIds + others.filter { !record!.mergedIds.contains($0) })

            // 說話者時間的比例（§81.1）：每個說話者正式逐字稿留下的長度 ÷ 原本的長度
            var spoken: [Int64: [Int: (before: Double, after: Double)]] = [:]
            func count(_ meetingId: Int64, _ segment: Segment, kept: (start: Double, end: Double)?) {
                guard segment.kind == "final", let idx = segment.speaker else { return }
                var entry = spoken[meetingId, default: [:]][idx, default: (0, 0)]
                entry.before += max(0, segment.end - segment.start)
                if let kept { entry.after += max(0, kept.end - kept.start) }
                spoken[meetingId, default: [:]][idx] = entry
            }

            // 這場的句子：留下的改時間，其餘刪掉（精簡版、校正建議跟著 ON DELETE CASCADE）
            try Self.unindexSegments(db, meetingId: id)
            for segment in try Segment.filter(Column("meeting_id") == id).fetchAll(db) {
                let kept = plan.mapSentence(id, start: segment.start, end: segment.end)
                count(id, segment, kept: kept)
                if let kept {
                    try db.execute(sql: "UPDATE segments SET start = ?, end = ? WHERE id = ?", arguments: [kept.start, kept.end, segment.id])
                } else {
                    _ = try Segment.deleteOne(db, key: segment.id!)
                }
            }
            for bookmark in try Bookmark.filter(Column("meeting_id") == id).fetchAll(db) {
                if let t = plan.map(id, bookmark.time) {
                    try db.execute(sql: "UPDATE bookmarks SET time = ? WHERE id = ?", arguments: [t, bookmark.id])
                } else {
                    _ = try Bookmark.deleteOne(db, key: bookmark.id!)
                }
            }
            // 會議中的畫面（design §83.7、D190）：同我的筆記，落在刪掉那段裡的移到切口，不刪
            for screen in try MeetingDocument.filter(Column("meeting_id") == id && Column("at") != nil).fetchAll(db) {
                try db.execute(sql: "UPDATE documents SET at = ? WHERE id = ?", arguments: [plan.snap(id, screen.at!), screen.id])
            }

            // 合併：另一場的說話者對起來，句子、標記、參考文件複製過來（§81.4）
            var documentCopies: [(from: String, to: String)] = []
            var notesParts: [(order: Int, text: String)] = []
            let order = { (meetingId: Int64) in plan.pieces.firstIndex { $0.meetingId == meetingId } ?? 0 }
            notesParts.append((order(id), plan.remapUserNotes(meeting.userNotes, meetingId: id)))
            var participants = meeting.participantList
            var addedSeconds: [Int: Double] = [:]
            for other in others {
                guard let source = try Meeting.fetchOne(db, key: other) else { continue }
                let speakerMap = try Self.mergeSpeakers(db, into: id, from: other, language: meeting.meetingLanguage)
                for segment in try Segment.filter(Column("meeting_id") == other).order(Column("seq")).fetchAll(db) {
                    let kept = plan.mapSentence(other, start: segment.start, end: segment.end)
                    count(other, segment, kept: kept)
                    guard let kept else { continue }
                    var copy = segment
                    copy.id = nil
                    copy.meetingId = id
                    copy.start = kept.start
                    copy.end = kept.end
                    copy.speaker = segment.speaker.flatMap { speakerMap[$0] }
                    try copy.insert(db)
                    if let clean = try String.fetchOne(db, sql: "SELECT text FROM clean_lines WHERE segment_id = ?", arguments: [segment.id]) {
                        try db.execute(sql: "INSERT INTO clean_lines (segment_id, text) VALUES (?, ?)", arguments: [copy.id, clean])
                    }
                    for var correction in try Correction.filter(Column("segment_id") == segment.id).fetchAll(db) {
                        correction.id = nil
                        correction.meetingId = id
                        correction.segmentId = copy.id!
                        try correction.insert(db)
                    }
                }
                for bookmark in try Bookmark.filter(Column("meeting_id") == other).fetchAll(db) {
                    guard let t = plan.map(other, bookmark.time) else { continue }
                    var copy = Bookmark(meetingId: id, time: t, label: bookmark.label)
                    try copy.insert(db)
                }
                for document in try MeetingDocument.filter(Column("meeting_id") == other).order(Column("id")).fetchAll(db) {
                    var copy = document
                    copy.id = nil
                    copy.meetingId = id
                    copy.createdAt = now
                    // 會議中的畫面：時間照合併後的位置（design §83.7）
                    if let at = document.at { copy.at = plan.snap(other, at) }
                    try copy.insert(db)
                    copy.file = "\(copy.id!)/\(document.name)"
                    try copy.update(db)
                    if copy.isScreen { try AppDatabase.indexScreen(db, copy) }
                    documentCopies.append((document.file, copy.file))
                }
                notesParts.append((order(other), plan.remapUserNotes(source.userNotes, meetingId: other)))
                for name in source.participantList where !participants.contains(name) { participants.append(name) }
                for (from, to) in speakerMap {
                    let ratio = spoken[other]?[from].map { $0.before > 0 ? $0.after / $0.before : 1 } ?? 1
                    let seconds = try Double.fetchOne(db, sql: "SELECT speech_seconds FROM speakers WHERE meeting_id = ? AND idx = ?",
                                                      arguments: [other, from]) ?? 0
                    addedSeconds[to, default: 0] += seconds * ratio
                }
                try db.execute(sql: "UPDATE meetings SET deleted_at = ? WHERE id = ?", arguments: [now, other])
            }

            // 句子照新的時間重新編號（正式、即時各自）
            for kind in ["final", "live"] {
                let ids = try Int64.fetchAll(db, sql: "SELECT id FROM segments WHERE meeting_id = ? AND kind = ? ORDER BY start, seq, id",
                                             arguments: [id, kind])
                for (seq, segmentId) in ids.enumerated() {
                    try db.execute(sql: "UPDATE segments SET seq = ? WHERE id = ?", arguments: [seq, segmentId])
                }
            }

            // 說話者：沒有句子的拿掉（聲紋、人物留著，還原時才放得回去）；說話時間照留下的比例
            for speaker in try SpeakerName.filter(Column("meeting_id") == id).fetchAll(db) {
                let speaks = try Bool.fetchOne(db, sql: "SELECT EXISTS (SELECT 1 FROM segments WHERE meeting_id = ? AND speaker = ?)",
                                               arguments: [id, speaker.idx]) ?? false
                if !speaks {
                    try db.execute(sql: "DELETE FROM speakers WHERE meeting_id = ? AND idx = ?", arguments: [id, speaker.idx])
                    continue
                }
                let ratio = spoken[id]?[speaker.idx].map { $0.before > 0 ? $0.after / $0.before : 1 } ?? 1
                let seconds = (speaker.speechSeconds * ratio) + (addedSeconds[speaker.idx] ?? 0)
                try db.execute(sql: "UPDATE speakers SET speech_seconds = ? WHERE meeting_id = ? AND idx = ?",
                               arguments: [seconds, id, speaker.idx])
            }

            // 會議紀錄：舊的存成版本，時間點先移到切口（重新整理之前議題時間軸大致對得上）
            if !plan.keepsWhole(id), let notes = meeting.decodedNotes, let old = meeting.notes {
                var version = NotesVersion(meetingId: id, notes: old, reason: String(localized: "編輯會議前"))
                try version.insert(db)
                let mapped = notes.mappingTimes { plan.snap(id, $0) }
                meeting.notes = mapped.jsonString()
            }
            meeting.userNotes = notesParts.sorted { $0.order < $1.order }.map(\.text)
                .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.joined(separator: "\n\n")
            if participants != meeting.participantList {
                meeting.participants = participants.joined(separator: meeting.participants.contains("、") ? "、" : ", ")  // l10n-skip
            }
            // 開始時間：第一個片段那場的開始加上裁掉的秒數；日期跟著那場
            if let first = plan.pieces.first, let owner = try Meeting.fetchOne(db, key: first.meetingId),
               let started = ISO8601DateFormatter().date(from: owner.createdAt) {
                meeting.createdAt = started.addingTimeInterval(first.start).ISO8601Format()
                meeting.meetingDate = owner.meetingDate
            }
            meeting.durationSeconds = Int(plan.duration.rounded())
            if let audioExtension { meeting.audioFile = "\(id).\(audioExtension)" }
            meeting.updatedAt = now
            try meeting.update(db)
            try db.execute(sql: "DELETE FROM speaker_undo WHERE meeting_id = ?", arguments: [id])
            try Self.reindexMeeting(db, meeting)

            var created: Meeting?
            if let cut, let split = try Self.splitMeeting(db, id, at: cut, title: splitTitle, audioExtension: audioExtension) {
                created = split
                record!.splitInto = MeetingEditRecord.encodeIds(record!.splitIds + [split.id!])
                meeting = try Meeting.fetchOne(db, key: id) ?? meeting
            }
            try record!.save(db)
            return EditOutcome(meeting: meeting, created: created, firstEdit: firstEdit, documentCopies: documentCopies)
        }
    }

    /// 還原成編輯前的樣子（R107.8）：照快照放回欄位、句子、標記、說話者，刪掉編輯後加進來的參考文件；
    /// 分出去的移到垃圾桶、合併進來的從垃圾桶拿出來。錄音檔由呼叫的人搬
    func restoreEdit(_ id: Int64) throws -> RestoreOutcome? {
        try pool.write { db -> RestoreOutcome? in
            guard let record = try MeetingEditRecord.fetchOne(db, key: id), var meeting = try Meeting.fetchOne(db, key: id),
                  let snapshot = try? JSONDecoder().decode(EditSnapshot.self, from: Data(record.snapshot.utf8)) else { return nil }
            let now = Clock.now()
            let currentAudio = meeting.audioFile

            try Self.unindexSegments(db, meetingId: id)
            try db.execute(sql: "DELETE FROM segments WHERE meeting_id = ?", arguments: [id])
            try db.execute(sql: "DELETE FROM bookmarks WHERE meeting_id = ?", arguments: [id])
            try db.execute(sql: "DELETE FROM speakers WHERE meeting_id = ?", arguments: [id])
            // 句子用新的 id 放回（分出去的那場可能還用著原本的 id）
            for line in snapshot.lines {
                var segment = line.segment
                segment.id = nil
                segment.meetingId = id
                try segment.insert(db)
                if let clean = line.clean {
                    try db.execute(sql: "INSERT INTO clean_lines (segment_id, text) VALUES (?, ?)", arguments: [segment.id, clean])
                }
                for var correction in line.corrections {
                    correction.id = nil
                    correction.meetingId = id
                    correction.segmentId = segment.id!
                    try correction.insert(db)
                }
            }
            for var bookmark in snapshot.bookmarks {
                bookmark.id = nil
                bookmark.meetingId = id
                try bookmark.insert(db)
            }
            for var speaker in snapshot.speakers {
                speaker.meetingId = id
                if let person = speaker.personId,
                   !(try Bool.fetchOne(db, sql: "SELECT EXISTS (SELECT 1 FROM people WHERE id = ?)", arguments: [person]) ?? false) {
                    speaker.personId = nil
                }
                try speaker.insert(db)
            }
            // 會議中的畫面（design §83.7）：分到另一場的搬回來、時間改回原本的（被刪掉的就算了）
            for screen in snapshot.screens ?? [] {
                try db.execute(sql: "UPDATE documents SET meeting_id = ?, at = ? WHERE id = ? AND at IS NOT NULL", arguments: [id, screen.at, screen.id])
                try db.execute(sql: "UPDATE search SET meeting_id = ? WHERE rowid = ?", arguments: [id, AppDatabase.screenRowidBase + screen.id])
            }
            var removedDocuments: [String] = []
            for document in try MeetingDocument.filter(Column("meeting_id") == id).fetchAll(db) where !snapshot.documentIds.contains(document.id!) {
                _ = try MeetingDocument.deleteOne(db, key: document.id!)
                if document.isScreen { try db.execute(sql: "DELETE FROM search WHERE rowid = ?", arguments: [AppDatabase.screenRowidBase + document.id!]) }
                removedDocuments.append(document.file)
            }

            if let notes = meeting.notes, notes != snapshot.notes {
                var version = NotesVersion(meetingId: id, notes: notes, reason: String(localized: "還原編輯前"))
                try version.insert(db)
            }
            meeting.notes = snapshot.notes
            meeting.notesBy = snapshot.notesBy
            meeting.notesDraft = snapshot.notesDraft
            meeting.userNotes = snapshot.userNotes
            meeting.durationSeconds = snapshot.durationSeconds
            meeting.createdAt = snapshot.createdAt
            meeting.meetingDate = snapshot.meetingDate
            meeting.participants = snapshot.participants
            meeting.audioFile = record.audioFile.isEmpty ? "" : snapshot.audioFile
            meeting.updatedAt = now
            try meeting.update(db)
            try db.execute(sql: "DELETE FROM speaker_undo WHERE meeting_id = ?", arguments: [id])
            try Self.reindexMeeting(db, meeting)

            var trashed: [Int64] = [], untrashed: [Int64] = []
            for other in record.splitIds {
                guard let split = try Meeting.fetchOne(db, key: other), split.deletedAt == nil else { continue }
                try db.execute(sql: "UPDATE meetings SET deleted_at = ? WHERE id = ?", arguments: [now, other])
                trashed.append(other)
            }
            for other in record.mergedIds {
                guard let merged = try Meeting.fetchOne(db, key: other), merged.deletedAt != nil else { continue }
                try db.execute(sql: "UPDATE meetings SET deleted_at = NULL WHERE id = ?", arguments: [other])
                untrashed.append(other)
            }
            _ = try MeetingEditRecord.deleteOne(db, key: id)
            return RestoreOutcome(currentAudio: currentAudio, editsAudio: record.audioFile, restoredAudio: meeting.audioFile,
                                  trashed: trashed, untrashed: untrashed, removedDocuments: removedDocuments)
        }
    }

    private static func snapshot(_ db: Database, _ meeting: Meeting) throws -> String {
        let id = meeting.id!
        let lines = try Segment.filter(Column("meeting_id") == id).order(Column("kind"), Column("seq")).fetchAll(db).map { segment in
            EditSnapshot.Line(segment: segment,
                              clean: try String.fetchOne(db, sql: "SELECT text FROM clean_lines WHERE segment_id = ?", arguments: [segment.id]),
                              corrections: try Correction.filter(Column("segment_id") == segment.id).order(Column("id")).fetchAll(db))
        }
        var snapshot = EditSnapshot(
            audioFile: meeting.audioFile, durationSeconds: meeting.durationSeconds, userNotes: meeting.userNotes, notes: meeting.notes,
            notesBy: meeting.notesBy, notesDraft: meeting.notesDraft, createdAt: meeting.createdAt, meetingDate: meeting.meetingDate,
            participants: meeting.participants, lines: lines,
            bookmarks: try Bookmark.filter(Column("meeting_id") == id).order(Column("time")).fetchAll(db),
            speakers: try SpeakerName.filter(Column("meeting_id") == id).order(Column("idx")).fetchAll(db),
            documentIds: try Int64.fetchAll(db, sql: "SELECT id FROM documents WHERE meeting_id = ? ORDER BY id", arguments: [id]))
        let screens = try MeetingDocument.filter(Column("meeting_id") == id && Column("at") != nil).order(Column("id")).fetchAll(db)
        snapshot.screens = screens.isEmpty ? nil : screens.map { EditSnapshot.ScreenTime(id: $0.id!, at: $0.at!) }
        return String(decoding: try JSONEncoder().encode(snapshot), as: UTF8.self)
    }

    /// 合併時另一場的說話者對到這場（§81.4）：同一個人物 → 同樣的名字（不是預設的）→ 聲紋（門檻同聲紋比對）；
    /// 都對不上的接在後面。回傳另一場的編號 → 這場的編號
    private static func mergeSpeakers(_ db: Database, into id: Int64, from other: Int64, language: MeetingLanguage) throws -> [Int: Int] {
        var mine = try SpeakerName.filter(Column("meeting_id") == id).order(Column("idx")).fetchAll(db)
        let theirs = try SpeakerName.filter(Column("meeting_id") == other).order(Column("idx")).fetchAll(db)
        var map: [Int: Int] = [:]
        var taken = Set<Int>()
        func claim(_ from: Int, _ to: Int) { map[from] = to; taken.insert(to) }
        for s in theirs {
            if let person = s.personId, let match = mine.first(where: { $0.personId == person && !taken.contains($0.idx) }) {
                claim(s.idx, match.idx)
            }
        }
        for s in theirs where map[s.idx] == nil && s.nameSource != "default" {
            let name = s.name.lowercased()
            if let match = mine.first(where: { $0.nameSource != "default" && $0.name.lowercased() == name && !taken.contains($0.idx) }) {
                claim(s.idx, match.idx)
            }
        }
        for s in theirs where map[s.idx] == nil {
            guard let data = s.embedding else { continue }
            let vector = Voiceprint.vector(data)
            let best = mine.filter { !taken.contains($0.idx) }.compactMap { m -> (Int, Double)? in
                m.embedding.map { (m.idx, Voiceprint.similarity(vector, Voiceprint.vector($0))) }
            }.max { $0.1 < $1.1 }
            if let best, best.1 >= voiceprintThreshold { claim(s.idx, best.0) }
        }
        var next = (mine.map(\.idx).max() ?? 0) + 1
        for s in theirs where map[s.idx] == nil {
            var copy = s
            copy.meetingId = id
            copy.idx = next
            if s.nameSource == "default" { copy.name = language.defaultSpeaker(next) }
            copy.speechSeconds = 0   // 套用時照留下的比例加上去
            try copy.insert(db)
            mine.append(copy)
            claim(s.idx, next)
            next += 1
        }
        return map
    }

    /// 這場會議的搜尋索引全部重建（句子、標題、筆記、會議紀錄）
    static func reindexMeeting(_ db: Database, _ meeting: Meeting) throws {
        let id = meeting.id!
        try unindexSegments(db, meetingId: id)
        try unindexMeeting(db, meetingId: id)
        try db.execute(sql: "INSERT INTO search (rowid, meeting_id, kind, ref, text) SELECT id, meeting_id, 'segment', id, text FROM segments WHERE meeting_id = ?",
                       arguments: [id])
        try index(db, meetingId: id, kind: .title, ref: 0, text: meeting.displayTitle)
        try index(db, meetingId: id, kind: .userNotes, ref: 0, text: meeting.userNotes)
        if let notes = meeting.decodedNotes { try index(db, meetingId: id, kind: .notes, ref: 0, text: notes.plainText) }
    }
}
