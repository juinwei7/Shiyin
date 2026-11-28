import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// 匯出對話框（R7）：選項目、格式、選項，再存檔或分享
struct ExportSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var item: ExportItem = .notes
    @State private var appliedInitial = false
    @State private var format = "md"
    @State private var times = true
    @State private var speakers = true
    @State private var working = false
    /// 匯出的進度（R120、design §94）：錄音、封包才有百分比
    @State private var meter = ExportMeter()
    /// 進行中的匯出：「取消匯出」、Esc 停下來留在對話框；✕、其他方式關掉對話框時也停下來（onDisappear）
    @State private var exportTask: Task<Void, Never>?
    /// 按了「取消匯出」、還在收尾（停掉轉檔、刪掉寫到一半的檔案）
    @State private var cancelling = false
    /// 「分享…」的位置：分享選單（Mail、AirDrop、訊息；R7.5）從按鈕下面打開
    @State private var shareAnchor = MenuAnchorBox()
    /// 打開時那一場會議的資料（design §20.9）：對話框開著時選取的會議換了也不變
    @State private var bundle: ExportBundle?

    var body: some View {
        // 同其他彈窗的樣子（R106、design §80）
        VStack(alignment: .leading, spacing: 16) {
            SheetHeader(title: bundle.map { String(localized: "匯出「\($0.meeting.displayTitle)」") } ?? String(localized: "匯出"))
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 6) {
                    SheetFieldLabel(String(localized: "項目"))
                    SheetMenuField(options: ExportItem.allCases.map { ($0, $0.label) }, selection: $item) { item in
                        item == .audio && bundle?.meeting.hasAudio != true || item == .mindMap && !hasMindMap
                    }
                }
                VStack(alignment: .leading, spacing: 6) {
                    SheetFieldLabel(String(localized: "格式"))
                    SheetMenuField(options: item.formats.map { ($0, formatLabel($0)) }, selection: $format)
                        .disabled(item.formats.count == 1)
                }
            }
            .disabled(working)
            if item == .transcript {
                HStack(spacing: 18) {
                    Toggle("包含時間點", isOn: $times).disabled(format == "srt" || format == "vtt")
                    Toggle("包含說話者", isOn: $speakers)
                }
                .toggleStyle(.checkbox)
                .disabled(working)
            }
            // 匯出中：說明這一行換成進度（自己一行，在按鈕上面）。說明照樣排版、只是看不到，進度疊在上面（overlay 不佔位置），
            // 對話框不會變高、跳動
            Text(hint)
                .font(.callout)
                .foregroundStyle(item == .notes && !hasNotes ? Color.signal : .secondary)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
                .opacity(working ? 0 : 1)
                .accessibilityHidden(working)
                .overlay(alignment: .leading) { if working { progressRow } }
            HStack {
                Button("分享…") { exportTask = Task { await share() } }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .background(MenuAnchor(box: shareAnchor))
                    .disabled(working)
                Spacer()
                // 匯出中只有一個取消：停下來、刪掉寫到一半的檔案，留在對話框裡可以重來（R120.2）
                if working {
                    Button("取消匯出") { cancelExport() }
                        .buttonStyle(SheetPillButtonStyle(.secondary))
                        .keyboardShortcut(.cancelAction)
                        .disabled(cancelling)
                } else {
                    Button("取消") { dismiss() }
                        .buttonStyle(SheetPillButtonStyle(.secondary))
                        .keyboardShortcut(.cancelAction)
                }
                Button("存檔…") { exportTask = Task { await save() } }
                    .buttonStyle(SheetPillButtonStyle(.primary))
                    .keyboardShortcut(.defaultAction)
                    .disabled(working)
            }
        }
        .sheetChrome(width: 480)
        .onChange(of: item) { _, newValue in
            format = newValue.formats[0]
            #if DEBUG
            if let (debugItem, debugFormat) = Self.debugChoice, debugItem == newValue { format = debugFormat }
            #endif
        }
        // 還沒有會議紀錄（整理中或整理失敗）時，預設改匯出逐字稿（使用者回報：匯出的會議紀錄只有標題）
        .onAppear {
            bundle = model.exportMeetingId.flatMap { try? model.exportBundle(meetingId: $0) }
            if !hasNotes { item = .transcript }
            // 分享視窗的「用檔案分享…」：直接選封包（design §99.4）
            if let initial = model.exportInitialItem {
                item = initial
                model.exportInitialItem = nil
            }
            #if DEBUG
            if let (debugItem, debugFormat) = Self.debugChoice { item = debugItem; format = debugFormat }
            #endif
        }
        // 匯出中關掉對話框（✕ 或其他方式，design §94）：先取消，不在看不到的地方繼續寫
        .onDisappear { exportTask?.cancel() }
    }

    /// 進度：錄音、封包有百分比和進度條，其他項目是不定進度（通常一兩秒）
    private var progressRow: some View {
        HStack(spacing: 12) {
            Group {
                if cancelling {
                    Text("正在取消…")
                } else if let value = meter.value {
                    Text("正在匯出… \(Int((value * 100).rounded(.down)))%")
                } else {
                    Text("正在匯出…")
                }
            }
            .font(.callout)
            .foregroundStyle(.secondary)
            .monospacedDigit()
            .lineLimit(1)
            .fixedSize()
            if let value = meter.value, !cancelling {
                ProgressView(value: value).tint(.signal)
            } else {
                ProgressView().progressViewStyle(.linear)
            }
        }
        .accessibilityElement(children: .combine)
    }

    private func cancelExport() {
        cancelling = true
        meter.stop()
        exportTask?.cancel()
    }

    private var hasNotes: Bool { bundle?.notes != nil }
    /// 心智圖要有內容（design §42.4）
    private var hasMindMap: Bool { bundle?.notes.map { !$0.isEmpty } ?? false }

    private var hint: String {
        switch item {
        case .notes:
            if !hasNotes {
                return String(localized: "這場會議還沒有會議紀錄（還在整理或整理失敗），匯出的會議紀錄只會有標題和基本資料。要留下內容，請改匯出逐字稿。")
            }
            return String(localized: "PDF 和 docx 的段落標題使用宋體；Markdown 適合貼到 Notion、GitHub。")
        case .mindMap: return String(localized: "依會議紀錄畫成心智圖。PNG 適合貼到聊天軟體、簡報；SVG 可以再用其他軟體編輯。")
        case .transcript: return String(localized: "srt、vtt 是字幕格式，可以搭配錄音或影片在播放器裡顯示。")
        case .audio: return String(localized: "m4a 檔案小、適合分享；wav 是 16 kHz 單聲道，適合給其他轉錄工具使用。")
        case .package: return String(localized: "包含錄音、逐字稿、說話者、會議紀錄、筆記和重點標記。傳給同事後，對方雙擊就能匯入拾音。")
        }
    }

    private func meetingId() throws -> Int64 {
        guard let id = model.exportMeetingId else { throw AppModel.ExportError.meetingGone }
        return id
    }

    private func formatLabel(_ format: String) -> String {
        ["md": "Markdown", "pdf": "PDF", "png": String(localized: "PNG 圖片"), "svg": "SVG", "docx": String(localized: "Word（docx）"), "txt": String(localized: "純文字（txt）"), "srt": String(localized: "字幕（srt）"),
         "vtt": String(localized: "字幕（vtt）"), "m4a": "m4a", "wav": "wav", "shiyin": String(localized: ".shiyin 封包")][format] ?? format
    }

    private func save() async {
        guard let url = pickDestination() else { return }
        working = true
        let progress = meter.start()
        defer { working = false; cancelling = false; exportTask = nil }
        do {
            try await model.export(item, format: format, times: times, speakers: speakers, meetingId: try meetingId(), to: url,
                                   progress: progress)
            model.noticeMessage = String(localized: "已匯出 \(url.lastPathComponent)")
            dismiss()
        } catch is CancellationError {
            // 取消了：不是錯誤，什麼都不顯示
        } catch {
            model.errorMessage = error.localizedDescription
        }
    }

    /// 存檔面板（DEBUG 的 exportsheet 給了路徑時不開面板）
    private func pickDestination() -> URL? {
        #if DEBUG
        if let url = Self.debugSaveURL { return url }
        #endif
        let panel = NSSavePanel()
        panel.nameFieldStringValue = model.suggestedFilename(item, format: format, meeting: bundle?.meeting)
        if let type = UTType(filenameExtension: format) { panel.allowedContentTypes = [type] }
        return panel.runModal() == .OK ? panel.url : nil
    }

    private func share() async {
        guard let url = await exportToTemp(), let view = shareAnchor.view else { return }
        NSSharingServicePicker(items: [url]).show(relativeTo: view.bounds, of: view, preferredEdge: .minY)
    }

    private func exportToTemp() async -> URL? {
        guard let dir = try? Paths.makeTempDirectory() else { return nil }
        let url = dir.appendingPathComponent(model.suggestedFilename(item, format: format, meeting: bundle?.meeting))
        working = true
        let progress = meter.start()
        defer { working = false; cancelling = false; exportTask = nil }
        do {
            try await model.export(item, format: format, times: times, speakers: speakers, meetingId: try meetingId(), to: url,
                                   progress: progress)
            return url
        } catch is CancellationError {
            return nil
        } catch {
            model.errorMessage = error.localizedDescription
            return nil
        }
    }
}

/// 匯出的進度（R120、design §94）：背景回報的比例換到主執行緒，變了 1% 以上、或離上次超過 0.2 秒才更新畫面；只往前。
/// 每一次匯出有自己的編號：上一次（取消了、做完了）晚到的回報不會動到這一次的進度條
@MainActor
@Observable
final class ExportMeter {
    private(set) var value: Double?
    @ObservationIgnored private var updated = Date.distantPast
    @ObservationIgnored private var generation = 0

    /// 開始新的一次：歸零，回傳給這一次匯出用的回報（任何執行緒都可以呼叫）
    func start() -> @Sendable (Double) -> Void {
        generation += 1
        value = nil
        updated = .distantPast
        let current = generation
        return { [weak self] p in Task { @MainActor in self?.report(p, generation: current) } }
    }

    /// 取消了：之後才到的回報都不算
    func stop() {
        generation += 1
    }

    private func report(_ p: Double, generation: Int) {
        guard generation == self.generation else { return }
        let p = min(1, max(value ?? 0, p))
        guard value.map({ p - $0 >= 0.01 || Date().timeIntervalSince(updated) >= 0.2 }) ?? true else { return }
        value = p
        updated = Date()
    }
}

#if DEBUG
extension ExportSheet {
    /// DEBUG（exportsheet）：打開時選好的項目、格式；存檔不開存檔面板、直接寫到這裡（截匯出中的畫面、驗 ✕ 取消用）
    nonisolated(unsafe) static var debugChoice: (ExportItem, String)?
    nonisolated(unsafe) static var debugSaveURL: URL?
}
#endif
