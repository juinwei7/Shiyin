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
