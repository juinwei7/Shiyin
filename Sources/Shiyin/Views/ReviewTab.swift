import SwiftUI

/// 待確認分頁（R6）：每題可以回答或略過，送出後由 LLM 依回答修正。
/// 最上面是說話者的題目（聲紋沒把握的、AI 猜的，R33、design §27）：送出時直接改名字，不經過整理工具。
/// 回答存在 `ReviewDraft`（design §31.2），跟紀錄旁的就地回答（R41.2）共用同一份
struct ReviewTab: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    private var items: [MeetingNotes.ReviewItem] { detail.notes?.reviewItems ?? [] }
    private var speakerItems: [(idx: Int, suggestion: SpeakerNaming.Suggestion)] {
        detail.speakerSuggestions.sorted { $0.key < $1.key }.map { ($0.key, $0.value) }
    }

    var body: some View {
        if detail.notes == nil && speakerItems.isEmpty {
            NotesPlaceholder(detail: detail)
        } else if items.isEmpty && speakerItems.isEmpty {
            ReadingColumn {
                Label("沒有待確認的項目", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(Color.celadon)
                Text("會議紀錄裡推測出來的人名、專有名詞、負責人和期限，都已經確認過了。")
                    .foregroundStyle(.secondary)
            }
        } else if let draft = model.reviewDraft, draft.meetingId == detail.meeting.id {
            content(draft)
        }
    }

    private func content(_ draft: ReviewDraft) -> some View {
        VStack(spacing: 0) {
            ReadingColumn {
                Text(items.contains(where: \.yesNo) || !speakerItems.isEmpty
                     ? "以下是整理時不確定的地方。按「是」「不是」或直接回答就好；看不懂或不重要的可以略過。"
                     : "以下是整理時不確定的地方。直接回答就好；看不懂或不重要的可以略過。")
                    .foregroundStyle(.secondary)
                ForEach(speakerItems, id: \.idx) { item in
                    speakerRow(item.idx, item.suggestion, draft: draft)
                }
                ForEach(Array(items.enumerated()), id: \.offset) { index, item in
                    ReviewQuestionRow(index: index, item: item, language: language, draft: draft)
                }
            }
            Divider()
            HStack(spacing: 12) {
                Text(footnote)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                Spacer()
                Button(draft.submitTitle) { model.submitReviewDraft() }
                    .buttonStyle(.borderedProminent)
                    .disabled(!draft.hasSomethingToSubmit || model.detailStatus?.isActive == true)
            }
            .padding(.horizontal, 32)
            .padding(.vertical, 12)
        }
    }

    /// 說話者的題目（R33.2）：「『說話者 3』是 weiwei 嗎？」，下面是原因；選「不是」可以挑最像的人或打名字，也可以不填
    private func speakerRow(_ idx: Int, _ suggestion: SpeakerNaming.Suggestion, draft: ReviewDraft) -> some View {
        let skipped = draft.speakerSkipped.contains(idx)
        let current = detail.speakers[idx] ?? ""
        let question = language.text(zh: "「\(current)」是\(suggestion.name)嗎？", en: "Is “\(current)” \(suggestion.name)?",
                                     ja: "「\(current)」は\(suggestion.name)ですか？")  // l10n-skip：會議語言的題目
        let choice = YesNoAnswer(draft.speakerAnswers[idx] ?? "", language: language) ?? YesNoAnswer(isYes: nil)
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
                YesNoPicker(choice: choice, language: language, disabled: skipped) { draft.speakerAnswers[idx] = $0 }
                Spacer()
                Toggle("略過", isOn: Binding(get: { skipped }, set: { on in
                    if on { draft.speakerSkipped.insert(idx) } else { draft.speakerSkipped.remove(idx) }
                }))
                .toggleStyle(.checkbox)
            }
            .padding(.leading, 22)
            if choice.isYes == false {
                HStack(spacing: 10) {
                    if !others.isEmpty {
                        Menu("是…") {
                            ForEach(others, id: \.self) { name in
                                Button(name) { draft.speakerAnswers[idx] = YesNoAnswer(isYes: false, correction: name).text(language) }
                            }
                        }
                        .fixedSize()
                        .disabled(skipped)
                    }
                    TextField("是誰？（可以不填）", text: Binding(get: { choice.correction }, set: { text in
                        draft.speakerAnswers[idx] = YesNoAnswer(isYes: false, correction: text).text(language)
                    }))
                    .textFieldStyle(.roundedBorder)
                    .disabled(skipped)
                }
                .padding(.leading, 22)
            }
        }
    }

    private var language: MeetingLanguage { detail.meeting.meetingLanguage }

    private var footnote: String {
        let provider = AppSettings.provider == .none ? "LLM" : AppSettings.provider.label
        let speakers = String(localized: "說話者的題目不會送給整理工具，送出後直接換名字。")
        guard !items.isEmpty else { return speakers }
        let notes = String(localized: "有回答的題目會交給 \(provider) 依答案修正會議紀錄；略過的題目直接移除。改錯了可以從「更多」復原上一版。")
        return speakerItems.isEmpty ? notes : notes + " " + speakers
    }
}

/// 一題待確認（design §31.2）：待確認分頁和紀錄旁的就地回答共用。
/// 是非題（R32）給「是」「不是」，選了「不是」才要填正確的答案；舊的草稿認不出來時照舊用輸入框
struct ReviewQuestionRow: View {
    let index: Int
    let item: MeetingNotes.ReviewItem
    let language: MeetingLanguage
    let draft: ReviewDraft

    var body: some View {
        let isDismissed = draft.dismissed.contains(index)
        VStack(alignment: .leading, spacing: 8) {
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
            if item.yesNo, let choice = YesNoAnswer(draft.answers[index] ?? "", language: language) {
                HStack(spacing: 12) {
                    YesNoPicker(choice: choice, language: language, disabled: isDismissed) { draft.answers[index] = $0 }
                    Spacer()
                    skipToggle(isDismissed)
                }
                .padding(.leading, 22)
                if choice.isYes == false {
                    TextField("正確的是…（可以不填）", text: Binding(get: { choice.correction }, set: { text in
                        draft.answers[index] = YesNoAnswer(isYes: false, correction: text).text(language)
                    }))
                    .textFieldStyle(.roundedBorder)
                    .disabled(isDismissed)
                    .padding(.leading, 22)
                }
            } else {
                HStack(spacing: 12) {
                    TextField("你的回答", text: Binding(get: { draft.answers[index] ?? "" }, set: { draft.answers[index] = $0 }))
                        .textFieldStyle(.roundedBorder)
                        .disabled(isDismissed)
                    skipToggle(isDismissed)
                }
                .padding(.leading, 22)
            }
        }
    }

    private func skipToggle(_ isDismissed: Bool) -> some View {
        Toggle("略過", isOn: Binding(get: { isDismissed }, set: { on in
            if on { draft.dismissed.insert(index) } else { draft.dismissed.remove(index) }
        }))
        .toggleStyle(.checkbox)
    }
}

/// 是非題的「是」「不是」（design §27）：選了「是」就清掉填過的答案
struct YesNoPicker: View {
    let choice: YesNoAnswer
    let language: MeetingLanguage
    let disabled: Bool
    let set: (String) -> Void

    var body: some View {
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
