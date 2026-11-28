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

    /// <summary>匯入的分享是誰分享的（顯示用）</summary>
    public void SetSharedFrom(long meetingId, string name) =>
        Write(db => db.Execute("UPDATE meetings SET shared_from = ? WHERE id = ?", name, meetingId));
}
