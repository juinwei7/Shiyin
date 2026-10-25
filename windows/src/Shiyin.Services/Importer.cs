using Shiyin.Data;
using Shiyin.IO;
using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>匯入的選填資料（R1.2）</summary>
public sealed record ImportOptions
{
    public string Title { get; init; } = "";
    public string? Date { get; init; }
    public string Participants { get; init; } = "";
    /// <summary>顯示用的來源名稱；沒給就用檔名</summary>
    public string? SourceName { get; init; }
    public string? TemplateId { get; init; }
    /// <summary>放進哪個資料夾（R9.2）；空字串是不放</summary>
    public string Folder { get; init; } = "";
    /// <summary>會議語言（M§16.3）；null 用設定的預設</summary>
    public MeetingLanguage? Language { get; init; }
    /// <summary>大約幾個人（M§26.2）：null 是自動判斷</summary>
    public int? MinSpeakers { get; init; }
}

public sealed class ImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>匯入錄音、影片（M§7、R3）</summary>
public static class Importer
{
    /// <summary>可以匯入的錄音、影片（R27 R3.1：Media Foundation 讀得了的格式，CAF 待 T-W9）</summary>
    public static readonly string[] MediaExtensions = ["m4a", "mp3", "wav", "aac", "flac", "mp4", "mov", "aiff", "aif", "m4v", "wma", "wmv", "ogg", "opus", "webm"];
    public static readonly string[] TranscriptExtensions = ["srt", "vtt", "txt"];
    public static readonly string[] MarkdownExtensions = ["md", "markdown"];

    public static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    public static bool IsMedia(string path) => MediaExtensions.Contains(Extension(path));

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public static DateOnly? ModificationDate(string path) =>
        File.Exists(path) ? DateOnly.FromDateTime(File.GetLastWriteTime(path)) : null;

    /// <summary>建立會議、複製錄音、排轉錄工作。複製失敗會刪掉剛建立的會議（M§7）</summary>
    /// <summary>usage：匿名使用統計（design-windows §12.10）；復原當掉的錄音（move）不算匯入</summary>
    public static Meeting ImportFile(string path, ImportOptions options, AppDatabase db, ShiyinPaths paths, AppSettings settings, bool move = false,
                                     UsageStats? usage = null)
    {
        var filename = Path.GetFileName(path);
        var title = options.Title.Trim();
        var draft = new Meeting
        {
            Title = title, TitleLocked = title.Length > 0,
            MeetingDate = options.Date ?? ImportRules.InferDate(filename, ModificationDate(path), Today),
            Participants = options.Participants.Trim(),
            SourceFilename = options.SourceName ?? filename,
            TemplateId = options.TemplateId ?? settings.DefaultTemplate, Source = "import", Folder = options.Folder,
            Language = (options.Language ?? settings.DefaultMeetingLanguage).Code(),
            MinSpeakers = options.MinSpeakers,
        };
        var meeting = db.InsertMeeting(draft, ImportRules.SafeExtension(filename));
        var destination = paths.AudioPath(meeting);
        var moved = false;
        try
        {
            paths.EnsureDirectories();
            if (move)
            {
                File.Move(path, destination, overwrite: true);
                moved = true;
            }
            else File.Copy(path, destination, overwrite: true);
            db.Enqueue(meeting.Id!.Value, JobKind.Transcribe);
            if (!move) usage?.Imported();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            try { db.DeleteMeeting(meeting.Id!.Value); } catch (Microsoft.Data.Sqlite.SqliteException) { }
            try
            {
                // 復原的錄音是唯一的一份：搬回原處，下次開啟再試（M§20.6、R18.6）
                if (moved) File.Move(destination, path);
                else File.Delete(destination);
            }
            catch (IOException) { }
            throw new ImportException(L.F("無法匯入「%@」：%@", filename, e.Message), e);
        }
        return meeting;
    }

    /// <summary>匯入逐字稿（srt、vtt、txt，R3.4）：建一場沒有錄音的會議</summary>
    public static Meeting ImportTranscript(string path, string folder, AppDatabase db, AppSettings settings)
    {
        var filename = Path.GetFileName(path);
        string text;
        try { text = ReadText(path); }
        catch (IOException) { throw new ImportException(L.F("讀不到「%@」", filename)); }
        var parsed = TranscriptImporter.Parse(text, Extension(path));
        if (parsed.Count == 0) throw new ImportException(L.F("「%@」裡沒有可以用的逐字稿內容", filename));
        var meeting = db.InsertMeeting(new Meeting
        {
            MeetingDate = ImportRules.InferDate(filename, ModificationDate(path), Today), SourceFilename = filename,
            TemplateId = settings.DefaultTemplate, Source = "transcript", Folder = folder, Language = settings.DefaultMeetingLanguage.Code(),
        }, null);
        var id = meeting.Id!.Value;
        db.SaveTranscript(id, parsed.Select((s, i) => new Segment
        {
            MeetingId = id, Seq = i, Start = s.Start, End = s.End, Text = s.Text, Kind = "final",
        }).ToList(), (int)parsed[^1].End, 0);
        if (settings.AutoSummarize && settings.SummarizerReady) db.Enqueue(id, JobKind.Summarize);
        return db.Meeting(id) ?? meeting;
    }

    /// <summary>UTF-8，不是的話試 UTF-16（同 Mac）</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.Unicode.GetString(bytes).TrimStart('﻿');
        }
    }
}
