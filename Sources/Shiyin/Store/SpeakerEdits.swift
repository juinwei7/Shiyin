import Foundation
import GRDB

/// 說話者的修正（design §24.2～§24.4、R23）：合併、改句子的說話者、復原最近一次；說話者辨識的自我檢查（§24.5）
extension AppDatabase {
    private struct SpeakerSnapshot: Codable {
        struct SegmentState: Codable { var id: Int64; var speaker: Int?; var manual: Bool }
        struct SpeakerState: Codable {
            var idx: Int, name: String, nameSource: String, personId: Int64?
            var embedding: Data?, speechSeconds: Double, audioSource: String
        }
        var segments: [SegmentState]
        var speakers: [SpeakerState]
        /// 合併時換了會議紀錄裡的名字：換之前、換之後的會議紀錄（JSON）。復原時會議紀錄還是換之後的樣子，就換回去
        var notesBefore: String?
        var notesAfter: String?
        /// 拆句（design §46.1）：前半句、後半句的 id，中間去掉的空白
        struct SplitState: Codable { var id: Int64; var newId: Int64; var gap: String }
        var split: SplitState?
    }

    /// 改之前先存一份，只留最近一次
    private static func saveSpeakerUndo(_ db: Database, meetingId: Int64, label: String,
                                        notesBefore: String? = nil, notesAfter: String? = nil,
                                        split: SpeakerSnapshot.SplitState? = nil) throws {
        let segments = try Row.fetchAll(db, sql: "SELECT id, speaker, speaker_manual FROM segments WHERE meeting_id = ? AND kind = 'final'",
                                        arguments: [meetingId])
            .map { SpeakerSnapshot.SegmentState(id: $0["id"], speaker: $0["speaker"], manual: $0["speaker_manual"]) }
        let speakers = try SpeakerName.filter(Column("meeting_id") == meetingId).fetchAll(db).map {
            SpeakerSnapshot.SpeakerState(idx: $0.idx, name: $0.name, nameSource: $0.nameSource, personId: $0.personId,
                                         embedding: $0.embedding, speechSeconds: $0.speechSeconds, audioSource: $0.audioSource)
        }
        let snapshot = SpeakerSnapshot(segments: segments, speakers: speakers, notesBefore: notesBefore, notesAfter: notesAfter, split: split)
        let json = String(decoding: try JSONEncoder().encode(snapshot), as: UTF8.self)
        try db.execute(sql: "INSERT OR REPLACE INTO speaker_undo (meeting_id, snapshot, label, created_at) VALUES (?, ?, ?, ?)",
                       arguments: [meetingId, json, label, Clock.now()])
    }

    /// 可以復原的調整（顯示用的說明），沒有就是 nil
    func speakerUndoLabel(_ meetingId: Int64) throws -> String? {
        try pool.read { db in try String.fetchOne(db, sql: "SELECT label FROM speaker_undo WHERE meeting_id = ?", arguments: [meetingId]) }
    }

    /// 復原最近一次說話者的調整。合併時換過名字的會議紀錄，之後沒有再改的話一起換回去（拿掉合併時存的那一版）
    @discardableResult
    func undoSpeakerEdit(_ meetingId: Int64) throws -> Bool {
        try pool.write { db in
            guard let json = try String.fetchOne(db, sql: "SELECT snapshot FROM speaker_undo WHERE meeting_id = ?", arguments: [meetingId]),
                  let snapshot = try? JSONDecoder().decode(SpeakerSnapshot.self, from: Data(json.utf8)) else { return false }
            if let after = snapshot.notesAfter, let before = snapshot.notesBefore,
               try String.fetchOne(db, sql: "SELECT notes FROM meetings WHERE id = ?", arguments: [meetingId]) == after,
               try String.fetchOne(db, sql: "SELECT notes FROM notes_versions WHERE meeting_id = ? ORDER BY id DESC LIMIT 1",
                                   arguments: [meetingId]) == before {
                try Self.undoNotes(db, meetingId)
            }
            if let split = snapshot.split { try Self.joinSplit(db, meetingId: meetingId, split) }
            for s in snapshot.segments {
                try db.execute(sql: "UPDATE segments SET speaker = ?, speaker_manual = ? WHERE id = ?", arguments: [s.speaker, s.manual, s.id])
            }
            let people = Set(try Int64.fetchAll(db, sql: "SELECT id FROM people"))
            try SpeakerName.filter(Column("meeting_id") == meetingId).deleteAll(db)
            for s in snapshot.speakers {
                var row = SpeakerName(meetingId: meetingId, idx: s.idx, name: s.name)
                row.nameSource = s.nameSource
                row.personId = s.personId.flatMap { people.contains($0) ? $0 : nil }
                row.embedding = s.embedding
                row.speechSeconds = s.speechSeconds
                row.audioSource = s.audioSource
                try row.insert(db)
            }
            try db.execute(sql: "DELETE FROM speaker_undo WHERE meeting_id = ?", arguments: [meetingId])
            return true
        }
    }

    /// 合併說話者（R23.1）：from 的段落併到 into；聲紋依說話秒數加權平均；from 存過的聲紋刪掉；
    /// 會議紀錄裡 from 的名字換成 into 的名字（存一版）。回傳兩人原本的名字
    func mergeSpeakers(_ meetingId: Int64, from: Int, into: Int, label: String) throws -> (from: String, into: String)? {
        try pool.write { db in
            guard from != into,
                  let a = try SpeakerName.filter(Column("meeting_id") == meetingId && Column("idx") == into).fetchOne(db),
                  let b = try SpeakerName.filter(Column("meeting_id") == meetingId && Column("idx") == from).fetchOne(db),
                  let meeting = try Meeting.fetchOne(db, key: meetingId) else { return nil }
            var notesAfter: String?
            if let notes = meeting.decodedNotes, a.name != b.name {
                let updated = notes.replacingSpeakerName(b.name, with: a.name, isDefaultName: Self.isDefaultSpeakerName(b.name))
                if updated != notes {
                    try Self.saveNotes(db, meetingId, notes: updated, by: nil, reason: String(localized: "合併說話者"))
                    notesAfter = try String.fetchOne(db, sql: "SELECT notes FROM meetings WHERE id = ?", arguments: [meetingId])
                }
            }
            try Self.saveSpeakerUndo(db, meetingId: meetingId, label: label,
                                     notesBefore: notesAfter == nil ? nil : meeting.notes, notesAfter: notesAfter)
            try db.execute(sql: "UPDATE segments SET speaker = ? WHERE meeting_id = ? AND speaker = ?", arguments: [into, meetingId, from])
            var merged = a
            merged.speechSeconds = a.speechSeconds + b.speechSeconds
            switch (a.embedding, b.embedding) {
            case let (x?, y?):
                let (u, v) = (Voiceprint.vector(x), Voiceprint.vector(y))
                let (wa, wb) = (Float(max(a.speechSeconds, 1)), Float(max(b.speechSeconds, 1)))
                var sum = zip(u, v).map { $0 * wa + $1 * wb }
                let norm = sqrt(sum.reduce(0) { $0 + $1 * $1 })
                if norm > 0 { sum = sum.map { $0 / norm } }
                merged.embedding = Voiceprint.data(sum)
            case (nil, let y?):
                merged.embedding = y
            default:
                break
            }
            try db.execute(sql: "UPDATE speakers SET speech_seconds = ?, embedding = ? WHERE meeting_id = ? AND idx = ?",
                           arguments: [merged.speechSeconds, merged.embedding, meetingId, into])
            try db.execute(sql: "DELETE FROM speakers WHERE meeting_id = ? AND idx = ?", arguments: [meetingId, from])
            try db.execute(sql: "DELETE FROM voiceprints WHERE meeting_id = ? AND speaker_idx = ?", arguments: [meetingId, from])
            try Self.deleteOrphanPeople(db)
            return (b.name, a.name)
        }
    }

    /// 改句子的說話者（R23.2）：to 是 nil 時新增一位說話者（預設名稱）；改過的句子標成 speaker_manual；
    /// 沒有段落的說話者拿掉。回傳句子現在的說話者
    @discardableResult
    func reassignSegments(_ meetingId: Int64, ids: [Int64], to idx: Int?, label: String) throws -> Int {
        try pool.write { db in
            try Self.saveSpeakerUndo(db, meetingId: meetingId, label: label)
            var target = idx ?? 0
            if idx == nil { target = try Self.addSpeaker(db, meetingId: meetingId) }
            for id in ids {
                try db.execute(sql: "UPDATE segments SET speaker = ?, speaker_manual = 1 WHERE id = ? AND meeting_id = ?",
                               arguments: [target, id, meetingId])
            }
            try Self.removeEmptySpeakers(db, meetingId: meetingId)
            return target
        }
    }

    /// 拆句時去掉的空白（design §46.1）：空白、tab、換行、全形空白
    private static func isGap(_ unit: UInt16) -> Bool { unit == 0x20 || unit == 0x09 || unit == 0x0A || unit == 0x0D || unit == 0x3000 }

    /// 把一句拆成兩句（R70、design §46.1）：at 是 UTF-16 的位置，to 是後半句的說話者（nil 是新的說話者）。
    /// 任一邊沒有字就不拆，回傳 nil；拆了回傳後半句的 id
    @discardableResult
    func splitSegment(_ meetingId: Int64, id: Int64, at: Int, to idx: Int?, label: String) throws -> Int64? {
        try pool.write { db in
            guard let segment = try Segment.fetchOne(db, key: id), segment.meetingId == meetingId, segment.kind == "final" else { return nil }
            let units = Array(segment.text.utf16)
            guard at > 0, at < units.count else { return nil }
            var headEnd = at, tailStart = at
            while headEnd > 0, Self.isGap(units[headEnd - 1]) { headEnd -= 1 }
            while tailStart < units.count, Self.isGap(units[tailStart]) { tailStart += 1 }
            var headStart = 0, tailEnd = units.count
            while headStart < headEnd, Self.isGap(units[headStart]) { headStart += 1 }
            while tailEnd > tailStart, Self.isGap(units[tailEnd - 1]) { tailEnd -= 1 }
            guard headStart < headEnd, tailStart < tailEnd,
                  let head = String(utf16CodeUnits: Array(units[headStart..<headEnd]), count: headEnd - headStart) as String?,
                  let tail = String(utf16CodeUnits: Array(units[tailStart..<tailEnd]), count: tailEnd - tailStart) as String?
            else { return nil }
            let gap = String(utf16CodeUnits: Array(units[headEnd..<tailStart]), count: tailStart - headEnd)
            // 時間照字數（不算空白）的比例，四捨五入到 0.01 秒
            let a = Double(units[headStart..<headEnd].filter { !Self.isGap($0) }.count)
            let b = Double(units[tailStart..<tailEnd].filter { !Self.isGap($0) }.count)
            let cut = ((segment.start + (segment.end - segment.start) * a / (a + b)) * 100).rounded() / 100
            // 先佔好 id 才存得了復原：新的一句的 id 是現在最大的 + 1（AUTOINCREMENT）
            let newId = (try Int64.fetchOne(db, sql: "SELECT seq FROM sqlite_sequence WHERE name = 'segments'") ?? 0) + 1
            try Self.saveSpeakerUndo(db, meetingId: meetingId, label: label, split: .init(id: id, newId: newId, gap: gap))
            var target = idx ?? 0
            if idx == nil { target = try Self.addSpeaker(db, meetingId: meetingId) }
            try db.execute(sql: "UPDATE segments SET seq = seq + 1 WHERE meeting_id = ? AND seq > ?", arguments: [meetingId, segment.seq])
            try db.execute(sql: "UPDATE segments SET text = ?, end = ? WHERE id = ?", arguments: [head, cut, id])
            try db.execute(sql: "UPDATE search SET text = ? WHERE rowid = ?", arguments: [head, id])
            var second = Segment(meetingId: meetingId, seq: segment.seq + 1, start: cut, end: segment.end, text: tail,
                                 lowConfidence: segment.lowConfidence, speaker: target, kind: "final", channel: segment.channel,
                                 speakerManual: true)
            try second.insert(db)
            // 復原存的 id 要對（同一個交易裡只有這裡新增段落，AUTOINCREMENT 一定是這個）
            guard let secondId = second.id, secondId == newId else { throw DatabaseError(message: "split: unexpected segment id") }
            try Self.indexSegment(db, meetingId: meetingId, id: secondId, text: tail)
            // 校正建議跟著文字（R70.6）：待確認的看原文、已接受的看建議，只在後半句找得到的移過去
            for c in try Correction.filter(Column("segment_id") == id).fetchAll(db) {
                let key = c.status == "accepted" ? c.suggestion : c.original
                if !key.isEmpty, !head.contains(key), tail.contains(key) {
                    try db.execute(sql: "UPDATE corrections SET segment_id = ? WHERE id = ?", arguments: [secondId, c.id])
                }
            }
            try Self.removeEmptySpeakers(db, meetingId: meetingId)
            return secondId
        }
    }

    /// 復原拆句（design §46.1）：兩句接回一句，文字是兩句現在的內容
    private static func joinSplit(_ db: Database, meetingId: Int64, _ split: SpeakerSnapshot.SplitState) throws {
        guard let first = try Segment.fetchOne(db, key: split.id), let second = try Segment.fetchOne(db, key: split.newId) else { return }
        let text = first.text + split.gap + second.text
        try db.execute(sql: "UPDATE corrections SET segment_id = ? WHERE segment_id = ?", arguments: [split.id, split.newId])
        try db.execute(sql: "UPDATE segments SET text = ?, end = ? WHERE id = ?", arguments: [text, second.end, split.id])
        try db.execute(sql: "UPDATE search SET text = ? WHERE rowid = ?", arguments: [text, split.id])
        try db.execute(sql: "DELETE FROM search WHERE rowid = ?", arguments: [split.newId])
        try db.execute(sql: "DELETE FROM segments WHERE id = ?", arguments: [split.newId])
        try db.execute(sql: "UPDATE segments SET seq = seq - 1 WHERE meeting_id = ? AND seq > ?", arguments: [meetingId, second.seq])
    }

    /// 新增一位說話者（預設名稱），回傳編號
    private static func addSpeaker(_ db: Database, meetingId: Int64) throws -> Int {
        let target = (try Int.fetchOne(db, sql: "SELECT MAX(idx) FROM speakers WHERE meeting_id = ?", arguments: [meetingId]) ?? 0) + 1
        let language = try Meeting.fetchOne(db, key: meetingId)?.meetingLanguage ?? .zh
        var speaker = SpeakerName(meetingId: meetingId, idx: target, name: language.defaultSpeaker(target))
        speaker.audioSource = try String.fetchOne(db, sql: "SELECT audio_source FROM speakers WHERE meeting_id = ? LIMIT 1",
                                                  arguments: [meetingId]) ?? "mix"
        try speaker.insert(db)
        return target
    }

    /// 沒有段落的說話者拿掉
    private static func removeEmptySpeakers(_ db: Database, meetingId: Int64) throws {
        let empty = try Int.fetchAll(db, sql: """
            SELECT idx FROM speakers s WHERE meeting_id = ?
              AND NOT EXISTS (SELECT 1 FROM segments g WHERE g.meeting_id = s.meeting_id AND g.speaker = s.idx)
            """, arguments: [meetingId])
        for e in empty {
            try db.execute(sql: "DELETE FROM speakers WHERE meeting_id = ? AND idx = ?", arguments: [meetingId, e])
            try db.execute(sql: "DELETE FROM voiceprints WHERE meeting_id = ? AND speaker_idx = ?", arguments: [meetingId, e])
        }
        try deleteOrphanPeople(db)
    }

    /// 說話者辨識完記下自我檢查的分數（design §24.5），之前按過的「不用了」重設
    func setSpeakerCheck(_ meetingId: Int64, _ score: Double?) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET speaker_check = ?, speaker_hint_dismissed = 0 WHERE id = ?", arguments: [score, meetingId])
        }
    }

    func dismissSpeakerHint(_ meetingId: Int64) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET speaker_hint_dismissed = 1 WHERE id = ?", arguments: [meetingId])
        }
    }
}
