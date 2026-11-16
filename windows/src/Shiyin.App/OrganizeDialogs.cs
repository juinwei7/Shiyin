using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;

namespace Shiyin.App;

/// <summary>定期會議的確認、改名，資料夾的自動加入條件（R98、R99、M§72、§73、design-windows §11.31、§11.32）</summary>
public static partial class Dialogs
{
    /// <summary>DEBUG 的 state：可能的定期會議的確認開著時，正在看的那一組（沒有其他組時是空字串）；沒開是 null</summary>
    public static string? SeriesSuggestionKey { get; private set; }

    /// <summary>DEBUG 的 state：條件的編輯開著時是哪個資料夾</summary>
    public static string? EditingFolderRule { get; private set; }

    /// <summary>
    /// 確認可能的定期會議（同 Mac 的 SeriesSuggestionSheet）：一次一組，同一個對話框換內容（同時只能開一個 ContentDialog）。
    /// 「建立」是主要按鈕（預設），「不是定期會議」記進拒絕、換下一組，「略過」（關閉鈕，Esc）這次不處理、換下一組；
    /// 都處理完時說明，按鈕只剩「完成」
    /// </summary>
    public static async Task SeriesSuggestions(MainWindow window)
    {
        if (window.XamlRoot is null || SeriesSuggestionKey is not null) return;
        var host = AppHost.Current;
        var state = window.State;
        var skipped = new HashSet<string>();
        var handled = 0;
        SeriesCandidate? current = null;
        var chosen = new HashSet<long>();
        var name = new TextBox { Header = L.T("名稱") };
        AutomationProperties.SetName(name, L.T("名稱"));
        var counter = Ui.Text("", 13, color: Ui.Secondary, wrap: false);
        counter.VerticalAlignment = VerticalAlignment.Center;
        // ContentDialog 的標題是 SemiBold：不改的話中文會換成新細明體的假粗體（CLAUDE.md）
        counter.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        var body = new StackPanel { Spacing = 14, Width = 460 };
        var dialog = new ContentDialog
        {
            XamlRoot = window.XamlRoot, Title = Ui.Bar(Ui.DialogTitle(L.T("可能的定期會議")), null, counter), Content = body,
            DefaultButton = ContentDialogButton.Primary,
        };
        List<SeriesCandidate> Remaining() => state.SeriesCandidates.Where(c => !skipped.Contains(c.Key)).ToList();
        void Load()
        {
            var remaining = Remaining();
            current = remaining.FirstOrDefault();
            body.Children.Clear();
            if (current is null)
            {
                SeriesSuggestionKey = "";
                counter.Text = "";
                body.Children.Add(Ui.Text(skipped.Count == 0 ? L.T("沒有其他可能的定期會議了。") : L.T("略過的那幾組下次打開還會在。"), color: Ui.Secondary));
                dialog.PrimaryButtonText = L.T("完成");
                dialog.SecondaryButtonText = "";
                dialog.CloseButtonText = "";
                dialog.IsPrimaryButtonEnabled = true;
                return;
            }
            SeriesSuggestionKey = current.Key;
            var total = handled + remaining.Count;
            counter.Text = total > 1 ? L.F("第 %lld 組，共 %lld 組", handled + 1, total) : "";
            body.Children.Add(Ui.Text(L.F("這幾場會議是同一群人開的（%@）。確認之後，下次的會議會自動加進來，整理時會接上上次的待辦。", current.FrequencyText),
                13, color: Ui.Secondary));
            name.Text = current.Name;
            body.Children.Add(name);
            // 與會者：頭像加名字
            var people = state.People.Where(p => current.People.Contains(p.Id)).ToList();
            if (people.Count > 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                foreach (var person in people)
                {
                    var avatar = Ui.Avatar(person.Name, Ui.Person(person.Name), 20, person.IsMe);
                    avatar.VerticalAlignment = VerticalAlignment.Center;
                    var label = Ui.Text(person.Name, 13, wrap: false);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(Ui.Row(5, avatar, label));
                }
                body.Children.Add(new ScrollViewer
                {
                    Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Disabled,
                });
            }
            // 每一場：日期、標題，預設全勾；至少 2 場才能建立
            chosen.Clear();
            var list = new StackPanel { Spacing = 2 };
            foreach (var id in Enumerable.Reverse(current.MeetingIds))
            {
                if (state.Meetings.FirstOrDefault(m => m.Id == id) is not { } meeting) continue;
                chosen.Add(id);
                var date = Ui.Text(meeting.Meeting.MeetingDate, 13, color: Ui.Secondary, wrap: false);
                var title = Ui.Text(meeting.Meeting.DisplayTitle, 13, wrap: false);
                title.Language = Ui.ContentLanguage(meeting.Meeting.MeetingLanguage);
                var check = new CheckBox { Content = Ui.FollowForeground(Ui.Row(12, date, title)), IsChecked = true };   // 對比主題：字跟著勾選框的前景色
                AutomationProperties.SetName(check, $"{meeting.Meeting.MeetingDate} {meeting.Meeting.DisplayTitle}");
                var meetingId = id;
                check.Checked += (_, _) => { chosen.Add(meetingId); dialog.IsPrimaryButtonEnabled = chosen.Count >= 2; };
                check.Unchecked += (_, _) => { chosen.Remove(meetingId); dialog.IsPrimaryButtonEnabled = chosen.Count >= 2; };
                list.Children.Add(check);
            }
            body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            dialog.PrimaryButtonText = L.T("建立");
            dialog.SecondaryButtonText = L.T("不是定期會議");
            dialog.CloseButtonText = L.T("略過");
            dialog.IsPrimaryButtonEnabled = chosen.Count >= 2;
            // 換一組時焦點回到名稱（按過「略過」的焦點留在那顆按鈕上，「建立」就不是強調色了）
            if (name.IsLoaded) name.Focus(FocusState.Programmatic);
        }
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (current is not { } candidate) return;
            e.Cancel = true;
            var ids = candidate.MeetingIds.Where(chosen.Contains).ToList();
            handled++;
            host.Controller.CreateSeries(candidate, name.Text, ids);
            state.Reload();
            Load();
        };
        dialog.SecondaryButtonClick += (_, e) =>
        {
            if (current is not { } candidate) return;
            e.Cancel = true;
            handled++;
            host.Controller.RejectSeriesCandidate(candidate);
            state.Reload();
            Load();
        };
        dialog.CloseButtonClick += (_, e) =>
        {
            if (current is not { } candidate) return;
            e.Cancel = true;
            handled++;
            skipped.Add(candidate.Key);
            Load();
        };
        Load();
        try { await Show(dialog); }
        finally { SeriesSuggestionKey = null; }
    }

    /// <summary>重新命名定期會議（側欄的右鍵）：名稱選好，空的不存</summary>
    public static async Task RenameSeries(MainWindow window, long id)
    {
        if (window.State.SeriesById(id) is not { } series) return;
        var name = new TextBox { Text = series.Name };
        AutomationProperties.SetName(name, L.T("名稱"));
        name.Loaded += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        if (await Show(Dialog(window, L.T("重新命名定期會議"), name, L.T("重新命名"))) != ContentDialogResult.Primary) return;
        AppHost.Current.Controller.RenameSeries(id, name.Text);
    }

    /// <summary>DEBUG 的 state：重新命名中的標記</summary>
    public static long? RenamingBookmark { get; private set; }

    /// <summary>重新命名一個重點標記（R100、同 Mac 的 BookmarkRenameModifier）：說明「mm:ss 的標記」</summary>
    public static async Task RenameBookmark(MainWindow window, Bookmark bookmark)
    {
        if (RenamingBookmark is not null) return;
        var name = new TextBox { Text = bookmark.Label, Header = L.F("%@ 的標記", TimeFormat.String(bookmark.Time)) };
        AutomationProperties.SetName(name, L.T("名稱"));
        name.Loaded += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        RenamingBookmark = bookmark.Id;
        try
        {
            if (await Show(Dialog(window, L.T("重新命名標記"), name, L.T("重新命名"))) != ContentDialogResult.Primary) return;
            AppHost.Current.Controller.RenameBookmark(bookmark.MeetingId, bookmark.Id ?? 0, name.Text);
        }
        finally { RenamingBookmark = null; }
    }

    /// <summary>
    /// 資料夾右鍵「自動加入的條件…」（同 Mac 的 FolderRuleSheet）：標題包含（一行，用、，, 或換行分隔）、這些人都參加（勾選，最高 160）、
    /// 用這個範本、屬於定期會議（有定期會議才出現）；「現在不在資料夾裡的會議也套用（N 場）」跟著條件即時算，0 場時停用。
    /// 按鈕：儲存（條件都空時當成移除）、移除條件（已經有條件時）、取消
    /// </summary>
    public static async Task FolderRule(MainWindow window, string folder)
    {
        if (window.XamlRoot is null) return;
        var host = AppHost.Current;
        var state = window.State;
        var existing = state.RuleFor(folder);
        var keywords = new TextBox
        {
            Header = L.T("標題包含"), PlaceholderText = L.T("例如：週會、sync（任一個詞）"),
            Text = string.Join(MeetingLanguages.Ui("、", ", "), existing?.Keywords ?? []),
        };
        var people = new HashSet<long>(existing?.People ?? []);
        string? templateId = existing?.TemplateId;
        long? seriesId = existing?.SeriesId;
        var apply = new CheckBox();
        var matching = 0;
        Shiyin.Model.FolderRule Rule() => new()
        {
            Folder = folder, Keywords = GlossaryInput.Split(keywords.Text), People = [.. people.Order()], TemplateId = templateId, SeriesId = seriesId,
        };
        void Update()
        {
            var rule = Rule();
            matching = rule.IsEmpty ? 0 : host.Db.MeetingsMatching(rule).Count;
            apply.Content = L.F("現在不在資料夾裡的會議也套用（%lld 場）", matching);
            apply.IsEnabled = matching > 0;
        }
        var form = new StackPanel { Spacing = 14, Width = 460 };
        form.Children.Add(Ui.Text(L.T("符合任一個條件的新會議，會自動放進這個資料夾。已經在別的資料夾的會議不會移動。"), 13, color: Ui.Secondary));
        keywords.TextChanged += (_, _) => Update();
        form.Children.Add(keywords);
        if (state.People.Count > 0)
        {
            var list = new StackPanel { Spacing = 0 };
            foreach (var person in state.People)
            {
                var id = person.Id;
                var check = new CheckBox { Content = person.IsMe ? L.F("%@（我）", person.Name) : person.Name, IsChecked = people.Contains(id) };
                check.Checked += (_, _) => { people.Add(id); Update(); };
                check.Unchecked += (_, _) => { people.Remove(id); Update(); };
                list.Children.Add(check);
            }
            var scroll = new ScrollViewer { Content = list, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(scroll, L.T("這些人都參加"));
            form.Children.Add(Ui.Column(4, Ui.Text(L.T("這些人都參加"), 14), scroll));
        }
        form.Children.Add(Ui.Combo(new[] { ((string?)null, L.T("不限")) }.Concat(state.Templates.Select(t => ((string?)t.Id, t.DisplayName()))),
            templateId, v => { templateId = v; Update(); }, L.T("用這個範本")));
        if (state.Series.Count > 0)
            form.Children.Add(Ui.Combo(new[] { ((long?)null, L.T("不限")) }.Concat(state.Series.Select(s => (s.Id, s.Name))),
                seriesId, v => { seriesId = v; Update(); }, L.T("屬於定期會議")));
        form.Children.Add(apply);
        Update();
        var dialog = Dialog(window, L.F("「%@」自動加入的條件", folder), new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            L.T("儲存"));
        if (existing is not null) dialog.SecondaryButtonText = L.T("移除條件");
        EditingFolderRule = folder;
        ContentDialogResult result;
        try { result = await Show(dialog); }
        finally { EditingFolderRule = null; }
        if (result == ContentDialogResult.Primary) host.Controller.SaveFolderRule(Rule(), apply.IsChecked == true && matching > 0);
        else if (result == ContentDialogResult.Secondary) host.Controller.SaveFolderRule(new Shiyin.Model.FolderRule { Folder = folder }, applyNow: false);
    }
}
