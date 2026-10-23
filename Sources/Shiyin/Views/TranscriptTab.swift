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
    @State private var draftTask: Task<Void, Never>?
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
            if model.canUpgradeTranscript(detail.meeting) && model.detailStatus?.isActive != true {
                upgradeBar
                Divider()
            }
            if detail.showsSpeakerHint && model.detailStatus?.isActive != true {
                speakerHintBar
                Divider()
            }
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
                    // 單段編輯的草稿（design §21.15、R20.6）：換分頁、換會議、結束拾音都還在
                    .onAppear(perform: restoreEditDraft)
                    .onChange(of: editText) { _, _ in scheduleEditDraft() }
                    .onDisappear {
                        draftTask?.cancel()
                        saveEditDraft()
                    }
                    .onReceive(NotificationCenter.default.publisher(for: .flushDrafts)) { _ in
                        draftTask?.cancel()
                        saveEditDraft()
                    }
                    #if DEBUG
                    .onChange(of: model.debugEditSegment) { _, id in
                        if let segment = detail.segments.first(where: { $0.id == id }) { startEdit(segment) }
                        model.debugEditSegment = nil
                    }
                    #endif
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
        // 合併、改說話者後篩選的那位不見了：回到所有說話者
        .onChange(of: detail.speakers.keys.sorted()) { _, keys in
            if let speakerFilter, !keys.contains(speakerFilter) { self.speakerFilter = nil }
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

    /// 模型下載好之前用 macOS 內建辨識轉錄的會議（design §21.17）
    private var upgradeBar: some View {
        HStack(spacing: 10) {
            Image(systemName: "waveform.badge.magnifyingglass").foregroundStyle(Color.signal)
            Text(detail.meeting.meetingLanguage == .ja
                 ? "這場會議是在模型下載好之前，用 macOS 內建辨識轉錄的。可以用日文轉錄模型重新轉錄，通常比較準。"
                 : "這場會議是在模型下載好之前，用 macOS 內建辨識轉錄的。可以用 Breeze 重新轉錄，通常比較準。")
            // 不能加 fixedSize：在 HStack 裡量最小高度時會用很窄的寬度，算出很高的高度，整個視窗的內容被往上推
            Spacer()
            Button("重新轉錄") { model.retranscribe() }
                .buttonStyle(.borderedProminent)
                .help("逐字稿裡改過的文字會換成新的逐字稿，說話者的名字會留著")
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 8)
        .font(.callout)
        .controlSize(.small)
        .background(Color.signal.opacity(0.08))
    }

    /// 說話者可能分得不夠（design §24.5）：說話者辨識的自我檢查覺得可能把不同的人併在一起
    private var speakerHintBar: some View {
        HStack(spacing: 10) {
            Image(systemName: "person.2.badge.gearshape").foregroundStyle(Color.signal)
            Text("說話者可能分得不夠：有些不同的人可能被當成同一位。人多、吵雜或手機放在桌上錄音時常發生，告訴拾音大約幾個人會分得比較準。")
            // 不能加 fixedSize：理由同 upgradeBar
            Spacer()
            Button("不用了") { model.dismissSpeakerHint() }
                .help("這場會議不再提示")
            Button("重新辨識說話者…") { model.requestRediarize() }
                .buttonStyle(.borderedProminent)
                .help("只重新分辨誰在說話，逐字稿的文字不會變")
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 8)
        .font(.callout)
        .controlSize(.small)
        .background(Color.signal.opacity(0.08))
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
                // 組數少時照內容的高度，超過 220 才捲動（ScrollView 的理想高度是內容的高度，最多 220）
                ScrollView {
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(groups, id: \.first!.id) { group in correctionGroupRow(group) }
                    }
                    .padding(.horizontal, 20)
                    .padding(.vertical, 8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 220)
                .fixedSize(horizontal: false, vertical: true)
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
            TimeButton(time: TimeFormat.string(segment.start), seek: segment.start)
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
                    RenameSpeakerForm(name: $renameText, automatic: detail.speakerSources[idx] == "voiceprint",
                                      onReject: { model.rejectVoiceprintMatch(idx); renaming = nil },
                                      mergeTargets: otherSpeakers(than: idx),
                                      onMerge: { into in
                                          renaming = nil
                                          model.mergeSpeaker(idx, into: into)
                                      }) {
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
                        Button("取消") { cancelEdit() }
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
            // 改這句的說話者（design §24.3、R23.2）
            if let id = segment.id, segment.kind == "final", !detail.speakers.isEmpty {
                Divider()
                Menu("這句是誰說的") { speakerChoices(for: [id], current: segment.speaker) }
                let run = detail.speakerRun(around: id)
                if run.count > 1 {
                    Menu("這段連續的話是誰說的（\(run.count) 句）") { speakerChoices(for: run, current: segment.speaker) }
                }
            }
        }
        .help(segment.lowConfidence ? "辨識信心低，建議對照錄音確認" : "")
    }

    /// 除了 idx 以外的說話者（合併、改說話者的選單）
    private func otherSpeakers(than idx: Int?) -> [(idx: Int, name: String)] {
        detail.speakers.keys.sorted().filter { $0 != idx }.map { ($0, detail.speakerName($0) ?? "") }
    }

    @ViewBuilder
    private func speakerChoices(for ids: [Int64], current: Int?) -> some View {
        ForEach(otherSpeakers(than: current), id: \.idx) { speaker in
            Button(speaker.name) { model.reassignSegments(ids, to: speaker.idx) }
        }
        Divider()
        Button("新的說話者") { model.reassignSegments(ids, to: nil) }
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
        // 正在改別段的話，那一段的草稿不留（跟按取消一樣）
        if editingId != nil && editingId != segment.id { cancelEdit() }
        editText = segment.text
        editingId = segment.id
    }

    private func saveEdit(_ segment: Segment) {
        let text = editText.trimmingCharacters(in: .whitespacesAndNewlines)
        if let id = segment.id, !text.isEmpty, text != segment.text { model.updateSegment(id, text: text) }
        clearEditDraft()
        editingId = nil
    }

    private func cancelEdit() {
        clearEditDraft()
        editingId = nil
    }

    private func clearEditDraft() {
        draftTask?.cancel()
        model.deleteDrafts(detail.meeting.id!, prefix: "segment:")
    }

    /// 打字停 0.8 秒存一次
    private func scheduleEditDraft() {
        guard editingId != nil else { return }
        draftTask?.cancel()
        draftTask = Task {
            try? await Task.sleep(for: .milliseconds(800))
            guard !Task.isCancelled else { return }
            saveEditDraft()
        }
    }

    /// 跟原本一樣（剛開始編輯、改回原樣）就不留草稿
    private func saveEditDraft() {
        guard let editingId else { return }
        let original = detail.segments.first { $0.id == editingId }?.text
        model.saveDraft(detail.meeting.id!, key: "segment:\(editingId)", text: editText == original ? "" : editText)
    }

    /// 上次沒存的編輯：回到編輯狀態，內容是草稿
    private func restoreEditDraft() {
        guard editingId == nil, let draft = model.latestDraft(detail.meeting.id!, prefix: "segment:"),
              let id = Int64(draft.key.dropFirst("segment:".count)),
              detail.segments.contains(where: { $0.id == id }) else { return }
        editText = draft.text
        editingId = id
        model.flashSegment(id)
    }
}

struct RenameSpeakerForm: View {
    @Binding var name: String
    /// 名字是聲紋自動帶入的：多一個「不是他」（design §21.4）
    var automatic = false
    var onReject: (() -> Void)? = nil
    /// 「合併到…」（design §24.2）：其他說話者
    var mergeTargets: [(idx: Int, name: String)] = []
    var onMerge: ((Int) -> Void)? = nil
    let onSave: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("說話者名字").font(.headline)
            TextField("例如：小明", text: $name)
                .textFieldStyle(.roundedBorder)
                .onSubmit(onSave)
            if automatic {
                Text("這個名字是拾音認出聲音後自動帶入的。不對的話直接改名，或按「不是他」。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Text("整場逐字稿和之後的會議紀錄都會用這個名字；拾音也會記住這個人的聲音，之後的會議自動帶入。")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                if automatic, let onReject { Button("不是他", action: onReject) }
                if !mergeTargets.isEmpty, let onMerge {
                    Menu("合併到…") {
                        ForEach(mergeTargets, id: \.idx) { target in
                            Button(target.name) { onMerge(target.idx) }
                        }
                    }
                    .fixedSize()
                    .help("同一個人被分成兩位時，把這位說的話全部算到另一位")
                }
                Spacer()
                Button("儲存", action: onSave).keyboardShortcut(.defaultAction)
            }
        }
        .padding(14)
        .frame(width: 280)
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
