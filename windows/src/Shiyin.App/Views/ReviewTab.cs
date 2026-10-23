using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;

namespace Shiyin.App.Views;

/// <summary>待確認分頁（同 Mac 的 ReviewTab，R6）：每題可以回答或略過，送出後由整理工具依回答修正。
/// 是非題給「是」「不是」（R32）；最上面是說話者的題目（聲紋沒把握的、AI 猜的，R33），送出時直接改名字，不經過整理工具（M§27）。
/// 回答存在 ReviewDraft（M§31.2），跟紀錄旁的就地回答（R41.2）共用同一份</summary>
public sealed class ReviewTab
{
    readonly AppHost host;
    readonly MainWindow window;
    readonly ReviewDraft draft;
    MeetingDetail? detail;

    public ReviewTab(AppHost host, MainWindow window, ReviewDraft draft)
    {
        this.host = host;
        this.window = window;
        this.draft = draft;
    }

    List<MeetingNotes.ReviewItem> Items => detail?.Notes?.ReviewItems ?? [];
    List<SpeakerNaming.Suggestion> SpeakerItems => detail?.SpeakerSuggestions.OrderBy(p => p.Key).Select(p => p.Value).ToList() ?? [];
    MeetingLanguage Language => detail?.Meeting.MeetingLanguage ?? MeetingLanguage.Zh;

    public FrameworkElement Show(MeetingDetail value)
    {
        detail = value;
        var speakerItems = SpeakerItems;
        if (value.Notes is null && speakerItems.Count == 0)
            return new Border { Padding = new Thickness(32), Child = Ui.Text(L.T("轉錄和整理完成後，會議紀錄會出現在這裡。"), color: Ui.Secondary) };
        if (Items.Count == 0 && speakerItems.Count == 0)
            return Ui.Scroll(Ui.Column(8, Ui.Text(L.T("沒有待確認的項目"), 15, color: Ui.Celadon),
                Ui.Text(L.T("會議紀錄裡推測出來的人名、專有名詞、負責人和期限，都已經確認過了。"), color: Ui.Secondary)), 32);
        var column = new StackPanel { Spacing = 22 };
        var submit = Ui.Button("", () => { draft.Submit(); window.Refresh(); }, accent: true);
        void UpdateSubmit()
        {
            submit.Content = draft.SubmitTitle();
            submit.IsEnabled = draft.HasSomethingToSubmit && window.State.DetailStatus?.IsActive != true;
        }
        var intro = Ui.Text(Items.Any(i => i.YesNo) || speakerItems.Count > 0
            ? L.T("以下是整理時不確定的地方。按「是」「不是」或直接回答就好；看不懂或不重要的可以略過。")
            : L.T("以下是整理時不確定的地方。直接回答就好；看不懂或不重要的可以略過。"), color: Ui.Secondary);
        // 全部略過（R41.5）：還沒回答的都標成略過，已經有回答的不動
        var skipAll = Ui.Button(L.T("全部略過"), () => { draft.SkipAllUnanswered(value); draft.Flush(); window.Refresh(); },
            tooltip: L.T("還沒回答的題目都標成略過，送出後直接移除"));
        skipAll.IsEnabled = draft.HasUnanswered(value);
        skipAll.VerticalAlignment = VerticalAlignment.Top;
        column.Children.Add(Ui.Bar(null, intro, skipAll, 12));
        foreach (var suggestion in speakerItems) column.Children.Add(SpeakerRow(value, suggestion, UpdateSubmit));
        for (var i = 0; i < Items.Count; i++)
            column.Children.Add(ReviewRows.Question(i, Items[i], Language, draft, host.Player.IsLoaded ? s => host.Player.Seek(s) : null, window.Refresh, UpdateSubmit));
        UpdateSubmit();
        var footer = Ui.Bar(null, Ui.Text(Footnote(speakerItems.Count > 0), 13, color: Ui.Secondary), submit, 12);
        footer.Padding = new Thickness(32, 12, 32, 12);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Ui.Scroll(new ReadingColumn(column, 680), 32));
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);
        return grid;
    }

    /// <summary>說話者的題目（R33.2）：「『說話者 3』是 weiwei 嗎？」，下面是原因；選「不是」可以挑最像的人或打名字，也可以不填。
    /// 略過的收成一行（R41.5）</summary>
    FrameworkElement SpeakerRow(MeetingDetail value, SpeakerNaming.Suggestion suggestion, Action updateSubmit)
    {
        var idx = suggestion.Speaker;
        var skipped = draft.SpeakerSkipped.Contains(idx);
        var current = value.Speakers.GetValueOrDefault(idx) ?? "";
        var question = Ui.Text(Language.Text($"「{current}」是{suggestion.Name}嗎？", $"Is “{current}” {suggestion.Name}?", $"「{current}」は{suggestion.Name}ですか？"), 14, select: true);  // l10n-skip：會議語言的題目
        question.Language = Ui.ContentLanguage(Language);
        if (skipped) { question.Foreground = Ui.Secondary; question.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough; }
        var icon = new FontIcon { Glyph = "", FontSize = 14, Foreground = Ui.Speaker(idx), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        if (skipped)
            return Ui.Bar(icon, question, ReviewRows.SkipButton(true, () => { draft.SpeakerSkipped.Remove(idx); draft.Flush(); window.Refresh(); }), 10);
        var parts = Ui.Column(8, Ui.Bar(icon, question, null, 10));
        if (suggestion.Reason.Length > 0 || suggestion.Time.Length > 0)
        {
            var reason = Ui.Text(suggestion.Reason, 13, color: Ui.Secondary);
            reason.Language = Ui.ContentLanguage(Language);
            var line = Ui.Bar(suggestion.Time.Length > 0 ? (FrameworkElement)Ui.Time(suggestion.Time, host.Player.IsLoaded ? s => host.Player.Seek(s) : null) : null, reason, null, 6);
            line.Margin = new Thickness(22, 0, 0, 0);
            parts.Children.Add(line);
        }
        var choice = YesNoAnswer.Parse(draft.SpeakerAnswers.GetValueOrDefault(idx) ?? "", Language) ?? new YesNoAnswer((bool?)null);
        var skip = ReviewRows.SkipButton(false, () => { draft.SpeakerSkipped.Add(idx); draft.Flush(); window.Refresh(); });
        var row = Ui.Bar(ReviewRows.YesNo(choice, Language, text => { draft.SpeakerAnswers[idx] = text; draft.Flush(); window.Refresh(); }), null, skip, 12);
        row.Margin = new Thickness(22, 0, 0, 0);
        parts.Children.Add(row);
        if (choice.IsYes == false)
        {
            var name = new TextBox { PlaceholderText = L.T("是誰？（可以不填）"), Text = choice.Correction };
            name.TextChanged += (_, _) => { draft.SpeakerAnswers[idx] = new YesNoAnswer(false, name.Text).Text(Language); updateSubmit(); draft.ScheduleSave(); };
            var others = (value.SpeakerCandidates.GetValueOrDefault(idx) ?? []).Where(n => !string.Equals(n, suggestion.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            FrameworkElement? pick = null;
            if (others.Count > 0)
            {
                var menu = new MenuFlyout();
                foreach (var other in others)
                    menu.Items.Add(Ui.Item(other, () => { draft.SpeakerAnswers[idx] = new YesNoAnswer(false, other).Text(Language); draft.Flush(); window.Refresh(); }));
                pick = new DropDownButton { Content = L.T("是…"), Flyout = menu };
            }
            var line = Ui.Bar(pick, name, null, 10);
            line.Margin = new Thickness(22, 0, 0, 0);
            parts.Children.Add(line);
        }
        return parts;
    }

    string Footnote(bool hasSpeakers)
    {
        var speakers = L.T("說話者的題目不會送給整理工具，送出後直接換名字。");
        if (Items.Count == 0) return speakers;
        var provider = host.Settings.Provider == LLMProvider.None ? "LLM" : host.Settings.Provider.Label();
        var notes = L.F("有回答的題目會交給 %@ 依答案修正會議紀錄；略過的題目直接移除。改錯了可以從「更多」復原上一版。", provider);
        return hasSpeakers ? notes + " " + speakers : notes;
    }
}

/// <summary>一題待確認和就地回答的卡片（M§31.2）：待確認分頁和紀錄旁共用（同 Mac 的 ReviewQuestionRow、InlineReviewCard）</summary>
public static class ReviewRows
{
    /// <summary>一題：題號、時間點、題目；是非題（R32）給「是」「不是」，選了「不是」才要填正確的答案；舊的草稿認不出來時照舊用輸入框。
    /// 略過的收成一行加「取消略過」（R41.5）。refresh：整個畫面重畫（按鈕）；changed：只更新送出鈕（打字）</summary>
    public static FrameworkElement Question(int index, MeetingNotes.ReviewItem item, MeetingLanguage language, ReviewDraft draft,
                                            Action<double>? seek, Action refresh, Action changed)
    {
        var dismissed = draft.Dismissed.Contains(index);
        var question = Ui.Text(item.Question, 14, select: true);
        question.Language = Ui.ContentLanguage(language);
        if (dismissed) { question.Foreground = Ui.Secondary; question.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough; }
        // 題目放中間那欄才會換行（放在橫的 StackPanel 裡不會換行，長的題目會被切掉）
        var lead = Ui.Row(10, Ui.Text($"{index + 1}", 14, bold: true, color: Ui.Signal, wrap: false), Ui.Time(item.Time, seek));
        lead.VerticalAlignment = VerticalAlignment.Top;   // 題號、時間對齊題目的第一行
        if (dismissed)
            return Ui.Bar(lead, question, SkipButton(true, () => { draft.Dismissed.Remove(index); draft.Flush(); refresh(); }), 10);
        var head = Ui.Bar(lead, question, null, 10);
        var skip = SkipButton(false, () => { draft.Dismissed.Add(index); draft.Flush(); refresh(); });
        if (item.YesNo && YesNoAnswer.Parse(draft.Answers.GetValueOrDefault(index) ?? "", language) is { } choice)
        {
            var row = Ui.Bar(YesNo(choice, language, text => { draft.Answers[index] = text; draft.Flush(); refresh(); }), null, skip, 12);
            row.Margin = new Thickness(22, 0, 0, 0);
            var parts = Ui.Column(8, head, row);
            if (choice.IsYes == false)
            {
                var correction = new TextBox { PlaceholderText = L.T("正確的是…（可以不填）"), Text = choice.Correction, Margin = new Thickness(22, 0, 0, 0) };
                correction.TextChanged += (_, _) => { draft.Answers[index] = new YesNoAnswer(false, correction.Text).Text(language); changed(); draft.ScheduleSave(); };
                parts.Children.Add(correction);
            }
            return parts;
        }
        var answer = new TextBox { PlaceholderText = L.T("你的回答"), Text = draft.Answers.GetValueOrDefault(index) ?? "" };
        answer.TextChanged += (_, _) => { draft.Answers[index] = answer.Text; changed(); draft.ScheduleSave(); };
        var answerRow = Ui.Bar(null, answer, skip, 12);
        answerRow.Margin = new Thickness(22, 0, 0, 0);
        return Ui.Column(8, head, answerRow);
    }

    /// <summary>「是」「不是」兩個按鈕（M§27）：選了「是」就清掉填過的答案；set 之後由呼叫的人存草稿、重畫（「不是」才出現輸入框）</summary>
    public static FrameworkElement YesNo(YesNoAnswer choice, MeetingLanguage language, Action<string> set)
    {
        Microsoft.UI.Xaml.Controls.Primitives.ToggleButton Choice(string label, bool yes)
        {
            var button = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = label, IsChecked = choice.IsYes == yes, MinWidth = 56 };
            button.Click += (_, _) => set(new YesNoAnswer(yes, yes ? "" : choice.Correction).Text(language));
            return button;
        }
        return Ui.Row(4, Choice(L.T("是"), true), Choice(L.T("不是"), false));
    }

    /// <summary>「略過」「取消略過」（R41.5）：不用勾選框，按了那題收成一行</summary>
    public static FrameworkElement SkipButton(bool skipped, Action click)
    {
        var button = Ui.Link(skipped ? L.T("取消略過") : L.T("略過"), click);
        button.Foreground = Ui.Secondary;
        button.VerticalAlignment = VerticalAlignment.Top;
        return button;
    }

    /// <summary>就地回答（R41.2）：展開在那一條紀錄下面，列出對應的題目；「收合」「送出」，送出走同一份草稿</summary>
    public static FrameworkElement Card(MeetingDetail detail, IReadOnlyList<int> indexes, ReviewDraft draft, AppHost host, MainWindow window)
    {
        var items = detail.Notes?.ReviewItems ?? [];
        var shown = indexes.Where(i => i >= 0 && i < items.Count).ToHashSet();
        var column = Ui.Column(12);
        // 只送這一條對到的題目（R41.2），題數也只算這幾題
        var submit = Ui.Button("", () => { draft.Submit(shown); window.Refresh(); }, accent: true);
        void UpdateSubmit()
        {
            submit.Content = draft.SubmitTitle(shown);
            submit.IsEnabled = draft.CanSubmit(shown) && window.State.DetailStatus?.IsActive != true;
        }
        foreach (var index in shown.Order())
            column.Children.Add(Question(index, items[index], detail.Meeting.MeetingLanguage, draft, host.Player.IsLoaded ? s => host.Player.Seek(s) : null, window.Refresh, UpdateSubmit));
        UpdateSubmit();
        var footer = Ui.Bar(null, Ui.Text(L.T("只送這一條的題目；有回答的交給整理工具依答案修正，略過的直接移除。"), 12, color: Ui.Secondary),
            Ui.Row(8, Ui.Button(L.T("收合"), () => { draft.Expanded = null; window.Refresh(); }), submit), 12);
        column.Children.Add(footer);
        var tint = new Microsoft.UI.Xaml.Media.SolidColorBrush(Ui.Signal.Color) { Opacity = 0.07 };
        return new Border { Child = column, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Background = tint, Margin = new Thickness(14, 0, 0, 0) };
    }

    /// <summary>待確認的標記（R41.1、M§31.2）：這一條紀錄有幾題整理時不確定的地方，琥珀色跟側欄的「待確認 N 題」一樣；點了展開（同時一條），再點一次收合</summary>
    public static FrameworkElement? Marker(MeetingDetail detail, NoteItemRef r, ReviewDraft draft, MainWindow window)
    {
        var count = detail.ReviewCountOf(r);
        if (count == 0) return null;
        var mark = MeetingLanguages.Ui("？", "?");
        var button = new Button
        {
            Content = count > 1 ? $"{mark}{count}" : mark, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ui.Signal, Padding = new Thickness(count > 1 ? 6 : 0, 0, count > 1 ? 6 : 0, 0), MinWidth = 18, MinHeight = 18, Height = 18,
            CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Ui.Signal.Color) { Opacity = 0.16 }, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, count > 1 ? L.F("整理時有 %lld 題不確定的地方，點了就能回答", count) : L.T("整理時有一題不確定的地方，點了就能回答"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.F("待確認 %lld 題", count));
        button.Click += (_, _) => { draft.Expanded = draft.Expanded == r ? null : r; window.Refresh(); };
        return button;
    }
}
