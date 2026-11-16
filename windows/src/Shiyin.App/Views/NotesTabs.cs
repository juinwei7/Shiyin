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
    /// <summary>心智圖的縮放（R91、design-windows §11.24）：整頁重畫時留著；mindMapMeeting 是哪一場的</summary>
    public MindMapZoom MindMapZoom { get; } = new();
    long? mindMapMeeting;

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

    /// <summary>心智圖回到預設的倍數（R91.3）：換會議、換分頁、切回條列、編輯</summary>
    public void ResetMindMapZoom() => MindMapZoom.Value = null;

    static StackPanel Column() => new() { Spacing = 26 };

    /// <summary>閱讀的欄位最寬 680（同 Mac），靠左；文字放大時跟著變寬（R59.2）</summary>
    static ScrollViewer Reading(UIElement column) => Ui.Scroll(new ReadingColumn(column, Ui.C(680)), 32);

    FrameworkElement Section(string title, MeetingLanguage language, UIElement content) =>
        Ui.Column(10, Ui.Heading(title, language, Ui.C(18)), content);

    /// <summary>時間點後面加待確認的標記（R41.1）：這一條對到題目時顯示，點了在下面展開就地回答（R41.2）</summary>
    UIElement? Trailing(MeetingDetail detail, NoteItemRef r, string time)
    {
        var marker = ReviewRows.Marker(detail, r, draft, window);
        var clock = time.Length > 0 ? Ui.Time(time, SeekIfLoaded, size: Ui.C(13)) : null;
        return marker is null ? clock : clock is null ? marker : Ui.Row(8, clock, marker);
    }

    // MARK: 紀錄的條目：雙擊就地改、右鍵、「⋯」（R100、M§74）

    NoteItemRef? editingItem;
    long? editingMeeting;

    /// <summary>正在就地改的那一條（只算目前這場會議；DEBUG 的 state）</summary>
    public NoteItemRef? EditingItem(MeetingDetail? detail) => detail?.Meeting.Id == editingMeeting ? editingItem : null;

    /// <summary>能不能就地改：有紀錄、不在垃圾桶、沒在錄這場、沒在處理（同「編輯」按鈕）</summary>
    bool CanEdit(MeetingDetail detail) => detail.Notes is not null && detail.Meeting.DeletedAt is null && host.Recorder.MeetingId != detail.Meeting.Id
        && window.State.DetailStatus?.IsActive != true && editor is null;

    /// <summary>開始就地改一條（雙擊、右鍵「編輯這一條」、DEBUG 的 noteitem editui）</summary>
    public void BeginEdit(MeetingDetail detail, NoteItemRef? item)
    {
        if (item is { } r && !CanEdit(detail)) return;
        editingItem = item;
        editingMeeting = detail.Meeting.Id;
        window.Refresh();
    }

    /// <summary>就地改的輸入框：Enter 儲存、Esc 取消；待辦只改事項</summary>
    FrameworkElement ItemEditor(MeetingDetail detail, NoteItemRef r, MeetingLanguage language)
    {
        var box = new TextBox
        {
            TextWrapping = TextWrapping.Wrap, Text = detail.Notes?.TextAt(r) ?? "", FontSize = Ui.C(14), Language = Ui.ContentLanguage(language),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, L.T("編輯這一條"));
        void Save()
        {
            editingItem = null;
            host.Controller.EditNoteItem(detail.Meeting.Id!.Value, r, box.Text);
            window.Refresh();
        }
        void Cancel() => BeginEdit(detail, null);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Save(); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Cancel(); }
        };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectionStart = box.Text.Length; };
        var save = Ui.Button(L.T("儲存"), Save, accent: true);
        box.TextChanged += (_, _) => save.IsEnabled = box.Text.Trim().Length > 0;
        var buttons = Ui.Row(8, Ui.Button(L.T("取消"), Cancel), save);
        var hint = Ui.Text(L.T("Enter 儲存、Esc 取消"), 12, color: Ui.Secondary);
        hint.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Column(6, box, Ui.Bar(null, hint, buttons));
    }

    /// <summary>一條紀錄的選單（同 Mac 的 NoteItemMenuItems）：拷貝、從這裡播放（有時間點、有錄音時）、編輯這一條、刪除這一條（不確認，可以復原上一版）</summary>
    void FillItemMenu(MenuFlyout menu, MeetingDetail detail, NoteItemRef r, string text, string time)
    {
        menu.Items.Add(Ui.Item(L.T("拷貝"), () => Copy.Text(text), icon: Symbol.Copy));
        if (TimeFormat.Seconds(time) is double seconds && host.Player.IsLoaded)
            menu.Items.Add(Ui.Item(L.T("從這裡播放"), () => host.Player.Seek(seconds, true), icon: Symbol.Play));
        menu.Items.Add(new MenuFlyoutSeparator());
        var editable = CanEdit(detail);
        menu.Items.Add(Ui.Item(L.T("編輯這一條"), () => BeginEdit(detail, r), editable, Symbol.Edit));
        menu.Items.Add(Ui.DangerItem(L.T("刪除這一條"), () => host.Controller.DeleteNoteItem(detail.Meeting.Id!.Value, r), editable));
    }

    /// <summary>時間點、待確認的標記後面接「⋯」</summary>
    static UIElement WithMore(UIElement? trailing, Button more) => trailing is null ? more : Ui.Row(8, trailing, more);

    /// <summary>
    /// 右鍵、雙擊就地改、滑過時的「⋯」。能直接改的條目不開文字選取（同 Mac a8a59e8，M§74）：開了的話右鍵是系統的文字選單（拷貝、全選）、
    /// 雙擊只選到一個詞；拷貝用右鍵的「拷貝」。整行（含行尾空白）都要收得到，列的背景設成透明（沒有背景的 Panel 空白處點不到）
    /// </summary>
    void ItemActions(Panel row, Button more, MeetingDetail detail, NoteItemRef r, string itemText, string time)
    {
        row.Background ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        row.ContextFlyout = RowMore.Context(menu => FillItemMenu(menu, detail, r, itemText, time));
        row.AddHandler(UIElement.DoubleTappedEvent, new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler((_, _) => BeginEdit(detail, r)), true);
        RowMore.Attach(row, more);
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
            // 就地改這一條（R100）
            if (EditingItem(detail) == r)
            {
                panel.Children.Add(Ui.Split(Ui.Text("•", Ui.C(14), color: Ui.Secondary), ItemEditor(detail, r, language)));
                continue;
            }
            var text = Ui.Text(item.Text, Ui.C(14));   // 不開文字選取（見 ItemActions）
            text.Language = Ui.ContentLanguage(language);
            var more = RowMore.Button(menu => FillItemMenu(menu, detail, r, item.Text, item.Time));
            var row = Ui.Split(Ui.Text("•", Ui.C(14), color: Ui.Secondary), Ui.Inline(text, WithMore(Trailing(detail, r, item.Time), more)));
            ItemActions(row, more, detail, r, item.Text, item.Time);
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
        Add(Ui.Text(L.T("負責人"), Ui.C(12), color: Ui.Secondary), rows, 0);
        Add(Ui.Text(L.T("事項"), Ui.C(12), color: Ui.Secondary), rows, 1);
        Add(Ui.Text(L.T("期限"), Ui.C(12), color: Ui.Secondary), rows++, 2);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var r = NoteItemRef.ActionItems(i);
            var owner = Ui.Text(item.Owner.Length == 0 ? L.T("未指定") : item.Owner, Ui.C(14), color: item.Owner.Length == 0 ? Ui.Signal : null, wrap: false);
            owner.Language = Ui.ContentLanguage(language);
            var task = Ui.Text(item.Task, Ui.C(14));   // 不開文字選取（見 ItemActions）
            task.Language = Ui.ContentLanguage(language);
            var more = RowMore.Button(menu => FillItemMenu(menu, detail, r, item.Task, item.Time));
            // 負責人是這場的說話者時放他的頭像（M§58.2、R83.2）
            if (detail.SpeakerIdx(item.Owner) is int speaker)
            {
                var avatar = Ui.Avatar(item.Owner, Ui.Speaker(speaker), Ui.C(18), speaker == detail.SelfIdx);
                avatar.VerticalAlignment = VerticalAlignment.Center;
                owner.VerticalAlignment = VerticalAlignment.Center;
                var chip = Ui.Row(6, avatar, owner);
                chip.VerticalAlignment = VerticalAlignment.Top;
                Add(chip, rows, 0);
            }
            else Add(owner, rows, 0);
            // 就地改只改事項（R100）：負責人、期限照舊用「編輯」
            if (EditingItem(detail) == r) Add(ItemEditor(detail, r, language), rows, 1);
            else
            {
                // 外面包一層撐滿事項那一欄：行尾的空白也能右鍵、雙擊
                var cell = new Grid { Children = { Ui.Inline(task, WithMore(Trailing(detail, r, item.Time), more)) } };
                ItemActions(cell, more, detail, r, item.Task, item.Time);
                Add(cell, rows, 1);
            }
            Add(Ui.Text(item.Due.Length == 0 ? "—" : item.Due, Ui.C(14), color: item.Due.Length == 0 ? Ui.Secondary : null, wrap: false), rows++, 2);
            if (ExpandedCard(detail, r) is { } card) Add(card, rows++, 0, 3);
        }
        return grid;
    }

    /// <summary>
    /// 人物（R92、M§66、design-windows §11.25，同 Mac 的 PeopleNotes）：每位說話者一張，頭像、名字、說了多久，重點（時間點、待確認的標記）、
    /// 負責的待辦。這場的紀錄還沒有每個人的重點時說明原因；有兩位以上說話者時可以只補上這一段（其他段落不動）
    /// </summary>
    FrameworkElement PeopleNotes(MeetingDetail detail, MeetingNotes notes, MeetingLanguage language)
    {
        if (!notes.People.Any(p => p.Points.Count > 0))
        {
            var panel = Ui.Column(12);
            panel.Margin = new Thickness(0, 10, 0, 0);
            if (detail.Speakers.Count < 2)
                panel.Children.Add(Ui.Text(L.T("這場會議只有一位說話者（或沒有辨識說話者），沒有依人物整理的重點。"), Ui.C(14), color: Ui.Secondary));
            else
            {
                panel.Children.Add(Ui.Text(L.T("這場的會議紀錄還沒有每個人的重點。"), Ui.C(14), color: Ui.Secondary));
                var title = L.T("產生每個人的重點");
                var button = new Button { Content = Ui.Row(8, new SymbolIcon(Symbol.People), Ui.Text(title, wrap: false)), HorizontalAlignment = HorizontalAlignment.Left };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
                ToolTipService.SetToolTip(button, L.T("用整理工具依說話者整理每個人的重點；摘要、決議、待辦這些段落不會變"));
                button.IsEnabled = window.State.DetailStatus?.IsActive != true;
                var id = detail.Meeting.Id!.Value;
                button.Click += (_, _) => host.Controller.GeneratePeopleNotes(id);
                panel.Children.Add(button);
            }
            return panel;
        }
        var cards = new StackPanel { Spacing = 30, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var card in detail.PeopleCards(notes)) cards.Children.Add(PersonCard(card, language, detail));
        return cards;
    }

    /// <summary>
    /// 一個人：頭像（顏色跟逐字稿的說話者一樣，對不到說話者時照名字算，同人物頁；「我」有外圈）、名字、「說了 N 分鐘（P%）」，
    /// 下面是重點，再下面是負責的待辦（「事項（期限 日期）」，待確認的標記連到原本的待辦）
    /// </summary>
    FrameworkElement PersonCard(MeetingDetail.PersonCard card, MeetingLanguage language, MeetingDetail detail)
    {
        var name = card.Person.Name;
        var avatar = Ui.Avatar(name, card.Speaker is int idx ? Ui.Speaker(idx) : Ui.Person(name), Ui.C(30), card.Speaker is not null && card.Speaker == detail.SelfIdx);
        avatar.VerticalAlignment = VerticalAlignment.Center;
        var spoke = card.Seconds is int seconds
            ? Ui.Text(L.F("說了 %@（%lld%%）", L.Ui(TimeFormat.Duration(seconds, MeetingLanguages.Interface)), card.Percent ?? 0), Ui.C(12), color: Ui.Secondary)
            : null;
        var info = Ui.Column(2, Ui.Heading(name, language, Ui.C(18)), spoke);
        info.VerticalAlignment = VerticalAlignment.Center;
        var column = Ui.Column(10, Ui.Split(avatar, info, 10), Items(card.Person.Points, language, detail, i => NoteItemRef.Person(card.Index, i)));
        if (card.Owned.Count > 0)
        {
            var label = Ui.Text(L.T("負責的待辦"), Ui.C(13), color: Ui.Secondary);
            label.Margin = new Thickness(0, 4, 0, 0);
            column.Children.Add(label);
            var items = card.Owned.Select(o => new MeetingNotes.Item(o.Item.Due.Length == 0 ? o.Item.Task : L.F("%@（期限 %@）", o.Item.Task, o.Item.Due), o.Item.Time));
            column.Children.Add(Items(items, language, detail, i => NoteItemRef.ActionItems(card.Owned[i].Index)));
        }
        return column;
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

    /// <summary>右上角的「編輯」；有上一版時旁邊多一顆「復原上一版」（R44.4，原本在「更多」）；左邊是「條列｜心智圖｜人物」（R67、R92）</summary>
    FrameworkElement EditBar(MeetingDetail detail)
    {
        var busy = window.State.DetailStatus?.IsActive == true;
        var row = Ui.Row(8);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        var mode = new SelectorBar();
        foreach (var (code, label) in new[] { ("list", L.T("條列")), ("mindMap", L.T("心智圖")), ("people", L.T("人物")) })
        {
            var item = new SelectorBarItem { Text = label, Tag = code };
            mode.Items.Add(item);
            if (code == host.Settings.NotesView) mode.SelectedItem = item;
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mode, L.T("顯示方式"));
        ToolTipService.SetToolTip(mode, L.T("把會議紀錄切換成條列、心智圖或依人物整理"));
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedItem?.Tag is not string code || code == host.Settings.NotesView) return;
            host.Settings.Set(AppSettings.NotesViewKey, code);
            window.Refresh();
        };
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
        var bar = new Grid();
        bar.Children.Add(mode);
        bar.Children.Add(row);
        mode.HorizontalAlignment = HorizontalAlignment.Left;
        return bar;
    }

    /// <summary>紀錄（R44.1）：摘要、決議、待辦、未決問題、議題、自訂段落由上而下一頁；有待確認時頂部一條可以展開（R44.2）。
    /// 也可以切換成心智圖（R67）、人物（R92：條列、心智圖不變，依人整理的另外一頁）</summary>
    public FrameworkElement Page(MeetingDetail detail, ReviewTab review)
    {
        if (editor is { } e && e.MeetingId == detail.Meeting.Id)
        {
            ResetMindMapZoom();
            return e.Root;
        }
        if (detail.Notes is not { IsEmpty: false } notes) return Placeholder(detail);
        var language = detail.Meeting.MeetingLanguage;
        var column = Column();
        // 加入、移除文件之後還沒重新整理（R61.8）：提示列不跟著文字大小（R59.2）
        if (host.Controller.DocumentsChanged.Contains(detail.Meeting.Id!.Value)) column.Children.Add(DocumentsBanner(detail.Meeting.Id!.Value));
        // 這場可能是某個定期會議的一場（R99.3、M§73）：資料夾的建議上面（同 Mac）
        if (window.State.SeriesJoinSuggestion(detail.Meeting) is { } join) column.Children.Add(SeriesJoinBanner(detail.Meeting.Id!.Value, join));
        // 這場很像某個資料夾裡的會議（R98.3、M§72）：待確認條的上面
        if (Organizing.Suggestion(detail.Meeting, window.State.Meetings, window.State.MeetingPeople, host.Recorder.MeetingId) is { } suggestion)
            column.Children.Add(FolderSuggestionBanner(detail.Meeting.Id!.Value, suggestion));
        if (detail.ReviewCount > 0) column.Children.Add(ReviewBanner(detail, review));
        column.Children.Add(EditBar(detail));
        if (host.Settings.NotesView == "mindMap") return MindMapPage(detail, notes, column);
        ResetMindMapZoom();
        if (host.Settings.NotesView == "people")
        {
            column.Children.Add(PeopleNotes(detail, notes, language));
            return Reading(column);
        }
        if (notes.Summary.Count > 0) column.Children.Add(Section(L.T("摘要"), language, Items(notes.Summary, language, detail, NoteItemRef.Summary)));
        // 定期會議的上一場的待辦（R99.5、M§73）：只有 followUps 不是空的才有；心智圖不畫
        if (notes.FollowUps.Count > 0) column.Children.Add(Section(L.T("上次的待辦"), language, FollowUps(notes.FollowUps, language)));
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
                topics.Children.Add(Ui.Column(10, Ui.Bar(Ui.Time(topic.Start, SeekIfLoaded, size: Ui.C(13)) as FrameworkElement, Ui.Heading(topic.Title, language, Ui.C(16)), null, 10),
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
            column.Children.Add(Ui.Text(L.F("由 %@ 整理．%@", LLMProviders.Parse(by)?.ShortLabel() ?? by, L.T("範本：") + template), Ui.C(12), color: Ui.Tertiary));
        }
        return Reading(column);
    }

    /// <summary>
    /// 心智圖模式（M§42.4）：上面照舊是提示條、編輯列，下面是心智圖；段落名稱跟著介面語言（同條列）。
    /// 整頁一起上下捲（同條列、同 Mac）：待確認展開時很高，心智圖不會被擠掉
    /// </summary>
    FrameworkElement MindMapPage(MeetingDetail detail, MeetingNotes notes, StackPanel header)
    {
        var layout = MindMap.LayoutOf(MindMap.Tree(notes, detail.Meeting.DisplayTitle, MindMap.Labels.For(MeetingLanguages.Interface, L.Ui)));
        if (mindMapMeeting != detail.Meeting.Id)
        {
            mindMapMeeting = detail.Meeting.Id;
            ResetMindMapZoom();
        }
        var page = new StackPanel();
        var scroller = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        page.Children.Add(new Border { Child = new ReadingColumn(header, Ui.C(680)), Padding = new Thickness(32, 24, 32, 16) });
        // Ctrl＋滾輪在整頁都能縮放、滑鼠在圖上時一般的滾輪捲整頁（design-windows §11.24）
        page.Children.Add(MindMapView.Scrolling(layout, detail.Meeting.MeetingLanguage, SeekIfLoaded, MindMapZoom, page: scroller));
        return scroller;
    }

    /// <summary>
    /// 這場很像某個資料夾裡的會議（R98.3、同 Mac 的 FolderSuggestionBanner）：「這場很像「客戶」裡的會議：陳家豪、Bob 都參加（2 場）」，
    /// 「不用」（之後不再建議這場）、「放進去」。提示列不跟著文字大小（R59.2），底色是淡淡的青瓷綠
    /// </summary>
    FrameworkElement FolderSuggestionBanner(long id, FolderSuggestion suggestion) =>
        SuggestionBanner("\uE8F4", Organizing.SuggestionText(suggestion, window.State.People),
            Ui.Button(L.T("不用"), () => host.Controller.DismissFolderSuggestion(id)),
            Ui.Button(L.T("放進去"), () => host.Controller.AcceptFolderSuggestion(id, suggestion.Folder), accent: true));

    /// <summary>
    /// 這場可能是某個定期會議的一場（R99.3、同 Mac 的 SeriesJoinBanner）：星期幾、時間對得上，聲紋只認出一位系列的人、證據不夠自動加入時。
    /// 「這場可能是定期會議「產品週會」的一場：週一、陳家豪 有參加」，「不是」（記進 series_rejected，之後不再建議）、「加進去」。
    /// 圖示同資訊列的定期會議（E895，Mac 是 arrow.triangle.2.circlepath）
    /// </summary>
    FrameworkElement SeriesJoinBanner(long id, SeriesJoinSuggestion suggestion)
    {
        var seriesId = suggestion.Series.Id!.Value;
        return SuggestionBanner("\uE895", Organizing.JoinSuggestionText(suggestion, window.State.People),
            Ui.Button(L.T("不是"), () => host.Controller.DismissSeriesJoin(id, seriesId)),
            Ui.Button(L.T("加進去"), () => host.Controller.AcceptSeriesJoin(id, seriesId), accent: true));
    }

    /// <summary>紀錄分頁頂端的建議（資料夾、定期會議）：青瓷綠的圖示、最多兩行的說明、右邊兩顆按鈕；底色是淡淡的青瓷綠（淺色 8%、深色 14%）</summary>
    static Border SuggestionBanner(string glyph, string message, Button dismiss, Button accept)
    {
        var text = Ui.Text(message, 13, maxLines: 2);
        var left = Ui.Split(new FontIcon { Glyph = glyph, FontSize = 15, Foreground = Ui.Celadon, VerticalAlignment = VerticalAlignment.Center }, text);
        var right = Ui.Row(8, dismiss, accept);
        left.VerticalAlignment = VerticalAlignment.Center;
        right.VerticalAlignment = VerticalAlignment.Center;
        // 對比主題時沒有底色、一圈細框（Ui.Tinted）
        return Ui.Tinted(new Border { Child = Ui.Bar(null, left, right, 12), Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(8) },
            Ui.Tint(Ui.CeladonColor, (Ui.IsDark ? 36 : 20) / 255.0));
    }

    /// <summary>
    /// 上次的待辦（R99.5、同 Mac 的 FollowUpsList）：每一條前面是狀態的圖示，後面事項、負責人（次要文字）、狀態（小字、狀態的顏色）、時間點。
    /// 編輯器不能改這一段（重新整理會重算）
    /// </summary>
    UIElement FollowUps(List<MeetingNotes.FollowUp> followUps, MeetingLanguage language)
    {
        var panel = new StackPanel { Spacing = 9 };
        foreach (var item in followUps)
        {
            var icon = FollowUpIcons.Icon(item.Status, Ui.C(15));
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, Ui.C(3), 0, 0);
            var task = Ui.Text(item.Task, Ui.C(14), select: true);
            task.Language = Ui.ContentLanguage(language);
            var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (item.Owner.Length > 0)
            {
                var owner = Ui.Text(item.Owner, Ui.C(14), color: Ui.Secondary, wrap: false);
                owner.Language = Ui.ContentLanguage(language);
                trailing.Children.Add(owner);
            }
            var status = Ui.Text(FollowUpIcons.Label(item.Status), Ui.C(12), color: FollowUpIcons.Color(item.Status), wrap: false);
            status.VerticalAlignment = VerticalAlignment.Center;
            trailing.Children.Add(status);
            if (item.Time.Length > 0) trailing.Children.Add(Ui.Time(item.Time, SeekIfLoaded, size: Ui.C(13)));
            panel.Children.Add(Ui.Split(icon, Ui.Inline(task, trailing)));
        }
        return panel;
    }

    /// <summary>文件有變動（R61.8，同 Mac 的 DocumentsBanner）：不用、重新整理會議紀錄</summary>
    FrameworkElement DocumentsBanner(long id)
    {
        var left = Ui.Row(8, new SymbolIcon(Symbol.Attach), Ui.Text(L.T("文件有變動，會議紀錄還沒用上。"), 13));
        var right = Ui.Row(8, Ui.Button(L.T("不用"), () => host.Controller.DismissDocumentsChanged(id)),
            Ui.Button(L.T("重新整理會議紀錄"), () => host.Controller.Resummarize(id), accent: true));
        left.VerticalAlignment = VerticalAlignment.Center;
        right.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Tinted(new Border { Child = Ui.Bar(null, left, right, 12), Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(8) }, Ui.Subtle);
    }

    /// <summary>紀錄頂部的「還有 N 題待確認」（R44.2，同 Mac 的 ReviewBanner）：點了在下面展開待確認的題目，再點收起；展開的狀態換會議就重設</summary>
    FrameworkElement ReviewBanner(MeetingDetail detail, ReviewTab review)
    {
        var state = window.State;
        var icon = new FontIcon { Glyph = "\uE9CE", FontSize = 14, Foreground = Ui.Signal, VerticalAlignment = VerticalAlignment.Center };
        var header = Ui.Row(10, icon, Ui.Text(L.F("還有 %lld 題待確認", detail.ReviewCount), Ui.C(14), bold: true, wrap: false));
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
        userNotes.FontSize = Ui.C(14);   // 我的筆記跟著會議內容的文字大小（R59.2）
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
            userNotes.Height = Ui.C(180);
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
            // 每個標記：跳到這裡、重新命名…、刪除（R100）
            foreach (var b in detail.Bookmarks)
            {
                var bookmark = b;
                var sub = new MenuFlyoutSubItem { Text = $"{TimeFormat.String(b.Time)} {b.Label}" };
                sub.Items.Add(Ui.Item(L.T("跳到這裡"), () => Seek(bookmark.Time), icon: Symbol.Play));
                sub.Items.Add(Ui.Item(L.T("重新命名…"), () => window.RenameUi(new RenameTarget.Bookmark(bookmark.Id ?? 0)), icon: Symbol.Edit));
                sub.Items.Add(new MenuFlyoutSeparator());
                sub.Items.Add(Ui.DangerItem(L.T("刪除"), () => host.Controller.DeleteBookmark(bookmark.MeetingId, bookmark.Id ?? 0)));
                menu.Items.Add(sub);
            }
            marks = new DropDownButton { Content = L.F("重點標記 %lld", detail.Bookmarks.Count), Flyout = menu, VerticalAlignment = VerticalAlignment.Top };
        }
        var bar = Ui.Bar(null, expander, marks, 8);
        return new Border { Child = new ReadingColumn(bar, Ui.C(760)), Padding = new Thickness(20, 8, 20, 0) };
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
    /// <summary>打開時有每個人的重點才顯示那一段（刪光了也留著，可以再新增；R92.5、M§66）</summary>
    readonly bool showsPeople;
    MeetingNotes notes;
    public event Action<bool>? Finished;

    public MeetingNotes Notes => notes;

    public NotesEditor(MeetingNotes notes, long meetingId, MeetingLanguage language)
    {
        Base = notes;
        this.notes = Copy(notes);
        MeetingId = meetingId;
        this.language = language;
        showsPeople = notes.People.Count > 0;
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = Ui.Bar(Ui.Text(L.T("編輯會議紀錄"), 15, bold: true), null,
            Ui.Row(8, Ui.Button(L.T("取消"), () => Finished?.Invoke(false)), Ui.Button(L.T("完成"), () => Finished?.Invoke(true), accent: true)));
        bar.Padding = new Thickness(32, 10, 32, 10);
        Root.Children.Add(bar);
        var scroll = Ui.Scroll(new ReadingColumn(body, Ui.C(720)), 32);
        Grid.SetRow(scroll, 1);
        Root.Children.Add(scroll);
        Rebuild();
    }

    static MeetingNotes Copy(MeetingNotes n) => n with
    {
        Summary = [.. n.Summary], Decisions = [.. n.Decisions], ActionItems = [.. n.ActionItems], OpenQuestions = [.. n.OpenQuestions],
        Topics = n.Topics.Select(t => t with { Points = [.. t.Points] }).ToList(), Custom = n.Custom.Select(c => c with { Items = [.. c.Items] }).ToList(),
        ReviewItems = [.. n.ReviewItems], People = n.People.Select(p => p with { Points = [.. p.Points] }).ToList(),
    };

    void Rebuild()
    {
        body.Children.Clear();
        var titleBox = new TextBox { Text = notes.Title, PlaceholderText = L.T("標題"), FontFamily = Ui.TitleFont(language), FontSize = Ui.C(18) };
        titleBox.TextChanged += (_, _) => notes = notes with { Title = titleBox.Text };
        body.Children.Add(titleBox);
        body.Children.Add(Section(L.T("摘要"), ItemsEditor(notes.Summary)));
        body.Children.Add(Section(L.T("決議"), ItemsEditor(notes.Decisions)));
        body.Children.Add(Section(L.T("待辦"), ActionsEditor(notes.ActionItems)));
        body.Children.Add(Section(L.T("未決問題"), ItemsEditor(notes.OpenQuestions)));
        body.Children.Add(Section(L.T("議題"), TopicsEditor()));
        foreach (var section in notes.Custom) body.Children.Add(Section(section.Title, ItemsEditor(section.Items)));
        // 每個人的重點（「人物」顯示的那一段）：有這一段時才出現，放在自訂段落後面（原本的編輯器不變）
        if (showsPeople) body.Children.Add(Section(L.T("每個人的重點"), PeopleEditor()));
        if (notes.ReviewItems.Count > 0)
        {
            var list = Ui.Column(6);
            for (var i = 0; i < notes.ReviewItems.Count; i++)
            {
                var index = i;
                var item = notes.ReviewItems[i];
                list.Children.Add(Ui.Bar(Ui.Text(item.Time, Ui.C(13), color: Ui.Secondary, wrap: false), Ui.Text(item.Question, Ui.C(14)),
                    Ui.IconButton(Symbol.Delete, L.T("刪除這一題"), () => { notes.ReviewItems.RemoveAt(index); Rebuild(); })));
            }
            body.Children.Add(Section(L.T("待確認"), list));
        }
    }

    UIElement Section(string title, UIElement content) => Ui.Column(8, Ui.Heading(title, language, Ui.C(16)), content);

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
            var title = new TextBox { Text = topic.Title, PlaceholderText = L.T("議題"), FontFamily = Ui.TitleFont(language), FontSize = Ui.C(14) };
            title.TextChanged += (_, _) => notes.Topics[index] = notes.Topics[index] with { Title = title.Text };
            var delete = Ui.IconButton(Symbol.Delete, L.T("刪除這個議題"), () => { notes.Topics.RemoveAt(index); Rebuild(); });
            panel.Children.Add(Ui.Column(8, Ui.Bar(start, title, delete), ItemsEditor(notes.Topics[index].Points)));
        }
        panel.Children.Add(Ui.Link(L.T("新增議題"), () => { notes.Topics.Add(new MeetingNotes.Topic("", "", [])); Rebuild(); }));
        return panel;
    }

    /// <summary>每個人的重點（R92、design-windows §11.25）：同議題的編輯器，每個人有名字和幾條重點</summary>
    UIElement PeopleEditor()
    {
        var panel = Ui.Column(14);
        for (var i = 0; i < notes.People.Count; i++)
        {
            var index = i;
            var name = new TextBox { Text = notes.People[i].Name, PlaceholderText = L.T("名字"), FontFamily = Ui.TitleFont(language), FontSize = Ui.C(14) };
            name.TextChanged += (_, _) => notes.People[index] = notes.People[index] with { Name = name.Text };
            var delete = Ui.IconButton(Symbol.Delete, L.T("刪除這個人的重點"), () => { notes.People.RemoveAt(index); Rebuild(); });
            panel.Children.Add(Ui.Column(8, Ui.Bar(null, name, delete), ItemsEditor(notes.People[index].Points)));
        }
        panel.Children.Add(Ui.Link(L.T("新增一個人"), () => { notes.People.Add(new MeetingNotes.Person("", [])); Rebuild(); }));
        return panel;
    }

    UIElement ItemsEditor(List<MeetingNotes.Item> items)
    {
        var panel = Ui.Column(6);
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var text = new TextBox { Text = items[i].Text, PlaceholderText = L.T("內容"), TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, Language = Ui.ContentLanguage(language), FontSize = Ui.C(14) };
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
            var task = new TextBox { Text = items[i].Task, PlaceholderText = L.T("事項"), TextWrapping = TextWrapping.Wrap, Language = Ui.ContentLanguage(language), FontSize = Ui.C(14) };
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
            list.Children.Add(Ui.Column(6, Ui.Heading(L.T("問這場會議"), MeetingLanguage.Zh, Ui.C(18)),
                Ui.Text(L.T("例如：「誰負責寄信的部分？」「最後決定的上線日期是哪一天？」回答只根據這場會議的會議紀錄和逐字稿，每一點都附上時間，點了就能聽原話。"), Ui.C(14), color: Ui.Secondary)));
        foreach (var record in records) list.Children.Add(QuestionCard(record, null, time => { if (TimeFormat.Seconds(time) is double s) host.Player.Seek(s); }, null));
        if (askingMeeting == id)
            list.Children.Add(Ui.Row(10, new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                Ui.Text(L.F("正在用 %@ 回答…", host.Settings.Provider.ShortLabel()), color: Ui.Secondary), Ui.Link(L.T("取消"), () => asking?.Cancel())));
        var scroll = new ScrollViewer { Content = new ReadingColumn(list, Ui.C(760)) };
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
            menu.Items.Add(Ui.DangerItem(L.T("清除問答紀錄"), clear, icon: null));
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
        // 問答是會議內容，跟著文字大小（R59.2）
        var panel = Ui.Column(8, Ui.Text(record.Question, Ui.C(14), bold: true, select: true));
        if (AskPrompt.Answer.Decode(record.Answer) is { } answer)
        {
            panel.Children.Add(Ui.Text(answer.Text.Replace("**", ""), Ui.C(14), color: answer.Found ? null : Ui.Secondary, select: true));
            foreach (var c in answer.Citations)
            {
                if (c.Time.Length == 0)
                {
                    // 參考文件的出處（M§39.3）：沒有錄音的時間點，quote 開頭寫了文件名稱和頁數
                    var clip = new SymbolIcon(Symbol.Attach) { Foreground = Ui.Secondary };
                    ToolTipService.SetToolTip(clip, L.T("出自這場會議的參考文件"));
                    panel.Children.Add(Ui.Split(clip, Ui.Text(c.Quote, Ui.C(13), color: Ui.Secondary, maxLines: 2), 8));
                    continue;
                }
                var label = c.MeetingId is long mid && onMeetingCitation is not null ? $"{meetingTitle?.Invoke(mid) ?? L.T("會議")} {c.Time}" : c.Time;
                var link = Ui.Link(label, () => { if (c.MeetingId is long m && onMeetingCitation is not null) onMeetingCitation(m, c.Time); else onCitation(c.Time); });
                if (!Ui.HighContrast) link.Foreground = Ui.Celadon;   // 對比主題時照平台的超連結顏色
                link.FontSize = Ui.C(14);
                panel.Children.Add(Ui.Split(link, Ui.Text(c.Quote, Ui.C(13), color: Ui.Secondary, maxLines: 2), 8));
            }
            if (answer.MeetingIds is { } ids && record.MeetingId is null)
                panel.Children.Add(Ui.Text(ids.Count == 0 ? L.T("沒有找到相關的會議") : L.F("參考了 %lld 場會議", ids.Count), Ui.C(12), color: Ui.Tertiary));
        }
        return Ui.Tinted(new Border { Child = panel, Padding = new Thickness(14), CornerRadius = new CornerRadius(10) }, Ui.Subtle);
    }
}
