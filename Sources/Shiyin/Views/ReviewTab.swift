import SwiftUI

/// 待確認分頁（R6）：每題可以回答或略過，送出後由 LLM 依回答修正
struct ReviewTab: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @State private var answers: [Int: String] = [:]
    @State private var dismissed: Set<Int> = []

    private var items: [MeetingNotes.ReviewItem] { detail.notes?.reviewItems ?? [] }

    private var answeredCount: Int {
        answers.filter { !dismissed.contains($0.key) && !$0.value.trimmingCharacters(in: .whitespaces).isEmpty }.count
    }

    var body: some View {
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
                    Text("以下是整理時不確定的地方。直接回答就好；看不懂或不重要的可以略過。")
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
                        model.submitReview(answers: answers, dismissed: dismissed)
                        answers = [:]
                        dismissed = []
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled((answeredCount == 0 && dismissed.isEmpty) || model.detailStatus?.isActive == true)
                }
                .padding(.horizontal, 32)
                .padding(.vertical, 12)
            }
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
            HStack(spacing: 12) {
                TextField("你的回答", text: Binding(get: { answers[index] ?? "" }, set: { answers[index] = $0 }))
                    .textFieldStyle(.roundedBorder)
                    .disabled(isDismissed)
                Toggle("略過", isOn: Binding(get: { isDismissed }, set: { on in
                    if on { dismissed.insert(index) } else { dismissed.remove(index) }
                }))
                .toggleStyle(.checkbox)
            }
            .padding(.leading, 22)
        }
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
