import SwiftUI

/// 待確認分頁（R6）：每題可以回答或略過，送出後由 LLM 依回答修正。
/// 最上面是說話者的題目（聲紋沒把握的、AI 猜的，R33、design §27）：送出時直接改名字，不經過整理工具
struct ReviewTab: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @State private var answers: [Int: String] = [:]
    @State private var dismissed: Set<Int> = []
    /// 說話者編號 → 回答（YesNoAnswer 的文字：「是」「不是，名字」）
    @State private var speakerAnswers: [Int: String] = [:]
    @State private var speakerSkipped: Set<Int> = []
    @State private var saveTask: Task<Void, Never>?

    private var items: [MeetingNotes.ReviewItem] { detail.notes?.reviewItems ?? [] }
    private var speakerItems: [(idx: Int, suggestion: SpeakerNaming.Suggestion)] {
        detail.speakerSuggestions.sorted { $0.key < $1.key }.map { ($0.key, $0.value) }
    }

    private var answeredCount: Int {
        answers.filter { !dismissed.contains($0.key) && !$0.value.trimmingCharacters(in: .whitespaces).isEmpty }.count
    }
    private var speakerAnsweredCount: Int {
        speakerAnswers.filter { !speakerSkipped.contains($0.key) && !$0.value.isEmpty }.count
    }

    var body: some View {
        content
            // 回答的草稿（design §21.15、R20.6）：用題目的文字當 key，換分頁、換會議、結束拾音都還在
            .onAppear(perform: loadDrafts)
            .onChange(of: answers) { _, _ in scheduleSave() }
            .onChange(of: dismissed) { _, _ in scheduleSave() }
            .onChange(of: speakerAnswers) { _, _ in scheduleSave() }
            .onChange(of: speakerSkipped) { _, _ in scheduleSave() }
            .onDisappear {
                saveTask?.cancel()
                saveDrafts()
            }
            .onReceive(NotificationCenter.default.publisher(for: .flushDrafts)) { _ in
                saveTask?.cancel()
                saveDrafts()
            }
    }

    @ViewBuilder
    private var content: some View {
        if detail.notes == nil && speakerItems.isEmpty {
            NotesPlaceholder(detail: detail)
        } else if items.isEmpty && speakerItems.isEmpty {
            ReadingColumn {
                Label("沒有待確認的項目", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(Color.celadon)
                Text("會議紀錄裡推測出來的人名、專有名詞、負責人和期限，都已經確認過了。")
                    .foregroundStyle(.secondary)
            }
        } else {
            VStack(spacing: 0) {
                ReadingColumn {
                    Text(items.contains(where: \.yesNo) || !speakerItems.isEmpty
                         ? "以下是整理時不確定的地方。按「是」「不是」或直接回答就好；看不懂或不重要的可以略過。"
                         : "以下是整理時不確定的地方。直接回答就好；看不懂或不重要的可以略過。")
                        .foregroundStyle(.secondary)
                    ForEach(speakerItems, id: \.idx) { item in
                        speakerRow(item.idx, item.suggestion)
                    }
                    ForEach(Array(items.enumerated()), id: \.offset) { index, item in
                        reviewRow(index, item)
                    }
                }
                Divider()
                HStack(spacing: 12) {
                    Text(footnote)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button(submitTitle, action: submit)
                        .buttonStyle(.borderedProminent)
                        .disabled((answeredCount + speakerAnsweredCount == 0 && dismissed.isEmpty && speakerSkipped.isEmpty)
                                  || model.detailStatus?.isActive == true)
                }
                .padding(.horizontal, 32)
                .padding(.vertical, 12)
            }
            // 回答是照題號存的：題目換了（依回答修正、重新整理、復原上一版）就清掉，不然會配到別的題目
            .onChange(of: items.map(\.question)) { _, _ in
                saveTask?.cancel()
                model.deleteDrafts(detail.meeting.id!, prefix: "review")
                answers = [:]
                dismissed = []
            }
            // 說話者的建議被套用或清掉（例如從「猜到幾位說話者」那邊）：那位的回答不留
            .onChange(of: speakerItems.map(\.idx)) { _, keys in
                speakerAnswers = speakerAnswers.filter { keys.contains($0.key) }
                speakerSkipped = speakerSkipped.filter { keys.contains($0) }
            }
        }
    }

    /// 送出：先送整理工具要處理的題目（題目文字可能有說話者的名字，改名後會變），失敗時全部留著；
    /// 再處理說話者的題目：是 = 套用建議，不是而且有名字 = 改成那個名字，不是沒填、略過 = 清掉建議
    private func submit() {
        if answeredCount > 0 || !dismissed.isEmpty {
            guard model.submitReview(answers: answers, dismissed: dismissed) else { return }
        }
        model.answerSpeakerQuestions(speakerAnswers, skipped: speakerSkipped)
        saveTask?.cancel()
        model.deleteDrafts(detail.meeting.id!, prefix: "review")
        model.deleteDrafts(detail.meeting.id!, prefix: "speaker-review")
        answers = [:]
        dismissed = []
        speakerAnswers = [:]
        speakerSkipped = []
    }

    /// 說話者的草稿 key：建議換了人（重新辨識、重新轉錄）就是新的題目
    private func speakerKey(_ idx: Int, _ suggestion: SpeakerNaming.Suggestion) -> String { "\(idx):\(suggestion.name)" }

    private func loadDrafts() {
        let drafts = model.drafts(detail.meeting.id!)
        guard !drafts.isEmpty else { return }
        for (index, item) in items.enumerated() {
            if let answer = drafts["review:" + item.question] { answers[index] = answer }
            if drafts["review-skip:" + item.question] != nil { dismissed.insert(index) }
        }
        for (idx, suggestion) in speakerItems {
            if let answer = drafts["speaker-review:" + speakerKey(idx, suggestion)] { speakerAnswers[idx] = answer }
            if drafts["speaker-review-skip:" + speakerKey(idx, suggestion)] != nil { speakerSkipped.insert(idx) }
        }
    }

    /// 打字停 0.8 秒存一次
    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task {
            try? await Task.sleep(for: .milliseconds(800))
            guard !Task.isCancelled else { return }
            saveDrafts()
        }
    }

    private func saveDrafts() {
        let id = detail.meeting.id!
        for (index, item) in items.enumerated() {
            model.saveDraft(id, key: "review:" + item.question, text: answers[index] ?? "")
            model.saveDraft(id, key: "review-skip:" + item.question, text: dismissed.contains(index) ? "1" : "")
        }
        for (idx, suggestion) in speakerItems {
            model.saveDraft(id, key: "speaker-review:" + speakerKey(idx, suggestion), text: speakerAnswers[idx] ?? "")
            model.saveDraft(id, key: "speaker-review-skip:" + speakerKey(idx, suggestion), text: speakerSkipped.contains(idx) ? "1" : "")
        }
    }

    private func reviewRow(_ index: Int, _ item: MeetingNotes.ReviewItem) -> some View {
        let isDismissed = dismissed.contains(index)
        return VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Text("\(index + 1)")
                    .font(.callout.monospacedDigit().weight(.semibold))
                    .foregroundStyle(Color.signal)
                TimeButton(time: item.time)
                Text(item.question).meetingText()
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .foregroundStyle(isDismissed ? .secondary : .primary)
                    .strikethrough(isDismissed)
            }
            // 是非題（R32、design §27）：「是」「不是」，選了「不是」才要填正確的答案；舊的草稿認不出來時照舊用輸入框
            if item.yesNo, let choice = YesNoAnswer(answers[index] ?? "", language: language) {
                HStack(spacing: 12) {
                    yesNoPicker(choice, disabled: isDismissed) { answers[index] = $0 }
                    Spacer()
                    skipToggle(index, isDismissed)
                }
                .padding(.leading, 22)
                if choice.isYes == false {
                    TextField("正確的是…（可以不填）", text: Binding(get: { choice.correction }, set: { text in
                        answers[index] = YesNoAnswer(isYes: false, correction: text).text(language)
                    }))
                    .textFieldStyle(.roundedBorder)
                    .disabled(isDismissed)
                    .padding(.leading, 22)
                }
            } else {
                HStack(spacing: 12) {
                    TextField("你的回答", text: Binding(get: { answers[index] ?? "" }, set: { answers[index] = $0 }))
                        .textFieldStyle(.roundedBorder)
                        .disabled(isDismissed)
                    skipToggle(index, isDismissed)
                }
                .padding(.leading, 22)
            }
        }
    }

    /// 說話者的題目（R33.2）：「『說話者 3』是 weiwei 嗎？」，下面是原因；選「不是」可以挑最像的人或打名字，也可以不填
    private func speakerRow(_ idx: Int, _ suggestion: SpeakerNaming.Suggestion) -> some View {
        let skipped = speakerSkipped.contains(idx)
        let current = detail.speakers[idx] ?? ""
        let question = language.text(zh: "「\(current)」是\(suggestion.name)嗎？", en: "Is “\(current)” \(suggestion.name)?",
                                     ja: "「\(current)」は\(suggestion.name)ですか？")  // l10n-skip：會議語言的題目
        let choice = YesNoAnswer(speakerAnswers[idx] ?? "", language: language) ?? YesNoAnswer(isYes: nil)
        let others = (detail.speakerCandidates[idx] ?? []).filter { $0.caseInsensitiveCompare(suggestion.name) != .orderedSame }
        return VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Image(systemName: "person.crop.circle.badge.questionmark")
                    .foregroundStyle(Color.speaker(idx))
                    .frame(width: 12)
                Text(question).meetingText()
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .foregroundStyle(skipped ? .secondary : .primary)
                    .strikethrough(skipped)
            }
            if !suggestion.reason.isEmpty || !suggestion.time.isEmpty {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    if !suggestion.time.isEmpty { TimeButton(time: suggestion.time) }
                    Text(suggestion.reason).meetingText()
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.leading, 22)
            }
            HStack(spacing: 12) {
                yesNoPicker(choice, disabled: skipped) { speakerAnswers[idx] = $0 }
                Spacer()
                Toggle("略過", isOn: Binding(get: { skipped }, set: { on in
                    if on { speakerSkipped.insert(idx) } else { speakerSkipped.remove(idx) }
                }))
                .toggleStyle(.checkbox)
            }
            .padding(.leading, 22)
            if choice.isYes == false {
                HStack(spacing: 10) {
                    if !others.isEmpty {
                        Menu("是…") {
                            ForEach(others, id: \.self) { name in
                                Button(name) { speakerAnswers[idx] = YesNoAnswer(isYes: false, correction: name).text(language) }
                            }
                        }
                        .fixedSize()
                        .disabled(skipped)
                    }
                    TextField("是誰？（可以不填）", text: Binding(get: { choice.correction }, set: { text in
                        speakerAnswers[idx] = YesNoAnswer(isYes: false, correction: text).text(language)
                    }))
                    .textFieldStyle(.roundedBorder)
                    .disabled(skipped)
                }
                .padding(.leading, 22)
            }
        }
    }

    /// 是非題的「是」「不是」：選了「是」就清掉填過的答案
    private func yesNoPicker(_ choice: YesNoAnswer, disabled: Bool, set: @escaping (String) -> Void) -> some View {
        Picker("回答", selection: Binding(get: { choice.isYes }, set: { yes in
            set(YesNoAnswer(isYes: yes, correction: yes == false ? choice.correction : "").text(language))
        })) {
            Text("是").tag(Bool?.some(true))
            Text("不是").tag(Bool?.some(false))
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .fixedSize()
        .disabled(disabled)
    }

    private var language: MeetingLanguage { detail.meeting.meetingLanguage }

    private func skipToggle(_ index: Int, _ isDismissed: Bool) -> some View {
        Toggle("略過", isOn: Binding(get: { isDismissed }, set: { on in
            if on { dismissed.insert(index) } else { dismissed.remove(index) }
        }))
        .toggleStyle(.checkbox)
    }

    private var submitTitle: String {
        switch (answeredCount + speakerAnsweredCount, dismissed.count + speakerSkipped.count) {
        case (0, 0): return String(localized: "送出")
        case (0, let d): return String(localized: "略過 \(d) 題")
        case (let a, 0): return String(localized: "送出 \(a) 題回答")
        case (let a, let d): return String(localized: "送出 \(a) 題回答、略過 \(d) 題")
        }
    }

    private var footnote: String {
        let provider = AppSettings.provider == .none ? "LLM" : AppSettings.provider.label
        let speakers = String(localized: "說話者的題目不會送給整理工具，送出後直接換名字。")
        guard !items.isEmpty else { return speakers }
        let notes = String(localized: "有回答的題目會交給 \(provider) 依答案修正會議紀錄；略過的題目直接移除。改錯了可以從「更多」復原上一版。")
        return speakerItems.isEmpty ? notes : notes + " " + speakers
    }
}

/// 是非題的回答（design §27）：存成會議語言的文字，就是草稿和送給校稿的回答。
/// 「是」→ 是／Yes／はい；「不是」→ 不是／No／いいえ；不是而且填了答案 →「不是，答案」「No, 答案」「いいえ、答案」
struct YesNoAnswer: Equatable {
    /// nil：還沒選
    var isYes: Bool?
    var correction = ""

    init(isYes: Bool?, correction: String = "") {
        self.isYes = isYes
        self.correction = correction
    }

    /// 從存起來的文字認回來；空字串是還沒選，認不出來的（例如舊的草稿）回傳 nil
    init?(_ text: String, language: MeetingLanguage) {
        let yes = Self.yes(language), no = Self.no(language), separator = Self.separator(language)
        if text.isEmpty {
            self.init(isYes: nil)
        } else if text == yes {
            self.init(isYes: true)
        } else if text == no {
            self.init(isYes: false)
        } else if text.hasPrefix(no + separator) {
            self.init(isYes: false, correction: String(text.dropFirst(no.count + separator.count)))
        } else {
            return nil
        }
    }

    func text(_ language: MeetingLanguage) -> String {
        switch isYes {
        case nil: return ""
        case true?: return Self.yes(language)
        case false?: return correction.isEmpty ? Self.no(language) : Self.no(language) + Self.separator(language) + correction
        }
    }

    private static func yes(_ l: MeetingLanguage) -> String { l.text(zh: "是", en: "Yes", ja: "はい") }
    private static func no(_ l: MeetingLanguage) -> String { l.text(zh: "不是", en: "No", ja: "いいえ") }
    private static func separator(_ l: MeetingLanguage) -> String { l.text(zh: "，", en: ", ", ja: "、") }
}
