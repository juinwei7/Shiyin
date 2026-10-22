#if DEBUG
import FluidAudio
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
    /// quit 指令：結束時不跳確認對話框，直接當成按了「結束」
    static var confirmQuit = false

    static func install() {
        DistributedNotificationCenter.default().addObserver(forName: notificationName, object: nil, queue: .main) { note in
            guard let command = note.object as? String else { return }
            MainActor.assumeIsolated { handle(command.split(separator: separator, omittingEmptySubsequences: false).map(String.init)) }
        }
        DebugMetrics.startMainThreadWatch()
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
        case "confirmpurge": model.confirmPurge = id   // 等於在垃圾桶按右鍵「永久刪除…」
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
        // 聲紋（design §21.4）：notthis <會議 id> <說話者>（不是他）；people <輸出.json>；personrename <id> <名字>；
        // persondelete <id>；peopledeleteall
        case "notthis": model.selection = id; model.rejectVoiceprintMatch(Int(args[2]) ?? 1)
        // speakerreview <會議 id> <回答 JSON：{"說話者編號":"是"|"不是，名字"}> <略過的說話者，逐號分隔>：等於在待確認送出說話者的題目（R33.2）
        case "speakerreview":
            model.selection = id
            let answers = (try? JSONDecoder().decode([String: String].self, from: Data(args[2].utf8))) ?? [:]
            model.answerSpeakerQuestions(Dictionary(uniqueKeysWithValues: answers.compactMap { k, v in Int(k).map { ($0, v) } }),
                                         skipped: Set(args[3].split(separator: ",").compactMap { Int($0) }))
        // 猜說話者（design §21.5）：namespeakers <會議 id>（排工作）；applysuggestion、dismisssuggestion <會議 id> <說話者>；
        // applyallsuggestions <會議 id>
        // 對會議提問（design §21.8）：ask <會議 id> <問題>；askall <all|folder|recent> <問題>；
        // questions <會議 id|all> <輸出.json>；cancelask
        case "ask": model.selection = id; model.loadMeetingQuestions(id ?? 0); model.askMeeting(args[2])
        case "askall": model.loadAllQuestions(); model.askAll(args[2], scope: AskScope(rawValue: args[1]) ?? .all)
        case "questions":
            let list = args[1] == "all" ? ((try? model.db.questions(meetingId: nil)) ?? []) : ((try? model.db.questions(meetingId: Int64(args[1]))) ?? [])
            write(["asking": model.asking != nil, "questions": list.map { ["question": $0.question, "scope": $0.scope, "provider": $0.provider,
                                                                            "answer": (try? JSONSerialization.jsonObject(with: Data($0.answer.utf8))) ?? [:]] }],
                  to: args[2])
        case "cancelask": model.cancelAsk()
        // 偵測會議（design §21.7）：meetingapp <bundleID> start|stop；meetingprompts <輸出.json>；meetingaction <按鈕>
        case "meetingapp": model.meetingDetector.simulated[args[1]] = args[2] == "start"
        case "meetingprompts":
            let current = MeetingPromptPanel.shared.current
            let text: String
            switch current {
            case .start(let app): text = "start:\(app.bundleID)"
            case .stop(let app): text = "stop:\(app.bundleID)"
            case nil: text = ""
            }
            write(["prompt": text, "actions": MeetingPromptPanel.shared.actions.keys.sorted(), "muted": MeetingDetector.muted,
                   "recording": model.recorder.isRecording], to: args[1])
        case "meetingaction": MeetingPromptPanel.shared.perform(args[1])
        // reportinfo <輸出.json>：回報問題會附上的內容和網址（不打開瀏覽器、不動剪貼簿，design §21.9）
        case "reportinfo":
            let text = ProblemReport.text(db: model.db)
            write(["text": text, "url": ProblemReport.url(body: text)?.absoluteString ?? "", "issuesURL": ProblemReport.issuesURL], to: args[1])
        // rediarize <會議 id> <人數|auto>：等於在選人數的視窗按「開始」；rediarizerequest <會議 id>：等於按「更多 → 重新辨識說話者…」
        case "rediarize": if let id { model.rediarize(meetingId: id, minSpeakers: args[2] == "auto" ? nil : Int(args[2])) }
        case "rediarizerequest": model.selection = id; model.requestRediarize()
        // 說話者的修正（design §24.6）：mergespeaker <會議 id> <來源> <目標>；reassign <會議 id> <說話者|new> <段落 id…>；undospeaker <會議 id>；
        // speakerhint <會議 id> dismiss：等於按「不用了」
        case "mergespeaker": model.selection = id; model.mergeSpeaker(Int(args[2]) ?? 0, into: Int(args[3]) ?? 0)
        case "reassign":
            model.selection = id
            model.reassignSegments(args.dropFirst(3).compactMap { Int64($0) }, to: args[2] == "new" ? nil : Int(args[2]))
        case "undospeaker": model.selection = id; model.undoSpeakerEdit()
        case "speakerhint": model.selection = id; if args.count > 2, args[2] == "dismiss" { model.dismissSpeakerHint() }
        case "minspeakers": model.selection = id; model.setMinSpeakers(args[2] == "auto" ? nil : Int(args[2]))   // 錄音中的「人數」選單（design §26.2）
        case "levelhint": if args.count > 1, args[1] == "dismiss" { model.recorder.dismissLevelHint() }   // 收音提醒按「知道了」
        case "namespeakers": if let id { try? model.db.enqueue(id, kind: .nameSpeakers); model.runner.wake() }
        case "applysuggestion": model.selection = id; model.applySpeakerSuggestion(Int(args[2]) ?? 1)
        case "dismisssuggestion": model.selection = id; model.dismissSpeakerSuggestion(Int(args[2]) ?? 1)
        case "applyallsuggestions": model.selection = id; model.applyAllSpeakerSuggestions()
        case "people":
            model.reloadPeople()
            write(["people": model.people.map { ["id": $0.id, "name": $0.name, "voiceprints": $0.voiceprintCount,
                                                 "lastTitle": $0.lastMeetingTitle ?? "", "lastDate": $0.lastMeetingDate ?? ""] }], to: args[1])
        case "personrename": if let id { model.renamePerson(id, to: args[2]) }
        case "persondelete": if let id { model.deletePerson(id) }
        case "peopledeleteall": model.deleteAllPeople()
        case "template": model.selection = id; model.setTemplate(args[2])
        case "language": model.selection = id; model.setMeetingLanguage(MeetingLanguage(rawValue: args[2]) ?? .zh)
        case "transcribe-eval": transcribeEval(audio: args[1], options: args[2], output: args[3])   // design §17.1
        case "dump-golden": GoldenDump.run(fixtures: args[1], output: args[2])   // design-windows §4.1
        case "segment-embed": segmentEmbed(audio: args[1], spans: args[2], output: args[3], batched: args.count > 4 ? Int(args[4]) : nil)   // design §6.3.2
        case "correct-eval": correctEval(input: args[1], options: args[2], output: args[3])          // design §17.4
        case "detect-eval": detectEval(audio: args[1], start: Double(args[2]) ?? 0, output: args[3])  // design §18.8
        case "checktranscript": model.selection = id; model.checkTranscript()
        case "correction":   // correction accept|reject|revert <校正 id>
            if let cid = Int64(args[2]) {
                switch args[1] {
                case "accept": model.acceptCorrection(cid)
                case "reject": model.rejectCorrection(cid)
                default: model.revertCorrection(cid)
                }
            }
        // correctiongroups <會議 id> <輸出.json>：校正列「依詞處理」看到的組；
        // correctiongroup <會議 id> accept|reject <第幾組>：等於按那一組的「接受 N 處」「忽略」（design §21.11）
        case "correctiongroups":
            model.selection = id
            let groups = model.detail?.correctionGroups ?? []
            write(["groups": groups.map { ["original": $0[0].original, "suggestion": $0[0].suggestion, "count": $0.count,
                                           "segments": $0.map(\.segmentId)] }], to: args[2])
        case "correctiongroup":
            model.selection = id
            let groups = model.detail?.correctionGroups ?? []
            if let index = Int(args[3]), groups.indices.contains(index) {
                let ids = groups[index].compactMap(\.id)
                args[2] == "accept" ? model.acceptCorrections(ids) : model.rejectCorrections(ids)
            }
        case "corrections":  // corrections <會議 id> accept|reject：全部接受或全部忽略
            model.selection = id
            if args[2] == "accept" { model.acceptAllCorrections() } else { model.rejectAllCorrections() }
        case "editnotes":
            // editnotes <id> <會議紀錄 JSON>
            model.selection = id
            if let notes = try? JSONDecoder().decode(MeetingNotes.self, from: Data(args[2].utf8)) { model.saveEditedNotes(notes, meetingId: id ?? 0) }
        case "usernotes": model.selection = id; model.updateUserNotes(args[2])
        // notestype <文字>：在畫面上的筆記輸入框（TextEditor 背後的 NSTextView）結尾打字，跟鍵盤打字走同一條路，
        // 螢幕上鎖、視窗在別的桌面時也能用；notesvalue <輸出.json>：輸入框目前顯示的內容（design §20.4）
        case "notestype":
            guard let textView = notesTextView() else { break }
            textView.setSelectedRange(NSRange(location: (textView.string as NSString).length, length: 0))
            textView.insertText(args[1], replacementRange: textView.selectedRange())
        case "notesvalue": write(["found": notesTextView() != nil, "text": notesTextView()?.string ?? ""], to: args[1])
        // press <標籤> [第幾個，從 0 開始] [結果.json]：在 app 內走輔助使用的元件樹，按下標籤相符的按鈕。
        // 跟從外面用 AX 按的是同一條路，螢幕上鎖、視窗在別的桌面時也能用
        case "metrics":   // metrics <輸出.json> [reset]：效能計數（design §21.12）；加 reset 讀完歸零
            write(DebugMetrics.snapshot, to: args[1])
            if args.count > 2 && args[2] == "reset" { DebugMetrics.reset() }
        // 輸入框（design §21.15 驗證用，螢幕上鎖、視窗在別的桌面時也能用）：用提示文字找 SwiftUI TextField 背後的 NSTextField
        // typefield <提示文字> <文字> [第幾個]：在結尾打字；fieldvalue <提示文字> <輸出.json> [第幾個]；submitfield <提示文字> [第幾個]：按 Return
        case "typefield":
            guard let field = textField(placeholder: args[1], index: args.count > 3 ? Int(args[3]) ?? 0 : 0) else { break }
            field.window?.makeFirstResponder(field)
            if let editor = field.currentEditor() as? NSTextView {
                editor.setSelectedRange(NSRange(location: (editor.string as NSString).length, length: 0))
                editor.insertText(args[2], replacementRange: editor.selectedRange())
            }
        case "fieldvalue":
            let field = textField(placeholder: args[1], index: args.count > 3 ? Int(args[3]) ?? 0 : 0)
            write(["found": field != nil, "text": field?.stringValue ?? ""], to: args[2])
        case "submitfield":
            guard let field = textField(placeholder: args[1], index: args.count > 2 ? Int(args[2]) ?? 0 : 0) else { break }
            field.window?.makeFirstResponder(field)
            field.currentEditor()?.insertNewline(nil)
        case "editsegment": model.debugEditSegment = id   // editsegment <段落 id>：等於點那一段開始編輯
        case "settingsaction": model.debugSettingsAction = Array(args.dropFirst())   // settingsaction pickmodel | savekey <key>（設定頁要開著）
        // voiceprint-eval <設定.json> <輸出.json>：設定是 [{"file": 音檔, "name": 名稱}]；每個檔案跑說話者辨識，
        // 輸出每位說話者的聲紋、說話秒數、說話的時段，用來跟 AMI 標註比對、決定門檻（design §21.4.1）
        // 設定的每一項可以加 threshold、minSpeakers、numSpeakers（字串）換分群設定，比較分出幾個人；dump（路徑）倒出每個片段的聲紋
        case "voiceprint-eval":
            let config = (try? JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: args[1])))) as? [[String: String]] ?? []
            let output = args[2]
            Task {
                var results: [[String: Any]] = []
                for item in config {
                    guard let file = item["file"] else { continue }
                    let started = Date()
                    do {
                        let samples = try await AudioDecoder.decode(URL(fileURLWithPath: file))
                        var clustering: OfflineDiarizerConfig.Clustering?
                        if item["threshold"] != nil || item["minSpeakers"] != nil || item["numSpeakers"] != nil {
                            var c = OfflineDiarizerConfig.Clustering.community
                            if let t = item["threshold"].flatMap(Double.init) { c.threshold = t }
                            c.minSpeakers = item["minSpeakers"].flatMap(Int.init)
                            c.numSpeakers = item["numSpeakers"].flatMap(Int.init)
                            clustering = c
                        }
                        // 短句的實驗：zeroVote（重新嵌入沒人認領的片段，最短秒數）、minSegment（算聲紋的最短秒數）、step（視窗步距比例）
                        var config: OfflineDiarizerConfig?
                        if item["zeroVote"] != nil || item["minSegment"] != nil || item["step"] != nil || item["legacy"] != nil {
                            // legacy：改之前的設定（FluidAudio 的預設），跟現在比
                            var c = item["legacy"] != nil ? OfflineDiarizerConfig(clustering: clustering ?? .community, exposeChunkEmbeddings: true)
                                : Diarizer.config(clustering: clustering ?? .community)
                            if let v = item["zeroVote"].flatMap(Double.init) { c.zeroVoteReembed = .init(enabled: true, minDurationSeconds: v) }
                            if let v = item["minSegment"].flatMap(Double.init) { c.embedding.minSegmentDurationSeconds = v }
                            if let v = item["step"].flatMap(Double.init) { c.segmentation.stepRatio = v }
                            config = c
                        }
                        let diarization = try await Diarizer.diarize(samples: samples, clustering: clustering, config: config) { _ in }
                        // dump：<路徑>.f32 是每個片段的聲紋（256 個 Float32 一筆），<路徑>.json 是對應的群、片段、片段裡的說話者
                        if let dump = item["dump"] {
                            var floats: [Float] = []
                            for chunk in diarization.chunks { floats += chunk.embedding256 }
                            try floats.withUnsafeBytes { Data($0) }.write(to: URL(fileURLWithPath: dump + ".f32"))
                            write(["cluster": diarization.chunks.map(\.speakerId), "chunk": diarization.chunks.map(\.chunkIndex),
                                   "local": diarization.chunks.map(\.speakerIndex), "start": diarization.chunks.map(\.startTimeSeconds),
                                   "end": diarization.chunks.map(\.endTimeSeconds)], to: dump + ".json")
                        }
                        let speakers: [[String: Any]] = diarization.voiceprints.keys.sorted().map { number in
                            ["number": number, "seconds": diarization.speechSeconds[number] ?? 0,
                             "embedding": diarization.voiceprints[number] ?? [],
                             "turns": diarization.turns.filter { $0.speaker == number }.map { [$0.start, $0.end] }]
                        }
                        results.append(["name": item["name"] ?? file, "seconds": Double(samples.count) / AudioDecoder.sampleRate,
                                        "elapsed": Date().timeIntervalSince(started), "speakers": speakers,
                                        "collisionRate": diarization.quality?.collisionRate ?? -1,
                                        "multiSpeakerChunks": diarization.quality?.multiSpeakerChunks ?? -1,
                                        "cohesion": diarization.quality?.cohesion ?? -1,
                                        "mergeRisk": diarization.quality?.mergeRisk ?? -1])
                    } catch {
                        results.append(["name": item["name"] ?? file, "error": error.localizedDescription])
                    }
                    write(["done": results.count, "total": config.count, "results": results], to: output)
                }
            }
        case "summon":   // summon：把拾音的視窗移到目前的桌面、放到最前面（真的畫面上用輔助使用測試時，視窗在別的桌面會按不到）
            for window in NSApp.windows where window.isVisible && window.frame.width > 400 {
                window.collectionBehavior.insert(.moveToActiveSpace)
                window.makeKeyAndOrderFront(nil)
                window.collectionBehavior.remove(.moveToActiveSpace)
            }
            NSApp.activate()
        case "livescroll":   // livescroll <0～1>：把錄音畫面的即時逐字稿捲到這個位置（0 是最上面），等於拖捲軸（design §25）
            let fraction = CGFloat(Double(args[1]) ?? 0)
            func scrollViews(_ view: NSView) -> [NSScrollView] {
                (view as? NSScrollView).map { [$0] } ?? view.subviews.flatMap(scrollViews)
            }
            for window in NSApp.windows where window.isVisible {
                for scroll in scrollViews(window.contentView ?? NSView())
                where !(scroll.documentView is NSTextView) && !(scroll.documentView is NSTableView) {
                    guard let document = scroll.documentView else { continue }
                    let range = max(0, document.frame.height - scroll.contentView.bounds.height)
                    let y = document.isFlipped ? range * fraction : range * (1 - fraction)
                    scroll.contentView.scroll(to: NSPoint(x: 0, y: y))
                    scroll.reflectScrolledClipView(scroll.contentView)
                }
            }
        case "axdump":   // axdump <輸出.txt>：倒出輔助使用的元件樹（角色、標籤），找 press 要用的標籤
            var lines: [String] = []
            func dump(_ element: NSAccessibilityProtocol, depth: Int) {
                guard depth < 80 else { return }
                lines.append(String(repeating: "  ", count: depth) + "\(element.accessibilityRole()?.rawValue ?? "?") label=\(element.accessibilityLabel() ?? "") title=\(element.accessibilityTitle() ?? "")")
                for child in element.accessibilityChildren() ?? [] {
                    if let child = child as? NSAccessibilityProtocol { dump(child, depth: depth + 1) }
                }
            }
            for window in NSApp.windows where window.isVisible { dump(window, depth: 0) }
            try? lines.joined(separator: "\n").write(toFile: args[1], atomically: true, encoding: .utf8)
        case "press":
            let matches = accessibilityButtons(label: args[1])
            let index = args.count > 2 ? Int(args[2]) ?? 0 : 0
            let pressed = matches.indices.contains(index) && matches[index].accessibilityPerformPress()
            if args.count > 3 { write(["found": matches.count, "pressed": pressed], to: args[3]) }
        case "bookmark": model.addBookmark(at: Double(args[2]) ?? 0, meetingId: id)
        case "addterm": model.addTerm(args[1])
        case "dismissterm": model.dismissGlossarySuggestion(args[1])
        case "record":
            switch args[1] {
            case "start": model.startRecording()
            case "pause": model.recorder.pause()
            case "resume": model.recorder.resume()
            default: model.recorder.stop()
            }
        case "importsheet": model.requestImport(args.dropFirst().map { URL(fileURLWithPath: $0) })   // 只打開匯入對話框
        case "recordstall":   // recordstall <秒數|off> [聲道]：模擬錄音的音源停止送資料（design §20.2）
            model.recorder.simulateStall(channel: args.count > 2 ? Int(args[2]) ?? 0 : 0, seconds: args[1] == "off" ? nil : Double(args[1]) ?? 5)
        case "recordinterrupt":   // recordinterrupt [聲道]：等於音源發現裝置變了，Recorder 會重新開始這個音源
            model.recorder.simulateInterruption(channel: args.count > 1 ? Int(args[1]) ?? 0 : 0)
        case "recordlang":   // recordlang <zh|en|ja>：錄音中換會議語言（等於按「改回」）
            if let language = MeetingLanguage(rawValue: args[1]) { model.recorder.switchLanguage(to: language) }
        case "record-sim":
            // record-sim <音檔> <倍速> <both: 1/0>
            model.startSimulatedRecording(file: URL(fileURLWithPath: args[1]), speed: Double(args[2]) ?? 1, both: args.count > 3 && args[3] == "1")
        case "seek": model.player.seek(to: Double(args[1]) ?? 0, play: args.count > 2 && args[2] == "play")
        case "rate": model.player.setRate(Float(args[1]) ?? 1)
        case "toggle": model.player.toggle()
        case "export":
            // export <項目> <格式> <路徑> [times 1/0] [speakers 1/0] [會議 id，預設目前選取的]
            let item = ExportItem(rawValue: args[1]) ?? .notes
            let url = URL(fileURLWithPath: args[3])
            let meetingId = args.count > 6 ? Int64(args[6]) : model.selection
            Task {
                do {
                    guard let meetingId else { throw AppModel.ExportError.meetingGone }
                    try await model.export(item, format: args[2], times: args.count <= 4 || args[4] == "1",
                                           speakers: args.count <= 5 || args[5] == "1", meetingId: meetingId, to: url)
                }
                catch { try? "ERROR \(error.localizedDescription)".write(to: url.appendingPathExtension("error"), atomically: true, encoding: .utf8) }
            }
        case "import":   // import [lang=ja] [minSpeakers=6] <檔案>…（minSpeakers：匯入對話框的「大約幾個人」，design §26.2）
            var files = Array(args.dropFirst()), options = ImportOptions()
            while let first = files.first, first.hasPrefix("lang=") || first.hasPrefix("minSpeakers=") {
                if first.hasPrefix("lang=") { options.language = MeetingLanguage(rawValue: String(first.dropFirst(5))) ?? .zh }
                else { options.minSpeakers = Int(first.dropFirst(12)) }
                files.removeFirst()
            }
            model.requestImport(files.map { URL(fileURLWithPath: $0) })
            if model.showImportSheet { model.confirmImport(options) }
        case "copy": model.copyNotes(args.count > 1 && args[1] == "plain" ? .plain : args.count > 1 && args[1] == "md" ? .markdown : .rich)
        case "search": model.searchQuery = args.count > 1 ? args[1] : ""
        case "openhit": if let index = Int(args[1]), model.searchResults.indices.contains(index) { model.openSearchHit(model.searchResults[index]) }
        case "model":   // model start|pause|delete [breeze|japanese|languageDetection]
            let which = ModelStore.Model(rawValue: args.count > 2 ? args[2] : "") ?? .breeze
            args[1] == "start" ? model.models.start(which) : args[1] == "pause" ? model.models.pause(which) : model.models.delete(which)
        case "diarizer": model.models.prepareDiarizer()
        case "modelurl":   // modelurl <模型> <網址>：改從別的網址下載；網址給 - 就改回原本的
            if let which = ModelStore.Model(rawValue: args[1]) { model.models.debugURLs[which] = args[2] == "-" ? nil : URL(string: args[2]) }
        case "llmurl":     // llmurl <anthropic|openai|gemini|openaiCompatible> <網址>：API 改送到本機的假伺服器；網址給 - 就改回原本的
            if let provider = LLMProvider(rawValue: args[1]) { LLMClient.debugBaseURLs[provider] = args[2] == "-" ? nil : args[2] }
        case "llmkey":   // llmkey <account> <key>：只放記憶體
            Keychain.debugKeys[args[1]] = args[2] == "-" ? nil : args[2]
            Keychain.debugForget(args[1])
        case "keychainsim":   // keychainsim <account> denied|duplicate|-：模擬鑰匙圈回「拒絕」「已經有舊的」（design §22.1）
            Keychain.debugSimulated[args[1]] = args[2] == "-" ? nil : args[2]
            Keychain.debugForget(args[1])
        case "keychainreads": write(["reads": Keychain.debugReads], to: args[1])   // keychainreads <輸出.json>：查鑰匙圈的次數
        case "llmprovider":   // llmprovider <整理工具> [模型]：只放記憶體，不改共用的設定；給 - 改回設定
            AppSettings.debugProvider = args[1] == "-" ? nil : LLMProvider(rawValue: args[1])
            AppSettings.debugModel = args.count > 2 ? args[2] : nil
        case "listmodels":  // listmodels <整理工具> <結果.json>：設定頁「選擇」用的模型清單
            if let provider = LLMProvider(rawValue: args[1]) {
                Task { write(["models": await LLMClient.listModels(provider)], to: args[2]) }
            }
        case "refreshmodels": model.refreshEnvironment()
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
        case "hotkey":   // hotkey [bookmark]：模擬按下 ⌃⌥⌘R（或 ⌃⌥⌘B）
            if args.count > 1 && args[1] == "bookmark" { GlobalHotKey.shared.fireBookmark() } else { GlobalHotKey.shared.fire() }
        case "hotkeyenable": GlobalHotKey.shared.setEnabled(args[1] == "1")
        // 點通知後的動作（系統的點擊沒辦法模擬，直接呼叫同一個處理函式）
        case "opennotification": if let id { model.notifier.onOpen?(id) }
        case "exportsheet": if args.count < 2 || args[1] == "1" { model.openExportSheet() } else { model.showExportSheet = false }
        // quit：照正常流程結束拾音（錄音中、有工作時等於在確認對話框按「結束」）
        case "quit":
            confirmQuit = true
            NSApp.terminate(nil)
        // benchsave <會議 id> <段數> <輸出.json>：存一份假的逐字稿，量 saveTranscript 花多久（design §20.11）
        case "benchsave":
            guard let meetingId = id, let count = Int(args[2]) else { return }
            let segments = (0..<count).map { i in
                Segment(meetingId: meetingId, seq: i, start: Double(i), end: Double(i + 1), text: "測試段落 \(i) Kubernetes 部署",
                        lowConfidence: false, speaker: nil, kind: "final")
            }
            let start = Date()
            do {
                try model.db.saveTranscript(meetingId, segments: segments, duration: count, speakerCount: 0)
                write(["seconds": Date().timeIntervalSince(start)], to: args[3])
            } catch {
                write(["error": "\(error)"], to: args[3])
            }
        // epipe <輸出.json>：子程序沒讀完輸入就結束（design §20.1）。拾音不能被 SIGPIPE 結束
        case "epipe":
            let path = args[1]
            var current = sigaction()
            sigaction(SIGPIPE, nil, &current)
            let ignored = unsafeBitCast(current.__sigaction_u.__sa_handler, to: Int.self) == 1   // SIG_IGN
            Task {
                do {
                    let result = try await Subprocess(URL(fileURLWithPath: "/usr/bin/true"), []).run(input: Data(count: 1 << 20))
                    write(["status": Int(result.status), "survived": true, "sigpipeIgnored": ignored], to: path)
                } catch {
                    write(["error": error.localizedDescription, "survived": true, "sigpipeIgnored": ignored], to: path)
                }
            }
        // 麥克風權限被拒時的提醒（R1.8）：真的拒絕要使用者操作，這裡只看畫面
        case "permdenied": model.recorder.permissionDenied = args.count < 2 || args[1] == "1"
        case "info": model.selection = id; model.updateInfo(title: args[2], date: args[3], participants: args[4])
        case "closewindow":
            let prefix = args.count > 1 ? args[1] : "main"
            NSApp.windows.filter { $0.identifier?.rawValue.hasPrefix(prefix) == true }.forEach { $0.close() }
        case "openwindow": model.openWindowById?(args[1])
        case "setkey": try? Keychain.set(args[1], args[2])          // setkey anthropic|openai <key>
        case "delkey": Keychain.delete(args[1])
        case "settings": NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
        case "preview":   // preview <settings|onboarding [步驟]|import|rename>（import：匯入對話框；rename：說話者的改名小視窗，design §26.2、§24.2）
            // 設定視窗要 app 在前景才打得開；這裡把畫面放進桌面層的無邊框視窗（被所有視窗蓋住；
            // 螢幕外的視窗截不到），不搶前景，再用 screencapture -l 截（closewindow <settings|onboarding>-preview 關掉）。
            // preview detail：目前選取的會議頁（量長會議的效能用，design §21.12：不在畫面上的視窗 SwiftUI 不更新）
            let onboarding = args.count > 1 && args[1] == "onboarding"
            let detailPage = args.count > 1 && args[1] == "detail"
            let size = detailPage ? NSSize(width: 1100, height: 760) : NSSize(width: 640, height: 560)
            let window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless],
                                  backing: .buffered, defer: false)
            window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopWindow)))
            window.collectionBehavior = [.canJoinAllSpaces, .stationary]
            window.identifier = NSUserInterfaceItemIdentifier(onboarding ? "onboarding-preview" : detailPage ? "detail-preview" : "settings-preview")
            window.title = window.identifier!.rawValue
            window.isReleasedWhenClosed = false
            if args.count > 1 && (args[1] == "import" || args[1] == "rename") {
                window.identifier = NSUserInterfaceItemIdentifier("\(args[1])-preview")
                window.title = window.identifier!.rawValue
                let view = args[1] == "import"
                    ? AnyView(ImportSheet(urls: [URL(fileURLWithPath: "/tmp/週會 0827.m4a")]).environment(model))   // l10n-skip
                    : AnyView(RenameSpeakerForm(name: .constant("說話者 2"), mergeTargets: [(1, "小明"), (3, "說話者 3")],   // l10n-skip
                                                onMerge: { _ in }, onSave: {}))
                window.contentView = NSHostingView(rootView: view.background(Color(nsColor: .windowBackgroundColor)))
                window.setContentSize(window.contentView!.fittingSize)
            } else if detailPage {
                window.contentView = NSHostingView(rootView: AnyView(DetailPreview().environment(model)))
            } else {
                window.contentView = onboarding
                    ? NSHostingView(rootView: AnyView(OnboardingView(startStep: args.count > 2 ? Int(args[2]) ?? 0 : 0).environment(model)))
                    : NSHostingView(rootView: AnyView(SettingsView().environment(model)))
            }
            window.orderFrontRegardless()
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
            // 選中的會議：說話者的名字、來源、建議、最像的幾位認識的人（R33，「自動」名字的選單）
            "speakers": (model.detail?.speakers ?? [:]).keys.sorted().map { idx -> [String: Any] in
                ["idx": idx, "name": model.detail?.speakers[idx] ?? "", "source": model.detail?.speakerSources[idx] ?? "",
                 "suggestion": model.detail?.speakerSuggestions[idx]?.name ?? "", "candidates": model.detail?.speakerCandidates[idx] ?? []]
            },
            "glossary": model.glossary.map(\.term),
            "glossarySuggestions": model.glossarySuggestions.map { ["term": $0.term, "source": $0.sourceLabel] },
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
                         "levelCheck": model.recorder.levelCheck,   // 收音提醒（design §26.1）
                         "failure": model.recorder.failureMessage ?? "", "language": model.recorder.language.rawValue,
                         "languageSwitch": model.recorder.languageSwitch.map { "\($0.from.rawValue)→\($0.to.rawValue)" } ?? "",
                         "deviceWarning": model.recorder.deviceWarning ?? ""],
            "languages": ["defaultMeeting": AppSettings.defaultMeetingLanguage.rawValue, "interface": MeetingLanguage.interface.rawValue,
                          "preferred": Locale.preferredLanguages.first ?? ""],
            "models": ["breeze": "\(model.models.state)", "japanese": "\(model.models.state(.japanese))",
                       "languageDetection": "\(model.models.state(.languageDetection))", "diarizer": "\(model.models.diarizerState)"],
            "exportMeeting": model.exportMeetingId as Any,
            "search": ["query": model.searchQuery, "results": model.searchResults.map { ["meeting": $0.meetingId, "kind": $0.kind.rawValue, "ref": $0.ref, "snippet": $0.snippet] }],
            "trash": model.trash.map { $0.id },
            "templates": model.templates.map { $0.id },
            "folders": model.folders.map { ["name": $0.name, "count": $0.count] },
            "folderFilter": model.folderFilter ?? "",
            "update": ["state": model.updater.state.label, "prompt": model.updater.prompt?.version ?? "",
                       "version": Updater.currentVersion, "feed": Updater.feedURLString],
            "hotkey": ["registered": GlobalHotKey.shared.isRegistered, "status": Int(GlobalHotKey.shared.status),
                       "bookmarkRegistered": GlobalHotKey.shared.isBookmarkRegistered],
            "notifications": model.notifier.recent,
            "filteredIds": model.filteredMeetings.map(\.id),
            "highlightSegment": model.highlightSegment ?? -1,
            "errorMessage": model.errorMessage ?? "",
            "noticeMessage": model.noticeMessage ?? "",
            // 視窗的最小尺寸（SwiftUI 依內容算的）：錄音中卡住的調查用
            "windows": NSApp.windows.map { ["id": $0.identifier?.rawValue ?? "", "title": $0.title, "visible": $0.isVisible,
                                            "minWidth": $0.contentMinSize.width, "minHeight": $0.contentMinSize.height,
                                            "minSizeW": $0.minSize.width, "minSizeH": $0.minSize.height,
                                            "width": $0.frame.width, "height": $0.frame.height] },
        ]
        if let status = model.detailStatus { out["detailStatus"] = status.label }
        if let detail = model.detail {
            out["transcribedWith"] = detail.meeting.transcribedWith
            out["canUpgradeTranscript"] = model.canUpgradeTranscript(detail.meeting)   // design §21.17
            out["minSpeakers"] = detail.meeting.minSpeakers ?? 0   // design §23
            out["showRediarizeSheet"] = model.showRediarizeSheet
            out["speakerCheck"] = detail.meeting.speakerCheck ?? -1   // design §24.6
            out["showsSpeakerHint"] = detail.showsSpeakerHint
            out["speakerUndo"] = (try? model.db.speakerUndoLabel(detail.meeting.id!)) ?? ""
            out["manualSegments"] = detail.segments.filter(\.speakerManual).count
        }
        return out
    }

    /// 提示文字是 placeholder 的輸入框（依畫面上的順序）
    private static func textField(placeholder: String, index: Int) -> NSTextField? {
        var found: [NSTextField] = []
        func walk(_ view: NSView) {
            if let field = view as? NSTextField, field.isEditable,
               field.placeholderString == placeholder || field.placeholderAttributedString?.string == placeholder {
                found.append(field)
            }
            view.subviews.forEach(walk)
        }
        for window in NSApp.windows where window.isVisible { window.contentView.map(walk) }
        return found.indices.contains(index) ? found[index] : nil
    }

    /// 所有看得到的視窗裡，標籤或標題等於 label 的按鈕（依元件樹的順序）
    private static func accessibilityButtons(label: String) -> [NSAccessibilityProtocol] {
        var found: [NSAccessibilityProtocol] = []
        func walk(_ element: NSAccessibilityProtocol, depth: Int) {
            guard depth < 80 else { return }
            if element.accessibilityRole() == .button,
               element.accessibilityLabel() == label || element.accessibilityTitle() == label {
                found.append(element)
            }
            for child in element.accessibilityChildren() ?? [] {
                if let child = child as? NSAccessibilityProtocol { walk(child, depth: depth + 1) }
            }
        }
        for window in NSApp.windows where window.isVisible { walk(window, depth: 0) }
        return found
    }

    /// 主視窗裡可以編輯的 NSTextView（筆記分頁、錄音畫面的筆記輸入框）
    private static func notesTextView() -> NSTextView? {
        func find(_ view: NSView) -> NSTextView? {
            if let textView = view as? NSTextView, textView.isEditable, !textView.isFieldEditor { return textView }
            for subview in view.subviews { if let found = find(subview) { return found } }
            return nil
        }
        return NSApp.windows.lazy.filter(\.isVisible).compactMap { $0.contentView.flatMap(find) }.first
    }

    private static func write(_ object: [String: Any], to path: String) {
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]) else { return }
        try? data.write(to: URL(fileURLWithPath: path))
    }

    /// correct-eval <transcribe-eval 的輸出.json> <設定.json> <輸出.json>：用目前的整理工具跑逐字稿校正，
    /// 輸出「全部接受」之後的逐字稿和所有建議（design §17.4），格式跟 transcribe-eval 一樣，可以直接評分。
    /// 設定：{"language": "zh", "glossary": [...], "title": "…", "participants": [...]}
    private static func correctEval(input: String, options: String, output: String) {
        Task { @MainActor in
            let started = Date()
            var result: [String: Any] = ["input": input]
            do {
                guard let source = try JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: input))) as? [String: Any],
                      var segments = source["segments"] as? [[String: Any]],
                      let settings = try JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: options))) as? [String: Any]
                else { throw LLMError.badOutput("bad input") }
                let language = MeetingLanguage(rawValue: settings["language"] as? String ?? "zh") ?? .zh
                let speakerName = { (s: [String: Any]) -> String? in
                    (s["speaker"] as? Int).map { language.text(zh: "說話者 \($0)", en: "Speaker \($0)", ja: "話者 \($0)") }
                }
                let context = TranscriptCorrection.Context(
                    language: language,
                    title: settings["title"] as? String ?? "", participants: settings["participants"] as? [String] ?? [],
                    speakerNames: [], glossary: settings["glossary"] as? [String] ?? [],
                    segments: segments.map { ($0["text"] as? String ?? "", speakerName($0)) })
                let suggestions = try await TranscriptCorrection.suggest(context, provider: AppSettings.provider, model: AppSettings.model,
                                                                         onStart: { _ in }, onProgress: { _ in })
                for s in suggestions {
                    if let text = segments[s.segment]["text"] as? String, let fixed = TranscriptCorrection.apply(s, to: text) {
                        segments[s.segment]["text"] = fixed
                    }
                }
                result["segments"] = segments
                result["duration"] = source["duration"]
                result["options"] = source["options"]
                result["suggestions"] = suggestions.map { ["segment": $0.segment, "original": $0.original,
                                                           "suggestion": $0.suggestion, "reason": $0.reason] }
            } catch {
                result["error"] = error.localizedDescription
            }
            result["elapsed"] = Date().timeIntervalSince(started)
            write(result, to: output + ".tmp")
            try? FileManager.default.removeItem(atPath: output)
            try? FileManager.default.moveItem(atPath: output + ".tmp", toPath: output)
        }
    }

    /// segment-embed <錄音> <時段.json：[[開始, 結束], …]> <輸出.json>：每一段帶著前後文（10 秒的窗、遮罩只標這一段）
    /// 用 FluidAudio 的 wespeaker_v2 算聲紋，給短句的實驗離線比對（design §6.3.2）。模型放在快取資料夾，不放進拾音的模型資料夾
    private static func segmentEmbed(audio: String, spans: String, output: String, batched: Int? = nil) {
        Task { @MainActor in
            let started = Date()
            var result: [String: Any] = ["audio": audio]
            do {
                let samples = try await AudioDecoder.decode(URL(fileURLWithPath: audio))
                let list = (try JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: spans))) as? [[Double]]) ?? []
                // batched：用 app 的做法（SpeakerRecheck，一次三句），跟 FluidAudio 的 extractor 比
                if let batched {
                    let model = try await SpeakerRecheck.prepare()
                    result["embeddings"] = try SpeakerRecheck.embed(list.map { ($0[0], $0[1]) }, samples: samples, model: model, perCall: batched)
                    result["elapsed"] = Date().timeIntervalSince(started)
                    write(result, to: output + ".tmp")
                    try? FileManager.default.removeItem(atPath: output)
                    try? FileManager.default.moveItem(atPath: output + ".tmp", toPath: output)
                    return
                }
                let dir = URL.cachesDirectory.appending(path: "ShiyinEval/wespeaker", directoryHint: .isDirectory)
                let models = try await DiarizerModels.downloadIfNeeded(to: dir)
                let extractor = EmbeddingExtractor(embeddingModel: models.embeddingModel)
                let shape = models.segmentationModel.modelDescription.outputDescriptionsByName["segments"]?.multiArrayConstraint?.shape
                let frames = shape.map { $0[1].intValue } ?? 589
                let window = 160_000, rate = AudioDecoder.sampleRate
                var embeddings: [Any] = []
                for span in list where span.count == 2 {
                    let center = (span[0] + span[1]) / 2
                    let start = max(0, min(Int((center - 5) * rate), samples.count - window))
                    var clip = Array(samples[start..<min(samples.count, start + window)])
                    if clip.count < window { clip += [Float](repeating: 0, count: window - clip.count) }
                    let from = Double(start) / rate
                    let mask = (0..<frames).map { i -> Float in
                        let t = from + (Double(i) + 0.5) * 10 / Double(frames)
                        return t >= span[0] && t <= span[1] ? 1 : 0
                    }
                    guard mask.reduce(0, +) >= 1 else { embeddings.append(NSNull()); continue }
                    embeddings.append(try extractor.getEmbeddings(audio: clip, masks: [mask], minActivityThreshold: 0).first ?? [])
                }
                result["embeddings"] = embeddings
            } catch {
                result["error"] = "\(error)"
            }
            result["elapsed"] = Date().timeIntervalSince(started)
            write(result, to: output + ".tmp")
            try? FileManager.default.removeItem(atPath: output)
            try? FileManager.default.moveItem(atPath: output + ".tmp", toPath: output)
        }
    }

    /// transcribe-eval <錄音> <設定.json> <輸出.json>：用跟 app 一樣的轉錄流程跑一次，不建立會議、不寫資料庫（design §17.1）。
    /// 輸出寫完才出現（先寫暫存檔再改名），評估腳本等這個檔案出現就能讀
    private static func transcribeEval(audio: String, options: String, output: String) {
        Task { @MainActor in
            let started = Date()
            var result: [String: Any] = ["audio": audio]
            let meter = MemoryMeter()
            do {
                let options = try JSONDecoder().decode(TranscriptionOptions.self, from: Data(contentsOf: URL(fileURLWithPath: options)))
                result["options"] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(options))
                var pipeline = TranscriptionPipeline(options: options)
                pipeline.stage = { meter.set(stage: $0) }
                if let run = try await pipeline.run(URL(fileURLWithPath: audio)) {
                    result["duration"] = run.duration
                    result["seams"] = run.seams
                    result["segments"] = run.segments.enumerated().map { i, segment -> [String: Any] in
                        ["start": segment.start, "end": segment.end, "text": segment.text,
                         "speaker": run.speakers[i] ?? NSNull(), "channel": run.channels[i] ?? NSNull(),
                         "lowConfidence": segment.lowConfidence]
                    }
                }
            } catch {
                result["error"] = error.localizedDescription
            }
            result["elapsed"] = Date().timeIntervalSince(started)
            result["memory"] = meter.stop()
            write(result, to: output + ".tmp")
            try? FileManager.default.removeItem(atPath: output)
            try? FileManager.default.moveItem(atPath: output + ".tmp", toPath: output)
        }
    }

    /// detect-eval <音檔> <開始秒數> <輸出.json>：從開始秒數起，跟錄音時一樣收集 10 秒有聲音的片段做語言偵測（design §18.8）
    private static func detectEval(audio: String, start: Double, output: String) {
        Task { @MainActor in
            var result: [String: Any] = ["audio": audio, "start": start]
            do {
                let samples = try await AudioDecoder.decode(URL(fileURLWithPath: audio))
                let from = min(samples.count, Int(start * AudioDecoder.sampleRate))
                let box = DetectBox()
                let collector = LanguageDetector.Collector { box.samples = $0 }
                var position = from
                while box.samples == nil && position < samples.count {
                    let end = min(samples.count, position + 1600)
                    collector.feed(Array(samples[position..<end]))
                    position = end
                }
                guard let voiced = box.samples else { throw WhisperError.empty }
                result["used"] = Double(position - from) / AudioDecoder.sampleRate
                let path = ModelStore.path(.languageDetection).path
                let probabilities = try await Task.detached { try LanguageDetector.detect(voiced, modelPath: path) }.value
                result["probabilities"] = Dictionary(uniqueKeysWithValues: probabilities.map { ($0.key.rawValue, $0.value) })
                result["detected"] = probabilities.max { $0.value < $1.value }?.key.rawValue ?? ""
            } catch {
                result["error"] = error.localizedDescription
            }
            write(result, to: output)
        }
    }

    private final class DetectBox: @unchecked Sendable { var samples: [Float]? }

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
        case "glossary":
            content = AnyView(GlossarySettings().frame(width: 560, height: 420))
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

/// preview detail：跟主視窗一樣的會議頁，跟著 selection 換
private struct DetailPreview: View {
    @Environment(AppModel.self) private var model
    var body: some View {
        if let detail = model.detail { MeetingDetailView(detail: detail) } else { Text("沒有選取會議") }  // l10n-skip
    }
}

/// 截圖用：標題、狀態列、分頁內容、播放列（不含 NavigationSplitView 與工具列）
private struct SnapshotView: View {
    let detail: MeetingDetail
    let tab: DetailTab
    var speakerFilter: Int? = nil

    var body: some View {
        VStack(spacing: 0) {
            MeetingTop(detail: detail)
            Divider()
            Group {
                switch tab {
                case .summary: SummaryTab(detail: detail)
                case .topics: TopicsTab(detail: detail)
                case .transcript: TranscriptTab(detail: detail, speakerFilter: speakerFilter)
                case .review: ReviewTab(detail: detail)
                case .notes: UserNotesTab(detail: detail)
                case .ask: AskTab(detail: detail)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            PlayerBar(detail: detail)
        }
        .tint(.celadon)
    }
}
#endif
