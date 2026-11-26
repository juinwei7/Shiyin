import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// 匯出對話框（R7）：選項目、格式、選項，再存檔或分享
struct ExportSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var item: ExportItem = .notes
    @State private var format = "md"
    @State private var times = true
    @State private var speakers = true
    @State private var working = false
    /// 匯出的進度（R120、design §94）：錄音、封包才有百分比；進行中的匯出，按「取消」就停下來
    @State private var meter = ExportMeter()
    @State private var exportTask: Task<Void, Never>?
    /// 匯出錄音時麥克風只保留建議收錄（R119.6、design §93.6），記住上次的選擇
    @AppStorage(AppSettings.exportMicRegionsOnlyKey) private var micRegionsOnly = false
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
            // 只有「麥克風＋電腦聲音」的會議；還沒有區塊時停用並寫原因
            if item == .audio, let meeting = bundle?.meeting, meeting.channels == "both" {
                let noRegions = bundle?.micRegions.isEmpty ?? true
                Toggle("麥克風只保留建議收錄", isOn: $micRegionsOnly)
                    .toggleStyle(.checkbox)
                    .disabled(noRegions)
                    .help(noRegions ? "這場還沒有建議收錄的區塊" : "其餘的麥克風會是沒有聲音，電腦聲音全部保留")
            }
            if item == .transcript {
                HStack(spacing: 18) {
                    Toggle("包含時間點", isOn: $times).disabled(format == "srt" || format == "vtt")
                    Toggle("包含說話者", isOn: $speakers)
                }
                .toggleStyle(.checkbox)
            }
            Text(hint)
                .font(.callout)
                .foregroundStyle(item == .notes && !hasNotes ? Color.signal : .secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("分享…") { exportTask = Task { await share() } }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .background(MenuAnchor(box: shareAnchor))
                    .disabled(working)
                if working {
                    if let value = meter.value {
                        ProgressView(value: value).frame(width: 90)
                        Text("正在匯出… \(Int((value * 100).rounded(.down)))%").foregroundStyle(.secondary).monospacedDigit()
                    } else {
                        ProgressView().controlSize(.small)
                        Text("正在匯出…").foregroundStyle(.secondary)
                    }
                    // 停下來、刪掉寫到一半的檔案，留在對話框裡可以重來（R120.2）
                    Button("取消匯出") { exportTask?.cancel() }
                        .buttonStyle(.plain)
                        .foregroundStyle(Color.celadon)
                }
                Spacer()
                Button("取消") {
                    exportTask?.cancel()   // 匯出中按「取消」：取消匯出再關掉
                    dismiss()
                }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                Button("存檔…") { exportTask = Task { await save() } }
                    .buttonStyle(SheetPillButtonStyle(.primary))
                    .keyboardShortcut(.defaultAction)
                    .disabled(working)
            }
        }
        .sheetChrome(width: 480)
        .onChange(of: item) { _, newValue in format = newValue.formats[0] }
        // 還沒有會議紀錄（整理中或整理失敗）時，預設改匯出逐字稿（使用者回報：匯出的會議紀錄只有標題）
        .onAppear {
            bundle = model.exportMeetingId.flatMap { try? model.exportBundle(meetingId: $0) }
            if !hasNotes { item = .transcript }
        }
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
        case .audio:
            let base = String(localized: "m4a 檔案小、適合分享；wav 是 16 kHz 單聲道，適合給其他轉錄工具使用。")
            guard bundle?.meeting.channels == "both" else { return base }
            if bundle?.micRegions.isEmpty ?? true { return base + String(localized: "這場還沒有建議收錄的區塊，麥克風會全部保留。") }
            return base + (micRegionsOnly ? String(localized: "麥克風只留建議收錄的區塊，其餘是沒有聲音；電腦聲音全部保留，長度跟原本一樣。")
                                          : String(localized: "麥克風和電腦聲音全部混在一起。"))
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
        let panel = NSSavePanel()
        panel.nameFieldStringValue = model.suggestedFilename(item, format: format, meeting: bundle?.meeting)
        if let type = UTType(filenameExtension: format) { panel.allowedContentTypes = [type] }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        working = true
        meter.reset()
        defer { working = false; exportTask = nil }
        do {
            try await model.export(item, format: format, times: times, speakers: speakers, meetingId: try meetingId(), to: url,
                                   progress: meter.reporter)
            model.noticeMessage = String(localized: "已匯出 \(url.lastPathComponent)")
            dismiss()
        } catch is CancellationError {
            // 取消了：不是錯誤，什麼都不顯示
        } catch {
            model.errorMessage = error.localizedDescription
        }
    }

    private func share() async {
        guard let url = await exportToTemp(), let view = shareAnchor.view else { return }
        NSSharingServicePicker(items: [url]).show(relativeTo: view.bounds, of: view, preferredEdge: .minY)
    }

    private func exportToTemp() async -> URL? {
        guard let dir = try? Paths.makeTempDirectory() else { return nil }
        let url = dir.appendingPathComponent(model.suggestedFilename(item, format: format, meeting: bundle?.meeting))
        working = true
        meter.reset()
        defer { working = false; exportTask = nil }
        do {
            try await model.export(item, format: format, times: times, speakers: speakers, meetingId: try meetingId(), to: url,
                                   progress: meter.reporter)
            return url
        } catch is CancellationError {
            return nil
        } catch {
            model.errorMessage = error.localizedDescription
            return nil
        }
    }
}

/// 匯出的進度（R120、design §94）：背景回報的比例換到主執行緒，變了 1% 以上、或離上次超過 0.2 秒才更新畫面
@MainActor
@Observable
final class ExportMeter {
    private(set) var value: Double?
    @ObservationIgnored private var updated = Date.distantPast

    func reset() {
        value = nil
        updated = .distantPast
    }

    /// 給匯出用的回報（任何執行緒都可以呼叫）
    var reporter: @Sendable (Double) -> Void {
        { [weak self] p in Task { @MainActor in self?.report(p) } }
    }

    private func report(_ p: Double) {
        let p = min(1, max(value ?? 0, p))   // 只往前
        guard value.map({ p - $0 >= 0.01 || Date().timeIntervalSince(updated) >= 0.2 }) ?? true else { return }
        value = p
        updated = Date()
    }
}
