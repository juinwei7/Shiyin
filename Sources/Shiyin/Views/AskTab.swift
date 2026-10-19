import SwiftUI

/// 對這場會議提問（design §21.8、R19.6）：回答附上錄音時間點，點了跳過去；問答紀錄留在會議裡
struct AskTab: View {
    @Environment(AppModel.self) private var model
    @Environment(\.snapshotMode) private var snapshotMode
    let detail: MeetingDetail
    @State private var question = ""

    private var asking: Bool { model.asking == .meeting(detail.meeting.id!) }

    var body: some View {
        VStack(spacing: 0) {
            if snapshotMode {
                // 截圖畫不出 ScrollView 的內容（DEBUG 的 snapshot），直接排出來
                list
                Spacer(minLength: 0)
            } else {
                ScrollViewReader { proxy in
                    ScrollView { list }
                        .onChange(of: model.meetingQuestions.count) { _, _ in
                            if let last = model.meetingQuestions.last?.id { withAnimation { proxy.scrollTo(last, anchor: .top) } }
                        }
                }
            }
            Divider()
            AskInputBar(question: $question, placeholder: String(localized: "問這場會議，例如：誰負責寄信的部分？"),
                        busy: model.asking != nil, onClear: model.meetingQuestions.isEmpty ? nil : {
                            model.clearQuestions(meetingId: detail.meeting.id)
                        }) {
                model.askMeeting(question)
                question = ""
            }
        }
        .onAppear { model.loadMeetingQuestions(detail.meeting.id!) }
    }

    private var list: some View {
        VStack(alignment: .leading, spacing: 18) {
            if model.meetingQuestions.isEmpty && !asking {
                VStack(alignment: .leading, spacing: 6) {
                    Text("問這場會議").font(.songti(18))
                    Text("例如：「誰負責寄信的部分？」「最後決定的上線日期是哪一天？」回答只根據這場會議的會議紀錄和逐字稿，每一點都附上時間，點了就能聽原話。")
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            ForEach(model.meetingQuestions) { record in
                QuestionView(record: record) { time in
                    if let seconds = TimeFormat.seconds(time) { model.player.seek(to: seconds) }
                }
                .id(record.id)
            }
            if asking {
                HStack(spacing: 10) {
                    ProgressView().controlSize(.small)
                    Text("正在用 \(AppSettings.provider.shortLabel) 回答…").foregroundStyle(.secondary)
                    Button("取消") { model.cancelAsk() }.buttonStyle(.link)
                }
                .id("asking")
            }
        }
        .padding(.horizontal, 32)
        .padding(.vertical, 20)
        .frame(maxWidth: 760, alignment: .leading)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// 提問的輸入列（單場和所有會議共用）
struct AskInputBar: View {
    @Environment(AppModel.self) private var model
    @Binding var question: String
    let placeholder: String
    let busy: Bool
    var onClear: (() -> Void)?
    let onSubmit: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let reason = model.askUnavailableReason {
                Label(reason, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(Color.signal)
            }
            HStack(spacing: 10) {
                TextField(placeholder, text: $question, axis: .vertical)
                    .textFieldStyle(.roundedBorder)
                    .lineLimit(1...4)
                    .onSubmit(submit)
                Button("提問", action: submit)
                    .buttonStyle(.borderedProminent)
                    .disabled(busy || question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || model.askUnavailableReason != nil)
                if let onClear {
                    Menu {
                        Button("清除問答紀錄", role: .destructive, action: onClear)
                    } label: { Image(systemName: "ellipsis.circle") }
                    .menuStyle(.borderlessButton)
                    .menuIndicator(.hidden)
                    .fixedSize()
                    .help("更多")
                }
            }
        }
        .padding(.horizontal, 32)
        .padding(.vertical, 12)
    }

    private func submit() {
        guard !busy, model.askUnavailableReason == nil, !question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        onSubmit()
    }
}

/// 一組問答：問題、回答、出處（時間點，跨會議時加上會議名稱）
struct QuestionView: View {
    @Environment(AppModel.self) private var model
    let record: QuestionRecord
    var meetingTitle: ((Int64) -> String?)? = nil
    let onCitation: (String) -> Void
    var onMeetingCitation: ((Int64, String) -> Void)? = nil

    var body: some View {
        let answer = record.decoded
        VStack(alignment: .leading, spacing: 8) {
            Text(record.question).fontWeight(.semibold).textSelection(.enabled)
            if let answer {
                // 回答常用 **粗體**：解析行內的 Markdown，保留換行和「- 」列點
                Text((try? AttributedString(markdown: answer.answer, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
                     ?? AttributedString(answer.answer))
                    .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    .foregroundStyle(answer.found ? .primary : .secondary)
                if !answer.citations.isEmpty {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(Array(answer.citations.enumerated()), id: \.offset) { _, citation in
                            HStack(alignment: .firstTextBaseline, spacing: 8) {
                                if let id = citation.meetingId, let onMeetingCitation {
                                    Button {
                                        onMeetingCitation(id, citation.time)
                                    } label: {
                                        Text("\(meetingTitle?(id) ?? String(localized: "會議")) \(citation.time)")
                                            .font(.callout.monospacedDigit())
                                            .foregroundStyle(Color.celadon)
                                    }
                                    .buttonStyle(.plain)
                                    .help("打開這場會議，從 \(citation.time) 開始")
                                } else {
                                    Button(citation.time) { onCitation(citation.time) }
                                        .buttonStyle(.plain)
                                        .font(.callout.monospacedDigit())
                                        .foregroundStyle(Color.celadon)
                                        .help("從 \(citation.time) 開始播放")
                                }
                                Text(citation.quote).font(.callout).foregroundStyle(.secondary).lineLimit(2).meetingText()
                            }
                        }
                    }
                }
                if let ids = answer.meetingIds, record.meetingId == nil {
                    Text(ids.isEmpty ? String(localized: "沒有找到相關的會議") : String(localized: "參考了 \(ids.count) 場會議"))
                        .font(.caption).foregroundStyle(.tertiary)
                }
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.secondary.opacity(0.06), in: RoundedRectangle(cornerRadius: 10))
    }
}

/// 問所有會議（design §21.8）：獨立的視窗，可以一邊看會議一邊問；出處點了打開那場會議
struct AskAllView: View {
    @Environment(AppModel.self) private var model
    @State private var question = ""
    @State private var scope: AskScope = .all

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("問所有會議").font(.songti(20))
                Spacer()
                Picker("範圍", selection: $scope) {
                    ForEach(AskScope.allCases) { scope in
                        if scope != .folder || model.folderFilter != nil { Text(scope.label).tag(scope) }
                    }
                }
                .pickerStyle(.segmented)
                .fixedSize()
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 14)
            Divider()
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 18) {
                        if model.allQuestions.isEmpty && model.asking != .all {
                            Text("例如：「上個月跟客戶談過哪些價格？」「寄信的機制最後怎麼決定的？」拾音會先找出相關的會議片段，再請整理工具回答，回答附上是哪一場會議、哪個時間點。")
                                .foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        ForEach(model.allQuestions) { record in
                            QuestionView(record: record, meetingTitle: { id in
                                model.meetings.first { $0.id == id }?.meeting.displayTitle
                            }, onCitation: { _ in }, onMeetingCitation: { id, time in model.openCitation(meetingId: id, time: time) })
                            .id(record.id)
                        }
                        if model.asking == .all {
                            HStack(spacing: 10) {
                                ProgressView().controlSize(.small)
                                Text("正在找相關的會議，再用 \(AppSettings.provider.shortLabel) 回答…").foregroundStyle(.secondary)
                                Button("取消") { model.cancelAsk() }.buttonStyle(.link)
                            }
                        }
                    }
                    .padding(24)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .onChange(of: model.allQuestions.count) { _, _ in
                    if let last = model.allQuestions.last?.id { withAnimation { proxy.scrollTo(last, anchor: .top) } }
                }
            }
            Divider()
            AskInputBar(question: $question, placeholder: String(localized: "問所有會議，例如：寄信的機制最後怎麼決定的？"),
                        busy: model.asking != nil, onClear: model.allQuestions.isEmpty ? nil : { model.clearQuestions(meetingId: nil) }) {
                model.askAll(question, scope: scope)
                question = ""
            }
        }
        .frame(minWidth: 560, minHeight: 480)
        .onAppear { model.loadAllQuestions() }
    }
}
