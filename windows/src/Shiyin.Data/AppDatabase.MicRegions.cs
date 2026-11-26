using Shiyin.Model;
using Shiyin.Transcribe;

namespace Shiyin.Data;

/// <summary>麥克風的「建議收錄」區塊（R119、M§93.3）。區塊不重疊：新增、調整時碰到別塊就合併成一塊（使用者的）</summary>
public sealed partial class AppDatabase
{
    public List<MicRegion> MicRegions(long meetingId) => Read(db => MicRegions(db, meetingId));

    internal static List<MicRegion> MicRegions(Db db, long meetingId) =>
        db.Query("SELECT * FROM mic_regions WHERE meeting_id = ? ORDER BY start, id", meetingId).Select(ToMicRegion).ToList();

    /// <summary>偵測的結果：換掉自動產生的，跟使用者的重疊的捨棄（使用者的優先）；記下基準、門檻</summary>
    public void SaveMicDetection(long meetingId, MicRegionResult result) => Write(db =>
    {
        db.Execute("DELETE FROM mic_regions WHERE meeting_id = ? AND origin = ?", meetingId, MicRegion.Auto);
        var mine = MicRegions(db, meetingId);
        foreach (var (start, end) in result.Regions)
        {
            if (mine.Any(m => m.Start < end && start < m.End)) continue;
            InsertMicRegion(db, new MicRegion { MeetingId = meetingId, Start = start, End = end, Origin = MicRegion.Auto });
        }
        db.Execute("UPDATE meetings SET mic_floor_db = ?, mic_threshold_db = ? WHERE id = ?", result.FloorDb, result.ThresholdDb, meetingId);
    });

    /// <summary>新增一塊（使用者的）；回傳放好的那塊（可能跟別塊合併了）</summary>
    public MicRegion? AddMicRegion(long meetingId, double start, double end) => Write(db => PlaceMicRegion(db, meetingId, null, start, end));

    /// <summary>改一塊的開始、結束（變成使用者的）</summary>
    public MicRegion? UpdateMicRegion(long id, double start, double end) => Write(db =>
        db.QueryOne("SELECT * FROM mic_regions WHERE id = ?", id) is { } row ? PlaceMicRegion(db, ToMicRegion(row).MeetingId, id, start, end) : null);

    /// <summary>封包裡的區塊照原樣放回（M§93.3）</summary>
    public void ImportMicRegion(long meetingId, double start, double end, string origin) =>
        Write(db => InsertMicRegion(db, new MicRegion { MeetingId = meetingId, Start = start, End = end, Origin = origin }));

    public void DeleteMicRegion(long id) => Write(db => db.Execute("DELETE FROM mic_regions WHERE id = ?", id));

    /// <summary>播放時聽哪一邊（both / mic / system）、麥克風只播建議收錄（R119.2、R119.5），每場會議記住</summary>
    public void SetPlayChannels(long meetingId, string channels) =>
        Write(db => db.Execute("UPDATE meetings SET play_channels = ? WHERE id = ?", channels, meetingId));

    public void SetPlayMicRegions(long meetingId, bool on) =>
        Write(db => db.Execute("UPDATE meetings SET play_mic_regions = ? WHERE id = ?", on, meetingId));

    public void ClearMicRegions(long meetingId) => Write(db => db.Execute("DELETE FROM mic_regions WHERE meeting_id = ?", meetingId));

    static MicRegion? PlaceMicRegion(Db db, long meetingId, long? replacing, double start, double end)
    {
        double s = Math.Max(0, Math.Min(start, end)), e = Math.Max(start, end);
        if (!(e > s)) return null;
        if (replacing is long id) db.Execute("DELETE FROM mic_regions WHERE id = ?", id);
        foreach (var other in MicRegions(db, meetingId).Where(o => o.Start <= e && s <= o.End))
        {
            s = Math.Min(s, other.Start);
            e = Math.Max(e, other.End);
            db.Execute("DELETE FROM mic_regions WHERE id = ?", other.Id);
        }
        var region = new MicRegion { MeetingId = meetingId, Start = s, End = e, Origin = MicRegion.User };
        InsertMicRegion(db, region);
        return region;
    }

    internal static void InsertMicRegion(Db db, MicRegion r)
    {
        db.Execute("INSERT INTO mic_regions (meeting_id, start, end, origin) VALUES (?, ?, ?, ?)", r.MeetingId, r.Start, r.End, r.Origin);
        r.Id = db.LastInsertRowId;
    }

    /// <summary>這場的區塊整份換掉（編輯會議、還原，M§93.3）</summary>
    internal static void ReplaceMicRegions(Db db, long meetingId, IEnumerable<MicRegion> regions)
    {
        db.Execute("DELETE FROM mic_regions WHERE meeting_id = ?", meetingId);
        foreach (var r in regions) InsertMicRegion(db, new MicRegion { MeetingId = meetingId, Start = r.Start, End = r.End, Origin = r.Origin });
    }
}
