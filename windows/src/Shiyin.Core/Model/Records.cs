using Shiyin.Text;

namespace Shiyin.Model;

/// <summary>ISO 8601 的現在時間（UTC、不帶小數秒），跟 Mac 的 Clock.now() 同格式</summary>
public static class Clock
{
    public static Func<DateTimeOffset> Source { get; set; } = () => DateTimeOffset.UtcNow;
    public static string Now() => Source().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>會議（資料表 meetings，M§4、§21.1、§23.2、§24.1）</summary>
public sealed class Meeting
{
    public long? Id { get; set; }
    public string Title { get; set; } = "";
    public bool TitleLocked { get; set; }
    public string MeetingDate { get; set; } = "";
    public string Participants { get; set; } = "";
    public string SourceFilename { get; set; } = "";
    public string AudioFile { get; set; } = "";
    public int? DurationSeconds { get; set; }
    public string? Notes { get; set; }
    public string? NotesBy { get; set; }
    /// <summary>目前的會議紀錄是用哪個範本整理的（v17、M§89）：整理、草稿時記下，舊的紀錄是 null</summary>
    public string? NotesTemplate { get; set; }
    public string CreatedAt { get; set; } = Clock.Now();
    public string UpdatedAt { get; set; } = Clock.Now();
    public string TemplateId { get; set; } = "general";
    public string UserNotes { get; set; } = "";
    /// <summary>recording / import / package / transcript</summary>
    public string Source { get; set; } = "import";
    /// <summary>mic / system / both</summary>
    public string Channels { get; set; } = "mic";
    public string? DeletedAt { get; set; }
    /// <summary>在哪個資料夾（v18、R118、M§92）：null 是不在資料夾；名字問資料夾表（MainState.FolderName、AppDatabase.FolderName）</summary>
    public long? FolderId { get; set; }
    public string Language { get; set; } = "zh";
    /// <summary>1 是用即時逐字稿整理的草稿，2 是使用者改過的草稿</summary>
    public int NotesDraft { get; set; }
    /// <summary>breeze / japanese / system（Windows 的快速辨識也寫 system）</summary>
    public string TranscribedWith { get; set; } = "";
    public int? MinSpeakers { get; set; }
    public double? SpeakerCheck { get; set; }
    public bool SpeakerHintDismissed { get; set; }
    /// <summary>v12（M§72）：資料夾已經決定了（自動放過、自己移過、建議按了不用）。定期會議的 series_id 欄位留著不用（D177）</summary>
    public bool FolderDecided { get; set; }
    /// <summary>v21（R125、M§99.3）：匯入的分享是誰分享的（名字，沒有名字時是信箱），顯示用；不是分享來的是 null</summary>
    public string? SharedFrom { get; set; }
    /// <summary>v21：匯入的是哪一個分享（再匯入時找之前那份，M§99.4）</summary>
    public string? SharedId { get; set; }

    public MeetingLanguage MeetingLanguage => MeetingLanguages.Parse(Language) ?? MeetingLanguage.Zh;

    public string DisplayTitle => Title.Length > 0 ? Title : PathText.DeletingExtension(SourceFilename);

    public MeetingNotes? DecodedNotes => MeetingNotes.Decode(Notes);

    public List<string> ParticipantList => Participants
        .Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries)
        .Select(SwiftText.TrimSpaces).Where(s => s.Length > 0).ToList();

    public Meeting Clone() => (Meeting)MemberwiseClone();
}

/// <summary>資料夾（資料表 folders，v18、R118、M§92）：會議、自動加入條件、側欄都記 id，名字只用來顯示（不能重複，區分大小寫）</summary>
public sealed record Folder(long Id, string Name, string CreatedAt);

public sealed class Segment
{
    public long? Id { get; set; }
    public long MeetingId { get; set; }
    public int Seq { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public bool LowConfidence { get; set; }
    public int? Speaker { get; set; }
    /// <summary>live（即時預覽）/ final</summary>
    public string Kind { get; set; } = "final";
    /// <summary>分聲道轉錄時：0 麥克風、1 電腦聲音</summary>
    public int? Channel { get; set; }
    /// <summary>使用者改過這句的說話者（M§24.2）</summary>
    public bool SpeakerManual { get; set; }

    public Segment Clone() => (Segment)MemberwiseClone();
}

/// <summary>說話者名字（資料表 speakers）</summary>
public sealed class SpeakerName
{
    public long MeetingId { get; set; }
    public int Idx { get; set; }
    public string Name { get; set; } = "";
    /// <summary>default / manual / voiceprint / suggestion</summary>
    public string NameSource { get; set; } = "default";
    public long? PersonId { get; set; }
    /// <summary>聲紋，Float32 little endian</summary>
    public byte[]? Embedding { get; set; }
    public double SpeechSeconds { get; set; }
    /// <summary>mic / system / mix</summary>
    public string AudioSource { get; set; } = "mix";
    public string? SuggestedName { get; set; }
    public string? SuggestionReason { get; set; }
    public string? SuggestionTime { get; set; }
}

public sealed class Bookmark
{
    public long? Id { get; set; }
    public long MeetingId { get; set; }
    public double Time { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>逐字稿校正建議（M§17.4）：pending、accepted、rejected</summary>
public sealed class Correction
{
    public long? Id { get; set; }
    public long MeetingId { get; set; }
    public long SegmentId { get; set; }
    public string Original { get; set; } = "";
    public string Suggestion { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string CreatedAt { get; set; } = Clock.Now();
}

public sealed class TemplateRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Builtin { get; set; }
    public string Definition { get; set; } = "";
    public string UpdatedAt { get; set; } = Clock.Now();
}

public sealed class NotesVersion
{
    public long? Id { get; set; }
    public long MeetingId { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>換掉這一版的那次存檔是什麼（重新整理、校稿、手動編輯…）</summary>
    public string Reason { get; set; } = "";
    public string CreatedAt { get; set; } = Clock.Now();
    /// <summary>這一版是用哪個範本整理的（v17、M§89）；不知道是 null</summary>
    public string? TemplateId { get; set; }
}

/// <summary>版本紀錄的一版（R115、M§89）：VersionId 是 null 的是目前的紀錄；Origin、Time 是這一版怎麼來的、什麼時候（最舊的一版不知道）</summary>
public sealed record NotesHistoryEntry(long? VersionId, MeetingNotes Notes, string? TemplateId, string? Origin, string? Time)
{
    public bool IsCurrent => VersionId is null;
}

public enum JobKind { Transcribe, Summarize, Revise, Compress, Correct, NameSpeakers, Rediarize, Clean }
public enum JobLane { Transcribe, Llm, Assist }
public enum JobStatus { Queued, Running, Done, Failed, Cancelled }

public static class JobKinds
{
    public static readonly JobKind[] All = Enum.GetValues<JobKind>();

    public static string Code(this JobKind k) => k switch
    {
        JobKind.Transcribe => "transcribe",
        JobKind.Summarize => "summarize",
        JobKind.Revise => "revise",
        JobKind.Compress => "compress",
        JobKind.Correct => "correct",
        JobKind.NameSpeakers => "nameSpeakers",
        JobKind.Clean => "clean",
        _ => "rediarize",
    };

    public static JobKind? Parse(string? code) => All.Cast<JobKind?>().FirstOrDefault(k => k!.Value.Code() == code);

    /// <summary>轉錄線、整理線、輔助線（M§21.2）</summary>
    public static JobLane Lane(this JobKind k) => k switch
    {
        JobKind.Transcribe or JobKind.Compress or JobKind.Rediarize => JobLane.Transcribe,
        JobKind.Summarize or JobKind.Revise => JobLane.Llm,
        _ => JobLane.Assist,
    };

    /// <summary>背景的輔助工作：不發通知、不改會議的狀態</summary>
    /// <summary>要用整理工具的工作（M§34）</summary>
    public static bool UsesSummarizer(this JobKind k) => k is JobKind.Summarize or JobKind.Revise or JobKind.Correct or JobKind.NameSpeakers or JobKind.Clean;
    /// <summary>精簡逐字稿（M§70）也是輔助工作：在輔助線、不發通知</summary>
    public static bool IsAuxiliary(this JobKind k) => k is JobKind.Compress or JobKind.Correct or JobKind.NameSpeakers or JobKind.Rediarize or JobKind.Clean;

    public static string Code(this JobLane l) => l switch { JobLane.Llm => "llm", JobLane.Assist => "assist", _ => "transcribe" };
    public static JobLane? ParseLane(string? code) => code switch { "transcribe" => JobLane.Transcribe, "llm" => JobLane.Llm, "assist" => JobLane.Assist, _ => null };

    public static string Code(this JobStatus s) => s.ToString().ToLowerInvariant();
    public static JobStatus ParseStatus(string code) => Enum.Parse<JobStatus>(code, ignoreCase: true);
}

public sealed class Job
{
    public long? Id { get; set; }
    public long MeetingId { get; set; }
    public JobKind Kind { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? Payload { get; set; }
    public string? Error { get; set; }
    public string CreatedAt { get; set; } = Clock.Now();
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    /// <summary>執行時拾音意外結束的次數（M§20.5）</summary>
    public int Crashes { get; set; }
    /// <summary>失敗的種類，不含內容（M§21.9）</summary>
    public string? ErrorKind { get; set; }
    /// <summary>排隊的順序（migration v19、M§96.1）：大的先拿，一樣的照 id；平常是 0</summary>
    public int Priority { get; set; }
}

/// <summary>詞彙表的群組（R126、M§100.1）：像標籤，一個詞可以在好幾組</summary>
public sealed record GlossaryGroup(long Id, string Name, string CreatedAt);

public sealed class GlossaryTerm
{
    public long? Id { get; set; }
    public string Term { get; set; } = "";
    public string CreatedAt { get; set; } = Clock.Now();
}

public sealed class Person
{
    public long? Id { get; set; }
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = Clock.Now();
}

/// <summary>每位說話者的聲紋、說話秒數、收音（M§21.4）</summary>
public sealed record SpeakerVoice(float[] Embedding, double Seconds, string Source);

/// <summary>檔名處理：跟 NSString 的 deletingPathExtension、pathExtension 一樣</summary>
public static class PathText
{
    /// <summary>最後一段的副檔名；開頭是點的檔名（.hidden）沒有副檔名</summary>
    public static string Extension(string path)
    {
        var name = LastComponent(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[(dot + 1)..];
    }

    public static string DeletingExtension(string path)
    {
        var ext = Extension(path);
        return ext.Length == 0 && !LastComponent(path).EndsWith('.') ? path : path[..(path.Length - ext.Length - 1)];
    }

    public static string LastComponent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}

/// <summary>會議的參考文件的種類（M§39.2）：PDF、投影片有頁碼</summary>
public enum DocumentKind { Pdf, Docx, Pptx, Text, Image }

public static class DocumentKinds
{
    public static string Raw(this DocumentKind kind) => kind switch
    {
        DocumentKind.Pdf => "pdf", DocumentKind.Docx => "docx", DocumentKind.Pptx => "pptx", DocumentKind.Image => "image", _ => "text",
    };

    public static DocumentKind? Parse(string? raw) => raw switch
    {
        "pdf" => DocumentKind.Pdf, "docx" => DocumentKind.Docx, "pptx" => DocumentKind.Pptx, "text" => DocumentKind.Text, "image" => DocumentKind.Image,
        _ => null,
    };

    public static bool IsPaged(this DocumentKind kind) => kind is DocumentKind.Pdf or DocumentKind.Pptx;
}

/// <summary>會議的參考文件（資料表 documents，M§39.1、R61）：原始檔在 documents/&lt;file&gt;，讀出來的文字每頁一項</summary>
public sealed class MeetingDocument
{
    public long? Id { get; set; }
    public long MeetingId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>documents 底下的路徑：&lt;id&gt;/&lt;檔名&gt;（用 /，同 Mac）</summary>
    public string File { get; set; } = "";
    public string Kind { get; set; } = "text";
    /// <summary>JSON 字串陣列</summary>
    public string Pages { get; set; } = "[]";
    public bool Ocr { get; set; }
    public string CreatedAt { get; set; } = Clock.Now();
    /// <summary>會議中的畫面（M§83.1、R109）：錄音時間（秒）；null 是一般的參考文件</summary>
    public double? At { get; set; }

    public DocumentKind DocumentKind => DocumentKinds.Parse(Kind) ?? DocumentKind.Text;

    public List<string> PageTexts
    {
        get
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(Pages) ?? []; }
            catch (System.Text.Json.JsonException) { return []; }
        }
    }

    /// <summary>頁的文字存成 JSON：跟 Mac 的 JSONEncoder（.withoutEscapingSlashes）一樣</summary>
    public static string EncodePages(IEnumerable<string> pages) => SwiftJsonWriter.Write(JNode.Of(pages), sortKeys: false, escapeSlashes: false);
}
