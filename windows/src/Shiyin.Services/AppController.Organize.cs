using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>資料夾自動歸類、定期會議的動作（R98、R99、M§72、§73，同 Mac 的 Store/OrganizeActions.swift）</summary>
public sealed partial class AppController
{
    // MARK: 定期會議

    /// <summary>確認時按「建立」：名稱空的時用預設名稱；weekday、minute、interval、people 照這一組，勾的會議加進去</summary>
    public MeetingSeries? CreateSeries(SeriesCandidate candidate, string name, IReadOnlyList<long> meetingIds)
    {
        var trimmed = name.Trim();
        var series = new MeetingSeries
        {
            Name = trimmed.Length == 0 ? candidate.Name : trimmed, Weekday = candidate.Weekday, Minute = candidate.Minute,
            IntervalWeeks = candidate.IntervalWeeks, People = [.. candidate.People],
        };
        MeetingSeries? created = null;
        Perform(null, () => created = Db.CreateSeries(series, meetingIds));
        return created;
    }

    /// <summary>「不是定期會議」：之後不再建議同一組</summary>
    public void RejectSeriesCandidate(SeriesCandidate candidate) => Perform(null, () => Db.RejectSeries(candidate.Key));

    public void RenameSeries(long id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        Perform(null, () => Db.RenameSeries(id, trimmed));
    }

    /// <summary>刪除定期會議：會議留著；之後不再建議同一組</summary>
    public void DeleteSeries(long id) => Perform(null, () => Db.DeleteSeries(id));

    /// <summary>加到定期會議、移出（series 是 null）；多選時一起（手動加入不檢查條件）</summary>
    public void SetSeries(IReadOnlyList<long> ids, long? series)
    {
        if (ids.Count == 0) return;
        Perform(null, () => Db.SetSeries(ids, series));
    }

    // MARK: 建議加入定期會議（R99.3、M§73）

    /// <summary>「加進去」：設成那個系列（同右鍵「加到定期會議」）</summary>
    public void AcceptSeriesJoin(long meetingId, long seriesId) => SetSeries([meetingId], seriesId);

    /// <summary>「不是」：記進 series_rejected（join|會議|系列），之後不再建議這場加入這個系列</summary>
    public void DismissSeriesJoin(long meetingId, long seriesId) =>
        Perform(meetingId, () => Db.RejectSeries(SeriesDetector.JoinKey(meetingId, seriesId)));

    // MARK: 資料夾的條件

    /// <summary>儲存條件（都空時移除）；applyNow：現在不在資料夾裡的會議也套用</summary>
    public void SaveFolderRule(FolderRule rule, bool applyNow)
    {
        Perform(null, () =>
        {
            Db.SaveFolderRule(rule);
            if (!applyNow || rule.IsEmpty) return;
            var ids = Db.MeetingsMatching(rule);
            if (ids.Count == 0) return;
            Db.SetFolder(ids, rule.Folder);
            Notice?.Invoke(L.F("已把 %lld 場會議放進「%@」", ids.Count, rule.Folder));
        });
    }

    // MARK: 建議放進資料夾（R98.3）

    public void AcceptFolderSuggestion(long meetingId, string folder) => MoveToFolder([meetingId], folder);

    /// <summary>「不用」：之後不再建議這場</summary>
    public void DismissFolderSuggestion(long meetingId) => Perform(meetingId, () => Db.SetFolderDecided(meetingId));

    // MARK: 每一種東西都能直接改、刪（R100、M§74）

    /// <summary>就地改一條紀錄（雙擊、右鍵「編輯這一條」）：存成一個版本，同手動編輯；空的、沒改的不存</summary>
    public void EditNoteItem(long meetingId, NoteItemRef item, string text)
    {
        var trimmed = text.Replace('\r', '\n').Trim();
        if (trimmed.Length == 0 || Db.Meeting(meetingId)?.DecodedNotes is not { } notes || notes.TextAt(item) is not { } current || current == trimmed) return;
        SaveEditedNotes(notes.WithText(item, trimmed), meetingId);
    }

    /// <summary>刪除一條紀錄：不確認，存成一個版本，提示怎麼復原（Windows 是紀錄分頁「編輯」旁的「復原上一版」）</summary>
    public void DeleteNoteItem(long meetingId, NoteItemRef item)
    {
        if (Db.Meeting(meetingId)?.DecodedNotes is not { } notes || notes.TextAt(item) is null) return;
        SaveEditedNotes(notes.Removing(item), meetingId);
        Notice?.Invoke(L.T("已刪除一條紀錄，可以按紀錄分頁「編輯」旁的「復原上一版」還原"));
    }

    public void RenameBookmark(long meetingId, long id, string label) => Perform(meetingId, () => Db.RenameBookmark(id, label.Trim()));

    /// <summary>改一個詞：改成已經有的詞時合併</summary>
    public void RenameTerm(long id, string term) => Perform(null, () => Db.RenameTerm(id, term));

    // MARK: 自動（轉錄完、整理完）

    /// <summary>自動放進資料夾、加入定期會議之後的提示（M§72、§73）；DEBUG 的 autofile、autojoin 也走這裡</summary>
    public void Organized(long meetingId, string? folder, MeetingSeries? series)
    {
        Changed?.Invoke(null);
        var title = Db.Meeting(meetingId)?.DisplayTitle ?? "";
        if (folder is not null) Notice?.Invoke(L.F("「%@」已放進「%@」", title, folder));
        else if (series is not null) Notice?.Invoke(L.F("「%@」已加到定期會議「%@」", title, series.Name));
    }
}
