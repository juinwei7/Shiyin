import SwiftUI

/// 逐字稿分頁（R4）：時間點跳播、播放時高亮跟隨、搜尋、單段編輯、全文取代
struct TranscriptTab: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.snapshotMode) private var snapshotMode
    @Environment(\.textScale) private var scale
    let detail: MeetingDetail

    @State private var query = ""
    @State private var matchCursor = 0
    /// 按一次上一個、下一個加一（design §85）：只有一段符合時游標不變，捲動要看這個
    @State private var matchJump = 0
    /// 目前跳到的那一段（R111.3）：游標換了才設，每一列不用重算 matches
    @State private var currentMatch: Int64?
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
    /// 拆成兩句（design §46.2）：正在拆的那一句、游標的位置（UTF-16）、後半句的說話者（nil 是新的說話者）
    @State private var splittingId: Int64?
    @State private var splitOffset: Int?
    @State private var splitTarget: Int?
    @FocusState private var searchFocused: Bool
    /// 原文／精簡（R96、design §70）
    @AppStorage(AppSettings.transcriptViewKey) private var transcriptView = "original"
    private var cleanMode: Bool { transcriptView == "clean" }

    init(detail: MeetingDetail, speakerFilter: Int? = nil) {
        self.detail = detail
        _speakerFilter = State(initialValue: speakerFilter)
    }

    private var visibleSegments: [Segment] {
        // 精簡：整句都是贅字的那句不顯示（R96.1）
        let segments = cleanMode && !detail.cleanLines.isEmpty
            ? detail.segments.filter { segment in segment.id.map { detail.cleanLines[$0] != "" } ?? true } : detail.segments
        guard let speakerFilter else { return segments }
        return segments.filter { $0.speaker == speakerFilter }
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
            // 我的筆記收在逐字稿頂部（R44.3，design §33.1）
            UserNotesDisclosure(detail: detail)
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
                // 還沒有逐字稿時，錄音中截的畫面照樣看得到（design §83.4）
                if !detail.screens.isEmpty {
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(detail.screens) { ScreenRow(screen: $0, highlighted: $0.id == model.highlightScreen) }
                        }
                        .contentFont()
                        .frame(maxWidth: 760 * scale, alignment: .leading)
                        .padding(.horizontal, 20)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                Spacer()
            } else if cleanMode && detail.cleanLines.isEmpty {
                cleanPlaceholder
                Spacer()
            } else if snapshotMode {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(visibleSegments.prefix(18)) { segment in row(segment) }
                }
                .contentFont()   // 文字大小（design §38.5）
                .frame(maxWidth: 760 * scale, alignment: .leading)
                .padding(.horizontal, 20)
                .padding(.vertical, 14)
                .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                let segments = visibleSegments
                // 會議中的畫面夾在逐字稿裡（design §83.4）
                let placement = ScreenPlacement.place(detail.screens, starts: segments.map(\.start))
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(Array(segments.enumerated()), id: \.element.id) { index, segment in
                                if let screens = placement.before[index] {
                                    ForEach(screens) { screenRow($0) }
                                }
                                row(segment)
                                    .id(segment.id)
                            }
                            ForEach(placement.trailing) { screenRow($0) }
                        }
                        .contentFont()   // 文字大小（design §38.5）
                        .frame(maxWidth: 760 * scale, alignment: .leading)
                        .padding(.horizontal, 20)
                        .padding(.vertical, 14)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .onChange(of: currentSegmentId) { _, id in
                        guard let id, model.player.isPlaying, editingId == nil else { return }
                        if reduceMotion { proxy.scrollTo(id, anchor: .center) }
                        else { withAnimation(.easeInOut(duration: 0.25)) { proxy.scrollTo(id, anchor: .center) } }
                    }
                    .onChange(of: matchJump) { _, _ in
                        if let currentMatch { proxy.scrollTo(currentMatch, anchor: .center) }
                    }
                    .onChange(of: query) { _, _ in
                        matchCursor = 0
                        currentMatch = matches.first
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
                    .onChange(of: model.splitRequest, initial: true) { _, id in
                        guard let id else { return }
                        model.splitRequest = nil
                        startSplit(id)
                        Task { @MainActor in
                            try? await Task.sleep(for: .milliseconds(150))
                            proxy.scrollTo(id, anchor: .center)
                        }
                    }
                    .onChange(of: model.highlightScreen, initial: true) { _, id in
                        guard let id else { return }
                        Task { @MainActor in
                            try? await Task.sleep(for: .milliseconds(150))
                            proxy.scrollTo("screen-\(id)", anchor: .center)
                        }
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
                    // ⇧Return 是上一個（R111.2）：onSubmit 拿不到修飾鍵，看送來的這個事件
                    .onSubmit { step(NSApp.currentEvent?.modifierFlags.contains(.shift) == true ? -1 : 1) }
                if !query.isEmpty {
                    Text(matches.isEmpty ? "找不到" : "\(min(matchCursor + 1, matches.count))／\(matches.count) 段")
                        .font(.caption)
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                    // 上一個、下一個（R111.1）
                    HStack(spacing: 2) {
                        Button { step(-1) } label: { Image(systemName: "chevron.up").font(.system(size: 11, weight: .semibold)).frame(width: 18, height: 16) }
                            .help("上一個（⇧Return）")
                            .accessibilityLabel("上一個")
                        Button { step(1) } label: { Image(systemName: "chevron.down").font(.system(size: 11, weight: .semibold)).frame(width: 18, height: 16) }
                            .help("下一個（Return）")
                            .accessibilityLabel("下一個")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .disabled(matches.isEmpty)
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
            // ⌘G、⇧⌘G（R111.2）：沒有符合的段落時停用，不吃掉快捷鍵
            Button("下一個") { step(1) }
                .keyboardShortcut("g")
                .disabled(matches.isEmpty)
                .hidden()
                .frame(width: 0)
            Button("上一個") { step(-1) }
                .keyboardShortcut("g", modifiers: [.command, .shift])
                .disabled(matches.isEmpty)
                .hidden()
                .frame(width: 0)
            if !detail.speakers.isEmpty {
                Picker("說話者", selection: $speakerFilter) {
                    Text("所有說話者").tag(Int?.none)
                    ForEach(detail.speakers.keys.sorted(), id: \.self) { idx in
                        Text(detail.speakers[idx] ?? detail.meeting.meetingLanguage.defaultSpeaker(idx)).tag(Int?.some(idx))
                    }
                }
                .labelsHidden()
                .frame(width: 150)
                .help("只看某一位說話者的內容")
            }
            // 原文／精簡（R96）
            Picker("逐字稿", selection: $transcriptView) {
                Text("原文").tag("original")
                Text("精簡").tag("clean")
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            .disabled(detail.segments.isEmpty)
            .help("精簡：用整理工具去掉贅字、重複和改口的版本；播放、搜尋、匯出照舊用原文")
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

    /// 還沒有精簡版（R96.2）：說明和「產生精簡版」；產生中顯示進度；上次失敗寫原因
    @ViewBuilder private var cleanPlaceholder: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let progress = model.cleaningProgress(detail.meeting.id!) {
                HStack(spacing: 10) {
                    ProgressView().controlSize(.small)
                    Text(progress.map { String(localized: "正在產生精簡版 \(Int($0 * 100))%") } ?? String(localized: "正在產生精簡版…"))
                }
            } else {
                Text("精簡版用整理工具去掉「嗯、呃、那個」這類贅字、重複的字詞和說到一半改口的部分，不改意思、不摘要。每一句的時間和說話者不變。")
                    .foregroundStyle(.secondary)
                if let failure = detail.cleanFailure {
                    Text(failure.isEmpty ? String(localized: "上次產生精簡版失敗。") : String(localized: "上次產生精簡版失敗：\(failure)"))
                        .foregroundStyle(Color.signal)
                }
                Button(detail.cleanFailure == nil ? "產生精簡版" : "再試一次", systemImage: "text.badge.minus") { model.generateCleanTranscript() }
                    .disabled(model.detailStatus?.isActive == true)
                    .help("會用整理工具的額度；長的會議要幾分鐘")
            }
        }
        .contentFont()
        .padding(32)
        .frame(maxWidth: 760 * scale, alignment: .leading)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// 重新轉錄會用的模型（design §41.3）
    private var upgradeText: String {
        switch ModelStore.transcription(for: detail.meeting.meetingLanguage) {
        case .japanese:
            return String(localized: "這場會議是在模型下載好之前，用 macOS 內建辨識轉錄的。可以用\(MeetingLanguage.uiNames(model: "turbo"))轉錄模型重新轉錄，通常比較準。")
        case .qwen: return String(localized: "這場會議是在模型下載好之前，用 macOS 內建辨識轉錄的。可以用 Qwen3-ASR 重新轉錄，通常比較準。")
        default: return String(localized: "這場會議是在模型下載好之前，用 macOS 內建辨識轉錄的。可以用 Breeze 重新轉錄，通常比較準。")
        }
    }

    /// 模型下載好之前用 macOS 內建辨識轉錄的會議（design §21.17）
    private var upgradeBar: some View {
        HStack(spacing: 10) {
            Image(systemName: "waveform.badge.magnifyingglass").foregroundStyle(Color.signal)
            Text(upgradeText)
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
        .contentFont(.caption)
    }

    private func screenRow(_ screen: MeetingDocument) -> some View {
        ScreenRow(screen: screen, highlighted: screen.id == model.highlightScreen)
            .id("screen-\(screen.id ?? 0)")
    }

    @ViewBuilder
    private func row(_ segment: Segment) -> some View {
        let isCurrent = segment.id == currentSegmentId
        let isHighlighted = segment.id == model.highlightSegment
        HStack(alignment: .firstTextBaseline, spacing: 14) {
            TimeButton(time: TimeFormat.string(segment.start), seek: segment.start)
                .frame(width: 58 * scale, alignment: .trailing)
            if let idx = segment.speaker {
                Button {
                    renameText = detail.speakerName(idx) ?? ""
                    if let id = segment.id { renaming = (idx, id) }
                } label: {
                    // 頭像加名字（design §58.2、R83.2）
                    HStack(spacing: 6) {
                        PersonAvatar(name: detail.speakerName(idx) ?? "", color: Color.speaker(idx), isMe: idx == detail.selfIdx)
                            .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 * scale }
                        Text(detail.speakerName(idx) ?? "")
                    }
                }
                .buttonStyle(.plain)
                .contentFont(.callout, weight: .medium)
                .foregroundStyle(Color.speaker(idx))
                .frame(width: 96 * scale, alignment: .leading)
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
                                      },
                                      undoLabel: detail.speakerUndoLabel,
                                      onUndo: { renaming = nil; model.undoSpeakerEdit() }) {
                        model.renameSpeaker(idx, to: renameText)
                        renaming = nil
                    }
                }
            }
            if splittingId == segment.id {
                splitEditor(segment)
            } else if editingId == segment.id {
                VStack(alignment: .trailing, spacing: 6) {
                    TextField("段落內容", text: $editText, axis: .vertical).meetingText()
                        .textFieldStyle(.sheet)
                        .onSubmit { saveEdit(segment) }
                    HStack(spacing: 8) {
                        Button("取消") { cancelEdit() }
                            .buttonStyle(SheetPillButtonStyle(.secondary, compact: true))
                            .keyboardShortcut(.cancelAction)
                        Button("儲存") { saveEdit(segment) }
                            .buttonStyle(SheetPillButtonStyle(.primary, compact: true))
                    }
                }
            } else {
                // 每一段的校正建議（design §17.4）：待確認的標在文字上並列出，已接受的可以還原，已拒絕的不顯示
                // 精簡（R96）：顯示精簡版的文字，不標校正建議（校正對的是原文）；改過的句子沒有精簡版，照原文
                let clean = cleanMode ? segment.id.flatMap { detail.cleanLines[$0] } : nil
                let corrections = clean == nil ? (segment.id.flatMap { detail.correctionsBySegment[$0] } ?? []) : []
                VStack(alignment: .leading, spacing: 4) {
                    Text(clean.map { AttributedString($0) } ?? highlighted(segment.text, corrections: corrections,
                                                                         current: segment.id != nil && segment.id == currentMatch)).meetingText()
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .onDoubleClick { startEdit(segment) }
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
        // 自己畫的選單（R105.4、design §79）：可以選取的文字上按右鍵原本跳出系統的文字選單，空白處沒有選單（GitHub #19）；
        // app 層級的事件監看比文字先收到右鍵，這一句的任何地方都是同一份
        .floatingContextMenu { segmentMenu(segment) }
        .help(segment.lowConfidence ? "辨識信心低，建議對照錄音確認" : "")
    }

    // MARK: 拆成兩句（R70、design §46.2）

    private func startSplit(_ id: Int64) {
        guard let index = detail.segments.firstIndex(where: { $0.id == id }) else { return }
        if model.detailStatus?.isActive == true {
            model.noticeMessage = String(localized: "正在處理這場會議，完成後才能改說話者")
            return
        }
        if editingId != nil { cancelEdit() }
        // 預設：下一句的說話者；下一句是同一位時用上一句的；都是同一位時新的說話者
        let current = detail.segments[index].speaker
        let next = detail.segments.dropFirst(index + 1).first?.speaker
        let previous = index > 0 ? detail.segments[index - 1].speaker : nil
        splitTarget = [next, previous].compactMap { $0 }.first { $0 != current }
        splitOffset = nil
        splittingId = id
    }

    /// 前半句、後半句（去掉頭尾的空白）；任一邊沒有字是 nil（R70.2，同 `AppDatabase.splitSegment`）
    private func splitParts(_ text: String) -> (head: String, tail: String)? {
        guard let offset = splitOffset else { return nil }
        let ns = text as NSString
        guard offset > 0, offset < ns.length else { return nil }
        let blanks = CharacterSet(charactersIn: " \t\n\r\u{3000}")
        let head = ns.substring(to: offset).trimmingCharacters(in: blanks)
        let tail = ns.substring(from: offset).trimmingCharacters(in: blanks)
        return head.isEmpty || tail.isEmpty ? nil : (head, tail)
    }

    @ViewBuilder
    private func splitEditor(_ segment: Segment) -> some View {
        let parts = splitParts(segment.text)
        VStack(alignment: .leading, spacing: 6) {
            Text("在文字裡點一下要拆開的地方").font(.caption).foregroundStyle(.secondary)
            SplitTextView(text: segment.text, scale: scale, offset: $splitOffset)
                .frame(minHeight: 40)
                .sheetFieldBox(focused: true)
            if let parts {
                Text("前半句：…\(String(parts.head.suffix(8)))｜後半句：\(String(parts.tail.prefix(8)))…")
                    .font(.caption).foregroundStyle(.secondary).meetingText()
            }
            HStack {
                Picker("後半句是", selection: $splitTarget) {
                    ForEach(detail.speakers.keys.sorted(), id: \.self) { idx in
                        let name = detail.speakerName(idx) ?? ""
                        Text(idx == segment.speaker ? String(localized: "\(name)（同一位）") : name).tag(Optional(idx))
                    }
                    Divider()
                    Text("新的說話者").tag(Int?.none)
                }
                .fixedSize()
                .controlSize(.small)
                Spacer()
                Button("取消") { splittingId = nil }
                    .buttonStyle(SheetPillButtonStyle(.secondary, compact: true))
                    .keyboardShortcut(.cancelAction)
                Button("拆開") {
                    guard let id = segment.id, let offset = splitOffset else { return }
                    splittingId = nil
                    model.splitSegment(id, at: offset, to: splitTarget)
                }
                .buttonStyle(SheetPillButtonStyle(.primary, compact: true))
                .disabled(parts == nil)
            }
        }
    }

    /// 除了 idx 以外的說話者（合併、改說話者的選單）
    private func otherSpeakers(than idx: Int?) -> [(idx: Int, name: String)] {
        detail.speakers.keys.sorted().filter { $0 != idx }.map { ($0, detail.speakerName($0) ?? "") }
    }

    @ViewBuilder
    /// 一句的右鍵選單（R105.4）：播放、編輯、拷貝；正式逐字稿再加拆句、改說話者、分成兩場、刪除、復原
    private func segmentMenu(_ segment: Segment) -> [MenuEntry] {
        var entries: [MenuEntry] = [
            .action(String(localized: "從這裡播放"), icon: "play") { model.player.seek(to: segment.start) },
            .action(String(localized: "編輯這一段"), icon: "pencil") { startEdit(segment) },
            .action(String(localized: "拷貝"), icon: "doc.on.doc") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(segment.text, forType: .string)
            },
        ]
        guard let id = segment.id, segment.kind == "final" else { return entries }
        entries.append(.divider)
        // 改這句的說話者（design §24.3、R23.2）
        if !detail.speakers.isEmpty {
            entries.append(.action(String(localized: "拆成兩句…"), icon: "arrow.triangle.branch") { startSplit(id) })   // R70
            entries.append(.submenu(String(localized: "這句是誰說的"), icon: "person", speakerChoices(for: [id], current: segment.speaker)))
            let run = detail.speakerRun(around: id)
            if run.count > 1 {
                entries.append(.submenu(String(localized: "這段連續的話是誰說的（\(run.count) 句）"), icon: "person.2",
                                        speakerChoices(for: run, current: segment.speaker)))
            }
        }
        // 分割會議（R90、design §64）：第一句之前沒有東西，不能從第一句分
        if detail.segments.first(where: { $0.kind == "final" })?.id != id {
            entries.append(.action(String(localized: "從這裡分成兩場…"), icon: "rectangle.split.2x1") { model.splitMeetingRequest = id })
        }
        entries.append(.action(String(localized: "刪除這句"), icon: "trash", destructive: true) { model.deleteSegment(id) })   // R89
        // 復原最近一次說話者的調整、刪除（R44.4、R89）：有東西可復原才出現
        if let label = detail.speakerUndoLabel {
            entries.append(.action(String(localized: "復原：\(label)"), icon: "arrow.uturn.backward") { model.undoSpeakerEdit() })
        }
        return entries
    }

    private func speakerChoices(for ids: [Int64], current: Int?) -> [MenuEntry] {
        otherSpeakers(than: current).map { speaker in
            MenuEntry.action(speaker.name) { model.reassignSegments(ids, to: speaker.idx) }
        } + [.divider, .action(String(localized: "新的說話者")) { model.reassignSegments(ids, to: nil) }]
    }

    /// current：目前跳到的那一段，搜尋到的字底色深一點（R111.3）
    private func highlighted(_ text: String, corrections: [Correction] = [], current: Bool = false) -> AttributedString {
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
            attributed[range].backgroundColor = Color.signal.opacity(current ? 0.7 : 0.35)
            searchRange = range.upperBound..<attributed.endIndex
        }
        return attributed
    }

    /// 跳到上一個（-1）、下一個（1）符合的段落，到頭繞回（R111.1）
    private func step(_ delta: Int) {
        let found = matches
        guard !found.isEmpty else { return }
        matchCursor = ((matchCursor + delta) % found.count + found.count) % found.count
        currentMatch = found[matchCursor]
        matchJump += 1
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
    /// 可以復原的說話者調整（R44.4）：有才顯示「復原」
    var undoLabel: String? = nil
    var onUndo: (() -> Void)? = nil
    let onSave: () -> Void

    var body: some View {
        // 同彈窗的元件（R106、design §80），小視窗裡小一號
        VStack(alignment: .leading, spacing: 12) {
            Text("說話者名字").font(.title3).fontWeight(.bold)
            SheetTextField(placeholder: String(localized: "例如：小明"), text: $name, focusOnAppear: true, onSubmit: onSave)
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
            HStack(spacing: 8) {
                if automatic, let onReject {
                    Button("不是他", action: onReject).buttonStyle(SheetPillButtonStyle(.secondary))
                }
                if !mergeTargets.isEmpty, let onMerge {
                    Menu {
                        ForEach(mergeTargets, id: \.idx) { target in
                            Button(target.name) { onMerge(target.idx) }
                        }
                    } label: {
                        Text("合併到…")
                    }
                    .menuStyle(.button)
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .menuIndicator(.hidden)
                    .fixedSize()
                    .help("同一個人被分成兩位時，把這位說的話全部算到另一位")
                }
                Spacer()
                Button("儲存", action: onSave)
                    .buttonStyle(SheetPillButtonStyle(.primary))
                    .keyboardShortcut(.defaultAction)
            }
            if let undoLabel, let onUndo {
                Button("復原：\(undoLabel)", systemImage: "arrow.uturn.backward", action: onUndo)
                    .buttonStyle(.borderless)
                    .font(.caption)
                    .help("復原最近一次合併或改句子的說話者")
            }
        }
        .padding(18)
        .frame(width: 320)
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
        // 同其他彈窗的樣子（R106、design §80）
        VStack(alignment: .leading, spacing: 16) {
            SheetHeader(title: String(localized: "用詞修正"),
                        subtitle: String(localized: "把逐字稿裡的錯字換成正確寫法，會議紀錄裡同樣的字也會一起改。區分大小寫。"))
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 6) {
                    SheetFieldLabel(String(localized: "錯誤寫法"))
                    SheetTextField(placeholder: String(localized: "錯誤寫法"), text: $find, focusOnAppear: true)
                }
                VStack(alignment: .leading, spacing: 6) {
                    SheetFieldLabel(String(localized: "正確寫法"))
                    SheetTextField(placeholder: String(localized: "正確寫法"), text: $replacement)
                }
            }
            Toggle("把正確寫法加進詞彙表，之後轉錄比較容易一次轉對", isOn: $addToGlossary)
                .toggleStyle(.checkbox)
            if let result {
                Text(resultText(result))
                    .font(.callout)
                    .foregroundStyle(Color.celadon)
            }
            HStack(spacing: 10) {
                Spacer()
                Button(result == nil ? "取消" : "完成") { dismiss() }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                Button("全部取代") {
                    result = model.replace(find: find, with: replacement, addToGlossary: addToGlossary)
                }
                .buttonStyle(SheetPillButtonStyle(.primary))
                .keyboardShortcut(.defaultAction)
                .disabled(find.isEmpty || find == replacement)
            }
        }
        .sheetChrome(width: 480)
        .onAppear { find = initialFind }
    }

    private func resultText(_ r: ReplaceResult) -> String {
        if r.segmentsChanged == 0 && !r.notesChanged { return String(localized: "沒有找到「\(find)」。") }
        var parts: [String] = []
        if r.segmentsChanged > 0 { parts.append(String(localized: "逐字稿改了 \(r.segmentsChanged) 段")) }
        if r.notesChanged { parts.append(String(localized: "會議紀錄也一起改了（按「編輯」旁的「復原上一版」可以還原）")) }
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

/// 拆成兩句時放游標的文字（design §46.2）：看得到游標、選得到位置，但不能改字。位置用 UTF-16（NSString 的單位）
private struct SplitTextView: NSViewRepresentable {
    let text: String
    let scale: CGFloat
    @Binding var offset: Int?

    func makeNSView(context: Context) -> NSTextView {
        let view = NSTextView()
        view.delegate = context.coordinator
        view.isRichText = false
        view.drawsBackground = false
        view.textContainerInset = NSSize(width: 2, height: 2)
        view.setAccessibilityLabel(String(localized: "要拆開的句子"))
        view.string = text
        view.font = .systemFont(ofSize: NSFont.systemFontSize * scale)
        view.textContainer?.widthTracksTextView = true
        DispatchQueue.main.async { view.window?.makeFirstResponder(view) }
        return view
    }

    func updateNSView(_ view: NSTextView, context: Context) {
        context.coordinator.parent = self
        if view.string != text { view.string = text }
        view.font = .systemFont(ofSize: NSFont.systemFontSize * scale)
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView view: NSTextView, context: Context) -> CGSize? {
        guard let width = proposal.width, let container = view.textContainer, let layout = view.layoutManager else { return nil }
        container.containerSize = NSSize(width: max(width - 4, 10), height: .greatestFiniteMagnitude)
        layout.ensureLayout(for: container)
        return CGSize(width: width, height: ceil(layout.usedRect(for: container).height) + 4)
    }

    func makeCoordinator() -> Coordinator { Coordinator(parent: self) }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: SplitTextView
        init(parent: SplitTextView) { self.parent = parent }

        /// 不能改字（打字、貼上、刪除都擋掉），游標照樣看得到
        func textView(_ textView: NSTextView, shouldChangeTextIn range: NSRange, replacementString: String?) -> Bool { false }

        func textViewDidChangeSelection(_ notification: Notification) {
            guard let view = notification.object as? NSTextView else { return }
            let location = view.selectedRange().location
            parent.offset = location == NSNotFound ? nil : location
        }
    }
}

// MARK: 雙擊（可以選取的文字上也收得到）

extension View {
    /// 雙擊做某件事：可以選取的文字自己處理雙擊（選一個詞），SwiftUI 的 onTapGesture(count: 2) 收不到（逐字稿雙擊進編輯，design §79）。
    /// 蓋一層不接事件的透明 view，app 層級的事件監看收到第二下時交給游標下面的那一個、吃掉這一下；第一下、拖曳選字照舊給文字
    func onDoubleClick(_ action: @escaping () -> Void) -> some View {
        overlay(DoubleClickCatcher(action: action))
    }
}

private struct DoubleClickCatcher: NSViewRepresentable {
    let action: () -> Void

    func makeNSView(context: Context) -> CatcherView { CatcherView() }
    func updateNSView(_ view: CatcherView, context: Context) { view.action = action }

    final class CatcherView: NSView {
        var action: (() -> Void)?

        override func hitTest(_ point: NSPoint) -> NSView? { nil }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if window != nil { DoubleClickRegistry.shared.add(self) }
        }
    }
}

@MainActor
private final class DoubleClickRegistry {
    static let shared = DoubleClickRegistry()
    private let views = NSHashTable<DoubleClickCatcher.CatcherView>.weakObjects()
    private var monitor: Any?

    func add(_ view: DoubleClickCatcher.CatcherView) {
        views.add(view)
        guard monitor == nil else { return }
        // 只接沒按修飾鍵的第二下（⌘、⇧ 點是多選、延伸選取）
        monitor = NSEvent.addLocalMonitorForEvents(matching: .leftMouseDown) { [weak self] event in
            guard event.clickCount == 2, event.modifierFlags.intersection([.command, .shift, .option, .control]).isEmpty,
                  let self, let window = event.window else { return event }
            let hit = self.views.allObjects.first { view in
                view.window === window && !view.isHiddenOrHasHiddenAncestor
                    && view.bounds.contains(view.convert(event.locationInWindow, from: nil))
            }
            guard let action = hit?.action else { return event }
            action()
            return nil
        }
    }
}
