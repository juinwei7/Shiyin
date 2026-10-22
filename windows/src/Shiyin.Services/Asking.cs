using Shiyin.Data;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;

namespace Shiyin.Services;

/// <summary>問所有會議的範圍</summary>
public enum AskScope { All, Folder, Recent }

public static class AskScopes
{
    public static string Code(this AskScope s) => s switch { AskScope.Folder => "folder", AskScope.Recent => "recent", _ => "all" };
    public static string Label(this AskScope s) => s switch
    {
        AskScope.Folder => L.T("目前的資料夾"),
        AskScope.Recent => L.T("最近 30 天"),
        _ => L.T("所有會議"),
    };
}

/// <summary>
/// 對會議提問（M§21.8、R19.6、D32，同 Mac 的 Asking）：不排進工作佇列，使用者在等；同時只問一題（畫面管），取消用 CancellationToken
/// </summary>
public sealed class AskService(AppDatabase db, AppSettings settings, LLMClient llm)
{
    /// <summary>跨會議時最多帶幾場會議、總共多少字</summary>
    public const int MeetingLimit = 8;
    public const int CharacterLimit = 40_000;

    /// <summary>不能問時的原因</summary>
    public string? UnavailableReason => settings.Provider != LLMProvider.None ? null
        : settings.NeedsProviderSetup ? L.T("還沒設定整理工具，沒辦法提問；請到設定選擇 Claude、Codex 或 API")
        : L.T("整理工具設定為「不整理」，沒辦法提問；請到設定改成 Claude、Codex 或 API");

    static string Line(Segment s, IReadOnlyDictionary<int, string> names, MeetingLanguage language)
    {
        var speaker = s.Speaker is int i
            ? (names.TryGetValue(i, out var n) ? n : language.Text($"說話者 {i}", $"Speaker {i}", $"話者 {i}")) + language.Text("：", ": ", "：")  // l10n-skip
            : "";
        return $"[{TimeFormat.String(s.Start)}] {speaker}{s.Text}";
    }

    /// <summary>問一場會議，存進資料庫並回傳</summary>
    public async Task<QuestionRecord> AskMeeting(long id, string question, CancellationToken cancel)
    {
        question = question.Trim();
        var meeting = db.Meeting(id) ?? throw LLMException.Failed(L.T("這場會議已經被刪除，沒辦法匯出"));
        var (provider, model) = (settings.Provider, settings.Model);
        var language = meeting.MeetingLanguage;
        var names = db.Speakers(id).ToDictionary(s => s.Idx, s => s.Name);
        var lines = db.Segments(id).Select(s => Line(s, names, language)).ToList();
        var history = db.Questions(id).TakeLast(3)
            .Select(q => AskPrompt.Answer.Decode(q.Answer) is { } a ? (q.Question, a.Text) : default)
            .Where(h => h.Question is not null).ToList();
        var prompt = AskPrompt.MeetingPrompt(meeting, meeting.DecodedNotes, lines, history, question);
        var json = await llm.GenerateJson(provider, model, AskPrompt.MeetingSystem(language), prompt, AskPrompt.MeetingFormat, cancel);
        var raw = AskPrompt.Answer.Decode(json) ?? throw LLMException.BadOutput("JSON" + provider.BadOutputHint());
        var record = new QuestionRecord
        {
            MeetingId = id, Question = question, Answer = AskPrompt.Validate(raw, null).JsonString(), Provider = provider.Code(),
        };
        cancel.ThrowIfCancellationRequested();
        db.SaveQuestion(record);
        return record;
    }

    /// <summary>問所有會議：抽關鍵詞 → 全文搜尋挑會議和片段 → 回答（M§21.8）</summary>
    public async Task<QuestionRecord> AskAll(string question, AskScope scope, string? folder, CancellationToken cancel)
    {
        question = question.Trim();
        var (provider, model) = (settings.Provider, settings.Model);
        var language = MeetingLanguages.Interface;
        var keywordsJson = await llm.GenerateJson(provider, model, AskPrompt.KeywordsSystem(language), AskPrompt.KeywordsPrompt(language, question),
            AskPrompt.KeywordsFormat, cancel);
        var keywords = ParseKeywords(keywordsJson, provider).Select(k => k.Trim()).Where(k => SwiftText.Count(k) >= 2).ToList();
        cancel.ThrowIfCancellationRequested();
        var blocks = Blocks(keywords, scope, folder);
        AskPrompt.Answer answer;
        if (blocks.Count == 0)
            answer = new AskPrompt.Answer(L.T("找不到跟這個問題有關的會議。可以換個說法，或用會議裡實際講的詞再問一次。"), false, [], keywords, []);
        else
        {
            var json = await llm.GenerateJson(provider, model, AskPrompt.AllSystem(language), AskPrompt.AllPrompt(language, blocks, question),
                AskPrompt.AllFormat, cancel);
            var raw = AskPrompt.Answer.Decode(json) ?? throw LLMException.BadOutput("JSON" + provider.BadOutputHint());
            answer = AskPrompt.Validate(raw, blocks.Select(b => b.Id).ToHashSet()) with { Keywords = keywords, MeetingIds = blocks.Select(b => b.Id).ToList() };
        }
        var record = new QuestionRecord { MeetingId = null, Scope = scope.Code(), Question = question, Answer = answer.JsonString(), Provider = provider.Code() };
        cancel.ThrowIfCancellationRequested();
        db.SaveQuestion(record);
        return record;
    }

    static List<string> ParseKeywords(string json, LLMProvider provider)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("keywords").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw LLMException.BadOutput(e.Message + provider.BadOutputHint());
        }
    }

    /// <summary>用每個關鍵詞搜尋，依命中數、日期挑最多 8 場會議；每場帶摘要、決議和命中的段落（前後各一段），總長 40,000 字以內</summary>
    public List<AskPrompt.MeetingBlock> Blocks(IReadOnlyList<string> keywords, AskScope scope, string? folder)
    {
        var hits = new Dictionary<long, List<SearchHit>>();
        foreach (var keyword in keywords)
            foreach (var hit in db.Search(keyword, 300))
                (hits.TryGetValue(hit.MeetingId, out var list) ? list : hits[hit.MeetingId] = []).Add(hit);
        var cutoff = DateText.String(DateOnly.FromDateTime(DateTime.Now.AddDays(-30)));
        var candidates = hits.Select(p => (Meeting: db.Meeting(p.Key), Hits: p.Value))
            .Where(c => c.Meeting is { DeletedAt: null } m && scope switch
            {
                AskScope.Folder => folder is not null && m.Folder == folder,
                AskScope.Recent => string.CompareOrdinal(m.MeetingDate, cutoff) >= 0,
                _ => true,
            })
            .OrderByDescending(c => c.Hits.Count).ThenByDescending(c => c.Meeting!.MeetingDate, StringComparer.Ordinal).ToList();
        var blocks = new List<AskPrompt.MeetingBlock>();
        var total = 0;
        foreach (var (meeting, list) in candidates.Take(MeetingLimit))
        {
            var id = meeting!.Id!.Value;
            var language = meeting.MeetingLanguage;
            var notes = meeting.DecodedNotes;
            var summary = (notes?.Summary ?? []).Concat(notes?.Decisions ?? []).Select(x => x.Time.Length == 0 ? x.Text : $"[{x.Time}] {x.Text}").ToList();
            var segments = db.Segments(id);
            var names = db.Speakers(id).ToDictionary(s => s.Idx, s => s.Name);
            var index = new Dictionary<long, int>();
            for (var i = 0; i < segments.Count; i++) if (segments[i].Id is long sid) index.TryAdd(sid, i);
            var wanted = new SortedSet<int>();
            foreach (var hit in list.Where(h => h.Kind == SearchKind.Segment))
                if (index.TryGetValue(hit.Ref, out var i))
                    foreach (var j in new[] { i - 1, i, i + 1 }) if (j >= 0 && j < segments.Count) wanted.Add(j);
            var lines = wanted.Select(i => Line(segments[i], names, language)).ToList();
            static int Size(IEnumerable<string> items) => items.Sum(SwiftText.Count);
            if (total + Size(summary) + Size(lines) > CharacterLimit)
            {
                // 放不下整場：只放到上限為止的段落
                var room = CharacterLimit - total - Size(summary);
                if (room <= 200) break;
                var used = 0;
                lines = lines.TakeWhile(l => (used += SwiftText.Count(l)) <= room).ToList();
            }
            total += Size(summary) + Size(lines);
            blocks.Add(new AskPrompt.MeetingBlock(id, meeting.DisplayTitle, meeting.MeetingDate, summary, lines));
        }
        return blocks;
    }
}
