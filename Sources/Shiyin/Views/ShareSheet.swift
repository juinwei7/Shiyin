import SwiftUI

/// 分享視窗（R125.2～R125.4、design §99.4；彈窗的樣子照 §80）：
/// 沒有伺服器的分享時只有「用檔案分享…」；還沒分享時是名單、連結、期限、要帶的內容；分享了之後是名單的狀態、連結、期限、更新、停止
struct ShareSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let meetingId: Int64

    @State private var emails: [String] = []
    @State private var draft = ""
    @State private var suggestions: [EnterpriseShares.Person] = []
    @State private var withLink = false
    @State private var access = "company"
    @State private var days: Int? = 30
    @State private var contents = EnterpriseShares.Contents()
    @State private var confirmNotice = false
    @State private var confirmStop = false
    @State private var loaded = false

    private var meeting: Meeting? { model.detail?.meeting.id == meetingId ? model.detail?.meeting : (try? model.db.meeting(meetingId)) }
    private var detail: EnterpriseShares.Detail? { model.shareDetails[meetingId] }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            SheetHeader(title: String(localized: "分享「\(meeting?.displayTitle ?? "")」"), subtitle: subtitle)
            if model.sharing == nil {
                Text("整場會議（錄音、逐字稿、說話者、紀錄、筆記）存成 .shiyin，傳給同事後點兩下就能匯入。")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else if let detail {
                sharedView(detail)
            } else if loaded {
                newShareView
            } else {
                ProgressView().frame(maxWidth: .infinity)
            }
            if model.shareBusy, let progress = model.shareProgress {
                HStack(spacing: 10) {
                    ProgressView(value: progress).frame(maxWidth: .infinity)
                    Text("\(Int(progress * 100))%").font(.caption).monospacedDigit().foregroundStyle(.secondary)
                    Button("取消") { model.shareTask?.cancel() }.buttonStyle(.link)
                }
            }
            footer
        }
        .sheetChrome()
        .frame(width: 520)
        .task {
            if let max = model.sharing?.maxDays { days = min(30, max) }
            await model.loadShareDetail(meetingId)
            loaded = true
        }
        .confirmationDialog(String(localized: "透過公司的伺服器分享？"), isPresented: $confirmNotice) {
            Button("分享") {
                UserDefaults.standard.set(true, forKey: AppSettings.shareNoticeShown)
                share()
            }
        } message: {
            Text("這場會議會存在 \(EnterpriseServer.shared.host)（公司的伺服器），直到你停止分享或到期。收到的人匯入後是他自己的一份，停止分享不會刪掉別人已經匯入的。")
        }
        .confirmationDialog(String(localized: "停止分享這場會議？"), isPresented: $confirmStop) {
            Button("停止分享", role: .destructive) { Task { await model.stopSharing(meetingId) } }
        } message: {
            Text("公司的伺服器會刪掉這場，名單上的人和連結都打不開；別人已經匯入的那份不受影響。")
        }
    }

    private var subtitle: String {
        guard model.sharing != nil else { return String(localized: "登入企業伺服器、公司有開分享時，也可以直接傳給同事或建立連結。") }
        return String(localized: "透過 \(EnterpriseServer.shared.host)（公司的伺服器）分享，收到的人匯入成自己的一份。")
    }

    // MARK: 還沒分享

    @ViewBuilder private var newShareView: some View {
        recipientsInput
        VStack(alignment: .leading, spacing: 8) {
            Toggle("同時建立連結", isOn: $withLink).toggleStyle(.checkbox)
            if withLink { accessPicker($access) }
        }
        expiryPicker($days)
        VStack(alignment: .leading, spacing: 6) {
            Text("要帶的內容").font(.callout).foregroundStyle(.secondary)
            Text("會議資訊、逐字稿、說話者名字、會議紀錄、重點標記一定會帶。").font(.caption).foregroundStyle(.tertiary)
            Toggle(audioLabel, isOn: $contents.audio).toggleStyle(.checkbox).disabled(meeting?.hasAudio != true)
            Toggle("我的筆記", isOn: $contents.userNotes).toggleStyle(.checkbox)
            Toggle("參考文件和會議中的畫面", isOn: $contents.documents).toggleStyle(.checkbox)
        }
    }

    private var audioLabel: String {
        guard let meeting, meeting.hasAudio,
              let size = (try? FileManager.default.attributesOfItem(atPath: meeting.audioURL.path))?[.size] as? NSNumber else { return String(localized: "錄音") }
        return String(localized: "錄音（\(ByteCountFormatter.string(fromByteCount: size.int64Value, countStyle: .file))）")
    }

    /// 信箱：打字時從員工名單補完，Return 或逗號變成一顆，可以刪
    @ViewBuilder private var recipientsInput: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("傳給同事").font(.callout).foregroundStyle(.secondary)
            if !emails.isEmpty {
                FlowLayout(spacing: 6) {
                    ForEach(emails, id: \.self) { email in
                        HStack(spacing: 4) {
                            Text(email).font(.callout)
                            Button { emails.removeAll { $0 == email } } label: { Image(systemName: "xmark").font(.caption2) }
                                .buttonStyle(.plain).help("移除 \(email)")
                        }
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Color.primary.opacity(0.08), in: Capsule())
                    }
                }
            }
            SheetTextField(placeholder: String(localized: "同事的信箱"), text: $draft, focusOnAppear: detail == nil) { addDraft() }
                .onChange(of: draft) { _, text in
                    if text.contains(",") || text.contains("，") { addDraft() } else { lookup(text) }   // l10n-skip
                }
            if !suggestions.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(suggestions) { person in
                        Button { add(person.email) } label: {
                            HStack {
                                Text(person.display)
                                if person.name?.isEmpty == false { Text(person.email).foregroundStyle(.secondary) }
                                Spacer()
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain).font(.callout)
                    }
                }
                .padding(8)
                .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 8))
            }
        }
    }

    private func addDraft() {
        for part in draft.split(whereSeparator: { ",，;；\n ".contains($0) }) { add(String(part)) }   // l10n-skip
        draft = ""
        suggestions = []
    }

    private func add(_ email: String) {
        let trimmed = email.trimmingCharacters(in: .whitespaces).lowercased()
        guard trimmed.contains("@"), !emails.contains(trimmed) else { return }
        emails.append(trimmed)
        draft = ""
        suggestions = []
    }

    private func lookup(_ text: String) {
        let query = text.trimmingCharacters(in: .whitespaces)
        guard query.count >= 1 else { suggestions = []; return }
        Task {
            try? await Task.sleep(for: .milliseconds(250))
            guard draft.trimmingCharacters(in: .whitespaces) == query else { return }
            suggestions = ((try? await EnterpriseShares.people(query)) ?? []).filter { !emails.contains($0.email.lowercased()) }
        }
    }

    private func accessPicker(_ value: Binding<String>) -> some View {
        Picker("誰打得開", selection: value) {
            Text("公司裡登入的人").tag("company")
            if model.sharing?.publicLinks == true { Text("知道連結的任何人").tag("public") }
        }
        .pickerStyle(.menu).fixedSize()
    }

    private func expiryPicker(_ value: Binding<Int?>) -> some View {
        let max = model.sharing?.maxDays
        return Picker("有效期限", selection: value) {
            ForEach([7, 30].filter { max == nil || $0 <= max! }, id: \.self) { Text("\($0) 天").tag(Optional($0)) }
            if let max, max != 7, max != 30 { Text("\(max) 天").tag(Optional(max)) }
            if max == nil { Text("不限").tag(Int?.none) }
        }
        .pickerStyle(.menu).fixedSize()
    }

    // MARK: 分享了之後

    @ViewBuilder private func sharedView(_ detail: EnterpriseShares.Detail) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(detail.recipients.isEmpty ? String(localized: "還沒有傳給任何人") : String(localized: "已傳給 \(detail.recipients.count) 人"))
                .font(.callout).foregroundStyle(.secondary)
            ForEach(detail.recipients) { r in
                HStack {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(r.name.flatMap { $0.isEmpty ? nil : $0 } ?? r.email)
                        if r.name?.isEmpty == false { Text(r.email).font(.caption).foregroundStyle(.secondary) }
                    }
                    Spacer()
                    Text(r.importedAt != nil ? String(localized: "已匯入") : r.openedAt != nil ? String(localized: "已打開") : String(localized: "還沒打開"))
                        .font(.caption).foregroundStyle(.secondary)
                    Button { Task { await model.removeShareRecipient(meetingId, email: r.email) } } label: { Image(systemName: "xmark.circle") }
                        .buttonStyle(.borderless).help("從名單移除 \(r.email)")
                }
            }
        }
        recipientsInput
        if !emails.isEmpty {
            Button("加入名單") {
                let added = emails
                emails = []
                Task {
                    let back = await model.addShareRecipients(meetingId, emails: added)
                    emails = back + emails.filter { !back.contains($0) }
                }
            }
        }
        Divider()
        if let link = detail.link {
            VStack(alignment: .leading, spacing: 6) {
                Text("連結").font(.callout).foregroundStyle(.secondary)
                HStack {
                    Text(link.url).font(.callout.monospaced()).textSelection(.enabled).lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("拷貝") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(link.url, forType: .string)
                        model.noticeMessage = String(localized: "已拷貝連結")
                    }
                }
                HStack {
                    accessPicker(Binding(get: { link.access }, set: { value in Task { await model.setShareLink(meetingId, access: value) } }))
                    Spacer()
                    Button("停止連結") { Task { await model.setShareLink(meetingId, access: nil) } }.buttonStyle(.link)
                }
            }
        } else {
            HStack {
                accessPicker($access)
                Button("建立連結") { Task { await model.setShareLink(meetingId, access: access) } }
            }
        }
        HStack {
            Text(detail.expiresAt.map { String(localized: "到期：\(ShareDate.day($0))") } ?? String(localized: "不限期")).font(.callout).foregroundStyle(.secondary)
            Spacer()
            Menu("改期限") {
                let max = model.sharing?.maxDays
                ForEach([7, 30].filter { max == nil || $0 <= max! }, id: \.self) { n in
                    Button("從今天起 \(n) 天") { Task { await model.setShareExpiry(meetingId, days: n) } }
                }
                if let max, max != 7, max != 30 { Button("從今天起 \(max) 天") { Task { await model.setShareExpiry(meetingId, days: max) } } }
                if max == nil { Button("不限期") { Task { await model.setShareExpiry(meetingId, days: nil) } } }
            }
            .menuStyle(.borderlessButton).fixedSize()
        }
    }

    // MARK: 按鈕

    @ViewBuilder private var footer: some View {
        HStack(spacing: 10) {
            Button("用檔案分享…") {
                dismiss()
                model.exportInitialItem = .package
                model.openExportSheet()
            }
            .buttonStyle(.link)
            if detail != nil, model.sharing != nil {
                Button("停止分享…", role: .destructive) { confirmStop = true }
                    .buttonStyle(.plain).foregroundStyle(.red).disabled(model.shareBusy)
            }
            Spacer()
            if let detail, model.sharing != nil {
                Button("更新內容") { model.shareTask = Task { await model.updateShareContents(meetingId) } }
                    .buttonStyle(SheetPillButtonStyle(.secondary)).disabled(model.shareBusy || detail.uploading)
                    .help("改過會議之後重新上傳（名單、連結不變），收到的人會看到「已更新」")
            }
            Button(detail == nil && model.sharing != nil ? String(localized: "取消") : String(localized: "完成")) { dismiss() }
                .buttonStyle(SheetPillButtonStyle(.secondary))
                .keyboardShortcut(.cancelAction)
            if detail == nil, model.sharing != nil {
                Button("分享") {
                    addDraft()
                    if UserDefaults.standard.bool(forKey: AppSettings.shareNoticeShown) { share() } else { confirmNotice = true }
                }
                .buttonStyle(SheetPillButtonStyle(.primary))
                .keyboardShortcut(.defaultAction)
                .disabled(model.shareBusy || !loaded)
            }
        }
    }

    private func share() {
        let (list, link) = (emails, withLink ? access : nil)
        emails = []
        model.shareTask = Task {
            let back = await model.shareMeeting(meetingId, emails: list, link: link, days: days, contents: contents)
            emails = back + emails.filter { !back.contains($0) }
        }
    }
}

/// 側欄「與我分享」的一列（R125.5）：標題、分享者、日期；沒匯入過或已更新的有圓點
struct SharedRow: View {
    let item: EnterpriseShares.SharedItem

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "tray.and.arrow.down").foregroundStyle(.secondary).frame(width: 18)
            VStack(alignment: .leading, spacing: 1) {
                Text(item.title).lineLimit(1)
                Text("\(item.from.display) · \(item.meeting?.meetingDate ?? "")").font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 4)
            if item.isNew { Circle().fill(Color.celadon).frame(width: 7, height: 7).help(item.importedVersion == nil ? "還沒匯入" : "已更新") }
        }
    }
}

/// 選到「與我分享」的一場時右邊的預覽（R125.5）：標題、分享者、日期、長度、大小、帶了哪些內容、到期日；匯入、從清單移除
struct SharedPreview: View {
    @Environment(AppModel.self) private var model
    let item: EnterpriseShares.SharedItem

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(item.title).contentTitleFont(26).textSelection(.enabled)
            Text("\(item.from.display)（\(item.from.email)）分享").foregroundStyle(.secondary)
            Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 6) {
                if let date = item.meeting?.meetingDate, !date.isEmpty { row(String(localized: "日期"), date) }
                if let seconds = item.meeting?.durationSeconds { row(String(localized: "長度"), TimeFormat.duration(seconds)) }
                row(String(localized: "大小"), ByteCountFormatter.string(fromByteCount: item.bytes, countStyle: .file))
                row(String(localized: "內容"), contentsText)
                row(String(localized: "到期"), item.expiresAt.map { ShareDate.day($0) } ?? String(localized: "不限期"))
                if item.importedVersion != nil {
                    row(String(localized: "狀態"), item.isNew ? String(localized: "分享者更新過，可以再匯入一次") : String(localized: "已匯入"))
                }
            }
            .font(.callout)
            Text("匯入後是你自己的一份，可以編輯、重新整理，跟分享者那份無關。").font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 10) {
                Button(item.importedVersion == nil ? String(localized: "匯入") : String(localized: "再匯入一次")) { Task { await model.importShared(item) } }
                    .buttonStyle(SheetPillButtonStyle(.primary)).disabled(model.shareBusy)
                Button("從清單移除") { Task { await model.hideShared(item.id) } }
                    .buttonStyle(SheetPillButtonStyle(.secondary)).disabled(model.shareBusy)
                if model.shareBusy, let progress = model.shareProgress {
                    ProgressView(value: progress).frame(width: 160)
                }
            }
            Spacer()
        }
        .padding(28)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var contentsText: String {
        var parts = [String(localized: "逐字稿、紀錄")]
        if item.contents.audio { parts.append(String(localized: "錄音")) }
        if item.contents.userNotes { parts.append(String(localized: "我的筆記")) }
        if item.contents.documents { parts.append(String(localized: "參考文件")) }
        return parts.joined(separator: MeetingLanguage.ui(zh: "、", en: ", "))
    }

    private func row(_ title: String, _ value: String) -> some View {
        GridRow {
            Text(title).foregroundStyle(.secondary)
            Text(value)
        }
    }
}

/// 伺服器給的時間（ISO 8601、UTC）換成這台電腦的日期
enum ShareDate {
    static func day(_ stamp: String) -> String {
        let parser = ISO8601DateFormatter()
        parser.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let date = parser.date(from: stamp) ?? ISO8601DateFormatter().date(from: stamp) else { return stamp }
        return DateText.string(date)
    }
}
