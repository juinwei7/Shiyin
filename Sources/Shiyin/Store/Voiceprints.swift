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
    /// 取中間偏高，寧可少帶入、不要帶錯
    static let voiceprintThreshold = 0.65
    /// 同一個人的聲紋最多留幾份，超過刪最舊的
    static let voiceprintsPerPerson = 30

    /// 轉錄完：還是預設名稱的說話者跟認識的人比對，夠像的自動帶入名字（name_source = voiceprint）。
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
            for speaker in speakers {
                let vector = Voiceprint.vector(speaker.embedding!)
                for (person, prints) in library {
                    let score = prints.map { Voiceprint.similarity(vector, $0) }.max() ?? 0
                    if score >= Self.voiceprintThreshold { candidates.append((score, speaker.idx, person)) }
                }
            }
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
                try db.execute(sql: "UPDATE speakers SET name = ?, name_source = 'voiceprint', person_id = ? WHERE meeting_id = ? AND idx = ?",
                               arguments: [name, candidate.person, meetingId, candidate.idx])
                matched += 1
            }
            return matched
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

    /// 「不是他」：自動帶入的名字不對，回到預設名稱；之後重新比對也不再帶入這位（name_source = manual）
    func rejectVoiceprintMatch(_ meetingId: Int64, idx: Int, defaultName: String) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE speakers SET name = ?, name_source = 'manual', person_id = NULL WHERE meeting_id = ? AND idx = ?",
                           arguments: [defaultName, meetingId, idx])
            try db.execute(sql: "DELETE FROM voiceprints WHERE meeting_id = ? AND speaker_idx = ?", arguments: [meetingId, idx])
            try Self.deleteOrphanPeople(db)
        }
    }

    /// 沒有聲紋、也沒有連到任何說話者的人（例如取名後又改回去），不留在認識的人裡
    private static func deleteOrphanPeople(_ db: Database) throws {
        try db.execute(sql: """
            DELETE FROM people WHERE id NOT IN (SELECT person_id FROM voiceprints)
              AND id NOT IN (SELECT person_id FROM speakers WHERE person_id IS NOT NULL)
            """)
    }

    // MARK: 設定頁：認識的人

    func peopleSummaries() throws -> [PersonSummary] {
        try pool.read { db in
            try Row.fetchAll(db, sql: """
                SELECT p.id, p.name,
                  (SELECT count(*) FROM voiceprints v WHERE v.person_id = p.id) AS prints,
                  (SELECT m.title FROM speakers s JOIN meetings m ON m.id = s.meeting_id
                     WHERE s.person_id = p.id AND m.deleted_at IS NULL ORDER BY m.meeting_date DESC LIMIT 1) AS last_title,
                  (SELECT m.meeting_date FROM speakers s JOIN meetings m ON m.id = s.meeting_id
                     WHERE s.person_id = p.id AND m.deleted_at IS NULL ORDER BY m.meeting_date DESC LIMIT 1) AS last_date
                FROM people p ORDER BY p.name COLLATE NOCASE
                """).map { PersonSummary(id: $0["id"], name: $0["name"], voiceprintCount: $0["prints"],
                                         lastMeetingTitle: $0["last_title"], lastMeetingDate: $0["last_date"]) }
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
}
