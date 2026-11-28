using Shiyin.Model;

namespace Shiyin.Data;

/// <summary>透過企業伺服器分享會議（R125、M§99.3、D212）：只記這場分享過的 id，名單、連結、期限只在伺服器（同 Mac 的 AppDatabase 分享那段）</summary>
public sealed partial class AppDatabase
{
    public void RecordShare(long meetingId, string server, string shareId) => Write(db =>
        db.Execute("INSERT OR REPLACE INTO meeting_shares (meeting_id, server, share_id, created_at) VALUES (?, ?, ?, ?)",
                   meetingId, server, shareId, Clock.Now()));

    /// <summary>這場在這台伺服器分享過的（新的在前）</summary>
    public List<string> ShareIds(long meetingId, string server) => Read(db =>
        db.Query("SELECT share_id FROM meeting_shares WHERE meeting_id = ? AND server = ? ORDER BY created_at DESC, rowid DESC", meetingId, server)
            .Select(r => r.Str("share_id")).ToList());

    public void ForgetShare(string server, string shareId) =>
        Write(db => db.Execute("DELETE FROM meeting_shares WHERE server = ? AND share_id = ?", server, shareId));

    /// <summary>從這個分享匯入、還不在垃圾桶裡的會議（再匯入時問要不要取代，M§99.4）</summary>
    public List<long> MeetingsFromShare(string shareId) => Read(db =>
        db.Query("SELECT id FROM meetings WHERE shared_id = ? AND deleted_at IS NULL ORDER BY id", shareId).Select(r => r.Long("id")).ToList());
}
