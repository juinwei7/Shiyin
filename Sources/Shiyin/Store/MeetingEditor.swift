import AVFoundation
import SwiftUI

/// 編輯畫面的狀態（R107、design §81.5）：一條時間軸（合併時兩場接在一起），把手、刪掉的段落、選取、切點、播放頭都是這條時間軸上的秒數；
/// 套用時換成 EditPlan（每場自己的時間）
@MainActor @Observable
final class MeetingEditor {
    struct Part: Identifiable, Equatable {
        let id: Int64
        var title: String
        var offset: Double
        var length: Double
        var createdAt: String
        var meetingDate: String
        var language: String
        var url: URL?
    }

    struct Line: Identifiable, Equatable {
        let id: Int64
        let meetingId: Int64
        let start: Double
        let end: Double
        let speaker: Int?
        let speakerName: String
        let text: String
        var mid: Double { (start + max(start, end)) / 2 }
    }

    struct TopicSpan: Equatable {
        var title: String
        var start: Double
        var end: Double
        var branch: Int
    }

    let meetingId: Int64
    private(set) var parts: [Part] = []
    private(set) var lines: [Line] = []
    private(set) var topics: [TopicSpan] = []
    private(set) var marks: [Double] = []
    /// 每場的波形（每 0.05 秒一個峰值，用那場的大聲處調成 0～1）；還在讀的不在裡面
    private(set) var peaks: [Int64: [Float]] = [:]
    private(set) var loadingPeaks: Set<Int64> = []

    var start: Double = 0
    var end: Double = 0
    private(set) var deleted: [ClosedRange<Double>] = []
    var selection: ClosedRange<Double>?
    private(set) var cut: Double?
    var playhead: Double = 0
    /// 1 是整場放得下，最大 40 倍
    var zoom: Double = 1
    private(set) var isPlaying = false
    /// 逐字稿選到的句子（⌘、⇧ 點選）
    var selectedLines: Set<Int64> = []
    /// 在時間軸上點了一下：逐字稿捲到播放頭那一句
    var revealPlayhead = 0

    static let peakStep = 0.05

    init(meetingId: Int64) {
        self.meetingId = meetingId
        #if DEBUG
        Self.debugCurrent = self
        #endif
    }

    #if DEBUG
    /// DEBUG 的 editorstate 改目前打開的編輯畫面
    @ObservationIgnored static weak var debugCurrent: MeetingEditor?

    var debugPreview: [String: Any] {
        ["time": player?.currentTime().seconds ?? -1, "rate": player?.rate ?? -1,
         "status": player?.currentItem?.status.rawValue ?? -1, "duration": player?.currentItem?.duration.seconds ?? -1,
         "error": player?.currentItem?.error?.localizedDescription ?? ""]
    }

    func debugSet(start: Double?, end: Double?, deleted: [ClosedRange<Double>]?, selection: ClosedRange<Double>??, cut: Double??) {
        if let start { self.start = start }
        if let end { self.end = end }
        if let deleted { self.deleted = EditPlan.normalized(deleted) }
        if let selection { self.selection = selection }
        if let cut { self.cut = cut }
    }
    #endif

    var total: Double { parts.reduce(0) { $0 + $1.length } }
    var mergedPart: Part? { parts.first { $0.id != meetingId } }
    var ownPart: Part? { parts.first { $0.id == meetingId } }

    var hasChanges: Bool {
        start > 0.05 || end < total - 0.05 || !deleted.isEmpty || cut != nil || mergedPart != nil
    }

    // MARK: 讀資料

    func load(db: AppDatabase) {
        guard let meeting = try? db.meeting(meetingId) else { return }
        parts = [part(meeting, db: db, offset: 0)]
        rebuildContent(db: db)
        start = 0
        end = total
        playhead = 0
        loadPeaks()
    }

    private func part(_ meeting: Meeting, db: AppDatabase, offset: Double) -> Part {
        let id = meeting.id!
        let segmentsEnd = max(((try? db.segments(id)) ?? []).map(\.end).max() ?? 0,
                              ((try? db.segments(id, kind: "live")) ?? []).map(\.end).max() ?? 0)
        var length = max(Double(meeting.durationSeconds ?? 0), segmentsEnd)
        if meeting.hasAudio, let seconds = Self.audioSeconds(meeting.audioURL) { length = max(length, seconds) }
        return Part(id: id, title: meeting.displayTitle, offset: offset, length: max(length, 1), createdAt: meeting.createdAt,
                    meetingDate: meeting.meetingDate, language: meeting.language, url: meeting.hasAudio ? meeting.audioURL : nil)
    }

    private static func audioSeconds(_ url: URL) -> Double? {
        guard let file = try? AVAudioFile(forReading: url), file.processingFormat.sampleRate > 0 else { return nil }
        return Double(file.length) / file.processingFormat.sampleRate
    }

    /// 句子、議題、標記照各場的位置排到同一條時間軸
    private func rebuildContent(db: AppDatabase) {
        var lines: [Line] = [], topics: [TopicSpan] = [], marks: [Double] = []
        // 合併進來的那場：同名的說話者用這場的編號（顏色），其他接在後面（套用時還會看人物、聲紋，§81.4）
        let ownSpeakers = ((try? db.speakers(meetingId)) ?? [])
        let ownByName = Dictionary(ownSpeakers.filter { $0.nameSource != "default" }.map { ($0.name.lowercased(), $0.idx) },
                                   uniquingKeysWith: { a, _ in a })
        let nextIdx = (ownSpeakers.map(\.idx).max() ?? 0)
        for part in parts {
            let speakers = (try? db.speakers(part.id)) ?? []
            let names = Dictionary(speakers.map { ($0.idx, $0.name) }, uniquingKeysWith: { a, _ in a })
            var display: [Int: Int] = [:]
            if part.id != meetingId {
                for s in speakers {
                    display[s.idx] = s.nameSource != "default" ? ownByName[s.name.lowercased()] ?? nextIdx + s.idx : nextIdx + s.idx
                }
            }
            let language = MeetingLanguage(rawValue: part.language) ?? .zh
            for s in (try? db.segments(part.id)) ?? [] {
                lines.append(Line(id: s.id!, meetingId: part.id, start: part.offset + s.start, end: part.offset + s.end,
                                  speaker: s.speaker.map { display[$0] ?? $0 },
                                  speakerName: s.speaker.map { names[$0] ?? language.defaultSpeaker($0) } ?? "", text: s.text))
            }
            if let notes = (try? db.meeting(part.id))?.decodedNotes {
                let ranges = MindMap.topicRanges(notes: notes, duration: part.length, untitled: String(localized: "議題"))
                topics += ranges.map { TopicSpan(title: $0.title, start: part.offset + $0.start, end: part.offset + $0.end, branch: $0.branch) }
            }
            marks += ((try? db.bookmarks(part.id)) ?? []).map { part.offset + $0.time }
        }
        self.lines = lines.sorted { $0.start < $1.start }
        self.topics = topics
        self.marks = marks
    }

    private func loadPeaks() {
        for part in parts where peaks[part.id] == nil && !loadingPeaks.contains(part.id) {
            guard let url = part.url else { continue }
            loadingPeaks.insert(part.id)
            let id = part.id
            Task {
                let values = try? await Task.detached(priority: .userInitiated) { try await AudioEdit.peaks(url, step: Self.peakStep) }.value
                loadingPeaks.remove(id)
                if let values {
                    // 錄音大小聲差很多：用那場第 98 百分位的峰值當 1，安靜的錄音也看得出起伏
                    let reference = values.sorted()[min(values.count - 1, Int(Double(values.count) * 0.98))]
                    peaks[id] = reference > 0.001 ? values.map { min(1, $0 / reference) } : values
                }
            }
        }
    }

    /// 某個時間的波形高度（0～1）：有錄音用峰值，沒有錄音或還沒讀好時用句子（有句子的地方中等高度）
    func level(from t0: Double, to t1: Double) -> Float {
        guard let part = parts.last(where: { $0.offset <= t0 }) ?? parts.first else { return 0 }
        if let values = peaks[part.id], !values.isEmpty {
            let a = max(0, Int((t0 - part.offset) / Self.peakStep)), b = min(values.count, max(a + 1, Int((t1 - part.offset) / Self.peakStep)))
            guard a < b else { return 0 }
            return values[a..<b].max() ?? 0
        }
        return lines.contains { $0.start < t1 && $0.end > t0 } ? 0.45 : 0.04
    }

    var isLoadingPeaks: Bool { !loadingPeaks.isEmpty }

    // MARK: 規則

    func kept(_ t: Double) -> Bool {
        t >= start && t <= end && !deleted.contains { t >= $0.lowerBound && t < $0.upperBound }
    }

    func keptDuration(_ a: Double = 0, _ b: Double? = nil) -> Double {
        let lo = max(a, start), hi = min(b ?? total, end)
        guard hi > lo else { return 0 }
        return hi - lo - deleted.reduce(0) { $0 + max(0, min(hi, $1.upperBound) - max(lo, $1.lowerBound)) }
    }

    var removedLines: Int { lines.filter { !kept($0.mid) }.count }
    var removedMarks: Int { marks.filter { !kept($0) }.count }

    /// 套用用的片段：每場自己的時間
    var plan: EditPlan {
        var pieces: [EditPlan.Piece] = []
        for part in parts {
            let lo = max(0, start - part.offset), hi = min(part.length, end - part.offset)
            guard hi > lo else { continue }
            let local = deleted.compactMap { r -> ClosedRange<Double>? in
                let a = r.lowerBound - part.offset, b = r.upperBound - part.offset
                return b > 0 && a < part.length ? max(0, a)...min(part.length, b) : nil
            }
            pieces += EditPlan.keeping(part.id, start: lo, end: hi, deleted: local)
        }
        return EditPlan(pieces: pieces)
    }

    /// 切點換成新時間軸上的時間
    var planCut: Double? {
        guard let cut, let part = parts.last(where: { $0.offset <= cut }) else { return nil }
        return plan.map(part.id, cut - part.offset)
    }

    // MARK: 編輯

    func setStart(_ t: Double) {
        start = min(max(0, t), end - 1)
        if let c = cut, c <= start { cut = nil }
        stopPlaying()
    }

    func setEnd(_ t: Double) {
        end = max(min(total, t), start + 1)
        if let c = cut, c >= end { cut = nil }
        stopPlaying()
    }

    func deleteRange(_ range: ClosedRange<Double>) {
        let r = max(0, range.lowerBound)...min(total, range.upperBound)
        guard r.upperBound - r.lowerBound >= EditPlan.minimumPiece else { return }
        deleted = EditPlan.normalized(deleted + [r])
        if let c = cut, !kept(c) { cut = nil }
        selection = nil
        stopPlaying()
    }

    func deleteSelection() {
        if let selection { deleteRange(selection) }
    }

    func restore(_ range: ClosedRange<Double>) {
        deleted.removeAll { $0 == range }
        stopPlaying()
    }

    func restore(at t: Double) {
        deleted.removeAll { t >= $0.lowerBound && t < $0.upperBound }
        stopPlaying()
    }

    var canCut: Bool { mergedPart == nil && kept(playhead) && playhead > start + 1 && playhead < end - 1 }

    func toggleCut() {
        if cut != nil { cut = nil } else if canCut { cut = playhead }
        stopPlaying()
    }

    func cut(at t: Double) {
        guard mergedPart == nil, kept(t), t > start + 1, t < end - 1 else { return }
        cut = t
        stopPlaying()
    }

    /// 逐字稿選到的句子（右鍵的對象：點的那句在選取裡就是全部選取的，不在就只有那句）
    func targetLines(for line: Line) -> [Line] {
        selectedLines.contains(line.id) ? lines.filter { selectedLines.contains($0.id) } : [line]
    }

    func deleteLines(_ targets: [Line]) {
        let ranges = targets.map { $0.start...max($0.start + EditPlan.minimumPiece, $0.end) }
        deleted = EditPlan.normalized(deleted + ranges)
        if let c = cut, !kept(c) { cut = nil }
        selectedLines = []
        stopPlaying()
    }

    func restoreLines(_ targets: [Line]) {
        for line in targets { deleted.removeAll { line.mid >= $0.lowerBound && line.mid < $0.upperBound } }
        stopPlaying()
    }

    // MARK: 合併（§81.4）

    func merge(with other: Meeting, db: AppDatabase) {
        guard mergedPart == nil, let own = ownPart else { return }
        stopPlaying()
        // 這場的裁切先變成刪掉的段落，合併後兩端的把手放在整條的兩端
        var deletedHere = deleted
        if start > 0.05 { deletedHere.append(0...start) }
        if end < own.length - 0.05 { deletedHere.append(end...own.length) }
        let theirs = part(other, db: db, offset: 0)
        let first = other.createdAt < own.createdAt
        let shift = first ? theirs.length : 0
        var mine = own
        mine.offset = shift
        var placed = theirs
        placed.offset = first ? 0 : own.length
        parts = first ? [placed, mine] : [mine, placed]
        deleted = EditPlan.normalized(deletedHere.map { ($0.lowerBound + shift)...($0.upperBound + shift) })
        playhead += shift
        selection = nil
        cut = nil
        rebuildContent(db: db)
        start = 0
        end = total
        loadPeaks()
    }

    func unmerge(db: AppDatabase) {
        guard let own = ownPart, mergedPart != nil else { return }
        stopPlaying()
        let shift = own.offset, length = own.length
        var local = deleted.compactMap { r -> ClosedRange<Double>? in
            let a = r.lowerBound - shift, b = r.upperBound - shift
            return b > 0 && a < length ? max(0, a)...min(length, b) : nil
        }
        var newStart = max(0, start - shift), newEnd = min(length, end - shift)
        if let head = local.first, head.lowerBound <= 0.05 { newStart = max(newStart, head.upperBound); local.removeFirst() }
        if let tail = local.last, tail.upperBound >= length - 0.05 { newEnd = min(newEnd, tail.lowerBound); local.removeLast() }
        var mine = own
        mine.offset = 0
        parts = [mine]
        deleted = local
        start = newStart
        end = max(newEnd, newStart + 1)
        playhead = min(max(0, playhead - shift), length)
        selection = nil
        rebuildContent(db: db)
    }

    // MARK: 試聽（跳過刪掉、裁掉的部分）

    @ObservationIgnored private var player: AVPlayer?
    @ObservationIgnored private var observer: Any?
    @ObservationIgnored private var previewPlan: EditPlan?
    @ObservationIgnored private var previewAssets: [AVURLAsset] = []

    func togglePlay() {
        if isPlaying { stopPlaying() } else { Task { await play() } }
    }

    private func play() async {
        let plan = self.plan
        guard plan.duration > 0 else { return }
        let composition = AVMutableComposition()
        guard let track = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) else { return }
        var cursor = CMTime.zero
        // 來源的 asset 要留著到播完：放掉的話音軌接不上去，整段是空的
        var assets: [Int64: AVURLAsset] = [:]
        for piece in plan.pieces {
            let duration = CMTime(seconds: piece.length, preferredTimescale: 48_000)
            var inserted = false
            if let url = parts.first(where: { $0.id == piece.meetingId })?.url {
                let asset = assets[piece.meetingId] ?? AVURLAsset(url: url)
                assets[piece.meetingId] = asset
                if let source = try? await asset.loadTracks(withMediaType: .audio).first {
                    let range = CMTimeRange(start: CMTime(seconds: piece.start, preferredTimescale: 48_000), duration: duration)
                    inserted = (try? track.insertTimeRange(range, of: source, at: cursor)) != nil
                }
            }
            if !inserted { track.insertEmptyTimeRange(CMTimeRange(start: cursor, duration: duration)) }
            cursor = cursor + duration
        }
        previewAssets = Array(assets.values)
        // 從播放頭開始；播放頭在刪掉、裁掉的地方時從下一個留下的地方開始
        let part = parts.last { $0.offset <= playhead } ?? parts[0]
        var from = plan.snap(part.id, playhead - part.offset)
        if from >= plan.duration - 0.1 { from = 0 }
        let player = AVPlayer(playerItem: AVPlayerItem(asset: composition))
        #if DEBUG
        // 測試時不要從喇叭出聲（-debugMutePreview YES）
        player.isMuted = UserDefaults.standard.bool(forKey: "debugMutePreview")
        #endif
        self.player = player
        previewPlan = plan
        await player.seek(to: CMTime(seconds: from, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        observer = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.05, preferredTimescale: 600), queue: .main) { [weak self] time in
            MainActor.assumeIsolated { self?.tick(time.seconds) }
        }
        player.play()
        isPlaying = true
    }

    private func tick(_ seconds: Double) {
        guard isPlaying, let plan = previewPlan else { return }
        if seconds >= plan.duration - 0.05 {
            stopPlaying()
            return
        }
        if let source = plan.source(at: seconds), let part = parts.first(where: { $0.id == source.meetingId }) {
            playhead = part.offset + source.time
        }
    }

    func stopPlaying() {
        guard isPlaying || player != nil else { return }
        player?.pause()
        if let observer { player?.removeTimeObserver(observer) }
        observer = nil
        player = nil
        previewPlan = nil
        previewAssets = []
        isPlaying = false
    }

    func seek(_ t: Double) {
        let wasPlaying = isPlaying
        stopPlaying()
        playhead = min(max(0, t), total)
        if wasPlaying { Task { await play() } }
    }
}
