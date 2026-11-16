using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;

namespace Shiyin.App;

/// <summary>定期會議的確認，資料夾的自動加入條件（R98、R99、M§72、§73、design-windows §11.31、§11.32）</summary>
public static partial class Dialogs
{
    /// <summary>DEBUG 的 state：可能的定期會議的確認開著時，正在看的那一組（沒有其他組時是空字串）；沒開是 null</summary>
    public static string? SeriesSuggestionKey { get; private set; }

    /// <summary>DEBUG 的 state：條件的編輯開著時是哪個資料夾</summary>
    public static string? EditingFolderRule { get; private set; }

    /// <summary>
    /// 確認可能的定期會議（同 Mac 的 SeriesSuggestionSheet）：一次一組，同一個對話框換內容（同時只能開一個 ContentDialog）。
    /// 樣子照 Sheet（R106.1、design-windows §11.39）：標題、「第 1 組，共 2 組」、說明、名稱、與會者、包含哪幾場（同一種框裡的勾選清單）；
    /// 左下「不是定期會議」記進拒絕、換下一組，右下「略過」（Esc、✕）這次不處理、換下一組，「建立」（Enter，少於 2 場時停用）；
    /// 都處理完時說明，按鈕只剩「完成」，Esc、✕ 也是關掉（同 Mac 的 escCloses）
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
        var heading = L.T("可能的定期會議");
        var sheet = new SheetDialog(window, heading, Sheet.WideWidth);
        var name = Sheet.TextField("", L.T("名稱"));
        StackPanel? nameColumn = null;
        List<SeriesCandidate> Remaining() => state.SeriesCandidates.Where(c => !skipped.Contains(c.Key)).ToList();
        // 建立、不是定期會議、略過：處理這一組、換下一組
        void Next(Action<SeriesCandidate> act)
        {
            if (current is not { } candidate) return;
            handled++;
            act(candidate);
            Load();
        }
        void Create() => Next(c =>
        {
            host.Controller.CreateSeries(c, name.Text, c.MeetingIds.Where(chosen.Contains).ToList());
            state.Reload();
        });
        void Load()
        {
            var remaining = Remaining();
            current = remaining.FirstOrDefault();
            var form = sheet.Form;
            form.Children.Clear();
            if (current is null)
            {
                SeriesSuggestionKey = "";
                var done = Sheet.Pill(L.T("完成"), Sheet.Kind.Primary, click: sheet.Close);
                form.Children.Add(Sheet.Header(heading, sheet.Close,
                    Sheet.Subtitle(skipped.Count == 0 ? L.T("沒有其他可能的定期會議了。") : L.T("略過的那幾組下次打開還會在。"))));
                form.Children.Add(Sheet.Buttons(null, done));
                sheet.Escape = null;
                sheet.Submit = sheet.Close;
                sheet.Primary = done;
                if (form.IsLoaded) done.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(() => done.Focus(FocusState.Programmatic)));
                return;
            }
            SeriesSuggestionKey = current.Key;
            var total = handled + remaining.Count;
            var counter = total > 1 ? Ui.Text(L.F("第 %lld 組，共 %lld 組", handled + 1, total), 13, color: Ui.Secondary, wrap: false) : null;
            form.Children.Add(Sheet.Header(heading, sheet.Cancel,
                Sheet.Subtitle(L.F("這幾場會議是同一群人開的（%@）。確認之後，下次的會議會自動加進來，整理時會接上上次的待辦。", current.FrequencyText)), counter));
            name.Text = current.Name;
            // 同一個輸入框換到新的一組：先從上一組的那一欄拿下來（還掛在舊的欄裡，再放進去 WinUI 丟 0x800F1000，
            // 2026-11-17 虛擬機：略過第一組後第二組只剩標題、Esc 沒反應）。舊的欄已經不在畫面上，name.Parent 是 null，要自己記著
            nameColumn?.Children.Remove(name);
            nameColumn = Sheet.Labeled(L.T("名稱"), name);
            form.Children.Add(nameColumn);
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
                form.Children.Add(new ScrollViewer
                {
                    Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Disabled,
                });
            }
            var create = Sheet.Pill(L.T("建立"), Sheet.Kind.Primary, click: Create);
            // 每一場：日期、標題，預設全勾；至少 2 場才能建立
            chosen.Clear();
            var list = new StackPanel { Spacing = 0 };
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
                check.Checked += (_, _) => { chosen.Add(meetingId); create.IsEnabled = chosen.Count >= 2; };
                check.Unchecked += (_, _) => { chosen.Remove(meetingId); create.IsEnabled = chosen.Count >= 2; };
                list.Children.Add(check);
            }
            var scroll = new ScrollViewer { Content = list, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            form.Children.Add(Sheet.Labeled(L.T("包含哪幾場"), Sheet.Box(scroll)));
            create.IsEnabled = chosen.Count >= 2;
            var reject = Sheet.Pill(L.T("不是定期會議"), click: () => Next(c =>
            {
                host.Controller.RejectSeriesCandidate(c);
                state.Reload();
            }));
            ToolTipService.SetToolTip(reject, L.T("之後不再建議這一組"));
            var skip = Sheet.Pill(L.T("略過"), click: () => Next(c => skipped.Add(c.Key)));
            ToolTipService.SetToolTip(skip, L.T("這次先不處理，下次打開還會在"));
            form.Children.Add(Sheet.Buttons(reject, skip, create));
            sheet.Escape = () => Next(c => skipped.Add(c.Key));
            sheet.Submit = Create;
            sheet.Primary = create;
            // 換一組時焦點回到名稱（按過「略過」的焦點留在那顆按鈕上，那顆按鈕已經拿掉了）
            if (form.IsLoaded) name.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(() => name.Focus(FocusState.Programmatic)));
        }
        Load();
        sheet.Focus = current is null ? null : name;
        sheet.SelectOnFocus = false;
        try { await sheet.Show(); }
        finally { SeriesSuggestionKey = null; }
    }

    /// <summary>
    /// 資料夾右鍵「自動加入的條件…」（同 Mac 的 FolderRuleSheet）：標題包含（一行，用、，, 或換行分隔）、這些人都參加（同一種框裡的勾選清單，最高 140）、
    /// 用這個範本、屬於定期會議（有定期會議才出現；兩欄）；「現在不在資料夾裡的會議也套用（N 場）」跟著條件即時算，0 場時停用。
    /// 按鈕：左下「移除條件」（紅字，已經有條件時）、右下「取消」「儲存」（Enter；條件都空時當成移除）。樣子照 Sheet（R106.1）
    /// </summary>
    public static async Task FolderRule(MainWindow window, string folder)
    {
        if (window.XamlRoot is null) return;
        var host = AppHost.Current;
        var state = window.State;
        var existing = state.RuleFor(folder);
        var keywords = Sheet.TextField(string.Join(MeetingLanguages.Ui("、", ", "), existing?.Keywords ?? []), L.T("例如：週會、sync（任一個詞）"));
        AutomationProperties.SetName(keywords, L.T("標題包含"));
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
        var heading = L.F("「%@」自動加入的條件", folder);
        var sheet = new SheetDialog(window, heading, Sheet.WideWidth, 16);
        var form = sheet.Form;
        form.Children.Add(Sheet.Header(heading, sheet.Cancel, Sheet.Subtitle(L.T("符合任一個條件的新會議，會自動放進這個資料夾。已經在別的資料夾的會議不會移動。"))));
        keywords.TextChanged += (_, _) => Update();
        form.Children.Add(Sheet.Labeled(L.T("標題包含"), keywords));
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
            var scroll = new ScrollViewer { Content = list, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(scroll, L.T("這些人都參加"));
            form.Children.Add(Sheet.Labeled(L.T("這些人都參加"), Sheet.Box(scroll)));
        }
        var template = Sheet.Labeled(L.T("用這個範本"), Sheet.Combo(Ui.Combo(new[] { ((string?)null, L.T("不限")) }.Concat(state.Templates.Select(t => ((string?)t.Id, t.DisplayName()))),
            templateId, v => { templateId = v; Update(); })));
        form.Children.Add(state.Series.Count > 0
            ? Sheet.Pair(template, Sheet.Labeled(L.T("屬於定期會議"), Sheet.Combo(Ui.Combo(new[] { ((long?)null, L.T("不限")) }.Concat(state.Series.Select(s => (s.Id, s.Name))),
                seriesId, v => { seriesId = v; Update(); }))))
            : template);
        form.Children.Add(apply);
        Update();
        var result = 0;   // 1 儲存、2 移除條件
        var save = Sheet.Pill(L.T("儲存"), Sheet.Kind.Primary, click: () => { result = 1; sheet.Close(); });
        var remove = existing is null ? null : Sheet.DangerText(Sheet.Pill(L.T("移除條件"), click: () => { result = 2; sheet.Close(); }));
        form.Children.Add(Sheet.Buttons(remove, Sheet.Pill(L.T("取消"), click: sheet.Cancel), save));
        sheet.Submit = () => { result = 1; sheet.Close(); };
        sheet.Primary = save;
        sheet.Focus = keywords;
        sheet.SelectOnFocus = false;
        EditingFolderRule = folder;
        try { await sheet.Show(); }
        finally { EditingFolderRule = null; }
        if (result == 1) host.Controller.SaveFolderRule(Rule(), apply.IsChecked == true && matching > 0);
        else if (result == 2) host.Controller.SaveFolderRule(new Shiyin.Model.FolderRule { Folder = folder }, applyNow: false);
    }
}
