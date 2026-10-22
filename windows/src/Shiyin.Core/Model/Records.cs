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
    public string CreatedAt { get; set; } = Clock.Now();
    public string UpdatedAt { get; set; } = Clock.Now();
    public string TemplateId { get; set; } = "general";
    public string UserNotes { get; set; } = "";
    /// <summary>recording / import / package / transcript</summary>
    public string Source { get; set; } = "import";
    /// <summary>mic / system / both</summary>
    public string Channels { get; set; } = "mic";
    public string? DeletedAt { get; set; }
    public string Folder { get; set; } = "";
    public string Language { get; set; } = "zh";
    /// <summary>1 是用即時逐字稿整理的草稿，2 是使用者改過的草稿</summary>
    public int NotesDraft { get; set; }
    /// <summary>breeze / japanese / system（Windows 的快速辨識也寫 system）</summary>
    public string TranscribedWith { get; set; } = "";
    public int? MinSpeakers { get; set; }
    public double? SpeakerCheck { get; set; }
    public bool SpeakerHintDismissed { get; set; }

    public MeetingLanguage MeetingLanguage => MeetingLanguages.Parse(Language) ?? MeetingLanguage.Zh;

    public string DisplayTitle => Title.Length > 0 ? Title : PathText.DeletingExtension(SourceFilename);

    public MeetingNotes? DecodedNotes => MeetingNotes.Decode(Notes);

    public List<string> ParticipantList => Participants
        .Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries)
        .Select(SwiftText.TrimSpaces).Where(s => s.Length > 0).ToList();

    public Meeting Clone() => (Meeting)MemberwiseClone();
}

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
    public string Reason { get; set; } = "";
    public string CreatedAt { get; set; } = Clock.Now();
}

public enum JobKind { Transcribe, Summarize, Revise, Compress, Correct, NameSpeakers, Rediarize }
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
    public static bool IsAuxiliary(this JobKind k) => k is JobKind.Compress or JobKind.Correct or JobKind.NameSpeakers or JobKind.Rediarize;

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
}

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
