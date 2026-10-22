import Foundation
import GRDB

/// 認識的人（design §21.4）：使用者替說話者取過名字的人
struct Person: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "people"
    var id: Int64?
    var name: String
    var createdAt: String = Clock.now()

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

/// 設定頁的認識的人：幾份聲紋、最近出現在哪場會議
struct PersonSummary: Identifiable, Hashable {
    var id: Int64
    var name: String
    var voiceprintCount: Int
    var lastMeetingTitle: String?
    var lastMeetingDate: String?
}

/// 聲紋的比對與學習（design §21.4、R19.3、D29）。都在本機的資料庫裡做，不上傳
extension AppDatabase {
    /// 說話少於這個秒數不比對、不學：太短的聲紋不準
    static let voiceprintMinimumSeconds = 8.0
    /// 相似度門檻（cosine）：AMI 評估（design §21.4.1）同一個人最低 0.81、不同的人最高 0.42，
    /// 取中間偏高，寧可少帶入、不要帶錯。到這裡算候選，沒到直接帶入的門檻就在待確認問（R33）
    static let voiceprintThreshold = 0.65
    /// 直接帶入：很像（使用者的資料裡不同的人可以像到 0.77、0.85），而且比第二像的人高出一截（design §21.4.1、D91）
    static let voiceprintAutoThreshold = 0.88
    static let voiceprintAutoMargin = 0.15
    /// 同一個人的聲紋最多留幾份，超過刪最舊的
    static let voiceprintsPerPerson = 30

    /// 轉錄完：還是預設名稱的說話者跟認識的人比對。很有把握的自動帶入名字（name_source = voiceprint），
    /// 夠像但沒把握的存成說話者的建議，在待確認問（R33、D91）。
    /// 一個人在一場會議只對到一位說話者，依相似度由高到低分配。回傳帶入了幾位
    @discardableResult
    func matchVoiceprints(_ meetingId: Int64) throws -> Int {
        try pool.write { db in
            let speakers = try SpeakerName.filter(Column("meeting_id") == meetingId).fetchAll(db)
                .filter { $0.nameSource == "default" && $0.embedding != nil && $0.speechSeconds >= Self.voiceprintMinimumSeconds }
            guard !speakers.isEmpty else { return 0 }
            let library = try Self.library(db)
            guard !library.isEmpty else { return 0 }
            var candidates: [(score: Double, idx: Int, person: Int64)] = []
            var scores: [Int: [Int64: Double]] = [:]
            for speaker in speakers {
                let vector = Voiceprint.vector(speaker.embedding!)
                for (person, prints) in library {
                    let score = prints.map { Voiceprint.similarity(vector, $0) }.max() ?? 0
                    scores[speaker.idx, default: [:]][person] = score
                    if score >= Self.voiceprintThreshold { candidates.append((score, speaker.idx, person)) }
                }
            }
            let language = try Meeting.fetchOne(db, key: meetingId)?.meetingLanguage ?? .zh
            var usedSpeakers = Set<Int>(), usedPeople = Set<Int64>()
            // 已經手動取名、對到某人的說話者，那個人就不再帶給別人
            for row in try SpeakerName.filter(Column("meeting_id") == meetingId && Column("person_id") != nil).fetchAll(db) {
                if let person = row.personId { usedPeople.insert(person) }
            }
            var matched = 0
            for candidate in candidates.sorted(by: { $0.score > $1.score }) {
                guard !usedSpeakers.contains(candidate.idx), !usedPeople.contains(candidate.person),
                      let name = try String.fetchOne(db, sql: "SELECT name FROM people WHERE id = ?", arguments: [candidate.person]) else { continue }
                usedSpeakers.insert(candidate.idx)
                usedPeople.insert(candidate.person)
                let second = scores[candidate.idx]?.filter { $0.key != candidate.person }.values.max() ?? 0
                if candidate.score >= Self.voiceprintAutoThreshold && candidate.score - second >= Self.voiceprintAutoMargin {
                    try db.execute(sql: "UPDATE speakers SET name = ?, name_source = 'voiceprint', person_id = ? WHERE meeting_id = ? AND idx = ?",
                                   arguments: [name, candidate.person, meetingId, candidate.idx])
                    matched += 1
                } else {
                    // 沒把握：不帶入，存成建議，在待確認問「說話者 N 是 X 嗎？」（design §27）
                    let reason = language.text(zh: "聲音跟之前的\(name)很像", en: "Sounds like \(name) from earlier meetings",
                                               ja: "以前の\(name)の声に似ています")  // l10n-skip：會議語言的原因
                    try db.execute(sql: """
                        UPDATE speakers SET suggested_name = ?, suggestion_reason = ?, suggestion_time = NULL
                        WHERE meeting_id = ? AND idx = ? AND name_source = 'default'
                        """, arguments: [name, reason, meetingId, candidate.idx])
                }
            }
            return matched
        }
    }

    /// 每位說話者最像的幾位認識的人（不含目前的名字），由高到低：「是別人」的選單、待確認的「不是」用（R33）
    func voiceprintCandidates(_ meetingId: Int64, limit: Int = 3) throws -> [Int: [String]] {
        try pool.read { db in
            let speakers = try SpeakerName.filter(Column("meeting_id") == meetingId).fetchAll(db)
                .filter { $0.embedding != nil && $0.speechSeconds >= Self.voiceprintMinimumSeconds }
            guard !speakers.isEmpty else { return [:] }
            let library = try Self.library(db)
            guard !library.isEmpty else { return [:] }
            let names = Dictionary(try Person.fetchAll(db).compactMap { p in p.id.map { ($0, p.name) } }, uniquingKeysWith: { a, _ in a })
            var result: [Int: [String]] = [:]
            for speaker in speakers {
                let vector = Voiceprint.vector(speaker.embedding!)
                let ranked = library.compactMap { person, prints -> (name: String, score: Double)? in
                    guard let name = names[person], name.caseInsensitiveCompare(speaker.name) != .orderedSame else { return nil }
                    return (name, prints.map { Voiceprint.similarity(vector, $0) }.max() ?? 0)
                }
                result[speaker.idx] = ranked.sorted { $0.score > $1.score }.prefix(limit).map(\.name)
            }
            return result
        }
    }

    /// 所有認識的人的聲紋
    private static func library(_ db: Database) throws -> [Int64: [[Float]]] {
        var result: [Int64: [[Float]]] = [:]
        for row in try Row.fetchAll(db, sql: "SELECT person_id, embedding FROM voiceprints") {
            let data: Data = row["embedding"]
            result[row["person_id"], default: []].append(Voiceprint.vector(data))
        }
        return result
    }

    /// 說話者被取名（手動、套用建議）之後：連到同名的人（沒有就建立），把這場會議的聲紋存起來（design §21.4）。
    /// 名字是預設的「說話者 N」時，取消連結、刪掉這場會議這位說話者存過的聲紋
    func learnVoiceprint(_ meetingId: Int64, idx: Int, name: String, source: String = "manual") throws {
        try pool.write { db in
            // 這位說話者之前存過的聲紋（例如改正自動帶入的名字）先刪掉
            try db.execute(sql: "DELETE FROM voiceprints WHERE meeting_id = ? AND speaker_idx = ?", arguments: [meetingId, idx])
            guard !Self.isDefaultSpeakerName(name) else {
                try db.execute(sql: "UPDATE speakers SET person_id = NULL WHERE meeting_id = ? AND idx = ?", arguments: [meetingId, idx])
                try Self.deleteOrphanPeople(db)
                return
            }
            var person = try Person.filter(sql: "name = ? COLLATE NOCASE", arguments: [name]).fetchOne(db)
            if person == nil {
                var created = Person(name: name)
                try created.insert(db)
                person = created
            }
            guard let personId = person?.id else { return }
            try db.execute(sql: "UPDATE speakers SET person_id = ?, name_source = ? WHERE meeting_id = ? AND idx = ?",
                           arguments: [personId, source, meetingId, idx])
            guard let speaker = try SpeakerName.filter(Column("meeting_id") == meetingId && Column("idx") == idx).fetchOne(db),
                  let embedding = speaker.embedding, speaker.speechSeconds >= Self.voiceprintMinimumSeconds else { return }
            let vector = Voiceprint.vector(embedding)
            let existing = try Row.fetchAll(db, sql: "SELECT embedding FROM voiceprints WHERE person_id = ?", arguments: [personId])
                .map { Voiceprint.vector($0["embedding"] as Data) }
            // 跟已有的某份幾乎一樣就不重複存
            guard !existing.contains(where: { Voiceprint.similarity(vector, $0) > 0.95 }) else { return }
            try db.execute(sql: """
                INSERT INTO voiceprints (person_id, embedding, audio_source, meeting_id, speaker_idx, created_at) VALUES (?, ?, ?, ?, ?, ?)
                """, arguments: [personId, embedding, speaker.audioSource, meetingId, idx, Clock.now()])
            try db.execute(sql: """
                DELETE FROM voiceprints WHERE person_id = ? AND id NOT IN
                  (SELECT id FROM voiceprints WHERE person_id = ? ORDER BY id DESC LIMIT ?)
                """, arguments: [personId, personId, Self.voiceprintsPerPerson])
        }
    }

    /// 這場會議說話者辨識至少分出幾個人（design §23.2），nil 自動判斷
    func setMinSpeakers(_ meetingId: Int64, _ minSpeakers: Int?) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET min_speakers = ? WHERE id = ?", arguments: [minSpeakers, meetingId])
        }
    }

    /// 重新辨識說話者（design §23.3）：段落換成新的說話者；刪掉這場存過的聲紋（舊的分群可能混了兩個人）；
    /// 重建這場的說話者，inherited 的沿用名字，其他用預設名稱，聲紋、說話秒數照新的結果
    func reassignSpeakers(_ meetingId: Int64, segmentSpeakers: [(id: Int64, speaker: Int?)], voices: [Int: SpeakerVoice],
                          inherited: [Int: (name: String, source: String, personId: Int64?)]) throws {
        try pool.write { db in
            for item in segmentSpeakers {
                try db.execute(sql: "UPDATE segments SET speaker = ? WHERE id = ?", arguments: [item.speaker, item.id])
            }
            try db.execute(sql: "DELETE FROM voiceprints WHERE meeting_id = ?", arguments: [meetingId])
            let language = try Meeting.fetchOne(db, key: meetingId)?.meetingLanguage ?? .zh
            try SpeakerName.filter(Column("meeting_id") == meetingId).deleteAll(db)
            let count = segmentSpeakers.compactMap(\.speaker).max() ?? 0
            for idx in stride(from: 1, through: count, by: 1) {
                var speaker = SpeakerName(meetingId: meetingId, idx: idx,
                                          name: language.text(zh: "說話者 \(idx)", en: "Speaker \(idx)", ja: "話者 \(idx)"))
                if let old = inherited[idx] {
                    speaker.name = old.name
                    speaker.nameSource = old.source
                    speaker.personId = old.personId
                }
                if let voice = voices[idx] {
                    speaker.embedding = Voiceprint.data(voice.embedding)
                    speaker.speechSeconds = voice.seconds
                    speaker.audioSource = voice.source
                }
                try speaker.insert(db)
            }
            try Self.deleteOrphanPeople(db)
            // 舊的復原資料對不上新的說話者了（design §24.2）
            try db.execute(sql: "DELETE FROM speaker_undo WHERE meeting_id = ?", arguments: [meetingId])
            try db.execute(sql: "UPDATE meetings SET updated_at = ? WHERE id = ?", arguments: [Clock.now(), meetingId])
        }
    }

    /// 「不是他」：自動帶入的名字不對，回到預設名稱；之後重新比對也不再帶入這位（name_source = manual）
    func rejectVoiceprintMatch(_ meetingId: Int64, idx: Int, defaultName: String) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE speakers SET name = ?, name_source = 'manual', person_id = NULL WHERE meeting_id = ? AND idx = ?",
                           arguments: [defaultName, meetingId, idx])
            try db.execute(sql: "DELETE FROM voiceprints WHERE meeting_id = ? AND speaker_idx = ?", arguments: [meetingId, idx])
            try Self.deleteOrphanPeople(db)
            try db.execute(sql: "DELETE FROM speaker_undo WHERE meeting_id = ?", arguments: [meetingId])
        }
    }

    /// 沒有聲紋、也沒有連到任何說話者的人（例如取名後又改回去），不留在認識的人裡
    static func deleteOrphanPeople(_ db: Database) throws {
        try db.execute(sql: """
            DELETE FROM people WHERE id NOT IN (SELECT person_id FROM voiceprints)
              AND id NOT IN (SELECT person_id FROM speakers WHERE person_id IS NOT NULL)
            """)
    }

    // MARK: 猜說話者（design §21.5）

    /// 存 AI 猜的名字：只寫還是預設名稱、還沒有建議的說話者（聲紋的建議優先：有聲音當根據，design §27）
    func saveSpeakerSuggestions(_ meetingId: Int64, _ suggestions: [SpeakerNaming.Suggestion]) throws {
        try pool.write { db in
            for s in suggestions {
                try db.execute(sql: """
                    UPDATE speakers SET suggested_name = ?, suggestion_reason = ?, suggestion_time = ?
                    WHERE meeting_id = ? AND idx = ? AND name_source = 'default' AND suggested_name IS NULL
                    """, arguments: [s.name, s.reason, s.time, meetingId, s.speaker])
            }
        }
    }

    func clearSpeakerSuggestion(_ meetingId: Int64, idx: Int) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE speakers SET suggested_name = NULL, suggestion_reason = NULL, suggestion_time = NULL WHERE meeting_id = ? AND idx = ?",
                           arguments: [meetingId, idx])
        }
    }

    /// 說話者改名後，會議紀錄裡的舊名字換成新名字（design §21.6）；有改到就存一版，回傳是否有改
    @discardableResult
    func replaceSpeakerNameInNotes(_ meetingId: Int64, from old: String, to new: String) throws -> Bool {
        guard old != new, let meeting = try self.meeting(meetingId), let notes = meeting.decodedNotes else { return false }
        let updated = notes.replacingSpeakerName(old, with: new, isDefaultName: Self.isDefaultSpeakerName(old))
        guard updated != notes else { return false }
        try saveNotes(meetingId, notes: updated, by: nil, reason: String(localized: "說話者改名"))
        return true
    }

    // MARK: 設定頁：認識的人

    func peopleSummaries() throws -> [PersonSummary] {
        try pool.read { db in
            try Row.fetchAll(db, sql: """
                SELECT p.id, p.name,
                  (SELECT count(*) FROM voiceprints v WHERE v.person_id = p.id) AS prints,
                  (SELECT m.id FROM speakers s JOIN meetings m ON m.id = s.meeting_id
                     WHERE s.person_id = p.id AND m.deleted_at IS NULL ORDER BY m.meeting_date DESC LIMIT 1) AS last_id
                FROM people p ORDER BY p.name COLLATE NOCASE
                """).map { row in
                    // 沒有標題的會議跟會議列表一樣顯示檔名
                    let last = try (row["last_id"] as Int64?).flatMap { try Meeting.fetchOne(db, key: $0) }
                    return PersonSummary(id: row["id"], name: row["name"], voiceprintCount: row["prints"],
                                         lastMeetingTitle: last?.displayTitle, lastMeetingDate: last?.meetingDate)
                }
        }
    }

    /// 改名：認識的人和所有連到他的說話者一起改；新名字已經有人用了就合併過去
    func renamePerson(_ id: Int64, to name: String) throws {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        try pool.write { db in
            if let other = try Person.filter(sql: "name = ? COLLATE NOCASE AND id <> ?", arguments: [trimmed, id]).fetchOne(db), let otherId = other.id {
                try db.execute(sql: "UPDATE voiceprints SET person_id = ? WHERE person_id = ?", arguments: [otherId, id])
                try db.execute(sql: "UPDATE speakers SET person_id = ?, name = ? WHERE person_id = ?", arguments: [otherId, other.name, id])
                try db.execute(sql: "DELETE FROM people WHERE id = ?", arguments: [id])
            } else {
                try db.execute(sql: "UPDATE people SET name = ? WHERE id = ?", arguments: [trimmed, id])
                try db.execute(sql: "UPDATE speakers SET name = ? WHERE person_id = ?", arguments: [trimmed, id])
            }
        }
    }

    /// 刪除認識的人：聲紋刪掉（外鍵），已經帶入的名字留著
    func deletePerson(_ id: Int64) throws {
        try pool.write { db in try db.execute(sql: "DELETE FROM people WHERE id = ?", arguments: [id]) }
    }

    func deleteAllPeople() throws {
        try pool.write { db in try db.execute(sql: "DELETE FROM people") }
    }

    /// 認識的人的名字，給猜說話者參考寫法
    func peopleNames() throws -> [String] {
        try pool.read { db in try String.fetchAll(db, sql: "SELECT name FROM people ORDER BY name COLLATE NOCASE") }
    }
}
