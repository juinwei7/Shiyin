import Foundation

/// 編輯會議（R107、design §81.1、D178）：新的時間軸＝依序接起來的片段。裁切、刪掉中間幾段、合併都化成片段；
/// 分成兩場是新時間軸上的一個切點。純邏輯，Mac、Windows 同一套（標準答案 util/edit-plan.json）
struct EditPlan: Hashable, Codable {
    struct Piece: Hashable, Codable {
        var meetingId: Int64
        var start: Double
        var end: Double
        var length: Double { max(0, end - start) }
    }

    var pieces: [Piece]

    var duration: Double { pieces.reduce(0) { $0 + $1.length } }

    /// 一場會議保留的片段：start～end 之間，去掉 deleted（重疊的先合起來；太短的片段不要，避免留下一兩個取樣的碎片）
    static func keeping(_ meetingId: Int64, start: Double, end: Double, deleted: [ClosedRange<Double>]) -> [Piece] {
        var pieces: [Piece] = []
        var cursor = start
        for range in normalized(deleted) where range.upperBound > start && range.lowerBound < end {
            if range.lowerBound > cursor { pieces.append(Piece(meetingId: meetingId, start: cursor, end: range.lowerBound)) }
            cursor = max(cursor, range.upperBound)
        }
        if end > cursor { pieces.append(Piece(meetingId: meetingId, start: cursor, end: end)) }
        return pieces.filter { $0.length >= minimumPiece }
    }

    static let minimumPiece = 0.05

    /// 依開始排序、重疊或相接的合起來
    static func normalized(_ ranges: [ClosedRange<Double>]) -> [ClosedRange<Double>] {
        var out: [ClosedRange<Double>] = []
        for r in ranges.sorted(by: { $0.lowerBound < $1.lowerBound }) {
            if let last = out.last, r.lowerBound <= last.upperBound {
                out[out.count - 1] = last.lowerBound...max(last.upperBound, r.upperBound)
            } else {
                out.append(r)
            }
        }
        return out
    }

    /// 只有這場一段、從 0 開始：時間都不變（逐字稿右鍵分成兩場也走這裡，§64）
    func keepsWhole(_ meetingId: Int64) -> Bool {
        pieces.count == 1 && pieces[0].meetingId == meetingId && pieces[0].start == 0
    }

    /// 片段在新時間軸上的開始
    private func offset(of index: Int) -> Double { pieces[..<index].reduce(0) { $0 + $1.length } }

    private func pieceIndex(_ meetingId: Int64, _ t: Double) -> Int? {
        // 片段的結尾算在片段裡（最後一句常剛好結束在結尾）；相接的兩段取前面那段
        pieces.firstIndex { $0.meetingId == meetingId && t >= $0.start && t <= $0.end }
    }

    /// 原本那場的時間 → 新時間軸；不在任何片段裡回傳 nil
    func map(_ meetingId: Int64, _ t: Double) -> Double? {
        guard let i = pieceIndex(meetingId, t) else { return nil }
        return offset(of: i) + (t - pieces[i].start)
    }

    /// 不在片段裡時對到「下一個片段的開始」（同一場會議裡、時間在後面的第一段；後面沒有了就對到這場最後一段的結束，
    /// 這場一段都沒有時是 0）。用在我的筆記、會議紀錄的時間（D181）
    func snap(_ meetingId: Int64, _ t: Double) -> Double {
        if let mapped = map(meetingId, t) { return mapped }
        if let next = pieces.indices.first(where: { pieces[$0].meetingId == meetingId && pieces[$0].start >= t }) {
            return offset(of: next)
        }
        if let last = pieces.indices.last(where: { pieces[$0].meetingId == meetingId }) {
            return offset(of: last) + pieces[last].length
        }
        return 0
    }

    /// 一句話（start～end）留不留：中點在片段裡就留，開始、結束夾在那個片段裡再對應（D181）
    func mapSentence(_ meetingId: Int64, start: Double, end: Double) -> (start: Double, end: Double)? {
        let mid = (start + max(start, end)) / 2
        guard let i = pieceIndex(meetingId, mid) else { return nil }
        let piece = pieces[i], base = offset(of: i)
        let s = min(max(start, piece.start), piece.end), e = min(max(end, s), piece.end)
        return (base + s - piece.start, base + e - piece.start)
    }

    /// 新時間軸 → 原本那場的時間（試聽、播放頭）；超出時夾在兩端
    func source(at t: Double) -> (meetingId: Int64, time: Double)? {
        guard !pieces.isEmpty else { return nil }
        var rest = max(0, t)
        for piece in pieces {
            if rest <= piece.length { return (piece.meetingId, piece.start + rest) }
            rest -= piece.length
        }
        let last = pieces[pieces.count - 1]
        return (last.meetingId, last.end)
    }

    /// 新時間軸在 cut 切開：前、後兩份（R107.5）；cut 不在中間時回傳 nil
    func split(at cut: Double) -> (head: EditPlan, tail: EditPlan)? {
        guard cut > Self.minimumPiece, cut < duration - Self.minimumPiece else { return nil }
        var head: [Piece] = [], tail: [Piece] = []
        var t = 0.0
        for piece in pieces {
            let end = t + piece.length
            if end <= cut { head.append(piece) }
            else if t >= cut { tail.append(piece) }
            else {
                let at = piece.start + (cut - t)
                head.append(Piece(meetingId: piece.meetingId, start: piece.start, end: at))
                tail.append(Piece(meetingId: piece.meetingId, start: at, end: piece.end))
            }
            t = end
        }
        return (EditPlan(pieces: head), EditPlan(pieces: tail))
    }

    /// 我的筆記（user_notes）每行開頭的 [mm:ss] 照 snap 改；一行都不刪（D181）
    func remapUserNotes(_ text: String, meetingId: Int64) -> String {
        text.components(separatedBy: "\n").map { line in
            guard line.hasPrefix("["), let close = line.firstIndex(of: "]"),
                  let seconds = TimeFormat.seconds(String(line[line.index(after: line.startIndex)..<close])) else { return line }
            return "[" + TimeFormat.string(snap(meetingId, seconds)) + "]" + line[line.index(after: close)...]
        }.joined(separator: "\n")
    }
}

extension MeetingNotes {
    /// 每個時間點（條目、待辦、議題的開始、待確認）換成 f 算出來的時間；沒有時間的留空
    func mappingTimes(_ f: (Double) -> Double) -> MeetingNotes {
        func time(_ text: String) -> String { TimeFormat.seconds(text).map { TimeFormat.string(f($0)) } ?? text }
        func items(_ list: [Item]) -> [Item] { list.map { Item(text: $0.text, time: time($0.time)) } }
        var notes = self
        notes.summary = items(summary)
        notes.decisions = items(decisions)
        notes.openQuestions = items(openQuestions)
        notes.actionItems = actionItems.map { var a = $0; a.time = time($0.time); return a }
        notes.topics = topics.map { Topic(title: $0.title, start: time($0.start), points: items($0.points)) }
        notes.custom = custom.map { var c = $0; c.items = items($0.items); return c }
        notes.people = people.map { var p = $0; p.points = items($0.points); return p }
        notes.reviewItems = reviewItems.map { var r = $0; r.time = time($0.time); return r }
        return notes
    }
}
