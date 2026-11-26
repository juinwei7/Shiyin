import Foundation
import GRDB

/// 麥克風的「建議收錄」區塊（R119、design §93.3）。區塊不重疊：新增、調整時碰到別塊就合併成一塊（使用者的）
extension AppDatabase {
    func micRegions(_ meetingId: Int64) throws -> [MicRegion] {
        try pool.read { db in try Self.micRegions(db, meetingId) }
    }

    static func micRegions(_ db: Database, _ meetingId: Int64) throws -> [MicRegion] {
        try MicRegion.filter(Column("meeting_id") == meetingId).order(Column("start"), Column("id")).fetchAll(db)
    }

    /// 偵測的結果：換掉自動產生的，跟使用者的重疊的捨棄（使用者的優先）；記下基準、門檻
    func saveMicDetection(_ meetingId: Int64, _ result: MicRegions.Result) throws {
        try pool.write { db in
            try db.execute(sql: "DELETE FROM mic_regions WHERE meeting_id = ? AND origin = ?", arguments: [meetingId, MicRegion.auto])
            let mine = try Self.micRegions(db, meetingId)
            for r in result.regions where !mine.contains(where: { $0.start < r.end && r.start < $0.end }) {
                var region = MicRegion(meetingId: meetingId, start: r.start, end: r.end)
                try region.insert(db)
            }
            try db.execute(sql: "UPDATE meetings SET mic_floor_db = ?, mic_threshold_db = ? WHERE id = ?",
                           arguments: [result.floorDb, result.thresholdDb, meetingId])
        }
    }

    /// 新增一塊（使用者的）；回傳放好的那塊（可能跟別塊合併了）
    @discardableResult
    func addMicRegion(_ meetingId: Int64, start: Double, end: Double) throws -> MicRegion? {
        try pool.write { db in try Self.placeMicRegion(db, meetingId, replacing: nil, start: start, end: end) }
    }

    /// 改一塊的開始、結束（變成使用者的）
    @discardableResult
    func updateMicRegion(_ id: Int64, start: Double, end: Double) throws -> MicRegion? {
        try pool.write { db in
            guard let region = try MicRegion.fetchOne(db, key: id) else { return nil }
            return try Self.placeMicRegion(db, region.meetingId, replacing: id, start: start, end: end)
        }
    }

    /// 封包裡的區塊照原樣放回（design §93.3）
    func importMicRegion(_ meetingId: Int64, start: Double, end: Double, origin: String) throws {
        try pool.write { db in
            var region = MicRegion(meetingId: meetingId, start: start, end: end, origin: origin)
            try region.insert(db)
        }
    }

    func deleteMicRegion(_ id: Int64) throws {
        _ = try pool.write { db in try MicRegion.deleteOne(db, key: id) }
    }

    /// 播放時聽哪一邊、麥克風只播建議收錄（R119.2、R119.5），每場會議記住
    func setPlayChannels(_ meetingId: Int64, _ channels: PlayChannels) throws {
        try pool.write { db in try db.execute(sql: "UPDATE meetings SET play_channels = ? WHERE id = ?", arguments: [channels.rawValue, meetingId]) }
    }

    func setPlayMicRegions(_ meetingId: Int64, _ on: Bool) throws {
        try pool.write { db in try db.execute(sql: "UPDATE meetings SET play_mic_regions = ? WHERE id = ?", arguments: [on, meetingId]) }
    }

    func clearMicRegions(_ meetingId: Int64) throws {
        try pool.write { db in try db.execute(sql: "DELETE FROM mic_regions WHERE meeting_id = ?", arguments: [meetingId]) }
    }

    private static func placeMicRegion(_ db: Database, _ meetingId: Int64, replacing id: Int64?, start: Double, end: Double) throws -> MicRegion? {
        var s = max(0, min(start, end)), e = max(start, end)
        guard e > s else { return nil }
        if let id { _ = try MicRegion.deleteOne(db, key: id) }
        for other in try micRegions(db, meetingId) where other.start <= e && s <= other.end {
            s = min(s, other.start)
            e = max(e, other.end)
            _ = try MicRegion.deleteOne(db, key: other.id!)
        }
        var region = MicRegion(meetingId: meetingId, start: s, end: e, origin: MicRegion.user)
        try region.insert(db)
        return region
    }

    /// 這場的區塊整份換掉（編輯會議、還原，design §93.3）
    static func replaceMicRegions(_ db: Database, _ meetingId: Int64, _ regions: [MicRegion]) throws {
        try db.execute(sql: "DELETE FROM mic_regions WHERE meeting_id = ?", arguments: [meetingId])
        for r in regions {
            var region = MicRegion(meetingId: meetingId, start: r.start, end: r.end, origin: r.origin)
            try region.insert(db)
        }
    }
}
