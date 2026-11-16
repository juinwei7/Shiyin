using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.Data;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;
using Windows.System;

namespace Shiyin.App.Views;

/// <summary>
/// 系列頁（R99.4、M§73、design-windows §11.32；同 Mac 的 SeriesPage）：上面是不跟著捲動的標題列（同人物頁：圖示、名稱和鉛筆、
/// 「每週一 10:00・6 場・最近 2026-11-10」，右上角「更多」），下面是常參加的人、還沒完成的待辦、每一場會議。
/// Windows 沒有「加到提醒事項」（同人物頁）
/// </summary>
public sealed class SeriesPage(AppHost host, MainWindow window)
{
    string? shownKey;
    FrameworkElement? shown;

    /// <summary>主視窗每次資料改了都會重畫：內容一樣時留著原本的（轉錄的進度一直更新時捲動位置不會跳回最上面）</summary>
    public FrameworkElement Show(MeetingSeries series)
    {
        var id = series.Id ?? 0;
        var rows = window.State.Meetings.Where(m => m.Meeting.SeriesId == id).ToList();
        var items = SeriesOpenItems.Compute(host.Db.SeriesMeetings(id));
        var regulars = Organizing.Regulars(id, window.State.Meetings, window.State.MeetingPeople, window.State.People);
        var key = string.Join('\u001F', series.Id, series.Name, series.FrequencyText, Ui.IsDark, Ui.ContentScale,
            string.Join(',', rows.Select(r => $"{r.Id}|{r.Meeting.DisplayTitle}|{r.Meeting.MeetingDate}|{r.Meeting.DurationSeconds}")),
            string.Join(',', regulars.Select(p => $"{p.Id}|{p.Name}|{p.IsMe}")),
            string.Join(',', items.Select(i => $"{i.Meeting.Id}|{i.Meeting.DisplayTitle}|{i.Index}|{i.Item}")));
        if (key == shownKey && shown is not null) return shown;
        shownKey = key;
        return shown = Page(series, rows, items, regulars);
    }

    FrameworkElement Page(MeetingSeries series, List<MeetingRow> rows, List<PersonActionItem> items, List<PersonOverview> regulars)
    {
        var column = new StackPanel { Spacing = 26 };
        if (regulars.Count > 0) column.Children.Add(Section(L.T("常參加的人"), Regulars(regulars)));
        column.Children.Add(Section(L.T("還沒完成的待辦"), items.Count == 0
            ? Ui.Text(L.T("目前沒有還沒完成的待辦。之後每一場整理時，會對照上一場的待辦標出完成了沒。"), Ui.C(14), color: Ui.Secondary)
            : ItemsGrid(items)));
        // 英文是複數（「會議」這個鍵在別處是單數的 Meeting，同人物頁）
        column.Children.Add(Section(MeetingLanguages.Ui("會議", "Meetings"), MeetingList(rows)));
        var scroll = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(32, 14, 32, 32), Child = column },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(Header(series, rows));
        Grid.SetRow(scroll, 1);
        page.Children.Add(scroll);
        return page;
    }

    static FrameworkElement Section(string title, UIElement content) => Ui.Column(10, Ui.Heading(title, MeetingLanguage.Zh, Ui.C(18)), content);

    /// <summary>圖示（青瓷綠的圓底、循環的箭頭）、名稱（旁邊鉛筆改名）、頻率・場數・最近；改名、刪除收在右上角的「更多」</summary>
    FrameworkElement Header(MeetingSeries series, List<MeetingRow> rows)
    {
        var name = Ui.Heading(series.Name, MeetingLanguage.Zh, 26);
        name.IsTextSelectionEnabled = true;
        name.MaxLines = 1;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var rename = RenameFlyout(series);
        var pencil = Ui.Borderless(new Button { Content = new SymbolIcon(Symbol.Edit) });
        pencil.Padding = new Thickness(6);
        pencil.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(pencil, L.T("改名"));
        AutomationProperties.SetName(pencil, L.T("改名"));
        pencil.Click += (_, _) => rename.ShowAt(pencil, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
        var title = Ui.Row(8, name, pencil);
        var info = Ui.Column(4, title, Ui.Text(Organizing.SeriesSummaryLine(series, rows), 14, color: Ui.Secondary));
        info.VerticalAlignment = VerticalAlignment.Center;
        var c = Ui.CeladonColor;
        var icon = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(26), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(31, c.R, c.G, c.B)),
            Child = new SymbolIcon(Symbol.Sync) { Foreground = Ui.Celadon, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var more = MoreMenu(series, rename);
        more.VerticalAlignment = VerticalAlignment.Top;
        var header = Ui.Bar(icon, info, more, 16);
        header.Margin = new Thickness(32, 20, 32, 10);
        return header;
    }

    /// <summary>「更多」（同人物頁）：重新命名…（輸入框接在「更多」下面）、刪除定期會議…（確認對話框）</summary>
    DropDownButton MoreMenu(MeetingSeries series, Flyout rename)
    {
        var menu = new MenuFlyout();
        var button = new DropDownButton { Content = Ui.Row(6, new SymbolIcon(Symbol.More), Ui.Text(L.T("更多"), wrap: false)), Flyout = menu };
        AutomationProperties.SetName(button, L.T("更多"));
        ToolTipService.SetToolTip(button, L.T("重新命名、刪除定期會議"));
        menu.Items.Add(Ui.Item(L.T("重新命名…"), () => rename.ShowAt(button, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
        }), icon: Symbol.Rename));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item(L.T("刪除定期會議…"), () => _ = Dialogs.DeleteSeries(window, series.Id ?? 0), icon: Symbol.Delete));
        return button;
    }

    /// <summary>改名（同人物頁的改名）：名稱選好；Enter 存</summary>
    Flyout RenameFlyout(MeetingSeries series)
    {
        var box = new TextBox { Text = series.Name, Header = L.T("名稱"), Width = 260 };
        var flyout = new Flyout();
        void Save()
        {
            var text = box.Text.Trim();
            if (text.Length == 0) return;
            flyout.Hide();
            host.Controller.RenameSeries(series.Id ?? 0, text);
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
        flyout.Opening += (_, _) => box.Text = series.Name;
        flyout.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        return flyout;
    }

    /// <summary>常參加的人（過半數場次都有的人物）：頭像＋名字，放不下時換行；點了打開人物頁</summary>
    FrameworkElement Regulars(List<PersonOverview> people)
    {
        var flow = new FlowPanel { Spacing = 12, LineSpacing = 8 };
        foreach (var person in people)
        {
            var avatar = Ui.Avatar(person.Name, Ui.Person(person.Name), Ui.C(22), person.IsMe);
            avatar.VerticalAlignment = VerticalAlignment.Center;
            var label = Ui.Text(person.Name, Ui.C(14), wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            var button = new Button
            {
                Content = Ui.Row(6, avatar, label), Padding = new Thickness(4, 2, 6, 2),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(button, L.T("打開人物頁"));
            AutomationProperties.SetName(button, person.Name);
            var id = person.Id;
            button.Click += (_, _) => window.State.SelectPerson(id);
            flow.Children.Add(button);
        }
        return flow;
    }

    /// <summary>還沒完成的待辦（同人物頁的表，多一欄負責人）：事項、負責人、期限、哪一場會議和時間點（點了打開那場、從那裡播放）</summary>
    FrameworkElement ItemsGrid(List<PersonActionItem> items)
    {
        var grid = new Grid { ColumnSpacing = 18, RowSpacing = 9 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = Ui.C(220) });
        var row = 0;
        void Add(FrameworkElement e, int column)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(e, row);
            Grid.SetColumn(e, column);
            grid.Children.Add(e);
        }
        Add(Ui.Text(L.T("事項"), Ui.C(12), color: Ui.Secondary), 0);
        Add(Ui.Text(L.T("負責人"), Ui.C(12), color: Ui.Secondary), 1);
        Add(Ui.Text(L.T("期限"), Ui.C(12), color: Ui.Secondary), 2);
        Add(Ui.Text(L.T("會議"), Ui.C(12), color: Ui.Secondary), 3);
        row++;
        var divider = new Rectangle { Height = 1, Fill = Ui.Divider, HorizontalAlignment = HorizontalAlignment.Stretch };
        Add(divider, 0);
        Grid.SetColumnSpan(divider, 4);
        row++;
        foreach (var entry in items)
        {
            var language = entry.Meeting.MeetingLanguage;
            var task = Ui.Text(entry.Item.Task, Ui.C(14), select: true);
            task.Language = Ui.ContentLanguage(language);
            var owner = Ui.Text(entry.Item.Owner.Length == 0 ? "—" : entry.Item.Owner, Ui.C(14), color: entry.Item.Owner.Length == 0 ? Ui.Secondary : null, wrap: false);
            owner.Language = Ui.ContentLanguage(language);
            var due = Ui.Text(entry.Item.Due.Length == 0 ? "—" : entry.Item.Due, Ui.C(14), color: entry.Item.Due.Length == 0 ? Ui.Secondary : null, wrap: false);
            Add(task, 0);
            Add(owner, 1);
            Add(due, 2);
            Add(MeetingLink(entry), 3);
            row++;
        }
        return grid;
    }

    /// <summary>哪一場會議（日期）和時間點：點了打開那場、從那裡播放</summary>
    FrameworkElement MeetingLink(PersonActionItem entry)
    {
        var id = entry.Meeting.Id ?? 0;
        var content = Ui.Row(6, Ui.Text(entry.Meeting.MeetingDate, Ui.C(13), wrap: false),
            entry.Item.Time.Length > 0 ? Ui.Text(entry.Item.Time, Ui.C(13), color: Ui.Celadon, wrap: false) : null);
        var button = new HyperlinkButton { Content = content, Padding = new Thickness(0), Foreground = Ui.Primary, VerticalAlignment = VerticalAlignment.Top };
        ToolTipService.SetToolTip(button, L.T("打開這場會議，從這裡開始播放"));
        AutomationProperties.SetName(button, entry.Item.Time.Length > 0 ? $"{entry.Meeting.MeetingDate} {entry.Item.Time}" : entry.Meeting.MeetingDate);
        button.Click += (_, _) =>
        {
            if (TimeFormat.Seconds(entry.Item.Time) is double at && host.ShowMeetingAt is { } show) show(id, at);
            else window.State.Select(id);
        };
        return button;
    }

    /// <summary>每一場會議：日期、標題、長度；點了打開（留在這個定期會議的列表）</summary>
    FrameworkElement MeetingList(List<MeetingRow> rows)
    {
        var list = new StackPanel { Spacing = 4 };
        foreach (var row in rows)
        {
            var meeting = row.Meeting;
            var date = Ui.Text(meeting.MeetingDate, Ui.C(14), color: Ui.Secondary, wrap: false);
            var title = Ui.Text(meeting.DisplayTitle, Ui.C(14), wrap: false);
            title.Language = Ui.ContentLanguage(meeting.MeetingLanguage);
            var length = meeting.DurationSeconds is null ? null
                : Ui.Text(L.Ui(TimeFormat.Duration(meeting.DurationSeconds, MeetingLanguages.Interface)), Ui.C(13), color: Ui.Secondary, wrap: false);
            var button = new Button
            {
                Content = Ui.Bar(date, title, length, 14), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(6, 4, 6, 4),
            };
            ToolTipService.SetToolTip(button, L.T("打開這場會議"));
            AutomationProperties.SetName(button, string.Join(MeetingLanguages.Ui("，", ", "), meeting.MeetingDate, meeting.DisplayTitle));
            var id = row.Id;
            button.Click += (_, _) => window.State.Select(id);
            list.Children.Add(button);
        }
        return list;
    }
}

/// <summary>一排會換行的小元件（常參加的人，同 Mac 的 FlowLayout）</summary>
public sealed partial class FlowPanel : Panel
{
    public double Spacing { get; set; } = 8;
    public double LineSpacing { get; set; } = 8;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width) { x = 0; y += line + LineSpacing; line = 0; }
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, x - Spacing);
        }
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? widest : Math.Min(widest, available.Width), y + line);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > final.Width) { x = 0; y += line + LineSpacing; line = 0; }
            child.Arrange(new Windows.Foundation.Rect(x, y, size.Width, size.Height));
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
        }
        return final;
    }
}

/// <summary>「上次的待辦」的狀態（M§73）：完成（實心青瓷綠、打勾）、還在進行（琥珀、半實心）、這次沒提到（灰、減號）。用圖形畫，不靠字型的圖示</summary>
public static class FollowUpIcons
{
    public static Brush Color(FollowUpStatus status) => status switch
    {
        FollowUpStatus.Done => Ui.Celadon,
        FollowUpStatus.Ongoing => Ui.Signal,
        _ => Ui.Secondary,
    };

    public static string Label(FollowUpStatus status) => status switch
    {
        FollowUpStatus.Done => L.T("完成"),
        FollowUpStatus.Ongoing => L.T("還在進行"),
        _ => L.T("這次沒提到"),
    };

    public static FrameworkElement Icon(FollowUpStatus status, double size)
    {
        var color = Color(status);
        var grid = new Grid { Width = size, Height = size };
        var stroke = Math.Max(1.2, size / 11);
        switch (status)
        {
            case FollowUpStatus.Done:
                grid.Children.Add(new Ellipse { Fill = color });
                grid.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E", FontSize = size * 0.6, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
                break;
            case FollowUpStatus.Ongoing:
                grid.Children.Add(new Ellipse { Stroke = color, StrokeThickness = stroke });
                // 左半邊實心（同 Mac 的 circle.lefthalf.filled）
                var r = size / 2;
                var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(r, 0), IsClosed = true, IsFilled = true };
                figure.Segments.Add(new ArcSegment
                {
                    Point = new Windows.Foundation.Point(r, size), Size = new Windows.Foundation.Size(r, r), SweepDirection = SweepDirection.Counterclockwise,
                });
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = color });
                break;
            default:
                grid.Children.Add(new Ellipse { Stroke = color, StrokeThickness = stroke });
                grid.Children.Add(new Rectangle
                {
                    Fill = color, Height = stroke, Width = size * 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
                break;
        }
        ToolTipService.SetToolTip(grid, Label(status));
        AutomationProperties.SetName(grid, Label(status));
        return grid;
    }
}
