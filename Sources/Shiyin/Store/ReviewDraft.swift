import Foundation
import Observation

/// 待確認的回答（design §31.2、R41.2）：待確認分頁和紀錄旁的就地回答共用同一份，兩邊看到的一樣。
/// 草稿存在資料庫（§21.15、R20.6）：打字停 0.8 秒存一次；換分頁、換會議、結束拾音都還在，直到送出。
/// 回答是照題號存的：題目換了（依回答修正、重新整理、復原上一版）就清掉，不然會配到別的題目（`sync`）
@MainActor
@Observable
final class ReviewDraft {
    let meetingId: Int64
    /// 題號 → 回答（是非題是 `YesNoAnswer` 的文字）
    var answers: [Int: String] = [:] { didSet { scheduleSave() } }
    var dismissed: Set<Int> = [] { didSet { scheduleSave() } }
    /// 說話者編號 → 回答（「是」「不是，名字」）
    var speakerAnswers: [Int: String] = [:] { didSet { scheduleSave() } }
    var speakerSkipped: Set<Int> = [] { didSet { scheduleSave() } }
    /// 就地回答展開中的那一條紀錄（R41.2），同時只展開一條
    var expanded: NoteItemRef?

    @ObservationIgnored private unowned let model: AppModel
    @ObservationIgnored private var questions: [String] = []
    /// 說話者編號 → 草稿的 key（建議換了人就是新的題目）
    @ObservationIgnored private var speakerKeys: [Int: String] = [:]
    @ObservationIgnored private var loaded = false
    @ObservationIgnored private var muted = false
    @ObservationIgnored private var saveTask: Task<Void, Never>?
    @ObservationIgnored nonisolated(unsafe) private var observer: NSObjectProtocol?

    init(meetingId: Int64, model: AppModel) {
        self.meetingId = meetingId
        self.model = model
        // 拾音要結束了：馬上存（design §21.15）
        observer = NotificationCenter.default.addObserver(forName: .flushDrafts, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.flush() }
        }
    }

    deinit {
        if let observer { NotificationCenter.default.removeObserver(observer) }
    }

    /// 載入會議、會議紀錄或說話者換掉時呼叫：第一次從資料庫讀草稿；題目變了就清掉回答；
    /// 說話者的建議被套用或清掉（例如從「猜到幾位說話者」那邊）就不留那位的回答
    func sync(items: [MeetingNotes.ReviewItem], suggestions: [Int: SpeakerNaming.Suggestion]) {
        let newQuestions = items.map(\.question)
        let newKeys = Dictionary(uniqueKeysWithValues: suggestions.map { ($0.key, "\($0.key):\($0.value.name)") })
        if !loaded {
            loaded = true
            questions = newQuestions
            speakerKeys = newKeys
            let drafts = model.drafts(meetingId)
            guard !drafts.isEmpty else { return }
            withoutSaving {
                for (index, question) in newQuestions.enumerated() {
                    if let answer = drafts["review:" + question] { answers[index] = answer }
                    if drafts["review-skip:" + question] != nil { dismissed.insert(index) }
                }
                for (idx, key) in newKeys {
                    if let answer = drafts["speaker-review:" + key] { speakerAnswers[idx] = answer }
                    if drafts["speaker-review-skip:" + key] != nil { speakerSkipped.insert(idx) }
                }
            }
            return
        }
        if newQuestions != questions {
            // 題目換了（送出一部分、依回答修正、重新整理、復原上一版）：草稿是照題目文字存的，
            // 還在的題目把回答照文字接回來，不在的刪掉草稿（design §31.2）
            saveTask?.cancel()
            var answerByText: [String: String] = [:], skippedTexts: Set<String> = []
            for (index, question) in questions.enumerated() {
                if let answer = answers[index] { answerByText[question] = answer }
                if dismissed.contains(index) { skippedTexts.insert(question) }
            }
            let surviving = Set(newQuestions)
            for question in questions where !surviving.contains(question) {
                model.saveDraft(meetingId, key: "review:" + question, text: "")
                model.saveDraft(meetingId, key: "review-skip:" + question, text: "")
            }
            questions = newQuestions
            withoutSaving {
                answers = [:]
                dismissed = []
                for (index, question) in newQuestions.enumerated() {
                    if let answer = answerByText[question] { answers[index] = answer }
                    if skippedTexts.contains(question) { dismissed.insert(index) }
                }
            }
            expanded = nil
        }
        if newKeys != speakerKeys {
            speakerKeys = newKeys
            withoutSaving {
                speakerAnswers = speakerAnswers.filter { newKeys[$0.key] != nil }
                speakerSkipped = speakerSkipped.filter { newKeys[$0] != nil }
            }
        }
    }

    /// 「全部略過」（R41.5）：還沒回答的題目都標成略過，已經有回答的不動
    func skipAllUnanswered(questionCount: Int, speakers: [Int]) {
        var skipQuestions = dismissed
        for index in 0..<max(0, questionCount) where (answers[index] ?? "").trimmingCharacters(in: .whitespaces).isEmpty {
            skipQuestions.insert(index)
        }
        dismissed = skipQuestions
        var skipSpeakers = speakerSkipped
        for idx in speakers where (speakerAnswers[idx] ?? "").isEmpty { skipSpeakers.insert(idx) }
        speakerSkipped = skipSpeakers
    }

    /// 還有沒回答也沒略過的題目（「全部略過」才有東西可以略）
    func hasUnanswered(questionCount: Int, speakers: [Int]) -> Bool {
        (0..<max(0, questionCount)).contains { !dismissed.contains($0) && (answers[$0] ?? "").trimmingCharacters(in: .whitespaces).isEmpty }
            || speakers.contains { !speakerSkipped.contains($0) && (speakerAnswers[$0] ?? "").isEmpty }
    }

    /// 送出後：草稿刪掉、回答清空
    func clear() {
        saveTask?.cancel()
        model.deleteDrafts(meetingId, prefix: "review")
        model.deleteDrafts(meetingId, prefix: "speaker-review")
        withoutSaving { answers = [:]; dismissed = []; speakerAnswers = [:]; speakerSkipped = [] }
        expanded = nil
    }

    /// 卡片只送了這幾題（R41.2）：只清這幾題的回答，其他留著；題目消失後 `sync` 會把它們的草稿刪掉
    func clear(indexes: Set<Int>) {
        withoutSaving {
            for index in indexes { answers[index] = nil }
            dismissed.subtract(indexes)
        }
        expanded = nil
    }

    /// 馬上存（結束拾音、換會議）
    func flush() {
        saveTask?.cancel()
        save()
    }

    // MARK: 題數、送出鈕（待確認分頁和就地回答共用）

    /// 已回答的題數；`only` 給了就只算這幾題（卡片）
    func answeredCount(only: Set<Int>? = nil) -> Int {
        answers.filter { (only?.contains($0.key) ?? true) && !dismissed.contains($0.key) && !$0.value.trimmingCharacters(in: .whitespaces).isEmpty }.count
    }

    func dismissedCount(only: Set<Int>? = nil) -> Int {
        only.map { dismissed.intersection($0).count } ?? dismissed.count
    }

    var answeredCount: Int { answeredCount() }

    var speakerAnsweredCount: Int {
        speakerAnswers.filter { !speakerSkipped.contains($0.key) && !$0.value.isEmpty }.count
    }

    /// 待確認分頁：全部的題目（含說話者題目）；卡片（`only`）：只有這幾題
    func hasSomethingToSubmit(only: Set<Int>? = nil) -> Bool {
        if let only { return answeredCount(only: only) + dismissedCount(only: only) > 0 }
        return answeredCount + speakerAnsweredCount > 0 || !dismissed.isEmpty || !speakerSkipped.isEmpty
    }

    var hasSomethingToSubmit: Bool { hasSomethingToSubmit() }

    func submitTitle(only: Set<Int>? = nil) -> String {
        let answered = answeredCount(only: only) + (only == nil ? speakerAnsweredCount : 0)
        let skipped = dismissedCount(only: only) + (only == nil ? speakerSkipped.count : 0)
        switch (answered, skipped) {
        case (0, 0): return String(localized: "送出")
        case (0, let d): return String(localized: "略過 \(d) 題")
        case (let a, 0): return String(localized: "送出 \(a) 題回答")
        case (let a, let d): return String(localized: "送出 \(a) 題回答、略過 \(d) 題")
        }
    }

    var submitTitle: String { submitTitle() }

    // MARK: 存草稿

    private func withoutSaving(_ body: () -> Void) {
        muted = true
        body()
        muted = false
    }

    /// 打字停 0.8 秒存一次
    private func scheduleSave() {
        guard loaded, !muted else { return }
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(800))
            guard !Task.isCancelled else { return }
            self?.save()
        }
    }

    private func save() {
        guard loaded else { return }
        for (index, question) in questions.enumerated() {
            model.saveDraft(meetingId, key: "review:" + question, text: answers[index] ?? "")
            model.saveDraft(meetingId, key: "review-skip:" + question, text: dismissed.contains(index) ? "1" : "")
        }
        for (idx, key) in speakerKeys {
            model.saveDraft(meetingId, key: "speaker-review:" + key, text: speakerAnswers[idx] ?? "")
            model.saveDraft(meetingId, key: "speaker-review-skip:" + key, text: speakerSkipped.contains(idx) ? "1" : "")
        }
    }
}
