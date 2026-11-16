using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shiyin.Data;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;

namespace Shiyin.App.Views;

/// <summary>
/// 人物頁（M§60.2、R85，design-windows §11.22；同 Mac 的 Views/PeopleViews.swift）：大頭像、名字、一起開過幾場，
/// 重新命名、合併到…、刪除聲音特徵；負責的待辦（點了打開那場、從那裡播放）；一起開過的會議（他說了多久）。
/// Windows 沒有「加到提醒事項」
/// </summary>
public sealed class PersonPage(AppHost host, MainWindow window)
{
    string? shownKey;
    FrameworkElement? shown;

    /// <summary>
    /// 右邊的人物頁；沒選人時是說明（同 Mac 的 PeopleEmptyView）。主視窗每次資料改了都會重畫：內容一樣時留著原本的
    ///（不然轉錄、整理的進度一直更新時，捲動位置會一直跳回最上面）
    /// </summary>
    public FrameworkElement Show(PersonOverview? person)
    {
        if (person is null)
        {
            shownKey = null;
            return Empty();
        }
        var meetings = host.Db.PersonMeetings(person.Id);
        var items = host.Db.PersonActionItems(person.Name);
        var key = string.Join('\u001F', person, Ui.ThemeKey, Ui.ContentScale, string.Join(',', window.State.People.Select(p => p.Id + p.Name)),
            string.Join(',', meetings.Select(m => $"{m.Id}|{m.Meeting.DisplayTitle}|{m.Meeting.MeetingDate}|{m.Seconds}")),
            string.Join(',', items.Select(i => $"{i.Meeting.Id}|{i.Meeting.DisplayTitle}|{i.Index}|{i.Item}")));
        if (key == shownKey && shown is not null) return shown;
        shownKey = key;
        return shown = Page(person, meetings, items);
    }

    FrameworkElement Empty()
    {
        var content = Ui.Column(10, new FontIcon { Glyph = "\uE716", FontSize = 34, Foreground = Ui.Tertiary },
            Ui.Text(window.State.People.Count == 0 ? L.T("還沒有認識的人") : L.T("選一個人，看一起開過的會議和負責的待辦"), color: Ui.Secondary));
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Children = { content } };
    }

    /// <summary>
    /// 上面是頁面的標題列（同會議頁：左邊頭像、名字、摘要，右上角「更多」，不跟著捲動），下面是負責的待辦和會議。
    /// 內容跟標題列同樣的左右邊距、整欄寬：負責的待辦那張表右邊跟「更多」對齊（M§60.2）
    /// </summary>
    FrameworkElement Page(PersonOverview person, List<PersonMeeting> meetings, List<PersonActionItem> items)
    {
        var column = new StackPanel { Spacing = 26 };
        column.Children.Add(Section(L.T("負責的待辦"), items.Count == 0
            ? Ui.Text(L.T("會議紀錄裡還沒有這個人負責的待辦。"), Ui.C(14), color: Ui.Secondary)
            : ItemsGrid(items)));
        // 英文是複數（「會議」這個鍵在別處是單數的 Meeting）
        column.Children.Add(Section(MeetingLanguages.Ui("會議", "Meetings"), MeetingList(meetings)));
        var scroll = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(32, 14, 32, 32), Child = column },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(Header(person));
        Grid.SetRow(scroll, 1);
        page.Children.Add(scroll);
        return page;
    }

    static FrameworkElement Section(string title, UIElement content) =>
        Ui.Column(10, Ui.Heading(title, MeetingLanguage.Zh, Ui.C(18)), content);

    /// <summary>頭像（我的話外圈細的青瓷綠）、名字（我的話旁邊灰字「我」）、摘要；改名、合併、刪除收在右上角的「更多」</summary>
    FrameworkElement Header(PersonOverview person)
    {
        var name = Ui.Heading(person.Name, MeetingLanguage.Zh, 26);
        name.IsTextSelectionEnabled = true;
        name.MaxLines = 1;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(name);
        // 名字旁的鉛筆（R85.6、M§60.2）：打開重新命名的對話框（R102，「更多」、側欄右鍵的「重新命名…」也是這一個）
        var pencil = Ui.Borderless(new Button { Content = new SymbolIcon(Symbol.Edit) });
        pencil.Padding = new Thickness(6);
        pencil.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(pencil, L.T("重新命名"));
        AutomationProperties.SetName(pencil, L.T("重新命名"));
        var personId = person.Id;
        pencil.Click += (_, _) => window.RenameUi(new RenameTarget.Person(personId));
        title.Children.Add(pencil);
        if (person.IsMe)
        {
            var me = Ui.Text(L.T("我"), 14, color: Ui.Secondary, wrap: false);
            me.VerticalAlignment = VerticalAlignment.Bottom;
            me.Margin = new Thickness(0, 0, 0, 5);
            title.Children.Add(me);
        }
        var info = Ui.Column(4, title, Ui.Text(SummaryLine(person, longForm: true), 14, color: Ui.Secondary), Actions(person));
        info.VerticalAlignment = VerticalAlignment.Center;
        var avatar = Ui.Avatar(person.Name, Ui.Person(person.Name), 52, person.IsMe);
        avatar.VerticalAlignment = VerticalAlignment.Center;
        var more = MoreMenu(person);
        more.VerticalAlignment = VerticalAlignment.Top;
        var header = Ui.Bar(avatar, info, more, 16);
        header.Margin = new Thickness(32, 20, 32, 10);
        return header;
    }

    /// <summary>
    /// 「更多」（同會議頁的「更多」：DropDownButton＋MenuFlyout，無障礙名稱「更多」）：項目跟側欄人物列的右鍵選單同一份（R85.7）
    /// </summary>
    DropDownButton MoreMenu(PersonOverview person)
    {
        var menu = new MenuFlyout();
        var button = new DropDownButton { Content = Ui.Row(6, new SymbolIcon(Symbol.More), Ui.Text(L.T("更多"), wrap: false)), Flyout = menu };
        AutomationProperties.SetName(button, L.T("更多"));
        ToolTipService.SetToolTip(button, L.T("重新命名、跟另一個名字合併（同一個人被記成兩個名字時）、移除這個人"));
        foreach (var item in MenuItems(person)) menu.Items.Add(item);
        // 打開時重建：人物可能改了（合併到的名單）
        menu.Opening += (_, _) => Ui.Guard(() =>
        {
            menu.Items.Clear();
            foreach (var item in MenuItems(person)) menu.Items.Add(item);
        });
        return button;
    }

    /// <summary>
    /// 一個人的動作（R85.7，同 Mac 的 PersonMenuItems）：人物頁的「更多」和側欄人物列的右鍵選單共用。
    /// 重新命名…（選那個人、打開重新命名的對話框，R102）、合併到（子選單：其他認識的人）、分隔線、移除這個人…（確認對話框）
    /// </summary>
    /// <param name="sidebar">側欄人物列的右鍵、「⋯」：右邊標 F2、Delete（R105，側欄的鍵盤；人物頁的「更多」焦點不在側欄，不標）</param>
    public List<MenuFlyoutItemBase> MenuItems(PersonOverview person, bool sidebar = false)
    {
        var rename = Ui.Item(L.T("重新命名…"), () => window.RenamePersonUi(person.Id), icon: Symbol.Edit);
        if (sidebar) MenuLook.Hint(rename, MenuLook.Rename);
        var items = new List<MenuFlyoutItemBase> { rename };
        var others = window.State.People.Where(p => p.Id != person.Id).ToList();
        if (others.Count > 0)
        {
            // 同一個人被記成兩個名字時，合併成一個（聲音特徵、會議裡的名字一起改成那個名字）
            var merge = new MenuFlyoutSubItem { Text = L.T("合併到"), Icon = new SymbolIcon(Symbol.People) };
            foreach (var other in others)
                merge.Items.Add(Ui.Item(other.Name, () =>
                {
                    host.Controller.RenamePerson(person.Id, other.Name);
                    window.State.SelectPerson(other.Id);
                }));
            items.Add(merge);
        }
        items.Add(new MenuFlyoutSeparator());
        var remove = Ui.DangerItem(L.T("移除這個人…"), () => _ = window.ConfirmRemovePerson(person.Id), icon: null, glyph: Ui.RemovePersonGlyph);
        if (sidebar) MenuLook.Hint(remove, MenuLook.Delete);
        items.Add(remove);
        return items;
    }

    /// <summary>
    /// 標題下面看得到的動作（R100、M§74）：「合併到 ▸」（沒有別人時不放）、「移除這個人…」（紅字）；重新命名是名字旁的鉛筆，右上角「更多」照舊
    /// </summary>
    FrameworkElement Actions(PersonOverview person)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(-6, 2, 0, 0) };
        var others = window.State.People.Where(p => p.Id != person.Id).ToList();
        if (others.Count > 0)
        {
            var menu = new MenuFlyout();
            foreach (var other in others)
                menu.Items.Add(Ui.Item(other.Name, () =>
                {
                    host.Controller.RenamePerson(person.Id, other.Name);
                    window.State.SelectPerson(other.Id);
                }));
            var merge = Ui.Borderless(new DropDownButton { Content = Ui.Row(6, new SymbolIcon(Symbol.People), Ui.Text(L.T("合併到"), 13, wrap: false)), Flyout = menu });
            ToolTipService.SetToolTip(merge, L.T("同一個人被記成兩個名字時，合併成一個"));
            AutomationProperties.SetName(merge, L.T("合併到"));
            row.Children.Add(merge);
        }
        var remove = Ui.Borderless(new Button { Content = Ui.Row(6, new SymbolIcon(Symbol.Delete), Ui.Text(L.T("移除這個人…"), 13, wrap: false)) }, Ui.Danger);
        AutomationProperties.SetName(remove, L.T("移除這個人…"));
        remove.Click += (_, _) => _ = window.ConfirmRemovePerson(person.Id);
        row.Children.Add(remove);
        return row;
    }

    /// <summary>負責的待辦：事項、期限、哪一場會議和時間點（點了打開那場、從那裡播放）</summary>
    FrameworkElement ItemsGrid(List<PersonActionItem> items)
    {
        var grid = new Grid { ColumnSpacing = 18, RowSpacing = 9 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = Ui.C(260) });
        var row = 0;
        void Add(FrameworkElement e, int column)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(e, row);
            Grid.SetColumn(e, column);
            grid.Children.Add(e);
        }
        Add(Ui.Text(L.T("事項"), Ui.C(12), color: Ui.Secondary), 0);
        Add(Ui.Text(L.T("期限"), Ui.C(12), color: Ui.Secondary), 1);
        Add(Ui.Text(L.T("會議"), Ui.C(12), color: Ui.Secondary), 2);
        row++;
        foreach (var entry in items)
        {
            var language = entry.Meeting.MeetingLanguage;
            var task = Ui.Text(entry.Item.Task, Ui.C(14), select: true);
            task.Language = Ui.ContentLanguage(language);
            var due = Ui.Text(entry.Item.Due.Length == 0 ? "—" : entry.Item.Due, Ui.C(14), color: entry.Item.Due.Length == 0 ? Ui.Secondary : null, wrap: false);
            Add(task, 0);
            Add(due, 1);
            Add(MeetingLink(entry), 2);
            row++;
        }
        return grid;
    }

    /// <summary>會議的標題和時間點：點了切回「會議」、選那場、從那裡播放（M§60.2）</summary>
    FrameworkElement MeetingLink(PersonActionItem entry)
    {
        var id = entry.Meeting.Id ?? 0;
        var title = Ui.Text(entry.Meeting.DisplayTitle, Ui.C(13), wrap: false);
        title.Language = Ui.ContentLanguage(entry.Meeting.MeetingLanguage);
        var content = new Grid { ColumnSpacing = 6 };
        // 標題放得下時照內容寬，放不下時切掉（時間點一直看得到）
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(title);
        if (entry.Item.Time.Length > 0)
        {
            var time = Ui.Text(entry.Item.Time, Ui.C(13), color: Ui.Celadon, wrap: false);
            Grid.SetColumn(time, 1);
            content.Children.Add(time);
        }
        // 對比主題：字跟著超連結的顏色（自己給的文字顏色、反白的顏色會讓它看起來不像連結，滑過去也不會反白）
        var button = new HyperlinkButton { Content = Ui.FollowForeground(content), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        if (!Ui.HighContrast) button.Foreground = Ui.Primary;
        ToolTipService.SetToolTip(button, L.T("打開這場會議，從這裡開始播放"));
        AutomationProperties.SetName(button, entry.Item.Time.Length > 0 ? $"{entry.Meeting.DisplayTitle} {entry.Item.Time}" : entry.Meeting.DisplayTitle);
        button.Click += (_, _) => Open(id, TimeFormat.Seconds(entry.Item.Time));
        return button;
    }

    /// <summary>一起開過的會議：日期、標題、他說了多久；點了打開那場</summary>
    FrameworkElement MeetingList(List<PersonMeeting> meetings)
    {
        var list = new StackPanel { Spacing = 4 };
        foreach (var entry in meetings)
        {
            var date = Ui.Text(entry.Meeting.MeetingDate, Ui.C(14), color: Ui.Secondary, wrap: false);
            var title = Ui.Text(entry.Meeting.DisplayTitle, Ui.C(14), wrap: false);
            title.Language = Ui.ContentLanguage(entry.Meeting.MeetingLanguage);
            var spoke = Ui.Text(Spoke(entry.Seconds), Ui.C(13), color: Ui.Secondary, wrap: false);
            var row = Ui.Bar(date, title, spoke, 14);
            var button = new Button
            {
                // 對比主題：字跟著按鈕的前景色（滑過去是反白的字，Ui.FollowForeground）
                Content = Ui.FollowForeground(row), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(6, 4, 6, 4),
            };
            ToolTipService.SetToolTip(button, L.T("打開這場會議"));
            AutomationProperties.SetName(button, string.Join(MeetingLanguages.Ui("，", ", "), entry.Meeting.MeetingDate, entry.Meeting.DisplayTitle, Spoke(entry.Seconds)));
            var id = entry.Id;
            button.Click += (_, _) => Open(id, null);
            list.Children.Add(MeetingLinkRow(button, menu => menu.Items.Add(Ui.Item(L.T("打開這場會議"), () => Open(id, null), glyph: Ui.OpenGlyph))));
        }
        return list;
    }

    /// <summary>
    /// 人物頁、系列頁裡的一場會議（R100、同 Mac 的 MeetingLinkMenuItems）：右鍵和滑過時右邊的「⋯」同一份選單（打開；系列頁多「移出」）
    /// </summary>
    public static FrameworkElement MeetingLinkRow(Button button, Action<MenuFlyout> fill)
    {
        var more = RowMore.Button(fill);
        more.HorizontalAlignment = HorizontalAlignment.Right;
        more.Margin = new Thickness(0, 0, -30, 0);
        var row = new Grid { Padding = new Thickness(0, 0, 30, 0) };
        row.Children.Add(button);
        row.Children.Add(more);
        button.ContextFlyout = RowMore.Context(fill);
        RowMore.Attach(row, more);
        return row;
    }

    /// <summary>從人物頁打開一場會議（切回「會議」）；有時間點時切到逐字稿、從那裡播放</summary>
    void Open(long meetingId, double? seconds)
    {
        if (seconds is double at && host.ShowMeetingAt is { } show) show(meetingId, at);
        else window.Reveal(meetingId);
    }

    /// <summary>「N 場會議・最近 &lt;日期&gt;」；人物頁上方用長的寫法</summary>
    public static string SummaryLine(PersonOverview person, bool longForm = false)
    {
        if (person.LastMeetingDate is not { } date)
            return person.IsMe ? L.T("錄過你的聲音，還沒有一起開過會議") : L.T("還沒有一起開過會議");
        return longForm ? L.F("一起開過 %lld 場會議，最近 %@", person.MeetingCount, date) : L.F("%lld 場會議・最近 %@", person.MeetingCount, date);
    }

    static string Spoke(double seconds)
    {
        var minutes = (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        return minutes < 1 ? L.T("說了不到 1 分鐘") : L.F("說了 %lld 分鐘", minutes);
    }
}
