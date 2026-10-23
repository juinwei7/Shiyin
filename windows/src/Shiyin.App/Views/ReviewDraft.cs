using Microsoft.UI.Xaml;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;

namespace Shiyin.App.Views;

/// <summary>待確認的回答（M§31.2、R41.2，同 Mac 的 ReviewDraft）：待確認分頁和紀錄旁的就地回答共用同一份，兩邊看到的一樣。
/// 草稿存在資料庫（M§21.15、R20.6）：打字停 0.8 秒存一次；換分頁、換會議、結束拾音都還在，直到送出。
/// 回答是照題號存的：題目換了（依回答修正、重新整理、復原上一版）就清掉，不然會配到別的題目（Sync）</summary>
public sealed class ReviewDraft
{
    readonly AppHost host;
    readonly DispatcherTimer save = new() { Interval = TimeSpan.FromMilliseconds(800) };
    /// <summary>題號 → 回答（是非題是 YesNoAnswer 的文字）</summary>
    public Dictionary<int, string> Answers { get; } = [];
    public HashSet<int> Dismissed { get; } = [];
    /// <summary>說話者編號 → 回答（「是」「不是，名字」）</summary>
    public Dictionary<int, string> SpeakerAnswers { get; } = [];
    public HashSet<int> SpeakerSkipped { get; } = [];
    /// <summary>就地回答展開中的那一條紀錄（R41.2），同時只展開一條</summary>
    public NoteItemRef? Expanded { get; set; }
    public long? MeetingId { get; private set; }
    List<string> questions = [];
    /// <summary>說話者編號 → 草稿的 key（建議換了人就是新的題目）</summary>
    Dictionary<int, string> speakerKeys = [];

    public ReviewDraft(AppHost host)
    {
        this.host = host;
        save.Tick += (_, _) => { save.Stop(); Ui.Guard(Save); };
    }

    /// <summary>載入會議、會議紀錄或說話者換掉時呼叫（DetailView.Show）：換會議就先存舊的再從資料庫讀草稿；
    /// 題目變了就清掉回答；說話者的建議被套用或清掉（例如從「猜到幾位說話者」那邊）就不留那位的回答</summary>
    public void Sync(MeetingDetail detail)
    {
        var id = detail.Meeting.Id!.Value;
        var newQuestions = (detail.Notes?.ReviewItems ?? []).Select(r => r.Question).ToList();
        var newKeys = detail.SpeakerSuggestions.ToDictionary(p => p.Key, p => $"{p.Key}:{p.Value.Name}");
        if (MeetingId != id)
        {
            Flush();
            MeetingId = id;
            questions = newQuestions;
            speakerKeys = newKeys;
            Expanded = null;
            Answers.Clear();
            Dismissed.Clear();
            SpeakerAnswers.Clear();
            SpeakerSkipped.Clear();
            var drafts = host.Controller.Drafts(id);
            for (var i = 0; i < questions.Count; i++)
            {
                if (drafts.TryGetValue("review:" + questions[i], out var answer)) Answers[i] = answer;
                if (drafts.ContainsKey("review-skip:" + questions[i])) Dismissed.Add(i);
            }
            foreach (var (idx, key) in speakerKeys)
            {
                if (drafts.TryGetValue("speaker-review:" + key, out var answer)) SpeakerAnswers[idx] = answer;
                if (drafts.ContainsKey("speaker-review-skip:" + key)) SpeakerSkipped.Add(idx);
            }
            return;
        }
        if (!questions.SequenceEqual(newQuestions))
        {
            save.Stop();
            host.Controller.DeleteDrafts(id, "review");
            Answers.Clear();
            Dismissed.Clear();
            questions = newQuestions;
            Expanded = null;
        }
        if (newKeys.Count != speakerKeys.Count || newKeys.Any(p => speakerKeys.GetValueOrDefault(p.Key) != p.Value))
        {
            speakerKeys = newKeys;
            foreach (var idx in SpeakerAnswers.Keys.Where(k => !speakerKeys.ContainsKey(k)).ToList()) SpeakerAnswers.Remove(idx);
            SpeakerSkipped.RemoveWhere(k => !speakerKeys.ContainsKey(k));
        }
    }

    /// <summary>打字停 0.8 秒存一次</summary>
    public void ScheduleSave()
    {
        save.Stop();
        save.Start();
    }

    /// <summary>馬上存（按了按鈕、換會議、關視窗）</summary>
    public void Flush()
    {
        save.Stop();
        Save();
    }

    /// <summary>送出後：草稿刪掉、回答清空</summary>
    public void Clear()
    {
        save.Stop();
        if (MeetingId is long id)
        {
            host.Controller.DeleteDrafts(id, "review");
            host.Controller.DeleteDrafts(id, "speaker-review");
        }
        Answers.Clear();
        Dismissed.Clear();
        SpeakerAnswers.Clear();
        SpeakerSkipped.Clear();
        Expanded = null;
    }

    /// <summary>「全部略過」（R41.5）：還沒回答的題目都標成略過，已經有回答的不動</summary>
    public void SkipAllUnanswered(MeetingDetail detail)
    {
        var count = detail.Notes?.ReviewItems.Count ?? 0;
        for (var i = 0; i < count; i++) if ((Answers.GetValueOrDefault(i) ?? "").Trim().Length == 0) Dismissed.Add(i);
        foreach (var idx in detail.SpeakerSuggestions.Keys) if ((SpeakerAnswers.GetValueOrDefault(idx) ?? "").Length == 0) SpeakerSkipped.Add(idx);
    }

    /// <summary>還有沒回答也沒略過的題目（「全部略過」才有東西可以略）</summary>
    public bool HasUnanswered(MeetingDetail detail)
    {
        var count = detail.Notes?.ReviewItems.Count ?? 0;
        return Enumerable.Range(0, count).Any(i => !Dismissed.Contains(i) && (Answers.GetValueOrDefault(i) ?? "").Trim().Length == 0)
            || detail.SpeakerSuggestions.Keys.Any(idx => !SpeakerSkipped.Contains(idx) && (SpeakerAnswers.GetValueOrDefault(idx) ?? "").Length == 0);
    }

    public int Answered => Answers.Count(p => !Dismissed.Contains(p.Key) && p.Value.Trim().Length > 0);
    public int SpeakerAnswered => SpeakerAnswers.Count(p => !SpeakerSkipped.Contains(p.Key) && p.Value.Length > 0);
    public bool HasSomethingToSubmit => Answered + SpeakerAnswered > 0 || Dismissed.Count + SpeakerSkipped.Count > 0;

    public string SubmitTitle() => (Answered + SpeakerAnswered, Dismissed.Count + SpeakerSkipped.Count) switch
    {
        (0, 0) => L.T("送出"),
        (0, var d) => L.F("略過 %lld 題", d),
        (var a, 0) => L.F("送出 %lld 題回答", a),
        var (a, d) => L.F("送出 %lld 題回答、略過 %lld 題", a, d),
    };

    /// <summary>送出（待確認分頁和就地回答共用，同 Mac 的 submitReviewDraft）：先送整理工具要處理的題目（題目文字可能有說話者的名字，
    /// 改名後會變），失敗時全部留著；再處理說話者的題目（M§27）</summary>
    public void Submit()
    {
        if (MeetingId is not long id) return;
        if ((Answered > 0 || Dismissed.Count > 0) && !host.Controller.SubmitReview(id, new Dictionary<int, string>(Answers), new HashSet<int>(Dismissed))) return;
        host.Controller.AnswerSpeakerQuestions(id, new Dictionary<int, string>(SpeakerAnswers), new HashSet<int>(SpeakerSkipped));
        Clear();
    }

    void Save()
    {
        if (MeetingId is not long id) return;
        for (var i = 0; i < questions.Count; i++)
        {
            host.Controller.SaveDraft(id, "review:" + questions[i], Answers.GetValueOrDefault(i) ?? "");
            host.Controller.SaveDraft(id, "review-skip:" + questions[i], Dismissed.Contains(i) ? "1" : "");
        }
        foreach (var (idx, key) in speakerKeys)
        {
            host.Controller.SaveDraft(id, "speaker-review:" + key, SpeakerAnswers.GetValueOrDefault(idx) ?? "");
            host.Controller.SaveDraft(id, "speaker-review-skip:" + key, SpeakerSkipped.Contains(idx) ? "1" : "");
        }
    }
}
