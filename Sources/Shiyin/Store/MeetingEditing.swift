import Foundation

/// 編輯會議（R107、design §81）：打開編輯畫面、套用、還原、30 天後清掉編輯前的錄音
extension AppModel {
    /// 不能編輯的原因（R107.1）；nil 是可以
    func editProblem(_ id: Int64) -> String? {
        guard let meeting = meetings.first(where: { $0.id == id })?.meeting ?? (try? db.meeting(id)) else { return "" }
        if recorder.meetingId == id && recorder.isRecording { return String(localized: "這場會議還在錄音，請先停止錄音再編輯") }
        if meeting.deletedAt != nil { return String(localized: "垃圾桶裡的會議要先還原才能操作") }
        if applyingEdit != nil || (try? db.hasActiveJobs(id)) == true { return String(localized: "正在處理這場會議，完成後才能編輯") }
        return nil
    }

    /// 「編輯會議…」（右鍵、「⋯」、更多）：選到那場、打開編輯畫面
    func requestEditMeeting(_ id: Int64) {
        if let problem = editProblem(id) {
            if !problem.isEmpty { noticeMessage = problem }
            return
        }
        if selection != id { selection = id }
        editingMeeting = id
    }

    /// 合併時可以選的會議（§81.5）：不在垃圾桶、不是這場、不在錄音或處理中；同一天、前後三天的在前面
    func mergeCandidates(for id: Int64) -> (near: [MeetingRow], older: [MeetingRow]) {
        guard let base = meetings.first(where: { $0.id == id })?.meeting,
              let day = DateText.date(base.meetingDate) else { return ([], []) }
        let rows = meetings.filter { row in
            row.id != id && !(recorder.meetingId == row.id && recorder.isRecording) && (try? db.hasActiveJobs(row.id)) != true
        }
        func distance(_ row: MeetingRow) -> Double {
            DateText.date(row.meeting.meetingDate).map { abs($0.timeIntervalSince(day)) / 86_400 } ?? .infinity
        }
        let near = rows.filter { distance($0) <= 3 }.sorted { distance($0) < distance($1) }
        let older = rows.filter { distance($0) > 3 }
        return (near, older)
    }

    /// 套用（R107.7）：背景接好錄音（分成兩場時前後各一個檔），一個交易改資料庫，再換檔案、複製參考文件、排重新整理
    func applyMeetingEdit(_ id: Int64, plan: EditPlan, cut: Double?) {
        if let problem = editProblem(id) {
            if !problem.isEmpty { noticeMessage = problem }
            return
        }
        guard let meeting = try? db.meeting(id) else { return }
        var sources: [Int64: URL] = [:]
        var titles: [Int64: String] = [:]
        for meetingId in Set(plan.pieces.map(\.meetingId)) {
            guard let m = meetingId == id ? meeting : try? db.meeting(meetingId) else { continue }
            if m.hasAudio { sources[meetingId] = m.audioURL }
            titles[meetingId] = m.displayTitle
        }
        let splitTitle = String(localized: "\(meeting.displayTitle)（2）")
        let parts = cut.flatMap { plan.split(at: $0) }
        applyingEdit = id
        Task {
            defer { applyingEdit = nil }
            var temps: [URL] = []
            defer { temps.forEach { try? FileManager.default.removeItem(at: $0.deletingLastPathComponent()) } }
            do {
                let head = try await Task.detached(priority: .userInitiated) {
                    try await AudioEdit.compose(parts?.head ?? plan, sources: sources)
                }.value
                let tail = try await Task.detached(priority: .userInitiated) { () async throws -> URL? in
                    guard let parts else { return nil }
                    return try await AudioEdit.compose(parts.tail, sources: sources)
                }.value
                temps = [head, tail].compactMap { $0 }
                guard let outcome = try db.applyEdit(id, plan: plan, cut: parts == nil ? nil : cut, splitTitle: splitTitle,
                                                     audioExtension: head?.pathExtension) else { return }
                if selection == id { player.unload() }
                try replaceAudio(meeting, with: head, firstEdit: outcome.firstEdit)
                if let tail, let created = outcome.created { try FileManager.default.moveItem(at: tail, to: created.audioURL) }
                for copy in outcome.documentCopies {
                    let from = Paths.documentsDir.appendingPathComponent(copy.from), to = Paths.documentsDir.appendingPathComponent(copy.to)
                    try? FileManager.default.createDirectory(at: to.deletingLastPathComponent(), withIntermediateDirectories: true)
                    try? FileManager.default.copyItem(at: from, to: to)
                }
                let others = titles.filter { $0.key != id }.map(\.value)
                var message: String
                if let created = outcome.created {
                    message = String(localized: "已分成兩場：「\(created.displayTitle)」")
                } else if let other = others.first {
                    message = String(localized: "已合併「\(other)」，那場移到垃圾桶")
                } else {
                    message = String(localized: "已編輯「\(meeting.displayTitle)」")
                }
                if AppSettings.summarizerReady {
                    try db.enqueue(id, kind: .summarize)
                    if let created = outcome.created { try db.enqueue(created.id!, kind: .summarize) }
                    runner.wake()
                    message += String(localized: "。會議紀錄正在重新整理")
                } else {
                    message += String(localized: "。會議紀錄要重新整理，設定好整理工具後按「重新整理」")
                }
                reload()
                if selection == id { reloadDetail(forceAudio: true) }
                noticeMessage = message
            } catch {
                errorMessage = String(localized: "編輯會議失敗：\(error.localizedDescription)")
                reload()
            }
        }
    }

    /// 換上新的錄音：第一次編輯時原本的錄音搬到 audio/edits/（還原用），之後的編輯舊的直接刪
    private func replaceAudio(_ meeting: Meeting, with new: URL?, firstEdit: Bool) throws {
        let fm = FileManager.default
        if meeting.hasAudio {
            if firstEdit {
                let ext = meeting.audioURL.pathExtension
                let kept = Paths.editsAudioDir.appendingPathComponent("\(meeting.id!).\(ext)")
                try fm.createDirectory(at: Paths.editsAudioDir, withIntermediateDirectories: true)
                try? fm.removeItem(at: kept)
                try fm.moveItem(at: meeting.audioURL, to: kept)
            } else {
                try fm.removeItem(at: meeting.audioURL)
            }
        }
        if let new {
            let destination = Paths.audioDir.appendingPathComponent("\(meeting.id!).\(new.pathExtension)")
            try? fm.removeItem(at: destination)
            try fm.moveItem(at: new, to: destination)
        }
    }

    /// 「還原成編輯前的樣子…」要確認（R107.8）
    func requestRestoreEdit(_ id: Int64) {
        guard (try? db.editRecord(id)) ?? nil != nil else {
            noticeMessage = String(localized: "這場會議沒有編輯過。編輯會議後 30 天內可以從這裡還原。")
            return
        }
        if let problem = editProblem(id) {
            if !problem.isEmpty { noticeMessage = problem }
            return
        }
        confirmRestoreEdit = id
    }

    /// 還原確認彈窗的說明：原本多長、編輯後的修改會不見、分出去的會議移到垃圾桶、合併進來的從垃圾桶拿出來
    func restoreEditMessage(_ id: Int64) -> String {
        guard let record = try? db.editRecord(id),
              let snapshot = try? JSONDecoder().decode(EditSnapshot.self, from: Data(record.snapshot.utf8)) else { return "" }
        var lines = [String(localized: "會議會回到編輯前的樣子（原本 \(TimeFormat.string(Double(snapshot.durationSeconds ?? 0)))），錄音、逐字稿、標記重點、會議紀錄都換回來。"),
                     String(localized: "編輯之後對逐字稿、說話者、筆記的修改會不見。")]
        for other in record.splitIds {
            if let m = try? db.meeting(other), m.deletedAt == nil { lines.append(String(localized: "分出去的「\(m.displayTitle)」會移到垃圾桶。")) }
        }
        for other in record.mergedIds {
            if let m = try? db.meeting(other), m.deletedAt != nil { lines.append(String(localized: "合併進來的「\(m.displayTitle)」會從垃圾桶還原。")) }
        }
        return lines.map { "・" + $0 }.joined(separator: "\n")  // l10n-skip
    }

    func restoreMeetingEdit(_ id: Int64) {
        if let problem = editProblem(id) {
            if !problem.isEmpty { noticeMessage = problem }
            return
        }
        applyingEdit = id
        defer { applyingEdit = nil }
        do {
            if selection == id { player.unload() }
            guard let outcome = try db.restoreEdit(id) else { return }
            let fm = FileManager.default
            if !outcome.currentAudio.isEmpty { try? fm.removeItem(at: Paths.audioDir.appendingPathComponent(outcome.currentAudio)) }
            if !outcome.editsAudio.isEmpty {
                let destination = Paths.audioDir.appendingPathComponent(outcome.restoredAudio)
                try? fm.removeItem(at: destination)
                try fm.moveItem(at: Paths.audioDir.appendingPathComponent(outcome.editsAudio), to: destination)
            }
            outcome.removedDocuments.forEach(db.removeDocumentFile)
            var message = String(localized: "已還原成編輯前的樣子")
            if !outcome.trashed.isEmpty { message += String(localized: "，分出去的會議移到垃圾桶") }
            if !outcome.untrashed.isEmpty { message += String(localized: "，合併進來的會議已從垃圾桶拿出來") }
            reload()
            if selection == id { reloadDetail(forceAudio: true) }
            noticeMessage = message
        } catch {
            errorMessage = String(localized: "還原失敗：\(error.localizedDescription)")
            reload()
        }
    }

    /// 最後一次編輯超過 30 天：刪掉編輯前的錄音和快照（R107.8）；audio/edits/ 裡沒人用的檔（會議永久刪除了）一起刪
    func purgeExpiredEdits() {
        let fm = FileManager.default
        for record in (try? db.expiredEdits(olderThan: MeetingEditRecord.keepDays)) ?? [] {
            if !record.audioFile.isEmpty { try? fm.removeItem(at: Paths.audioDir.appendingPathComponent(record.audioFile)) }
            try? db.deleteEditRecord(record.meetingId)
        }
        guard let keep = try? db.editAudioFiles(),
              let files = try? fm.contentsOfDirectory(atPath: Paths.editsAudioDir.path) else { return }
        for name in files where !keep.contains("edits/" + name) {
            try? fm.removeItem(at: Paths.editsAudioDir.appendingPathComponent(name))
        }
    }
}
