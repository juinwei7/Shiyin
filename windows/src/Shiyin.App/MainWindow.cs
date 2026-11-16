using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shiyin.App.Views;
using Shiyin.Data;
using Shiyin.IO;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Shiyin.App;

/// <summary>
/// 主視窗（同 Mac 的 MainWindow.swift，design-windows §11.1）：左邊側欄（錄音、提問、搜尋、資料夾、會議列表、垃圾桶、匯入），右邊會議內容。
/// 關掉視窗 app 不結束（錄音和工作照常進行，R13.3），從系統匣再打開
/// </summary>
public sealed class MainWindow : Window
{
    readonly AppHost host;
    public MainState State { get; }
    readonly Grid root = new();
    readonly StackPanel toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 10, 12, 6) };
    readonly AutoSuggestBox search = new() { PlaceholderText = L.T("搜尋所有會議"), Margin = new Thickness(12, 0, 12, 8), QueryIcon = new SymbolIcon(Symbol.Find) };
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(4, 0, 4, 0) };
    /// <summary>
    /// 側欄的導覽（M§60.2、R85.1）：所有會議、人物、資料夾都是同一種列，用平台的 ListView 項目（同 Outlook、檔案總管的資料夾窗格），
    /// 選到的那一列是平台的選取樣子（強調色＝青瓷綠）。下面一段的標題在 sectionTitle
    /// </summary>
    readonly ListView scopes = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(4, 0, 4, 0) };
    readonly TextBlock sectionTitle = Ui.Caption("");
    /// <summary>
    /// 會議那一段的標題列（R97.1、M§71）：左邊「所有會議」（或資料夾、定期會議的名稱）・分組方式，右邊「顯示方式」（排序、分組、篩選）；
    /// 篩選中下面一列「篩選：…」＋✕，篩選後沒有會議時說明＋「清除篩選」。都放在列表外面（停用的 ListViewItem 裡的按鈕按不了）
    /// </summary>
    readonly Button viewOptions = new();
    readonly Grid sectionHeader;
    readonly Grid filterBar;
    readonly TextBlock filterText = Ui.Text("", 13, maxLines: 2);
    readonly StackPanel filterEmpty;
    readonly StackPanel folders = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 2) };
    /// <summary>垃圾桶裡列表上方的一行：說明和「清空垃圾桶…」（R88.3）。放在列表外面：停用的 ListViewItem 裡的按鈕按不了</summary>
    readonly Grid trashHeader;
    readonly Button emptyTrash;
    readonly PersonPage personPage;
    readonly SeriesPage seriesPage;
    readonly StackPanel footer = new() { Spacing = 6, Margin = new Thickness(12, 6, 12, 10) };
    readonly Border detailHost = new();
    readonly InfoBar toast = new() { IsClosable = true, Severity = InfoBarSeverity.Informational, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 80), MaxWidth = 560 };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool wasBusy;
    readonly DetailView detailView;
    /// <summary>待確認的回答（M§31.2）：DEBUG 的控制通道用</summary>
    public Views.ReviewDraft ReviewDraft => detailView.ReviewDraft;
    public MindMapZoom MindMapZoom => detailView.MindMapZoom;
    /// <summary>主視窗目前是前景（Activated 事件）</summary>
    public bool IsForeground { get; private set; }
    bool updatingSelection;

    public MainWindow(AppHost host)
    {
        this.host = host;
        State = new MainState(host);
        Title = L.T("拾音");
        WindowPlacement.Center(this, 1180, 780);
        WindowPlacement.MinimumSize(this, 960, 620);
        Ui.ContentScale = TextSizes.Scale(host.Settings.TextSize);   // 會議內容的文字大小（R59）
        detailView = new DetailView(host, this);
        personPage = new PersonPage(host, this);
        seriesPage = new SeriesPage(host, this);
        emptyTrash = Ui.Button(L.T("清空垃圾桶…"), () => _ = ConfirmEmptyTrash());
        emptyTrash.VerticalAlignment = VerticalAlignment.Center;
        // 垃圾桶裡也照排序（R97.4）：「顯示方式」只有排序那一段（Windows 沒有選單列，放在說明那一行）
        var trashOptions = ViewOptionsButton(trash: true);
        trashHeader = Ui.Bar(null, Ui.Caption(L.T("垃圾桶裡的會議 30 天後會永久刪除")), Ui.Row(4, trashOptions, emptyTrash));
        ConfigureViewOptions(viewOptions, trash: false);
        sectionTitle.VerticalAlignment = VerticalAlignment.Center;
        sectionTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        sectionTitle.TextWrapping = TextWrapping.NoWrap;
        sectionHeader = Ui.Bar(null, sectionTitle, viewOptions, 4);
        sectionHeader.Margin = new Thickness(16, 6, 10, 2);
        // 篩選中（R97.3）：漏斗、「篩選：有待確認、王小明 有參加」、✕
        var clear = Ui.Borderless(new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 11 } }, Ui.Secondary);
        clear.Padding = new Thickness(6);
        ToolTipService.SetToolTip(clear, L.T("清除篩選"));
        AutomationProperties.SetName(clear, L.T("清除篩選"));
        clear.Click += (_, _) => State.SetFilter(new MeetingFilter());
        var funnel = new FontIcon { Glyph = "\uE71C", FontSize = 13, Foreground = Ui.Celadon, VerticalAlignment = VerticalAlignment.Center };
        filterText.VerticalAlignment = VerticalAlignment.Center;
        clear.VerticalAlignment = VerticalAlignment.Center;
        filterBar = Ui.Bar(funnel, filterText, clear, 6);
        filterBar.Margin = new Thickness(16, 2, 10, 2);
        var clearLink = Ui.Link(L.T("清除篩選"), () => State.SetFilter(new MeetingFilter()));
        filterEmpty = Ui.Column(4, Ui.Text(L.T("沒有符合篩選的會議"), 13, color: Ui.Secondary), clearLink);
        filterEmpty.Margin = new Thickness(16, 4, 12, 4);
        trashHeader.Margin = new Thickness(16, 0, 12, 6);
        trashHeader.Visibility = Visibility.Collapsed;
        Build();
        // 關掉主視窗只是藏起來（R13.3）
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            detailView.FlushDrafts();
            AppWindow.Hide();
        };
        State.Changed += Refresh;
        State.RecorderChanged += () => { BuildToolbar(); detailView.UpdateRecording(); };
        // 主視窗是不是前景（R42.1：在前景時收音提醒不送通知，只有畫面和系統匣的提示）
        Activated += (_, e) =>
        {
            IsForeground = e.WindowActivationState != WindowActivationState.Deactivated;
            // 回到前景時更新 claude、codex 的登入狀態（使用者可能剛在終端機登入，M§34.1）
            if (IsForeground) _ = CliLogin.RefreshIfStale();
        };
        // 整理工具準備好了沒變了（登入狀態、key、暫停、設定）：會議頁的提示和失敗橫幅跟著變（M§34.2）
        host.Settings.SummarizerChanged += () => host.Post(Refresh);
        host.Settings.Changed += key =>
        {
            if (key is AppSettings.ProviderKey or AppSettings.CompatibleBaseUrlKey || key.EndsWith("Model", StringComparison.Ordinal)) host.Post(Refresh);
            // 文字大小（R59）：快捷鍵、設定頁、控制通道改的都一樣，整個會議內容重畫
            if (key == AppSettings.TextSizeKey) host.Post(ApplyTextSize);
        };
        // 工作進度不發事件：轉錄、整理中每秒更新一次列表的狀態和會議頁的進度
        progressTimer.Tick += (_, _) => Ui.Guard(() =>
        {
            if (!host.Runner.IsBusy && !wasBusy) return;
            wasBusy = host.Runner.IsBusy;
            BuildList();
            detailView.UpdateStatus();
        });
        progressTimer.Start();
        host.Notifier.Toast += ShowNotice;
        host.Controller.Notice += m => host.Post(() => ShowNotice(m));
        host.Controller.Error += m => host.Post(() => ShowError(m));
        host.Controller.Reveal += id => host.Post(() => Reveal(id));
        // 一次還原好幾場（R88）：回到會議列表、選取那幾場
        host.Controller.RevealMany += ids => host.Post(() => State.RevealMany(ids));
        host.Recorder.Changed += () => host.Post(ShowRecorderAlerts);
        // 更新下載的進度只重畫側欄底部（R10.6）
        if (host.Updater is { } updater) updater.Changed += () => host.Post(BuildFooter);
        toastTimer.Tick += (_, _) => { toast.IsOpen = false; toastTimer.Stop(); };
        // 換深淺色：列表的顏色是建列時給的，要重建
        root.ActualThemeChanged += (_, _) => { Ui.SetTheme(root.ActualTheme); listSignature = null; Refresh(); };
        Ui.SetTheme(root.ActualTheme);
        State.Reload();
    }

    public XamlRoot? XamlRoot => root.XamlRoot;
    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    void Build()
    {
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 240 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = Ui.SidebarBackground(new Grid { BorderBrush = Ui.Divider, BorderThickness = new Thickness(0, 0, 1, 0) });
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            sidebar.RowDefinitions.Add(new RowDefinition { Height = h });
        Place(sidebar, toolbar, 0);
        Place(sidebar, search, 1);
        folders.Children.Add(scopes);
        folders.Children.Add(sectionHeader);
        folders.Children.Add(filterBar);
        folders.Children.Add(filterEmpty);
        Place(sidebar, folders, 2);
        Place(sidebar, trashHeader, 2);
        AutomationProperties.SetName(scopes, L.T("資料夾"));
        // 「發現 N 組可能的定期會議」那一列（M§73）是動作不是地方：點了（或 Enter）打開確認，選取留在原本那一列
        scopes.IsItemClickEnabled = true;
        scopes.ItemClick += (_, e) =>
        {
            // 項目直接是 ListViewItem 時，ClickedItem 是它的 Content（2026-11-16 虛擬機：比對 Tag 一直對不到）
            var clicked = e.ClickedItem as ListViewItem ?? scopes.Items.OfType<ListViewItem>().FirstOrDefault(i => ReferenceEquals(i.Content, e.ClickedItem));
            if (clicked is { Tag: Scope { Suggestions: true } }) _ = Dialogs.SeriesSuggestions(this);
        };
        scopes.SelectionChanged += (_, _) =>
        {
            if (updatingSelection || scopes.SelectedItem is not ListViewItem { Tag: Scope scope }) return;
            if (scope.Suggestions)
            {
                SyncScopeSelection();
                return;
            }
            detailView.FlushDrafts();
            if (scope.People) State.SetSidebar(SidebarMode.People);
            else if (scope.Series is long series) State.ShowSeries(series);
            else State.ShowMeetings(scope.Folder);
        };
        Place(sidebar, list, 3);
        Place(sidebar, footer, 4);
        Grid.SetColumn(sidebar, 0);
        root.Children.Add(sidebar);
        Grid.SetColumn(detailHost, 1);
        root.Children.Add(detailHost);
        Grid.SetColumnSpan(toast, 2);
        root.Children.Add(toast);

        search.TextChanged += (_, e) => { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) State.SetSearch(search.Text); };
        list.SelectionChanged += (_, _) =>
        {
            if (updatingSelection) return;
            // 會議列表可以多選（R88.1、design-windows §11.23）：Ctrl、Shift 點選
            if (list.SelectionMode == ListViewSelectionMode.Extended)
            {
                State.SetListSelection(list.SelectedItems.OfType<ListViewItem>().Select(i => i.Tag).OfType<long>().ToList());
                return;
            }
            if (list.SelectedItem is ListViewItem { Tag: long id }) State.Select(id);
            else if (list.SelectedItem is ListViewItem { Tag: SearchHit hit }) OpenSearchHit(hit);
            else if (list.SelectedItem is ListViewItem { Tag: PersonOverview person }) State.SelectPerson(person.Id);
        };
        // 會議列表有焦點時按 Delete：選取的全部移到垃圾桶，垃圾桶裡是永久刪除（同檔案總管；都要確認，R88.2）。Ctrl+Delete 是整個視窗的快捷鍵
        list.KeyDown += (_, e) =>
        {
            // 人物列表選到一個人時按 Delete：移除這個人（R85.7，要確認）
            if (e.Key == VirtualKey.Delete && State.ShowsPeople && State.SelectedPerson is long person)
            {
                _ = ConfirmRemovePerson(person);
                e.Handled = true;
                return;
            }
            if (e.Key != VirtualKey.Delete || list.SelectionMode != ListViewSelectionMode.Extended
                || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)) return;
            var ids = SelectedMeetings();
            if (ids.Count == 0) return;
            _ = State.ShowTrash ? ConfirmPurge(ids) : ConfirmTrash(ids);
            e.Handled = true;
        };

        // 拖放匯入（R3.1）
        root.AllowDrop = true;
        root.DragOver += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
        root.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            RequestImport(items.OfType<StorageFile>().Select(f => f.Path).ToList());
        };

        // 鍵盤快捷鍵（同 Mac 的選單，⌘ 換成 Ctrl）。掛在整個視窗上，所以不顯示 WinUI 的快捷鍵提示：
        // 不然滑鼠停在視窗裡任何地方，上方都會跳出「Ctrl+O」（2026-10-21 在虛擬機看到）
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Accelerator(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = PickAndImport());
        Accelerator(VirtualKey.R, VirtualKeyModifiers.Control, () => host.Controller.ToggleRecording(State.CurrentFolder));
        Accelerator(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () =>
        {
            if (host.Recorder.IsPaused) host.Recorder.Resume(); else host.Recorder.Pause();
        });
        Accelerator(VirtualKey.B, VirtualKeyModifiers.Control, () =>
        {
            if (host.Controller.BookmarkRecording()) ShowNotice(L.F("已標記 %@ 為重點", TimeFormat.String(host.Recorder.Elapsed)));
        });
        Accelerator(VirtualKey.E, VirtualKeyModifiers.Control, () => { if (State.Detail is { } d) _ = Dialogs.Export(this, d.Meeting.Id!.Value); });
        Accelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => detailView.FocusSearch());
        foreach (var tab in Enum.GetValues<DetailTab>())
        {
            var i = (int)tab;
            Accelerator(VirtualKey.Number1 + i, VirtualKeyModifiers.Control, () => { if (State.Detail is not null) State.SetTab(tab); });
        }
        // Ctrl+Delete：選取的會議（多選時是全部，R88.1）移到垃圾桶
        Accelerator(VirtualKey.Delete, VirtualKeyModifiers.Control, () =>
        {
            if (!State.ShowTrash && !State.ShowsPeople && SelectedMeetings() is { Count: > 0 } ids) _ = ConfirmTrash(ids);
        });
        // 會議內容的文字大小（R59.1）：Ctrl+=（加號那顆，按不按 Shift 都算）、數字鍵盤的 +；Ctrl+-；Ctrl+0。
        // 不用 KeyboardAccelerator：游標在輸入框（搜尋框、筆記）時，TextBox 會把 Ctrl+- 當成「插入選擇性連字號」吃掉，
        // 加速鍵收不到（2026-11-02 虛擬機：Ctrl+=、Ctrl+0 有用，只有 Ctrl+- 沒反應）。PreviewKeyDown 比輸入框先收到
        root.PreviewKeyDown += (_, e) =>
        {
            static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
            if (!Down(VirtualKey.Control) || Down(VirtualKey.Menu)) return;   // 右 Alt（AltGr）在某些鍵盤是 Ctrl+Alt，不算
            int? step = (int)e.Key switch
            {
                187 or (int)VirtualKey.Add => 1,          // VK_OEM_PLUS
                189 or (int)VirtualKey.Subtract => -1,    // VK_OEM_MINUS
                (int)VirtualKey.Number0 or (int)VirtualKey.NumberPad0 => 0,
                _ => null,
            };
            if (step is not int s) return;
            if (s != 1 && Down(VirtualKey.Shift)) return;   // Shift 只在加號那顆算（Ctrl+Shift+= 就是 Ctrl++）
            ChangeTextSize(s == 0 ? null : s);
            e.Handled = true;
        };
        // 空白鍵播放／暫停；游標在輸入框時不攔截（M§9.4）
        root.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Space || FocusManager.GetFocusedElement(root.XamlRoot) is TextBox or AutoSuggestBox or RichEditBox) return;
            if (!host.Player.IsLoaded) return;
            host.Player.Toggle();
            e.Handled = true;
        };
        Content = Ui.WindowBackground(root);
    }

    /// <summary>放大、縮小一級，null 是實際大小（R59.1）；到頭了不動。Windows 沒有選單列，下方提示目前的大小</summary>
    void ChangeTextSize(int? delta)
    {
        var current = host.Settings.TextSize;
        var level = delta is int d ? TextSizes.Clamp(current + d) : 0;
        if (level == current) return;
        host.Settings.Set(AppSettings.TextSizeKey, (double)level);
        ShowNotice(L.F("會議內容的文字大小：%@", TextSizes.Label(level)));
    }

    /// <summary>文字大小改了：會議內容整個重畫（R59.2）</summary>
    void ApplyTextSize()
    {
        var scale = TextSizes.Scale(host.Settings.TextSize);
        if (scale == Ui.ContentScale) return;
        Ui.ContentScale = scale;
        Refresh();
        detailView.Rescale();
    }

    static void Place(Grid grid, FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    void Accelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var a = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        a.Invoked += (_, e) => { action(); e.Handled = true; };
        root.KeyboardAccelerators.Add(a);
    }

    // MARK: 畫面

    public void Refresh()
    {
        BuildToolbar();
        BuildFolders();
        BuildList();
        BuildFooter();
        if (State.ShowsPeople)
        {
            // 人物頁（M§60.2）：會議的內容先收起來（編輯中的存草稿），播放照常
            if (ReferenceEquals(detailHost.Child, detailView.Root)) detailView.FlushDrafts();
            var page = personPage.Show(State.People.FirstOrDefault(p => p.Id == State.SelectedPerson));
            if (!ReferenceEquals(detailHost.Child, page)) detailHost.Child = page;
            return;
        }
        // 選了兩場以上（R88.1）：右邊是「已選取 N 場會議」和能一起做的事
        if (!State.SearchActive && State.MultiSelected is { Count: > 1 } multi)
        {
            if (ReferenceEquals(detailHost.Child, detailView.Root)) detailView.FlushDrafts();
            var signature = $"{State.ShowTrash}\u001F{string.Join(',', multi)}";
            if (signature != multiSignature || multiPane is null)
            {
                multiSignature = signature;
                multiPane = MultiSelectionPane(multi);
            }
            if (!ReferenceEquals(detailHost.Child, multiPane)) detailHost.Child = multiPane;
            return;
        }
        multiPane = null;
        // 系列頁（M§73）：側欄點了定期會議、還沒選會議
        if (State.Selection is null && !State.ShowTrash && !State.SearchActive && State.SeriesById(State.SeriesFilter) is { } series)
        {
            if (ReferenceEquals(detailHost.Child, detailView.Root)) detailView.FlushDrafts();
            var page = seriesPage.Show(series);
            if (!ReferenceEquals(detailHost.Child, page)) detailHost.Child = page;
            return;
        }
        if (State.Detail is { } detail) detailView.Show(detail);
        else detailView.Clear();
        detailHost.Child = State.Detail is null ? EmptyDetail() : detailView.Root;
    }

    string? multiSignature;
    FrameworkElement? multiPane;

    /// <summary>選取的會議：多選時是那幾場（列表的順序），不然是目前這場（要在列表裡）</summary>
    public List<long> SelectedMeetings()
    {
        if (State.MultiSelected is { Count: > 1 } multi) return multi;
        return State.Selection is long id && State.FilteredMeetings.Any(m => m.Id == id) ? [id] : [];
    }

    /// <summary>
    /// 選了兩場以上的會議（R88、M§62、design-windows §11.23）：置中的圖示、「已選取 N 場會議」、說明、能一起做的事
    /// （移到資料夾、移到垃圾桶…；垃圾桶裡是還原、永久刪除…）。排法同沒選會議時的畫面
    /// </summary>
    FrameworkElement MultiSelectionPane(List<long> ids)
    {
        var icon = new FontIcon { Glyph = "", FontSize = 40, Foreground = Ui.Secondary, HorizontalAlignment = HorizontalAlignment.Center };
        var title = Ui.Text(L.F("已選取 %lld 場會議", ids.Count), 20, bold: true);
        title.TextAlignment = TextAlignment.Center;
        var hint = Ui.Text(L.T("按住 Ctrl 點選可以加入或取消，按住 Shift 點選一次選一段。"), color: Ui.Secondary);
        hint.TextAlignment = TextAlignment.Center;
        StackPanel buttons;
        if (State.ShowTrash)
            buttons = Ui.Row(8, Ui.Button(L.T("還原"), () => host.Controller.Restore(ids)), Ui.Button(L.T("永久刪除…"), () => _ = ConfirmPurge(ids)));
        else
        {
            var folder = new DropDownButton { Content = Ui.Row(8, new SymbolIcon(Symbol.Folder), Ui.Text(L.T("移到資料夾"), wrap: false)) };
            AutomationProperties.SetName(folder, L.T("移到資料夾"));
            var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
            void Fill()
            {
                menu.Items.Clear();
                foreach (var item in FolderMenuItems(ids, CommonFolder(ids))) menu.Items.Add(item);
            }
            Fill();
            // 打開時才重建：資料夾可能改了
            menu.Opening += (_, _) => Ui.Guard(Fill);
            folder.Flyout = menu;
            buttons = Ui.Row(8, folder, Ui.Button(L.T("移到垃圾桶…"), () => _ = ConfirmTrash(ids)));
        }
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        buttons.Margin = new Thickness(0, 8, 0, 0);
        var content = Ui.Column(10, icon, title, hint, buttons);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        content.MaxWidth = 440;
        content.Padding = new Thickness(24);
        return new Grid { Children = { content } };
    }

    void BuildToolbar()
    {
        toolbar.Children.Clear();
        var recorder = host.Recorder;
        if (recorder.IsRecording)
        {
            // 錄音中：● 經過時間、停止（側邊欄很窄）
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = recorder.IsPaused ? Ui.Secondary : Ui.Danger };
            var clock = Ui.Text(TimeFormat.String(recorder.ElapsedSeconds), 14, wrap: false);
            var open = new Button { Content = Ui.Row(6, dot, clock) };
            ToolTipService.SetToolTip(open, recorder.IsPaused ? L.T("已暫停，點一下打開錄音中的會議") : L.T("錄音中，點一下打開這場會議"));
            open.Click += (_, _) => { if (recorder.MeetingId is long id) Reveal(id); };
            toolbar.Children.Add(open);
            toolbar.Children.Add(Ui.Button(L.T("停止"), recorder.Stop, tooltip: L.T("停止錄音並開始轉錄（Ctrl+R）")));
            return;
        }
        var settings = host.Settings;
        var record = new SplitButton { Content = Ui.Row(6, new SymbolIcon(Symbol.Microphone), Ui.Text(MeetingLanguages.Ui("錄音", "Record"), wrap: false)) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(record, MeetingLanguages.Ui("錄音", "Record"));
        ToolTipService.SetToolTip(record, L.F("開始錄音（Ctrl+R），用%@辨識；點右邊的箭頭可以改錄音來源、麥克風和語言", settings.DefaultMeetingLanguage.UiName()));
        record.Click += (_, _) => host.Controller.StartRecording(State.CurrentFolder);
        var menu = new MenuFlyout();
        FillRecordMenu(menu);
        // 打開時才重建：麥克風的清單是當下的裝置（接上、拔掉耳機後，R58.1）
        menu.Opening += (_, _) => Ui.Guard(() => FillRecordMenu(menu));
        record.Flyout = menu;
        toolbar.Children.Add(record);
        toolbar.Children.Add(Ui.IconButton(Symbol.Message, L.T("問所有會議，回答附上是哪一場會議、哪個時間點"), () => App.Current.ShowAskAll()));
        toolbar.Children.Add(Ui.IconButton(Symbol.Setting, L.T("設定"), () => App.Current.ShowSettings()));
    }

    /// <summary>錄音按鈕的下拉選單：錄音來源、麥克風（R58.1）、會議語言</summary>
    void FillRecordMenu(MenuFlyout menu)
    {
        var settings = host.Settings;
        menu.Items.Clear();
        foreach (var source in new[] { "mic", "both", "system" })
            menu.Items.Add(Ui.Check(SourceLabel(source), settings.RecordSource == source, () =>
            {
                settings.Set(AppSettings.RecordSourceKey, source);
                ShowNotice(L.F("之後的錄音：%@", SourceLabel(source)));
                BuildToolbar();
            }));
        menu.Items.Add(new MenuFlyoutSeparator());
        // 麥克風（R58.1）：跟設定頁的「麥克風」同一個設定；只錄電腦聲音時不列
        if (settings.RecordSource != "system")
        {
            var mic = new MenuFlyoutSubItem { Text = L.F("麥克風：%@", MicrophoneMenu.SelectedLabel(host)), Icon = new SymbolIcon(Symbol.Microphone) };
            foreach (var item in MicrophoneMenu.Items(host, id => settings.InputDevice == id, id =>
            {
                settings.Set(AppSettings.InputDeviceKey, id.Length == 0 ? null : id);
                ShowNotice(id.Length == 0
                    ? L.F("之後的錄音用系統預設的麥克風（%@）", MicrophoneMenu.Name(host, ""))
                    : L.F("之後的錄音用 %@", MicrophoneMenu.Name(host, id)));
                BuildToolbar();
            })) mic.Items.Add(item);
            menu.Items.Add(mic);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var language in MeetingLanguages.All)
            menu.Items.Add(Ui.Check(language.Name(), settings.DefaultMeetingLanguage == language, () =>
            {
                settings.Set(AppSettings.DefaultMeetingLanguageKey, language.Code());
                ShowNotice(L.F("之後的錄音都用%@", language.UiName()));
                BuildToolbar();
            }));
    }

    public static string SourceLabel(string source) => source switch
    {
        "both" => L.T("麥克風＋電腦聲音（線上會議）"),
        "system" => L.T("只錄電腦聲音"),
        _ => L.T("只錄麥克風"),
    };

    /// <summary>
    /// 側欄導覽的一列：人物，或所有會議（Folder 是 null）、某個資料夾、某個定期會議（Series）；
    /// Suggestions 是「發現 N 組可能的定期會議」（動作，不是地方）
    /// </summary>
    sealed record Scope(bool People, string? Folder, long? Series = null, bool Suggestions = false);

    string? scopesSignature;

    /// <summary>
    /// 側欄的導覽（M§60.2、R85.1，2026-11-15 照 Mac 改，拿掉 SelectorBar）：搜尋框下面一直有「所有會議」「人物」兩列（圖示、名字、數量），
    /// 有資料夾時下面是「資料夾」那一組，最後是下面那一段的標題（選的那一項的名字）。點所有會議、資料夾回到會議模式，點人物切到人物模式。
    /// 垃圾桶裡、搜尋中不顯示（搜尋的結果是會議）。名字、數量沒變時只換選取，不重建（滑鼠停留、鍵盤焦點不會跳）
    /// </summary>
    void BuildFolders()
    {
        var hidden = State.ShowTrash || State.SearchActive;
        folders.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        // 垃圾桶裡：說明和「清空垃圾桶…」，垃圾桶是空的時停用（R88.3）
        trashHeader.Visibility = State.ShowTrash && !State.SearchActive ? Visibility.Visible : Visibility.Collapsed;
        emptyTrash.IsEnabled = State.Trash.Count > 0;
        if (hidden) return;
        var people = State.Sidebar == SidebarMode.People;
        var all = Organizing.Folders(State.Meetings, State.FolderRules);
        var signature = string.Join('\u001F', [State.Meetings.Count.ToString(), State.People.Count.ToString(), Ui.IsDark.ToString(),
            .. all.Select(f => $"{f.Name}\u001E{f.Count}\u001E{(State.RuleFor(f.Name) is { } rule ? RuleTip(rule) : "")}"),
            "series", State.SeriesCandidates.Count.ToString(), .. State.Series.Select(x => $"{x.Id}\u001E{x.Name}\u001E{x.FrequencyText}\u001E{State.SeriesCount(x.Id ?? 0)}")]);
        if (signature != scopesSignature)
        {
            scopesSignature = signature;
            updatingSelection = true;
            scopes.Items.Clear();
            scopes.Items.Add(ScopeRow(L.T("所有會議"), State.Meetings.Count, new Scope(false, null), new SymbolIcon(Symbol.AllApps)));
            scopes.Items.Add(ScopeRow(L.T("人物"), State.People.Count, new Scope(true, null), new SymbolIcon(Symbol.People)));
            // 定期會議（M§73）：人物下面、資料夾上面；沒有定期會議也沒有建議時整段不出現
            if (State.Series.Count > 0 || State.SeriesCandidates.Count > 0)
            {
                scopes.Items.Add(GroupHeader(L.T("定期會議")));
                if (State.SeriesCandidates.Count > 0) scopes.Items.Add(SuggestionsRow(State.SeriesCandidates.Count));
                foreach (var series in State.Series)
                {
                    var id = series.Id ?? 0;
                    var row = ScopeRow(series.Name, State.SeriesCount(id), new Scope(false, null, id), new SymbolIcon(Symbol.Sync));
                    // 滑過去看「每週一 10:00」
                    ToolTipService.SetToolTip(row, series.FrequencyText);
                    var menu = new MenuFlyout();
                    menu.Items.Add(Ui.Item(L.T("重新命名…"), () => _ = Dialogs.RenameSeries(this, id), icon: Symbol.Rename));
                    menu.Items.Add(Ui.Item(L.T("刪除定期會議（會議會保留）"), () => host.Controller.DeleteSeries(id), icon: Symbol.Delete));
                    row.ContextFlyout = menu;
                    scopes.Items.Add(row);
                }
            }
            if (all.Count > 0)
            {
                // 「資料夾」那一組的標題：不能選的項目（同搜尋結果的會議標題）
                scopes.Items.Add(GroupHeader(L.T("資料夾")));
                foreach (var (name, count) in all)
                {
                    // 有自動加入條件的資料夾換圖示（資料夾加齒輪，同 Mac 的 folder.badge.gearshape），滑過去看條件（M§72）
                    var rule = State.RuleFor(name);
                    var row = ScopeRow(name, count, new Scope(false, name), rule is null ? new SymbolIcon(Symbol.Folder) : RuleFolderIcon());
                    if (rule is not null) ToolTipService.SetToolTip(row, RuleTip(rule));
                    scopes.Items.Add(row);
                }
            }
            updatingSelection = false;
        }
        SyncScopeSelection();
        // 標題：「所有會議」（或資料夾、定期會議的名稱），分組時接「・依月份」；人物模式沒有「顯示方式」
        var scope = State.SeriesById(State.SeriesFilter)?.Name ?? State.FolderFilter ?? L.T("所有會議");
        var grouping = State.EffectiveGrouping;
        sectionTitle.Text = people ? L.T("人物") : grouping == MeetingGrouping.None ? scope : scope + "・" + grouping.Label();
        viewOptions.Visibility = people ? Visibility.Collapsed : Visibility.Visible;
        StyleViewOptions(viewOptions);
        var filtering = !people && State.Filter.IsActive;
        filterBar.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
        filterText.Text = filtering ? L.F("篩選：%@", State.FilterSummary) : "";
        filterEmpty.Visibility = filtering && State.FilteredMeetings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>側欄的選取跟著 State：人物、定期會議、資料夾或所有會議；一樣時不動</summary>
    void SyncScopeSelection()
    {
        var people = State.Sidebar == SidebarMode.People;
        var current = people ? new Scope(true, null) : State.SeriesFilter is long series ? new Scope(false, null, series) : new Scope(false, State.FolderFilter);
        var selected = scopes.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is Scope s && s == current);
        if (ReferenceEquals(scopes.SelectedItem, selected)) return;
        var was = updatingSelection;
        updatingSelection = true;
        scopes.SelectedItem = selected;
        updatingSelection = was;
    }

    string RuleTip(FolderRule rule) => Organizing.RuleSummary(rule, State.People, State.Templates, State.Series);

    /// <summary>一組的標題：不能選的項目（同搜尋結果的會議標題）</summary>
    static ListViewItem GroupHeader(string title) =>
        new() { Content = Ui.Caption(title), IsEnabled = false, IsTabStop = false, MinHeight = 28, Padding = new Thickness(12, 8, 12, 0) };

    /// <summary>資料夾加一個小齒輪（有自動加入條件，M§72）</summary>
    static FrameworkElement RuleFolderIcon()
    {
        var gear = new FontIcon
        {
            Glyph = "\uE713", FontSize = 11, Foreground = Ui.Celadon, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        // 齒輪放在右下角的小圓底上（側欄的底色），不會跟資料夾的線疊在一起
        var badge = new Border
        {
            Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5), Child = gear,
            Background = new SolidColorBrush(Ui.HexColor(Ui.IsDark ? "#2B2B2B" : "#F7F7F7")),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -4),
        };
        return new Grid { Width = 20, Height = 20, Children = { new SymbolIcon(Symbol.Folder), badge } };
    }

    /// <summary>「發現 N 組可能的定期會議」（M§73）：青瓷綠的燈泡和字，點了逐組確認</summary>
    ListViewItem SuggestionsRow(int count)
    {
        var text = L.F("發現 %lld 組可能的定期會議", count);
        var icon = new FontIcon { Glyph = "\uE82F", FontSize = 16, Foreground = Ui.Celadon };
        var item = new ListViewItem
        {
            Tag = new Scope(false, null, Suggestions: true), HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 36,
            Content = Ui.Bar(icon, Ui.Text(text, 14, color: Ui.Celadon, wrap: false), null, 10),
        };
        ToolTipService.SetToolTip(item, L.T("同一群人、每週同一天同一個時間開的會議。確認之後，下次的會議會自動加進去，整理時接上上次的待辦"));
        AutomationProperties.SetName(item, text);
        return item;
    }

    /// <summary>一列：圖示、名字、數量；資料夾的右鍵選單：自動加入的條件、重新命名、刪除</summary>
    ListViewItem ScopeRow(string name, int count, Scope scope, FrameworkElement icon)
    {
        var item = new ListViewItem
        {
            Tag = scope, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 36,
            Content = Ui.Bar(icon, Ui.Text(name, 14, wrap: false), Ui.Text(count.ToString(), 12, color: Ui.Secondary), 10),
        };
        AutomationProperties.SetName(item, name + MeetingLanguages.Ui("，", ", ") + count);
        if (scope.Folder is { } folder)
        {
            var menu = new MenuFlyout();
            menu.Items.Add(Ui.Item(State.RuleFor(folder) is null ? L.T("自動加入的條件…") : L.T("編輯自動加入的條件…"),
                () => _ = Dialogs.FolderRule(this, folder), icon: Symbol.Setting));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.Item(L.T("重新命名…"), () => _ = Dialogs.RenameFolder(this, folder)));
            menu.Items.Add(Ui.Item(L.T("刪除資料夾（會議會保留）"), () => host.Controller.DeleteFolder(folder)));
            item.ContextFlyout = menu;
        }
        return item;
    }

    // MARK: 顯示方式（R97、M§71）

    /// <summary>「顯示方式」按鈕：漏斗圖示，打開時才建選單（篩選的人、範本會變）</summary>
    Button ViewOptionsButton(bool trash)
    {
        var button = new Button();
        ConfigureViewOptions(button, trash);
        return button;
    }

    void ConfigureViewOptions(Button button, bool trash)
    {
        button.Content = new FontIcon { Glyph = "\uE71C", FontSize = 14 };
        button.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(button, trash ? L.T("排序方式") : L.T("排序、分組、篩選會議"));
        AutomationProperties.SetName(button, L.T("顯示方式"));
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Opening += (_, _) => Ui.Guard(() => FillViewOptions(menu, trash));
        button.Flyout = menu;
        StyleViewOptions(button, force: true);
    }

    /// <summary>篩選中變成實心的青瓷綠（強調色的按鈕），沒篩選時沒有框、灰色的圖示（同 Mac）；垃圾桶裡不篩選，一直是灰的</summary>
    void StyleViewOptions(Button button, bool force = false)
    {
        var active = button != viewOptions ? false : State?.Filter.IsActive == true;
        if (!force && button.Tag is bool shown && shown == active) return;
        button.Tag = active;
        if (active)
        {
            button.Resources.Clear();
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.ForegroundProperty);
            button.ClearValue(Control.BorderThicknessProperty);
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accent) button.Style = accent;
        }
        else
        {
            button.ClearValue(FrameworkElement.StyleProperty);
            Ui.Borderless(button, Ui.Secondary);
        }
        button.Padding = new Thickness(7, 5, 7, 5);
    }

    /// <summary>
    /// 三段（同 Mac 的 MeetingListMenu）：排序方式（單選）、分組方式（單選，看某個資料夾時沒有「依資料夾」）、篩選（打勾、可以多選；
    /// 有誰參加、範本是子選單），篩選中最後多「清除篩選」。垃圾桶裡只有排序
    /// </summary>
    void FillViewOptions(MenuFlyout menu, bool trash)
    {
        menu.Items.Clear();
        menu.Items.Add(Ui.Item(L.T("排序方式"), () => { }, enabled: false));
        foreach (var sort in MeetingSorts.All)
            menu.Items.Add(Radio(sort.Label(), "sort", State.Sort == sort, () => State.SetSort(sort)));
        if (trash) return;
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item(L.T("分組方式"), () => { }, enabled: false));
        var effective = State.EffectiveGrouping;
        foreach (var grouping in MeetingList.Available(State.FolderFilter))
            menu.Items.Add(Radio(grouping.Label(), "group", effective == grouping, () => State.SetGrouping(grouping)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item(L.T("篩選"), () => { }, enabled: false));
        var f = State.Filter;
        menu.Items.Add(Ui.Check(L.T("有待確認的"), f.NeedsReview, () => State.SetFilter(State.Filter with { NeedsReview = !State.Filter.NeedsReview })));
        // 沒有設定「我」時不出現（R97.3）
        if (State.MyName is not null)
            menu.Items.Add(Ui.Check(L.T("有我的待辦的"), f.MyActionItems, () => State.SetFilter(State.Filter with { MyActionItems = !State.Filter.MyActionItems })));
        if (State.People.Count > 0)
        {
            var who = new MenuFlyoutSubItem { Text = L.T("有誰參加"), Icon = new SymbolIcon(Symbol.People) };
            who.Items.Add(Radio(L.T("不限"), "person", f.PersonId is null, () => State.SetFilter(State.Filter with { PersonId = null })));
            who.Items.Add(new MenuFlyoutSeparator());
            foreach (var person in State.People)
            {
                var id = person.Id;
                who.Items.Add(Radio(person.IsMe ? L.F("%@（我）", person.Name) : person.Name, "person", f.PersonId == id,
                    () => State.SetFilter(State.Filter with { PersonId = id })));
            }
            menu.Items.Add(who);
        }
        var templates = new MenuFlyoutSubItem { Text = L.T("範本"), Icon = new SymbolIcon(Symbol.Document) };
        templates.Items.Add(Radio(L.T("不限"), "template", f.TemplateId is null, () => State.SetFilter(State.Filter with { TemplateId = null })));
        templates.Items.Add(new MenuFlyoutSeparator());
        foreach (var t in State.Templates)
        {
            var id = t.Id;
            templates.Items.Add(Radio(t.DisplayName(), "template", f.TemplateId == id, () => State.SetFilter(State.Filter with { TemplateId = id })));
        }
        menu.Items.Add(templates);
        if (f.IsActive)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.Item(L.T("清除篩選"), () => State.SetFilter(new MeetingFilter()), icon: Symbol.Clear));
        }
    }

    static RadioMenuFlyoutItem Radio(string text, string group, bool on, Action click)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = group, IsChecked = on };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>一列會議：狀態文字、進度條直接改，不用整個列表重建</summary>
    sealed class RowView(ListViewItem item, StackPanel column, TextBlock status)
    {
        public ListViewItem Item { get; } = item;
        public StackPanel Column { get; } = column;
        public TextBlock Status { get; } = status;
        public ProgressBar? Progress { get; set; }
    }

    readonly Dictionary<long, RowView> rowViews = [];
    string? listSignature;

    /// <summary>
    /// 會議列表。哪幾場、順序、標題、日期、資料夾都沒變時，只更新每一列的狀態和進度（轉錄、整理中每秒一次）：
    /// 整個清掉重建會一直閃（2026-10-21 使用者在虛擬機看到），捲動位置、滑鼠停留的效果也會重來
    /// </summary>
    void BuildList()
    {
        var signature = ListSignature();
        if (signature is not null && signature == listSignature)
        {
            UpdateRows();
            return;
        }
        listSignature = signature;
        rowViews.Clear();
        updatingSelection = true;
        list.Items.Clear();
        // 會議列表可以多選（Ctrl、Shift 點選，R88.1）；搜尋結果、人物只能選一個
        var mode = State.ShowsPeople || State.SearchActive ? ListViewSelectionMode.Single : ListViewSelectionMode.Extended;
        if (list.SelectionMode != mode) list.SelectionMode = mode;
        if (State.ShowsPeople) BuildPeopleList();
        else if (State.SearchActive)
        {
            if (State.SearchResults.Count == 0) list.Items.Add(new ListViewItem { Content = Ui.Text(L.F("找不到「%@」", State.SearchQuery), color: Ui.Secondary), IsEnabled = false });
            foreach (var group in State.SearchResults.GroupBy(h => h.MeetingId))
            {
                var meeting = State.Meetings.FirstOrDefault(m => m.Id == group.Key)?.Meeting;
                list.Items.Add(new ListViewItem { Content = Ui.Text(meeting?.DisplayTitle ?? L.T("會議"), 13, bold: true), IsEnabled = false });
                foreach (var hit in group)
                {
                    var snippet = Ui.Text(hit.Snippet, 13, maxLines: 3);
                    if (meeting is not null) snippet.Language = Ui.ContentLanguage(meeting.MeetingLanguage);
                    var hitItem = new ListViewItem { Tag = hit, Content = Ui.Column(2, Ui.Caption(KindLabel(hit.Kind)), snippet) };
                    AutomationProperties.SetName(hitItem, KindLabel(hit.Kind) + MeetingLanguages.Ui("：", ": ") + hit.Snippet);
                    list.Items.Add(hitItem);
                }
            }
        }
        else
        {
            // 排序、篩選過的列表；分組時每組一段，標題是月份或資料夾名稱（R97.2、M§71）
            var groups = State.Groups;
            var grouped = State.EffectiveGrouping != MeetingGrouping.None;
            if (State.ShowTrash && State.Trash.Count == 0) list.Items.Add(new ListViewItem { Content = Ui.Text(L.T("垃圾桶是空的"), color: Ui.Secondary), IsEnabled = false });
            foreach (var group in groups)
            {
                if (grouped)
                {
                    var header = new ListViewItem
                    {
                        Content = Ui.Text(group.Title, 12, bold: true, color: Ui.Secondary, wrap: false), IsEnabled = false, IsTabStop = false,
                        MinHeight = 28, Padding = new Thickness(12, 10, 12, 0),
                    };
                    AutomationProperties.SetName(header, group.Title);
                    list.Items.Add(header);
                }
                foreach (var row in group.Rows)
                {
                    var view = MeetingRow(row);
                    rowViews[row.Id] = view;
                    SetStatus(view, row);
                    view.Item.ContextFlyout = RowMenu(row.Id);
                    list.Items.Add(view.Item);
                }
            }
            SyncSelection();
        }
        updatingSelection = false;
    }

    /// <summary>列表的選取跟著 State：多選時是那幾場，不然是目前這場；一樣時不動（滑鼠、鍵盤焦點不會跳）</summary>
    void SyncSelection()
    {
        var multi = State.MultiSelected;
        var wanted = multi.Count > 1 ? multi.ToHashSet() : State.Selection is long id && rowViews.ContainsKey(id) ? [id] : new HashSet<long>();
        var current = list.SelectedItems.OfType<ListViewItem>().Select(i => i.Tag).OfType<long>().ToHashSet();
        if (current.SetEquals(wanted)) return;
        var was = updatingSelection;
        updatingSelection = true;
        if (wanted.Count == 1 && rowViews.TryGetValue(wanted.First(), out var only)) list.SelectedItem = only.Item;
        else
        {
            list.SelectedItems.Clear();
            foreach (var row in State.FilteredMeetings)
                if (wanted.Contains(row.Id) && rowViews.TryGetValue(row.Id, out var view)) list.SelectedItems.Add(view.Item);
        }
        updatingSelection = was;
    }

    /// <summary>人物列表（M§60.2、R85.1）：頭像、名字（我的話後面「（我）」）、「N 場會議・最近 &lt;日期&gt;」</summary>
    void BuildPeopleList()
    {
        if (State.People.Count == 0)
        {
            var empty = Ui.Text(L.T("在逐字稿點「說話者 1」改成名字，或到設定錄一段你自己的聲音，拾音就會記住這個人。"), 13, color: Ui.Secondary);
            empty.TextAlignment = TextAlignment.Center;
            list.Items.Add(new ListViewItem { Content = empty, IsEnabled = false, Padding = new Thickness(16, 24, 16, 0) });
            return;
        }
        foreach (var person in State.People)
        {
            var name = person.IsMe ? L.F("%@（我）", person.Name) : person.Name;
            var summary = Views.PersonPage.SummaryLine(person);
            var avatar = Ui.Avatar(person.Name, Ui.Person(person.Name), 26, person.IsMe);
            avatar.VerticalAlignment = VerticalAlignment.Center;
            var text = Ui.Column(2, Ui.Text(name, 14, wrap: false), Ui.Text(summary, 12, color: Ui.Secondary, wrap: false));
            var row = Ui.Split(avatar, text, 10);
            row.Padding = new Thickness(0, 4, 0, 4);
            var item = new ListViewItem { Tag = person, Content = row };
            // 列裡是好幾個元件：螢幕閱讀器、UI Automation 讀的名稱
            AutomationProperties.SetName(item, name + MeetingLanguages.Ui("，", ", ") + summary);
            // 右鍵（R85.7）：跟人物頁的「更多」同一份（改名…、合併到 ▸、移除這個人…），打開時才建
            var menu = new MenuFlyout();
            var personId = person.Id;
            menu.Opening += (_, _) => Ui.Guard(() =>
            {
                menu.Items.Clear();
                if (State.People.FirstOrDefault(p => p.Id == personId) is { } current)
                    foreach (var entry in personPage.MenuItems(current)) menu.Items.Add(entry);
            });
            foreach (var entry in personPage.MenuItems(person)) menu.Items.Add(entry);
            item.ContextFlyout = menu;
            list.Items.Add(item);
            if (person.Id == State.SelectedPerson) list.SelectedItem = item;
        }
    }

    /// <summary>列表的樣子；搜尋結果（不顯示狀態）每次重建，回傳 null</summary>
    string? ListSignature()
    {
        if (State.SearchActive) return null;
        if (State.ShowsPeople)
        {
            // 人物：名字、場數、日期（選了誰只換選取，UpdateRows；每秒的進度更新不會重建）
            var people = new System.Text.StringBuilder("people");
            foreach (var p in State.People)
                people.Append('\u001E').Append(p.Id).Append('\u001F').Append(p.Name).Append('\u001F').Append(p.MeetingCount)
                    .Append('\u001F').Append(p.LastMeetingDate).Append('\u001F').Append(p.IsMe);
            return people.ToString();
        }
        var text = new System.Text.StringBuilder(State.ShowTrash ? "trash" : "meetings");
        // 分組（R97.2）：每組的標題跟著分組方式、語言
        text.Append('\u001D').Append(State.EffectiveGrouping.Code()).Append('\u001D').Append(State.Sort.Code());
        // 右鍵選單的「移到資料夾」列出所有資料夾：資料夾變了也要重建
        foreach (var (name, _) in Organizing.Folders(State.Meetings, State.FolderRules)) text.Append('\u001D').Append(name);
        foreach (var row in State.FilteredMeetings)
            text.Append('\u001E').Append(row.Id).Append('\u001F').Append(row.Meeting.DisplayTitle).Append('\u001F').Append(row.Meeting.MeetingDate)
                .Append('\u001F').Append(row.Meeting.Folder).Append('\u001F').Append(row.Meeting.Language);
        return text.ToString();
    }

    void UpdateRows()
    {
        if (State.ShowsPeople)
        {
            // 選的人換了只換選取，不重建列表（重建時焦點離開列表，按 Delete 移除收不到，R85.7）
            var wanted = list.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is PersonOverview p && p.Id == State.SelectedPerson);
            if (!ReferenceEquals(list.SelectedItem, wanted))
            {
                updatingSelection = true;
                list.SelectedItem = wanted;
                updatingSelection = false;
            }
            return;
        }
        updatingSelection = true;
        foreach (var row in State.FilteredMeetings)
            if (rowViews.TryGetValue(row.Id, out var view)) SetStatus(view, row);
        SyncSelection();
        updatingSelection = false;
    }

    static string KindLabel(SearchKind kind) => kind switch
    {
        SearchKind.Title => L.T("標題"),
        SearchKind.Segment => L.T("逐字稿"),
        SearchKind.Notes => L.T("會議紀錄"),
        _ => L.T("我的筆記"),
    };

    RowView MeetingRow(MeetingRow row)
    {
        var title = Ui.Text(row.Meeting.DisplayTitle, 14, wrap: false);
        title.Language = Ui.ContentLanguage(row.Meeting.MeetingLanguage);
        var status = Ui.Text("", 12, wrap: false);
        var info = Ui.Row(8, Ui.Text(ShortDate(row.Meeting.MeetingDate), 12, color: Ui.Secondary, wrap: false), status);
        var column = Ui.Column(3, title, info);
        column.Padding = new Thickness(0, 3, 0, 3);
        return new RowView(new ListViewItem { Tag = row.Id, Content = column }, column, status);
    }

    /// <summary>狀態、顏色、轉錄的進度條，只在值變了才設（每秒呼叫一次）</summary>
    void SetStatus(RowView view, MeetingRow row)
    {
        var status = host.Controller.Status(row);
        if (view.Status.Text != status.Label)
        {
            view.Status.Text = status.Label;
            view.Status.Foreground = status switch
            {
                MeetingStatus.Recording or MeetingStatus.Failed => Ui.Danger,
                MeetingStatus.Queued or MeetingStatus.Transcribing or MeetingStatus.Summarizing or MeetingStatus.Review => Ui.Signal,
                _ => Ui.Secondary,
            };
            // 螢幕閱讀器、UI Automation 讀的名稱（列裡是好幾個文字元件，ListViewItem 本身沒有名稱）
            AutomationProperties.SetName(view.Item, string.Join(MeetingLanguages.Ui("，", ", "),
                row.Meeting.DisplayTitle, ShortDate(row.Meeting.MeetingDate), status.Label));
        }
        if (status is MeetingStatus.Transcribing { Progress: double p })
        {
            if (view.Progress is null)
            {
                view.Progress = new ProgressBar { Maximum = 100, Foreground = Ui.Signal };
                view.Column.Children.Add(view.Progress);
            }
            view.Progress.Value = p * 100;
        }
        else if (view.Progress is not null)
        {
            view.Column.Children.Remove(view.Progress);
            view.Progress = null;
        }
    }

    static string ShortDate(string date)
    {
        var parts = date.Split('-');
        if (parts.Length != 3) return date;
        return parts[0] == DateTime.Now.Year.ToString() ? $"{parts[1]}/{parts[2]}" : $"{parts[0]}/{parts[1]}/{parts[2]}";
    }

    /// <summary>
    /// 一列會議的右鍵選單（R88.1、M§62）：打開時才決定是哪幾場：在多選裡的列是全部，不在多選裡只有那一列（WinUI 的右鍵不改選取）
    /// </summary>
    MenuFlyout RowMenu(long id)
    {
        var menu = new MenuFlyout();
        FillRowMenu(menu, id);
        menu.Opening += (_, _) => Ui.Guard(() => FillRowMenu(menu, id));
        return menu;
    }

    void FillRowMenu(MenuFlyout menu, long id)
    {
        menu.Items.Clear();
        var multi = State.MultiSelected;
        List<long> ids = multi.Count > 1 && multi.Contains(id) ? multi : [id];
        if (State.ShowTrash)
        {
            menu.Items.Add(Ui.Item(L.T("還原"), () => host.Controller.Restore(ids)));
            menu.Items.Add(Ui.Item(L.T("永久刪除…"), () => _ = ConfirmPurge(ids)));
            return;
        }
        menu.Items.Add(FolderSubMenu(ids, CommonFolder(ids)));
        foreach (var item in SeriesMenuItems(ids)) menu.Items.Add(item);
        menu.Items.Add(new MenuFlyoutSeparator());
        // 一場直接移（可以還原）；好幾場先確認（M§62）
        menu.Items.Add(ids.Count == 1 ? Ui.Item(L.T("移到垃圾桶"), () => host.Controller.MoveToTrash(ids), icon: Symbol.Delete)
            : Ui.Item(L.F("把 %lld 場會議移到垃圾桶…", ids.Count), () => _ = ConfirmTrash(ids), icon: Symbol.Delete));
    }

    /// <summary>這幾場會議都在同一個資料夾時是那個資料夾（空字串是不在資料夾裡），不一樣時是 null</summary>
    string? CommonFolder(IReadOnlyList<long> ids) => AppController.CommonFolder(State.ShowTrash ? State.Trash : State.Meetings, ids);

    /// <summary>
    /// 會議的右鍵選單（同 Mac 的 SeriesMenu，M§73）：「加到定期會議 ▸」列出全部系列（都在同一個系列時不列它）；
    /// 都在同一個系列時多「移出「產品週會」」。多選時全部；手動加入不檢查條件
    /// </summary>
    List<MenuFlyoutItemBase> SeriesMenuItems(IReadOnlyList<long> ids)
    {
        var items = new List<MenuFlyoutItemBase>();
        var current = Organizing.CommonSeries(State.Meetings, ids.ToList());
        if (State.Series.Count > 0)
        {
            var sub = new MenuFlyoutSubItem { Text = L.T("加到定期會議"), Icon = new SymbolIcon(Symbol.Sync) };
            foreach (var series in State.Series.Where(s => s.Id != current))
            {
                var id = series.Id;
                sub.Items.Add(Ui.Item(series.Name, () => host.Controller.SetSeries(ids, id)));
            }
            if (sub.Items.Count > 0) items.Add(sub);
        }
        if (State.SeriesById(current) is { } inSeries)
            items.Add(Ui.Item(L.F("移出「%@」", inSeries.Name), () => host.Controller.SetSeries(ids, null)));
        return items;
    }

    /// <summary>「移到資料夾」選單：既有的資料夾、新資料夾、移出資料夾（R9.2）</summary>
    public MenuFlyoutSubItem FolderMenu(long id, string current) => FolderSubMenu([id], current);

    MenuFlyoutSubItem FolderSubMenu(IReadOnlyList<long> ids, string? current)
    {
        var sub = new MenuFlyoutSubItem { Text = L.T("移到資料夾"), Icon = new SymbolIcon(Symbol.Folder) };
        foreach (var item in FolderMenuItems(ids, current)) sub.Items.Add(item);
        return sub;
    }

    /// <summary>
    /// 「移到資料夾」的項目（同 Mac 的 FolderMenu，M§62）：都在同一個資料夾（current）時不列那個資料夾、最後是「移出「名稱」」；
    /// 在不同的資料夾（current 是 null，多選時）列全部、最後是「移出資料夾」
    /// </summary>
    List<MenuFlyoutItemBase> FolderMenuItems(IReadOnlyList<long> ids, string? current)
    {
        var items = new List<MenuFlyoutItemBase>();
        // 有自動加入條件的資料夾沒有會議也列出來（M§72）
        var others = Organizing.Folders(State.Meetings, State.FolderRules).Where(f => f.Name != current).ToList();
        foreach (var (name, _) in others) items.Add(Ui.Item(name, () => host.Controller.MoveToFolder(ids, name)));
        if (others.Count > 0) items.Add(new MenuFlyoutSeparator());
        items.Add(Ui.Item(L.T("新資料夾…"), () => _ = Dialogs.NewFolder(this, ids)));
        if (current is null) items.Add(Ui.Item(L.T("移出資料夾"), () => host.Controller.MoveToFolder(ids, "")));
        else if (current.Length > 0) items.Add(Ui.Item(L.F("移出「%@」", current), () => host.Controller.MoveToFolder(ids, "")));
        return items;
    }

    void BuildFooter()
    {
        footer.Children.Clear();
        if (host.Controller.ImportingCount > 0)
            footer.Children.Add(Ui.Row(8, new ProgressRing { Width = 16, Height = 16, IsActive = true },
                Ui.Text(L.F("正在匯入 %lld 個檔案…", host.Controller.ImportingCount), 13, color: Ui.Secondary)));
        // 模型下載中（M§7）
        foreach (var model in Enum.GetValues<ModelStore.Model>())
            if (host.Models.StateOf(model) is ModelStore.State.Downloading { Total: > 0 } d)
                footer.Children.Add(Ui.Column(2, Ui.Text(L.F("正在下載%@", ModelStore.Info(model).Name), 12, color: Ui.Secondary, maxLines: 1),
                    new ProgressBar { Maximum = d.Total, Value = d.Received }));
        // 更新下載中（R10.6、design-windows §14.2）：Velopack 只給百分比；下載好、驗證、套用前是「正在準備安裝」
        if (host.Updater?.Current is Updater.State.Downloading u)
            footer.Children.Add(Ui.Column(2, Ui.Text(L.F("正在下載更新 %lld%%", (long)u.Percent), 12, color: Ui.Secondary, maxLines: 1),
                new ProgressBar { Maximum = 100, Value = u.Percent }));
        else if (host.Updater?.Current is Updater.State.Installing)
            footer.Children.Add(Ui.Row(8, new ProgressRing { Width = 16, Height = 16, IsActive = true }, Ui.Text(L.T("正在準備安裝…"), 13, color: Ui.Secondary)));
        var trash = Ui.Link(State.ShowTrash ? L.T("回到所有會議") : L.F("垃圾桶（%lld）", State.Trash.Count), () => State.ToggleTrash());
        var import = State.ShowTrash ? null : Ui.Link(L.T("匯入"), () => _ = PickAndImport());
        if (import is not null) ToolTipService.SetToolTip(import, L.T("匯入錄音、影片、逐字稿或 .shiyin 封包（Ctrl+O）"));
        footer.Children.Add(Ui.Bar(trash, null, import));
    }

    FrameworkElement EmptyDetail()
    {
        StackPanel content;
        if (State.ShowTrash)
            content = Ui.Column(8, Ui.Text(L.T("垃圾桶"), 20, bold: true), Ui.Text(L.T("選一場會議來還原或永久刪除。"), color: Ui.Secondary));
        else if (State.Meetings.Count == 0)
            content = Ui.Column(12, Ui.Text(L.T("還沒有會議"), 20, bold: true), Ui.Text(L.T("按「錄音」開始，或把錄音檔拖進來。"), color: Ui.Secondary),
                Ui.Row(8, Ui.Button(L.T("開始錄音"), () => host.Controller.StartRecording(State.CurrentFolder), accent: true),
                    Ui.Button(L.T("匯入錄音…"), () => _ = PickAndImport()), Ui.Button(L.T("看使用說明"), () => App.Current.ShowGuide())));
        else
            content = Ui.Column(8, Ui.Text(L.T("從左邊選一場會議"), 20, bold: true), Ui.Text(L.T("也可以把新的錄音檔拖進來。"), color: Ui.Secondary));
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Children = { content } };
    }

    // MARK: 動作

    public void Reveal(long id)
    {
        State.Reveal(id);
        AppWindow.Show();
        Activate();
    }

    void OpenSearchHit(SearchHit hit)
    {
        State.Select(hit.MeetingId, keepSearch: true);
        switch (hit.Kind)
        {
            case SearchKind.Segment:
                State.SetTab(DetailTab.Transcript);
                detailView.Flash(hit.Ref, seek: true);
                break;
            case SearchKind.UserNotes: State.OpenTab(DetailTab.Transcript, expandNotes: true); break;
            default: State.SetTab(DetailTab.Summary); break;
        }
    }

    public async Task PickAndImport()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        foreach (var ext in Importer.MediaExtensions.Concat(Importer.TranscriptExtensions).Concat(Importer.MarkdownExtensions).Append("shiyin"))
            picker.FileTypeFilter.Add("." + ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) RequestImport(files.Select(f => f.Path).ToList());
    }

    /// <summary>「加入文件…」（R61.1）：選檔後加到這場會議</summary>
    public async Task PickDocuments(long meetingId)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        foreach (var ext in DocumentReader.PickerExtensions) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await host.Controller.AddDocuments(files.Select(f => f.Path).ToList(), meetingId);
    }

    public void RequestImport(IReadOnlyList<string> files)
    {
        var media = host.Controller.RequestImport(files, State.CurrentFolder, State.Selection);
        if (media.Count > 0) _ = Dialogs.Import(this, media);
    }

    // 確認對話框開著時要處理的會議、那一句（DEBUG 的 state，同 Mac 的 deleteTargets、confirmPurge、confirmEmptyTrash、splitMeetingRequest）
    public List<long> DeleteTargets { get; private set; } = [];
    public List<long> PurgeTargets { get; private set; } = [];
    public bool ConfirmingEmptyTrash { get; private set; }
    public long? SplitMeetingRequest { get; private set; }

    public Task ConfirmTrash(long id) => ConfirmTrash([id]);

    /// <summary>移到垃圾桶前確認，寫出場數（R88.2）；可以還原，主要按鈕是預設</summary>
    public async Task ConfirmTrash(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        DeleteTargets = [.. ids];
        bool ok;
        try
        {
            ok = await Dialogs.Confirm(this, ids.Count > 1 ? L.F("把 %lld 場會議移到垃圾桶？", ids.Count) : L.T("把這場會議移到垃圾桶？"),
                L.T("30 天內都可以從垃圾桶還原。"), L.T("移到垃圾桶"));
        }
        finally { DeleteTargets = []; }
        if (ok) host.Controller.MoveToTrash(ids);
    }

    public Task ConfirmPurge(long id) => ConfirmPurge([id]);

    /// <summary>永久刪除前確認，寫出場數（R88.2）；沒辦法還原，預設按鈕是「取消」</summary>
    public async Task ConfirmPurge(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        PurgeTargets = [.. ids];
        bool ok;
        try
        {
            ok = await Dialogs.Confirm(this, ids.Count > 1 ? L.F("永久刪除 %lld 場會議？", ids.Count) : L.T("永久刪除這場會議？"),
                L.T("會議紀錄、逐字稿和錄音都會刪掉，沒辦法還原。"), L.T("永久刪除"), destructive: true);
        }
        finally { PurgeTargets = []; }
        // 正在播的那場：AppController 先發 ReleasingAudio，MainState 停止播放
        if (ok) host.Controller.DeletePermanently(ids);
    }

    /// <summary>「清空垃圾桶…」（R88.3）：沒辦法還原，預設按鈕是「取消」</summary>
    public async Task ConfirmEmptyTrash()
    {
        var count = State.Trash.Count;
        if (count == 0) return;
        ConfirmingEmptyTrash = true;
        bool ok;
        try
        {
            ok = await Dialogs.Confirm(this, L.F("永久刪除垃圾桶裡的 %lld 場會議？", count), L.T("會議紀錄、逐字稿和錄音都會刪掉，沒辦法還原。"),
                L.T("清空垃圾桶"), destructive: true);
        }
        finally { ConfirmingEmptyTrash = false; }
        if (ok) host.Controller.EmptyTrash();
    }

    /// <summary>確認對話框開著時要移除的人（DEBUG 的 state，同 Mac 的 confirmRemovePerson）</summary>
    public long? RemovingPerson { get; private set; }

    /// <summary>
    /// 移除一個人（R85.7、M§60.2）：人物頁的「更多」、側欄人物列的右鍵、Delete。會從「人物」拿掉、刪掉聲音特徵，會議裡已經帶入的名字留著
    /// （跟設定頁的「刪除聲音特徵」同一個動作）；預設按鈕是「取消」。移除的是正在看的人時回到沒選人
    /// </summary>
    public async Task ConfirmRemovePerson(long id)
    {
        if (RemovingPerson is not null || State.People.FirstOrDefault(p => p.Id == id) is not { } person) return;
        RemovingPerson = id;
        bool ok;
        try
        {
            ok = await Dialogs.Confirm(this, L.F("移除「%@」？", person.Name),
                L.T("會從「人物」拿掉，也會刪掉他的聲音特徵。會議裡已經帶入的名字會留著，之後的會議不會再自動認出他。"), L.T("移除"), destructive: true);
        }
        finally { RemovingPerson = null; }
        if (!ok) return;
        var selected = State.SelectedPerson == id;
        host.Controller.DeletePerson(id);
        if (selected) State.SelectPerson(null);
    }

    /// <summary>「改名…」（DEBUG 的 personrenameui，同側欄右鍵）：選那個人、打開名字旁鉛筆下面的輸入框</summary>
    public void RenamePersonUi(long id)
    {
        State.SelectPerson(id);
        personPage.RequestRename(id);
    }

    public bool PersonRenameOpen => personPage.RenameOpen;

    /// <summary>「從這裡分成兩場…」（R90.1、M§64）：寫出從幾分幾秒開始、會怎麼分；分開後沒辦法合併回來（D170），預設按鈕是「取消」</summary>
    public async Task ConfirmSplitMeeting(long meetingId, Segment segment)
    {
        if (segment.Id is not long segmentId) return;
        SplitMeetingRequest = segmentId;
        bool ok;
        try
        {
            ok = await Dialogs.Confirm(this, L.F("從 %@ 這句開始分成另一場會議？", TimeFormat.String(segment.Start)),
                L.T("這句和之後的逐字稿、錄音、標記重點變成新的一場，前面的留在這場；我的筆記、參考文件、提問留在這場。兩場都要重新整理會議紀錄。分開後沒辦法合併回來。"),
                L.T("分成兩場"), destructive: true);
        }
        finally { SplitMeetingRequest = null; }
        if (ok) await host.Controller.SplitMeeting(meetingId, segmentId);
    }

    public void ShowNotice(string message)
    {
        toast.Severity = InfoBarSeverity.Informational;
        toast.Message = message;
        toast.IsOpen = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    bool recorderAlertOpen;

    /// <summary>
    /// 錄音沒辦法開始、中途出問題（同 Mac 的「錄音的提醒」）、沒有麥克風權限（R27 R1.8，可以直接打開設定的那一頁）。
    /// Recorder 只記下原因，這裡顯示一次就清掉；一次只開一個對話框
    /// </summary>
    async void ShowRecorderAlerts()
    {
        var r = host.Recorder;
        if (recorderAlertOpen || XamlRoot is null) return;
        if (r.PermissionDenied)
        {
            r.PermissionDenied = false;
            recorderAlertOpen = true;
            if (await Dialogs.Confirm(this, L.T("拾音沒有麥克風權限"),
                    L.T("請到「設定 → 隱私權與安全性 → 麥克風」打開「麥克風存取」和「讓傳統型應用程式存取您的麥克風」，再重新開始錄音。"), L.T("打開設定")))
                Platform.Shell.OpenMicrophoneSettings();
            recorderAlertOpen = false;
        }
        else if (r.FailureMessage is { } message)
        {
            r.FailureMessage = null;
            recorderAlertOpen = true;
            await Dialogs.Alert(this, L.T("錄音的提醒"), message);
            recorderAlertOpen = false;
        }
        else return;
        ShowRecorderAlerts();
    }

    /// <summary>WinRT 的例外會把同一句錯誤描述再接在訊息後面一次：重複的行只留一行</summary>
    public void ShowError(string message) => _ = Dialogs.Alert(this, L.T("出了一點問題"),
        string.Join("\n", message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()));
}
