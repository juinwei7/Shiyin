using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;
using Shiyin.Services.Capture;

namespace Shiyin.App.Views;

/// <summary>
/// 會議內容（同 Mac 的 MeetingDetailView）：上方標題、資訊列、狀態或垃圾桶提示、草稿提示、猜到的名字；
/// 中間六個分頁（錄音中換成錄音的畫面）；下方播放列
/// </summary>
public sealed class DetailView
{
    readonly AppHost host;
    readonly MainWindow window;
    public Grid Root { get; } = new();
    readonly StackPanel top = new() { Spacing = 12, Padding = new Thickness(32, 20, 32, 10) };
    readonly SelectorBar tabs = new() { Margin = new Thickness(24, 0, 24, 0) };
    readonly Border content = new();
    readonly Border player = new();
    readonly Border status = new();
    MeetingDetail? detail;
    readonly TranscriptTab transcript;
    readonly RecordingPanel recording;
    readonly PlayerBar playerBar;
    readonly NotesTabs notes;
    readonly ReviewTab review;
    readonly AskTab ask;
    bool switchingTabs;

    public DetailView(AppHost host, MainWindow window)
    {
        this.host = host;
        this.window = window;
        transcript = new TranscriptTab(host, window);
        recording = new RecordingPanel(host, window);
        playerBar = new PlayerBar(host);
        notes = new NotesTabs(host, window);
        review = new ReviewTab(host, window);
        ask = new AskTab(host, window);
        playerBar.Tick += t => { if (window.State.Tab == DetailTab.Transcript) transcript.Follow(t); };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            Root.RowDefinitions.Add(new RowDefinition { Height = h });
        Add(top, 0);
        Add(tabs, 1);
        Add(content, 2);
        Add(player, 3);
        foreach (var tab in Enum.GetValues<DetailTab>()) tabs.Items.Add(new SelectorBarItem { Text = tab.Label(), Tag = tab });
        tabs.SelectionChanged += (_, _) =>
        {
            if (switchingTabs || tabs.SelectedItem?.Tag is not DetailTab tab) return;
            FlushDrafts();
            window.State.SetTab(tab);
        };
    }

    void Add(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        Root.Children.Add(element);
    }

    MeetingDetail Detail => detail!;
    bool IsRecordingThis => host.Recorder.IsRecording && host.Recorder.MeetingId == detail?.Meeting.Id;
    bool IsTrashed => detail?.Meeting.DeletedAt is not null;

    public void Clear()
    {
        FlushDrafts();
        detail = null;
    }

    /// <summary>換分頁、換會議、關視窗前：編輯中的東西存草稿（M§21.15、R20.6）</summary>
    public void FlushDrafts()
    {
        transcript.FlushDraft();
        review.FlushDraft();
        notes.FlushEditor();
        if (detail is not null) notes.SaveUserNotes();
    }

    public void Show(MeetingDetail value)
    {
        var changed = detail?.Meeting.Id != value.Meeting.Id;
        if (changed) FlushDrafts();
        detail = value;
        BuildTop();
        if (IsRecordingThis)
        {
            tabs.Visibility = Visibility.Collapsed;
            player.Child = null;
            recording.Show(value);
            content.Child = recording.Root;
            return;
        }
        tabs.Visibility = Visibility.Visible;
        switchingTabs = true;
        foreach (var item in tabs.Items)
        {
            var tab = (DetailTab)item.Tag;
            var count = value.Notes?.ReviewItems.Count ?? 0;
            item.Text = tab == DetailTab.Review && count > 0 ? $"{tab.Label()} {count}" : tab.Label();
            if (tab == window.State.Tab) tabs.SelectedItem = item;
        }
        switchingTabs = false;
        content.Child = window.State.Tab switch
        {
            DetailTab.Summary => notes.Summary(value),
            DetailTab.Topics => notes.Topics(value),
            DetailTab.Transcript => transcript.Show(value, changed),
            DetailTab.Review => review.Show(value, changed),
            DetailTab.Notes => notes.UserNotes(value),
            _ => ask.Show(value, changed),
        };
        player.Child = host.Paths.HasAudio(value.Meeting) ? playerBar.Show(value) : null;
    }

    public void UpdateRecording()
    {
        if (detail is null) return;
        if (IsRecordingThis) recording.Update();
    }

    /// <summary>
    /// 轉錄、整理中每秒呼叫一次。狀態列的樣子（狀態、階段、在跑的工作）沒變時只改文字和進度條：
    /// 整塊換掉時會閃，滑鼠停在「取消」上也會一直被重設
    /// </summary>
    public void UpdateStatus()
    {
        if (detail is null || IsTrashed) return;
        if (StatusKey() == statusKey)
        {
            if (statusLiveText is not null && host.Controller.Status(Detail) is MeetingStatus.Transcribing t)
            {
                var text = TranscribingText(t.Progress, t.Stage, host.Runner.RunningJob(Detail.Meeting.Id!.Value)?.Remaining);
                if (statusLiveText.Text != text) statusLiveText.Text = text;
                if (statusLiveProgress is not null && t.Progress is double p) statusLiveProgress.Value = p * 100;
            }
            return;
        }
        status.Child = StatusBanner();
    }

    string? statusKey;
    TextBlock? statusLiveText;
    ProgressBar? statusLiveProgress;

    /// <summary>狀態列的樣子：一樣時不重建（轉錄的百分比、剩幾分鐘不算）</summary>
    string StatusKey()
    {
        var id = Detail.Meeting.Id!.Value;
        var state = host.Controller.Status(Detail);
        var running = host.Runner.RunningJob(id);
        var helpers = string.Join(",", Enum.GetValues<JobLane>().Select(l => host.Runner.Running(l)).Where(h => h is not null && h.Job.MeetingId == id).Select(h => h!.Job.Kind));
        return state switch
        {
            MeetingStatus.Transcribing t => $"transcribing|{t.Stage}|{t.Progress is null}",
            MeetingStatus.Failed f => $"failed|{f.Kind}|{f.Error}",
            _ => $"{state.GetType().Name}|{running?.Job.Kind}|{running?.Provider}|{Detail.LatestJob?.Kind}",
        } + "|" + helpers;
    }

    public void FocusSearch() => transcript.FocusSearch();

    /// <summary>捲到這一段並暫時加底色（搜尋結果、出處、校正的「跳到第一處」）</summary>
    public void Flash(long segmentId, bool seek = false)
    {
        transcript.Flash(segmentId);
        if (seek && detail?.Segments.FirstOrDefault(s => s.Id == segmentId) is { } segment) host.Player.Seek(segment.Start, play: false);
    }

    // MARK: 上方

    void BuildTop()
    {
        top.Children.Clear();
        var meeting = Detail.Meeting;
        var title = Ui.Heading(meeting.DisplayTitle, meeting.MeetingLanguage, 26);
        title.IsTextSelectionEnabled = true;
        title.MaxLines = 2;
        var edit = Ui.Link(L.T("編輯"), () => _ = Dialogs.EditInfo(window, meeting));
        var more = IsTrashed || IsRecordingThis ? null : MoreMenu();
        top.Children.Add(Ui.Bar(null, Ui.Row(12, title, edit), more));
        top.Children.Add(InfoRow());
        if (IsTrashed) top.Children.Add(TrashBanner());
        else
        {
            status.Child = StatusBanner();
            top.Children.Add(status);
        }
        if (meeting.NotesDraft != 0 && Detail.Notes is not null && !IsRecordingThis && !IsTrashed)
            top.Children.Add(Banner(Ui.Text(L.T("草稿：用錄音中的即時逐字稿整理的，正式轉錄完成後會自動更新"), 13, color: Ui.Secondary), null, Ui.Subtle));
        if (Detail.SpeakerSuggestions.Count > 0 && !IsRecordingThis && !IsTrashed) top.Children.Add(SuggestionsBanner());
    }

    FrameworkElement InfoRow()
    {
        var meeting = Detail.Meeting;
        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        if (meeting.Folder.Length > 0) info.Children.Add(Ui.Row(4, new SymbolIcon(Symbol.Folder) { Foreground = Ui.Secondary }, Ui.Text(meeting.Folder, 13, color: Ui.Secondary, wrap: false)));
        info.Children.Add(Ui.Text($"{meeting.MeetingDate} {DateText.Weekday(meeting.MeetingDate, MeetingLanguages.Interface)}", 13, color: Ui.Secondary, wrap: false));
        if (meeting.DurationSeconds is not null) info.Children.Add(Ui.Text(TimeFormat.Duration(meeting.DurationSeconds, MeetingLanguages.Interface), 13, color: Ui.Secondary, wrap: false));
        if (Detail.Speakers.Count > 0)
        {
            var speakers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var idx in Detail.Speakers.Keys.Order())
            {
                var name = Ui.Text(Detail.Speakers[idx], 13, color: Ui.Speaker(idx), wrap: false);
                if (Detail.SpeakerSources.GetValueOrDefault(idx) == "voiceprint")
                {
                    // 聲紋自動帶入的名字（M§21.4）：點了可以說不是他
                    var button = new DropDownButton { Content = Ui.Row(4, name, Ui.Text(L.T("自動"), 10, color: Ui.Secondary)), Padding = new Thickness(4, 0, 4, 0) };
                    var menu = new MenuFlyout();
                    menu.Items.Add(Ui.Item(L.F("不是%@", Detail.Speakers[idx]), () => host.Controller.RejectVoiceprintMatch(meeting.Id!.Value, idx)));
                    menu.Items.Add(Ui.Item(L.T("到逐字稿改名"), () => window.State.SetTab(DetailTab.Transcript)));
                    button.Flyout = menu;
                    ToolTipService.SetToolTip(button, L.T("拾音認出這個人的聲音，自動帶入名字。不對的話點名字"));
                    speakers.Children.Add(button);
                }
                else speakers.Children.Add(name);
            }
            info.Children.Add(speakers);
        }
        else if (meeting.ParticipantList.Count > 0) info.Children.Add(Ui.Text(string.Join(L.T("、"), meeting.ParticipantList), 13, color: Ui.Secondary, wrap: false));

        var menus = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var templates = window.State.Templates;
        var template = new DropDownButton { Content = templates.FirstOrDefault(t => t.Id == meeting.TemplateId)?.DisplayName() ?? L.T("一般會議") };
        ToolTipService.SetToolTip(template, L.T("會議紀錄的範本。換了範本之後，從「更多」選「重新整理會議紀錄」就會照新範本整理"));
        var templateMenu = new MenuFlyout();
        foreach (var t in templates) templateMenu.Items.Add(Ui.Check(t.DisplayName(), t.Id == meeting.TemplateId, () => host.Controller.SetTemplate(meeting.Id!.Value, t.Id)));
        template.Flyout = templateMenu;
        menus.Children.Add(template);
        var language = new DropDownButton { Content = meeting.MeetingLanguage.Name() };
        ToolTipService.SetToolTip(language, L.T("會議語言：決定轉錄和會議紀錄用的語言。改了之後，從「更多」重新轉錄或重新整理會議紀錄"));
        var languageMenu = new MenuFlyout();
        foreach (var l in MeetingLanguages.All) languageMenu.Items.Add(Ui.Check(l.Name(), l == meeting.MeetingLanguage, () => host.Controller.SetMeetingLanguage(meeting.Id!.Value, l)));
        language.Flyout = languageMenu;
        menus.Children.Add(language);
        // 錄音中先填人數（M§26.2）：錄完轉錄時用
        if (IsRecordingThis)
        {
            var count = new DropDownButton { Content = meeting.MinSpeakers is int n ? L.F("大約 %lld 人", n) : L.T("人數：自動判斷") };
            var countMenu = new MenuFlyout();
            countMenu.Items.Add(Ui.Check(L.T("自動判斷"), meeting.MinSpeakers is null, () => host.Controller.SetMinSpeakers(meeting.Id!.Value, null)));
            for (var i = 2; i <= 12; i++)
            {
                var value = i;
                countMenu.Items.Add(Ui.Check(L.F("%lld 人", value), meeting.MinSpeakers == value, () => host.Controller.SetMinSpeakers(meeting.Id!.Value, value)));
            }
            count.Flyout = countMenu;
            ToolTipService.SetToolTip(count, L.T("大約幾個人：人多、吵雜或手機放在桌上錄音時，先告訴拾音大約幾個人，說話者會分得比較準"));
            menus.Children.Add(count);
        }
        var wrap = new StackPanel { Spacing = 8 };
        wrap.Children.Add(info);
        wrap.Children.Add(menus);
        return wrap;
    }

    Border Banner(UIElement left, UIElement? right, Brush background)
    {
        var bar = right is FrameworkElement r ? Ui.Bar(left as FrameworkElement, null, r, 12) : (FrameworkElement)left;
        return new Border { Child = bar, Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(8), Background = background };
    }

    Border TrashBanner()
    {
        var id = Detail.Meeting.Id!.Value;
        return Banner(Ui.Text(L.T("這場會議在垃圾桶裡，30 天後會永久刪除"), 13),
            Ui.Row(8, Ui.Button(L.T("還原"), () => host.Controller.Restore(id)), Ui.Button(L.T("永久刪除"), () => _ = window.ConfirmPurge(id))), Ui.Subtle);
    }

    /// <summary>工作進度、失敗原因；背景的輔助工作（校正、猜說話者、重新辨識說話者）也顯示一行</summary>
    UIElement? StatusBanner()
    {
        statusKey = StatusKey();
        statusLiveText = null;
        statusLiveProgress = null;
        var id = Detail.Meeting.Id!.Value;
        var state = host.Controller.Status(Detail);
        var running = host.Runner.RunningJob(id);
        Button Cancel() => Ui.Button(L.T("取消"), () => host.Controller.CancelJobs(id));
        var warm = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 0xB9, 0x7F, 0x06));
        switch (state)
        {
            case MeetingStatus.Queued:
                return Banner(Ui.Text(Detail.LatestJob?.Kind == JobKind.Transcribe ? L.T("排隊等待轉錄") : L.T("排隊等待整理"), 13), Cancel(), warm);
            case MeetingStatus.Transcribing t:
                statusLiveProgress = t.Progress is double p ? new ProgressBar { Value = p * 100, Width = 160, Foreground = Ui.Signal } : null;
                statusLiveText = Ui.Text(TranscribingText(t.Progress, t.Stage, running?.Remaining), 13);
                return Banner(Ui.Row(10, statusLiveText, (UIElement?)statusLiveProgress ?? new ProgressRing { IsActive = true, Width = 16, Height = 16 }), Cancel(), warm);
            case MeetingStatus.Summarizing:
                var provider = running?.Provider?.ShortLabel() ?? "";
                var text = running?.Job.Kind == JobKind.Revise ? L.F("正在用 %@ 依你的回答修正會議紀錄…", provider) : L.F("正在用 %@ 整理會議紀錄…", provider);
                return Banner(Ui.Row(10, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Text(text, 13)), Cancel(), warm);
            case MeetingStatus.Failed f:
                var actions = Ui.Row(8);
                // 登入問題（3.1.2）：先引導重新登入，登入完再按重試
                if (f.Kind != JobKind.Transcribe && LLMException.IsLoginProblem(f.Error) && host.Settings.Provider is LLMProvider.Claude or LLMProvider.Codex)
                    actions.Children.Add(Ui.Button(L.F("重新登入 %@", host.Settings.Provider.ShortLabel()), () => CliLogin.Open(host, host.Settings.Provider)));
                actions.Children.Add(Ui.Button(L.T("重試"), () => host.Controller.Retry(id)));
                var message = Ui.Text(f.Kind == JobKind.Transcribe ? L.F("轉錄失敗：%@", f.Error) : L.F("整理失敗：%@", f.Error), 13, maxLines: 3, select: true);
                return Banner(Ui.Row(8, new SymbolIcon(Symbol.Important) { Foreground = Ui.Danger }, message), actions,
                    new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 0xC0, 0x39, 0x2B)));
            case MeetingStatus.Cancelled:
                return Banner(Ui.Text(L.T("已取消轉錄"), 13), Ui.Button(L.T("重新轉錄"), () => host.Controller.Retry(id)), warm);
        }
        foreach (var lane in Enum.GetValues<JobLane>())
        {
            if (host.Runner.Running(lane) is not { } helper || helper.Job.MeetingId != id) continue;
            if (helper.Job.Kind is not (JobKind.Correct or JobKind.NameSpeakers or JobKind.Rediarize)) continue;
            var provider = helper.Provider?.ShortLabel() ?? "";
            var text = helper.Job.Kind switch
            {
                JobKind.Correct => L.F("正在用 %@ 檢查可能聽錯的字…", provider),
                JobKind.Rediarize => L.T("正在重新辨識說話者…"),
                _ => L.F("正在用 %@ 猜說話者的名字…", provider),
            };
            return Banner(Ui.Row(10, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Text(text, 13, color: Ui.Secondary)), Cancel(), Ui.Subtle);
        }
        return null;
    }

    static string TranscribingText(double? progress, string stage, TimeSpan? remaining)
    {
        if (progress is not double p) return stage.Length == 0 ? L.T("準備轉錄…") : stage + "…";
        var text = L.F("%@中 %lld%%", stage.Length == 0 ? L.T("轉錄") : stage, (long)(p * 100));
        if (remaining is { TotalSeconds: > 0 } r)
            text += r.TotalSeconds < 60 ? L.T("，快好了") : L.F("，約剩 %lld 分鐘", (long)Math.Ceiling(r.TotalMinutes));
        return text;
    }

    /// <summary>AI 猜到的說話者名字（M§21.5、R19.2）：按「套用」才改名</summary>
    UIElement SuggestionsBanner()
    {
        var id = Detail.Meeting.Id!.Value;
        var suggestions = Detail.SpeakerSuggestions.OrderBy(p => p.Key).ToList();
        var view = new Button { Content = L.T("查看") };
        var panel = Ui.Column(12, Ui.Text(L.T("猜到的說話者名字"), 15, bold: true));
        foreach (var (idx, s) in suggestions)
        {
            var row = Ui.Bar(Ui.Row(8, Ui.Text(Detail.Speakers.GetValueOrDefault(idx) ?? "", 14, color: Ui.Speaker(idx)), new SymbolIcon(Symbol.Forward), Ui.Text(s.Name, 14, bold: true)),
                null, Ui.Row(8, Ui.Link(L.T("忽略"), () => host.Controller.DismissSpeakerSuggestion(id, idx)), Ui.Link(L.T("套用"), () => host.Controller.ApplySpeakerSuggestion(id, idx))));
            panel.Children.Add(Ui.Column(4, row, Ui.Row(6, Ui.Time(s.Time, Seek), Ui.Caption(s.Reason))));
        }
        panel.Children.Add(Ui.Bar(Ui.Caption(L.T("套用後，逐字稿和會議紀錄都會換成名字。")), null,
            Ui.Button(L.T("全部套用"), () => host.Controller.ApplyAllSpeakerSuggestions(id), accent: true)));
        panel.Width = 420;
        view.Flyout = new Flyout { Content = panel };
        return Banner(Ui.Text(L.F("從對話內容猜到 %lld 位說話者的名字", suggestions.Count), 13), view,
            new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(20, 0x2D, 0x6A, 0x5D)));
    }

    void Seek(double seconds) => host.Player.Seek(seconds);

    /// <summary>「更多」選單（同 Mac 的 MoreMenu）：按下時由 AppController 檢查目前的會議，不能做就說明原因</summary>
    FrameworkElement MoreMenu()
    {
        var id = Detail.Meeting.Id!.Value;
        var c = host.Controller;
        var menu = new MenuFlyout();
        menu.Items.Add(Ui.Item(L.T("重新整理會議紀錄"), () => c.Resummarize(id), icon: Symbol.Refresh));
        menu.Items.Add(Ui.Item(L.T("檢查可能聽錯的字"), () => c.CheckTranscript(id), icon: Symbol.Find));
        menu.Items.Add(Ui.Item(L.T("重新轉錄（含說話者辨識）"), () => c.Retranscribe(id), icon: Symbol.Microphone));
        menu.Items.Add(Ui.Item(L.T("重新辨識說話者…"), () => { if (c.RequestRediarize(id)) _ = Dialogs.Rediarize(window, id); }, icon: Symbol.People));
        menu.Items.Add(Ui.Item(L.T("復原說話者的調整"), () => c.UndoSpeakerEdit(id), icon: Symbol.Undo));
        menu.Items.Add(Ui.Item(L.T("復原上一版會議紀錄"), () => c.UndoNotes(id), icon: Symbol.Undo));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item(L.T("匯出…"), () => _ = Dialogs.Export(window, id), icon: Symbol.Share));
        menu.Items.Add(Ui.Sub(L.T("複製會議紀錄"),
            Ui.Item(L.T("富文字（貼到 Word、Notion、郵件）"), () => CopyNotes(id, "rich")),
            Ui.Item(L.T("Markdown"), () => CopyNotes(id, "markdown")),
            Ui.Item(L.T("純文字（貼到 Slack、訊息）"), () => CopyNotes(id, "plain"))));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(window.FolderMenu(id, Detail.Meeting.Folder));
        menu.Items.Add(Ui.Item(L.T("移到垃圾桶"), () => host.Controller.MoveToTrash(id), icon: Symbol.Delete));
        var button = new DropDownButton { Content = Ui.Row(6, new SymbolIcon(Symbol.More), Ui.Text(L.T("更多"), wrap: false)), Flyout = menu };
        // 內容是圖示加文字時，無障礙名稱不會自動帶出來（螢幕閱讀器、UI Automation 都讀不到）
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.T("更多"));
        ToolTipService.SetToolTip(button, L.T("更多動作"));
        return button;
    }

    void CopyNotes(long id, string style)
    {
        var b = host.Controller.Bundle(id);
        if (b.Notes is null)
        {
            window.ShowNotice(L.T("還沒有會議紀錄可以複製"));
            return;
        }
        switch (style)
        {
            case "plain": Copy.Text(IO.NotesExporter.Plain(b)); break;
            case "markdown": Copy.Text(IO.NotesExporter.Markdown(b)); break;
            default: Copy.Rich(IO.NotesExporter.Plain(b), IO.NotesExporter.Html(b, IO.ExportFonts.Windows)); break;
        }
        window.ShowNotice(L.T("已複製會議紀錄"));
    }
}

/// <summary>
/// 錄音中的畫面（同 Mac 的 RecordingPanel）：狀態、計時、音量條、暫停、標記重點、停止；語言切換、收音提醒；即時逐字稿和我的筆記
/// </summary>
public sealed class RecordingPanel
{
    readonly AppHost host;
    readonly MainWindow window;
    public Grid Root { get; } = new();
    readonly TextBlock state = Ui.Text("", 18, wrap: false);
    readonly TextBlock clock = Ui.Text("", 18, color: null, wrap: false);
    readonly Ellipse dot = new() { Width = 10, Height = 10 };
    readonly LevelMeter mic = new(), system = new();
    readonly StackPanel meters = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    // 音量條各自一列，一開始就建好，之後只切換顯示：每次更新都重建 Row 的話，音量條還在舊的 Row 裡，
    // 再放進新的 Row 會丟出「已經是別的元件的子元件」（0x800F1000，2026-10-21 在虛擬機錄音時當掉）
    readonly StackPanel micRow, systemRow;
    readonly Button pause = new();
    readonly TextBlock warning = Ui.Text("", 12, maxLines: 2);
    readonly StackPanel hints = new();
    readonly StackPanel lines = new() { Spacing = 8, Padding = new Thickness(20) };
    readonly TextBlock volatileText = Ui.Text("", 14);
    readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Button latest = new() { Content = L.T("回到最新"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12), Visibility = Visibility.Collapsed };
    readonly TextBox userNotes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    readonly UserNotesBox notesBox;
    int shownLines;
    bool followLatest = true;
    long? meetingId;

    public RecordingPanel(AppHost host, MainWindow window)
    {
        this.host = host;
        this.window = window;
        notesBox = new UserNotesBox(host, userNotes, () => host.Recorder.Elapsed);
        micRow = Ui.Row(4, new SymbolIcon(Symbol.Microphone), mic.Root);
        systemRow = Ui.Row(4, new SymbolIcon(Symbol.Volume), system.Root);
        meters.Children.Add(micRow);
        meters.Children.Add(systemRow);
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            Root.RowDefinitions.Add(new RowDefinition { Height = h });
        pause.Click += (_, _) => { if (host.Recorder.IsPaused) host.Recorder.Resume(); else host.Recorder.Pause(); };
        var bookmark = Ui.Button(L.T("標記重點"), () =>
        {
            if (host.Controller.BookmarkRecording()) window.ShowNotice(L.F("已標記 %@ 為重點", TimeFormat.String(host.Recorder.Elapsed)));
        }, tooltip: L.T("標記這個時間點（Ctrl+B），整理時會被當成重點"));
        var stop = Ui.Button(L.T("停止並轉錄"), () => host.Recorder.Stop(), accent: true, tooltip: L.T("停止錄音，開始正式轉錄"));
        var controls = Ui.Bar(Ui.Row(14, Ui.Row(8, dot, state, clock), meters), null, Ui.Row(10, pause, bookmark, stop), 14);
        warning.Foreground = Ui.Signal;
        var head = Ui.Column(4, controls, warning);
        head.Padding = new Thickness(20, 12, 20, 8);
        Grid.SetRow(head, 0);
        Root.Children.Add(head);
        Grid.SetRow(hints, 1);
        Root.Children.Add(hints);

        // 即時逐字稿：往回捲時不拉回最新，「回到最新」（R2）
        scroll.Content = lines;
        scroll.ViewChanged += (_, e) =>
        {
            if (e.IsIntermediate) return;
            var atBottom = scroll.VerticalOffset >= scroll.ScrollableHeight - 40;
            followLatest = atBottom;
            latest.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
        };
        latest.Click += (_, _) => { followLatest = true; scroll.ChangeView(null, scroll.ScrollableHeight, null); latest.Visibility = Visibility.Collapsed; };
        var live = new Grid { Children = { scroll, latest } };
        var notesTitle = Ui.Text(L.T("我的筆記"), 16, bold: true);
        notesTitle.FontFamily = Ui.TitleFont(MeetingLanguage.Zh);
        var notes = Ui.Column(8, notesTitle, Ui.Caption(L.T("每換一行會自動加上當下的錄音時間；整理時會優先納入這些內容。")));
        var notesPanel = new Grid { Padding = new Thickness(16), Width = 300, RowSpacing = 8 };
        notesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        notesPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        notesPanel.Children.Add(notes);
        Grid.SetRow(userNotes, 1);
        notesPanel.Children.Add(userNotes);
        var body = new Grid { BorderBrush = Ui.Divider, BorderThickness = new Thickness(0, 1, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(live);
        Grid.SetColumn(notesPanel, 1);
        body.Children.Add(notesPanel);
        Grid.SetRow(body, 2);
        Root.Children.Add(body);
        mic.Read = () => host.Recorder.MicLevel;
        system.Read = () => host.Recorder.SystemLevel;
    }

    public void Show(MeetingDetail detail)
    {
        if (meetingId != detail.Meeting.Id)
        {
            meetingId = detail.Meeting.Id;
            lines.Children.Clear();
            shownLines = 0;
            followLatest = true;
            notesBox.Load(detail.Meeting.Id!.Value, detail.Meeting.UserNotes);
            volatileText.Language = Ui.ContentLanguage(detail.Meeting.MeetingLanguage);
        }
        Update();
    }

    public void Update()
    {
        var r = host.Recorder;
        dot.Fill = r.IsPaused ? Ui.Secondary : Ui.Danger;
        state.Text = r.IsPaused ? L.T("已暫停") : L.T("錄音中");
        clock.Text = TimeFormat.String(r.ElapsedSeconds);
        clock.Foreground = Ui.Secondary;
        pause.Content = r.IsPaused ? L.T("繼續") : L.T("暫停");
        ToolTipService.SetToolTip(pause, r.IsPaused ? L.T("繼續錄音（Ctrl+Shift+P）") : L.T("暫停錄音（Ctrl+Shift+P）"));
        micRow.Visibility = r.HasMic ? Visibility.Visible : Visibility.Collapsed;
        systemRow.Visibility = r.HasSystem ? Visibility.Visible : Visibility.Collapsed;
        mic.Running = r.HasMic;
        system.Running = r.HasSystem;
        warning.Text = r.DeviceWarning
            ?? (r.HasSystem && r.SystemSilent ? L.T("超過 15 秒沒有收到電腦的聲音：請確認線上會議有聲音在播") : r.LiveStatus);
        warning.Visibility = warning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        hints.Children.Clear();
        var warm = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 0xB9, 0x7F, 0x06));
        if (r.LanguageSwitch is var (from, to))
            hints.Children.Add(new Border
            {
                Background = warm, Padding = new Thickness(16, 8, 16, 8),
                Child = Ui.Bar(null, Ui.Text(L.F("聽起來是%@，即時逐字稿和會議語言已經改成%@", to.UiName(), to.UiName()), 13),
                    Ui.Button(L.F("改回%@", from.UiName()), () => r.SwitchLanguage(from))),
            });
        if (r.Hint is Recorder.LevelHint hint)
            hints.Children.Add(new Border
            {
                Background = warm, Padding = new Thickness(16, 8, 16, 8),
                Child = Ui.Bar(null, Ui.Text(HintText(hint), 13), Ui.Button(L.T("知道了"), r.DismissLevelHint, tooltip: L.T("這場錄音不再提示這件事"))),
            });

        var all = r.LiveLines;
        if (all.Count < shownLines) { lines.Children.Clear(); shownLines = 0; }
        if (lines.Children.Count == 0 && all.Count == 0 && r.VolatileText.Length == 0)
            lines.Children.Add(Ui.Text(host.Settings.LiveTranscription ? L.T("開始說話後，逐字稿會即時出現在這裡。") : L.T("即時逐字稿已在設定中關閉，錄音結束後會正式轉錄。"), color: Ui.Secondary));
        else if (shownLines == 0 && lines.Children.Count > 0 && all.Count > 0) lines.Children.Clear();
        lines.Children.Remove(volatileText);
        var language = window.State.Detail?.Meeting.MeetingLanguage ?? MeetingLanguage.Zh;
        for (; shownLines < all.Count; shownLines++)
        {
            var line = all[shownLines];
            var text = Ui.Text(line.Text, 14, select: true);
            text.Language = Ui.ContentLanguage(language);
            var time = Ui.Text(TimeFormat.String(line.Start), 13, color: Ui.Celadon, wrap: false);
            time.Width = 52;
            time.TextAlignment = TextAlignment.Right;
            lines.Children.Add(Ui.Split(time, text, 12));
        }
        volatileText.Text = r.VolatileText.Length > 0 ? "…  " + r.VolatileText : "";
        volatileText.Foreground = Ui.Secondary;
        if (r.VolatileText.Length > 0) lines.Children.Add(volatileText);
        if (followLatest) scroll.ChangeView(null, double.MaxValue, null, disableAnimation: true);
    }

    public static string HintText(Recorder.LevelHint hint) => hint switch
    {
        Recorder.LevelHint.Silent => L.T("麥克風沒有收到聲音：可能被靜音、被其他 app 佔用，或選錯了麥克風。可以在設定的「錄音」換一個。"),
        Recorder.LevelHint.Quiet => L.T("聲音有點小：手機或筆電放近說話的人一點。"),
        _ => L.T("背景有點吵，說話者可能分不準。錄完可以用「重新辨識說話者」告訴拾音大約幾個人。"),
    };
}

/// <summary>
/// 音量條：模仿 VU 表的分段 LED，滿格的地方是琥珀色（M§9.3）。自己每 0.08 秒讀一次音量，不經過畫面的重新整理（design-windows §11.2）
/// </summary>
public sealed class LevelMeter
{
    const int Segments = 14;
    public StackPanel Root { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    readonly Rectangle[] bars = new Rectangle[Segments];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    public Func<float> Read { get; set; } = () => 0;
    public bool Running
    {
        set { if (value) timer.Start(); else timer.Stop(); }
    }

    public LevelMeter()
    {
        for (var i = 0; i < Segments; i++)
        {
            bars[i] = new Rectangle { Width = 4, Height = 12, RadiusX = 1, RadiusY = 1 };
            Root.Children.Add(bars[i]);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Root, L.T("音量"));
        timer.Tick += (_, _) => Ui.Guard(() => Draw(Read()));
    }

    void Draw(float level)
    {
        for (var i = 0; i < Segments; i++)
        {
            var lit = i < level * Segments;
            bars[i].Fill = !lit ? Ui.Subtle : i >= Segments - 3 ? Ui.Signal : Ui.Celadon;
        }
    }
}

/// <summary>
/// 我的筆記的輸入框（同 Mac 的 UserNotesEditor）：換行時自動加上當下的錄音（或播放）時間；打字停 0.8 秒存一次
/// </summary>
public sealed class UserNotesBox
{
    readonly AppHost host;
    readonly TextBox box;
    readonly Func<double?> now;
    readonly DispatcherTimer save = new() { Interval = TimeSpan.FromMilliseconds(800) };
    long? meetingId;
    string saved = "";
    string previous = "";
    bool adjusting;

    public UserNotesBox(AppHost host, TextBox box, Func<double?> now)
    {
        this.host = host;
        this.box = box;
        this.now = now;
        // 旁邊的標題是另一個元件，螢幕閱讀器、UI Automation 只會讀到「編輯」
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, L.T("我的筆記"));
        box.TextChanged += (_, _) => Changed();
        save.Tick += (_, _) => { save.Stop(); Save(); };
    }

    public void Load(long id, string text)
    {
        if (meetingId == id && text == saved) return;
        Save();
        meetingId = id;
        adjusting = true;
        box.Text = text;
        adjusting = false;
        saved = previous = text;
    }

    void Changed()
    {
        if (adjusting) return;
        var text = box.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (now() is double seconds)
        {
            var stamp = $"[{TimeFormat.String(seconds)}] ";
            string? replaced = null;
            if (previous.Length == 0 && text.Length > 0 && !text.StartsWith('[')) replaced = stamp + text;
            else if (text.Length == previous.Length + 1 && text.EndsWith('\n')) replaced = text + stamp;
            if (replaced is not null)
            {
                adjusting = true;
                box.Text = replaced;
                box.SelectionStart = replaced.Length;
                adjusting = false;
                text = replaced;
            }
        }
        previous = text;
        if (text == saved) return;
        save.Stop();
        save.Start();
    }

    public void Save()
    {
        if (meetingId is not long id) return;
        var text = box.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text == saved) return;
        saved = text;
        host.Controller.SaveUserNotes(id, text);
    }
}
