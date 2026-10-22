import Foundation
import GRDB

/// 一組問答（design §21.8）：meeting_id 是 NULL 時是問所有會議
struct QuestionRecord: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "questions"
    var id: Int64?
    var meetingId: Int64?
    var scope: String = ""
    var question: String
    var answer: String          // AskPrompt.Answer 的 JSON
    var provider: String
    var createdAt: String = Clock.now()

    var decoded: AskPrompt.Answer? { try? JSONDecoder().decode(AskPrompt.Answer.self, from: Data(answer.utf8)) }

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

enum AskTarget: Equatable {
    case meeting(Int64)
    case all
}

/// 問所有會議的範圍
enum AskScope: String, CaseIterable, Identifiable {
    case all, folder, recent
    var id: String { rawValue }
    var label: String {
        switch self {
        case .all: return String(localized: "所有會議")
        case .folder: return String(localized: "目前的資料夾")
        case .recent: return String(localized: "最近 30 天")
        }
    }
}

extension AppDatabase {
    func questions(meetingId: Int64?) throws -> [QuestionRecord] {
        try pool.read { db in
            if let meetingId {
                return try QuestionRecord.filter(Column("meeting_id") == meetingId).order(Column("id")).fetchAll(db)
            }
            return try QuestionRecord.filter(Column("meeting_id") == nil).order(Column("id")).fetchAll(db)
        }
    }

    func saveQuestion(_ record: QuestionRecord) throws {
        try pool.write { db in
            var record = record
            try record.insert(db)
        }
    }

    func deleteQuestions(meetingId: Int64?) throws {
        try pool.write { db in
            if let meetingId {
                try db.execute(sql: "DELETE FROM questions WHERE meeting_id = ?", arguments: [meetingId])
            } else {
                try db.execute(sql: "DELETE FROM questions WHERE meeting_id IS NULL")
            }
        }
    }
}

/// 對會議提問（design §21.8、R19.6、D32）：不排進工作佇列，使用者在等；同時只問一題
extension AppModel {
    /// 跨會議時最多帶幾場會議、總共多少字
    static let askMeetingLimit = 8
    static let askCharacterLimit = 40_000

    func loadMeetingQuestions(_ meetingId: Int64) {
        meetingQuestions = (try? db.questions(meetingId: meetingId)) ?? []
    }

    func loadAllQuestions() {
        allQuestions = (try? db.questions(meetingId: nil)) ?? []
    }

    func clearQuestions(meetingId: Int64?) {
        try? db.deleteQuestions(meetingId: meetingId)
        if let meetingId { loadMeetingQuestions(meetingId) } else { loadAllQuestions() }
    }

    /// 不能問時的原因（還沒設定整理工具、設定為不整理、正在問別的）
    var askUnavailableReason: String? {
        if AppSettings.provider == .none {
            return AppSettings.needsProviderSetup
                ? String(localized: "還沒設定整理工具，沒辦法提問；請到設定選擇 Claude、Codex 或 API")
                : String(localized: "整理工具設定為「不整理」，沒辦法提問；請到設定改成 Claude、Codex 或 API")
        }
        return nil
    }

    func cancelAsk() {
        askProcess?.terminate()
        askTask?.cancel()
    }

    // MARK: 一場會議

    func askMeeting(_ question: String) {
        let question = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !question.isEmpty, asking == nil, let id = selection, let meeting = try? db.meeting(id) else { return }
        if let reason = askUnavailableReason { errorMessage = reason; return }
        let provider = AppSettings.provider, model = AppSettings.model
        let language = meeting.meetingLanguage
        let names = Dictionary(uniqueKeysWithValues: ((try? db.speakers(id)) ?? []).map { ($0.idx, $0.name) })
        let lines = ((try? db.segments(id)) ?? []).map { s in
            let speaker = s.speaker.map { (names[$0] ?? language.text(zh: "說話者 \($0)", en: "Speaker \($0)", ja: "話者 \($0)")) + language.text(zh: "：", en: ": ", ja: "：") } ?? ""
            return "[\(TimeFormat.string(s.start))] \(speaker)\(s.text)"
        }
        let history = meetingQuestions.suffix(3).compactMap { q in q.decoded.map { (q.question, $0.answer) } }
        let prompt = AskPrompt.meetingPrompt(meeting: meeting, notes: meeting.decodedNotes, lines: lines, history: history, question: question)
        let system = AskPrompt.meetingSystem(language)
        run(.meeting(id)) {
            let raw = try await AskPrompt.generate(AskPrompt.Answer.self, provider: provider, model: model, system: system,
                                                   prompt: prompt, format: AskPrompt.meetingFormat, onStart: { self.askProcess = $0 })
            return QuestionRecord(meetingId: id, question: question,
                                  answer: Self.json(AskPrompt.validate(raw, meetingIds: nil)), provider: provider.rawValue)
        }
    }

    // MARK: 所有會議

    func askAll(_ question: String, scope: AskScope) {
        let question = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !question.isEmpty, asking == nil else { return }
        if let reason = askUnavailableReason { errorMessage = reason; return }
        let provider = AppSettings.provider, model = AppSettings.model
        let language = MeetingLanguage.interface
        let folder = folderFilter
        run(.all) {
            // 1. 抽關鍵詞
            struct Keywords: Decodable { var keywords: [String] }
            let keywords = try await AskPrompt.generate(Keywords.self, provider: provider, model: model, system: AskPrompt.keywordsSystem(language),
                                                        prompt: AskPrompt.keywordsPrompt(language, question: question),
                                                        format: AskPrompt.keywordsFormat, onStart: { self.askProcess = $0 }).keywords
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { $0.count >= 2 }
            try Task.checkCancellation()
            // 2. 全文搜尋，挑會議和片段
            let blocks = self.askBlocks(keywords: keywords, scope: scope, folder: folder)
            guard !blocks.isEmpty else {
                let answer = AskPrompt.Answer(answer: String(localized: "找不到跟這個問題有關的會議。可以換個說法，或用會議裡實際講的詞再問一次。"),
                                              found: false, citations: [], keywords: keywords, meetingIds: [])
                return QuestionRecord(meetingId: nil, scope: scope.rawValue, question: question, answer: Self.json(answer), provider: provider.rawValue)
            }
            // 3. 回答
            let raw = try await AskPrompt.generate(AskPrompt.Answer.self, provider: provider, model: model, system: AskPrompt.allSystem(language),
                                                   prompt: AskPrompt.allPrompt(language, blocks: blocks, question: question),
                                                   format: AskPrompt.allFormat, onStart: { self.askProcess = $0 })
            var answer = AskPrompt.validate(raw, meetingIds: Set(blocks.map(\.id)))
            answer.keywords = keywords
            answer.meetingIds = blocks.map(\.id)
            return QuestionRecord(meetingId: nil, scope: scope.rawValue, question: question, answer: Self.json(answer), provider: provider.rawValue)
        }
    }

    /// 用每個關鍵詞搜尋，依命中數、日期挑最多 8 場會議；每場帶摘要、決議和命中的段落（前後各一段），總長 40,000 字以內
    func askBlocks(keywords: [String], scope: AskScope, folder: String?) -> [AskPrompt.MeetingBlock] {
        var hits: [Int64: [SearchHit]] = [:]
        for keyword in keywords {
            for hit in (try? db.search(keyword, limit: 300)) ?? [] { hits[hit.meetingId, default: []].append(hit) }
        }
        let cutoff = DateText.string(Date().addingTimeInterval(-30 * 86_400))
        let candidates: [(meeting: Meeting, hits: [SearchHit])] = hits.compactMap { id, list in
            guard let meeting = try? db.meeting(id), meeting.deletedAt == nil else { return nil }
            switch scope {
            case .all: break
            case .folder: guard let folder, meeting.folder == folder else { return nil }
            case .recent: guard meeting.meetingDate >= cutoff else { return nil }
            }
            return (meeting, list)
        }
        .sorted { a, b in a.hits.count != b.hits.count ? a.hits.count > b.hits.count : a.meeting.meetingDate > b.meeting.meetingDate }
        var blocks: [AskPrompt.MeetingBlock] = []
        var total = 0
        for (meeting, list) in candidates.prefix(Self.askMeetingLimit) {
            let id = meeting.id!
            let language = meeting.meetingLanguage
            let notes = meeting.decodedNotes
            let summary = ((notes?.summary ?? []) + (notes?.decisions ?? [])).map { $0.time.isEmpty ? $0.text : "[\($0.time)] \($0.text)" }
            let segments = (try? db.segments(id)) ?? []
            let names = Dictionary(uniqueKeysWithValues: ((try? db.speakers(id)) ?? []).map { ($0.idx, $0.name) })
            let index = Dictionary(segments.enumerated().compactMap { i, s in s.id.map { ($0, i) } }, uniquingKeysWith: { a, _ in a })
            var wanted = Set<Int>()
            for hit in list where hit.kind == .segment {
                if let i = index[hit.ref] { wanted.formUnion([i - 1, i, i + 1].filter { segments.indices.contains($0) }) }
            }
            let lines = wanted.sorted().map { i -> String in
                let s = segments[i]
                let speaker = s.speaker.map { (names[$0] ?? language.text(zh: "說話者 \($0)", en: "Speaker \($0)", ja: "話者 \($0)")) + language.text(zh: "：", en: ": ", ja: "：") } ?? ""
                return "[\(TimeFormat.string(s.start))] \(speaker)\(s.text)"
            }
            var block = AskPrompt.MeetingBlock(id: id, title: meeting.displayTitle, date: meeting.meetingDate, summary: summary, lines: lines)
            let size = (block.summary + block.lines).reduce(0) { $0 + $1.count }
            if total + size > Self.askCharacterLimit {
                // 放不下整場：只放到上限為止的段落
                let room = Self.askCharacterLimit - total - block.summary.reduce(0) { $0 + $1.count }
                guard room > 200 else { break }
                var used = 0
                block.lines = block.lines.prefix { used += $0.count; return used <= room }.map { $0 }
            }
            total += (block.summary + block.lines).reduce(0) { $0 + $1.count }
            blocks.append(block)
        }
        return blocks
    }

    /// 共用的流程：標記正在問、可以取消、存問答、更新畫面
    private func run(_ target: AskTarget, _ work: @escaping @MainActor () async throws -> QuestionRecord) {
        asking = target
        askTask = Task {
            defer {
                asking = nil
                askTask = nil
                askProcess = nil
            }
            do {
                let record = try await work()
                guard !Task.isCancelled else { return }
                try db.saveQuestion(record)
                UsageStats.shared.asked()
                if case .meeting(let id) = target { loadMeetingQuestions(id) } else { loadAllQuestions() }
            } catch is CancellationError {
            } catch {
                // CLI 被結束（按了取消）時也會丟錯誤
                if !Task.isCancelled { errorMessage = error.localizedDescription }
            }
        }
    }

    private static func json(_ answer: AskPrompt.Answer) -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return String(decoding: (try? encoder.encode(answer)) ?? Data(), as: UTF8.self)
    }

    /// 點了回答裡的出處：打開那場會議、切到逐字稿、從那個時間點播放
    func openCitation(meetingId: Int64, time: String) {
        openMainWindow?()
        showTrash = false
        selection = meetingId
        tab = .transcript
        guard let seconds = TimeFormat.seconds(time) else { return }
        player.seek(to: seconds, play: false)
        if let id = detail?.segmentId(at: seconds) { flashSegment(id) }
    }
}
