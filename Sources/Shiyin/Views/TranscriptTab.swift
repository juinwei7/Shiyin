import SwiftUI

/// 逐字稿分頁（R4）：時間點跳播、播放時高亮跟隨、搜尋、單段編輯、全文取代
struct TranscriptTab: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.snapshotMode) private var snapshotMode
    let detail: MeetingDetail

    @State private var query = ""
    @State private var matchCursor = 0
    @State private var editingId: Int64?
    @State private var editText = ""
    @State private var showReplace = false
    @State private var speakerFilter: Int?
    @State private var renaming: Int?
    @State private var renameText = ""
    @FocusState private var searchFocused: Bool

    init(detail: MeetingDetail, speakerFilter: Int? = nil) {
        self.detail = detail
        _speakerFilter = State(initialValue: speakerFilter)
    }

    private var visibleSegments: [Segment] {
        guard let speakerFilter else { return detail.segments }
        return detail.segments.filter { $0.speaker == speakerFilter }
    }

    private var matches: [Int64] {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return [] }
        return visibleSegments.filter { $0.text.localizedCaseInsensitiveContains(q) }.compactMap(\.id)
    }

    private var currentSegmentId: Int64? {
        let t = model.player.currentTime
        guard model.player.isPlaying || t > 0 else { return nil }
        return detail.segments.last(where: { $0.start <= t + 0.05 })?.id
    }

    var body: some View {
        VStack(spacing: 0) {
            searchBar
            Divider()
            if detail.segments.isEmpty {
                Text(model.detailStatus?.isActive == true ? "轉錄完成後，逐字稿會出現在這裡。" : "沒有逐字稿。")
                    .foregroundStyle(.secondary)
                    .padding(32)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Spacer()
            } else if snapshotMode {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(visibleSegments.prefix(18)) { segment in row(segment) }
                }
                .frame(maxWidth: 760, alignment: .leading)
                .padding(.horizontal, 20)
                .padding(.vertical, 14)
                .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(visibleSegments) { segment in
                                row(segment)
                                    .id(segment.id)
                            }
                        }
                        .frame(maxWidth: 760, alignment: .leading)
                        .padding(.horizontal, 20)
                        .padding(.vertical, 14)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .onChange(of: currentSegmentId) { _, id in
                        guard let id, model.player.isPlaying, editingId == nil else { return }
                        if reduceMotion { proxy.scrollTo(id, anchor: .center) }
                        else { withAnimation(.easeInOut(duration: 0.25)) { proxy.scrollTo(id, anchor: .center) } }
                    }
                    .onChange(of: matchCursor) { _, cursor in
                        if matches.indices.contains(cursor) { proxy.scrollTo(matches[cursor], anchor: .center) }
                    }
                    .onChange(of: query) { _, _ in
                        matchCursor = 0
                        if let first = matches.first { proxy.scrollTo(first, anchor: .center) }
                    }
                    .onChange(of: model.highlightSegment, initial: true) { _, id in
                        guard let id else { return }
                        // 要跳到的段落被說話者篩選藏起來時，先取消篩選
                        if !visibleSegments.contains(where: { $0.id == id }) { speakerFilter = nil }
                        Task { @MainActor in
                            try? await Task.sleep(for: .milliseconds(150))
                            proxy.scrollTo(id, anchor: .center)
                        }
                    }
                }
            }
        }
        .sheet(isPresented: $showReplace) {
            ReplaceSheet(initialFind: query)
        }
    }

    private var searchBar: some View {
        HStack(spacing: 10) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("搜尋逐字稿", text: $query)
                    .textFieldStyle(.plain)
                    .focused($searchFocused)
                    .onSubmit { if !matches.isEmpty { matchCursor = (matchCursor + 1) % matches.count } }
                if !query.isEmpty {
                    Text(matches.isEmpty ? "找不到" : "\(min(matchCursor + 1, matches.count))／\(matches.count) 段")
                        .font(.caption)
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                    Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 6))
            .frame(maxWidth: 360)
            // ⌘F 聚焦搜尋框（design §9.4）
            Button("搜尋") { searchFocused = true }
                .keyboardShortcut("f")
                .hidden()
                .frame(width: 0)
            if !detail.speakers.isEmpty {
                Picker("說話者", selection: $speakerFilter) {
                    Text("所有說話者").tag(Int?.none)
                    ForEach(detail.speakers.keys.sorted(), id: \.self) { idx in
                        Text(detail.speakers[idx] ?? "說話者 \(idx)").tag(Int?.some(idx))
                    }
                }
                .labelsHidden()
                .frame(width: 150)
                .help("只看某一位說話者的內容")
            }
            Spacer()
            if detail.segments.contains(where: \.lowConfidence) {
                Label("琥珀色是辨識信心低的段落", systemImage: "circle.fill")
                    .labelStyle(SmallDotLabelStyle())
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Button("用詞修正…") { showReplace = true }
                .disabled(detail.segments.isEmpty)
                .help("把逐字稿和會議紀錄裡的同一個詞一起改掉")
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 10)
    }

    @ViewBuilder
    private func row(_ segment: Segment) -> some View {
        let isCurrent = segment.id == currentSegmentId
        let isHighlighted = segment.id == model.highlightSegment
        HStack(alignment: .firstTextBaseline, spacing: 14) {
            TimeButton(time: TimeFormat.string(segment.start))
                .frame(width: 58, alignment: .trailing)
            if let idx = segment.speaker {
                Button(detail.speakerName(idx) ?? "") {
                    renameText = detail.speakerName(idx) ?? ""
                    renaming = idx
                }
                .buttonStyle(.plain)
                .font(.callout.weight(.medium))
                .foregroundStyle(Color.speaker(idx))
                .frame(width: 72, alignment: .leading)
                .lineLimit(1)
                .help("點一下幫這位說話者改名")
                .popover(isPresented: Binding(get: { renaming == idx && segment.id == firstSegmentId(of: idx) },
                                              set: { if !$0 { renaming = nil } })) {
                    RenameSpeakerForm(name: $renameText) {
                        model.renameSpeaker(idx, to: renameText)
                        renaming = nil
                    }
                }
            }
            if editingId == segment.id {
                VStack(alignment: .trailing, spacing: 6) {
                    TextField("段落內容", text: $editText, axis: .vertical)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit { saveEdit(segment) }
                    HStack {
                        Button("取消") { editingId = nil }
                            .keyboardShortcut(.cancelAction)
                        Button("儲存") { saveEdit(segment) }
                            .buttonStyle(.borderedProminent)
                    }
                    .controlSize(.small)
                }
            } else {
                Text(highlighted(segment.text))
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .onTapGesture(count: 2) { startEdit(segment) }
            }
        }
        .padding(.vertical, 6)
        .padding(.horizontal, 10)
        .background(isCurrent ? Color.celadon.opacity(0.13) : isHighlighted ? Color.signal.opacity(0.18) : .clear,
                    in: RoundedRectangle(cornerRadius: 6))
        .overlay(alignment: .leading) {
            if segment.lowConfidence {
                RoundedRectangle(cornerRadius: 1).fill(Color.signal).frame(width: 3).padding(.vertical, 6)
            }
        }
        .contextMenu {
            Button("從這裡播放") { model.player.seek(to: segment.start) }
            Button("編輯這一段") { startEdit(segment) }
            Button("拷貝") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(segment.text, forType: .string)
            }
        }
        .help(segment.lowConfidence ? "辨識信心低，建議對照錄音確認" : "")
    }

    /// 改名 popover 只掛在這位說話者的第一段，避免同時開好幾個
    private func firstSegmentId(of speaker: Int) -> Int64? {
        visibleSegments.first { $0.speaker == speaker }?.id
    }

    private func highlighted(_ text: String) -> AttributedString {
        var attributed = AttributedString(text)
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return attributed }
        var searchRange = attributed.startIndex..<attributed.endIndex
        while let range = attributed[searchRange].range(of: q, options: .caseInsensitive) {
            attributed[range].backgroundColor = Color.signal.opacity(0.35)
            searchRange = range.upperBound..<attributed.endIndex
        }
        return attributed
    }

    private func startEdit(_ segment: Segment) {
        editText = segment.text
        editingId = segment.id
    }

    private func saveEdit(_ segment: Segment) {
        let text = editText.trimmingCharacters(in: .whitespacesAndNewlines)
        if let id = segment.id, !text.isEmpty, text != segment.text { model.updateSegment(id, text: text) }
        editingId = nil
    }
}

struct RenameSpeakerForm: View {
    @Binding var name: String
    let onSave: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("說話者名字").font(.headline)
            TextField("例如：小明", text: $name)
                .textFieldStyle(.roundedBorder)
                .onSubmit(onSave)
            Text("整場逐字稿和之後的會議紀錄都會用這個名字。")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("儲存", action: onSave).keyboardShortcut(.defaultAction)
            }
        }
        .padding(14)
        .frame(width: 260)
    }
}

struct SmallDotLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 5) {
            RoundedRectangle(cornerRadius: 1).fill(Color.signal).frame(width: 3, height: 12)
            configuration.title
        }
    }
}

/// 全文取代（R4.5、design §6.4）
struct ReplaceSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let initialFind: String
    @State private var find = ""
    @State private var replacement = ""
    @State private var addToGlossary = true
    @State private var result: ReplaceResult?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("用詞修正").font(.title3.weight(.semibold))
            Text("把逐字稿裡的錯字換成正確寫法，會議紀錄裡同樣的字也會一起改。區分大小寫。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Form {
                TextField("錯誤寫法", text: $find)
                TextField("正確寫法", text: $replacement)
                Toggle("把正確寫法加進詞彙表，之後轉錄比較容易一次轉對", isOn: $addToGlossary)
            }
            .formStyle(.columns)
            if let result {
                Text(resultText(result))
                    .font(.callout)
                    .foregroundStyle(Color.celadon)
            }
            HStack {
                Spacer()
                Button(result == nil ? "取消" : "完成", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("全部取代") {
                    result = model.replace(find: find, with: replacement, addToGlossary: addToGlossary)
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
                .disabled(find.isEmpty || find == replacement)
            }
        }
        .padding(20)
        .frame(width: 440)
        .onAppear { find = initialFind }
    }

    private func resultText(_ r: ReplaceResult) -> String {
        if r.segmentsChanged == 0 && !r.notesChanged { return "沒有找到「\(find)」。" }
        var parts: [String] = []
        if r.segmentsChanged > 0 { parts.append("逐字稿改了 \(r.segmentsChanged) 段") }
        if r.notesChanged { parts.append("會議紀錄也一起改了（可以從「更多」復原上一版）") }
        return parts.joined(separator: "，") + "。"
    }
}
