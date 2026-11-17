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
    readonly TextBox search = new() { PlaceholderText = L.T("搜尋逐字稿"), MinWidth = 120 };
    readonly TextBlock matchLabel = Ui.Text("", 12, color: null, wrap: false);
    readonly ComboBox speakerFilter = new() { Width = 160 };
    /// <summary>原文｜精簡（R96、M§70、design-windows §11.29）：存在設定的 transcriptView</summary>
    readonly SelectorBar viewBar = new();
    readonly StackPanel bars = new();
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
    // 搜尋框最寬 300、視窗窄時跟著縮，後面的篩選和「用詞修正…」照內容寬，不會被擠出畫面（2026-10-22 介面巡檢，760 寬的視窗）
    readonly Grid searchRow = new() { ColumnSpacing = 10 };
    /// <summary>頂部的「我的筆記」（R44.3）：DetailView 每次 Show 給一份新的</summary>
    readonly Border top = new();
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
    /// <summary>還沒有精簡版時那一塊的樣子（產生中、進度）：一樣時不重建（每秒看一次）</summary>
    string? cleanKey;

    static readonly Lazy<DataTemplate> RowTemplate = new(() => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><ContentControl IsTabStop=\"False\" HorizontalContentAlignment=\"Stretch\" HorizontalAlignment=\"Stretch\"/></DataTemplate>"));

    public TranscriptTab(AppHost host, MainWindow window)
    {
        this.host = host;
        this.window = window;
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        searchRow.Padding = new Thickness(20, 10, 20, 10);
        // 篩選說話者的下拉選單沒有標題，螢幕閱讀器只會讀「下拉式方塊」
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(speakerFilter, L.T("說話者"));
        // 原文｜精簡（R96）：同紀錄分頁的「條列｜心智圖｜人物」用平台的 SelectorBar
        foreach (var (code, label) in new[] { ("original", L.T("原文")), ("clean", L.T("精簡")) })
            viewBar.Items.Add(new SelectorBarItem { Text = label, Tag = code });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(viewBar, L.T("逐字稿"));
        ToolTipService.SetToolTip(viewBar, L.T("精簡：用整理工具去掉贅字、重複和改口的版本；播放、搜尋、匯出照舊用原文"));
        viewBar.SelectionChanged += (_, _) =>
        {
            if (updating || viewBar.SelectedItem?.Tag is not string code || code == host.Settings.TranscriptView) return;
            host.Settings.Set(AppSettings.TranscriptViewKey, code);
            if (detail is not null) Rebind();
        };
        root.Children.Add(top);
        Grid.SetRow(searchRow, 1);
        root.Children.Add(searchRow);
        Grid.SetRow(bars, 2);
        root.Children.Add(bars);
        Grid.SetRow(list, 3);
        root.Children.Add(list);
        list.Padding = new Thickness(10, 4, 10, 14);
        // 每一列放在範本根元素（ContentControl）裡：直接換掉 ListViewItem 的 Content 會被 WinUI 用預設的樣子蓋掉
        //（只看到空白的列，2026-10-21 在虛擬機看到）；範本用 XamlReader 在執行時建，不用 XAML 編譯器（D59）
        list.ItemTemplate = RowTemplate.Value;
        list.ContainerContentChanging += (_, e) =>
        {
            if (e.ItemContainer.ContentTemplateRoot is not ContentControl slot) return;
            if (e.InRecycleQueue) slot.Content = null;
            else Bind(e.ItemContainer, slot, (Segment)e.Item);
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
        draftTimer.Tick += (_, _) => { draftTimer.Stop(); Ui.Guard(SaveDraft); };
        window.State.Changed += () => { };
    }

    public void FocusSearch() => search.Focus(FocusState.Programmatic);

    /// <summary>文字大小改了（R59）：看得到的那幾段重建；其他的捲到時才建，本來就會用新的大小</summary>
    public void Rescale()
    {
        if (detail is not null) RefreshRows();
    }

    public FrameworkElement Show(MeetingDetail value, bool changed, FrameworkElement? notes = null)
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
        top.Child = notes;
        BuildSearchRow();
        BuildBars();
        Rebind();
        if (changed) RestoreDraft();
        return root;
    }

    /// <summary>重新轉錄會用的模型（M§41.3）</summary>
    string UpgradeText(MeetingLanguage language) => ModelStore.Transcription(language, host.Settings.TranscriptionEngine) switch
    {
        ModelStore.Model.Japanese => L.F("這場會議是在模型下載好之前，用快速辨識轉錄的。可以用%@轉錄模型重新轉錄，通常比較準。", MeetingLanguages.UiNames("turbo")),
        ModelStore.Model.Qwen => L.T("這場會議是在模型下載好之前，用快速辨識轉錄的。可以用 Qwen3-ASR 重新轉錄，通常比較準。"),
        _ => L.T("這場會議是在模型下載好之前，用快速辨識轉錄的。可以用 Breeze 重新轉錄，通常比較準。"),
    };

    MeetingLanguage Language => detail?.Meeting.MeetingLanguage ?? MeetingLanguage.Zh;
    bool Busy => window.State.DetailStatus?.IsActive == true;

    void AddToSearchRow(FrameworkElement element, GridLength width, double maxWidth = double.PositiveInfinity)
    {
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = width, MaxWidth = maxWidth });
        Grid.SetColumn(element, searchRow.ColumnDefinitions.Count - 1);
        element.VerticalAlignment = VerticalAlignment.Center;
        searchRow.Children.Add(element);
    }

    void BuildSearchRow()
    {
        searchRow.Children.Clear();
        searchRow.ColumnDefinitions.Clear();
        AddToSearchRow(search, new GridLength(1, GridUnitType.Star), 300);
        AddToSearchRow(matchLabel, GridLength.Auto);
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
            AddToSearchRow(speakerFilter, GridLength.Auto);
        }
        updating = true;
        foreach (var item in viewBar.Items) if ((string)item.Tag == host.Settings.TranscriptView) viewBar.SelectedItem = item;
        updating = false;
        viewBar.IsEnabled = detail.Segments.Count > 0;
        AddToSearchRow(viewBar, GridLength.Auto);
        var replace = Ui.Button(L.T("用詞修正…"), () => _ = Dialogs.Replace(window, detail.Meeting.Id!.Value, search.Text),
            tooltip: L.T("把逐字稿和會議紀錄裡的同一個詞一起改掉"));
        // 轉錄或整理中不能改：工作完成時會整份寫回逐字稿或會議紀錄
        replace.IsEnabled = detail.Segments.Count > 0 && !Busy;
        AddToSearchRow(replace, GridLength.Auto);
    }

    void BuildBars()
    {
        bars.Children.Clear();
        var d = detail!;
        var warm = Ui.Tint(Ui.SignalColor, 20 / 255.0);
        // 對比主題時沒有底色、一圈細框（Ui.Tinted）
        Border Bar(FrameworkElement content) => Ui.Tinted(new Border { Child = content, Padding = new Thickness(20, 8, 20, 8) }, warm);
        var id = d.Meeting.Id!.Value;
        // 信心低的圖例自己一行（放在搜尋列裡，視窗窄時會把「用詞修正…」擠出畫面，2026-10-21 在虛擬機看到）
        if (d.Segments.Any(s => s.LowConfidence))
            bars.Children.Add(new Border
            {
                Padding = new Thickness(22, 0, 20, 6),
                Child = Ui.Row(5, new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 3, Height = 12, Fill = Ui.Signal },
                    Ui.Text(L.T("琥珀色是辨識信心低的段落"), 12, color: Ui.Secondary, wrap: false)),
            });
        // 模型下載好之前用快速辨識轉錄的會議（M§21.17）
        if (host.Controller.CanUpgradeTranscript(d.Meeting) && !Busy)
            bars.Children.Add(Bar(Ui.Bar(null, Ui.Text(UpgradeText(d.Meeting.MeetingLanguage), 13),
                Ui.Button(L.T("重新轉錄"), () => host.Controller.Retranscribe(id), accent: true, tooltip: L.T("逐字稿裡改過的文字會換成新的逐字稿，說話者的名字會留著")))));
        // 說話者可能分得不夠（M§24.5）
        if (d.ShowsSpeakerHint && !Busy)
            bars.Children.Add(Bar(Ui.Bar(null, Ui.Text(L.T("說話者可能分得不夠：有些不同的人可能被當成同一位。人多、吵雜或手機放在桌上錄音時常發生，告訴拾音大約幾個人會分得比較準。"), 13),
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
        // 精簡：整句都是贅字的那句不顯示（R96.1）
        var segments = CleanShown ? d.Segments.Where(s => s.Id is not long id || !d.CleanLines.TryGetValue(id, out var t) || t.Length > 0).ToList() : d.Segments;
        visible = filter is int f ? segments.Where(s => s.Speaker == f).ToList() : segments;
        UpdateMatches();
        cleanKey = null;
        if (d.Segments.Count == 0)
        {
            list.ItemsSource = null;
            list.Header = new Border { Padding = new Thickness(22), Child = Ui.Text(Busy ? L.T("轉錄完成後，逐字稿會出現在這裡。") : L.T("沒有逐字稿。"), color: Ui.Secondary) };
            return;
        }
        // 還沒有精簡版（R96.2）：說明和「產生精簡版」；產生中顯示進度；上次失敗寫原因
        if (Clean && d.CleanLines.Count == 0)
        {
            list.ItemsSource = null;
            list.Header = CleanPlaceholder();
            return;
        }
        list.Header = null;
        list.ItemsSource = visible;
    }

    bool Clean => host.Settings.TranscriptView == "clean";
    /// <summary>精簡而且這場已經有精簡版</summary>
    bool CleanShown => Clean && detail is { CleanLines.Count: > 0 };

    /// <summary>畫面上這一句的文字：精簡時是精簡版（改過、拆開的句子沒有精簡版，照原文）</summary>
    string DisplayText(Segment segment) =>
        CleanShown && segment.Id is long id && detail!.CleanLines.TryGetValue(id, out var clean) ? clean : segment.Text;

    (bool Active, double? Progress) CleaningState() => host.Controller.CleaningProgress(detail!.Meeting.Id!.Value);

    static string CleanStateKey((bool Active, double? Progress) state) =>
        state.Active ? $"active|{(state.Progress is double p ? (int)(p * 100) : -1)}" : "idle";

    /// <summary>
    /// 還沒有精簡版時（同 Mac 的 cleanPlaceholder）：說明、上次失敗的原因、「產生精簡版」（失敗過是「再試一次」）；產生中是進度。
    /// 字跟著文字大小，按鈕不跟（R59.2）
    /// </summary>
    FrameworkElement CleanPlaceholder()
    {
        var d = detail!;
        var state = CleaningState();
        cleanKey = CleanStateKey(state);
        var column = new StackPanel { Spacing = 12, MaxWidth = Ui.C(760), HorizontalAlignment = HorizontalAlignment.Left };
        if (state.Active)
        {
            var progress = state.Progress is double p ? L.F("正在產生精簡版 %lld%%", (long)(p * 100)) : L.T("正在產生精簡版…");
            column.Children.Add(Ui.Row(10, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Text(progress, Ui.C(14))));
        }
        else
        {
            column.Children.Add(Ui.Text(L.T("精簡版用整理工具去掉「嗯、呃、那個」這類贅字、重複的字詞和說到一半改口的部分，不改意思、不摘要。每一句的時間和說話者不變。"),
                Ui.C(14), color: Ui.Secondary));
            if (d.CleanFailure is { } failure)
                column.Children.Add(Ui.Text(failure.Length == 0 ? L.T("上次產生精簡版失敗。") : L.F("上次產生精簡版失敗：%@", L.Ui(failure)), Ui.C(14), color: Ui.Signal, select: true));
            var label = d.CleanFailure is null ? L.T("產生精簡版") : L.T("再試一次");
            var button = new Button { Content = Ui.Row(8, new SymbolIcon(Symbol.Filter), Ui.Text(label, wrap: false)) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, L.T("會用整理工具的額度；長的會議要幾分鐘"));
            button.IsEnabled = !Busy;
            var id = d.Meeting.Id!.Value;
            button.Click += (_, _) => host.Controller.GenerateCleanTranscript(id);
            column.Children.Add(button);
        }
        return new Border { Padding = new Thickness(22), Child = column };
    }

    /// <summary>產生精簡版的進度（轉錄、整理中每秒呼叫一次）：還沒有精簡版、樣子變了才重建那一塊</summary>
    public void UpdateClean()
    {
        if (detail is null || !Clean || detail.CleanLines.Count > 0 || detail.Segments.Count == 0) return;
        if (CleanStateKey(CleaningState()) != cleanKey) list.Header = CleanPlaceholder();
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
            if (list.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: ContentControl { Content: not null } slot } container) Bind(container, slot, visible[i]);
    }

    /// <summary>一列的內容，加上螢幕閱讀器、UI Automation 讀的名稱（沒設時是資料的型別名稱「Shiyin.Model.Segment」）</summary>
    void Bind(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container, ContentControl slot, Segment segment)
    {
        slot.Content = Row(segment);
        var speaker = segment.Speaker is int idx ? detail?.SpeakerName(idx) : null;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(container,
            string.Join(" ", new[] { TimeFormat.String(segment.Start), speaker, DisplayText(segment) }.Where(t => !string.IsNullOrEmpty(t))));
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
        timer.Tick += (_, _) => { timer.Stop(); if (highlightId == id) { highlightId = null; Ui.Guard(RefreshRows); } };
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
        // 時間、說話者的欄寬跟著文字大小（R59.2），放大時不會被擠掉
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Ui.C(58)) });
        // 說話者欄放頭像加名字（M§58.2），比原本寬一點（同 Mac 的 72 → 96）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(d.Speakers.Count > 0 || segment.Speaker is not null ? Ui.C(104) : 0) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (segment.Id == currentId) grid.Background = Ui.Tint(Ui.CeladonColor, 34 / 255.0);
        else if (segment.Id == highlightId) grid.Background = Ui.Tint(Ui.SignalColor, 46 / 255.0);
        if (segment.LowConfidence)
        {
            grid.BorderBrush = Ui.Signal;
            grid.BorderThickness = new Thickness(3, 0, 0, 0);
            ToolTipService.SetToolTip(grid, L.T("辨識信心低，建議對照錄音確認"));
        }
        // 對比主題：目前播放、跳過來的段落沒有底色（Ui.Tint 透明），改成一圈框（目前播放的是反白的顏色）；信心低的左邊照樣粗一點
        if (Ui.HighContrast && (segment.Id == currentId || segment.Id == highlightId))
        {
            grid.BorderBrush = segment.Id == currentId ? Ui.Celadon : Ui.Primary;
            grid.BorderThickness = new Thickness(segment.LowConfidence ? 3 : 2, 2, 2, 2);
        }
        var time = (FrameworkElement)Ui.Time(TimeFormat.String(segment.Start), host.Player.IsLoaded ? s => host.Player.Seek(s) : null, segment.Start, Ui.C(13));
        time.HorizontalAlignment = HorizontalAlignment.Right;
        // 時間、說話者對齊第一行（同 Mac 的 firstTextBaseline）：預設是上下置中，長的段落、有校正建議時會對不齊
        time.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(time);
        if (segment.Speaker is int idx)
        {
            // 頭像加名字（M§58.2、R83.2）：名字太長時切掉，頭像一直看得到
            var avatar = Ui.Avatar(d.SpeakerName(idx) ?? "", Ui.Speaker(idx), Ui.C(18), idx == d.SelfIdx);
            avatar.VerticalAlignment = VerticalAlignment.Center;
            var speaker = new HyperlinkButton
            {
                Content = Ui.FollowForeground(Ui.Split(avatar, Ui.Text(d.SpeakerName(idx) ?? "", Ui.C(13), bold: true, color: Ui.Speaker(idx), wrap: false), 6)), Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(speaker, d.SpeakerName(idx) ?? "");
            ToolTipService.SetToolTip(speaker, L.T("點一下幫這位說話者改名"));
            speaker.Click += (_, _) => RenameFlyout(speaker, idx).ShowAt(speaker);
            Grid.SetColumn(speaker, 1);
            grid.Children.Add(speaker);
        }
        var menu = Menu(segment);
        FrameworkElement body = segment.Id == editingId ? Editor(segment) : Text(segment, menu);
        Grid.SetColumn(body, 2);
        grid.Children.Add(body);
        grid.ContextFlyout = menu;
        return grid;
    }

    FrameworkElement Text(Segment segment, MenuFlyout menu)
    {
        // 精簡（R96）：顯示精簡版的文字，不標校正建議、搜尋（校正、搜尋對的是原文）；改過的句子沒有精簡版，照原文
        string? clean = CleanShown && segment.Id is long cleanId && detail!.CleanLines.TryGetValue(cleanId, out var cleanText) ? cleanText : null;
        var corrections = clean is null && segment.Id is long id && detail!.CorrectionsBySegment.TryGetValue(id, out var list) ? list : [];
        var text = Ui.Text(clean ?? segment.Text, Ui.C(14), select: true);
        text.Language = Ui.ContentLanguage(Language);
        // 可以選取的文字有自己的右鍵選單（只有拷貝、全選），會蓋掉這一段的選單：文字上按右鍵也開這一段的（裡面有「拷貝」）
        text.ContextFlyout = menu;
        // 待確認的校正建議：原文加底色；搜尋到的字也加底色
        foreach (var c in corrections.Where(c => c.Status == "pending"))
        {
            var at = segment.Text.IndexOf(c.Original, StringComparison.Ordinal);
            if (at >= 0) text.TextHighlighters.Add(Highlighter(at, c.Original.Length, Microsoft.UI.ColorHelper.FromArgb(52, 0xB9, 0x7F, 0x06)));
        }
        var q = clean is null ? search.Text.Trim() : "";
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
        // 字的顏色要給：預設是黑色，深色模式下標起來的字幾乎看不見（2026-10-22 介面巡檢）
        var h = new TextHighlighter { Background = new SolidColorBrush(color), Foreground = Ui.Primary };
        if (Ui.HighContrast)
        {
            // 對比主題：反白的底、反白的字（系統色成對用）
            h.Background = Ui.SystemBrush(Windows.UI.ViewManagement.UIElementType.Highlight);
            h.Foreground = Ui.SystemBrush(Windows.UI.ViewManagement.UIElementType.HighlightText);
        }
        h.Ranges.Add(new TextRange(start, length));
        return h;
    }

    UIElement CorrectionLine(Correction c)
    {
        var meeting = detail!.Meeting.Id!.Value;
        if (c.Status == "accepted")
            return Ui.Row(8, Ui.Text(L.F("已改成「%@」（原本是「%@」）", c.Suggestion, c.Original), Ui.C(12), color: Ui.Secondary),
                Ui.Link(L.T("還原"), () => { if (c.Id is long id) host.Controller.RevertCorrection(meeting, id); }));
        return Ui.Bar(Ui.Text(L.F("「%@」→「%@」", c.Original, c.Suggestion), Ui.C(12), bold: true, wrap: false), Ui.Text(c.Reason, Ui.C(12), color: Ui.Secondary, maxLines: 2),
            Ui.Row(8, Ui.Link(L.T("忽略"), () => { if (c.Id is long id) host.Controller.RejectCorrection(meeting, id); }),
                Ui.Link(L.T("接受"), () => { if (c.Id is long id) host.Controller.AcceptCorrection(meeting, id); })));
    }

    MenuFlyout Menu(Segment segment)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Ui.Item(L.T("從這裡播放"), () => host.Player.Seek(segment.Start), enabled: host.Player.IsLoaded));
        menu.Items.Add(Ui.Item(L.T("編輯這一段"), () => StartEdit(segment)));
        menu.Items.Add(Ui.Item(L.T("拷貝"), () => Copy.Text(segment.Text)));
        if (segment.Id is long id && segment.Kind == "final")
        {
            var d = detail!;
            var meeting = d.Meeting.Id!.Value;
            menu.Items.Add(new MenuFlyoutSeparator());
            // 改這句的說話者（M§24.3、R23.2）：有辨識說話者才有
            if (d.Speakers.Count > 0)
            {
                menu.Items.Add(Ui.Item(L.T("拆成兩句…"), () => _ = Dialogs.SplitSegment(window, d, segment)));   // R70
                menu.Items.Add(SpeakerChoices(L.T("這句是誰說的"), [id], segment.Speaker));
                var run = d.SpeakerRun(id);
                if (run.Count > 1) menu.Items.Add(SpeakerChoices(L.F("這段連續的話是誰說的（%lld 句）", run.Count), run, segment.Speaker));
            }
            // 分割會議（R90、M§64）：第一句之前沒有東西，不能從第一句分
            if (d.Segments.FirstOrDefault(s => s.Kind == "final")?.Id != id)
                menu.Items.Add(Ui.Item(L.T("從這裡分成兩場…"), () => _ = window.ConfirmSplitMeeting(meeting, segment)));
            // 刪除這句（R89、M§63）：不用確認，可以復原（D171）；沒辨識說話者的會議也有
            menu.Items.Add(Ui.DangerItem(L.T("刪除這句"), () => host.Controller.DeleteSegment(meeting, id)));
            // 復原最近一次說話者的調整、刪除（R44.4、R89）：有東西可復原才出現
            if (d.SpeakerUndoLabel is { } label)
                menu.Items.Add(Ui.Item(L.F("復原：%@", label), () => host.Controller.UndoSpeakerEdit(meeting), icon: Symbol.Undo));
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

    /// <summary>改名（同 Mac 的 RenameSpeakerForm）：聲紋自動帶入的多一個「不是他」；「合併到…」把這位說的話全部算到另一位；
    /// 有可以復原的調整時多一個「復原：…」（R44.4，原本在「更多」）</summary>
    Flyout RenameFlyout(FrameworkElement anchor, int idx)
    {
        var d = detail!;
        var meeting = d.Meeting.Id!.Value;
        var flyout = new Flyout();
        // 同彈窗的元件（R106.4、design-windows §11.39）：標題粗體、同一種框、圓角按鈕
        var name = Sheet.TextField(d.SpeakerName(idx) ?? "", L.T("例如：小明"));
        void Save()
        {
            flyout.Hide();
            host.Controller.RenameSpeaker(meeting, idx, name.Text);
        }
        name.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Save(); };
        var panel = Ui.Column(12, Ui.Text(L.T("說話者名字"), 18, bold: true), name);
        var automatic = d.SpeakerSources.GetValueOrDefault(idx) == "voiceprint";
        if (automatic) panel.Children.Add(Ui.Caption(L.T("這個名字是拾音認出聲音後自動帶入的。不對的話直接改名，或按「不是他」。")));
        panel.Children.Add(Ui.Caption(L.T("整場逐字稿和之後的會議紀錄都會用這個名字；拾音也會記住這個人的聲音，之後的會議自動帶入。")));
        var buttons = Ui.Row(8);
        if (automatic) buttons.Children.Add(Sheet.Pill(L.T("不是他"), click: () => { flyout.Hide(); host.Controller.RejectVoiceprintMatch(meeting, idx); }));
        var others = d.Speakers.Keys.Order().Where(k => k != idx).ToList();
        if (others.Count > 0)
        {
            var merge = Sheet.Pill(new DropDownButton { Content = L.T("合併到…") });
            var menu = new MenuFlyout();
            foreach (var other in others) menu.Items.Add(Ui.Item(d.SpeakerName(other) ?? "", () => { flyout.Hide(); host.Controller.MergeSpeaker(meeting, idx, other); }));
            merge.Flyout = menu;
            ToolTipService.SetToolTip(merge, L.T("同一個人被分成兩位時，把這位說的話全部算到另一位"));
            buttons.Children.Add(merge);
        }
        if (d.SpeakerUndoLabel is { } undo)
            buttons.Children.Add(Sheet.Pill(Ui.Button(L.F("復原：%@", undo), () => { flyout.Hide(); host.Controller.UndoSpeakerEdit(meeting); },
                tooltip: L.T("復原最近一次合併或改句子的說話者"))));
        panel.Children.Add(Ui.Bar(buttons, null, Sheet.Pill(L.T("儲存"), Sheet.Kind.Primary, click: Save)));
        panel.Width = 320;
        flyout.Content = panel;
        flyout.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        return flyout;
    }

    // MARK: 單段編輯（草稿存資料庫，M§21.15、R20.6）

    FrameworkElement Editor(Segment segment)
    {
        // 同彈窗的框（R106.4）：字照會議內容的文字大小，按鈕小一號
        var box = Sheet.Field(new TextBox { Text = editText, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, Language = Ui.ContentLanguage(Language) });
        box.FontSize = Ui.C(14);
        box.TextChanged += (_, _) => { editText = box.Text; draftTimer.Stop(); draftTimer.Start(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { SaveEdit(segment); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Escape) { CancelEdit(); e.Handled = true; }
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var buttons = Ui.Row(8, Sheet.Pill(L.T("取消"), compact: true, click: CancelEdit), Sheet.Pill(L.T("儲存"), Sheet.Kind.Primary, compact: true, click: () => SaveEdit(segment)));
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
