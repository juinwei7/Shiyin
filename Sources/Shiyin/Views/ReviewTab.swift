import SwiftUI

/// 待確認分頁（R6）：每題可以回答或略過，送出後由 LLM 依回答修正
struct ReviewTab: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @State private var answers: [Int: String] = [:]
    @State private var dismissed: Set<Int> = []
    @State private var saveTask: Task<Void, Never>?

    private var items: [MeetingNotes.ReviewItem] { detail.notes?.reviewItems ?? [] }

    private var answeredCount: Int {
        answers.filter { !dismissed.contains($0.key) && !$0.value.trimmingCharacters(in: .whitespaces).isEmpty }.count
    }

    var body: some View {
        content
            // 回答的草稿（design §21.15、R20.6）：用題目的文字當 key，換分頁、換會議、結束拾音都還在
            .onAppear(perform: loadDrafts)
            .onChange(of: answers) { _, _ in scheduleSave() }
            .onChange(of: dismissed) { _, _ in scheduleSave() }
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
        if detail.notes == nil {
            NotesPlaceholder(detail: detail)
        } else if items.isEmpty {
            ReadingColumn {
                Label("沒有待確認的項目", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(Color.celadon)
                Text("會議紀錄裡推測出來的人名、專有名詞、負責人和期限，都已經確認過了。")
                    .foregroundStyle(.secondary)
            }
        } else {
            VStack(spacing: 0) {
                ReadingColumn {
                    Text(items.contains(where: \.yesNo)
                         ? "以下是整理時不確定的地方。按「是」「不是」或直接回答就好；看不懂或不重要的可以略過。"
                         : "以下是整理時不確定的地方。直接回答就好；看不懂或不重要的可以略過。")
                        .foregroundStyle(.secondary)
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
                    Button(submitTitle) {
                        // 送出失敗（例如整理工具設成不整理）時保留回答
                        if model.submitReview(answers: answers, dismissed: dismissed) {
                            saveTask?.cancel()
                            model.deleteDrafts(detail.meeting.id!, prefix: "review")
                            answers = [:]
                            dismissed = []
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled((answeredCount == 0 && dismissed.isEmpty) || model.detailStatus?.isActive == true)
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
        }
    }

    private func loadDrafts() {
        let drafts = model.drafts(detail.meeting.id!)
        guard !drafts.isEmpty else { return }
        for (index, item) in items.enumerated() {
            if let answer = drafts["review:" + item.question] { answers[index] = answer }
            if drafts["review-skip:" + item.question] != nil { dismissed.insert(index) }
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
                    Picker("回答", selection: Binding(get: { choice.isYes }, set: { yes in
                        answers[index] = YesNoAnswer(isYes: yes, correction: yes == false ? choice.correction : "").text(language)
                    })) {
                        Text("是").tag(Bool?.some(true))
                        Text("不是").tag(Bool?.some(false))
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .fixedSize()
                    .disabled(isDismissed)
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

    private var language: MeetingLanguage { detail.meeting.meetingLanguage }

    private func skipToggle(_ index: Int, _ isDismissed: Bool) -> some View {
        Toggle("略過", isOn: Binding(get: { isDismissed }, set: { on in
            if on { dismissed.insert(index) } else { dismissed.remove(index) }
        }))
        .toggleStyle(.checkbox)
    }

    private var submitTitle: String {
        switch (answeredCount, dismissed.count) {
        case (0, 0): return String(localized: "送出")
        case (0, let d): return String(localized: "略過 \(d) 題")
        case (let a, 0): return String(localized: "送出 \(a) 題回答")
        case (let a, let d): return String(localized: "送出 \(a) 題回答、略過 \(d) 題")
        }
    }

    private var footnote: String {
        let provider = AppSettings.provider == .none ? "LLM" : AppSettings.provider.label
        return String(localized: "有回答的題目會交給 \(provider) 依答案修正會議紀錄；略過的題目直接移除。改錯了可以從「更多」復原上一版。")
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
