#if DEBUG
import FluidAudio
import AVFoundation
import CoreAudio
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
        // notesview list|mindmap|people：紀錄分頁切換成條列、心智圖或人物（design §42.5；寫進共用設定，測完 defaults delete com.weiwei.shiyin notesView）
        case "notesview":
            let view = args.count > 1 ? args[1].lowercased() : "list"
            UserDefaults.standard.set(view == "mindmap" ? "mindMap" : view == "people" ? "people" : "list", forKey: AppSettings.notesViewKey)
        case "tab":   // tab <summary|transcript|ask>；舊名稱 topics、review、notes 當別名（review 順便展開待確認、notes 展開我的筆記，design §33.1）
            if let r = DetailTab.resolve(args[1]) {
                model.tab = r.tab
                if r.expandReview { model.reviewExpanded = true }
                if r.expandNotes { model.userNotesExpanded = true }
            }
        case "cancel": model.selection = id; model.cancelJobs()
        case "retry": model.selection = id; model.retry()
        case "retranscribe": model.selection = id; model.retranscribe()
        case "resummarize": model.selection = id; model.resummarize()
        case "peoplenotes": model.selection = id; model.generatePeopleNotes()   // 「人物」的「產生每個人的重點」（design §66）
        case "cleantranscript": model.selection = id; model.generateCleanTranscript()   // 逐字稿「精簡」的「產生精簡版」（design §70）
        case "transcriptview": UserDefaults.standard.set(args[1] == "clean" ? "clean" : "original", forKey: AppSettings.transcriptViewKey)
        case "reload": model.reloadPeopleOverview(); model.reload()   // 直接改資料庫之後重讀
        // 資料夾自動歸類（design §72）
        case "folderrule":   // folderrule <資料夾> <JSON|-> [apply]：JSON 是 {"keywords":[…],"people":[id…],"templateId":…}
            var rule = FolderRule(folder: args[1])
            if args[2] != "-", let data = args[2].data(using: .utf8),
               let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                rule.keywords = object["keywords"] as? [String] ?? []
                rule.people = (object["people"] as? [NSNumber] ?? []).map(\.int64Value)
                rule.templateId = object["templateId"] as? String
            }
            model.saveFolderRule(rule, applyNow: args.count > 3 && args[3] == "apply")
        case "editfolderrule": model.editingFolderRule = args[1]   // 打開條件的編輯
        case "autofile":
            if let id, let folder = try? model.db.autoFile(id) { model.organized(id, folder: folder) }
        case "foldersuggest":   // foldersuggest <會議 id> <輸出.json>
            if let id, let meeting = try? model.db.meeting(id) {
                let s = model.folderSuggestion(for: meeting)
                var reason: [String: Any] = [:]
                switch s?.reason {
                case .people(let ids)?: reason = ["people": ids]
                case .title(let word)?: reason = ["title": word]
                case nil: break
                }
                write(["folder": s?.folder ?? "", "count": s?.count ?? 0, "reason": reason,
                       "text": s.map { model.folderSuggestionText($0) } ?? ""], to: args[2])
            }
        case "foldersuggestion":   // foldersuggestion accept|dismiss <會議 id>
            if let mid = Int64(args[2]), let meeting = try? model.db.meeting(mid) {
                if args[1] == "accept", let s = model.folderSuggestion(for: meeting) {
                    model.acceptFolderSuggestion(mid, folder: s.folder)
                } else if args[1] == "dismiss" {
                    model.dismissFolderSuggestion(mid)
                }
            }
        // 每一種東西都能直接改、刪（R100、design §74）
        case "renamemeetingui": if let id { model.requestRenameMeeting(id) }   // 等於側欄右鍵「重新命名…」、Return
        case "folderdeleteui": model.confirmDeleteFolder = args[1]              // 等於資料夾的「刪除資料夾…」、Delete，打開確認
        // 側欄的收合（R101、design §75）
        case "sidebarsection": if args.count > 2 { model.setSection(args[1], expanded: args[2] == "on") }   // sidebarsection <folders|meetings|people|group:…> on|off
        case "sidebarexpand": if args.count > 2 { model.setRow(args[1], expanded: args[2] == "on") }     // sidebarexpand <folder:名稱> on|off
        case "sidebarkey":   // sidebarkey return|delete：等於在側欄按 Return、Delete（照現在選到的）
            if args[1] == "return" { _ = model.sidebarReturn() } else { model.sidebarDelete() }
        case "noteitem":   // noteitem <editui|edit|delete> <summary|decisions|actionItems|openQuestions|topic|custom|person> <位置> [第二個位置] [文字]
            let a = Int(args.count > 3 ? args[3] : "") ?? 0
            let twoLevel = ["topic", "custom", "person"].contains(args[2])
            let b = twoLevel ? Int(args.count > 4 ? args[4] : "") ?? 0 : 0
            let ref: NoteItemRef? = switch args[2] {
            case "summary": .summary(a)
            case "decisions": .decisions(a)
            case "actionItems": .actionItems(a)
            case "openQuestions": .openQuestions(a)
            case "topic": .topicPoint(a, b)
            case "custom": .custom(a, b)
            case "person": .person(a, b)
            default: nil
            }
            let textIndex = twoLevel ? 5 : 4
            if let ref {
                switch args[1] {
                case "editui": model.editingNoteItem = ref
                case "edit": model.editNoteItem(ref, text: args.count > textIndex ? args[textIndex] : "")
                case "delete": model.deleteNoteItem(ref)
                default: model.editingNoteItem = nil
                }
            }
        case "bookmarkrename": if let id { model.renameBookmark(id, label: args[2]) }
        case "bookmarkrenameui": if let id { model.renameTarget = .bookmark(id) }
        case "menupick": if args.count > 1 { pickNextMenu(args.dropFirst().joined(separator: " ")) }   // menupick <項目>：接下來打開的選單選這一項
        // 自己畫的選單（R105）：floatpick <項目>（子選單「移到資料夾 ▸ 客戶」）選開著的那一項；floatclose 關掉
        case "floatpick": if args.count > 1 { FloatingMenuController.shared.debugPick(args.dropFirst().joined(separator: " ")) }
        case "floatclose": FloatingMenuController.shared.close()
        case "floatothers": FloatingMenuController.shared.ignoreOtherApps = args.count > 1 && args[1] == "off"   // floatothers off：點別的 app 不關選單
        case "foldernew": if args.count > 1 { model.createFolder(args[1]) }   // 等於按「資料夾」標題的「+」建立（R104）
        case "importinto": if args.count > 2 { model.importDropped([URL(fileURLWithPath: args[2])], into: args[1]) }   // 等於從 Finder 把檔案拖到資料夾（R103.3）
        // 重新命名的彈窗（R102、design §76）：renameui <meeting|folder|person|bookmark|newfolder> <id、名稱或會議 id,…>
        case "renameui":
            guard args.count > 2 else { break }
            switch args[1] {
            case "meeting": if let i = Int64(args[2]) { model.renameTarget = .meeting(i) }
            case "folder": model.renameTarget = .folder(args[2])
            case "person": if let i = Int64(args[2]) { model.renameTarget = .person(i) }
            case "bookmark": if let i = Int64(args[2]) { model.renameTarget = .bookmark(i) }
            case "newfolder": model.renameTarget = .newFolder(args[2].split(separator: ",").compactMap { Int64($0) })
            default: break
            }
        case "termrename": if let id { model.renameTerm(id, to: args[2]) }
        // 會議列表的排序、分組、篩選（design §71）
        case "listsort": if let sort = MeetingSort(rawValue: args[1]) { model.meetingSort = sort }
        case "listgroup": if let grouping = MeetingGrouping(rawValue: args[1]) { model.meetingGrouping = grouping }
        case "listfilter":
            switch args[1] {
            case "review": model.meetingFilter.needsReview.toggle()
            case "mine": model.meetingFilter.myActionItems.toggle()
            case "person": model.meetingFilter.personId = args.count > 2 ? Int64(args[2]) : nil
            case "template": model.meetingFilter.templateId = args.count > 2 ? args[2] : nil
            default: model.meetingFilter = MeetingFilter()
            }
        case "undo": model.selection = id; model.undoNotes()
        case "trash": if let id { model.moveToTrash(id) }
        case "restore": if let id { model.restore(id) }
        case "purge": if let id { model.deletePermanently(id) }
        case "confirmpurge": model.confirmPurge = id.map { [$0] }   // 等於在垃圾桶按右鍵「永久刪除…」
        // 多選（R88、design §62）：selectmany <id,id,…>（等於 ⌘ 點選）；trashmany、restoremany、purgemany <id,id,…>；
        // showtrash 0|1；confirmemptytrash（跳出確認）；emptytrash（不經過確認）
        case "selectmany": model.listSelection = Set(args[1].split(separator: ",").compactMap { Int64($0) })
        case "trashmany": model.moveToTrash(args[1].split(separator: ",").compactMap { Int64($0) })
        case "restoremany": model.restore(args[1].split(separator: ",").compactMap { Int64($0) })
        case "purgemany": model.deletePermanently(args[1].split(separator: ",").compactMap { Int64($0) })
        case "showtrash": model.showTrash = args.count > 1 && args[1] == "1"
        case "confirmemptytrash": model.confirmEmptyTrash = true
        case "emptytrash": model.emptyTrash()
        // 刪除一句（R89、design §63）：deletesegment <會議 id> <段落 id>；分割會議（R90、§64）：splitmeeting <會議 id> <段落 id>（不經過確認），
        // splitconfirm <會議 id> <段落 id>（跳出確認對話框，等於右鍵「從這裡分成兩場…」）
        case "deletesegment": model.selection = id; model.deleteSegment(Int64(args[2]) ?? 0)
        case "splitmeeting": model.selection = id; model.splitMeeting(at: Int64(args[2]) ?? 0)
        case "splitconfirm": model.selection = id; model.tab = .transcript; model.splitMeetingRequest = Int64(args[2])
        // 編輯會議（R107、design §81.6）：editmeeting <會議 id> keep=<開始>-<結束>[,…] [split=<秒>] [merge=<會議 id>[:<開始>-<結束>,…]]
        // （不經過畫面和確認；merge 沒給範圍是整場，照會議開始的先後接）；restoreedit <會議 id>（不經過確認）、confirmrestoreedit <會議 id>；
        // editage <會議 id> <天數>（把最後編輯的時間改早，接著清一次過期的）；openedit <會議 id>（打開編輯畫面）
        case "editmeeting":
            guard let id, let meeting = try? model.db.meeting(id) else { break }
            func ranges(_ text: Substring) -> [(Double, Double)] {
                text.split(separator: ",").compactMap { r in
                    let p = r.split(separator: "-").compactMap { Double($0) }
                    return p.count == 2 ? (p[0], p[1]) : nil
                }
            }
            func whole(_ m: Meeting) -> [(Double, Double)] {
                [(0, max(Double(m.durationSeconds ?? 0), ((try? model.db.segments(m.id!)) ?? []).map(\.end).max() ?? 0))]
            }
            var mine = whole(meeting)
            var cut: Double?
            var merged: (meeting: Meeting, ranges: [(Double, Double)])?
            for arg in args.dropFirst(2) {
                let value = arg.split(separator: "=", maxSplits: 1).last ?? ""
                if arg.hasPrefix("keep=") { mine = ranges(value) }
                else if arg.hasPrefix("split=") { cut = Double(value) }
                else if arg.hasPrefix("merge="), let other = Int64(value.split(separator: ":")[0]), let m = try? model.db.meeting(other) {
                    let parts = value.split(separator: ":", maxSplits: 1)
                    merged = (m, parts.count > 1 ? ranges(parts[1]) : whole(m))
                }
            }
            var pieces = mine.map { EditPlan.Piece(meetingId: id, start: $0.0, end: $0.1) }
            if let merged {
                let theirs = merged.ranges.map { EditPlan.Piece(meetingId: merged.meeting.id!, start: $0.0, end: $0.1) }
                pieces = merged.meeting.createdAt < meeting.createdAt ? theirs + pieces : pieces + theirs
            }
            model.applyMeetingEdit(id, plan: EditPlan(pieces: pieces), cut: cut)
        case "restoreedit": if let id { model.restoreMeetingEdit(id) }
        case "confirmrestoreedit": if let id { model.requestRestoreEdit(id) }
        case "editage": if let id { try? model.db.ageEdit(id, days: Double(args[2]) ?? 31); model.purgeExpiredEdits() }
        case "openedit": if let id { model.requestEditMeeting(id) }
        // editorstate start=<秒> end=<秒> del=<a-b,…> sel=<a-b|none> cut=<秒|none> playhead=<秒> zoom=<倍> merge=<會議 id|none> play lines=<id,…>：
        // 直接改打開的編輯畫面（背景的視窗收不到點擊，截圖、驗畫面用）
        case "editorstate":
            guard let editor = MeetingEditor.debugCurrent else { break }
            func range(_ text: Substring) -> ClosedRange<Double>? {
                let p = text.split(separator: "-").compactMap { Double($0) }
                return p.count == 2 ? min(p[0], p[1])...max(p[0], p[1]) : nil
            }
            for arg in args.dropFirst() {
                let parts = arg.split(separator: "=", maxSplits: 1)
                let key = parts[0], value = parts.count > 1 ? parts[1] : ""
                switch key {
                case "start": editor.debugSet(start: Double(value), end: nil, deleted: nil, selection: nil, cut: nil)
                case "end": editor.debugSet(start: nil, end: Double(value), deleted: nil, selection: nil, cut: nil)
                case "del": editor.debugSet(start: nil, end: nil, deleted: value.split(separator: ",").compactMap(range), selection: nil, cut: nil)
                case "sel": editor.debugSet(start: nil, end: nil, deleted: nil, selection: .some(range(value)), cut: nil)
                case "cut": editor.debugSet(start: nil, end: nil, deleted: nil, selection: nil, cut: .some(Double(value)))
                case "playhead": editor.playhead = Double(value) ?? 0
                case "zoom": editor.zoom = Double(value) ?? 1
                case "lines": editor.selectedLines = Set(value.split(separator: ",").compactMap { Int64($0) })
                case "merge":
                    if let other = Int64(value), let m = try? model.db.meeting(other) { editor.merge(with: m, db: model.db) }
                    else { editor.unmerge(db: model.db) }
                case "play": editor.togglePlay()
                default: break
                }
            }
        case "reviewexpand":
            // reviewexpand <會議 id> <summary|decisions|actionItems|openQuestions|topic|custom|person> <位置> [第二個位置]：
            // 等於點那一條紀錄旁的待確認標記（R41.2）；位置給 - 就收合。視窗不在目前的桌面時 press 按不到標記，用這個
            model.selection = id
            let a = Int(args.count > 3 ? args[3] : "") ?? 0, b = Int(args.count > 4 ? args[4] : "") ?? 0
            let ref: NoteItemRef? = switch args[2] {
            case "summary": .summary(a)
            case "decisions": .decisions(a)
            case "actionItems": .actionItems(a)
            case "openQuestions": .openQuestions(a)
            case "topic": .topicPoint(a, b)
            case "custom": .custom(a, b)
            case "person": .person(a, b)
            default: nil
            }
            model.reviewDraft?.expanded = ref
        case "reviewsubmit":   // reviewsubmit <會議 id> [段落 位置 [第二個位置]]：等於按待確認分頁的「送出」；給了段落就是按那一條卡片的「送出」（只送那幾題）
            model.selection = id
            if args.count > 3 {
                let a = Int(args[3]) ?? 0, b = Int(args.count > 4 ? args[4] : "") ?? 0
                let ref: NoteItemRef? = switch args[2] {
                case "summary": .summary(a)
                case "decisions": .decisions(a)
                case "actionItems": .actionItems(a)
                case "openQuestions": .openQuestions(a)
                case "topic": .topicPoint(a, b)
                case "custom": .custom(a, b)
                case "person": .person(a, b)
                default: nil
                }
                model.submitReviewDraft(only: Set(ref.flatMap { model.detail?.reviewLinks[$0] } ?? []))
            } else {
                model.submitReviewDraft()
            }
        case "reviewskipall":   // reviewskipall <會議 id>：等於按待確認分頁的「全部略過」（R41.5）
            model.selection = id
            model.reviewDraft?.skipAllUnanswered(questionCount: model.detail?.notes?.reviewItems.count ?? 0,
                                                 speakers: model.detail.map { Array($0.speakerSuggestions.keys) } ?? [])
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
            let current = RecordingIsland.isEnabled ? RecordingIsland.shared.state.prompt?.prompt : MeetingPromptPanel.shared.current
            let text: String
            switch current {
            case .start(let app): text = "start:\(app.bundleID)"
            case .stop(let app): text = "stop:\(app.bundleID)"
            case nil:
                // 會議的提醒以外，開著的是定時停止的倒數（R110.3）
                let stopTimer = RecordingIsland.isEnabled ? RecordingIsland.shared.state.prompt != nil : MeetingPromptPanel.shared.showsStopTimer
                text = stopTimer ? "stoptimer" : ""
            }
            let actions = RecordingIsland.isEnabled ? (RecordingIsland.shared.state.prompt?.actions.map(\.id) ?? []).sorted()
                : MeetingPromptPanel.shared.actions.keys.sorted()
            write(["prompt": text, "actions": actions, "muted": MeetingDetector.muted,
                   "countdown": model.meetingEndDeadline.map { max(0, $0.timeIntervalSinceNow) } ?? -1,
                   "recording": model.recorder.isRecording], to: args[1])
        case "meetingaction": RecordingIsland.isEnabled ? RecordingIsland.shared.perform(args[1]) : MeetingPromptPanel.shared.perform(args[1])
        case "island":   // island hover on|off：等於滑鼠移到錄音島上、移開（design §57.3）
            if args.count > 2, args[1] == "hover" { RecordingIsland.shared.state.hover = args[2] == "on"; RecordingIsland.shared.update() }
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
        case "splitmode":   // splitmode <會議 id> <段落 id>：逐字稿進入拆成兩句的模式（等於右鍵「拆成兩句…」）
            model.selection = id
            model.tab = .transcript
            model.splitRequest = Int64(args[2])
        case "splitcaret":   // splitcaret <UTF-16 位置>：拆開模式的文字框把游標放到這裡（背景的視窗點不到文字框，design §46.2）
            func find(_ view: NSView) -> NSTextView? {
                if let text = view as? NSTextView, text.accessibilityLabel() == String(localized: "要拆開的句子") { return text }
                return view.subviews.lazy.compactMap(find).first
            }
            if let content = NSApp.windows.first(where: { $0.identifier?.rawValue == "main" })?.contentView, let text = find(content) {
                text.setSelectedRange(NSRange(location: Int(args[1]) ?? 0, length: 0))
            }
        case "splitsegment":   // splitsegment <會議 id> <段落 id> <UTF-16 位置> <說話者|new>：拆成兩句（design §46.2）
            model.selection = id
            model.splitSegment(Int64(args[2]) ?? 0, at: Int(args[3]) ?? 0, to: args[4] == "new" ? nil : Int(args[4]))
        case "speakerhint": model.selection = id; if args.count > 2, args[2] == "dismiss" { model.dismissSpeakerHint() }
        case "minspeakers": model.selection = id; model.setMinSpeakers(args[2] == "auto" ? nil : Int(args[2]))   // 錄音中的「人數」選單（design §26.2）
        case "levelhint": if args.count > 1, args[1] == "dismiss" { model.recorder.dismissLevelHint() }   // 收音提醒按「知道了」
        // 麥克風（design §38.7）：fakemic <名稱> <bluetooth 0|1> [內建麥克風的名稱]｜off：加假的麥克風（只在記憶體），
        // 第一支當成系統預設；micswitch <uid|default>：等於在錄音畫面選一支；michint dismiss：藍牙提示按「知道了」
        case "fakemic":
            let monitor = MicrophoneMonitor.shared
            if args.count < 3 || args[1] == "off" {
                monitor.fakeDefault = nil
                monitor.fakes = []
            } else {
                var fakes = [AudioDevices.Device(uid: "fake-mic-1", name: args[1],
                                                 transport: args[2] == "1" ? kAudioDeviceTransportTypeBluetooth : kAudioDeviceTransportTypeUSB)]
                if args.count > 3 { fakes.append(AudioDevices.Device(uid: "fake-mic-builtin", name: args[3], transport: kAudioDeviceTransportTypeBuiltIn)) }
                monitor.fakes = fakes
                monitor.fakeDefault = "fake-mic-1"
            }
        case "micswitch": model.switchMicrophone(args[1] == "default" ? "" : args[1])
        case "michint": if args.count > 1, args[1] == "dismiss" { model.recorder.dismissMicHint() }
        // textsize <−1～5|bigger|smaller|actual>：會議內容的文字大小（design §38.7；寫共用設定，測完 defaults delete contentTextSize）
        case "textsize":
            switch args[1] {
            case "bigger": TextSize.change(1)
            case "smaller": TextSize.change(-1)
            case "actual": TextSize.change(nil)
            default: UserDefaults.standard.set(Int(args[1]) ?? 0, forKey: AppSettings.textSizeKey)
            }
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
        case "personremove": model.confirmRemovePerson = id   // 等於選了「移除這個人…」（R85.7），打開確認
        case "personrenameui":   // 等於選了「改名…」（更多、側欄右鍵，R85.7）：選那個人、打開鉛筆下面的輸入框
            model.sidebarMode = .people
            model.selectedPerson = id
            if let id { model.renameTarget = .person(id) }
        case "peopledeleteall": model.deleteAllPeople()
        case "template": model.selection = id; model.setTemplate(args[2])
        case "language": model.selection = id; model.setMeetingLanguage(MeetingLanguage(rawValue: args[2]) ?? .zh)
        case "transcribe-eval": transcribeEval(audio: args[1], options: args[2], output: args[3])   // design §17.1
        case "dump-golden": GoldenDump.run(fixtures: args[1], output: args[2])   // design-windows §4.1
        case "seedfixtures": GoldenDump.seed(fixtures: args[1], keys: Array(args.dropFirst(2)), into: model)   // 本地 MCP 的示範會議（design §44.6）
        case "mcpsetup":   // mcpsetup claudecode|codex|desktop <輸出.json>：等於按設定頁的「加到…」（design §44.6）
            let output = args.count > 2 ? args[2] : nil
            Task {
                let r: MCPSetup.Outcome
                switch args.count > 1 ? args[1] : "" {
                case "claudecode": r = await MCPSetup.addToClaudeCode()
                case "codex": r = await MCPSetup.addToCodex()
                default: r = MCPSetup.addToClaudeDesktop()
                }
                if let output { write(["ok": r.ok, "message": r.message], to: output) }
            }
        case "mcpcopy":   // mcpcopy command|json：等於按設定頁「複製」選單的「複製 Claude Code 指令」「複製設定（JSON）」（design §44.6）
            MCPSetup.copy(args.count > 1 && args[1] == "json" ? .configJSON : .claudeCodeCommand)
        case "unquarantine":   // unquarantine <app 的路徑> <輸出.json>：對那個 bundle 跑拿掉隔離屬性（D150）
            write(["removed": MCPSetup.removeQuarantine(in: URL(fileURLWithPath: args[1]))], to: args[2])
        case "openlink":   // openlink <shiyin://open?…>：等於點了 AI 工具給的會議連結（design §44.5），不拉到前面
            if let url = URL(string: args[1]), let target = MeetingLink.parse(url) {
                model.openMeetingLink(target.meetingId, seconds: target.seconds, activate: false)
            }
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
        case "topichover": model.debugTopicHover = args.count > 1 ? Int(args[1]) : nil   // topichover <第幾段|off>：當成滑過議題帶的那一段（design §87）
        case "notesvalue": write(["found": notesTextView() != nil, "text": notesTextView()?.string ?? ""], to: args[1])
        // fieldtype <提示字> <文字>：提示字相符的單行輸入框（例如「搜尋逐字稿」）換成這段文字。走欄位編輯器的 insertText，
        // 不經過輸入法（逐字稿搜尋的上一個、下一個，design §85）
        case "fieldtype":
            func find(_ view: NSView) -> NSTextField? {
                if let field = view as? NSTextField, field.isEditable, field.placeholderString == args[1] { return field }
                return view.subviews.lazy.compactMap(find).first
            }
            guard args.count > 2, let field = NSApp.windows.lazy.filter(\.isVisible).compactMap({ $0.contentView.flatMap(find) }).first,
                  field.window?.makeFirstResponder(field) == true, let editor = field.currentEditor() as? NSTextView else { break }
            editor.selectAll(nil)
            editor.insertText(args[2], replacementRange: editor.selectedRange())
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
        // model：standard、nemotron（R87，design §61.2；沒寫時用標準的）
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
                        let diarization = try await Diarizer.diarize(samples: samples, minSpeakers: item["minSpeakers"].flatMap(Int.init),
                                                                     clustering: clustering, config: config,
                                                                     model: Diarizer.Model(rawValue: item["model"] ?? "") ?? .standard) { _ in }
                        // dump：<路徑>.f32 是每個片段的聲紋（256 個 Float32 一筆），<路徑>.json 是對應的群、片段、片段裡的說話者
                        if let dump = item["dump"] {
                            var floats: [Float] = []
                            for chunk in diarization.chunks { floats += chunk.embedding256 }
                            try floats.withUnsafeBytes { Data($0) }.write(to: URL(fileURLWithPath: dump + ".f32"))
                            write(["cluster": diarization.chunks.map(\.speakerId), "chunk": diarization.chunks.map(\.chunkIndex),
                                   "local": diarization.chunks.map(\.speakerIndex), "start": diarization.chunks.map(\.startTimeSeconds),
                                   "end": diarization.chunks.map(\.endTimeSeconds)], to: dump + ".json")
                        }
                        // Nemotron-3 的說話者不一定有聲紋（§61.2）：說過話的都列
                        let numbers = Set(diarization.turns.map(\.speaker)).union(diarization.voiceprints.keys).sorted()
                        let speakers: [[String: Any]] = numbers.map { number in
                            ["number": number, "seconds": diarization.speechSeconds[number] ?? 0,
                             "embedding": diarization.voiceprints[number] ?? [],
                             "turns": diarization.turns.filter { $0.speaker == number }.map { [$0.start, $0.end] }]
                        }
                        results.append(["name": item["name"] ?? file, "seconds": Double(samples.count) / AudioDecoder.sampleRate,
                                        "model": diarization.model.rawValue,
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
        case "windowsize":   // windowsize <寬> <高>：把主視窗的內容區設成這個大小（點）。官網截圖用（website.md §16）：視窗在別的桌面時 AX 改不了大小，screencapture -l 照樣截得到
            if let w = Double(args[1]), let h = Double(args[2]),
               let window = NSApp.windows.first(where: { $0.isVisible && $0.identifier?.rawValue == "main" }) ?? NSApp.windows.first(where: { $0.isVisible && $0.frame.width > 400 }) {
                window.setContentSize(NSSize(width: w, height: h))
            }
        case "summon":   // summon：把拾音的視窗移到目前的桌面、放到最前面（真的畫面上用輔助使用測試時，視窗在別的桌面會按不到）
            for window in NSApp.windows where window.isVisible && window.frame.width > 400 {
                // preview 的視窗本來就在每個桌面：再加 moveToActiveSpace 會被 AppKit 丟例外、整個 app 當掉（2026-10-29）
                if window.collectionBehavior.contains(.canJoinAllSpaces) {
                    window.makeKeyAndOrderFront(nil)
                    continue
                }
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
        // clickat <x> <y> [視窗 id]：在視窗裡點一下（點，左上原點，含標題列；預設主視窗）。直接交給 NSWindow.sendEvent，
        // 走 AppKit、SwiftUI 正常的點擊判斷，視窗在別的桌面（例如使用者在全螢幕的 app）也點得到，不碰使用者的滑鼠
        case "clickat":
            // 視窗 id 或視窗編號（確認對話框的 sheet 沒有 id，用 screencapture 看到的編號）
            let target = args.count > 3 ? args[3] : "main"
            guard let x = Double(args[1]), let y = Double(args[2]),
                  let window = NSApp.windows.first(where: { $0.isVisible && ($0.identifier?.rawValue == target || "\($0.windowNumber)" == target) })
            else { break }
            let point = NSPoint(x: x, y: window.frame.height - y)
            for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
                if let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                                  windowNumber: window.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1) {
                    window.sendEvent(event)
                }
            }
        // mouseat <left|right|double|move> <x> <y> <視窗 id 或編號> [選單輸出.json]：同 clickat，直接交給 NSWindow.sendEvent，
        // 不碰使用者的滑鼠、不用切到那個桌面（R100 自己驗收）。給了輸出檔時，接下來打開的選單（右鍵、「⋯」）把項目寫出來再關掉
        case "mouseat":
            guard args.count > 4, let x = Double(args[2]), let y = Double(args[3]),
                  let window = NSApp.windows.first(where: { $0.isVisible && ($0.identifier?.rawValue == args[4] || "\($0.windowNumber)" == args[4]) })
            else { break }
            // 最後一個參數 app：走 NSApp.sendEvent（經過 app 的事件監看、NSApp.currentEvent 是這一下；自己畫的選單的右鍵靠它）
            let viaApp = args.last == "app"
            if args.count > 5, args[5] != "app" { captureNextMenu(to: args[5], keep: args.count > 6 ? Double(args[6]) ?? 0 : 0) }
            let point = NSPoint(x: x, y: window.frame.height - y)
            func deliver(_ event: NSEvent) { if viaApp { NSApp.sendEvent(event) } else { window.sendEvent(event) } }
            func send(_ type: NSEvent.EventType, clicks: Int) {
                if let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                                  windowNumber: window.windowNumber, context: nil, eventNumber: 0, clickCount: clicks, pressure: 1) {
                    deliver(event)
                }
            }
            // 從 CGEvent 產生：帶按鍵編號（右鍵是 1；NSEvent.mouseEvent 做的右鍵按鍵編號是 0，SwiftUI 會當成左鍵點擊）。
            // NSEvent(cgEvent:) 沒有視窗時 locationInWindow 是螢幕座標，所以把位置放成「數值等於視窗座標」，交給 sendEvent 時落在同一點
            func sendCG(_ type: CGEventType, _ button: CGMouseButton, clicks: Int64) {
                let height = NSScreen.screens.first?.frame.height ?? 0
                guard let cg = CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: CGPoint(x: point.x, y: height - point.y),
                                       mouseButton: button) else { return }
                cg.setIntegerValueField(.mouseEventClickState, value: clicks)
                if let event = NSEvent(cgEvent: cg) { deliver(event) }
            }
            switch args[1] {
            case "rightq":
                // 右鍵的按下、放開排進事件佇列（經過 app 的事件監看，自己畫的選單的右鍵靠它）
                for type in [NSEvent.EventType.rightMouseDown, .rightMouseUp] {
                    if let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                                      windowNumber: window.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1) {
                        NSApp.postEvent(event, atStart: false)
                    }
                }
            case "clickq", "clickqcmd", "clickqshift":
                // 按下、放開都排進事件佇列，同真的點一下：經過手勢辨識、NSApp.currentEvent 是這一下，
                // 表格按下後自己從佇列拿放開（直接 sendEvent 的話這些都不一樣，2026-11-17 驗側欄點擊卡住時發現）
                let flags: NSEvent.ModifierFlags = args[1] == "clickqcmd" ? .command : args[1] == "clickqshift" ? .shift : []
                for (type, pressure) in [(NSEvent.EventType.leftMouseDown, Float(1)), (.leftMouseUp, 0)] {
                    if let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: flags, timestamp: ProcessInfo.processInfo.systemUptime,
                                                      windowNumber: window.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: pressure) {
                        NSApp.postEvent(event, atStart: false)
                    }
                }
            case "dragq":
                // mouseat dragq <x> <y> <視窗> <x2> <y2>：按下、分 12 步拖到 (x2, y2)、放開，都排進事件佇列（SwiftUI 的拖曳手勢；
                // 不是拖放：beginDraggingSession 讀真的游標，拖放只能用真的滑鼠）
                guard args.count > 6, let x2 = Double(args[5]), let y2 = Double(args[6]) else { break }
                let end = NSPoint(x: x2, y: window.frame.height - y2)
                let steps = 12
                var events: [(NSEvent.EventType, NSPoint, Float)] = [(.leftMouseDown, point, 1)]
                for i in 1...steps {
                    let f = CGFloat(i) / CGFloat(steps)
                    events.append((.leftMouseDragged, NSPoint(x: point.x + (end.x - point.x) * f, y: point.y + (end.y - point.y) * f), 1))
                }
                events.append((.leftMouseUp, end, 0))
                for (type, location, pressure) in events {
                    if let event = NSEvent.mouseEvent(with: type, location: location, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                                      windowNumber: window.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: pressure) {
                        NSApp.postEvent(event, atStart: false)
                    }
                }
            case "right": send(.rightMouseDown, clicks: 1); send(.rightMouseUp, clicks: 1)
            case "cgright": sendCG(.rightMouseDown, .right, clicks: 1); sendCG(.rightMouseUp, .right, clicks: 1)
            case "cgleft": sendCG(.leftMouseDown, .left, clicks: 1); sendCG(.leftMouseUp, .left, clicks: 1)
            case "cgdouble":
                sendCG(.leftMouseDown, .left, clicks: 1); sendCG(.leftMouseUp, .left, clicks: 1)
                sendCG(.leftMouseDown, .left, clicks: 2); sendCG(.leftMouseUp, .left, clicks: 2)
            case "double": send(.leftMouseDown, clicks: 1); send(.leftMouseUp, clicks: 1); send(.leftMouseDown, clicks: 2); send(.leftMouseUp, clicks: 2)
            case "move": send(.mouseMoved, clicks: 0)
            default: send(.leftMouseDown, clicks: 1); send(.leftMouseUp, clicks: 1)
            }
        case "eventlog":   // eventlog <輸出檔|off>：把收到的滑鼠、按鍵事件寫下來（查外面送的事件有沒有進來；三指拖移的事件、拖曳開始了沒）
            if let eventMonitor { NSEvent.removeMonitor(eventMonitor); self.eventMonitor = nil }
            eventLogPath = nil
            if args[1] != "off" {
                let path = args[1]
                FileManager.default.createFile(atPath: path, contents: nil)
                eventLogPath = path
                eventMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .leftMouseUp, .leftMouseDragged, .rightMouseDown, .mouseMoved, .keyDown, .pressure]) { event in
                    let mouse = event.type != .keyDown
                    note("\(event.type.rawValue) win=\(event.windowNumber) loc=\(Int(event.locationInWindow.x)),\(Int(event.locationInWindow.y)) clicks=\(mouse && event.type != .pressure ? event.clickCount : 0) key=\(mouse ? -1 : Int(event.keyCode)) subtype=\(mouse ? Int(event.subtype.rawValue) : -1) pressure=\(mouse ? event.pressure : 0) buttons=\(NSEvent.pressedMouseButtons)")
                    return event
                }
            }
        case "appearance":   // appearance light|dark|system：只改這個 app 的外觀（巡檢淺色、深色用，不動系統設定）
            NSApp.appearance = args[1] == "light" ? NSAppearance(named: .aqua) : args[1] == "dark" ? NSAppearance(named: .darkAqua) : nil
        case "capturemenu": captureNextMenu(to: args[1])   // capturemenu <輸出.json>：接下來打開的選單寫出項目後關掉（配合外面送的真的滑鼠事件）
        // keyat [shift+|cmd+…]<return|delete|escape|f2|…|g> <視窗 id 或編號>：按鍵直接交給那個視窗（給第一回應者，同真的按鍵）
        case "keyat":
            guard args.count > 2, let window = NSApp.windows.first(where: { $0.isVisible && ($0.identifier?.rawValue == args[2] || "\($0.windowNumber)" == args[2]) })
            else { break }
            // 修飾鍵寫在前面：shift+return、cmd+g、cmd+shift+g（逐字稿搜尋的上一個、下一個，design §85）
            var keyName = Substring(args[1])
            var flags: NSEvent.ModifierFlags = []
            while let plus = keyName.firstIndex(of: "+"), plus != keyName.startIndex {
                switch keyName[..<plus] {
                case "shift": flags.insert(.shift)
                case "cmd": flags.insert(.command)
                case "option": flags.insert(.option)
                case "ctrl": flags.insert(.control)
                default: break
                }
                keyName = keyName[keyName.index(after: plus)...]
            }
            let (code, chars): (UInt16, String) = switch keyName {
            case "g": (5, "g")
            case "delete": (51, "\u{7f}")
            case "space": (49, " ")
            case "escape": (53, "\u{1b}")
            case "f2": (120, String(UnicodeScalar(NSF2FunctionKey)!))
            case "down": (125, String(UnicodeScalar(NSDownArrowFunctionKey)!))
            case "up": (126, String(UnicodeScalar(NSUpArrowFunctionKey)!))
            case "right": (124, String(UnicodeScalar(NSRightArrowFunctionKey)!))
            case "left": (123, String(UnicodeScalar(NSLeftArrowFunctionKey)!))
            default: (36, "\r")
            }
            // 第四個參數 app：走 NSApp.sendEvent（會經過 app 的事件監看，自己畫的選單的鍵盤靠它）；
            // queue：排進事件佇列，NSApp.currentEvent 是這一下（同 mouseat clickq；onSubmit 看 ⇧ 要這個）
            let throughApp = args.count > 3 && args[3] == "app"
            let queued = args.count > 3 && args[3] == "queue"
            for type in [NSEvent.EventType.keyDown, .keyUp] {
                if let event = NSEvent.keyEvent(with: type, location: .zero, modifierFlags: flags, timestamp: ProcessInfo.processInfo.systemUptime,
                                                windowNumber: window.windowNumber, context: nil, characters: chars, charactersIgnoringModifiers: chars,
                                                isARepeat: false, keyCode: code) {
                    if queued { NSApp.postEvent(event, atStart: false) } else if throughApp { NSApp.sendEvent(event) } else { window.sendEvent(event) }
                }
            }
        // pressbutton <按鈕的字> [視窗編號]：按 AppKit 的按鈕（確認對話框的 NSAlert 在桌面層時 AX 看不到、clickat 點不到）
        case "pressbutton":
            func find(_ view: NSView) -> NSButton? {
                if let button = view as? NSButton, button.title == args[1], button.isEnabled { return button }
                return view.subviews.lazy.compactMap(find).first
            }
            let windows = NSApp.windows.filter { $0.isVisible && (args.count < 3 || "\($0.windowNumber)" == args[2]) }
            windows.lazy.compactMap { $0.contentView.flatMap(find) }.first?.performClick(nil)
        case "bookmark": model.addBookmark(at: Double(args[2]) ?? 0, meetingId: id)
        case "addterm": model.addTerms(args[1].replacingOccurrences(of: "\\n", with: "\n"))   // 一次好幾個（R94.1）：「\n」當換行
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
        case "recordflap":   // recordflap <次數> [秒數]：麥克風接下來幾次開始收音後都說裝置變了，每次重新開始花幾秒（design §50.4）
            model.recorder.simulateFlapping(times: Int(args[1]) ?? 0, seconds: args.count > 2 ? Double(args[2]) ?? 0 : 0)
        case "recordlang":   // recordlang <語言代碼>：錄音中換會議語言（等於按「改回」；zh、en、ja、yue、ko…）
            if let language = MeetingLanguage(rawValue: args[1]) { model.recorder.switchLanguage(to: language) }
        case "stoptimer":   // stoptimer <秒數|off>：從現在起幾秒後定時停止（design §84）
            model.setStopTimer(args.count > 1 && args[1] != "off" ? Date().addingTimeInterval(Double(args[1]) ?? 0) : nil)
        case "record-sim":
            // record-sim <音檔> <倍速> <both: 1/0/2>：2 是雙聲道、電腦聲音那一聲道全是 0（design §51.1）
            let channels = args.count > 3 ? args[3] : "0"
            model.startSimulatedRecording(file: URL(fileURLWithPath: args[1]), speed: Double(args[2]) ?? 1,
                                          both: channels == "1" || channels == "2", systemSilent: channels == "2")
        case "reminders": model.selection = id; model.addActionItemsToReminders()   // reminders <會議 id>：等於按「加到提醒事項」（design §52.2）
        case "reminderdone":   // reminderdone <會議 id> <第幾項，0 開始>：假的提醒事項裡把那場會議的第幾項設成完成
            let matching = FakeReminders.shared.entries.indices.filter { Reminders.meetingAndKey(FakeReminders.shared.entries[$0].item.url)?.meetingId == id }
            if let n = Int(args[2]), n < matching.count { FakeReminders.shared.entries[matching[n]].completed = true }
            model.refreshReminderStatus()
        case "enrollvoice":   // enrollvoice <音檔> <名字>：用音檔代替麥克風錄「我的聲音」（design §56.3）
            Task {
                do {
                    let samples = try await AudioDecoder.decode(URL(fileURLWithPath: args[1]))
                    try await model.enrollVoice(name: args[2], samples: samples)
                } catch {
                    model.errorMessage = error.localizedDescription
                }
            }
        case "enrolldelete": model.deleteEnrollment()
        case "sidebar":   // sidebar people|meetings：側欄的「會議｜人物」（design §60.2）
            model.sidebarMode = args.count > 1 && args[1] == "people" ? .people : .meetings
            model.reloadPeopleOverview()
        case "person":   // person <人的 id>：側欄切到人物、選這個人
            model.sidebarMode = .people
            model.reloadPeopleOverview()
            model.selectedPerson = id
        case "hidesharing":   // hidesharing on|off：改預設值（只在記憶體，不寫進共用設定；共用設定裡有值時以它為準），套用到所有視窗（design §53）
            UserDefaults.standard.register(defaults: [AppSettings.hideFromScreenSharingKey: args[1] == "on"])
            WindowSharing.applyAll()
        case "systempermission":   // systempermission <denied|granted|notDetermined|real>：蓋掉查到的「錄製系統音訊」權限（design §51.1）
            SystemAudioPermission.debugOverride = SystemAudioPermission(rawValue: args[1])
        case "seek": model.player.seek(to: Double(args[1]) ?? 0, play: args.count > 2 && args[2] == "play")
        case "rate": model.player.setRate(Float(args[1]) ?? 1)
        case "playvolume": model.player.setVolume(Float(args[1]) ?? 1)   // playvolume <0～1>（design §49.3）
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
        // 會議的參考文件（design §39.7）：adddoc <會議 id> <檔案>…（加到這場，同「加入文件…」選了這些檔）；
        // drop <檔案>…（等於拖進主視窗：文件加到選取中的會議，其他照舊匯入）；removedoc <文件 id>；
        // docread <檔案> <zh|en|ja> <輸出.json>（只讀出文字，不寫資料庫）；docbanner <會議 id> dismiss|resummarize（文件有變動的提示條）
        case "adddoc": if let id { model.addDocuments(args.dropFirst(2).map { URL(fileURLWithPath: $0) }, to: id) }
        case "drop": model.requestImport(args.dropFirst().map { URL(fileURLWithPath: $0) })
        // 會議中的畫面（design §83.8）：screenadd <圖檔> [秒數]：同貼上（錄音中用當下時間；給秒數時加到選取的會議的那個時間）
        case "screenadd":
            guard args.count > 1, let image = ScreenImages.load(URL(fileURLWithPath: args[1])) else { break }
            if args.count > 2, let seconds = Double(args[2]), let id = model.selection { model.addScreens([image], at: seconds, to: id) }
            else { model.addScreensHere([image]) }
        case "screenshot": GlobalHotKey.shared.fireScreenshot()   // 同按 ⌃⌥⌘S
        case "screenmove": if args.count > 2, let screen = Int64(args[1]), let seconds = Double(args[2]) { model.moveScreen(screen, to: seconds) }
        case "screendelete": if args.count > 1, let screen = Int64(args[1]) { model.deleteScreen(screen) }
        case "screenpreview":   // screenpreview <id>：放大（Quick Look）；screenpreview off 關掉
            if args.count > 1, args[1] == "off" { model.screenPreview = nil }
            else if args.count > 1, let screen = Int64(args[1]), let document = model.detail?.screens.first(where: { $0.id == screen }) {
                model.previewScreen(document)
            }
        case "screenmenu":   // screenmenu <id> <項目>：選單的某一項（改時間…、刪除…會開彈窗）
            if args.count > 2, let screen = Int64(args[1]), let document = model.detail?.screens.first(where: { $0.id == screen }),
               let entry = model.screenMenu(document).first(where: { $0.title == args[2] }), case .action(let action) = entry.kind { action() }
        case "removedoc":
            if let docId = Int64(args[1]), let document = try? model.db.document(docId) { model.removeDocument(document) }
        case "docread":
            let url = URL(fileURLWithPath: args[1]), language = MeetingLanguage(rawValue: args[2]) ?? .zh, output = args[3]
            Task {
                let started = Date()
                let result = await Task.detached { Result { try DocumentReader.read(url, language: language) } }.value
                switch result {
                case .success(let r):
                    write(["kind": r.kind.rawValue, "pages": r.pages, "ocr": r.ocr, "seconds": Date().timeIntervalSince(started)], to: output)
                case .failure(let error):
                    write(["error": error.localizedDescription], to: output)
                }
            }
        case "docbanner":
            model.selection = id
            if args.count > 2, args[2] == "dismiss" { model.dismissDocumentsChanged() } else { model.resummarize() }
        case "copy":   // copy [plain|md|mindmap]
            if args.count > 1 && args[1] == "mindmap" { model.copyMindMap() }
            else { model.copyNotes(args.count > 1 && args[1] == "plain" ? .plain : args.count > 1 && args[1] == "md" ? .markdown : .rich) }
        case "search": model.searchQuery = args.count > 1 ? args[1] : ""
        case "openhit": if let index = Int(args[1]), model.searchResults.indices.contains(index) { model.openSearchHit(model.searchResults[index]) }
        case "model" where args.count > 2 && args[2] == "nemotron":   // model start|delete nemotron（R87，FluidAudio 下載）
            args[1] == "start" ? model.models.prepareNemotron() : args[1] == "delete" ? model.models.deleteNemotron() : ()
        case "model":   // model start|pause|delete [breeze|japanese|languageDetection|qwen]（qwen 是兩個檔一起）
            let which = ModelStore.Model(rawValue: args.count > 2 ? args[2] : "") ?? .breeze
            args[1] == "start" ? model.models.startAll(which) : args[1] == "pause" ? model.models.pauseAll(which) : model.models.deleteAll(which)
        case "qwenchunks":   // qwenchunks <錄音> <輸出.json> [最長秒數] [any] [mixed]：先辨識說話者再照 §32.4 分段，倒出片段、切點、能量中位數（驗證分段規則）
            qwenChunks(audio: args[1], output: args[2], maxSeconds: args.count > 3 ? Double(args[3]) ?? QwenEngine.maxChunkSeconds : QwenEngine.maxChunkSeconds,
                       anyChange: args.contains("any"), splitMixed: args.contains("mixed"))
        case "s2tw":    // s2tw <輸入.txt> <輸出.txt>：簡體轉台灣繁體（design §32.5），驗證跟原型工具的結果相同
            if let text = try? String(contentsOfFile: args[1], encoding: .utf8) {
                try? ChineseScript.toTaiwan(text).write(toFile: args[2], atomically: true, encoding: .utf8)
            }
        case "diarizer": model.models.prepareDiarizer()
        case "modelurl":   // modelurl <模型> <網址>：改從別的網址下載；網址給 - 就改回原本的
            if let which = ModelStore.Model(rawValue: args[1]) { model.models.debugURLs[which] = args[2] == "-" ? nil : URL(string: args[2]) }
        case "llmurl":     // llmurl <anthropic|openai|gemini|openaiCompatible> <網址>：API 改送到本機的假伺服器；網址給 - 就改回原本的
            if let provider = LLMProvider(rawValue: args[1]) { LLMClient.debugBaseURLs[provider] = args[2] == "-" ? nil : args[2] }
        case "enterprise":
            // 企業伺服器（design §36.7）：login <網址> <信箱> <輸出.json>（伺服器開發用的登入，不經過瀏覽器）、logout、expire（access token 立刻過期）、
            // local <會議 id>（等於按「改在這台電腦轉錄」）。登入會寫進共用的設定（網址、帳號），測完用 defaults delete 清掉
            switch args[1] {
            case "login":
                let output = args[4]
                Task {
                    do {
                        try await EnterpriseServer.shared.debugDevSignIn(args[2], email: args[3])
                        write(["ok": true, "state": EnterpriseServer.shared.debugState], to: output)
                    } catch {
                        write(["ok": false, "error": error.localizedDescription], to: output)
                    }
                    model.refreshEnvironment()
                }
            case "logout": Task { await EnterpriseServer.shared.signOut(); model.refreshEnvironment() }
            case "expire": EnterpriseServer.shared.debugExpireAccess()
            case "glossary": Task { await EnterpriseServer.shared.refreshGlossary() }   // 重新拿公司詞彙表（R53）
            case "join":   // join <shiyin://join?server=…>：等於點了加入連結（會跳出確認，出現在使用者畫面上）
                if let url = URL(string: args[2]) { Task { await EnterpriseJoin.handle(url) } }
            case "local": model.runner.transcribeLocally(meetingId: Int64(args[2]) ?? -1)
            case "info":   // info <網址> <輸出.json>：只讀伺服器的 /api/v1/info、寫出解析結果，不改帳號和設定（本地 MCP 的 mcp 欄位，design §44.6）
                let output = args[3]
                Task {
                    guard let url = EnterpriseServer.normalized(args[2]) else { write(["ok": false, "error": "bad url"], to: output); return }
                    do {
                        let info = try await EnterpriseServer.shared.fetchInfo(url)
                        write(["ok": true, "name": info.name, "llm": info.llm, "mcp": info.mcp.map { $0 as Any } ?? NSNull()], to: output)
                    } catch {
                        write(["ok": false, "error": error.localizedDescription], to: output)
                    }
                }
            default: break
            }
        case "llmkey":   // llmkey <account> <key>：只放記憶體
            Keychain.debugKeys[args[1]] = args[2] == "-" ? nil : args[2]
            Keychain.debugForget(args[1])
            Keychain.generation += 1   // 等於存了新的 key：解除暫停（design §34.4）
            model.refreshSummarizerProblem()
        case "keychainsim":   // keychainsim <account> denied|duplicate|-：模擬鑰匙圈回「拒絕」「已經有舊的」（design §22.1）
            Keychain.debugSimulated[args[1]] = args[2] == "-" ? nil : args[2]
            Keychain.debugForget(args[1])
        case "keychainreads": write(["reads": Keychain.debugReads], to: args[1])   // keychainreads <輸出.json>：查鑰匙圈的次數
        case "llmprovider":   // llmprovider <整理工具> [模型]：只放記憶體，不改共用的設定；給 - 改回設定
            AppSettings.debugProvider = args[1] == "-" ? nil : LLMProvider(rawValue: args[1])
            AppSettings.debugModel = args.count > 2 ? args[2] : nil
            model.refreshSummarizerProblem()
        case "summarizer":   // summarizer <輸出.json>：整理工具準備好了沒、登入快取、暫停（design §34.6）
            let problem = AppSettings.summarizerProblem
            write(["provider": AppSettings.provider.rawValue,
                   "ready": problem == nil,
                   "problem": problem.map { String(describing: $0) } ?? "",
                   "message": problem?.message ?? "",
                   "action": problem?.actionTitle ?? "",
                   "shown": model.summarizerProblem.map { String(describing: $0) } ?? "",
                   "loggedIn": ["claude": CLILoginCache.loggedIn(.claude).map { $0 ? "yes" : "no" } ?? "unknown",
                                "codex": CLILoginCache.loggedIn(.codex).map { $0 ? "yes" : "no" } ?? "unknown"]], to: args[1])
        case "loginrefresh":   // loginrefresh：重新查 claude、codex 的登入狀態
            Task {
                await CLILogin.refresh(.claude)
                await CLILogin.refresh(.codex)
            }
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
        case "quitprompt":   // quitprompt <輸出.json>：現在結束拾音的話會問什麼（不真的結束；design §12 的下載中提醒）
            let prompt = AppDelegate.terminationPrompt()
            write(["prompt": prompt != nil, "title": prompt?.title ?? "", "info": prompt?.info ?? ""], to: args[1])
        case "installupdate":   // 等於在設定頁按「下載並更新到」：install 自己會收起主視窗的詢問（開著時 app 結束不了，替換會被取消）
            if case .available(let manifest) = model.updater.state {
                Task { await model.updater.install(manifest, model: model) }
            }
        case "dismissupdate": model.updater.prompt = nil
        // 匿名使用統計（design §30.7）：DEBUG 版只在這裡設了網址才累計、送出
        case "usageurl":   // usageurl <網址|off>：只在記憶體設統計網址
            UsageStats.debugURL = args.count > 1 && args[1] != "off" ? args[1] : nil
            UsageStats.shared.touch()
        case "usage": write(UsageStats.shared.debugSnapshot(), to: args[1])   // usage <輸出.json>：目前的檔案和下一份要送的
        case "usageage": UsageStats.shared.debugAge(days: Int(args[1]) ?? 1)   // usageage <天數>：把今天的匯總改成幾天前
        case "usagesend": Task { await UsageStats.shared.sendPending() }
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
        case "windowlevel":   // windowlevel <視窗 id 開頭> desktop|normal：放到桌面層（被所有視窗蓋住，screencapture -l 照樣截得到），不出現在使用者的畫面上
            let level = args.count > 2 && args[2] == "desktop" ? NSWindow.Level(Int(CGWindowLevelForKey(.desktopWindow))) : .normal
            NSApp.windows.filter { $0.identifier?.rawValue.hasPrefix(args[1]) == true }.forEach { $0.level = level }
        // windowshot <視窗 id 或編號> <輸出.png>：app 自己把視窗的內容畫成圖（螢幕上鎖、視窗在桌面層或別的桌面也畫得出來，
        // 輸入框這些 AppKit 元件照樣畫；snapshot 的 ImageRenderer 畫不出來）
        case "windowshot":
            guard args.count > 2, let window = NSApp.windows.first(where: { $0.identifier?.rawValue == args[1] || "\($0.windowNumber)" == args[1] }),
                  let view = window.contentView, let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { break }
            view.cacheDisplay(in: view.bounds, to: rep)
            // 畫出來的內容沒有視窗的底色（材質、背景是視窗伺服器畫的），先鋪上視窗的底色再疊上去
            let output = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: rep.pixelsWide, pixelsHigh: rep.pixelsHigh,
                                          bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                          colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
            output.size = view.bounds.size
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: output)
            window.effectiveAppearance.performAsCurrentDrawingAppearance {
                NSColor.windowBackgroundColor.setFill()
                view.bounds.fill()
            }
            rep.draw(in: view.bounds, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
            NSGraphicsContext.restoreGraphicsState()
            try? output.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: args[2]))
        case "exportui": model.openExportSheet()   // 等於按工具列的「匯出」
        case "togglesidebar":   // togglesidebar [show|hide]：等於按工具列的側欄按鈕（不用座標點，工具列的按鈕位置會跟著畫面變）
            guard let window = NSApp.windows.first(where: { $0.identifier?.rawValue == "main" }) else { break }
            func split(_ view: NSView) -> NSSplitView? { (view as? NSSplitView) ?? view.subviews.lazy.compactMap(split).first }
            guard let controller = window.contentView.flatMap(split)?.delegate as? NSSplitViewController,
                  let sidebar = controller.splitViewItems.first else { break }
            let want = args.count > 1 ? args[1] == "show" : sidebar.isCollapsed
            if want == sidebar.isCollapsed { controller.toggleSidebar(nil) }
        case "mousehold": model.debugMouseHeld = args.count > 1 && args[1] == "on"   // mousehold on|off：當成左鍵按著（選取會議時先不換會議頁，design §77）
        case "setkey": try? Keychain.set(args[1], args[2])          // setkey anthropic|openai <key>
        case "delkey": Keychain.delete(args[1])
        case "settings": model.openWindowById?(SettingsPage.windowID)
        case "preview":   // preview <settings|onboarding [步驟]|import|rename|stoptimer|menubar> [front] [height=<點>]（import：匯入對話框；rename：說話者的改名小視窗，design §26.2、§24.2）
            // 設定視窗要 app 在前景才打得開；這裡把畫面放進桌面層的無邊框視窗（被所有視窗蓋住；
            // 螢幕外的視窗截不到），不搶前景，再用 screencapture -l 截（closewindow <settings|onboarding>-preview 關掉）。
            // preview detail：目前選取的會議頁（量長會議的效能用，design §21.12：不在畫面上的視窗 SwiftUI 不更新）
            let onboarding = args.count > 1 && args[1] == "onboarding"
            let detailPage = args.count > 1 && args[1] == "detail"
            // preview settings <頁面代號或 0～10>：預覽視窗一開始停在那一頁（provider、templates、recording、transcription、models、
            // speakers、glossary、general、ai、enterprise、status，R108）
            let settingsPreview = args.count > 1 && args[1] == "settings"
            let pageArg = args.count > 2 && !args[2].hasPrefix("height=") && args[2] != "front" ? args[2] : ""
            SettingsView.previewPage = settingsPreview
                ? SettingsPage(rawValue: pageArg) ?? Int(pageArg).flatMap { SettingsPage.allCases.indices.contains($0) ? SettingsPage.allCases[$0] : nil } ?? .provider
                : nil
            // height=<點>：設定頁要看下面的段落時拉高（桌面層的視窗捲不了）
            let height = args.first { $0.hasPrefix("height=") }.flatMap { Double($0.dropFirst(7)) }
            SettingsView.previewHeight = height.map { CGFloat($0) }
            let size = detailPage ? NSSize(width: 1100, height: 760)
                : settingsPreview ? NSSize(width: 820, height: height ?? 620) : NSSize(width: 640, height: height ?? 560)
            let window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless],
                                  backing: .buffered, defer: false)
            // 加 front：放在一般視窗層（會出現在使用者畫面上），輔助使用才看得到、按得到裡面的按鈕
            let front = args.contains("front")
            window.level = front ? .normal : NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopWindow)))
            window.collectionBehavior = front ? [.canJoinAllSpaces] : [.canJoinAllSpaces, .stationary]
            window.identifier = NSUserInterfaceItemIdentifier(onboarding ? "onboarding-preview" : detailPage ? "detail-preview" : "settings-preview")
            window.title = window.identifier!.rawValue
            window.isReleasedWhenClosed = false
            // editinfo、replace：會議頁裡自己打開的彈窗（編輯會議資訊、用詞修正，design §80），用目前選取的會議
            if args.count > 1, ["import", "rename", "editinfo", "replace", "rediarize", "noteseditor", "stoptimer", "menubar"].contains(args[1]) {
                window.identifier = NSUserInterfaceItemIdentifier("\(args[1])-preview")
                window.title = window.identifier!.rawValue
                let view: AnyView = switch args[1] {
                case "import": AnyView(ImportSheet(urls: [URL(fileURLWithPath: "/tmp/週會 0827.m4a")]).environment(model))   // l10n-skip
                case "editinfo":
                    if let meeting = model.detail?.meeting { AnyView(EditInfoForm(meeting: meeting) {}.environment(model)) } else { AnyView(EmptyView()) }
                case "replace": AnyView(ReplaceSheet(initialFind: "Lumeo").environment(model))
                case "stoptimer": AnyView(StopTimerSheet().environment(model))   // 定時停止的「指定時間…」（design §84）
                case "menubar": AnyView(MenuBarContent().environment(model).tint(.celadon))   // 選單列的小視窗（snapshot 畫不出裡面的 Menu）
                case "noteseditor":   // 編輯會議紀錄（紀錄分頁的「編輯」，R6.6）
                    if let notes = model.detail?.notes {
                        AnyView(NotesEditor(notes: .constant(notes)) { _ in }.frame(width: 900, height: 560).environment(model))
                    } else { AnyView(EmptyView()) }
                case "rediarize":   // 重新辨識說話者（示範會議沒有錄音，requestRediarize 打不開）
                    { model.rediarizeMeetingId = model.selection; return AnyView(RediarizeSheet().environment(model)) }()
                default: AnyView(RenameSpeakerForm(name: .constant("說話者 2"), automatic: true, onReject: {},   // l10n-skip
                                                   mergeTargets: [(1, "小明"), (3, "說話者 3")], onMerge: { _ in }, onSave: {}))   // l10n-skip
                }
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
            // 多選、清空垃圾桶、分割會議（design §62、§64）
            "multiSelected": model.multiSelected, "showTrash": model.showTrash, "trashCount": model.trash.count,
            "confirmDelete": model.confirmDelete, "deleteTargets": model.deleteTargets, "confirmPurge": model.confirmPurge ?? [],
            "confirmEmptyTrash": model.confirmEmptyTrash, "splitMeetingRequest": model.splitMeetingRequest ?? -1,
            "splittingMeeting": model.applyingEdit ?? -1,
            "tab": model.tab.rawValue, "reviewExpanded": model.reviewExpanded, "userNotesExpanded": model.userNotesExpanded,
            "notesView": UserDefaults.standard.string(forKey: AppSettings.notesViewKey) ?? "list",
            "folderRules": model.folderRules.map { ["folder": $0.folder, "keywords": $0.keywords, "people": $0.people,
                                                    "templateId": $0.templateId ?? ""] as [String: Any] },
            "confirmRemovePerson": model.confirmRemovePerson ?? -1,
            "confirmDeleteFolder": model.confirmDeleteFolder ?? "",
            "renameTarget": model.renameTarget?.id ?? "",
            "editingNoteItem": model.editingNoteItem.map { "\($0)" } ?? "",
            "sidebarCollapsed": model.sidebarCollapsed.sorted(), "sidebarExpanded": model.sidebarExpanded.sorted(),
            "sidebarOrder": model.sidebarFolderItems.map(\.key), "showImportSheet": model.showImportSheet,
            "floatingMenu": FloatingMenuController.shared.debugTitles, "floatingMenuState": FloatingMenuController.shared.debugState,
            "floatingMenuWindow": FloatingMenuController.shared.debugWindowNumber,
            "list": ["sort": model.meetingSort.rawValue, "group": model.effectiveGrouping.rawValue,
                     "filter": ["review": model.meetingFilter.needsReview, "mine": model.meetingFilter.myActionItems,
                                "person": model.meetingFilter.personId ?? -1, "template": model.meetingFilter.templateId ?? ""] as [String: Any],
                     "filterSummary": model.filterSummary, "myName": model.myName ?? "",
                     "ids": model.filteredMeetings.map(\.id),
                     "groups": model.meetingGroups.map { ["title": $0.title, "count": $0.rows.count] as [String: Any] }] as [String: Any],
            // 本地 MCP（design §44.6）：開關、啟動檔
            "mcp": ["enabled": AppSettings.mcpEnabled, "launcher": MCPSetup.launcherURL.path,
                    "launcherExists": FileManager.default.fileExists(atPath: MCPSetup.launcherURL.path)] as [String: Any],
            // 播放條上的議題帶（design §43.3）：選取中的會議，長度同播放條
            "topicRanges": model.detail.flatMap { d in
                d.notes.map { MindMap.topicRanges(notes: $0, duration: max(model.player.duration, Double(d.meeting.durationSeconds ?? 0)),
                                                  untitled: MindMap.topicLabel(.interface)) }
            }.map { $0.map { ["branch": $0.branch, "title": $0.title, "start": $0.start, "end": $0.end] } } ?? [],
            "topicHover": model.debugTopicHover ?? -1,   // 當成滑過的議題帶那一段（design §87），沒有是 -1
            // 會議的參考文件（design §39.7）：選取中的會議
            "documents": (model.detail?.documents ?? []).map { d -> [String: Any] in
                let use = model.detail?.documentUse[d.id ?? 0]
                return ["id": d.id ?? 0, "name": d.name, "kind": d.kind, "file": d.file, "pages": d.pageTexts.count, "ocr": d.ocr,
                        "chars": d.pageTexts.reduce(0) { $0 + $1.count },
                        "includedPages": use?.includedPages ?? 0, "truncated": use?.truncated ?? false]
            },
            "readingDocuments": Dictionary(uniqueKeysWithValues: model.readingDocuments.map { (String($0.key), $0.value) }),
            "documentsChanged": model.documentsChanged.sorted(),
            // 會議中的畫面（design §83.8）：選取中的會議
            "screens": (model.detail?.screens ?? []).map { s -> [String: Any] in
                ["id": s.id ?? 0, "at": s.at ?? 0, "file": s.file, "reading": model.readingScreens.contains(s.id ?? 0),
                 "chars": s.screenText.count, "text": String(s.screenText.prefix(80)),
                 "exists": FileManager.default.fileExists(atPath: s.fileURL.path)]
            },
            "screensChanged": model.screensChanged.sorted(),
            "screenshotProblem": model.screenshotProblem.map { "\($0)" } ?? "",
            "screenPreview": model.screenPreview?.lastPathComponent ?? "",
            "movingScreen": model.movingScreen ?? 0,
            "screenshotHotKey": GlobalHotKey.shared.isScreenshotRegistered,
            // 選中的會議：說話者的名字、來源、建議、最像的幾位認識的人（R33，「自動」名字的選單）
            "speakers": (model.detail?.speakers ?? [:]).keys.sorted().map { idx -> [String: Any] in
                ["idx": idx, "name": model.detail?.speakers[idx] ?? "", "source": model.detail?.speakerSources[idx] ?? "",
                 "suggestion": model.detail?.speakerSuggestions[idx]?.name ?? "", "candidates": model.detail?.speakerCandidates[idx] ?? []]
            },
            "glossary": model.glossary.map(\.term),
            "glossarySuggestions": model.glossarySuggestions.map { ["term": $0.term, "source": $0.sourceLabel] },
            "environment": model.environment.items.map { ["name": $0.name, "ok": $0.ok, "detail": $0.detail, "fix": $0.fix] },
            "meetings": model.meetings.map { ["id": $0.id, "title": $0.meeting.displayTitle, "date": $0.meeting.meetingDate,
                                               "status": model.status($0).label, "audio": $0.meeting.audioFile,
                                               "template": $0.meeting.templateId, "folder": $0.meeting.folder] },
            "queuedCount": model.queuedCount,
            "filteredCount": model.filteredMeetings.count,
            "menuBarSymbol": model.menuBarSymbol,
            "micStatus": AVCaptureDevice.authorizationStatus(for: .audio).rawValue,
            "openMainWindowReady": model.openMainWindow != nil,
            "detailMeeting": model.detail?.meeting.id ?? -1,
            "applyingEdit": model.applyingEdit ?? -1,
            "editingMeeting": model.editingMeeting ?? -1,
            "editor": MeetingEditor.debugCurrent.map { e -> [String: Any] in
                ["start": e.start, "end": e.end, "deleted": e.deleted.map { [$0.lowerBound, $0.upperBound] }, "cut": e.cut ?? -1,
                 "playhead": e.playhead, "zoom": e.zoom, "total": e.total, "isPlaying": e.isPlaying, "hasChanges": e.hasChanges,
                 "planDuration": e.plan.duration, "preview": e.debugPreview, "pieces": e.plan.pieces.map { [$0.meetingId, $0.start, $0.end] as [Any] }]
            } ?? [:],
            "meetingEdit": model.detail.flatMap { try? model.db.editRecord($0.meeting.id!) }.map { r -> [String: Any] in
                ["audioFile": r.audioFile, "daysLeft": r.daysLeft(), "splitInto": r.splitIds, "mergedFrom": r.mergedIds, "editedAt": r.editedAt]
            } ?? [:],
            // 側欄的表格（拖曳，design §77）：直直往上下拖會不會開始拖曳
            "sidebarTables": NSApp.windows.filter { $0.identifier?.rawValue == "main" }.flatMap { window -> [[String: Any]] in
                func tables(_ view: NSView) -> [NSTableView] { (view as? NSTableView).map { [$0] } ?? view.subviews.flatMap(tables) }
                return window.contentView.map(tables)?.map {
                    ["class": String(describing: type(of: $0)), "verticalMotionCanBeginDrag": $0.verticalMotionCanBeginDrag,
                     "allowsMultipleSelection": $0.allowsMultipleSelection, "rows": $0.numberOfRows]
                } ?? []
            },
            "running": model.runner.running.map { lane, r in
                ["lane": lane.rawValue, "job": r.job.id ?? 0, "kind": r.job.kind.rawValue, "stage": r.stage, "progress": r.progress ?? -1,
                 "remaining": r.remaining ?? -1, "server": r.server.map { ["text": $0.text, "waiting": $0.waiting] } as Any] },
            "enterprise": EnterpriseServer.shared.debugState,
            // 找到的 claude、codex：使用者自己裝的，或桌面版附的（design §37.4）
            "tools": ["claude": Paths.locateTool("claude").map { ["path": $0.url.path, "desktopApp": $0.desktopApp ?? ""] } as Any,
                      "codex": Paths.locateTool("codex").map { ["path": $0.url.path, "desktopApp": $0.desktopApp ?? "",
                                                                "extraPath": $0.extraPath] } as Any,
                      "claudeDesktop": DesktopApps.hasClaude],
            "transcriptionEngine": AppSettings.transcriptionEngine,
            "textSize": TextSize.level,   // 會議內容的文字大小（design §38.5）
            // 提醒事項（design §52.2）：假的列表裡的每一項、目前這場會議的狀態
            "reminders": FakeReminders.shared.entries.map { entry -> [String: Any] in
                ["title": entry.item.title, "notes": entry.item.notes, "url": entry.item.url.absoluteString, "completed": entry.completed,
                 "due": entry.item.due.map { "\($0.year ?? 0)-\($0.month ?? 0)-\($0.day ?? 0)" } ?? ""]
            },
            "reminderStatus": model.reminderStatus,
            "island": RecordingIsland.shared.debugInfo,   // 錄音島（design §57.3）
            // 人物頁（design §60.2）
            "sidebarMode": model.sidebarMode.rawValue, "selectedPerson": model.selectedPerson ?? -1,
            "peopleOverview": model.peopleOverview.map { ["id": $0.id, "name": $0.name, "meetings": $0.meetingCount,
                                                          "last": $0.lastMeetingDate ?? "", "me": $0.isMe] },
            // 我的聲音（design §56.3）
            "selfPerson": ((try? model.db.enrollment()) ?? nil).map { ["id": $0.personId, "name": $0.name, "createdAt": $0.createdAt] } as Any,
            "remindersAccess": Reminders.store.access.rawValue,
            "player": ["loaded": model.player.isLoaded, "playing": model.player.isPlaying,
                       "time": model.player.currentTime, "duration": model.player.duration, "rate": model.player.rate,
                       "mixing": model.player.isMixing,
                       "mix": ChannelMix.stats.withLock { ["frames": $0.frames, "sourcePeaks": $0.sourcePeaks, "mixedPeak": $0.mixedPeak] }],
            "recorder": ["recording": model.recorder.isRecording, "paused": model.recorder.isPaused,
                         "meetingId": model.recorder.meetingId ?? -1, "elapsed": model.recorder.elapsed,
                         "micLevel": model.recorder.micLevel, "systemLevel": model.recorder.systemLevel,
                         "liveLines": model.recorder.liveLines.count, "volatile": model.recorder.volatileText,
                         "liveStatus": model.recorder.liveStatus, "permissionDenied": model.recorder.permissionDenied,
                         "levelCheck": model.recorder.levelCheck,   // 收音提醒（design §26.1）
                         "permissionRestarts": model.recorder.permissionRestarts,   // 允許了權限而重新接上電腦聲音（design §51.1）
                         "systemSilent": model.recorder.systemSilent,
                         "idle": model.recorder.idleInfo,   // 忘了停止錄音（design §35）
                         "stopTimer": model.recorder.stopTimerInfo,   // 定時停止（design §84）
                         "failure": model.recorder.failureMessage ?? "", "language": model.recorder.language.rawValue,
                         "languageSwitch": model.recorder.languageSwitch.map { "\($0.from.rawValue)→\($0.to.rawValue)" } ?? "",
                         "deviceWarning": model.recorder.deviceWarning ?? "",
                         "restarts": model.recorder.restartInfo,   // 重新連接的節奏（design §50.4）
                         // 麥克風（design §38.7）
                         "micDevice": model.recorder.micDevice.map { ["name": $0.name, "uid": $0.uid, "bluetooth": $0.isBluetooth] } as Any,
                         "micHint": model.recorder.micHint.map { ["text": $0.text, "builtIn": $0.builtIn?.name ?? ""] } as Any],
            "microphones": ["inputs": MicrophoneMonitor.shared.inputs.map { ["name": $0.name, "uid": $0.uid, "bluetooth": $0.isBluetooth,
                                                                            "builtIn": $0.isBuiltIn] },
                            "default": MicrophoneMonitor.shared.defaultInput?.uid ?? "", "setting": AppSettings.inputDeviceUID],
            "languages": ["defaultMeeting": AppSettings.defaultMeetingLanguage.rawValue, "interface": MeetingLanguage.interface.rawValue,
                          "interfaceScript": MeetingLanguage.interfaceCode, "chineseScript": AppSettings.chineseScript,
                          "preferred": Locale.preferredLanguages.first ?? ""],
            "models": ["breeze": "\(model.models.state)", "japanese": "\(model.models.state(.japanese))",
                       "qwen": "\(model.models.combinedState(.qwen))",
                       "languageDetection": "\(model.models.state(.languageDetection))", "diarizer": "\(model.models.diarizerState)",
                       "nemotron": "\(model.models.nemotronState)", "diarizationModel": AppSettings.diarizationModel.rawValue],
            "exportMeeting": model.exportMeetingId as Any,
            "search": ["query": model.searchQuery, "results": model.searchResults.map { ["meeting": $0.meetingId, "kind": $0.kind.rawValue, "ref": $0.ref, "snippet": $0.snippet] }],
            "trash": model.trash.map { $0.id },
            "templates": model.templates.map { $0.id },
            "folders": model.folders.map { ["name": $0.name, "count": $0.count] },
            "folderFilter": model.folderFilter ?? "",
            "update": ["state": model.updater.state.label, "prompt": model.updater.prompt?.version ?? "",
                       "received": model.updater.progress?.received ?? -1, "total": model.updater.progress?.total ?? -1,
                       "progressText": model.updater.progress?.text ?? "",
                       "version": Updater.currentVersion, "feed": Updater.feedURLString],
            "hotkey": ["registered": GlobalHotKey.shared.isRegistered, "status": Int(GlobalHotKey.shared.status),
                       "bookmarkRegistered": GlobalHotKey.shared.isBookmarkRegistered],
            "notifications": model.notifier.recent,
            "filteredIds": model.filteredMeetings.map(\.id),
            "highlightSegment": model.highlightSegment ?? -1,
            "errorMessage": model.errorMessage ?? "",
            "noticeMessage": model.noticeMessage ?? "",
            // 視窗的最小尺寸（SwiftUI 依內容算的）：錄音中卡住的調查用；sharing 是分享畫面時藏不藏（0 none、1 readOnly，design §53）
            "windows": NSApp.windows.map { ["id": $0.identifier?.rawValue ?? "", "title": $0.title, "visible": $0.isVisible,
                                            "minWidth": $0.contentMinSize.width, "minHeight": $0.contentMinSize.height,
                                            "minSizeW": $0.minSize.width, "minSizeH": $0.minSize.height,
                                            "width": $0.frame.width, "height": $0.frame.height,
                                            "number": $0.windowNumber, "sharing": $0.sharingType.rawValue,
                                            // 最大尺寸可能是無限大（錄音島），JSON 寫不進去會當掉：寫 -1
                                            "resizable": $0.styleMask.contains(.resizable), "maxW": finite($0.maxSize.width), "maxH": finite($0.maxSize.height),
                                            "contentMaxW": finite($0.contentMaxSize.width), "contentMaxH": finite($0.contentMaxSize.height)] },
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

    /// 下一個開始追蹤的選單（右鍵選單、「⋯」）：項目寫到 path（子選單展開成「父 ▸ 子」），然後關掉，不留在使用者畫面上
    private static var menuObserver: NSObjectProtocol?
    private static var eventMonitor: Any?
    private static var eventLogPath: String?
    private static let eventLogStart = ProcessInfo.processInfo.systemUptime

    /// eventlog 開著時寫一行（拖曳開始了沒這類，事件以外的東西也寫進同一份）
    static func note(_ text: String) {
        guard let path = eventLogPath else { return }
        let line = String(format: "%.3f ", ProcessInfo.processInfo.systemUptime - eventLogStart) + text + "\n"
        if let handle = FileHandle(forWritingAtPath: path) { handle.seekToEndOfFile(); handle.write(Data(line.utf8)); try? handle.close() }
    }
    /// keep：選單留著幾秒再關（給外面截圖）
    /// 接下來打開的選單（右鍵、「⋯」）直接選標題是 title 的那一項（子選單用「移到資料夾 ▸ 客戶」），等於使用者點了它
    private static func pickNextMenu(_ title: String) {
        if let menuObserver { NotificationCenter.default.removeObserver(menuObserver) }
        menuObserver = NotificationCenter.default.addObserver(forName: NSMenu.didBeginTrackingNotification, object: nil, queue: nil) { note in
            guard let menu = note.object as? NSMenu, menu.supermenu == nil, menu != NSApp.mainMenu else { return }
            if let observer = menuObserver { NotificationCenter.default.removeObserver(observer); menuObserver = nil }
            nonisolated(unsafe) let tracked = menu
            RunLoop.main.perform(inModes: [.common]) {
                MainActor.assumeIsolated {
                    tracked.update()
                    var current: NSMenu? = tracked
                    let path = title.components(separatedBy: " ▸ ")
                    for (i, part) in path.enumerated() {
                        guard let m = current, let index = m.items.firstIndex(where: { $0.title == part }) else { break }
                        if i == path.count - 1 {
                            tracked.cancelTracking()
                            m.performActionForItem(at: index)
                        } else {
                            m.items[index].submenu?.update()
                            current = m.items[index].submenu
                        }
                    }
                    tracked.cancelTracking()
                }
            }
        }
    }

    private static func captureNextMenu(to path: String, keep: Double = 0) {
        if let menuObserver { NotificationCenter.default.removeObserver(menuObserver) }
        menuObserver = NotificationCenter.default.addObserver(forName: NSMenu.didBeginTrackingNotification, object: nil, queue: nil) { note in
            guard let menu = note.object as? NSMenu, menu.supermenu == nil, menu != NSApp.mainMenu else { return }
            if let observer = menuObserver { NotificationCenter.default.removeObserver(observer); menuObserver = nil }
            func titles(_ m: NSMenu, _ prefix: String) -> [String] {
                m.items.flatMap { item -> [String] in
                    let hidden = item.isHidden ? "（隱藏）" : ""
                    if item.isSeparatorItem { return [prefix + "——" + hidden] }
                    let header = item.isSectionHeader ? "【段落】" : ""
                    let line = prefix + header + item.title + (item.isEnabled ? "" : "（停用）") + (item.state == .on ? "（✓）" : "") + hidden
                    return [line] + (item.submenu.map { titles($0, prefix + item.title + " ▸ ") } ?? [])
                }
            }
            // SwiftUI 的選單內容在開始追蹤時才補齊，等一下再讀（選單追蹤時主執行緒在 event tracking 模式，要排在 common）
            nonisolated(unsafe) let tracked = menu
            RunLoop.main.perform(inModes: [.common]) {
                MainActor.assumeIsolated {
                    tracked.update()
                    write(["items": titles(tracked, "")], to: path)
                    if keep > 0 {
                        let timer = Timer(timeInterval: keep, repeats: false) { _ in MainActor.assumeIsolated { tracked.cancelTracking() } }
                        RunLoop.main.add(timer, forMode: .common)
                    } else {
                        tracked.cancelTracking()
                    }
                }
            }
        }
    }

    /// 無限大、NaN 寫成 -1（JSONSerialization 遇到會丟 Objective-C 例外，整個 app 當掉）
    private static func finite(_ x: CGFloat) -> CGFloat { x.isFinite ? x : -1 }

    private static func write(_ object: [String: Any], to path: String) {
        guard JSONSerialization.isValidJSONObject(object),
              let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]) else { return }
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
                    (s["speaker"] as? Int).map { language.defaultSpeaker($0) }
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
                result["whisperDefaults"] = WhisperEngine.defaultParameters()   // whisper.cpp 的預設值（實驗時對照用，design §17.1）
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

    private static func qwenChunks(audio: String, output: String, maxSeconds: Double, anyChange: Bool, splitMixed: Bool) {
        Task { @MainActor in
            var result: [String: Any] = ["audio": audio, "maxSeconds": maxSeconds]
            do {
                let samples = try await AudioDecoder.decode(URL(fileURLWithPath: audio))
                let diarization = try await Diarizer.diarize(samples: samples, minSpeakers: nil) { _ in }
                let chunks = QwenEngine.chunks(samples, turns: diarization.turns, maxSeconds: maxSeconds, anyChange: anyChange, splitMixed: splitMixed)
                let rate = AudioDecoder.sampleRate
                result["turns"] = diarization.turns.map { ["speaker": $0.speaker, "start": $0.start, "end": $0.end] }
                result["chunks"] = chunks.map { ["start": Double($0.range.lowerBound) / rate, "end": Double($0.range.upperBound) / rate] }
                result["median"] = samples.withUnsafeBufferPointer { QwenEngine.medianFrameEnergy($0) }
            } catch {
                result["error"] = error.localizedDescription
            }
            write(result, to: output)
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
                case .summary: NotesPage(detail: detail)
                case .transcript: TranscriptTab(detail: detail, speakerFilter: speakerFilter)
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
