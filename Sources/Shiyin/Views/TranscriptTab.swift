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
    @State private var showCorrectionGroups = false
    /// 播放中的段落。由 PlaybackFollower 在換段時才更新，播放時間每次變動不會讓整份列表重算（design §21.12）
    @State private var currentSegmentId: Int64?
    @State private var speakerFilter: Int?
    /// 正在改名的說話者和點的那一段：改名視窗掛在點的那一段（逐字稿是延遲建立的，第一段可能還不存在）
    @State private var renaming: (speaker: Int, segment: Int64)?
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


    private var pendingCorrections: Int { detail.corrections.filter { $0.status == "pending" }.count }



    var body: some View {
        #if DEBUG
        let _ = DebugMetrics.bump("transcriptBody")
        #endif
        VStack(spacing: 0) {
            PlaybackFollower(detail: detail, current: $currentSegmentId)
            searchBar
            Divider()
            if pendingCorrections > 0 && !snapshotMode {
                correctionBar
                Divider()
            }
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
                        Text(detail.speakers[idx] ?? detail.meeting.meetingLanguage.text(zh: "說話者 \(idx)", en: "Speaker \(idx)", ja: "話者 \(idx)")).tag(Int?.some(idx))
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
                // 轉錄或整理中不能改：工作完成時會整份寫回逐字稿或會議紀錄
                .disabled(detail.segments.isEmpty || model.detailStatus?.isActive == true)
                .help("把逐字稿和會議紀錄裡的同一個詞一起改掉")
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 10)
    }

    /// 有待確認的校正建議時，顯示在搜尋列下方；可以展開依詞處理（同一個詞的每一處一起接受或忽略）
    private var correctionBar: some View {
        let groups = detail.correctionGroups
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: "text.magnifyingglass").foregroundStyle(Color.signal)
                Text("\(pendingCorrections) 處可能聽錯的字，已標在逐字稿裡")
                Button(showCorrectionGroups ? "收起" : "依詞處理（\(groups.count) 組）") { showCorrectionGroups.toggle() }
                    .buttonStyle(.link)
                    .help("同一個詞的每一處一起接受或忽略")
                Spacer()
                Button("全部忽略") { model.rejectAllCorrections() }
                Button("全部接受") { model.acceptAllCorrections() }
                    .buttonStyle(.borderedProminent)
                    .help("把所有建議套用到逐字稿，之後還能逐條還原")
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 8)
            if showCorrectionGroups {
                Divider()
                ScrollView {
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(groups, id: \.first!.id) { group in correctionGroupRow(group) }
                    }
                    .padding(.horizontal, 20)
                    .padding(.vertical, 8)
                }
                .frame(maxHeight: 220)
            }
        }
        .font(.callout)
        .controlSize(.small)
        .background(Color.signal.opacity(0.08))
    }

    /// 一組「原文 → 建議」：點文字跳到第一處
    private func correctionGroupRow(_ group: [Correction]) -> some View {
        let first = group[0]
        let ids = group.compactMap(\.id)
        return HStack(alignment: .firstTextBaseline, spacing: 8) {
            Button {
                model.flashSegment(first.segmentId)
            } label: {
                Text("「\(first.original)」→「\(first.suggestion)」").fontWeight(.medium).meetingText()
            }
            .buttonStyle(.plain)
            .help("跳到第一處")
            Text("\(group.count) 處").foregroundStyle(.secondary).monospacedDigit()
            Text(first.reason).foregroundStyle(.secondary).lineLimit(1)
            Spacer(minLength: 8)
            Button("忽略") { model.rejectCorrections(ids) }
                .buttonStyle(.link)
            Button(group.count > 1 ? "接受 \(group.count) 處" : "接受") { model.acceptCorrections(ids) }
                .buttonStyle(.link)
        }
        .font(.caption)
    }

    /// 一則校正建議：待確認的有接受、忽略；已接受的可以還原
    @ViewBuilder
    private func correctionLine(_ c: Correction) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            if c.status == "accepted" {
                Text("已改成「\(c.suggestion)」（原本是「\(c.original)」）")
                    .foregroundStyle(.secondary)
                Button("還原") { if let id = c.id { model.revertCorrection(id) } }
                    .buttonStyle(.link)
            } else {
                Text("「\(c.original)」→「\(c.suggestion)」").fontWeight(.medium).meetingText()
                Text(c.reason).foregroundStyle(.secondary).lineLimit(2)
                Spacer(minLength: 8)
                Button("忽略") { if let id = c.id { model.rejectCorrection(id) } }
                    .buttonStyle(.link)
                Button("接受") { if let id = c.id { model.acceptCorrection(id) } }
                    .buttonStyle(.link)
            }
        }
        .font(.caption)
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
                    if let id = segment.id { renaming = (idx, id) }
                }
                .buttonStyle(.plain)
                .font(.callout.weight(.medium))
                .foregroundStyle(Color.speaker(idx))
                .frame(width: 72, alignment: .leading)
                .lineLimit(1)
                .help("點一下幫這位說話者改名")
                .popover(isPresented: Binding(get: { renaming?.segment == segment.id && segment.id != nil },
                                              set: { if !$0 { renaming = nil } })) {
                    RenameSpeakerForm(name: $renameText) {
                        model.renameSpeaker(idx, to: renameText)
                        renaming = nil
                    }
                }
            }
            if editingId == segment.id {
                VStack(alignment: .trailing, spacing: 6) {
                    TextField("段落內容", text: $editText, axis: .vertical).meetingText()
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
                // 每一段的校正建議（design §17.4）：待確認的標在文字上並列出，已接受的可以還原，已拒絕的不顯示
                let corrections = segment.id.flatMap { detail.correctionsBySegment[$0] } ?? []
                VStack(alignment: .leading, spacing: 4) {
                    Text(highlighted(segment.text, corrections: corrections)).meetingText()
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .onTapGesture(count: 2) { startEdit(segment) }
                    ForEach(corrections) { correctionLine($0) }
                }
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

    private func highlighted(_ text: String, corrections: [Correction] = []) -> AttributedString {
        var attributed = AttributedString(text)
        // 待確認的校正建議：原文加底線和底色
        for c in corrections where c.status == "pending" {
            if let range = attributed.range(of: c.original) {
                attributed[range].underlineStyle = .single
                attributed[range].backgroundColor = Color.signal.opacity(0.2)
            }
        }
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
        if model.detailStatus?.isActive == true {
            model.noticeMessage = String(localized: "正在處理這場會議，完成後才能編輯逐字稿")
            return
        }
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
        if r.segmentsChanged == 0 && !r.notesChanged { return String(localized: "沒有找到「\(find)」。") }
        var parts: [String] = []
        if r.segmentsChanged > 0 { parts.append(String(localized: "逐字稿改了 \(r.segmentsChanged) 段")) }
        if r.notesChanged { parts.append(String(localized: "會議紀錄也一起改了（可以從「更多」復原上一版）")) }
        return parts.joined(separator: String(localized: "，")) + String(localized: "。")
    }
}

/// 播放跟隨（design §21.12）：只有這個 view 讀播放時間（每 0.1 秒變一次），算出目前的段落，換段時才更新外面的狀態
private struct PlaybackFollower: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @Binding var current: Int64?

    var body: some View {
        let time = model.player.currentTime
        let id = model.player.isPlaying || time > 0 ? detail.segmentId(at: time) : nil
        Color.clear.frame(width: 0, height: 0)
            .onChange(of: id, initial: true) { _, new in
                if current != new { current = new }
            }
    }
}
