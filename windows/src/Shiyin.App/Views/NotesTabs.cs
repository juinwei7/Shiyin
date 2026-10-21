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

    static StackPanel Column() => new() { Spacing = 26, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };

    FrameworkElement Section(string title, MeetingLanguage language, UIElement content) =>
        Ui.Column(10, Ui.Heading(title, language), content);

    UIElement Items(IEnumerable<MeetingNotes.Item> items, MeetingLanguage language)
    {
        var panel = new StackPanel { Spacing = 7 };
        foreach (var item in items)
        {
            var text = Ui.Text(item.Text, 14, select: true);
            text.Language = Ui.ContentLanguage(language);
            var row = Ui.Split(Ui.Text("•", 14, color: Ui.Secondary), item.Time.Length > 0 ? Ui.Split(text, (FrameworkElement)Ui.Time(item.Time, SeekIfLoaded), 8, new GridLength(1, GridUnitType.Star)) : text);
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
            Add(item.Time.Length > 0 ? Ui.Split(task, (FrameworkElement)Ui.Time(item.Time, SeekIfLoaded), 8, new GridLength(1, GridUnitType.Star)) : task, i + 1, 1);
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
        return Ui.Scroll(column, 32);
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
            column.Children.Add(Ui.Column(10, Ui.Row(10, Ui.Time(topic.Start, SeekIfLoaded), Ui.Heading(topic.Title, language)), Items(topic.Points, language)));
        return Ui.Scroll(column, 32);
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
        var grid = new Grid { RowSpacing = 10, Padding = new Thickness(32, 20, 32, 20), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
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
        return grid;
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
    readonly StackPanel body = new() { Spacing = 22, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
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
        var scroll = Ui.Scroll(body, 32);
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
            panel.Children.Add(Ui.Bar(null, Ui.Split(text, time, 6, new GridLength(1, GridUnitType.Star)), RowButtons(items, index)));
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
        var up = Ui.IconButton(Symbol.Up, L.T("往上移"), () => { (items[index - 1], items[index]) = (items[index], items[index - 1]); Rebuild(); });
        up.IsEnabled = index > 0;
        var down = Ui.IconButton(Symbol.Download, L.T("往下移"), () => { (items[index + 1], items[index]) = (items[index], items[index + 1]); Rebuild(); });
        down.IsEnabled = index < items.Count - 1;
        return Ui.Row(2, up, down, Ui.IconButton(Symbol.Delete, L.T("刪除"), () => { items.RemoveAt(index); Rebuild(); }));
    }
}

/// <summary>待確認分頁（同 Mac 的 ReviewTab，R6）：每題可以回答或略過，送出後由整理工具依回答修正。回答的草稿存在資料庫（M§21.15）</summary>
public sealed class ReviewTab
{
    readonly AppHost host;
    readonly MainWindow window;
    readonly Dictionary<int, string> answers = [];
    readonly HashSet<int> dismissed = [];
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
            // 題目換了（依回答修正、重新整理、復原上一版）：回答是照題號存的，清掉
            host.Controller.DeleteDrafts(value.Meeting.Id!.Value, "review");
            answers.Clear();
            dismissed.Clear();
            detail = value;
            questions = newQuestions;
        }
        else detail = value;
        if (value.Notes is null) return new Border { Padding = new Thickness(32), Child = Ui.Text(L.T("轉錄和整理完成後，會議紀錄會出現在這裡。"), color: Ui.Secondary) };
        if (Items.Count == 0)
            return Ui.Scroll(Ui.Column(8, Ui.Text(L.T("沒有待確認的項目"), 15, color: Ui.Celadon),
                Ui.Text(L.T("會議紀錄裡推測出來的人名、專有名詞、負責人和期限，都已經確認過了。"), color: Ui.Secondary)), 32);
        var column = new StackPanel { Spacing = 22, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        column.Children.Add(Ui.Text(L.T("以下是整理時不確定的地方。直接回答就好；看不懂或不重要的可以略過。"), color: Ui.Secondary));
        var submit = Ui.Button("", Submit, accent: true);
        void UpdateSubmit()
        {
            submit.Content = SubmitTitle();
            submit.IsEnabled = (Answered > 0 || dismissed.Count > 0) && window.State.DetailStatus?.IsActive != true;
        }
        for (var i = 0; i < Items.Count; i++)
        {
            var index = i;
            var item = Items[i];
            var question = Ui.Text(item.Question, 14, select: true);
            question.Language = Ui.ContentLanguage(value.Meeting.MeetingLanguage);
            if (dismissed.Contains(index)) { question.Foreground = Ui.Secondary; question.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough; }
            var answer = new TextBox { PlaceholderText = L.T("你的回答"), Text = answers.GetValueOrDefault(index) ?? "", IsEnabled = !dismissed.Contains(index) };
            answer.TextChanged += (_, _) => { answers[index] = answer.Text; UpdateSubmit(); save.Stop(); save.Start(); };
            var skip = new CheckBox { Content = L.T("略過"), IsChecked = dismissed.Contains(index) };
            skip.Click += (_, _) =>
            {
                if (skip.IsChecked == true) dismissed.Add(index); else dismissed.Remove(index);
                save.Stop();
                SaveDrafts();
                window.Refresh();
            };
            var head = Ui.Row(10, Ui.Text($"{index + 1}", 14, bold: true, color: Ui.Signal, wrap: false), Ui.Time(item.Time, host.Player.IsLoaded ? s => host.Player.Seek(s) : null), question);
            var answerRow = Ui.Bar(null, answer, skip, 12);
            answerRow.Margin = new Thickness(22, 0, 0, 0);
            column.Children.Add(Ui.Column(8, head, answerRow));
        }
        UpdateSubmit();
        var provider = host.Settings.Provider == LLMProvider.None ? "LLM" : host.Settings.Provider.Label();
        var footer = Ui.Bar(Ui.Text(L.F("有回答的題目會交給 %@ 依答案修正會議紀錄；略過的題目直接移除。改錯了可以從「更多」復原上一版。", provider), 13, color: Ui.Secondary), null, submit, 12);
        footer.Padding = new Thickness(32, 12, 32, 12);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Ui.Scroll(column, 32));
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);
        return grid;
    }

    int Answered => answers.Count(p => !dismissed.Contains(p.Key) && p.Value.Trim().Length > 0);

    string SubmitTitle() => (Answered, dismissed.Count) switch
    {
        (0, 0) => L.T("送出"),
        (0, var d) => L.F("略過 %lld 題", d),
        (var a, 0) => L.F("送出 %lld 題回答", a),
        var (a, d) => L.F("送出 %lld 題回答、略過 %lld 題", a, d),
    };

    void Submit()
    {
        if (detail is null) return;
        // 送出失敗（例如整理工具設成不整理）時保留回答
        if (!host.Controller.SubmitReview(detail.Meeting.Id!.Value, answers, dismissed)) return;
        save.Stop();
        host.Controller.DeleteDrafts(detail.Meeting.Id!.Value, "review");
        answers.Clear();
        dismissed.Clear();
    }

    void LoadDrafts()
    {
        answers.Clear();
        dismissed.Clear();
        if (detail is null) return;
        var drafts = host.Controller.Drafts(detail.Meeting.Id!.Value);
        for (var i = 0; i < Items.Count; i++)
        {
            if (drafts.TryGetValue("review:" + Items[i].Question, out var answer)) answers[i] = answer;
            if (drafts.ContainsKey("review-skip:" + Items[i].Question)) dismissed.Add(i);
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
        var list = new StackPanel { Spacing = 18, Padding = new Thickness(32, 20, 32, 20), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        if (records.Count == 0 && askingMeeting != id)
            list.Children.Add(Ui.Column(6, Ui.Heading(L.T("問這場會議"), MeetingLanguage.Zh),
                Ui.Text(L.T("例如：「誰負責寄信的部分？」「最後決定的上線日期是哪一天？」回答只根據這場會議的會議紀錄和逐字稿，每一點都附上時間，點了就能聽原話。"), color: Ui.Secondary)));
        foreach (var record in records) list.Children.Add(QuestionCard(record, null, time => { if (TimeFormat.Seconds(time) is double s) host.Player.Seek(s); }, null));
        if (askingMeeting == id)
            list.Children.Add(Ui.Row(10, new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                Ui.Text(L.F("正在用 %@ 回答…", host.Settings.Provider.ShortLabel()), color: Ui.Secondary), Ui.Link(L.T("取消"), () => asking?.Cancel())));
        var scroll = new ScrollViewer { Content = list };
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
            var menu = new MenuFlyout();
            menu.Items.Add(Ui.Item(L.T("清除問答紀錄"), clear));
            more.Flyout = menu;
            right = Ui.Row(8, ask, more);
        }
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
