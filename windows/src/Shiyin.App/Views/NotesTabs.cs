using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;

namespace Shiyin.App.Views;

/// <summary>
/// 摘要、詳細、我的筆記三個分頁（同 Mac 的 NotesTabs）：閱讀的欄位寬度上限約 680；可以切換成編輯模式（R6.6），
/// 按「完成」存成新的版本，切到別的分頁、別場會議時自動存
/// </summary>
public sealed class NotesTabs
{
    readonly AppHost host;
    readonly MainWindow window;
    NotesEditor? editor;
    readonly TextBox userNotes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    readonly UserNotesBox notesBox;

    public NotesTabs(AppHost host, MainWindow window)
    {
        this.host = host;
        this.window = window;
        notesBox = new UserNotesBox(host, userNotes, () => host.Player.IsPlaying ? host.Player.CurrentTime : null);
    }

    void Seek(double seconds) => host.Player.Seek(seconds);
    Action<double>? SeekIfLoaded => host.Player.IsLoaded ? Seek : null;

    /// <summary>切到別的分頁、別場會議：編輯中的會議紀錄自動存，不丟掉打的字</summary>
    public void FlushEditor()
    {
        if (editor is not { } e) return;
        editor = null;
        host.Controller.FinishNotesEdit(e.Notes, e.Base, e.MeetingId, automatically: true);
    }

    public void SaveUserNotes() => notesBox.Save();

    static StackPanel Column() => new() { Spacing = 26 };

    /// <summary>閱讀的欄位最寬 680（同 Mac），靠左</summary>
    static ScrollViewer Reading(UIElement column) => Ui.Scroll(new ReadingColumn(column, 680), 32);

    FrameworkElement Section(string title, MeetingLanguage language, UIElement content) =>
        Ui.Column(10, Ui.Heading(title, language), content);

    UIElement Items(IEnumerable<MeetingNotes.Item> items, MeetingLanguage language)
    {
        var panel = new StackPanel { Spacing = 7 };
        foreach (var item in items)
        {
            var text = Ui.Text(item.Text, 14, select: true);
            text.Language = Ui.ContentLanguage(language);
            var row = Ui.Split(Ui.Text("•", 14, color: Ui.Secondary), Ui.Inline(text, item.Time.Length > 0 ? Ui.Time(item.Time, SeekIfLoaded) : null));
            panel.Children.Add(row);
        }
        return panel;
    }

    UIElement ActionItems(List<MeetingNotes.ActionItem> items, MeetingLanguage language)
    {
        var grid = new Grid { ColumnSpacing = 18, RowSpacing = 9 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Add(FrameworkElement e, int row, int column)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, column);
            grid.Children.Add(e);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(Ui.Caption(L.T("負責人")), 0, 0);
        Add(Ui.Caption(L.T("事項")), 0, 1);
        Add(Ui.Caption(L.T("期限")), 0, 2);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var owner = Ui.Text(item.Owner.Length == 0 ? L.T("未指定") : item.Owner, 14, color: item.Owner.Length == 0 ? Ui.Signal : null, wrap: false);
            owner.Language = Ui.ContentLanguage(language);
            var task = Ui.Text(item.Task, 14, select: true);
            task.Language = Ui.ContentLanguage(language);
            Add(owner, i + 1, 0);
            Add(Ui.Inline(task, item.Time.Length > 0 ? Ui.Time(item.Time, SeekIfLoaded) : null), i + 1, 1);
            Add(Ui.Text(item.Due.Length == 0 ? "—" : item.Due, 14, color: item.Due.Length == 0 ? Ui.Secondary : null, wrap: false), i + 1, 2);
        }
        return grid;
    }

    /// <summary>沒有會議紀錄時的說明與動作</summary>
    FrameworkElement Placeholder(MeetingDetail detail)
    {
        var status = window.State.DetailStatus;
        var panel = Ui.Column(12);
        panel.Padding = new Thickness(32);
        if (status is MeetingStatus.Recording) panel.Children.Add(Ui.Text(L.T("錄音結束、轉錄完成後，會議紀錄會出現在這裡。"), color: Ui.Secondary));
        else if (status?.IsActive == true) panel.Children.Add(Ui.Text(L.T("轉錄和整理完成後，會議紀錄會出現在這裡。"), color: Ui.Secondary));
        else if (detail.Segments.Count == 0 && detail.Meeting.UserNotes.Length == 0) panel.Children.Add(Ui.Text(L.T("還沒有逐字稿。"), color: Ui.Secondary));
        else if (host.Settings.NeedsProviderSetup)
        {
            // 沒選過整理工具、也沒有 claude、codex（R35.2）：說還沒設定，給打開設定的按鈕（設定一打開就是整理工具）
            panel.Children.Add(Ui.Text(L.T("還沒設定整理工具。選好之後，就能把逐字稿整理成摘要、決議和待辦；之後的會議轉錄完會自動整理。"), color: Ui.Secondary));
            panel.Children.Add(Ui.Button(L.T("設定整理工具"), () => App.Current.ShowSettings()));
        }
        else
        {
            var none = host.Settings.Provider == LLMProvider.None;
            panel.Children.Add(Ui.Text(none ? L.T("整理工具目前設定為「不整理」。要產生會議紀錄，請到設定選擇 Claude、Codex 或 API。") : L.T("這場會議還沒整理成會議紀錄。"), color: Ui.Secondary));
            var button = Ui.Button(L.T("整理成會議紀錄"), () => host.Controller.Resummarize(detail.Meeting.Id!.Value));
            button.IsEnabled = !none;
            panel.Children.Add(button);
        }
        return panel;
    }

    FrameworkElement EditBar(MeetingDetail detail, bool topics)
    {
        var busy = window.State.DetailStatus?.IsActive == true;
        var button = Ui.Button(L.T("編輯"), () =>
        {
            editor = new NotesEditor(detail.Notes!, detail.Meeting.Id!.Value, topics, detail.Meeting.MeetingLanguage);
            editor.Finished += save =>
            {
                var e = editor;
                editor = null;
                if (save && e is not null) host.Controller.FinishNotesEdit(e.Notes, e.Base, e.MeetingId);
                window.Refresh();
            };
            window.Refresh();
        }, tooltip: busy ? L.T("正在處理這場會議，完成後才能編輯") : L.T("直接修改會議紀錄，存檔時會留一個版本"));
        button.IsEnabled = !busy;
        button.HorizontalAlignment = HorizontalAlignment.Right;
        return button;
    }

    public FrameworkElement Summary(MeetingDetail detail)
    {
        if (editor is { Topics: false } e && e.MeetingId == detail.Meeting.Id) return e.Root;
        if (detail.Notes is not { IsEmpty: false } notes) return Placeholder(detail);
        var language = detail.Meeting.MeetingLanguage;
        var column = Column();
        column.Children.Add(EditBar(detail, topics: false));
        if (notes.Summary.Count > 0) column.Children.Add(Section(L.T("摘要"), language, Items(notes.Summary, language)));
        if (notes.Decisions.Count > 0) column.Children.Add(Section(L.T("決議"), language, Items(notes.Decisions, language)));
        if (notes.ActionItems.Count > 0) column.Children.Add(Section(L.T("待辦"), language, ActionItems(notes.ActionItems, language)));
        if (notes.OpenQuestions.Count > 0) column.Children.Add(Section(L.T("未決問題"), language, Items(notes.OpenQuestions, language)));
        foreach (var section in notes.Custom.Where(c => c.Items.Count > 0)) column.Children.Add(Section(section.Title, language, Items(section.Items, language)));
        if (detail.Meeting.NotesBy is { } by)
        {
            var template = window.State.Templates.FirstOrDefault(t => t.Id == detail.Meeting.TemplateId)?.DisplayName() ?? L.T("一般會議");
            column.Children.Add(Ui.Text(L.F("由 %@ 整理．%@", LLMProviders.Parse(by)?.ShortLabel() ?? by, L.T("範本：") + template), 12, color: Ui.Tertiary));
        }
        return Reading(column);
    }

    public FrameworkElement Topics(MeetingDetail detail)
    {
        if (editor is { Topics: true } e && e.MeetingId == detail.Meeting.Id) return e.Root;
        if (detail.Notes is not { IsEmpty: false } notes) return Placeholder(detail);
        var language = detail.Meeting.MeetingLanguage;
        var column = Column();
        column.Children.Add(EditBar(detail, topics: true));
        if (notes.Topics.Count == 0) column.Children.Add(Ui.Text(L.T("這場會議沒有分出議題（可能是範本沒有包含「議題詳細」）。"), color: Ui.Secondary));
        foreach (var topic in notes.Topics)
            column.Children.Add(Ui.Column(10, Ui.Bar(Ui.Time(topic.Start, SeekIfLoaded) as FrameworkElement, Ui.Heading(topic.Title, language), null, 10), Items(topic.Points, language)));
        return Reading(column);
    }

    /// <summary>我的筆記（R2.4）：播放錄音時換行，會自動加上當下的播放時間</summary>
    public FrameworkElement UserNotes(MeetingDetail detail)
    {
        notesBox.Load(detail.Meeting.Id!.Value, detail.Meeting.UserNotes);
        userNotes.Language = Ui.ContentLanguage(detail.Meeting.MeetingLanguage);
        var title = Ui.Heading(L.T("我的筆記"), MeetingLanguage.Zh);
        FrameworkElement? marks = null;
        if (detail.Bookmarks.Count > 0)
        {
            var menu = new MenuFlyout();
            foreach (var b in detail.Bookmarks) menu.Items.Add(Ui.Item($"{TimeFormat.String(b.Time)} {b.Label}", () => Seek(b.Time)));
            marks = new DropDownButton { Content = L.F("重點標記 %lld", detail.Bookmarks.Count), Flyout = menu };
        }
        var grid = new Grid { RowSpacing = 10, Padding = new Thickness(32, 20, 32, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = Ui.Bar(title, null, marks);
        grid.Children.Add(head);
        var caption = Ui.Caption(L.T("播放錄音時換行，會自動加上當下的播放時間。整理時 AI 會參考這些筆記；改完之後可以從「更多」選「重新整理會議紀錄」。"));
        Grid.SetRow(caption, 1);
        grid.Children.Add(caption);
        if (userNotes.Parent is Panel old) old.Children.Remove(userNotes);
        Grid.SetRow(userNotes, 2);
        grid.Children.Add(userNotes);
        return new ReadingColumn(grid, 760);
    }
}

/// <summary>
/// 手動編輯會議紀錄（同 Mac 的 NotesEditor，R6.6）：每一條都是可以改的欄位，可以新增、刪除、上下移動；按「完成」才存成新版本
/// </summary>
public sealed class NotesEditor
{
    public Grid Root { get; } = new();
    public MeetingNotes Base { get; }
    public long MeetingId { get; }
    public bool Topics { get; }
    readonly MeetingLanguage language;
    readonly StackPanel body = new() { Spacing = 22 };
    MeetingNotes notes;
    public event Action<bool>? Finished;

    public MeetingNotes Notes => notes;

    public NotesEditor(MeetingNotes notes, long meetingId, bool topics, MeetingLanguage language)
    {
        Base = notes;
        this.notes = Copy(notes);
        MeetingId = meetingId;
        Topics = topics;
        this.language = language;
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = Ui.Bar(Ui.Text(topics ? L.T("編輯議題") : L.T("編輯會議紀錄"), 15, bold: true), null,
            Ui.Row(8, Ui.Button(L.T("取消"), () => Finished?.Invoke(false)), Ui.Button(L.T("完成"), () => Finished?.Invoke(true), accent: true)));
        bar.Padding = new Thickness(32, 10, 32, 10);
        Root.Children.Add(bar);
        var scroll = Ui.Scroll(new ReadingColumn(body, 720), 32);
        Grid.SetRow(scroll, 1);
        Root.Children.Add(scroll);
        Rebuild();
    }

    static MeetingNotes Copy(MeetingNotes n) => n with
    {
        Summary = [.. n.Summary], Decisions = [.. n.Decisions], ActionItems = [.. n.ActionItems], OpenQuestions = [.. n.OpenQuestions],
        Topics = n.Topics.Select(t => t with { Points = [.. t.Points] }).ToList(), Custom = n.Custom.Select(c => c with { Items = [.. c.Items] }).ToList(),
        ReviewItems = [.. n.ReviewItems],
    };

    void Rebuild()
    {
        body.Children.Clear();
        if (Topics)
        {
            for (var i = 0; i < notes.Topics.Count; i++)
            {
                var index = i;
                var topic = notes.Topics[i];
                var start = new TextBox { Text = topic.Start, Width = 70, PlaceholderText = L.T("時間") };
                start.TextChanged += (_, _) => notes.Topics[index] = notes.Topics[index] with { Start = start.Text };
                var title = new TextBox { Text = topic.Title, PlaceholderText = L.T("議題"), FontFamily = Ui.TitleFont(language) };
                title.TextChanged += (_, _) => notes.Topics[index] = notes.Topics[index] with { Title = title.Text };
                var delete = Ui.IconButton(Symbol.Delete, L.T("刪除這個議題"), () => { notes.Topics.RemoveAt(index); Rebuild(); });
                body.Children.Add(Ui.Column(8, Ui.Bar(start, title, delete), ItemsEditor(notes.Topics[index].Points)));
            }
            body.Children.Add(Ui.Button(L.T("新增議題"), () => { notes.Topics.Add(new MeetingNotes.Topic("", "", [])); Rebuild(); }));
            return;
        }
        var titleBox = new TextBox { Text = notes.Title, PlaceholderText = L.T("標題"), FontFamily = Ui.TitleFont(language), FontSize = 18 };
        titleBox.TextChanged += (_, _) => notes = notes with { Title = titleBox.Text };
        body.Children.Add(titleBox);
        body.Children.Add(Section(L.T("摘要"), ItemsEditor(notes.Summary)));
        body.Children.Add(Section(L.T("決議"), ItemsEditor(notes.Decisions)));
        body.Children.Add(Section(L.T("待辦"), ActionsEditor(notes.ActionItems)));
        body.Children.Add(Section(L.T("未決問題"), ItemsEditor(notes.OpenQuestions)));
        foreach (var section in notes.Custom) body.Children.Add(Section(section.Title, ItemsEditor(section.Items)));
        if (notes.ReviewItems.Count > 0)
        {
            var list = Ui.Column(6);
            for (var i = 0; i < notes.ReviewItems.Count; i++)
            {
                var index = i;
                var item = notes.ReviewItems[i];
                list.Children.Add(Ui.Bar(Ui.Text(item.Time, 13, color: Ui.Secondary, wrap: false), Ui.Text(item.Question, 14),
                    Ui.IconButton(Symbol.Delete, L.T("刪除這一題"), () => { notes.ReviewItems.RemoveAt(index); Rebuild(); })));
            }
            body.Children.Add(Section(L.T("待確認"), list));
        }
    }

    UIElement Section(string title, UIElement content) => Ui.Column(8, Ui.Heading(title, language, 16), content);

    UIElement ItemsEditor(List<MeetingNotes.Item> items)
    {
        var panel = Ui.Column(6);
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var text = new TextBox { Text = items[i].Text, PlaceholderText = L.T("內容"), TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, Language = Ui.ContentLanguage(language) };
            text.TextChanged += (_, _) => items[index] = items[index] with { Text = text.Text };
            var time = new TextBox { Text = items[i].Time, PlaceholderText = L.T("時間"), Width = 70 };
            time.TextChanged += (_, _) => items[index] = items[index] with { Time = time.Text };
            panel.Children.Add(Ui.Bar(null, text, Ui.Row(6, time, RowButtons(items, index)), 6));
        }
        panel.Children.Add(Ui.Link(L.T("新增一條"), () => { items.Add(new MeetingNotes.Item("")); Rebuild(); }));
        return panel;
    }

    UIElement ActionsEditor(List<MeetingNotes.ActionItem> items)
    {
        var panel = Ui.Column(6);
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var owner = new TextBox { Text = items[i].Owner, PlaceholderText = L.T("負責人"), Width = 90 };
            owner.TextChanged += (_, _) => items[index] = items[index] with { Owner = owner.Text };
            var task = new TextBox { Text = items[i].Task, PlaceholderText = L.T("事項"), TextWrapping = TextWrapping.Wrap, Language = Ui.ContentLanguage(language) };
            task.TextChanged += (_, _) => items[index] = items[index] with { Task = task.Text };
            var due = new TextBox { Text = items[i].Due, PlaceholderText = "yyyy-MM-dd", Width = 110 };
            due.TextChanged += (_, _) => items[index] = items[index] with { Due = due.Text };
            var time = new TextBox { Text = items[i].Time, PlaceholderText = L.T("時間"), Width = 70 };
            time.TextChanged += (_, _) => items[index] = items[index] with { Time = time.Text };
            panel.Children.Add(Ui.Bar(owner, task, Ui.Row(6, due, time, RowButtons(items, index))));
        }
        panel.Children.Add(Ui.Link(L.T("新增待辦"), () => { items.Add(new MeetingNotes.ActionItem("", "", "")); Rebuild(); }));
        return panel;
    }

    FrameworkElement RowButtons<T>(List<T> items, int index)
    {
        var up = Ui.GlyphButton("\uE74A", L.T("往上移"), () => { (items[index - 1], items[index]) = (items[index], items[index - 1]); Rebuild(); });
        up.IsEnabled = index > 0;
        var down = Ui.GlyphButton("\uE74B", L.T("往下移"), () => { (items[index + 1], items[index]) = (items[index], items[index + 1]); Rebuild(); });
        down.IsEnabled = index < items.Count - 1;
        return Ui.Row(2, up, down, Ui.IconButton(Symbol.Delete, L.T("刪除"), () => { items.RemoveAt(index); Rebuild(); }));
    }
}

/// <summary>待確認分頁（同 Mac 的 ReviewTab，R6）：每題可以回答或略過，送出後由整理工具依回答修正。回答的草稿存在資料庫（M§21.15）。
/// 是非題給「是」「不是」（R32）；最上面是說話者的題目（聲紋沒把握的、AI 猜的，R33），送出時直接改名字，不經過整理工具（M§27）</summary>
public sealed class ReviewTab
{
    readonly AppHost host;
    readonly MainWindow window;
    readonly Dictionary<int, string> answers = [];
    readonly HashSet<int> dismissed = [];
    /// <summary>說話者編號 → 回答（YesNoAnswer 的文字：「是」「不是，名字」）</summary>
    readonly Dictionary<int, string> speakerAnswers = [];
    readonly HashSet<int> speakerSkipped = [];
    readonly DispatcherTimer save = new() { Interval = TimeSpan.FromMilliseconds(800) };
    MeetingDetail? detail;
    List<string> questions = [];

    public ReviewTab(AppHost host, MainWindow window)
    {
        this.host = host;
        this.window = window;
        save.Tick += (_, _) => { save.Stop(); Ui.Guard(SaveDrafts); };
    }

    List<MeetingNotes.ReviewItem> Items => detail?.Notes?.ReviewItems ?? [];
    List<SpeakerNaming.Suggestion> SpeakerItems => detail?.SpeakerSuggestions.OrderBy(p => p.Key).Select(p => p.Value).ToList() ?? [];
    MeetingLanguage Language => detail?.Meeting.MeetingLanguage ?? MeetingLanguage.Zh;

    public void FlushDraft()
    {
        save.Stop();
        SaveDrafts();
    }

    public FrameworkElement Show(MeetingDetail value, bool changed)
    {
        var newQuestions = (value.Notes?.ReviewItems ?? []).Select(r => r.Question).ToList();
        if (changed || detail?.Meeting.Id != value.Meeting.Id)
        {
            FlushDraft();
            detail = value;
            questions = newQuestions;
            LoadDrafts();
        }
        else if (!questions.SequenceEqual(newQuestions))
        {
            // 題目換了（依回答修正、重新整理、復原上一版）：回答是照題號存的，清掉（說話者的題目另外存，不清）
            host.Controller.DeleteDrafts(value.Meeting.Id!.Value, "review");
            answers.Clear();
            dismissed.Clear();
            detail = value;
            questions = newQuestions;
        }
        else detail = value;
        // 說話者的建議被套用或清掉（例如從「猜到幾位說話者」那邊）：那位的回答不留
        foreach (var idx in speakerAnswers.Keys.Where(k => !value.SpeakerSuggestions.ContainsKey(k)).ToList()) speakerAnswers.Remove(idx);
        speakerSkipped.RemoveWhere(k => !value.SpeakerSuggestions.ContainsKey(k));
        var speakerItems = SpeakerItems;
        if (value.Notes is null && speakerItems.Count == 0)
            return new Border { Padding = new Thickness(32), Child = Ui.Text(L.T("轉錄和整理完成後，會議紀錄會出現在這裡。"), color: Ui.Secondary) };
        if (Items.Count == 0 && speakerItems.Count == 0)
            return Ui.Scroll(Ui.Column(8, Ui.Text(L.T("沒有待確認的項目"), 15, color: Ui.Celadon),
                Ui.Text(L.T("會議紀錄裡推測出來的人名、專有名詞、負責人和期限，都已經確認過了。"), color: Ui.Secondary)), 32);
        var column = new StackPanel { Spacing = 22 };
        column.Children.Add(Ui.Text(Items.Any(i => i.YesNo) || speakerItems.Count > 0
            ? L.T("以下是整理時不確定的地方。按「是」「不是」或直接回答就好；看不懂或不重要的可以略過。")
            : L.T("以下是整理時不確定的地方。直接回答就好；看不懂或不重要的可以略過。"), color: Ui.Secondary));
        var submit = Ui.Button("", Submit, accent: true);
        void UpdateSubmit()
        {
            submit.Content = SubmitTitle();
            submit.IsEnabled = (Answered + SpeakerAnswered > 0 || dismissed.Count + speakerSkipped.Count > 0) && window.State.DetailStatus?.IsActive != true;
        }
        foreach (var suggestion in speakerItems) column.Children.Add(SpeakerRow(value, suggestion, UpdateSubmit));
        for (var i = 0; i < Items.Count; i++)
        {
            var index = i;
            var item = Items[i];
            var question = Ui.Text(item.Question, 14, select: true);
            question.Language = Ui.ContentLanguage(value.Meeting.MeetingLanguage);
            if (dismissed.Contains(index)) { question.Foreground = Ui.Secondary; question.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough; }
            var skip = SkipBox(dismissed.Contains(index), on => { if (on) dismissed.Add(index); else dismissed.Remove(index); });
            // 題目放中間那欄才會換行（放在橫的 StackPanel 裡不會換行，長的題目會被切掉）
            var lead = Ui.Row(10, Ui.Text($"{index + 1}", 14, bold: true, color: Ui.Signal, wrap: false), Ui.Time(item.Time, host.Player.IsLoaded ? s => host.Player.Seek(s) : null));
            lead.VerticalAlignment = VerticalAlignment.Top;   // 題號、時間對齊題目的第一行
            var head = Ui.Bar(lead, question, null, 10);
            // 是非題（R32、M§27）：「是」「不是」，選了「不是」才要填正確的答案；舊的草稿認不出來時照舊用輸入框
            if (item.YesNo && YesNoAnswer.Parse(answers.GetValueOrDefault(index) ?? "", Language) is { } choice)
            {
                var row = Ui.Bar(YesNoButtons(choice, dismissed.Contains(index), text => answers[index] = text), null, skip, 12);
                row.Margin = new Thickness(22, 0, 0, 0);
                var parts = Ui.Column(8, head, row);
                if (choice.IsYes == false)
                {
                    var correction = new TextBox { PlaceholderText = L.T("正確的是…（可以不填）"), Text = choice.Correction, IsEnabled = !dismissed.Contains(index), Margin = new Thickness(22, 0, 0, 0) };
                    correction.TextChanged += (_, _) => { answers[index] = new YesNoAnswer(false, correction.Text).Text(Language); UpdateSubmit(); save.Stop(); save.Start(); };
                    parts.Children.Add(correction);
                }
                column.Children.Add(parts);
                continue;
            }
            var answer = new TextBox { PlaceholderText = L.T("你的回答"), Text = answers.GetValueOrDefault(index) ?? "", IsEnabled = !dismissed.Contains(index) };
            answer.TextChanged += (_, _) => { answers[index] = answer.Text; UpdateSubmit(); save.Stop(); save.Start(); };
            var answerRow = Ui.Bar(null, answer, skip, 12);
            answerRow.Margin = new Thickness(22, 0, 0, 0);
            column.Children.Add(Ui.Column(8, head, answerRow));
        }
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

    /// <summary>說話者的題目（R33.2）：「『說話者 3』是 weiwei 嗎？」，下面是原因；選「不是」可以挑最像的人或打名字，也可以不填</summary>
    FrameworkElement SpeakerRow(MeetingDetail value, SpeakerNaming.Suggestion suggestion, Action updateSubmit)
    {
        var idx = suggestion.Speaker;
        var skipped = speakerSkipped.Contains(idx);
        var current = value.Speakers.GetValueOrDefault(idx) ?? "";
        var question = Ui.Text(Language.Text($"「{current}」是{suggestion.Name}嗎？", $"Is “{current}” {suggestion.Name}?", $"「{current}」は{suggestion.Name}ですか？"), 14, select: true);  // l10n-skip：會議語言的題目
        question.Language = Ui.ContentLanguage(Language);
        if (skipped) { question.Foreground = Ui.Secondary; question.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough; }
        var icon = new FontIcon { Glyph = "\uE77B", FontSize = 14, Foreground = Ui.Speaker(idx), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        var parts = Ui.Column(8, Ui.Bar(icon, question, null, 10));
        if (suggestion.Reason.Length > 0 || suggestion.Time.Length > 0)
        {
            var reason = Ui.Text(suggestion.Reason, 13, color: Ui.Secondary);
            reason.Language = Ui.ContentLanguage(Language);
            var line = Ui.Bar(suggestion.Time.Length > 0 ? (FrameworkElement)Ui.Time(suggestion.Time, host.Player.IsLoaded ? s => host.Player.Seek(s) : null) : null, reason, null, 6);
            line.Margin = new Thickness(22, 0, 0, 0);
            parts.Children.Add(line);
        }
        var choice = YesNoAnswer.Parse(speakerAnswers.GetValueOrDefault(idx) ?? "", Language) ?? new YesNoAnswer((bool?)null);
        var skip = SkipBox(skipped, on => { if (on) speakerSkipped.Add(idx); else speakerSkipped.Remove(idx); });
        var row = Ui.Bar(YesNoButtons(choice, skipped, text => speakerAnswers[idx] = text), null, skip, 12);
        row.Margin = new Thickness(22, 0, 0, 0);
        parts.Children.Add(row);
        if (choice.IsYes == false)
        {
            var name = new TextBox { PlaceholderText = L.T("是誰？（可以不填）"), Text = choice.Correction, IsEnabled = !skipped };
            name.TextChanged += (_, _) => { speakerAnswers[idx] = new YesNoAnswer(false, name.Text).Text(Language); updateSubmit(); save.Stop(); save.Start(); };
            var others = (value.SpeakerCandidates.GetValueOrDefault(idx) ?? []).Where(n => !string.Equals(n, suggestion.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            FrameworkElement? pick = null;
            if (others.Count > 0)
            {
                var menu = new MenuFlyout();
                foreach (var other in others)
                    menu.Items.Add(Ui.Item(other, () => { speakerAnswers[idx] = new YesNoAnswer(false, other).Text(Language); SaveDrafts(); window.Refresh(); }));
                pick = new DropDownButton { Content = L.T("是…"), Flyout = menu, IsEnabled = !skipped };
            }
            var line = Ui.Bar(pick, name, null, 10);
            line.Margin = new Thickness(22, 0, 0, 0);
            parts.Children.Add(line);
        }
        return parts;
    }

    /// <summary>「是」「不是」兩個按鈕（M§27）：選了「是」就清掉填過的答案；按了重畫，「不是」才出現輸入框</summary>
    FrameworkElement YesNoButtons(YesNoAnswer choice, bool disabled, Action<string> set)
    {
        Microsoft.UI.Xaml.Controls.Primitives.ToggleButton Choice(string label, bool yes)
        {
            var button = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = label, IsChecked = choice.IsYes == yes, IsEnabled = !disabled, MinWidth = 56 };
            button.Click += (_, _) =>
            {
                set(new YesNoAnswer(yes, yes ? "" : choice.Correction).Text(Language));
                SaveDrafts();
                window.Refresh();
            };
            return button;
        }
        return Ui.Row(4, Choice(L.T("是"), true), Choice(L.T("不是"), false));
    }

    CheckBox SkipBox(bool on, Action<bool> changed)
    {
        var skip = new CheckBox { Content = L.T("略過"), IsChecked = on };
        skip.Click += (_, _) =>
        {
            changed(skip.IsChecked == true);
            save.Stop();
            SaveDrafts();
            window.Refresh();
        };
        return skip;
    }

    int Answered => answers.Count(p => !dismissed.Contains(p.Key) && p.Value.Trim().Length > 0);
    int SpeakerAnswered => speakerAnswers.Count(p => !speakerSkipped.Contains(p.Key) && p.Value.Length > 0);

    string SubmitTitle() => (Answered + SpeakerAnswered, dismissed.Count + speakerSkipped.Count) switch
    {
        (0, 0) => L.T("送出"),
        (0, var d) => L.F("略過 %lld 題", d),
        (var a, 0) => L.F("送出 %lld 題回答", a),
        var (a, d) => L.F("送出 %lld 題回答、略過 %lld 題", a, d),
    };

    string Footnote(bool hasSpeakers)
    {
        var speakers = L.T("說話者的題目不會送給整理工具，送出後直接換名字。");
        if (Items.Count == 0) return speakers;
        var provider = host.Settings.Provider == LLMProvider.None ? "LLM" : host.Settings.Provider.Label();
        var notes = L.F("有回答的題目會交給 %@ 依答案修正會議紀錄；略過的題目直接移除。改錯了可以從「更多」復原上一版。", provider);
        return hasSpeakers ? notes + " " + speakers : notes;
    }

    /// <summary>送出：先送整理工具要處理的題目（題目文字可能有說話者的名字，改名後會變），失敗時全部留著；再處理說話者的題目</summary>
    void Submit()
    {
        if (detail is null) return;
        var id = detail.Meeting.Id!.Value;
        if (Answered > 0 || dismissed.Count > 0)
        {
            // 送出失敗（例如整理工具設成不整理）時保留回答
            if (!host.Controller.SubmitReview(id, answers, dismissed)) return;
        }
        host.Controller.AnswerSpeakerQuestions(id, new Dictionary<int, string>(speakerAnswers), new HashSet<int>(speakerSkipped));
        save.Stop();
        host.Controller.DeleteDrafts(id, "review");
        host.Controller.DeleteDrafts(id, "speaker-review");
        answers.Clear();
        dismissed.Clear();
        speakerAnswers.Clear();
        speakerSkipped.Clear();
    }

    /// <summary>說話者的草稿 key：建議換了人（重新辨識、重新轉錄）就是新的題目</summary>
    static string SpeakerKey(SpeakerNaming.Suggestion s) => $"{s.Speaker}:{s.Name}";

    void LoadDrafts()
    {
        answers.Clear();
        dismissed.Clear();
        speakerAnswers.Clear();
        speakerSkipped.Clear();
        if (detail is null) return;
        var drafts = host.Controller.Drafts(detail.Meeting.Id!.Value);
        for (var i = 0; i < Items.Count; i++)
        {
            if (drafts.TryGetValue("review:" + Items[i].Question, out var answer)) answers[i] = answer;
            if (drafts.ContainsKey("review-skip:" + Items[i].Question)) dismissed.Add(i);
        }
        foreach (var s in SpeakerItems)
        {
            if (drafts.TryGetValue("speaker-review:" + SpeakerKey(s), out var answer)) speakerAnswers[s.Speaker] = answer;
            if (drafts.ContainsKey("speaker-review-skip:" + SpeakerKey(s))) speakerSkipped.Add(s.Speaker);
        }
    }

    void SaveDrafts()
    {
        if (detail is null) return;
        var id = detail.Meeting.Id!.Value;
        for (var i = 0; i < Items.Count; i++)
        {
            host.Controller.SaveDraft(id, "review:" + Items[i].Question, answers.GetValueOrDefault(i) ?? "");
            host.Controller.SaveDraft(id, "review-skip:" + Items[i].Question, dismissed.Contains(i) ? "1" : "");
        }
        foreach (var s in SpeakerItems)
        {
            host.Controller.SaveDraft(id, "speaker-review:" + SpeakerKey(s), speakerAnswers.GetValueOrDefault(s.Speaker) ?? "");
            host.Controller.SaveDraft(id, "speaker-review-skip:" + SpeakerKey(s), speakerSkipped.Contains(s.Speaker) ? "1" : "");
        }
    }
}

/// <summary>對這場會議提問（同 Mac 的 AskTab，M§21.8、R19.6）：回答附上錄音時間點，點了跳過去；問答紀錄留在會議裡</summary>
public sealed class AskTab
{
    readonly AppHost host;
    readonly MainWindow window;
    readonly TextBox input = new() { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
    CancellationTokenSource? asking;
    long? askingMeeting;
    MeetingDetail? detail;

    public AskTab(AppHost host, MainWindow window)
    {
        this.host = host;
        this.window = window;
        input.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Submit(); e.Handled = true; } };
    }

    public FrameworkElement Show(MeetingDetail value, bool changed)
    {
        detail = value;
        var id = value.Meeting.Id!.Value;
        input.PlaceholderText = L.T("問這場會議，例如：誰負責寄信的部分？");
        var records = host.Db.Questions(id);
        var list = new StackPanel { Spacing = 18, Padding = new Thickness(32, 20, 32, 20) };
        if (records.Count == 0 && askingMeeting != id)
            list.Children.Add(Ui.Column(6, Ui.Heading(L.T("問這場會議"), MeetingLanguage.Zh),
                Ui.Text(L.T("例如：「誰負責寄信的部分？」「最後決定的上線日期是哪一天？」回答只根據這場會議的會議紀錄和逐字稿，每一點都附上時間，點了就能聽原話。"), color: Ui.Secondary)));
        foreach (var record in records) list.Children.Add(QuestionCard(record, null, time => { if (TimeFormat.Seconds(time) is double s) host.Player.Seek(s); }, null));
        if (askingMeeting == id)
            list.Children.Add(Ui.Row(10, new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                Ui.Text(L.F("正在用 %@ 回答…", host.Settings.Provider.ShortLabel()), color: Ui.Secondary), Ui.Link(L.T("取消"), () => asking?.Cancel())));
        var scroll = new ScrollViewer { Content = new ReadingColumn(list, 760) };
        var bar = InputBar(input, () => Submit(), records.Count == 0 ? null : () => { host.Db.DeleteQuestions(id); window.Refresh(); }, host);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(scroll);
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);
        scroll.Loaded += (_, _) => scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
        return grid;
    }

    async void Submit()
    {
        var question = input.Text.Trim();
        if (question.Length == 0 || asking is not null || detail is null || host.Ask.UnavailableReason is not null) return;
        var id = detail.Meeting.Id!.Value;
        input.Text = "";
        asking = new CancellationTokenSource();
        askingMeeting = id;
        window.Refresh();
        try { await Task.Run(() => host.Ask.AskMeeting(id, question, asking.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is LLMException or HttpRequestException or IOException or TimeoutException)
        {
            if (!asking.IsCancellationRequested) window.ShowError(e.Message);
        }
        finally
        {
            asking = null;
            askingMeeting = null;
            window.Refresh();
        }
    }

    /// <summary>提問的輸入列（單場和所有會議共用）</summary>
    public static FrameworkElement InputBar(TextBox input, Action submit, Action? clear, AppHost host)
    {
        var column = Ui.Column(6);
        column.Padding = new Thickness(32, 12, 32, 12);
        if (host.Ask.UnavailableReason is { } reason) column.Children.Add(Ui.Text(reason, 12, color: Ui.Signal));
        var ask = Ui.Button(L.T("提問"), submit, accent: true);
        ask.IsEnabled = host.Ask.UnavailableReason is null;
        FrameworkElement right = ask;
        if (clear is not null)
        {
            var more = new DropDownButton { Content = new SymbolIcon(Symbol.More) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, L.T("更多"));
            var menu = new MenuFlyout();
            menu.Items.Add(Ui.Item(L.T("清除問答紀錄"), clear));
            more.Flyout = menu;
            right = Ui.Row(8, ask, more);
        }
        // 輸入框一直用同一個（重畫時打到一半的字還在）：先從上一次的輸入列拿下來，同一個元件放進第二個地方 WinUI 會丟 0x800F1000
        //（2026-10-21 在虛擬機按「提問」時，送出後重畫就當掉、問題沒送出去）
        if (input.Parent is Panel old) old.Children.Remove(input);
        column.Children.Add(Ui.Bar(null, input, right, 10));
        return column;
    }

    /// <summary>一組問答：問題、回答、出處（時間點，跨會議時加上會議名稱）</summary>
    public static FrameworkElement QuestionCard(Data.QuestionRecord record, Func<long, string?>? meetingTitle, Action<string> onCitation, Action<long, string>? onMeetingCitation)
    {
        var panel = Ui.Column(8, Ui.Text(record.Question, 14, bold: true, select: true));
        if (AskPrompt.Answer.Decode(record.Answer) is { } answer)
        {
            panel.Children.Add(Ui.Text(answer.Text.Replace("**", ""), 14, color: answer.Found ? null : Ui.Secondary, select: true));
            foreach (var c in answer.Citations)
            {
                var label = c.MeetingId is long mid && onMeetingCitation is not null ? $"{meetingTitle?.Invoke(mid) ?? L.T("會議")} {c.Time}" : c.Time;
                var link = Ui.Link(label, () => { if (c.MeetingId is long m && onMeetingCitation is not null) onMeetingCitation(m, c.Time); else onCitation(c.Time); });
                link.Foreground = Ui.Celadon;
                panel.Children.Add(Ui.Split(link, Ui.Text(c.Quote, 13, color: Ui.Secondary, maxLines: 2), 8));
            }
            if (answer.MeetingIds is { } ids && record.MeetingId is null)
                panel.Children.Add(Ui.Text(ids.Count == 0 ? L.T("沒有找到相關的會議") : L.F("參考了 %lld 場會議", ids.Count), 12, color: Ui.Tertiary));
        }
        return new Border { Child = panel, Padding = new Thickness(14), CornerRadius = new CornerRadius(10), Background = Ui.Subtle };
    }
}
