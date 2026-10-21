using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;

namespace Shiyin.App.Views;

/// <summary>
/// 逐字稿分頁（同 Mac 的 TranscriptTab，R4）：時間點跳播、播放時高亮跟隨、搜尋、篩選說話者、單段編輯（草稿存資料庫）、
/// 用詞修正、校正建議、改說話者（改名、合併、這句是誰說的）。長的逐字稿用 ListView 的虛擬化，只建出看得到的那幾段（R20.3）
/// </summary>
public sealed class TranscriptTab
{
    readonly AppHost host;
    readonly MainWindow window;
    readonly Grid root = new();
    readonly TextBox search = new() { PlaceholderText = L.T("搜尋逐字稿"), Width = 300 };
    readonly TextBlock matchLabel = Ui.Text("", 12, color: null, wrap: false);
    readonly ComboBox speakerFilter = new() { Width = 160 };
    readonly StackPanel bars = new();
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
    readonly StackPanel searchRow = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    readonly DispatcherTimer draftTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    MeetingDetail? detail;
    List<Segment> visible = [];
    List<long> matches = [];
    int matchCursor;
    int? filter;
    long? editingId;
    string editText = "";
    long? currentId;
    long? highlightId;
    bool showGroups;
    bool updating;

    static readonly Lazy<DataTemplate> RowTemplate = new(() => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><ContentControl HorizontalContentAlignment=\"Stretch\" HorizontalAlignment=\"Stretch\"/></DataTemplate>"));

    public TranscriptTab(AppHost host, MainWindow window)
    {
        this.host = host;
        this.window = window;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        searchRow.Padding = new Thickness(20, 10, 20, 10);
        root.Children.Add(searchRow);
        Grid.SetRow(bars, 1);
        root.Children.Add(bars);
        Grid.SetRow(list, 2);
        root.Children.Add(list);
        list.Padding = new Thickness(10, 4, 10, 14);
        // 每一列放在範本根元素（ContentControl）裡：直接換掉 ListViewItem 的 Content 會被 WinUI 用預設的樣子蓋掉
        //（只看到空白的列，2026-10-21 在虛擬機看到）；範本用 XamlReader 在執行時建，不用 XAML 編譯器（D59）
        list.ItemTemplate = RowTemplate.Value;
        list.ContainerContentChanging += (_, e) =>
        {
            if (e.ItemContainer.ContentTemplateRoot is not ContentControl slot) return;
            slot.Content = e.InRecycleQueue ? null : Row((Segment)e.Item);
            e.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            e.Handled = true;
        };
        search.TextChanged += (_, _) => { matchCursor = 0; UpdateMatches(); RefreshRows(); ScrollToMatch(); };
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || matches.Count == 0) return;
            matchCursor = (matchCursor + 1) % matches.Count;
            UpdateMatchLabel();
            ScrollToMatch();
        };
        speakerFilter.SelectionChanged += (_, _) =>
        {
            if (updating || detail is null) return;
            var keys = detail.Speakers.Keys.Order().ToList();
            filter = speakerFilter.SelectedIndex <= 0 ? null : keys[speakerFilter.SelectedIndex - 1];
            Rebind();
        };
        draftTimer.Tick += (_, _) => { draftTimer.Stop(); SaveDraft(); };
        window.State.Changed += () => { };
    }

    public void FocusSearch() => search.Focus(FocusState.Programmatic);

    public FrameworkElement Show(MeetingDetail value, bool changed)
    {
        if (changed)
        {
            FlushDraft();
            editingId = null;
            filter = null;
            search.Text = "";
            currentId = null;
        }
        detail = value;
        BuildSearchRow();
        BuildBars();
        Rebind();
        if (changed) RestoreDraft();
        return root;
    }

    MeetingLanguage Language => detail?.Meeting.MeetingLanguage ?? MeetingLanguage.Zh;
    bool Busy => window.State.DetailStatus?.IsActive == true;

    void BuildSearchRow()
    {
        searchRow.Children.Clear();
        searchRow.Children.Add(search);
        searchRow.Children.Add(matchLabel);
        if (detail!.Speakers.Count > 0)
        {
            updating = true;
            speakerFilter.Items.Clear();
            speakerFilter.Items.Add(L.T("所有說話者"));
            var keys = detail.Speakers.Keys.Order().ToList();
            foreach (var idx in keys) speakerFilter.Items.Add(detail.SpeakerName(idx));
            // 合併、改說話者後篩選的那位不見了：回到所有說話者
            if (filter is int f && !keys.Contains(f)) filter = null;
            speakerFilter.SelectedIndex = filter is int s ? keys.IndexOf(s) + 1 : 0;
            ToolTipService.SetToolTip(speakerFilter, L.T("只看某一位說話者的內容"));
            updating = false;
            searchRow.Children.Add(speakerFilter);
        }
        if (detail.Segments.Any(s => s.LowConfidence))
            searchRow.Children.Add(Ui.Row(5, new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 3, Height = 12, Fill = Ui.Signal },
                Ui.Text(L.T("琥珀色是辨識信心低的段落"), 12, color: Ui.Secondary, wrap: false)));
        var replace = Ui.Button(L.T("用詞修正…"), () => _ = Dialogs.Replace(window, detail.Meeting.Id!.Value, search.Text),
            tooltip: L.T("把逐字稿和會議紀錄裡的同一個詞一起改掉"));
        // 轉錄或整理中不能改：工作完成時會整份寫回逐字稿或會議紀錄
        replace.IsEnabled = detail.Segments.Count > 0 && !Busy;
        searchRow.Children.Add(replace);
    }

    void BuildBars()
    {
        bars.Children.Clear();
        var d = detail!;
        var warm = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(20, 0xB9, 0x7F, 0x06));
        Border Bar(FrameworkElement content) => new() { Child = content, Padding = new Thickness(20, 8, 20, 8), Background = warm };
        var id = d.Meeting.Id!.Value;
        // 模型下載好之前用快速辨識轉錄的會議（M§21.17）
        if (host.Controller.CanUpgradeTranscript(d.Meeting) && !Busy)
            bars.Children.Add(Bar(Ui.Bar(Ui.Text(d.Meeting.MeetingLanguage == MeetingLanguage.Ja
                    ? L.T("這場會議是在模型下載好之前，用快速辨識轉錄的。可以用日文轉錄模型重新轉錄，通常比較準。")
                    : L.T("這場會議是在模型下載好之前，用快速辨識轉錄的。可以用 Breeze 重新轉錄，通常比較準。"), 13), null,
                Ui.Button(L.T("重新轉錄"), () => host.Controller.Retranscribe(id), accent: true, tooltip: L.T("逐字稿裡改過的文字會換成新的逐字稿，說話者的名字會留著")))));
        // 說話者可能分得不夠（M§24.5）
        if (d.ShowsSpeakerHint && !Busy)
            bars.Children.Add(Bar(Ui.Bar(Ui.Text(L.T("說話者可能分得不夠：有些不同的人可能被當成同一位。人多、吵雜或手機放在桌上錄音時常發生，告訴拾音大約幾個人會分得比較準。"), 13), null,
                Ui.Row(8, Ui.Button(L.T("不用了"), () => host.Controller.DismissSpeakerHint(id), tooltip: L.T("這場會議不再提示")),
                    Ui.Button(L.T("重新辨識說話者…"), () => { if (host.Controller.RequestRediarize(id)) _ = Dialogs.Rediarize(window, id); }, accent: true)))));
        var pending = d.Corrections.Count(c => c.Status == "pending");
        if (pending > 0)
        {
            var groups = d.CorrectionGroups;
            var toggle = Ui.Link(showGroups ? L.T("收起") : L.F("依詞處理（%lld 組）", groups.Count), () => { showGroups = !showGroups; BuildBars(); });
            ToolTipService.SetToolTip(toggle, L.T("同一個詞的每一處一起接受或忽略"));
            var head = Ui.Bar(Ui.Row(10, Ui.Text(L.F("%lld 處可能聽錯的字，已標在逐字稿裡", pending), 13), toggle), null,
                Ui.Row(8, Ui.Button(L.T("全部忽略"), () => host.Controller.RejectAllCorrections(id)),
                    Ui.Button(L.T("全部接受"), () => host.Controller.AcceptAllCorrections(id), accent: true, tooltip: L.T("把所有建議套用到逐字稿，之後還能逐條還原"))));
            var panel = Ui.Column(6, head);
            if (showGroups)
            {
                var rows = Ui.Column(6);
                foreach (var group in groups)
                {
                    var first = group[0];
                    var ids = group.Where(c => c.Id is not null).Select(c => c.Id!.Value).ToList();
                    var jump = Ui.Link(L.F("「%@」→「%@」", first.Original, first.Suggestion), () => Flash(first.SegmentId));
                    ToolTipService.SetToolTip(jump, L.T("跳到第一處"));
                    rows.Children.Add(Ui.Bar(Ui.Row(8, jump, Ui.Text(L.F("%lld 處", group.Count), 12, color: Ui.Secondary, wrap: false)),
                        Ui.Text(first.Reason, 12, color: Ui.Secondary, wrap: false),
                        Ui.Row(8, Ui.Link(L.T("忽略"), () => host.Controller.RejectCorrections(id, ids)),
                            Ui.Link(group.Count > 1 ? L.F("接受 %lld 處", group.Count) : L.T("接受"), () => host.Controller.AcceptCorrections(id, ids)))));
                }
                panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 220 });
            }
            bars.Children.Add(Bar(panel));
        }
    }

    void Rebind()
    {
        var d = detail!;
        visible = filter is int f ? d.Segments.Where(s => s.Speaker == f).ToList() : d.Segments;
        UpdateMatches();
        if (d.Segments.Count == 0)
        {
            list.ItemsSource = null;
            list.Header = new Border { Padding = new Thickness(22), Child = Ui.Text(Busy ? L.T("轉錄完成後，逐字稿會出現在這裡。") : L.T("沒有逐字稿。"), color: Ui.Secondary) };
            return;
        }
        list.Header = null;
        list.ItemsSource = visible;
    }

    void UpdateMatches()
    {
        var q = search.Text.Trim();
        matches = q.Length == 0 ? [] : visible.Where(s => s.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase) && s.Id is not null).Select(s => s.Id!.Value).ToList();
        UpdateMatchLabel();
    }

    void UpdateMatchLabel()
    {
        matchLabel.Foreground = Ui.Secondary;
        matchLabel.Text = search.Text.Trim().Length == 0 ? "" : matches.Count == 0 ? L.T("找不到") : L.F("%lld／%lld 段", Math.Min(matchCursor + 1, matches.Count), matches.Count);
    }

    void ScrollToMatch()
    {
        if (matchCursor < matches.Count) ScrollTo(matches[matchCursor]);
    }

    void ScrollTo(long id)
    {
        if (visible.FirstOrDefault(s => s.Id == id) is { } segment) list.ScrollIntoView(segment, ScrollIntoViewAlignment.Default);
    }

    /// <summary>重畫看得到的那幾段（搜尋、高亮換了）</summary>
    void RefreshRows()
    {
        for (var i = 0; i < list.Items.Count; i++)
            if (list.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: ContentControl { Content: not null } slot }) slot.Content = Row(visible[i]);
    }

    /// <summary>捲到這一段並暫時加底色；要跳到的段落被說話者篩選藏起來時，先取消篩選</summary>
    public void Flash(long id)
    {
        if (detail is null) return;
        if (!visible.Any(s => s.Id == id) && filter is not null)
        {
            filter = null;
            BuildSearchRow();
            Rebind();
        }
        highlightId = id;
        RefreshRows();
        ScrollTo(id);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); if (highlightId == id) { highlightId = null; RefreshRows(); } };
        timer.Start();
    }

    /// <summary>播放跟隨（M§21.12）：換段時才更新，播放中捲過去</summary>
    public void Follow(double time)
    {
        if (detail is null) return;
        var id = host.Player.IsPlaying || time > 0 ? detail.SegmentId(time) : null;
        if (id == currentId) return;
        currentId = id;
        RefreshRows();
        if (id is long s && host.Player.IsPlaying && editingId is null) ScrollTo(s);
    }

    // MARK: 一段

    FrameworkElement Row(Segment segment)
    {
        var d = detail!;
        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(d.Speakers.Count > 0 || segment.Speaker is not null ? 80 : 0) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (segment.Id == currentId) grid.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(34, 0x2D, 0x6A, 0x5D));
        else if (segment.Id == highlightId) grid.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(46, 0xB9, 0x7F, 0x06));
        if (segment.LowConfidence)
        {
            grid.BorderBrush = Ui.Signal;
            grid.BorderThickness = new Thickness(3, 0, 0, 0);
            ToolTipService.SetToolTip(grid, L.T("辨識信心低，建議對照錄音確認"));
        }
        var time = (FrameworkElement)Ui.Time(TimeFormat.String(segment.Start), host.Player.IsLoaded ? s => host.Player.Seek(s) : null);
        time.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(time);
        if (segment.Speaker is int idx)
        {
            var speaker = new HyperlinkButton
            {
                Content = Ui.Text(d.SpeakerName(idx) ?? "", 13, bold: true, color: Ui.Speaker(idx), wrap: false), Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(speaker, L.T("點一下幫這位說話者改名"));
            speaker.Click += (_, _) => RenameFlyout(speaker, idx).ShowAt(speaker);
            Grid.SetColumn(speaker, 1);
            grid.Children.Add(speaker);
        }
        FrameworkElement body = segment.Id == editingId ? Editor(segment) : Text(segment);
        Grid.SetColumn(body, 2);
        grid.Children.Add(body);
        grid.ContextFlyout = Menu(segment);
        return grid;
    }

    FrameworkElement Text(Segment segment)
    {
        var corrections = segment.Id is long id && detail!.CorrectionsBySegment.TryGetValue(id, out var list) ? list : [];
        var text = Ui.Text(segment.Text, 14, select: true);
        text.Language = Ui.ContentLanguage(Language);
        // 待確認的校正建議：原文加底色；搜尋到的字也加底色
        foreach (var c in corrections.Where(c => c.Status == "pending"))
        {
            var at = segment.Text.IndexOf(c.Original, StringComparison.Ordinal);
            if (at >= 0) text.TextHighlighters.Add(Highlighter(at, c.Original.Length, Microsoft.UI.ColorHelper.FromArgb(52, 0xB9, 0x7F, 0x06)));
        }
        var q = search.Text.Trim();
        if (q.Length > 0)
            for (var at = segment.Text.IndexOf(q, StringComparison.CurrentCultureIgnoreCase); at >= 0; at = segment.Text.IndexOf(q, at + q.Length, StringComparison.CurrentCultureIgnoreCase))
                text.TextHighlighters.Add(Highlighter(at, q.Length, Microsoft.UI.ColorHelper.FromArgb(90, 0xB9, 0x7F, 0x06)));
        text.DoubleTapped += (_, _) => StartEdit(segment);
        var panel = Ui.Column(4, text);
        foreach (var c in corrections) panel.Children.Add(CorrectionLine(c));
        return panel;
    }

    static TextHighlighter Highlighter(int start, int length, Windows.UI.Color color)
    {
        var h = new TextHighlighter { Background = new SolidColorBrush(color) };
        h.Ranges.Add(new TextRange(start, length));
        return h;
    }

    UIElement CorrectionLine(Correction c)
    {
        var meeting = detail!.Meeting.Id!.Value;
        if (c.Status == "accepted")
            return Ui.Row(8, Ui.Text(L.F("已改成「%@」（原本是「%@」）", c.Suggestion, c.Original), 12, color: Ui.Secondary),
                Ui.Link(L.T("還原"), () => { if (c.Id is long id) host.Controller.RevertCorrection(meeting, id); }));
        return Ui.Bar(Ui.Text(L.F("「%@」→「%@」", c.Original, c.Suggestion), 12, bold: true, wrap: false), Ui.Text(c.Reason, 12, color: Ui.Secondary, maxLines: 2),
            Ui.Row(8, Ui.Link(L.T("忽略"), () => { if (c.Id is long id) host.Controller.RejectCorrection(meeting, id); }),
                Ui.Link(L.T("接受"), () => { if (c.Id is long id) host.Controller.AcceptCorrection(meeting, id); })));
    }

    MenuFlyout Menu(Segment segment)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Ui.Item(L.T("從這裡播放"), () => host.Player.Seek(segment.Start), enabled: host.Player.IsLoaded));
        menu.Items.Add(Ui.Item(L.T("編輯這一段"), () => StartEdit(segment)));
        menu.Items.Add(Ui.Item(L.T("拷貝"), () => Copy.Text(segment.Text)));
        // 改這句的說話者（M§24.3、R23.2）
        if (segment.Id is long id && segment.Kind == "final" && detail!.Speakers.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(SpeakerChoices(L.T("這句是誰說的"), [id], segment.Speaker));
            var run = detail.SpeakerRun(id);
            if (run.Count > 1) menu.Items.Add(SpeakerChoices(L.F("這段連續的話是誰說的（%lld 句）", run.Count), run, segment.Speaker));
        }
        return menu;
    }

    MenuFlyoutSubItem SpeakerChoices(string title, List<long> ids, int? current)
    {
        var sub = new MenuFlyoutSubItem { Text = title };
        var meeting = detail!.Meeting.Id!.Value;
        foreach (var idx in detail.Speakers.Keys.Order().Where(k => k != current))
            sub.Items.Add(Ui.Item(detail.SpeakerName(idx) ?? "", () => host.Controller.ReassignSegments(meeting, ids, idx)));
        sub.Items.Add(new MenuFlyoutSeparator());
        sub.Items.Add(Ui.Item(L.T("新的說話者"), () => host.Controller.ReassignSegments(meeting, ids, null)));
        return sub;
    }

    /// <summary>改名（同 Mac 的 RenameSpeakerForm）：聲紋自動帶入的多一個「不是他」；「合併到…」把這位說的話全部算到另一位</summary>
    Flyout RenameFlyout(FrameworkElement anchor, int idx)
    {
        var d = detail!;
        var meeting = d.Meeting.Id!.Value;
        var flyout = new Flyout();
        var name = new TextBox { Text = d.SpeakerName(idx) ?? "", PlaceholderText = L.T("例如：小明") };
        void Save()
        {
            flyout.Hide();
            host.Controller.RenameSpeaker(meeting, idx, name.Text);
        }
        name.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Save(); };
        var panel = Ui.Column(10, Ui.Text(L.T("說話者名字"), 15, bold: true), name);
        var automatic = d.SpeakerSources.GetValueOrDefault(idx) == "voiceprint";
        if (automatic) panel.Children.Add(Ui.Caption(L.T("這個名字是拾音認出聲音後自動帶入的。不對的話直接改名，或按「不是他」。")));
        panel.Children.Add(Ui.Caption(L.T("整場逐字稿和之後的會議紀錄都會用這個名字；拾音也會記住這個人的聲音，之後的會議自動帶入。")));
        var buttons = Ui.Row(8);
        if (automatic) buttons.Children.Add(Ui.Button(L.T("不是他"), () => { flyout.Hide(); host.Controller.RejectVoiceprintMatch(meeting, idx); }));
        var others = d.Speakers.Keys.Order().Where(k => k != idx).ToList();
        if (others.Count > 0)
        {
            var merge = new DropDownButton { Content = L.T("合併到…") };
            var menu = new MenuFlyout();
            foreach (var other in others) menu.Items.Add(Ui.Item(d.SpeakerName(other) ?? "", () => { flyout.Hide(); host.Controller.MergeSpeaker(meeting, idx, other); }));
            merge.Flyout = menu;
            ToolTipService.SetToolTip(merge, L.T("同一個人被分成兩位時，把這位說的話全部算到另一位"));
            buttons.Children.Add(merge);
        }
        panel.Children.Add(Ui.Bar(buttons, null, Ui.Button(L.T("儲存"), Save, accent: true)));
        panel.Width = 300;
        flyout.Content = panel;
        flyout.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        return flyout;
    }

    // MARK: 單段編輯（草稿存資料庫，M§21.15、R20.6）

    FrameworkElement Editor(Segment segment)
    {
        var box = new TextBox { Text = editText, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, Language = Ui.ContentLanguage(Language) };
        box.TextChanged += (_, _) => { editText = box.Text; draftTimer.Stop(); draftTimer.Start(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { SaveEdit(segment); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Escape) { CancelEdit(); e.Handled = true; }
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var buttons = Ui.Row(8, Ui.Button(L.T("取消"), CancelEdit), Ui.Button(L.T("儲存"), () => SaveEdit(segment), accent: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        return Ui.Column(6, box, buttons);
    }

    void StartEdit(Segment segment)
    {
        if (Busy)
        {
            window.ShowNotice(L.T("正在處理這場會議，完成後才能編輯逐字稿"));
            return;
        }
        // 正在改別段的話，那一段的草稿不留（跟按取消一樣）
        if (editingId is not null && editingId != segment.Id) ClearDraft();
        editText = segment.Text;
        editingId = segment.Id;
        RefreshRows();
    }

    void SaveEdit(Segment segment)
    {
        var text = editText.Trim();
        ClearDraft();
        editingId = null;
        if (segment.Id is long id && text.Length > 0 && text != segment.Text) host.Controller.UpdateSegment(detail!.Meeting.Id!.Value, id, text);
        else RefreshRows();
    }

    void CancelEdit()
    {
        ClearDraft();
        editingId = null;
        RefreshRows();
    }

    void ClearDraft()
    {
        draftTimer.Stop();
        if (detail is not null) host.Controller.DeleteDrafts(detail.Meeting.Id!.Value, "segment:");
    }

    public void FlushDraft()
    {
        draftTimer.Stop();
        SaveDraft();
    }

    /// <summary>跟原本一樣（剛開始編輯、改回原樣）就不留草稿</summary>
    void SaveDraft()
    {
        if (editingId is not long id || detail is null) return;
        var original = detail.Segments.FirstOrDefault(s => s.Id == id)?.Text;
        host.Controller.SaveDraft(detail.Meeting.Id!.Value, $"segment:{id}", editText == original ? "" : editText);
    }

    /// <summary>上次沒存的編輯：回到編輯狀態，內容是草稿</summary>
    void RestoreDraft()
    {
        if (editingId is not null || detail is null) return;
        if (host.Controller.LatestDraft(detail.Meeting.Id!.Value, "segment:") is not var (key, text)) return;
        if (!long.TryParse(key["segment:".Length..], out var id) || !detail.Segments.Any(s => s.Id == id)) return;
        editText = text;
        editingId = id;
        Flash(id);
    }
}
