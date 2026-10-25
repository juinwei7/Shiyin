using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;

namespace Shiyin.App.Views;

/// <summary>
/// 「紀錄」分頁和逐字稿頂部的「我的筆記」（同 Mac 的 NotesPage、ReviewBanner、UserNotesDisclosure，R44.1～R44.3）：
/// 閱讀的欄位寬度上限約 680；可以切換成編輯模式（R6.6），按「完成」存成新的版本，切到別的分頁、別場會議時自動存
/// </summary>
public sealed class NotesTabs
{
    readonly AppHost host;
    readonly MainWindow window;
    readonly ReviewDraft draft;
    NotesEditor? editor;
    readonly TextBox userNotes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    readonly UserNotesBox notesBox;

    public NotesTabs(AppHost host, MainWindow window, ReviewDraft draft)
    {
        this.host = host;
        this.window = window;
        this.draft = draft;
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

    /// <summary>時間點後面加待確認的標記（R41.1）：這一條對到題目時顯示，點了在下面展開就地回答（R41.2）</summary>
    UIElement? Trailing(MeetingDetail detail, NoteItemRef r, string time)
    {
        var marker = ReviewRows.Marker(detail, r, draft, window);
        var clock = time.Length > 0 ? Ui.Time(time, SeekIfLoaded) : null;
        return marker is null ? clock : clock is null ? marker : Ui.Row(8, clock, marker);
    }

    FrameworkElement? ExpandedCard(MeetingDetail detail, NoteItemRef r) =>
        draft.Expanded == r && detail.ReviewLinks.TryGetValue(r, out var indexes) ? ReviewRows.Card(detail, indexes, draft, host, window) : null;

    UIElement Items(IEnumerable<MeetingNotes.Item> items, MeetingLanguage language, MeetingDetail detail, Func<int, NoteItemRef> refOf)
    {
        var panel = new StackPanel { Spacing = 7 };
        var index = 0;
        foreach (var item in items)
        {
            var r = refOf(index++);
            var text = Ui.Text(item.Text, 14, select: true);
            text.Language = Ui.ContentLanguage(language);
            var row = Ui.Split(Ui.Text("•", 14, color: Ui.Secondary), Ui.Inline(text, Trailing(detail, r, item.Time)));
            panel.Children.Add(ExpandedCard(detail, r) is { } card ? Ui.Column(8, row, card) : row);
        }
        return panel;
    }

    UIElement ActionItems(List<MeetingNotes.ActionItem> items, MeetingLanguage language, MeetingDetail detail)
    {
        var grid = new Grid { ColumnSpacing = 18, RowSpacing = 9 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rows = 0;
        void Add(FrameworkElement e, int row, int column, int span = 1)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(e, row);
            Grid.SetColumn(e, column);
            if (span > 1) Grid.SetColumnSpan(e, span);
            grid.Children.Add(e);
        }
        Add(Ui.Caption(L.T("負責人")), rows, 0);
        Add(Ui.Caption(L.T("事項")), rows, 1);
        Add(Ui.Caption(L.T("期限")), rows++, 2);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var r = NoteItemRef.ActionItems(i);
            var owner = Ui.Text(item.Owner.Length == 0 ? L.T("未指定") : item.Owner, 14, color: item.Owner.Length == 0 ? Ui.Signal : null, wrap: false);
            owner.Language = Ui.ContentLanguage(language);
            var task = Ui.Text(item.Task, 14, select: true);
            task.Language = Ui.ContentLanguage(language);
            Add(owner, rows, 0);
            Add(Ui.Inline(task, Trailing(detail, r, item.Time)), rows, 1);
            Add(Ui.Text(item.Due.Length == 0 ? "—" : item.Due, 14, color: item.Due.Length == 0 ? Ui.Secondary : null, wrap: false), rows++, 2);
            if (ExpandedCard(detail, r) is { } card) Add(card, rows++, 0, 3);
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
        else if (host.Settings.Provider != LLMProvider.None && host.Settings.SummarizerProblem is { } problem)
        {
            // 選了整理工具但還沒準備好（沒登入、沒有 key…，R45.4）：說原因，給修的按鈕
            panel.Children.Add(Ui.Text(L.F("整理工具還沒設定好：%@", problem.Message), color: Ui.Secondary));
            panel.Children.Add(Ui.Button(problem.ActionTitle, () =>
            {
                if (problem.OffersLogin) CliLogin.Open(host, problem.Provider); else App.Current.ShowSettings();
            }));
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

    /// <summary>右上角的「編輯」；有上一版時旁邊多一顆「復原上一版」（R44.4，原本在「更多」）</summary>
    FrameworkElement EditBar(MeetingDetail detail)
    {
        var busy = window.State.DetailStatus?.IsActive == true;
        var row = Ui.Row(8);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        if (detail.CanUndoNotes)
        {
            var undo = Ui.Button(L.T("復原上一版"), () => host.Controller.UndoNotes(detail.Meeting.Id!.Value), tooltip: L.T("換回上一版會議紀錄（再按一次換回更早的）"));
            undo.IsEnabled = !busy;
            row.Children.Add(undo);
        }
        var edit = Ui.Button(L.T("編輯"), () =>
        {
            editor = new NotesEditor(detail.Notes!, detail.Meeting.Id!.Value, detail.Meeting.MeetingLanguage);
            editor.Finished += save =>
            {
                var e = editor;
                editor = null;
                if (save && e is not null) host.Controller.FinishNotesEdit(e.Notes, e.Base, e.MeetingId);
                window.Refresh();
            };
            window.Refresh();
        }, tooltip: busy ? L.T("正在處理這場會議，完成後才能編輯") : L.T("直接修改會議紀錄，存檔時會留一個版本"));
        edit.IsEnabled = !busy;
        row.Children.Add(edit);
        return row;
    }

    /// <summary>紀錄（R44.1）：摘要、決議、待辦、未決問題、議題、自訂段落由上而下一頁；有待確認時頂部一條可以展開（R44.2）</summary>
    public FrameworkElement Page(MeetingDetail detail, ReviewTab review)
    {
        if (editor is { } e && e.MeetingId == detail.Meeting.Id) return e.Root;
        if (detail.Notes is not { IsEmpty: false } notes) return Placeholder(detail);
        var language = detail.Meeting.MeetingLanguage;
        var column = Column();
        if (detail.ReviewCount > 0) column.Children.Add(ReviewBanner(detail, review));
        column.Children.Add(EditBar(detail));
        if (notes.Summary.Count > 0) column.Children.Add(Section(L.T("摘要"), language, Items(notes.Summary, language, detail, NoteItemRef.Summary)));
        if (notes.Decisions.Count > 0) column.Children.Add(Section(L.T("決議"), language, Items(notes.Decisions, language, detail, NoteItemRef.Decisions)));
        if (notes.ActionItems.Count > 0) column.Children.Add(Section(L.T("待辦"), language, ActionItems(notes.ActionItems, language, detail)));
        if (notes.OpenQuestions.Count > 0) column.Children.Add(Section(L.T("未決問題"), language, Items(notes.OpenQuestions, language, detail, NoteItemRef.OpenQuestions)));
        if (notes.Topics.Count > 0)
        {
            var topics = new StackPanel { Spacing = 22 };
            for (var t = 0; t < notes.Topics.Count; t++)
            {
                var topic = notes.Topics[t];
                var index = t;
                topics.Children.Add(Ui.Column(10, Ui.Bar(Ui.Time(topic.Start, SeekIfLoaded) as FrameworkElement, Ui.Heading(topic.Title, language, 16), null, 10),
                    Items(topic.Points, language, detail, i => NoteItemRef.TopicPoint(index, i))));
            }
            column.Children.Add(Section(L.T("議題"), language, topics));
        }
        for (var c = 0; c < notes.Custom.Count; c++)
        {
            var section = notes.Custom[c];
            var index = c;
            if (section.Items.Count > 0) column.Children.Add(Section(section.Title, language, Items(section.Items, language, detail, i => NoteItemRef.Custom(index, i))));
        }
        if (detail.Meeting.NotesBy is { } by)
        {
            var template = window.State.Templates.FirstOrDefault(t => t.Id == detail.Meeting.TemplateId)?.DisplayName() ?? L.T("一般會議");
            column.Children.Add(Ui.Text(L.F("由 %@ 整理．%@", LLMProviders.Parse(by)?.ShortLabel() ?? by, L.T("範本：") + template), 12, color: Ui.Tertiary));
        }
        return Reading(column);
    }

    /// <summary>紀錄頂部的「還有 N 題待確認」（R44.2，同 Mac 的 ReviewBanner）：點了在下面展開待確認的題目，再點收起；展開的狀態換會議就重設</summary>
    FrameworkElement ReviewBanner(MeetingDetail detail, ReviewTab review)
    {
        var state = window.State;
        var icon = new FontIcon { Glyph = "\uE9CE", FontSize = 14, Foreground = Ui.Signal, VerticalAlignment = VerticalAlignment.Center };
        var header = Ui.Row(10, icon, Ui.Text(L.F("還有 %lld 題待確認", detail.ReviewCount), 14, bold: true, wrap: false));
        var expander = new Expander
        {
            Header = header, IsExpanded = state.ReviewExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, L.F("待確認 %lld 題", detail.ReviewCount));
        if (state.ReviewExpanded) expander.Content = review.Embedded(detail);
        // 展開時才建內容（題目可能很多）；只記狀態、不整頁重畫，題目裡的按鈕會自己重畫
        expander.Expanding += (_, _) => { state.ReviewExpanded = true; expander.Content ??= review.Embedded(detail); };
        expander.Collapsed += (_, _) => state.ReviewExpanded = false;
        return expander;
    }

    /// <summary>逐字稿頂部的「我的筆記」（R44.3，同 Mac 的 UserNotesDisclosure）：收起時一行（幾行筆記、重點標記），展開是輸入框；
    /// 播放錄音時換行，會自動加上當下的播放時間（R2.4）</summary>
    public FrameworkElement UserNotesDisclosure(MeetingDetail detail)
    {
        var state = window.State;
        notesBox.Load(detail.Meeting.Id!.Value, detail.Meeting.UserNotes);
        userNotes.Language = Ui.ContentLanguage(detail.Meeting.MeetingLanguage);
        var lines = detail.Meeting.UserNotes.Split('\n').Count(l => l.Trim().Length > 0);
        var icon = new FontIcon { Glyph = "\uE70B", FontSize = 14, Foreground = Ui.Secondary, VerticalAlignment = VerticalAlignment.Center };
        var label = lines > 0 ? L.F("我的筆記（%lld 行）", lines) : L.T("寫筆記");
        var expander = new Expander
        {
            Header = Ui.Row(8, icon, Ui.Text(label, 14, wrap: false)), IsExpanded = state.UserNotesExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, label);
        FrameworkElement Body()
        {
            if (userNotes.Parent is Panel old) old.Children.Remove(userNotes);
            userNotes.Height = 180;
            return Ui.Column(8,
                Ui.Caption(L.T("播放錄音時換行，會自動加上當下的播放時間。整理時 AI 會參考這些筆記；改完之後可以從「更多 → 重新處理」選「重新整理會議紀錄」。")),
                userNotes);
        }
        if (state.UserNotesExpanded) expander.Content = Body();
        expander.Expanding += (_, _) => { state.UserNotesExpanded = true; expander.Content ??= Body(); };
        expander.Collapsed += (_, _) => state.UserNotesExpanded = false;
        FrameworkElement? marks = null;
        if (detail.Bookmarks.Count > 0)
        {
            var menu = new MenuFlyout();
            foreach (var b in detail.Bookmarks) menu.Items.Add(Ui.Item($"{TimeFormat.String(b.Time)} {b.Label}", () => Seek(b.Time)));
            marks = new DropDownButton { Content = L.F("重點標記 %lld", detail.Bookmarks.Count), Flyout = menu, VerticalAlignment = VerticalAlignment.Top };
        }
        var bar = Ui.Bar(null, expander, marks, 8);
        return new Border { Child = new ReadingColumn(bar, 760), Padding = new Thickness(20, 8, 20, 0) };
    }
}

/// <summary>
/// 手動編輯會議紀錄（同 Mac 的 NotesEditor，R6.6）：每一條都是可以改的欄位，可以新增、刪除、上下移動；議題也在同一份（R44.1）；按「完成」才存成新版本
/// </summary>
public sealed class NotesEditor
{
    public Grid Root { get; } = new();
    public MeetingNotes Base { get; }
    public long MeetingId { get; }
    readonly MeetingLanguage language;
    readonly StackPanel body = new() { Spacing = 22 };
    MeetingNotes notes;
    public event Action<bool>? Finished;

    public MeetingNotes Notes => notes;

    public NotesEditor(MeetingNotes notes, long meetingId, MeetingLanguage language)
    {
        Base = notes;
        this.notes = Copy(notes);
        MeetingId = meetingId;
        this.language = language;
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = Ui.Bar(Ui.Text(L.T("編輯會議紀錄"), 15, bold: true), null,
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
        var titleBox = new TextBox { Text = notes.Title, PlaceholderText = L.T("標題"), FontFamily = Ui.TitleFont(language), FontSize = 18 };
        titleBox.TextChanged += (_, _) => notes = notes with { Title = titleBox.Text };
        body.Children.Add(titleBox);
        body.Children.Add(Section(L.T("摘要"), ItemsEditor(notes.Summary)));
        body.Children.Add(Section(L.T("決議"), ItemsEditor(notes.Decisions)));
        body.Children.Add(Section(L.T("待辦"), ActionsEditor(notes.ActionItems)));
        body.Children.Add(Section(L.T("未決問題"), ItemsEditor(notes.OpenQuestions)));
        body.Children.Add(Section(L.T("議題"), TopicsEditor()));
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

    /// <summary>議題（原本自己一個「詳細」分頁，R44.1）：每個議題有時間、標題和幾條重點</summary>
    UIElement TopicsEditor()
    {
        var panel = Ui.Column(14);
        for (var i = 0; i < notes.Topics.Count; i++)
        {
            var index = i;
            var topic = notes.Topics[i];
            var start = new TextBox { Text = topic.Start, Width = 70, PlaceholderText = L.T("時間") };
            start.TextChanged += (_, _) => notes.Topics[index] = notes.Topics[index] with { Start = start.Text };
            var title = new TextBox { Text = topic.Title, PlaceholderText = L.T("議題"), FontFamily = Ui.TitleFont(language) };
            title.TextChanged += (_, _) => notes.Topics[index] = notes.Topics[index] with { Title = title.Text };
            var delete = Ui.IconButton(Symbol.Delete, L.T("刪除這個議題"), () => { notes.Topics.RemoveAt(index); Rebuild(); });
            panel.Children.Add(Ui.Column(8, Ui.Bar(start, title, delete), ItemsEditor(notes.Topics[index].Points)));
        }
        panel.Children.Add(Ui.Link(L.T("新增議題"), () => { notes.Topics.Add(new MeetingNotes.Topic("", "", [])); Rebuild(); }));
        return panel;
    }

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
