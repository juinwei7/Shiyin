using System.IO.Compression;
using System.Text;
using Shiyin.Data;
using Shiyin.IO;
using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>
/// 整場會議封包 .shiyin（M§10、R7.4、R3.3）：zip 的根目錄有 manifest.json、meeting.json、transcript.json、audio.*，
/// 跟 Mac 互相匯入（design-windows §13）。JSON 的格式在 Core 的 PackageJson，跟 Mac 的標準答案比對過
/// </summary>
public static class MeetingPackage
{
    public sealed class PackageException(string message) : Exception(message)
    {
        public static PackageException Invalid(string reason) => new(L.F("這不是有效的拾音封包：%@", reason));
        public static PackageException TooNew(int version) => new(L.F("這個封包是新版拾音匯出的（格式 %lld），請先更新拾音再匯入", version));
    }

    public static void Export(ExportBundle b, string destination, ShiyinPaths paths, string appVersion)
    {
        var m = b.Meeting;
        string? audioName = paths.HasAudio(m) ? "audio." + Importer.Extension(m.AudioFile) : null;
        var temp = destination + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                Add(zip, "manifest.json", PackageJson.Write(new PackageJson.Manifest(PackageJson.FormatVersion, appVersion, Clock.Now(), audioName)));
                Add(zip, "meeting.json", PackageJson.Write(new PackageJson.MeetingInfo(
                    m.Title, m.TitleLocked, m.MeetingDate, m.Participants, m.SourceFilename, m.DurationSeconds, m.TemplateId, m.UserNotes,
                    m.Channels, m.Folder, m.Language, b.Notes, m.NotesBy,
                    b.Speakers.Select(s => (s.Key, s.Value)).ToList(), b.Bookmarks.Select(x => (x.Time, x.Label)).ToList())));
                Add(zip, "transcript.json", PackageJson.Write(b.Segments.Select(s =>
                    new PackageJson.SegmentInfo(s.Start, s.End, s.Text, s.LowConfidence, s.Speaker))));
                // 錄音已經壓縮過，不再壓
                if (audioName is not null) zip.CreateEntryFromFile(paths.AudioPath(m), audioName, CompressionLevel.NoCompression);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    static void Add(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// folder：封包沒帶資料夾時放進的資料夾（目前正在看的那個）。在背景執行（M§21.13）。
    /// 只讀根目錄的這幾個檔案，不解開整個 zip：封包可能來自別人，裡面的路徑可以指到這台電腦上的任何地方
    /// </summary>
    public static Meeting Import(string path, AppDatabase db, ShiyinPaths paths, string folder = "")
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw PackageException.Invalid(L.T("壓縮檔讀寫失敗")); }
        using (zip)
        {
            var manifest = Read(zip, "manifest.json") is { } mj ? PackageJson.ReadManifest(mj) : null;
            if (manifest is null) throw PackageException.Invalid(L.T("缺少 manifest.json"));
            if (manifest.Format > PackageJson.FormatVersion) throw PackageException.TooNew(manifest.Format);
            var info = Read(zip, "meeting.json") is { } ij ? PackageJson.ReadMeeting(ij) : null;
            if (info is null) throw PackageException.Invalid(L.T("缺少 meeting.json"));
            // transcript.json 存在卻讀不出來就報錯，不要當成沒有逐字稿（M§20.10、R18.10）
            var segments = new List<PackageJson.SegmentInfo>();
            if (Read(zip, "transcript.json") is { } tj)
                segments = PackageJson.ReadSegments(tj) ?? throw PackageException.Invalid(L.T("transcript.json 讀不出來"));
            ZipArchiveEntry? audio = null;
            if (manifest.Audio is { } audioName)
            {
                audio = Entry(zip, audioName);
                if (audioName.Length == 0 || audioName.StartsWith('.') || audioName.IndexOfAny(['/', '\\', ':']) >= 0 || audio is null)
                    throw PackageException.Invalid(L.T("封包裡的錄音檔不正確"));
            }

            var meeting = db.InsertMeeting(new Meeting
            {
                Title = info.Title, TitleLocked = info.TitleLocked, MeetingDate = info.MeetingDate, Participants = info.Participants,
                SourceFilename = info.SourceFilename, DurationSeconds = info.DurationSeconds, NotesBy = info.NotesBy,
                TemplateId = info.TemplateId, UserNotes = info.UserNotes, Source = "package", Channels = info.Channels,
                Folder = string.IsNullOrEmpty(info.Folder) ? folder : info.Folder, Language = info.Language ?? "zh",
            }, audio is null ? null : ImportRules.SafeExtension(audio.Name));
            var id = meeting.Id!.Value;
            try
            {
                if (audio is not null)
                {
                    paths.EnsureDirectories();
                    audio.ExtractToFile(paths.AudioPath(meeting), overwrite: true);
                }
                db.SaveTranscript(id, segments.Select((s, i) => new Segment
                {
                    MeetingId = id, Seq = i, Start = s.Start, End = s.End, Text = s.Text, LowConfidence = s.LowConfidence,
                    Speaker = s.Speaker, Kind = "final",
                }).ToList(), info.DurationSeconds ?? 0, 0);
                foreach (var (idx, name) in info.Speakers) db.RenameSpeaker(id, idx, name);
                foreach (var (time, label) in info.Bookmarks) db.AddBookmark(id, time, label);
                if (info.Notes is not null) db.SaveNotes(id, info.Notes, info.NotesBy, L.T("匯入"));
                db.UpdateUserNotes(id, info.UserNotes);
                return db.Meeting(id) ?? meeting;
            }
            catch
            {
                try { db.DeleteMeeting(id); } catch (Microsoft.Data.Sqlite.SqliteException) { }
                if (meeting.AudioFile.Length > 0) try { File.Delete(paths.AudioPath(meeting)); } catch (IOException) { }
                throw;
            }
        }
    }

    /// <summary>根目錄的檔案（不接受子資料夾裡的同名檔案）</summary>
    static ZipArchiveEntry? Entry(ZipArchive zip, string name) => zip.Entries.FirstOrDefault(e => e.FullName == name);

    static string? Read(ZipArchive zip, string name)
    {
        if (Entry(zip, name) is not { } entry) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
