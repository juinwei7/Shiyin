using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Shiyin.Data;
using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.App;

/// <summary>編輯會議的彈窗（R107、M§81.5、design-windows §11.40）：合併時選另一場</summary>
public static partial class Dialogs
{
    /// <summary>
    /// 合併另一場會議（M§81.5）：標題、搜尋、「前後三天」「更早或更晚」兩段（不列垃圾桶裡的、正在錄音或處理中的）；
    /// 選到會議語言不同的那場時說明照這場的語言整理。回傳選的會議（取消是 null）
    /// </summary>
    public static async Task<Meeting?> MergePicker(MainWindow window, long meetingId)
    {
        if (window.XamlRoot is null) return null;
        var host = window.Host;
        var (near, older) = host.Controller.MergeCandidates(meetingId);
        var own = host.Db.Meeting(meetingId);
        var sheet = new SheetDialog(window, L.T("合併另一場會議"), Sheet.WideWidth + 40, spacing: 14);
        var search = Sheet.TextField("", L.T("搜尋會議標題"));
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 300, Padding = new Thickness(0) };
        AutomationProperties.SetName(list, L.T("可以合併的會議"));
        Meeting? picked = null;
        var merge = Sheet.Pill(L.T("合併"), Sheet.Kind.Primary);
        merge.IsEnabled = false;
        var warnings = new Dictionary<long, (TextBlock Warning, FontIcon Check)>();

        void Fill()
        {
            list.Items.Clear();
            warnings.Clear();
            var query = search.Text.Trim();
            bool Match(MeetingRow r) => query.Length == 0 || r.Meeting.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            var (a, b) = (near.Where(Match).ToList(), older.Where(Match).ToList());
            if (a.Count == 0 && b.Count == 0)
                list.Items.Add(new ListViewItem
                {
                    Content = Ui.Text(query.Length == 0 ? L.T("沒有可以合併的會議") : L.F("找不到「%@」", query), color: Ui.Secondary), IsEnabled = false, IsTabStop = false,
                });
            void Section(string title, List<MeetingRow> rows)
            {
                if (rows.Count == 0) return;
                list.Items.Add(new ListViewItem { Content = Ui.Text(title, 13, color: Ui.Secondary), IsEnabled = false, IsTabStop = false, MinHeight = 28 });
                foreach (var row in rows) list.Items.Add(Item(row));
            }
            Section(L.T("前後三天"), a);
            Section(L.T("更早或更晚"), b);
        }

        ListViewItem Item(MeetingRow row)
        {
            var m = row.Meeting;
            var meta = new List<string> { m.MeetingDate, TimeFormat.String(m.DurationSeconds ?? 0) };
            if (m.Folder.Length > 0) meta.Add(m.Folder);
            var warning = Ui.Text(L.T("兩場的會議語言不同，合併後照這場的語言整理"), 13, color: Ui.Signal);
            warning.Visibility = Visibility.Collapsed;
            var check = new FontIcon { Glyph = "", FontSize = 14, Foreground = Ui.Celadon, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            var title = Ui.Text(m.DisplayTitle, 15, wrap: false);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            var body = Ui.Column(3, title, Ui.Text(string.Join("・", meta), 13, color: Ui.Secondary, wrap: false), warning);  // l10n-skip
            warnings[row.Id] = (warning, check);
            // 標題一欄（拿剩下的）、打勾照內容寬（Ui.Split 的兩欄都是比例寬，打勾會跑到中間）
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(check, 1);
            grid.Children.Add(body);
            grid.Children.Add(check);
            var item = new ListViewItem { Tag = row, Content = grid, Padding = new Thickness(12, 8, 12, 8) };
            item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(item, m.DisplayTitle);
            return item;
        }

        list.SelectionChanged += (_, _) => Ui.Guard(() =>
        {
            var row = (list.SelectedItem as ListViewItem)?.Tag as MeetingRow;
            picked = row?.Meeting;
            merge.IsEnabled = picked is not null;
            foreach (var (id, (warning, check)) in warnings)
            {
                var on = id == row?.Id;
                check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                warning.Visibility = on && own is not null && row!.Meeting.Language != own.Language ? Visibility.Visible : Visibility.Collapsed;
            }
        });
        search.TextChanged += (_, _) => Ui.Guard(() =>
        {
            picked = null;
            merge.IsEnabled = false;
            Fill();
        });
        Fill();
        var confirmed = false;
        void Accept()
        {
            if (picked is null) return;
            confirmed = true;
            sheet.Close();
        }
        merge.Click += (_, _) => Accept();
        list.DoubleTapped += (_, _) => Accept();
        sheet.Form.Children.Add(Sheet.Header(L.T("合併另一場會議"), sheet.Cancel, Sheet.Subtitle(L.T("照會議開始的先後接在一起。合併後另一場移到垃圾桶，30 天內可以還原。"))));
        sheet.Form.Children.Add(search);
        sheet.Form.Children.Add(list);
        sheet.Form.Children.Add(Sheet.Buttons(null, Sheet.Pill(L.T("取消"), click: sheet.Cancel), merge));
        sheet.Submit = Accept;
        sheet.Primary = merge;
        sheet.Focus = search;
        MergePickerOpen = true;
        try { await sheet.Show(); }
        finally { MergePickerOpen = false; }
        return confirmed ? picked : null;
    }

    /// <summary>DEBUG 的 state：合併時選會議的彈窗開著沒</summary>
    public static bool MergePickerOpen { get; private set; }

    /// <summary>「還原成編輯前的樣子…」的確認（R107.8）：說明原本多長、會不見什麼、分出去和合併進來的會議會怎樣</summary>
    public static async Task ConfirmRestoreEdit(MainWindow window, long meetingId)
    {
        var host = window.Host;
        if (!host.Controller.RequestRestoreEdit(meetingId)) return;
        RestoringEdit = meetingId;
        bool ok;
        try { ok = await Confirm(window, L.T("還原成編輯前的樣子？"), host.Controller.RestoreEditMessage(meetingId), L.T("還原")); }
        finally { RestoringEdit = null; }
        if (ok) await host.Controller.RestoreMeetingEdit(meetingId);
    }

    /// <summary>DEBUG 的 state（同 Mac 的 confirmRestoreEdit）：還原的確認開著時是哪一場</summary>
    public static long? RestoringEdit { get; private set; }
}
