using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shiyin.Data;
using Shiyin.Localization;
using Shiyin.Model;
using Windows.System;

namespace Shiyin.App.Views;

/// <summary>
/// 人物頁（M§60.2、R85，design-windows §11.22；同 Mac 的 Views/PeopleViews.swift）：大頭像、名字、一起開過幾場，
/// 改名、合併到…、刪除聲音特徵；負責的待辦（點了打開那場、從那裡播放）；一起開過的會議（他說了多久）。
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
        var key = string.Join('\u001F', person, Ui.IsDark, Ui.ContentScale, string.Join(',', window.State.People.Select(p => p.Id + p.Name)),
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
        if (person.IsMe)
        {
            var me = Ui.Text(L.T("我"), 14, color: Ui.Secondary, wrap: false);
            me.VerticalAlignment = VerticalAlignment.Bottom;
            me.Margin = new Thickness(0, 0, 0, 5);
            title.Children.Add(me);
        }
        var info = Ui.Column(4, title, Ui.Text(SummaryLine(person, longForm: true), 14, color: Ui.Secondary));
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
    /// 「更多」（同會議頁的「更多」：DropDownButton＋MenuFlyout，無障礙名稱「更多」）：改名…（輸入框接在「更多」下面）、
    /// 合併到（子選單：其他認識的人）、刪除聲音特徵…（確認對話框）
    /// </summary>
    DropDownButton MoreMenu(PersonOverview person)
    {
        var menu = new MenuFlyout();
        var button = new DropDownButton { Content = Ui.Row(6, new SymbolIcon(Symbol.More), Ui.Text(L.T("更多"), wrap: false)), Flyout = menu };
        AutomationProperties.SetName(button, L.T("更多"));
        ToolTipService.SetToolTip(button, L.T("改名、跟另一個名字合併（同一個人被記成兩個名字時）、刪除聲音特徵"));
        var rename = RenameFlyout(person);
        menu.Items.Add(Ui.Item(L.T("改名…"), () => rename.ShowAt(button), icon: Symbol.Rename));
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
            menu.Items.Add(merge);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item(L.T("刪除聲音特徵…"), () => _ = ConfirmDelete(person), icon: Symbol.Delete));
        return button;
    }

    /// <summary>「改名…」（同設定頁）：接在「更多」下面；改成已經有的名字時合併過去，選那個人</summary>
    Flyout RenameFlyout(PersonOverview person)
    {
        var box = new TextBox { Text = person.Name, Header = L.T("名字"), Width = 260 };
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        void Save()
        {
            var text = box.Text.Trim();
            if (text.Length == 0) return;
            flyout.Hide();
            host.Controller.RenamePerson(person.Id, text);
            window.State.SelectPersonNamed(text);
        }
        var save = Ui.Button(L.T("儲存"), Save, accent: true);
        box.TextChanged += (_, _) => save.IsEnabled = box.Text.Trim().Length > 0;
        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            Save();
        };
        var actions = Ui.Row(8, Ui.Button(L.T("取消"), flyout.Hide), save);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        flyout.Content = Ui.Column(12, box, actions);
        flyout.Opening += (_, _) => box.Text = person.Name;
        // 打開時名字整個選好，直接打新的名字
        flyout.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        return flyout;
    }

    /// <summary>刪除聲音特徵（同設定頁）：確認對話框，主要按鈕是「刪除」、預設按鈕是取消，內文寫出是哪個人</summary>
    async Task ConfirmDelete(PersonOverview person)
    {
        if (!await Dialogs.Confirm(window, L.F("刪除「%@」的聲音特徵？", person.Name),
                L.F("拾音會忘記「%@」的聲音，之後的會議不會再自動帶入這個名字。已經帶入的名字會留著。", person.Name), L.T("刪除"), destructive: true)) return;
        host.Controller.DeletePerson(person.Id);
        window.State.SelectPerson(null);
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
        var button = new HyperlinkButton { Content = content, Padding = new Thickness(0), Foreground = Ui.Primary, VerticalAlignment = VerticalAlignment.Top };
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
                Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(6, 4, 6, 4),
            };
            ToolTipService.SetToolTip(button, L.T("打開這場會議"));
            AutomationProperties.SetName(button, string.Join(MeetingLanguages.Ui("，", ", "), entry.Meeting.MeetingDate, entry.Meeting.DisplayTitle, Spoke(entry.Seconds)));
            var id = entry.Id;
            button.Click += (_, _) => Open(id, null);
            list.Children.Add(button);
        }
        return list;
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
