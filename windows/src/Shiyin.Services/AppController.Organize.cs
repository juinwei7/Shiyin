using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>資料夾自動歸類的動作（R98、M§72，同 Mac 的 Store/OrganizeActions.swift）。定期會議（R99、M§73）D177 拿掉</summary>
public sealed partial class AppController
{
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

    /// <summary>自動放進資料夾之後的提示（M§72）；DEBUG 的 autofile 也走這裡</summary>
    public void Organized(long meetingId, string? folder)
    {
        Changed?.Invoke(null);
        var title = Db.Meeting(meetingId)?.DisplayTitle ?? "";
        if (folder is not null) Notice?.Invoke(L.F("「%@」已放進「%@」", title, folder));
    }
}
