#if DEBUG
import AVFoundation
import AppKit
import SwiftUI
import UserNotifications

/// 只在 DEBUG 建置存在的控制通道：讓驗證腳本在不操作畫面（例如螢幕鎖定）時也能觸發功能。
/// 用 scripts/dev/shiyinctl 送指令；結果寫到指令指定的檔案。正式版（release）不會編進這段程式。
@MainActor
enum DebugControl {
    static let notificationName = Notification.Name("com.weiwei.shiyin.debug")
    static let separator: Character = "\u{1F}"

    static func install() {
        DistributedNotificationCenter.default().addObserver(forName: notificationName, object: nil, queue: .main) { note in
            guard let command = note.object as? String else { return }
            MainActor.assumeIsolated { handle(command.split(separator: separator, omittingEmptySubsequences: false).map(String.init)) }
        }
        log.info("debug control installed")
    }

    private static func handle(_ args: [String]) {
        let model = AppModel.shared
        guard let name = args.first else { return }
        let id = args.count > 1 ? Int64(args[1]) : nil
        switch name {
        case "select": model.showTrash = false; model.selection = id
        case "tab": model.tab = DetailTab(rawValue: args[1]) ?? .summary
        case "cancel": model.selection = id; model.cancelJobs()
        case "retry": model.selection = id; model.retry()
        case "retranscribe": model.selection = id; model.retranscribe()
        case "resummarize": model.selection = id; model.resummarize()
        case "undo": model.selection = id; model.undoNotes()
        case "trash": if let id { model.moveToTrash(id) }
        case "restore": if let id { model.restore(id) }
        case "purge": if let id { model.deletePermanently(id) }
        case "review":
            // review <id> <答案 JSON：{"0":"答案"}> <略過的題號，逗號分隔>
            model.selection = id
            let answers = (try? JSONDecoder().decode([String: String].self, from: Data(args[2].utf8))) ?? [:]
            let dismissed = Set(args[3].split(separator: ",").compactMap { Int($0) })
            model.submitReview(answers: Dictionary(uniqueKeysWithValues: answers.compactMap { k, v in Int(k).map { ($0, v) } }),
                               dismissed: dismissed)
        case "replace":
            model.selection = id
            let result = model.replace(find: args[2], with: args[3], addToGlossary: args[4] == "1")
            write(["segmentsChanged": result?.segmentsChanged ?? -1, "notesChanged": result?.notesChanged ?? false], to: args[5])
        case "segment": model.updateSegment(id ?? 0, text: args[2])
        case "speaker": model.selection = id; model.renameSpeaker(Int(args[2]) ?? 1, to: args[3])
        case "template": model.selection = id; model.setTemplate(args[2])
        case "language": model.selection = id; model.setMeetingLanguage(MeetingLanguage(rawValue: args[2]) ?? .zh)
        case "editnotes":
            // editnotes <id> <會議紀錄 JSON>
            model.selection = id
            if let notes = try? JSONDecoder().decode(MeetingNotes.self, from: Data(args[2].utf8)) { model.saveEditedNotes(notes) }
        case "usernotes": model.selection = id; model.updateUserNotes(args[2])
        case "bookmark": model.addBookmark(at: Double(args[2]) ?? 0, meetingId: id)
        case "addterm": model.addTerm(args[1])
        case "record":
            switch args[1] {
            case "start": model.startRecording()
            case "pause": model.recorder.pause()
            case "resume": model.recorder.resume()
            default: model.recorder.stop()
            }
        case "record-sim":
            // record-sim <音檔> <倍速> <both: 1/0>
            model.startSimulatedRecording(file: URL(fileURLWithPath: args[1]), speed: Double(args[2]) ?? 1, both: args.count > 3 && args[3] == "1")
        case "seek": model.player.seek(to: Double(args[1]) ?? 0, play: args.count > 2 && args[2] == "play")
        case "rate": model.player.setRate(Float(args[1]) ?? 1)
        case "toggle": model.player.toggle()
        case "export":
            // export <項目> <格式> <路徑> [times 1/0] [speakers 1/0]
            let item = ExportItem(rawValue: args[1]) ?? .notes
            let url = URL(fileURLWithPath: args[3])
            Task {
                do { try await model.export(item, format: args[2], times: args.count <= 4 || args[4] == "1",
                                            speakers: args.count <= 5 || args[5] == "1", to: url) }
                catch { try? "ERROR \(error.localizedDescription)".write(to: url.appendingPathExtension("error"), atomically: true, encoding: .utf8) }
            }
        case "import": model.requestImport(args.dropFirst().map { URL(fileURLWithPath: $0) }); if model.showImportSheet { model.confirmImport(ImportOptions()) }
        case "copy": model.copyNotes(args.count > 1 && args[1] == "plain" ? .plain : args.count > 1 && args[1] == "md" ? .markdown : .rich)
        case "search": model.searchQuery = args.count > 1 ? args[1] : ""
        case "openhit": if let index = Int(args[1]), model.searchResults.indices.contains(index) { model.openSearchHit(model.searchResults[index]) }
        case "model": args[1] == "start" ? model.models.start() : args[1] == "pause" ? model.models.pause() : model.models.delete()
        case "diarizer": model.models.prepareDiarizer()
        case "verifymodel":
            // verifymodel <檔案> <結果.json>：用下載完成時同一個函式驗證 SHA-256
            let ok = (try? ModelStore.verify(URL(fileURLWithPath: args[1]))) ?? false
            write(["valid": ok], to: args[2])
        case "state": write(state(), to: args[1])
        case "snapshot": snapshot(to: args[1], scheme: args.count > 2 ? args[2] : "light", target: args.count > 3 ? args[3] : "detail")
        case "filter": model.filter = args.count > 1 ? args[1] : ""
        // 資料夾（R9.2）：名稱用「-」表示空的
        case "folder": if let id { model.moveToFolder(id, folder: args[2] == "-" ? "" : args[2]) }
        case "folderfilter": model.folderFilter = args.count > 1 && args[1] != "-" ? args[1] : nil
        case "renamefolder": model.renameFolder(args[1], to: args[2])
        case "deletefolder": model.deleteFolder(args[1])
        // 更新（R10.6）
        case "setupdateurl": UserDefaults.standard.set(args.count > 1 && args[1] != "-" ? args[1] : nil, forKey: Updater.urlKey)
        case "checkupdate": Task { await model.updater.check(userInitiated: true) }
        case "installupdate":
            if case .available(let manifest) = model.updater.state { Task { await model.updater.install(manifest, model: model) } }
        case "dismissupdate": model.updater.prompt = nil
        // 全域快捷鍵（R11.3）：不能送按鍵，用這個模擬按下
        case "hotkey": GlobalHotKey.shared.fire()
        case "hotkeyenable": GlobalHotKey.shared.setEnabled(args[1] == "1")
        // 點通知後的動作（系統的點擊沒辦法模擬，直接呼叫同一個處理函式）
        case "opennotification": if let id { model.notifier.onOpen?(id) }
        case "exportsheet": model.showExportSheet = args.count < 2 || args[1] == "1"
        // 麥克風權限被拒時的提醒（R1.8）：真的拒絕要使用者操作，這裡只看畫面
        case "permdenied": model.recorder.permissionDenied = args.count < 2 || args[1] == "1"
        case "info": model.selection = id; model.updateInfo(title: args[2], date: args[3], participants: args[4])
        case "closewindow":
            let prefix = args.count > 1 ? args[1] : "main"
            NSApp.windows.filter { $0.identifier?.rawValue.hasPrefix(prefix) == true }.forEach { $0.close() }
        case "openwindow": model.openWindowById?(args[1])
        case "setkey": Keychain.set(args[1], args[2])          // setkey anthropic|openai <key>
        case "delkey": Keychain.delete(args[1])
        case "settings": NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
        case "notifystatus":
            Task {
                let settings = await UNUserNotificationCenter.current().notificationSettings()
                write(["authorization": settings.authorizationStatus.rawValue], to: args[1])
            }
        default: log.error("unknown debug command \(name)")
        }
    }

    private static func state() -> [String: Any] {
        let model = AppModel.shared
        var out: [String: Any] = [
            "selection": model.selection ?? -1,
            "tab": model.tab.rawValue,
            "environment": model.environment.items.map { ["name": $0.name, "ok": $0.ok] },
            "meetings": model.meetings.map { ["id": $0.id, "title": $0.meeting.displayTitle, "date": $0.meeting.meetingDate,
                                               "status": model.status($0).label, "audio": $0.meeting.audioFile,
                                               "template": $0.meeting.templateId, "folder": $0.meeting.folder] },
            "queuedCount": model.queuedCount,
            "filteredCount": model.filteredMeetings.count,
            "menuBarSymbol": model.menuBarSymbol,
            "micStatus": AVCaptureDevice.authorizationStatus(for: .audio).rawValue,
            "openMainWindowReady": model.openMainWindow != nil,
            "running": model.runner.running.map { lane, r in
                ["lane": lane.rawValue, "job": r.job.id ?? 0, "kind": r.job.kind.rawValue, "stage": r.stage, "progress": r.progress ?? -1,
                 "remaining": r.remaining ?? -1] },
            "player": ["loaded": model.player.isLoaded, "playing": model.player.isPlaying,
                       "time": model.player.currentTime, "duration": model.player.duration, "rate": model.player.rate],
            "recorder": ["recording": model.recorder.isRecording, "paused": model.recorder.isPaused,
                         "meetingId": model.recorder.meetingId ?? -1, "elapsed": model.recorder.elapsed,
                         "micLevel": model.recorder.micLevel, "systemLevel": model.recorder.systemLevel,
                         "liveLines": model.recorder.liveLines.count, "volatile": model.recorder.volatileText,
                         "liveStatus": model.recorder.liveStatus, "permissionDenied": model.recorder.permissionDenied,
                         "failure": model.recorder.failureMessage ?? ""],
            "models": ["breeze": "\(model.models.state)", "diarizer": "\(model.models.diarizerState)"],
            "search": ["query": model.searchQuery, "results": model.searchResults.map { ["meeting": $0.meetingId, "kind": $0.kind.rawValue, "ref": $0.ref, "snippet": $0.snippet] }],
            "trash": model.trash.map { $0.id },
            "templates": model.templates.map { $0.id },
            "folders": model.folders.map { ["name": $0.name, "count": $0.count] },
            "folderFilter": model.folderFilter ?? "",
            "update": ["state": model.updater.state.label, "prompt": model.updater.prompt?.version ?? "",
                       "version": Updater.currentVersion, "feed": Updater.feedURLString],
            "hotkey": ["registered": GlobalHotKey.shared.isRegistered, "status": Int(GlobalHotKey.shared.status)],
            "notifications": model.notifier.recent,
            "filteredIds": model.filteredMeetings.map(\.id),
            "highlightSegment": model.highlightSegment ?? -1,
            "errorMessage": model.errorMessage ?? "",
            "noticeMessage": model.noticeMessage ?? "",
            "windows": NSApp.windows.map { ["id": $0.identifier?.rawValue ?? "", "title": $0.title, "visible": $0.isVisible] },
        ]
        if let status = model.detailStatus { out["detailStatus"] = status.label }
        return out
    }

    private static func write(_ object: [String: Any], to path: String) {
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]) else { return }
        try? data.write(to: URL(fileURLWithPath: path))
    }

    /// 螢幕鎖定時沒辦法截視窗，改用 ImageRenderer 把畫面畫成圖片（AppKit 元件會變成黃色佔位框）
    private static func snapshot(to path: String, scheme: String, target: String) {
        let model = AppModel.shared
        let content: AnyView
        switch target {
        case "guide":
            content = AnyView(GuideView().frame(width: 680))
        case "menubar":
            content = AnyView(MenuBarContent().frame(width: 290))
        case "setup":
            content = AnyView(OnboardingView())
        case "import":
            content = AnyView(ImportSheet(urls: [URL(fileURLWithPath: "/tmp/週會 0827.m4a")]))
        case "onboarding":
            content = AnyView(OnboardingView())
        case "export":
            content = AnyView(ExportSheet())
        case "recording":
            guard let detail = model.detail else { return }
            content = AnyView(RecordingPanel(detail: detail).frame(width: 900, height: 520))
        case "replace":
            content = AnyView(ReplaceSheet(initialFind: "Lumeo"))
        case "folders":
            // 側欄的資料夾區塊（List 畫不出來，用同樣的列重組）
            content = AnyView(VStack(alignment: .leading, spacing: 8) {
                Text("資料夾").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                FolderRow(name: "所有會議", count: model.meetings.count, symbol: "tray.full", selected: model.folderFilter == nil) {}
                ForEach(model.folders, id: \.name) { folder in
                    FolderRow(name: folder.name, count: folder.count, symbol: "folder", selected: model.folderFilter == folder.name) {}
                }
                Text(model.folderFilter ?? "所有會議").font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 8)
                ForEach(model.filteredMeetings) { row in MeetingRowView(row: row, status: model.status(row)) }
            }
            .padding(14).frame(width: 280))
        case "sidebar":
            content = AnyView(VStack(alignment: .leading, spacing: 0) {
                ForEach(model.filteredMeetings) { row in
                    MeetingRowView(row: row, status: model.status(row)).padding(.horizontal, 12)
                    Divider()
                }
            }.frame(width: 260))
        case let t where t.hasPrefix("speaker-"):
            // speaker-2：逐字稿只看說話者 2
            guard let detail = model.detail else { return }
            content = AnyView(SnapshotView(detail: detail, tab: .transcript, speakerFilter: Int(t.dropFirst(8))).frame(width: 900))
        default:
            guard let detail = model.detail else { return }
            content = AnyView(SnapshotView(detail: detail, tab: model.tab).frame(width: 900))
        }
        let view = content
            .environment(model)
            .environment(\.snapshotMode, true)
            .environment(\.colorScheme, scheme == "dark" ? .dark : .light)
            .fixedSize(horizontal: false, vertical: true)
            .background(Color(nsColor: scheme == "dark" ? NSColor(white: 0.13, alpha: 1) : .white))
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        guard let image = renderer.nsImage, let tiff = image.tiffRepresentation,
              let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) else { return }
        try? png.write(to: URL(fileURLWithPath: path))
    }
}

/// 截圖用：標題、狀態列、分頁內容、播放列（不含 NavigationSplitView 與工具列）
private struct SnapshotView: View {
    let detail: MeetingDetail
    let tab: DetailTab
    var speakerFilter: Int? = nil

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 14) {
                MeetingHeader(detail: detail)
                StatusBanner(detail: detail)
            }
            .padding(.horizontal, 32)
            .padding(.top, 22)
            .padding(.bottom, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
            Divider()
            Group {
                switch tab {
                case .summary: SummaryTab(detail: detail)
                case .topics: TopicsTab(detail: detail)
                case .transcript: TranscriptTab(detail: detail, speakerFilter: speakerFilter)
                case .review: ReviewTab(detail: detail)
                case .notes: UserNotesTab(detail: detail)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            PlayerBar(detail: detail)
        }
        .tint(.celadon)
    }
}
#endif
