using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shiyin.Text;

namespace Shiyin.Model;

/// <summary>資料夾的自動加入條件（R98、M§72，同 Mac 的 Store/Series.swift 的 FolderRule）：任一個條件符合就放進去</summary>
public sealed class FolderRule
{
    public string Folder { get; set; } = "";
    public List<string> Keywords { get; set; } = [];
    /// <summary>人物 id：選的人都有參加才算</summary>
    public List<long> People { get; set; } = [];
    public string? TemplateId { get; set; }
    public long? SeriesId { get; set; }
    public string CreatedAt { get; set; } = Clock.Now();

    public bool IsEmpty => Keywords.Count == 0 && People.Count == 0 && TemplateId is null && SeriesId is null;

    /// <summary>標題包含任一個詞（不分大小寫）、選的人都有參加、用這個範本、屬於這個定期會議：任一個符合就算</summary>
    public bool Matches(string title, IReadOnlySet<long> attended, string templateId, long? seriesId)
    {
        if (Keywords.Any(k => k.Length > 0 && title.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;
        if (People.Count > 0 && People.All(attended.Contains)) return true;
        if (TemplateId is { } t && t == templateId) return true;
        if (SeriesId is { } s && s == seriesId) return true;
        return false;
    }
}

/// <summary>定期會議（R99、M§73）</summary>
public sealed class MeetingSeries
{
    public long? Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>1～7：週日～週六（同 Mac 的 Calendar 的 weekday）</summary>
    public int Weekday { get; set; }
    /// <summary>當地時間的開始時間（從 0 點算的分鐘）；不知道時 null</summary>
    public int? Minute { get; set; }
    public int IntervalWeeks { get; set; } = 1;
    /// <summary>建立時那一群人（人物 id），自動加入時比對</summary>
    public List<long> People { get; set; } = [];
    public string CreatedAt { get; set; } = Clock.Now();

    public string FrequencyText => SeriesDetector.FrequencyText(Weekday, Minute, IntervalWeeks);

    /// <summary>刪除時記進「不是定期會議」，之後不再建議同一組（M§73）</summary>
    public string Key => SeriesDetector.Key(Weekday, Minute, People);
}

/// <summary>會議的星期幾、開始時間、第幾天（M§73）</summary>
public readonly record struct MeetingTime(int Day, int Weekday, int? Minute)
{
    public MeetingTime(int day, int? minute) : this(day, WeekdayOf(day), minute) { }

    /// <summary>
    /// 星期幾看 meeting_date；開始時間只有錄音的會議才知道：created_at 換成當地時間、跟 meeting_date 同一天時。
    /// 日期不對時 null。timeZone 是 null 時用這台電腦的時區
    /// </summary>
    public static MeetingTime? Of(Meeting meeting, TimeZoneInfo? timeZone = null)
    {
        var date = meeting.MeetingDate.Length > 10 ? meeting.MeetingDate[..10] : meeting.MeetingDate;
        // Swift 的 split 會略過空的片段、compactMap 會丟掉不是數字的
        var parts = date.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? (int?)n : null)
            .OfType<int>().ToList();
        if (parts.Count != 3) return null;
        var day = DayNumber(parts[0], parts[1], parts[2]);
        int? minute = null;
        if (meeting.Source == "recording" && DateTimeOffset.TryParseExact(meeting.CreatedAt, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:sszzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
        {
            var local = TimeZoneInfo.ConvertTime(created, timeZone ?? TimeZoneInfo.Local);
            if (local.Year == parts[0] && local.Month == parts[1] && local.Day == parts[2]) minute = local.Hour * 60 + local.Minute;
        }
        return new MeetingTime(day, minute);
    }

    /// <summary>公曆日期換成從 1970-01-01 算的天數（Howard Hinnant 的 days_from_civil，同 Mac）</summary>
    public static int DayNumber(int year, int month, int day)
    {
        var y = month <= 2 ? year - 1 : year;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;
        var doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    /// <summary>1970-01-01 是週四；1～7 是週日～週六</summary>
    public static int WeekdayOf(int day) => ((day + 4) % 7 + 7) % 7 + 1;
}

/// <summary>一組可能的定期會議（M§73）：MeetingIds 由舊到新，People 是核心人物（過半數場次都有的人）照 id 排序</summary>
public sealed record SeriesCandidate(string Key, List<long> MeetingIds, int Weekday, int? Minute, int IntervalWeeks, List<long> People, string Name)
{
    public string FrequencyText => SeriesDetector.FrequencyText(Weekday, Minute, IntervalWeeks);
}

/// <summary>找可能的定期會議（M§73、D173）：同一群人、星期幾相同、開始時間差不到一小時、每週或每兩週、至少 3 場；不靠標題</summary>
public static partial class SeriesDetector
{
    public sealed record Input(long Id, MeetingTime Time, IReadOnlySet<long> People, string Title);

    /// <summary>同一群人：共同的人物至少 2 位、Jaccard ≥ 0.5</summary>
    public static bool SamePeople(IReadOnlySet<long> a, IReadOnlySet<long> b)
    {
        var common = a.Count(b.Contains);
        return common >= 2 && (double)common / a.Union(b).Count() >= 0.5;
    }

    /// <summary>開始時間差不到 60 分鐘；任一邊不知道時算符合</summary>
    public static bool CloseInTime(int? a, int? b) => a is not int x || b is not int y || Math.Abs(x - y) < 60;

    /// <summary>一個定期會議比對用的資料：最近一場（不算這場）的第幾天</summary>
    public sealed record SeriesInfo(long Id, int Weekday, int? Minute, IReadOnlySet<long> People, int? LastDay);

    /// <summary>新的一場要不要加入（M§73）：Join 自動加入、Suggest 建議（Shared 是共同的人）、都不是時兩個都是 null</summary>
    public sealed record JoinDecision(long? Join, long? Suggest, IReadOnlyList<long> Shared);

    /// <summary>
    /// 星期幾相同、時間差不到 60 分鐘、跟最近一場相隔 1～35 天的系列裡：同一群人 → 自動加入（相隔最近的）；
    /// 不然至少一位共同的人 → 建議（共同的人最多、再來相隔最近的）。2026-11-16 用 AMI 真人錄音驗收時加的：
    /// 聲紋只認出一位時原本直接漏掉
    /// </summary>
    public static JoinDecision Decide(MeetingTime time, IReadOnlySet<long> attended, IEnumerable<SeriesInfo> series)
    {
        var eligible = series
            .Where(s => s.Weekday == time.Weekday && CloseInTime(s.Minute, time.Minute) && s.LastDay is int)
            .Select(s => (Info: s, Gap: Math.Abs(time.Day - s.LastDay!.Value)))
            .Where(x => x.Gap is >= 1 and <= 35).ToList();
        var strong = eligible.Where(x => SamePeople(attended, x.Info.People)).OrderBy(x => x.Gap).ThenBy(x => x.Info.Id).FirstOrDefault();
        if (strong.Info is not null) return new JoinDecision(strong.Info.Id, null, []);
        var weak = eligible.Select(x => (x.Info, x.Gap, Shared: x.Info.People.Count(attended.Contains))).Where(x => x.Shared > 0)
            .OrderByDescending(x => x.Shared).ThenBy(x => x.Gap).ThenBy(x => x.Info.Id).FirstOrDefault();
        if (weak.Info is null) return new JoinDecision(null, null, []);
        return new JoinDecision(null, weak.Info.Id, attended.Where(weak.Info.People.Contains).Order().ToList());
    }

    public static string JoinKey(long meeting, long series) => $"join|{meeting}|{series}";

    public static List<SeriesCandidate> Candidates(IEnumerable<Input> meetings, IReadOnlySet<string> rejected)
    {
        var groups = new List<List<Input>>();
        foreach (var m in meetings.Where(m => m.People.Count >= 2).OrderBy(m => m.Time.Day).ThenBy(m => m.Id))
        {
            var group = groups.FirstOrDefault(g => Fits(m, g));
            if (group is not null) group.Add(m); else groups.Add([m]);
        }
        return groups.Where(g => g.Count >= 3).Select(Candidate).Where(c => !rejected.Contains(c.Key)).ToList();
    }

    static bool Fits(Input m, List<Input> group)
    {
        var last = group[^1];
        var gap = m.Time.Day - last.Time.Day;
        if (m.Time.Weekday != last.Time.Weekday || gap < 7 || gap > 28 || !CloseInTime(m.Time.Minute, last.Time.Minute)) return false;
        return SamePeople(m.People, Core(group.Select(g => g.People)));
    }

    /// <summary>過半數場次都有的人</summary>
    public static HashSet<long> Core(IEnumerable<IReadOnlySet<long>> people)
    {
        var list = people.ToList();
        var counts = new Dictionary<long, int>();
        foreach (var set in list)
            foreach (var id in set) counts[id] = counts.GetValueOrDefault(id) + 1;
        return counts.Where(p => p.Value * 2 > list.Count).Select(p => p.Key).ToHashSet();
    }

    static SeriesCandidate Candidate(List<Input> group)
    {
        var gaps = group.Skip(1).Zip(group, (a, b) => a.Time.Day - b.Time.Day).Order().ToList();
        var minutes = group.Select(g => g.Time.Minute).OfType<int>().Order().ToList();
        // 偶數個時取前面那個中位數
        int? minute = minutes.Count == 0 ? null : minutes[(minutes.Count - 1) / 2];
        var people = Core(group.Select(g => g.People)).Order().ToList();
        var weekday = group[0].Time.Weekday;
        return new SeriesCandidate(Key(weekday, minute, people), group.Select(g => g.Id).ToList(), weekday, minute,
            gaps[(gaps.Count - 1) / 2] >= 14 ? 2 : 1, people, DefaultName(group.Select(g => g.Title).ToList(), weekday, minute));
    }

    public static string Key(int weekday, int? minute, IEnumerable<long> people) =>
        $"{weekday}|{(minute is int m ? m / 60 : -1)}|" + string.Join(",", people.Order());

    // MARK: 名稱、頻率

    /// <summary>所有標題最長的共同子字串（頭尾去掉數字、日期、空白、標點）；沒有時「週一 10:00 的會議」（介面語言）</summary>
    public static string DefaultName(IReadOnlyList<string> titles, int weekday, int? minute)
    {
        if (CommonWord(titles) is { } common) return common;
        var day = WeekdayName(weekday);
        if (minute is int m) return MeetingLanguages.Ui($"{day} {TimeText(m)} 的會議", $"{day} {TimeText(m)} meeting");  // l10n-skip
        return MeetingLanguages.Ui($"{day}的會議", $"{day} meeting");  // l10n-skip
    }

    /// <summary>
    /// 共同的詞：標題先拿掉數字和跟著的日期單位（11/3、11月3日、2026-11-16），最長的共同子字串去掉頭尾的空白、標點後
    /// 至少 2 個字（只有拉丁字母時至少 3 個字母）。字數照 Swift 的 Character（字形叢集）
    /// </summary>
    public static string? CommonWord(IReadOnlyList<string> titles)
    {
        var list = titles.Select(t => SwiftText.Characters(WithoutNumbers(t))).Where(c => c.Count > 0).ToList();
        if (list.Count < 2) return null;
        var shortest = list.MinBy(c => c.Count)!;
        var lowered = list.Select(c => SwiftText.Lower(string.Concat(c))).ToList();
        for (var length = shortest.Count; length >= 2; length--)
            for (var start = 0; start <= shortest.Count - length; start++)
            {
                var piece = string.Concat(shortest.Skip(start).Take(length));
                var lower = SwiftText.Lower(piece);
                if (!lowered.All(t => t.Contains(lower, StringComparison.Ordinal))) continue;
                var cleaned = Clean(piece);
                if (IsSignificant(cleaned) && lowered.All(t => IsWholeWord(SwiftText.Lower(cleaned), t))) return cleaned;
            }
        return null;
    }

    /// <summary>
    /// 英文要是完整的單字：頭尾是英文字母或數字時，前後不能接著英文字母或數字（「Control」「Conceptual」不會取出「Con」，M§73）。
    /// 中文沒有空格，頭尾是中文時不檢查。字照 Swift 的 Character
    /// </summary>
    public static bool IsWholeWord(string word, string text)
    {
        var w = SwiftText.Characters(word);
        var t = SwiftText.Characters(text);
        if (w.Count == 0 || w.Count > t.Count) return false;
        static bool Latin(string c) => c.Length == 1 && c[0] < 128 && char.IsLetterOrDigit(c[0]);
        for (var i = 0; i <= t.Count - w.Count; i++)
        {
            if (!w.Select((c, k) => c == t[i + k]).All(x => x)) continue;
            var before = !Latin(w[0]) || i == 0 || !Latin(t[i - 1]);
            var after = !Latin(w[^1]) || i + w.Count == t.Count || !Latin(t[i + w.Count]);
            if (before && after) return true;
        }
        return false;
    }

    /// <summary>前面接英文字母的數字（Q3、v2）留著</summary>
    [GeneratedRegex(@"(?<![A-Za-z])[0-9０-９]+[年月日號号/.\-]?")]  // l10n-skip
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string WithoutNumbers(string title) => Spaces().Replace(NumberPattern().Replace(title, " "), " ");

    /// <summary>頭尾去掉空白、換行、標點、符號（Swift 的 CharacterSet：whitespacesAndNewlines、punctuationCharacters、symbols）</summary>
    public static string Clean(string text)
    {
        var runes = text.EnumerateRunes().ToList();
        static bool Edge(Rune r) => Rune.IsWhiteSpace(r) || Rune.IsPunctuation(r) || Rune.IsSymbol(r);
        var start = 0;
        var end = runes.Count;
        while (start < end && Edge(runes[start])) start++;
        while (end > start && Edge(runes[end - 1])) end--;
        var sb = new StringBuilder();
        for (var i = start; i < end; i++) sb.Append(runes[i].ToString());
        return sb.ToString();
    }

    /// <summary>至少 2 個字母（CharacterSet.letters：L、M 類），只有 ASCII 時至少 3 個</summary>
    public static bool IsSignificant(string word)
    {
        var letters = word.EnumerateRunes().Where(r => Rune.GetUnicodeCategory(r) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark).ToList();
        if (letters.Count < 2) return false;
        var latinOnly = letters.All(r => r.IsAscii);
        return !latinOnly || letters.Count >= 3;
    }

    static readonly string[] WeekdaysZh = ["週日", "週一", "週二", "週三", "週四", "週五", "週六"];  // l10n-skip
    static readonly string[] WeekdaysEn = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>介面語言的星期幾：「週一」「Monday」</summary>
    public static string WeekdayName(int weekday)
    {
        var i = Math.Clamp(weekday - 1, 0, 6);
        return MeetingLanguages.Ui(WeekdaysZh[i], WeekdaysEn[i]);
    }

    public static string TimeText(int minute) => $"{minute / 60}:{minute % 60:00}";

    /// <summary>「每週一 10:00」「每兩週的週一 10:00」「每週一」</summary>
    public static string FrequencyText(int weekday, int? minute, int intervalWeeks)
    {
        var day = WeekdayName(weekday);
        var time = minute is int m ? " " + TimeText(m) : "";
        var enTime = minute is int n ? " at " + TimeText(n) : "";
        return intervalWeeks >= 2
            ? MeetingLanguages.Ui($"每兩週的{day}{time}", $"Every other {day}{enTime}")  // l10n-skip
            : MeetingLanguages.Ui($"每{day}{time}", $"Every {day}{enTime}");  // l10n-skip
    }
}

/// <summary>不在資料夾的會議很像某個資料夾裡的會議（R98.3、M§72）：Reason 是都有這幾位參加（People）或標題都有這個詞（Title）</summary>
public sealed record FolderSuggestion(string Folder, IReadOnlyList<long>? People, string? Title, int Count);

public static class FolderSuggester
{
    public sealed record Input(long Id, string Folder, string Title, IReadOnlySet<long> People);

    /// <summary>通用的詞不算共同的詞</summary>
    public static readonly HashSet<string> GenericWords = ["會議", "会议", "會議記錄", "meeting", "meetings", "討論", "讨论", "会議", "회의"];  // l10n-skip

    /// <summary>
    /// 跟每個資料夾（至少 2 場）比：同一群人的場數，或標題有共同的詞的場數；至少 2 場才建議，取場數最多的（一樣多時名稱排前面的、
    /// 同一個資料夾人的比較準）。folderOrder 是資料夾名稱的排序（同 Mac 的 localizedStandardCompare）
    /// </summary>
    public static FolderSuggestion? Best(Input meeting, IEnumerable<Input> others, IComparer<string> folderOrder)
    {
        FolderSuggestion? best = null;
        var byFolder = others.Where(o => o.Folder.Length > 0 && o.Id != meeting.Id).GroupBy(o => o.Folder).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var folder in byFolder.Keys.Order(folderOrder))
        {
            var members = byFolder[folder];
            if (members.Count < 2) continue;
            var options = new List<FolderSuggestion>();
            var same = members.Where(m => SeriesDetector.SamePeople(meeting.People, m.People)).ToList();
            if (same.Count >= 2)
            {
                var core = SeriesDetector.Core(same.Select(m => m.People));
                core.IntersectWith(meeting.People);
                options.Add(new FolderSuggestion(folder, core.Order().ToList(), null, same.Count));
            }
            var words = new Dictionary<string, int>();
            foreach (var member in members)
                if (SeriesDetector.CommonWord([meeting.Title, member.Title]) is { } word && !GenericWords.Contains(SwiftText.Lower(word)))
                    words[word] = words.GetValueOrDefault(word) + 1;
            // 場數最多的詞；一樣多時照字串排序取第一個
            var top = words.OrderByDescending(p => p.Value).ThenBy(p => p.Key, SwiftText.Comparer).FirstOrDefault();
            if (top.Key is not null && top.Value >= 2) options.Add(new FolderSuggestion(folder, null, top.Key, top.Value));
            foreach (var option in options)
                if (best is null || option.Count > best.Count) best = option;
        }
        return best;
    }
}

/// <summary>定期會議的上一場（R99.5、M§73）：它的待辦（加上它的 followUps 裡還沒完成的）、未決問題</summary>
public sealed record PreviousMeeting(string Title, string Date, List<MeetingNotes.ActionItem> Items, List<MeetingNotes.Item> OpenQuestions)
{
    /// <summary>沒有會議紀錄、沒有待辦也沒有未決問題時是 null（提示詞不加這一段）</summary>
    public static PreviousMeeting? Of(Meeting meeting)
    {
        if (meeting.DecodedNotes is not { } notes) return null;
        var items = notes.ActionItems.Where(i => i.Task.Length > 0).ToList();
        foreach (var f in notes.FollowUps)
            if (f.Status != FollowUpStatus.Done && !items.Any(i => SeriesTasks.Same(i.Task, f.Task)))
                items.Add(new MeetingNotes.ActionItem(f.Owner, f.Task, ""));
        if (items.Count == 0 && notes.OpenQuestions.Count == 0) return null;
        return new PreviousMeeting(meeting.DisplayTitle, meeting.MeetingDate, items, notes.OpenQuestions);
    }

    /// <summary>
    /// 整理後的驗證：task 對不到給的待辦的丟掉、同一條只留第一個；沒給上一場（null）時清空。
    /// 對到的那一條，task 和 owner 換成上一場原本寫的（模型會把沒有負責人時提示詞寫的「(unassigned)」抄進 owner，M§73）
    /// </summary>
    public static List<MeetingNotes.FollowUp> ValidFollowUps(IEnumerable<MeetingNotes.FollowUp> followUps, PreviousMeeting? previous)
    {
        if (previous is null) return [];
        var used = new HashSet<int>();
        var result = new List<MeetingNotes.FollowUp>();
        foreach (var f in followUps)
        {
            var i = Enumerable.Range(0, previous.Items.Count).Where(i => !used.Contains(i) && SeriesTasks.Same(previous.Items[i].Task, f.Task))
                .Select(i => (int?)i).FirstOrDefault();
            if (i is not int index) continue;
            used.Add(index);
            result.Add(f with { Task = previous.Items[index].Task, Owner = previous.Items[index].Owner });
        }
        return result;
    }
}

public static class SeriesTasks
{
    /// <summary>同一件事：去掉頭尾空白、不分大小寫相同，或一邊包含另一邊（短的那邊至少 4 個字）</summary>
    public static bool Same(string a, string b)
    {
        var x = SwiftText.Lower(SwiftText.Trim(a));
        var y = SwiftText.Lower(SwiftText.Trim(b));
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        return Math.Min(SwiftText.Count(x), SwiftText.Count(y)) >= 4
            && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal));
    }
}
