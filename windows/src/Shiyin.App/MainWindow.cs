using Microsoft.UI.Input;
using Microsoft.UI.Text;
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
    /// <summary>會議列表的標題（「其他會議」）：跟列一樣大的灰字、不加粗（R101.4）</summary>
    readonly TextBlock sectionTitle = Ui.Text("", 14, color: Ui.Secondary, wrap: false);
    /// <summary>
    /// 會議那一段的標題列（R97.1、M§71）：左邊「所有會議」（或資料夾、定期會議的名稱）・分組方式，右邊「顯示方式」（排序、分組、篩選）；
    /// 篩選中下面一列「篩選：…」＋✕，篩選後沒有會議時說明＋「清除篩選」。都放在列表外面（停用的 ListViewItem 裡的按鈕按不了）
    /// </summary>
    readonly Button viewOptions = new();
    /// <summary>標題列左邊的名稱：點了收起、展開會議列表（人物模式是人物列表，R101、design-windows §11.34）</summary>
    readonly Button sectionToggle = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    readonly FontIcon sectionChevron = ChevronIcon(true);
    bool? sectionOpenShown;
    bool sectionHovering;

    void UpdateSectionChevron() =>
        sectionChevron.Opacity = sectionOpenShown == false || sectionHovering || sectionToggle.FocusState == FocusState.Keyboard ? 1 : 0;
    readonly Grid sectionHeader;
    readonly Grid filterBar;
    readonly FontIcon filterFunnel = new() { Glyph = "\uE71C", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>
    /// 建構時就做好、之後不重建的元件（側欄的標題列、篩選列、垃圾桶那一行）：換深淺色時重新給顏色。
    /// 文字的灰色是 app 主題的資源，拿到的是當時那一份，開著 app 換成淺色時還是深色用的淺灰（看不見）；
    /// 青瓷綠看 Ui.SetTheme，建構時還沒設（深色模式開 app 時漏斗是淺色用的深綠）。2026-11-16 虛擬機的深色巡檢
    /// </summary>
    readonly List<Action> restyle = [];
    readonly TextBlock filterText = Ui.Text("", 13, maxLines: 2);
    readonly StackPanel filterEmpty;
    readonly StackPanel folders = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 2) };
    /// <summary>垃圾桶裡列表上方的一行：說明和「清空垃圾桶…」（R88.3）。放在列表外面：停用的 ListViewItem 裡的按鈕按不了</summary>
    readonly Grid trashHeader;
    readonly Button emptyTrash;
    readonly PersonPage personPage;
    readonly StackPanel footer = new() { Spacing = 6, Margin = new Thickness(12, 6, 12, 10) };
    readonly Border detailHost = new();
    readonly InfoBar toast = new() { IsClosable = true, Severity = InfoBarSeverity.Informational, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 80), MaxWidth = 560 };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    /// <summary>查新的分享（R125.5）：每 15 分鐘（還有打開時、回到前景）</summary>
    readonly DispatcherTimer shareTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    bool wasBusy;
    readonly DetailView detailView;
    /// <summary>待確認的回答（M§31.2）：DEBUG 的控制通道用</summary>
    public Views.ReviewDraft ReviewDraft => detailView.ReviewDraft;
    public MindMapZoom MindMapZoom => detailView.MindMapZoom;
    public NotesTabs NotesTabs => detailView.Notes;
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
        emptyTrash = Ui.Button(L.T("清空垃圾桶…"), () => _ = ConfirmEmptyTrash());
        emptyTrash.VerticalAlignment = VerticalAlignment.Center;
        // 垃圾桶裡也照排序（R97.4）：「顯示方式」只有排序那一段（Windows 沒有選單列，放在說明那一行）
        var trashOptions = ViewOptionsButton(trash: true);
        var trashNote = Ui.Caption(L.T("垃圾桶裡的會議 30 天後會永久刪除"));
        trashHeader = Ui.Bar(null, trashNote, Ui.Row(4, trashOptions, emptyTrash));
        ConfigureViewOptions(viewOptions, trash: false);
        sectionTitle.VerticalAlignment = VerticalAlignment.Center;
        sectionTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        sectionTitle.TextWrapping = TextWrapping.NoWrap;
        // 名稱和後面的箭頭是一顆沒有框的按鈕（R101）：短的時候箭頭緊接在字後面，長的時候名稱省略號、箭頭留著；右邊的「顯示方式」不動
        sectionToggle.Content = Ui.Inline(sectionTitle, sectionChevron, 4, VerticalAlignment.Center);
        sectionToggle.Click += (_, _) => ToggleSection(State.Sidebar == SidebarMode.People ? "people" : "meetings");
        // 箭頭只在滑過、有鍵盤焦點或收起來時出現（R101.1）
        sectionToggle.PointerEntered += (_, _) => { sectionHovering = true; UpdateSectionChevron(); };
        sectionToggle.PointerExited += (_, _) => { sectionHovering = false; UpdateSectionChevron(); };
        sectionToggle.GotFocus += (_, _) => UpdateSectionChevron();
        sectionToggle.LostFocus += (_, _) => UpdateSectionChevron();
        sectionHeader = Ui.Bar(null, sectionToggle, viewOptions, 4);
        // 按鈕的左右內距 6：名稱跟原本（沒有按鈕時）的位置對齊
        sectionHeader.Margin = new Thickness(10, 6, 10, 2);
        // 篩選中（R97.3）：漏斗、「篩選：有待確認、王小明 有參加」、✕
        var clear = Ui.Borderless(new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 11 } }, Ui.Secondary);
        clear.Padding = new Thickness(6);
        ToolTipService.SetToolTip(clear, L.T("清除篩選"));
        AutomationProperties.SetName(clear, L.T("清除篩選"));
        clear.Click += (_, _) => State.SetFilter(new MeetingFilter());
        filterText.VerticalAlignment = VerticalAlignment.Center;
        clear.VerticalAlignment = VerticalAlignment.Center;
        filterBar = Ui.Bar(filterFunnel, filterText, clear, 6);
        filterBar.Margin = new Thickness(16, 2, 10, 2);
        var clearLink = Ui.Link(L.T("清除篩選"), () => State.SetFilter(new MeetingFilter()));
        var emptyNote = Ui.Text(L.T("沒有符合篩選的會議"), 13, color: Ui.Secondary);
        filterEmpty = Ui.Column(4, emptyNote, clearLink);
        filterEmpty.Margin = new Thickness(16, 4, 12, 4);
        trashHeader.Margin = new Thickness(16, 0, 12, 6);
        trashHeader.Visibility = Visibility.Collapsed;
        restyle.Add(() =>
        {
            trashNote.Foreground = emptyNote.Foreground = Ui.Secondary;
            // 名稱、箭頭跟著按鈕的前景色（對比主題時是平台的按鈕色，滑過去反白）
            sectionTitle.ClearValue(TextBlock.ForegroundProperty);
            Ui.Borderless(sectionToggle, Ui.Secondary);
            sectionToggle.Padding = new Thickness(6, 2, 6, 2);
            // 標題不是按鈕的樣子：滑過去沒有底色、只出現箭頭（R101.1）；對比主題時照平台的按鈕
            if (!Ui.HighContrast)
                foreach (var resource in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" })
                    sectionToggle.Resources[resource] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Ui.Borderless(clear, Ui.Secondary);
            filterFunnel.Foreground = Ui.Celadon;
            StyleViewOptions(viewOptions, force: true);
            StyleViewOptions(trashOptions, force: true);
        });
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
            // 回到前景時更新 claude、codex、agy 的登入狀態（使用者可能剛在終端機登入，M§34.1）
            if (IsForeground) _ = CliLogin.RefreshIfStale();
            // 回到前景時查新的分享（R125.5、M§99.4）
            if (IsForeground) _ = host.Controller.RefreshSharedWithMe();
        };
        shareTimer.Tick += (_, _) => _ = host.Controller.RefreshSharedWithMe();
        shareTimer.Start();
        // 整理工具準備好了沒變了（登入狀態、key、暫停、設定）：會議頁的提示和失敗橫幅跟著變（M§34.2）
        host.Settings.SummarizerChanged += () => host.Post(Refresh);
        host.Settings.Changed += key =>
        {
            if (key is AppSettings.ProviderKey or AppSettings.CompatibleBaseUrlKey || key.EndsWith("Model", StringComparison.Ordinal)) host.Post(Refresh);
            // 文字大小（R59）：快捷鍵、設定頁、控制通道改的都一樣，整個會議內容重畫
            if (key == AppSettings.TextSizeKey) host.Post(ApplyTextSize);
            // 側欄的收合（R101）：畫面上點的、控制通道、資料夾改名刪除都一樣；用鍵盤收合時焦點回到同一列
            // 拖曳排序（R103.2）：Alt+↑／↓ 移動時焦點留在同一列
            if (key is AppSettings.SidebarCollapsedKey or AppSettings.SidebarExpandedKey or AppSettings.SidebarOrderKey)
                host.Post(() => { Refresh(); RestoreFocus(); RestoreScroll(); });
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
        // 再找不確定的地方找到了（M§88）：選取中的那場展開待確認（紀錄存檔後畫面會跟著重畫）
        host.Controller.MoreReviewFound += (id, count) => host.Post(() => { if (count > 0 && State.Selection == id) State.ReviewExpanded = true; });
        // 一次還原好幾場（R88）：回到會議列表、選取那幾場
        host.Controller.RevealMany += ids => host.Post(() => State.RevealMany(ids));
        host.Recorder.Changed += () => host.Post(ShowRecorderAlerts);
        // 更新下載的進度只重畫側欄底部（R10.6）
        if (host.Updater is { } updater) updater.Changed += () => host.Post(BuildFooter);
        toastTimer.Tick += (_, _) => { toast.IsOpen = false; toastTimer.Stop(); };
        // 換深淺色：列表的顏色是建列時給的，要重建
        root.ActualThemeChanged += (_, _) => { Ui.SetTheme(root.ActualTheme); restyle.ForEach(a => a()); listSignature = null; Refresh(); };
        // 對比主題開關、換一個對比主題（ActualTheme 不會變）：同樣重新給顏色、重畫（design-windows §11.3）
        Ui.OnContrastChanged(root, () => { restyle.ForEach(a => a()); listSignature = null; Refresh(); });
        Ui.SetTheme(root.ActualTheme);
        restyle.ForEach(a => a());
        State.Reload();
        _ = host.Controller.RefreshSharedWithMe();
    }

    public XamlRoot? XamlRoot => root.XamlRoot;
    public AppHost Host => host;
    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    void Build()
    {
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 240 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = Ui.SidebarBackground(new Grid { BorderBrush = Ui.Divider, BorderThickness = new Thickness(0, 0, 1, 0) });
        restyle.Add(() => sidebar.BorderBrush = Ui.Divider);
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
        scopes.SelectionChanged += (_, _) =>
        {
            if (updatingSelection) return;
            // 段落標題、展開的會議、「顯示全部」不是地方（R101、design-windows §11.34）：上下鍵、點到時選取回到原本那一列，動作在 SidebarItem 自己做
            if (scopes.SelectedItem is SidebarItem)
            {
                SyncScopeSelection();
                return;
            }
            if (scopes.SelectedItem is not ListViewItem { Tag: Scope scope }) return;
            detailView.FlushDrafts();
            if (scope.People) State.SetSidebar(SidebarMode.People);
            else State.ShowMeetings(scope.Folder);
        };
        Place(sidebar, list, 3);
        Place(sidebar, footer, 4);
        // 上面的導覽最高佔側欄的 60%，超過時自己捲動（R101）：展開的會議、資料夾多的時候不會把會議列表擠掉、超出的被切掉
        sidebar.SizeChanged += (_, e) => scopes.MaxHeight = Math.Max(120, Math.Round(e.NewSize.Height * 0.6));
        Grid.SetColumn(sidebar, 0);
        root.Children.Add(sidebar);
        Grid.SetColumn(detailHost, 1);
        root.Children.Add(detailHost);
        Grid.SetColumnSpan(toast, 2);
        root.Children.Add(toast);

        search.TextChanged += (_, e) => { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) State.SetSearch(search.Text); };
        list.SelectionChanged += (_, e) =>
        {
            if (updatingSelection) return;
            // 分組的標題（R101）不是會議：被選到時拿掉；只選到標題（上下鍵、點標題）時還原原本選的會議，Shift 點選跨過標題時照舊選那一段
            if (e.AddedItems.OfType<SidebarItem>().ToList() is { Count: > 0 } headers)
            {
                var onlyHeaders = headers.Count == e.AddedItems.Count;
                updatingSelection = true;
                try
                {
                    foreach (var header in headers) list.SelectedItems.Remove(header);
                    if (onlyHeaders)
                        foreach (var removed in e.RemovedItems.OfType<ListViewItem>())
                            if (list.Items.Contains(removed) && !list.SelectedItems.Contains(removed)) list.SelectedItems.Add(removed);
                }
                finally { updatingSelection = false; }
                if (onlyHeaders) return;
            }
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
        // 側欄的鍵盤（R88.2、R85.7、R100，同 Mac 的 sidebarReturn、sidebarDelete）：照選到、亮起來的東西。
        // Enter 或 F2（Windows 的慣例）重新命名、Delete 刪除或移除，都先確認；Ctrl+Delete 是整個視窗的快捷鍵
        // ListView 自己會把列上的 Enter 標成處理過（2026-11-16 虛擬機：F2 打得開、Enter 沒反應），所以 handledEventsToo；
        // 只管焦點在列本身時（列裡的「⋯」按 Enter 是打開選單）
        list.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            // 焦點在分組的標題上時不處理（Enter、空白鍵是收合，SidebarItem 自己做）
            if (e.OriginalSource is not ListViewItem || e.OriginalSource is SidebarItem || Down(VirtualKey.Control) || Down(VirtualKey.Menu)) return;
            if (e.Key is VirtualKey.Enter or VirtualKey.F2) { if (SidebarRename()) e.Handled = true; }
            else if (e.Key == VirtualKey.Delete) { if (SidebarDelete()) e.Handled = true; }
        }), true);
        // 定期會議、資料夾那幾列的按鍵（F2 重新命名、Delete 刪除、→ ← 展開收起，R100、R101）在那一列自己處理（FolderRow），不是右邊選的會議

        // 拖曳（R103、design-windows §11.36）：下面列表的會議拖到資料夾、定期會議、列表的標題、垃圾桶。拖的是選到的列時 ListView 帶整個多選
        list.DragItemsStarting += (_, e) =>
        {
            // 項目直接是 ListViewItem 時，e.Items 是它的 Content（同 scopes 的 ClickedItem，2026-11-16 虛擬機：拖不動）：找回那一列
            var ids = e.Items.Select(x => x as ListViewItem ?? list.Items.OfType<ListViewItem>().FirstOrDefault(i => ReferenceEquals(i.Content, x)))
                .Select(i => i?.Tag).OfType<long>().ToList();
            if (ids.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            if (ids.Count == 1) ids = SidebarDrag.DragIds(ids[0], State.MultiSelected);
            BeginMeetingDrag(e.Data, ids);
        };
        list.DragItemsCompleted += (_, _) => EndDrag();
        // 列表的標題：在看資料夾＝移進去、定期會議＝加進去、「其他會議」＝移出資料夾和定期會議
        sectionHeader.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        sectionHeader.AllowDrop = true;
        sectionHeader.DragOver += (_, e) =>
        {
            var accepts = DraggingMeetings(e) is not null && SidebarDrag.HeaderAccepts(State.ShowTrash, State.ShowsPeople, State.FolderFilter, State.ListShowsOthersOnly);
            if (accepts) ClearDropTargets();
            ShowHeaderDrop(accepts);
            if (!accepts) return;
            e.AcceptedOperation = DataPackageOperation.Move;
            Caption(e, State.FolderFilter is long folder ? L.F("移到「%@」", State.FolderName(folder)) : L.T("移出資料夾"));
            e.Handled = true;
        };
        sectionHeader.DragLeave += (_, _) => ShowHeaderDrop(false);
        sectionHeader.Drop += (_, e) =>
        {
            ShowHeaderDrop(false);
            if (DraggingMeetings(e) is not { } ids) return;
            e.Handled = true;
            EndDrag();
            host.Controller.DropOnListHeader(ids, State.FolderFilter, State.ListShowsOthersOnly);
        };

        // 拖放匯入（R3.1）
        root.AllowDrop = true;
        root.DragOver += (_, e) =>
        {
            // 拖到沒有收東西的地方：亮著的列、標題、垃圾桶收掉（列的 DragLeave 不一定來，2026-11-16 虛擬機：拖過去的列一直亮著）
            ClearDropTargets();
            if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
        };
        root.DragLeave += (_, _) => ClearDropTargets();
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
        // 單獨暫停麥克風（R116、M§90）
        Accelerator(VirtualKey.M, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => host.Recorder.SetMicPaused(!host.Recorder.MicPaused));
        Accelerator(VirtualKey.B, VirtualKeyModifiers.Control, () =>
        {
            if (host.Controller.BookmarkRecording()) ShowNotice(L.F("已標記 %@ 為重點", TimeFormat.String(host.Recorder.Elapsed)));
        });
        Accelerator(VirtualKey.E, VirtualKeyModifiers.Control, () => { if (State.Detail is { } d) _ = Dialogs.Export(this, d.Meeting.Id!.Value); });
        Accelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => detailView.FocusSearch());
        // F3、Shift+F3：逐字稿搜尋的下一個、上一個（R111.2），在逐字稿分頁才有作用
        Accelerator(VirtualKey.F3, VirtualKeyModifiers.None, () => { if (State.Detail is not null && State.Tab == DetailTab.Transcript) detailView.StepSearch(1); });
        Accelerator(VirtualKey.F3, VirtualKeyModifiers.Shift, () => { if (State.Detail is not null && State.Tab == DetailTab.Transcript) detailView.StepSearch(-1); });
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
        // 編輯會議的覆蓋層（R107、design-windows §11.40）：蓋住整個視窗、中間一張卡片；跟 root 同一層，root 的快捷鍵、空白鍵不會收到裡面的按鍵
        editorLayer.Children.Add(editorCard);
        editorLayer.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        editorLayer.SizeChanged += (_, _) => Ui.Guard(SizeEditorCard);
        editorLayer.PointerPressed += (_, e) => e.Handled = true;
        editorCard.Shadow = new ThemeShadow();
        editorCard.Translation = new System.Numerics.Vector3(0, 0, 32);
        restyle.Add(StyleEditorLayer);
        restyle.Add(RestyleEditor);
        var outer = new Grid();
        outer.Children.Add(root);
        outer.Children.Add(editorLayer);
        Content = Ui.WindowBackground(outer);
    }

    // MARK: 編輯會議（R107、M§81.5、design-windows §11.40）

    readonly Grid editorLayer = new() { Visibility = Visibility.Collapsed };
    readonly Border editorCard = new()
    {
        CornerRadius = new CornerRadius(12), Padding = new Thickness(24), BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    MeetingEditView? editor;
    /// <summary>編輯畫面開著時有 Refresh 沒重建會議頁：關掉時補一次</summary>
    bool detailStale;

    /// <summary>打開的編輯畫面是哪一場（DEBUG 的 state editingMeeting）</summary>
    public long? EditingMeeting => editor?.MeetingId;

    void StyleEditorLayer()
    {
        editorLayer.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.IsDark ? (byte)140 : (byte)90, 0, 0, 0));
        editorCard.Background = Ui.HighContrast ? Ui.CardBackground : new SolidColorBrush(Ui.IsDark ? Ui.HexColor("#2B2B2B") : Microsoft.UI.Colors.White);
        // 對比主題：一圈字的顏色的框（卡片和變暗的主視窗都是深色，12% 的灰框分不出卡片的邊）
        editorCard.BorderBrush = Ui.HighContrast ? Ui.SystemBrush(Windows.UI.ViewManagement.UIElementType.WindowText) : SidebarLook.Gray(0.12);
    }

    /// <summary>開著編輯畫面時換了深淺色、對比主題：用同一份編輯狀態重建畫面（顏色是建的時候決定的），焦點給時間軸</summary>
    void RestyleEditor()
    {
        if (editor is not { } old) return;
        old.Closed -= CloseEditor;
        var view = old.Rebuilt();
        editor = view;
        view.Closed += CloseEditor;
        editorCard.Child = view.Host;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(view.FocusTimeline));
    }

    /// <summary>
    /// 卡片的大小跟著視窗（同 Mac 的 sheet：寬 880 左右、最小 720×520、最大 1100×900），視窗小的時候留 16 的邊。
    /// 上下只留 24（左右 48）：150% 縮放的 1080p 筆電視窗內容只有 640 左右高，逐字稿要多一點地方
    /// </summary>
    void SizeEditorCard()
    {
        double w = editorLayer.ActualWidth, h = editorLayer.ActualHeight;
        if (w <= 0 || h <= 0) return;
        editorCard.Width = Math.Max(Math.Min(720, w - 32), Math.Min(1100, w - 96));
        editorCard.Height = Math.Max(Math.Min(520, h - 32), Math.Min(900, h - 48));
    }

    /// <summary>「編輯會議…」（右鍵、「⋯」、更多）：不能編輯時說原因；選到那場、停止播放、打開編輯畫面</summary>
    public void OpenEditor(long id)
    {
        if (editor is not null || !host.Controller.RequestEditMeeting(id)) return;
        if (State.Selection != id) State.Reveal(id);
        if (host.Player.IsPlaying) host.Player.Toggle();
        detailView.FlushDrafts();
        var view = new MeetingEditView(host, this, id);
        editor = view;
        view.Closed += CloseEditor;
        editorCard.Child = view.Host;
        StyleEditorLayer();
        editorLayer.Visibility = Visibility.Visible;
        SizeEditorCard();
        // 編輯畫面開著時主視窗的快捷鍵（Ctrl+Delete 移到垃圾桶、Ctrl+R 錄音…）不作用（同 Mac 的 sheet）
        foreach (var a in root.KeyboardAccelerators) a.IsEnabled = false;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(view.FocusTimeline));
    }

    void CloseEditor()
    {
        if (editor is null) return;
        editor = null;
        editorLayer.Visibility = Visibility.Collapsed;
        editorCard.Child = null;
        foreach (var a in root.KeyboardAccelerators) a.IsEnabled = true;
        // 開著時沒重建的會議頁補一次（Refresh 開著時只更新側欄）
        if (detailStale)
        {
            detailStale = false;
            Refresh();
        }
        // 焦點回到會議列表選到的那一列（沒有就是列表）：覆蓋層收起來後焦點不會自己回來
        if (list.SelectedItem is { } selected && list.ContainerFromItem(selected) is ListViewItem row) row.Focus(FocusState.Programmatic);
        else list.Focus(FocusState.Programmatic);
    }

    /// <summary>「還原成編輯前的樣子…」（右鍵、「⋯」、更多）</summary>
    public Task ConfirmRestoreEdit(long id) => Dialogs.ConfirmRestoreEdit(this, id);

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
        // 編輯會議開著時，會議頁等關掉時才重建（Refresh），那時已經是新的大小
        if (editor is null) detailView.Rescale();
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
        // 編輯會議開著時不重建右邊的會議頁，關掉時補一次（design-windows §11.40）：覆蓋層底下重建的分頁列（SelectorBar）
        // 會畫到覆蓋層上面、跟著每次重建跳來跳去（2026-11-17 虛擬機：下載模型時每個進度都重讀會議、重建會議頁）
        if (editor is not null)
        {
            detailStale = true;
            return;
        }
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
            // 「先轉錄這幾場」出不出現、字（M§96.3）變了也重建
            var signature = $"{State.ShowTrash}\u001F{string.Join(',', multi)}\u001F{PrioritizeTitle(multi)}";
            if (signature != multiSignature || multiPane is null)
            {
                multiSignature = signature;
                multiPane = MultiSelectionPane(multi);
            }
            if (!ReferenceEquals(detailHost.Child, multiPane)) detailHost.Child = multiPane;
            return;
        }
        multiPane = null;
        // 「與我分享」選到的一場（R125.5）：右邊是預覽
        if (State.Detail is null && !State.SearchActive && State.SelectedShared is { } sharedId
            && host.Controller.SharedWithMe.FirstOrDefault(i => i.Id == sharedId) is { } shared)
        {
            detailView.Clear();
            detailHost.Child = SharedPreview(shared);
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
            // 先轉錄這幾場（R122.3、M§96.3）：同選單的第一項
            if (PrioritizeTitle(ids) is { } prioritize)
                buttons.Children.Insert(0, Ui.Button(prioritize, () => host.Controller.PrioritizeTranscription(ids, announce: true)));
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
        var record = Ui.IgnoreStrayKeyUp(new SplitButton { Content = Ui.Row(6, new SymbolIcon(Symbol.Microphone), Ui.Text(MeetingLanguages.Ui("錄音", "Record"), wrap: false)) });
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

    /// <summary>側欄導覽的一列：人物，或所有會議（Folder 是 null）、某個資料夾（id，R118）</summary>
    sealed record Scope(bool People, long? Folder);

    string? scopesSignature;

    /// <summary>
    /// 側欄的導覽（M§60.2、R85.1，2026-11-15 照 Mac 改，拿掉 SelectorBar）：搜尋框下面一直有「所有會議」「人物」兩列（圖示、名字、數量），
    /// 下面是資料夾，最後是下面那一段的標題（選的那一項的名字）。點所有會議回到會議模式，點人物切到人物模式；
    /// 點資料夾是在原地展開、收起最近 5 場（R101、design-windows §11.34），只看它要用展開後的「顯示全部」。
    /// 「資料夾」一段一直顯示，標題右邊「+」建空的資料夾（R104、design-windows §11.37），可以收起（定期會議 D177 拿掉）。垃圾桶裡、搜尋中不顯示（搜尋的結果是會議）。名字、數量沒變時只換選取，不重建（滑鼠停留、鍵盤焦點不會跳）
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
        var all = State.Folders;
        var collapsed = SidebarCollapse.Collapsed(host.Settings);
        var expanded = SidebarCollapse.Expanded(host.Settings);
        // 在原地展開的資料夾：裡面的會議也要算進樣子（改名、新的會議）
        var peeks = new Dictionary<string, List<MeetingRow>>();
        foreach (var (id, _, _) in all)
            if (expanded.Contains(SidebarCollapse.FolderKey(id))) peeks[SidebarCollapse.FolderKey(id)] = SidebarCollapse.PeekRows(State.Meetings, id);
        // 「資料夾」那一段的順序（R103.2）：拖曳排過的照 sidebarOrder，新的在後面
        var order = SidebarKeys();
        var signature = string.Join('\u001F', [State.Meetings.Count.ToString(), State.People.Count.ToString(), Ui.ThemeKey, "order", .. order,
            .. all.Select(f => $"{f.Id}\u001E{f.Name}\u001E{f.Count}\u001E{(State.RuleFor(f.Id) is { } rule ? RuleTip(rule) : "")}"),
            "collapsed", collapsed.Contains("folders").ToString(), collapsed.Contains("shared").ToString(),
            .. host.Controller.SharedWithMe.Select(i => $"{i.Id}\u001E{i.Title}\u001E{i.From.Display}\u001E{i.Meeting?.MeetingDate}\u001E{i.IsNew}\u001E{i.ImportedVersion}"),
            .. peeks.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\u001E" + p.Value.Count + "\u001E" + string.Join('\u001E',
                p.Value.Take(SidebarCollapse.PeekCount).Select(r => $"{r.Id}\u001D{r.Meeting.DisplayTitle}\u001D{r.Meeting.MeetingDate}\u001D{r.Meeting.Language}")))]);
        if (signature != scopesSignature)
        {
            scopesSignature = signature;
            updatingSelection = true;
            scopes.Items.Clear();
            peekViews.Clear();
            sharedViews.Clear();
            scopes.Items.Add(ScopeRow(L.T("所有會議"), State.Meetings.Count, new Scope(false, null), new SymbolIcon(Symbol.AllApps)));
            scopes.Items.Add(ScopeRow(L.T("人物"), State.People.Count, new Scope(true, null), new SymbolIcon(Symbol.People)));
            // 「資料夾」一段（R101.3）：一直顯示，標題右邊「+」建空的資料夾（R104）
            {
                var open = !collapsed.Contains("folders");
                scopes.Items.Add(SectionItem("folders", L.T("資料夾"), open, trailing: NewFolderButton()));
                if (open)
                {
                    // 還沒有任何資料夾：一行說明（R104.2）
                    if (all.Count == 0)
                    {
                        var none = new ListViewItem
                        {
                            Content = Ui.Text(L.T("按 + 新增資料夾"), 13, color: Ui.Secondary, wrap: false), IsEnabled = false, IsTabStop = false,
                            MinHeight = 30, Padding = new Thickness(16, 0, 12, 0),
                        };
                        AutomationProperties.SetName(none, L.T("按 + 新增資料夾"));
                        scopes.Items.Add(none);
                    }
                    var byId = all.ToDictionary(f => f.Id);
                    foreach (var key in order)
                    {
                        var rows = peeks.GetValueOrDefault(key);
                        if (SidebarCollapse.FolderId(key) is long folder && byId.TryGetValue(folder, out var info))
                        {
                            // 有自動加入條件的資料夾右下角一個小齒輪（同 Mac 的 folder.badge.gearshape），滑過去看條件（M§72）
                            var (name, count) = (info.Name, info.Count);
                            var rule = State.RuleFor(folder);
                            // 會議拖到這裡＝移進去；從檔案總管拖來的錄音＝匯入到這個資料夾（R103.3）
                            var drop = new RowDrop(key, ids => host.Controller.MoveToFolder(ids, folder), L.F("移到「%@」", name),
                                files => ImportInto(files, folder), L.F("匯入到「%@」", name));
                            var row = FolderRow(name, count, new Scope(false, folder), key, rows is not null, rule is null ? null : RuleBadge, drop,
                                menu => FillFolderMenu(menu, folder), () => RenameUi(new RenameTarget.Folder(folder)), () => ConfirmDeleteFolder(folder));
                            if (rule is not null) ToolTipService.SetToolTip(row, RuleTip(rule));
                            scopes.Items.Add(row);
                            if (rows is not null) AddPeek(rows, key, () => State.ShowMeetings(folder));
                        }
                    }
                }
            }
            // 「與我分享」（R125.5、M§99.4）：資料夾那一段下面，有東西時才出現
            if (host.Controller.SharedWithMe.Count > 0)
            {
                var open = !collapsed.Contains("shared");
                scopes.Items.Add(SectionItem("shared", L.T("與我分享"), open, top: 12));
                if (open) foreach (var item in host.Controller.SharedWithMe) scopes.Items.Add(SharedRow(item));
            }
            updatingSelection = false;
        }
        SyncScopeSelection();
        // 標題：「所有會議」（或資料夾的名稱），分組時接「・依月份」；人物模式沒有「顯示方式」
        // 下面只放資料夾以外的會議時叫「其他會議」（R101.6）
        var scope = State.FolderFilter is long current ? State.FolderName(current) : State.ListShowsOthersOnly ? L.T("其他會議") : L.T("所有會議");
        var grouping = State.EffectiveGrouping;
        sectionTitle.Text = people ? L.T("人物") : grouping == MeetingGrouping.None ? scope : scope + "・" + grouping.Label();
        // 名稱是收合的按鈕（R101）：會議列表（人物模式是人物列表）收起時只剩這一行
        var sectionKey = people ? "people" : "meetings";
        var sectionOpen = !collapsed.Contains(sectionKey);
        AutomationProperties.SetName(sectionToggle, sectionTitle.Text);
        AutomationProperties.SetAutomationId(sectionToggle, "section:" + sectionKey);
        if (sectionOpenShown != sectionOpen)
        {
            sectionOpenShown = sectionOpen;
            sectionChevron.Glyph = sectionOpen ? ChevronDown : ChevronRight;
            UpdateSectionChevron();
            AutomationProperties.SetItemStatus(sectionToggle, sectionOpen ? L.T("已展開") : L.T("已收起"));
            ToolTipService.SetToolTip(sectionToggle, sectionOpen ? L.T("收起這一段") : L.T("展開這一段"));
        }
        viewOptions.Visibility = people ? Visibility.Collapsed : Visibility.Visible;
        StyleViewOptions(viewOptions);
        var filtering = !people && sectionOpen && State.Filter.IsActive;
        filterBar.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
        filterText.Text = filtering ? L.F("篩選：%@", State.FilterSummary) : "";
        filterEmpty.Visibility = filtering && State.FilteredMeetings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 側欄的選取跟著 State：人物、資料夾或所有會議；一樣時不動。只看某個資料夾時（「顯示全部」）
    /// 那一列照舊是選到的樣子。展開的會議的「選到」樣子也在這裡換（R101）
    /// </summary>
    void SyncScopeSelection()
    {
        StylePeeks();
        var people = State.Sidebar == SidebarMode.People;
        var current = people ? new Scope(true, null) : new Scope(false, State.FolderFilter);
        var selected = scopes.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is Scope s && s == current);
        if (ReferenceEquals(scopes.SelectedItem, selected)) return;
        var was = updatingSelection;
        updatingSelection = true;
        scopes.SelectedItem = selected;
        updatingSelection = was;
    }

    string RuleTip(FolderRule rule) => Organizing.RuleSummary(rule, State.People, State.Templates);

    // MARK: 收合（R101、design-windows §11.34）

    const string ChevronDown = "\uE70D";
    const string ChevronRight = "\uE76C";
    /// <summary>側欄每一列圖示的寬度（SymbolIcon 20）：展開的會議留這個空位，名稱跟資料夾名稱對齊</summary>
    const double IconWidth = 20;
    /// <summary>資料夾圖示上的小標記：自動加入的條件（齒輪，右下角）</summary>
    const string RuleBadge = "\uE713";
    const string FolderGlyph = "\uE8B7";
    const string FolderOpenGlyph = "\uE838";

    static FontIcon ChevronIcon(bool expanded, Brush? color = null)
    {
        var icon = new FontIcon { Glyph = expanded ? ChevronDown : ChevronRight, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        if (color is not null) icon.Foreground = color;
        return icon;
    }

    /// <summary>
    /// 資料夾的圖示（R101.2）：收起時關著的資料夾（Segoe Fluent 的 Folder E8B7）、展開時打開的資料夾（FolderOpen E838）。
    /// 有自動加入條件的資料夾右下角一個小齒輪（M§72），放在側欄底色的小圓上，不跟資料夾的線疊在一起
    /// </summary>
    static FrameworkElement FolderIcon(bool open, string? badge, out FontIcon folder)
    {
        folder = new FontIcon { Glyph = open ? FolderOpenGlyph : FolderGlyph, FontSize = 20 };
        var grid = new Grid { Width = IconWidth, Height = 20, Children = { folder } };
        if (badge is null) return grid;
        var mark = new FontIcon
        {
            Glyph = badge, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(new Border
        {
            Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5), Child = mark, Background = Ui.SidebarColor,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -4),
        });
        return grid;
    }

    /// <summary>展開的會議：哪一場、標題、框（對比主題時選到的樣子）、「⋯」重新判斷要不要顯示</summary>
    sealed record PeekView(long Id, SidebarItem Item, TextBlock Title, Border Frame, Action UpdateMore)
    {
        public bool? Selected { get; set; }
    }

    readonly List<PeekView> peekViews = [];
    /// <summary>「與我分享」的列：分享的 id、那一列、「⋯」重新判斷要不要顯示</summary>
    sealed record SharedView(string Id, SidebarItem Item, Action UpdateMore)
    {
        public bool? Selected { get; set; }
    }
    readonly List<SharedView> sharedViews = [];
    /// <summary>用鍵盤收合、展開時焦點所在那一列的 AutomationId 和焦點的樣子：清單重建、選取換了之後找回來</summary>
    string? pendingFocus;
    FocusState pendingFocusState = FocusState.Keyboard;

    void ToggleSection(string key)
    {
        RememberFocus();
        RememberScroll();
        SidebarCollapse.SetSection(host.Settings, key, !SidebarCollapse.IsSectionExpanded(host.Settings, key));
    }

    void SetRow(string key, bool expanded)
    {
        RememberFocus();
        RememberScroll();
        SidebarCollapse.SetRow(host.Settings, key, expanded);
    }

    /// <summary>收合前兩個清單捲到哪裡：清單重建後會回到最上面，排好版後捲回來（不然展開下面的資料夾時那一列被捲走）</summary>
    (double Scopes, double List)? pendingScroll;

    void RememberScroll() => pendingScroll = (Scroller(scopes)?.VerticalOffset ?? 0, Scroller(list)?.VerticalOffset ?? 0);

    void RestoreScroll()
    {
        if (pendingScroll is not { } offsets) return;
        pendingScroll = null;
        root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(() =>
        {
            Scroller(scopes)?.ChangeView(null, offsets.Scopes, null, true);
            Scroller(list)?.ChangeView(null, offsets.List, null, true);
        }));
    }

    /// <summary>ListView 範本裡的 ScrollViewer</summary>
    static ScrollViewer? Scroller(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer viewer) return viewer;
            if (Scroller(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>鍵盤的焦點在側欄的某一列（有 AutomationId）時記下來；滑鼠點的不用（any：滑鼠點的也記）</summary>
    void RememberFocus(bool any = false)
    {
        if (root.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is Control focused
            && (any ? focused.FocusState != FocusState.Unfocused : focused.FocusState == FocusState.Keyboard)
            && AutomationProperties.GetAutomationId(focused) is { Length: > 0 } id)
            (pendingFocus, pendingFocusState) = (id, focused.FocusState);
    }

    /// <summary>收合後清單重建了：焦點回到同一列（新的列要排好版才拿得到焦點）</summary>
    void RestoreFocus()
    {
        if (pendingFocus is not { } id) return;
        pendingFocus = null;
        root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(() =>
        {
            Control? target = scopes.Items.Concat(list.Items).OfType<ListViewItem>().FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == id);
            if (target is null && id == AutomationProperties.GetAutomationId(sectionToggle)) target = sectionToggle;
            target?.Focus(pendingFocusState);
        }));
    }

    /// <summary>
    /// 段落標題（定期會議、資料夾、分組的每一組）：標題後面一個小箭頭，點標題、Enter、空白鍵切換（R101.1）。
    /// 是 SidebarItem：上下鍵走得到但選不到；UI Automation 是有名字的項目（Invoke、ExpandCollapse）。
    /// trailing：標題右邊的按鈕（「資料夾」的「+」，R104）；點按鈕不會收合（SidebarItem 不管從按鈕來的點擊）
    /// </summary>
    SidebarItem SectionItem(string key, string title, bool expanded, double top = 8, FrameworkElement? trailing = null)
    {
        // 跟列一樣大的灰字、不加粗（R101.4）
        var text = Ui.Text(title, 14, color: Ui.Secondary, wrap: false);
        var chevron = ChevronIcon(expanded, Ui.Tertiary);
        var label = Ui.Row(6, text, chevron);
        label.VerticalAlignment = VerticalAlignment.Center;
        var item = new SidebarItem(() => ToggleSection(key), expanded)
        {
            Content = Ui.FollowForeground<FrameworkElement>(trailing is null ? label : Ui.Bar(label, null, trailing, 4)),
            MinHeight = 28, Padding = new Thickness(12, top, trailing is null ? 12 : 4, 0),
        };
        // 標題不是一列：滑過去沒有底色，箭頭出現（R101.1）。對比主題時照平台（滑過去反白，字是反白的字）
        if (!Ui.HighContrast)
            foreach (var resource in new[] { "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundPressed" })
                item.Resources[resource] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ChevronOnHover(item, chevron, expanded);
        AutomationProperties.SetName(item, title);
        AutomationProperties.SetAutomationId(item, "section:" + key);
        AutomationProperties.SetItemStatus(item, expanded ? L.T("已展開") : L.T("已收起"));
        AutomationProperties.SetHeadingLevel(item, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        ToolTipService.SetToolTip(item, expanded ? L.T("收起這一段") : L.T("展開這一段"));
        return item;
    }

    /// <summary>「資料夾」標題右邊的「+」（R104、design-windows §11.37）：打開新資料夾的對話框，建一個空的資料夾</summary>
    Button NewFolderButton()
    {
        // 細線、灰色、滑過變亮加淡底，跟列的「⋯」對齊（SidebarLook.HeaderIcon，同 Mac 的 SidebarHeaderIcon）
        var button = SidebarLook.HeaderIcon(new Button(), "\uE710");
        ToolTipService.SetToolTip(button, L.T("新資料夾"));
        AutomationProperties.SetName(button, L.T("新資料夾"));
        button.Click += (_, _) => RenameUi(new RenameTarget.NewFolder([]));
        return button;
    }

    /// <summary>段落標題的箭頭：收起來時一直顯示，展開時只在滑過、有鍵盤焦點時出現（位置留著，R101.1）</summary>
    static void ChevronOnHover(Control owner, UIElement chevron, bool expanded)
    {
        if (!expanded) return;
        var hovering = false;
        void Update() => chevron.Opacity = hovering || owner.FocusState == FocusState.Keyboard ? 1 : 0;
        owner.PointerEntered += (_, _) => { hovering = true; Update(); };
        owner.PointerExited += (_, _) => { hovering = false; Update(); };
        owner.GotFocus += (_, _) => Update();
        owner.LostFocus += (_, _) => Update();
        Update();
    }

    /// <summary>
    /// 資料夾、定期會議的一列（R101.2）：圖示（關著／打開的資料夾）、名字、數量；點一下（Enter、空白鍵、UI Automation 的 Invoke）在原地展開、收起，
    /// 不換下面的列表。只看它的時候（「顯示全部」、右鍵）照舊是選到的樣子（SyncScopeSelection 選它）。
    /// 右鍵和右邊的「⋯」同一份（R100）；F2 重新命名、Delete 刪除、→ 展開、← 收起、Alt+↑／↓ 往上、往下移（R103.2）。
    /// 可以拖曳排序，收拖過來的會議、檔案（資料夾）、別的資料夾或定期會議（排序，R103、design-windows §11.36）
    /// </summary>
    SidebarItem FolderRow(string name, int count, Scope scope, string key, bool expanded, string? badge, RowDrop drop, Action<MenuFlyout> fill,
                          Action rename, Func<Task> delete)
    {
        // 右邊不放場數（R101.4），滑過或選到時「⋯」；場數在無障礙的值
        var more = RowMore.Button(fill);
        more.HorizontalAlignment = HorizontalAlignment.Right;
        var icon = FolderIcon(expanded, badge, out var glyph);
        bool Key(VirtualKey k)
        {
            // Alt+↑／↓：往上、往下移一格（排序；UI Automation 沒有 VoiceOver 的自訂動作，design-windows §11.36）
            if (Down(VirtualKey.Menu) && !Down(VirtualKey.Control) && k is VirtualKey.Up or VirtualKey.Down)
            {
                MoveFolderRow(key, k == VirtualKey.Up ? -1 : 1);
                return true;
            }
            if (Down(VirtualKey.Control) || Down(VirtualKey.Menu)) return false;
            switch (k)
            {
                case VirtualKey.F2: rename(); return true;
                case VirtualKey.Delete: _ = delete(); return true;
                case VirtualKey.Right: if (!expanded) SetRow(key, true); return true;
                case VirtualKey.Left: if (expanded) SetRow(key, false); return true;
                default: return false;
            }
        }
        var bar = Ui.Bar(icon, Ui.Text(name, 14, wrap: false), more, 10);
        bar.VerticalAlignment = VerticalAlignment.Center;
        // 排序時的插入線（上緣或下緣），伸到整列的寬
        var line = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 2, RadiusX = 1, RadiusY = 1, Fill = Ui.Celadon, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Margin = new Thickness(-8, -1, -8, -1),
        };
        var item = new SidebarItem(() => SetRow(key, !expanded), expanded, Key)
        {
            Tag = scope, MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = Ui.FollowForeground(new Grid { Children = { bar, line } }),
        };
        SidebarLook.Row(item);
        AutomationProperties.SetName(item, name);
        AutomationProperties.SetAutomationId(item, "scope:" + key);
        SidebarLook.Status(item, expanded, L.F("%lld 場", count));
        AutomationProperties.SetHelpText(item, L.T("按 Alt+↑、Alt+↓ 可以往上移、往下移"));
        item.ContextFlyout = RowMore.Context(fill);
        RowMore.Attach(item, more, () => item.IsSelected);
        // 拖曳排序：拖的是這一列的鍵
        item.DragData = data => BeginItemDrag(data, key);
        item.DropCompleted += (_, _) => EndDrag();
        AttachRowDrop(item, drop with { Glyph = glyph, Line = line, Expanded = expanded });
        return item;
    }

    /// <summary>
    /// 資料夾、定期會議那一列收什麼（R103）：Meetings（拖來的會議）、Files（檔案總管的檔案，資料夾才有）和各自的說明文字；
    /// 別的資料夾、定期會議（排序）一定收。Glyph、Line、Expanded 是畫回饋用的
    /// </summary>
    sealed record RowDrop(string Key, Action<List<long>>? Meetings, string? MeetingCaption, Action<List<string>>? Files, string? FilesCaption)
    {
        public FontIcon? Glyph { get; init; }
        public Microsoft.UI.Xaml.Shapes.Rectangle? Line { get; init; }
        public bool Expanded { get; init; }
    }

    enum DropKind { Into, Before, After, Files }

    /// <summary>拖曳中的東西（這個 app 裡）：DragOver 要知道是不是拖到自己上面，DataPackageView 只能非同步讀。拖曳結束時清掉</summary>
    List<long>? draggingMeetings;
    string? draggingItem;

    void BeginMeetingDrag(DataPackage data, List<long> ids)
    {
        draggingMeetings = ids;
        draggingItem = null;
        data.SetData(SidebarDrag.MeetingFormat, SidebarDrag.Encode(ids));
        data.RequestedOperation = DataPackageOperation.Move;
    }

    void BeginItemDrag(DataPackage data, string key)
    {
        draggingItem = key;
        draggingMeetings = null;
        data.SetData(SidebarDrag.ItemFormat, key);
        data.RequestedOperation = DataPackageOperation.Move;
    }

    void EndDrag()
    {
        ClearDropTargets();
        draggingMeetings = null;
        draggingItem = null;
    }

    /// <summary>拖過來的是會議（這個 app 裡拖的）：那幾場；不是時 null</summary>
    List<long>? DraggingMeetings(DragEventArgs e) => draggingMeetings is { Count: > 0 } ids && e.DataView.Contains(SidebarDrag.MeetingFormat) ? ids : null;

    string? DraggingItem(DragEventArgs e) => draggingItem is { } key && e.DataView.Contains(SidebarDrag.ItemFormat) ? key : null;

    static void Caption(DragEventArgs e, string? caption)
    {
        e.DragUIOverride.IsCaptionVisible = caption is not null;
        if (caption is not null) e.DragUIOverride.Caption = caption;
    }

    DropKind? RowDropKind(DragEventArgs e, SidebarItem item, RowDrop drop)
    {
        if (DraggingItem(e) is { } key) return key == drop.Key ? null : e.GetPosition(item).Y < item.ActualHeight / 2 ? DropKind.Before : DropKind.After;
        if (DraggingMeetings(e) is not null) return drop.Meetings is null ? null : DropKind.Into;
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) return drop.Files is null ? null : DropKind.Files;
        return null;
    }

    /// <summary>
    /// 拖到這一列上：會議、檔案是整列亮起來、圖示換成打開的資料夾；排序是上緣或下緣一條線（看放開的位置在上半或下半）。
    /// 處理了就 Handled，不然往上到整個視窗（拖檔案進來匯入）
    /// </summary>
    void AttachRowDrop(SidebarItem item, RowDrop drop)
    {
        item.AllowDrop = true;
        void Over(DragEventArgs e)
        {
            var kind = RowDropKind(e, item, drop);
            ShowDropRow(item, drop, kind);
            if (kind is null)
            {
                // 拖到自己上面、定期會議收不到的檔案：不能放（不交給整個視窗匯入）
                if (DraggingItem(e) is not null || DraggingMeetings(e) is not null || e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    e.Handled = true;
                }
                return;
            }
            e.AcceptedOperation = kind == DropKind.Files ? DataPackageOperation.Copy : DataPackageOperation.Move;
            Caption(e, kind switch { DropKind.Into => drop.MeetingCaption, DropKind.Files => drop.FilesCaption, _ => null });
            // 排序：只有插入線，不要說明和圖示
            e.DragUIOverride.IsGlyphVisible = kind is DropKind.Into or DropKind.Files;
            e.Handled = true;
        }
        item.DragEnter += (_, e) => Over(e);
        item.DragOver += (_, e) => Over(e);
        item.DragLeave += (_, _) => ShowDropRow(item, drop, null);
        item.Drop += async (_, e) =>
        {
            var kind = RowDropKind(e, item, drop);
            ClearDropTargets();
            if (kind is null) return;
            e.Handled = true;
            switch (kind)
            {
                case DropKind.Before or DropKind.After when DraggingItem(e) is { } key:
                    EndDrag();
                    RememberScroll();
                    if (!SidebarDrag.Move(host.Settings, SidebarKeys(), key, drop.Key, kind == DropKind.Before)) pendingScroll = null;
                    break;
                case DropKind.Into when DraggingMeetings(e) is { } ids:
                    EndDrag();
                    drop.Meetings?.Invoke(ids);
                    break;
                case DropKind.Files:
                {
                    var deferral = e.GetDeferral();
                    List<string> paths;
                    try { paths = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().Select(f => f.Path).ToList(); }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { paths = []; }
                    finally { deferral.Complete(); }
                    if (paths.Count > 0) Ui.Guard(() => drop.Files?.Invoke(paths));
                    break;
                }
            }
        };
    }

    /// <summary>亮著的那一列（拖東西進來、排序的線）：同時只有一列，換到別列時前一列收掉</summary>
    (SidebarItem Item, RowDrop Drop)? dropRow;
    /// <summary>亮著的垃圾桶連結（側欄底部，BuildFooter 每次重建）</summary>
    HyperlinkButton? dropTrash;

    void ShowDropRow(SidebarItem item, RowDrop drop, DropKind? kind)
    {
        if (dropRow is { } old && !ReferenceEquals(old.Item, item)) ShowRowDrop(old.Item, old.Drop, null);
        if (kind is not null)
        {
            ShowHeaderDrop(false);
            ShowTrashDrop(null);
        }
        ShowRowDrop(item, drop, kind);
        dropRow = kind is null ? null : (item, drop);
    }

    /// <summary>拖曳離開、放下、結束時：亮著的列、標題、垃圾桶都收掉</summary>
    void ClearDropTargets()
    {
        if (dropRow is { } old) ShowRowDrop(old.Item, old.Drop, null);
        dropRow = null;
        ShowHeaderDrop(false);
        ShowTrashDrop(null);
    }

    void ShowTrashDrop(HyperlinkButton? trash)
    {
        if (dropTrash is { } old && !ReferenceEquals(old, trash)) old.ClearValue(Control.ForegroundProperty);
        dropTrash = trash;
        if (trash is not null) trash.Foreground = Ui.AccentText;
    }

    void ShowRowDrop(SidebarItem item, RowDrop drop, DropKind? kind)
    {
        var into = kind is DropKind.Into or DropKind.Files;
        if (Ui.HighContrast)
        {
            // 對比主題：沒有淡淡的底色，一圈反白顏色的框（同 §11.3 的做法）
            item.BorderBrush = into ? Ui.Celadon : null;
            item.BorderThickness = new Thickness(into ? 2 : 0);
        }
        else item.Backplate(into ? new SolidColorBrush(Windows.UI.Color.FromArgb(46, Ui.Celadon.Color.R, Ui.Celadon.Color.G, Ui.Celadon.Color.B)) : null);
        if (drop.Glyph is { } glyph) glyph.Glyph = into || drop.Expanded ? FolderOpenGlyph : FolderGlyph;
        if (drop.Line is { } line)
        {
            line.Visibility = kind is DropKind.Before or DropKind.After ? Visibility.Visible : Visibility.Collapsed;
            line.VerticalAlignment = kind == DropKind.After ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            line.Fill = Ui.Celadon;
        }
    }

    /// <summary>拖到列表的標題上時字換成青瓷綠（R103.4）</summary>
    void ShowHeaderDrop(bool on)
    {
        if (on) sectionTitle.Foreground = Ui.AccentText;
        else sectionTitle.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>「資料夾」那一段的鍵，照拖曳排好的順序（新的在後面，預設照名稱）</summary>
    public List<string> SidebarKeys() => SidebarDrag.Sorted(host.Settings, [.. State.Folders.Select(f => SidebarCollapse.FolderKey(f.Id))]);

    /// <summary>Alt+↑／↓：往上、往下移一格，焦點留在這一列（設定改了才重畫；到頭了不動）</summary>
    void MoveFolderRow(string key, int offset)
    {
        RememberFocus();
        RememberScroll();
        if (SidebarDrag.MoveBy(host.Settings, SidebarKeys(), key, offset)) return;
        pendingFocus = null;
        pendingScroll = null;
    }

    /// <summary>展開的會議、「顯示全部」「沒有會議」的一列：前面留圖示的空位，名稱跟資料夾名稱對齊</summary>
    static Grid PeekLine(FrameworkElement text, FrameworkElement? right) => Ui.Bar(new Border { Width = IconWidth }, text, right, 10);

    /// <summary>
    /// 資料夾、定期會議在原地展開的最近 5 場；超過時最後一列「顯示全部 N 場」（下面的列表只看它，定期會議是打開系列頁）；
    /// 沒有會議時一列「沒有會議」（R101.2）
    /// </summary>
    void AddPeek(List<MeetingRow> rows, string key, Action showAll)
    {
        if (rows.Count == 0)
        {
            var none = new ListViewItem
            {
                Content = PeekLine(Ui.Text(L.T("沒有會議"), 13, color: Ui.Secondary, wrap: false), null), IsEnabled = false, IsTabStop = false,
                MinHeight = 30, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(none, L.T("沒有會議"));
            scopes.Items.Add(none);
            return;
        }
        foreach (var row in rows.Take(SidebarCollapse.PeekCount)) scopes.Items.Add(PeekRow(row, key));
        if (rows.Count <= SidebarCollapse.PeekCount) return;
        var text = L.F("顯示全部 %lld 場", rows.Count);
        var item = new SidebarItem(() =>
        {
            detailView.FlushDrafts();
            showAll();
        })
        {
            Content = Ui.FollowForeground(PeekLine(Ui.Text(text, 13, color: Ui.Secondary, wrap: false), null)), MinHeight = 30,
        };
        SidebarLook.Row(item);
        AutomationProperties.SetName(item, text);
        AutomationProperties.SetAutomationId(item, "showall:" + key);
        scopes.Items.Add(item);
    }

    /// <summary>
    /// 展開的一場：標題一行；點了打開（人物模式換回會議、下面的列表不換）。選到時青瓷綠粗體（不用清單的選取：上面的選取是目前的範圍，
    /// 同一場也在下面的列表）；對比主題時粗體加一圈反白顏色的框。右鍵、「⋯」同會議列表（只這一場）
    /// </summary>
    SidebarItem PeekRow(MeetingRow row, string key)
    {
        var id = row.Id;
        var title = Ui.Text(row.Meeting.DisplayTitle, 13, wrap: false);
        title.Language = Ui.ContentLanguage(row.Meeting.MeetingLanguage);
        title.VerticalAlignment = VerticalAlignment.Center;
        var more = RowMore.Button(menu => FillRowMenu(menu, id, single: true));
        var frame = new Border { Child = PeekLine(title, more), CornerRadius = new CornerRadius(4), Padding = new Thickness(0, 1, 0, 1) };
        var item = SidebarLook.Row(new SidebarItem(() => OpenPeek(id), key: k => PeekKey(id, k)) { Content = Ui.FollowForeground(frame), MinHeight = 30 });
        AutomationProperties.SetName(item, row.Meeting.DisplayTitle);
        AutomationProperties.SetAutomationId(item, $"peek:{key}:{id}");
        var tip = $"{row.Meeting.MeetingDate}・{row.Meeting.DisplayTitle}";
        ToolTipService.SetToolTip(item, tip);
        AutomationProperties.SetHelpText(item, tip);
        item.ContextFlyout = RowMore.Context(menu => FillRowMenu(menu, id, single: true));
        // 拖到別的資料夾、定期會議、列表的標題、垃圾桶（R103.1）；在多選裡時拖整個多選
        item.DragData = data => BeginMeetingDrag(data, SidebarDrag.DragIds(id, State.MultiSelected));
        item.DropCompleted += (_, _) => EndDrag();
        var update = RowMore.Attach(item, more, () => PeekSelected(id));
        peekViews.Add(new PeekView(id, item, title, frame, update));
        return item;
    }

    /// <summary>展開的那一場是不是正在看的會議（會議模式、不在垃圾桶、沒有多選）</summary>
    bool PeekSelected(long id) => State.Sidebar == SidebarMode.Meetings && !State.ShowTrash && State.Selection == id && State.MultiSelected.Count < 2;

    void OpenPeek(long id)
    {
        detailView.FlushDrafts();
        // 人物模式換回會議時上面的選取換成「所有會議」，清單把焦點帶過去：焦點留在這一列，接著按 Enter 才是重新命名這場
        RememberFocus(any: true);
        State.OpenFromSidebar(id);
        RestoreFocus();
    }

    /// <summary>焦點在展開的會議時：Enter／F2 選到的那場重新命名、還沒選到的打開；Delete 選到的那場移到垃圾桶（先確認，R101、R100）</summary>
    bool PeekKey(long id, VirtualKey key)
    {
        if (Down(VirtualKey.Control) || Down(VirtualKey.Menu)) return false;
        var selected = PeekSelected(id);
        if (key is VirtualKey.Enter or VirtualKey.F2)
        {
            if (selected) RenameMeetingUi(id);
            else OpenPeek(id);
            return true;
        }
        if (key == VirtualKey.Delete && selected)
        {
            _ = ConfirmTrash([id]);
            return true;
        }
        return false;
    }

    /// <summary>展開的會議的「選到」樣子：值變了才改（每次重畫都會呼叫）</summary>
    void StylePeeks()
    {
        foreach (var peek in peekViews)
        {
            var selected = PeekSelected(peek.Id);
            if (peek.Selected == selected) continue;
            peek.Selected = selected;
            if (Ui.HighContrast)
            {
                // 對比主題：字跟著列的前景色（滑過去是反白的底，反白的顏色的字會看不見），改成粗體加一圈反白顏色的框
                peek.Title.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
                peek.Frame.BorderBrush = selected ? Ui.Celadon : null;
                peek.Frame.BorderThickness = new Thickness(selected ? 1 : 0);
            }
            // 選到時一塊灰底、字照常（R101.4；同下面列表選到的樣子）
            else peek.Item.Backplate(selected ? SidebarLook.Selected : null);
            peek.UpdateMore();
        }
        foreach (var view in sharedViews)
        {
            var selected = SharedSelected(view.Id);
            if (view.Selected == selected) continue;
            view.Selected = selected;
            view.Item.Backplate(selected && !Ui.HighContrast ? SidebarLook.Selected : null);
            view.UpdateMore();
        }
    }

    bool SharedSelected(string id) => State.Selection is null && State.SelectedShared == id && !State.ShowTrash;

    /// <summary>
    /// 「與我分享」的一列（R125.5，同 Mac 的 SharedRow）：圖示、標題、分享者・日期；沒匯入過或已更新的有圓點。
    /// 點了右邊顯示預覽；右鍵和「⋯」同一份（匯入、從清單移除）
    /// </summary>
    SidebarItem SharedRow(EnterpriseShares.SharedItem item)
    {
        var id = item.Id;
        var icon = new FontIcon { Glyph = "\uE896", FontSize = 16, Foreground = Ui.Secondary, Width = IconWidth, VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text(item.Title, 14, wrap: false);
        var caption = Ui.Text($"{item.From.Display} · {item.Meeting?.MeetingDate ?? ""}", 12, color: Ui.Secondary, wrap: false);
        var more = RowMore.Button(menu => FillSharedMenu(menu, id));
        FrameworkElement right = more;
        if (item.IsNew)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = Ui.Celadon, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(dot, item.ImportedVersion is null ? L.T("還沒匯入") : L.T("已更新"));
            right = Ui.Row(6, dot, more);
        }
        var bar = Ui.Bar(icon, Ui.Column(1, title, caption), right, 10);
        bar.VerticalAlignment = VerticalAlignment.Center;
        var row = SidebarLook.Row(new SidebarItem(() => { detailView.FlushDrafts(); State.SelectShared(id); })
        {
            Content = Ui.FollowForeground(bar), MinHeight = 44,
        });
        AutomationProperties.SetName(row, item.Title);
        AutomationProperties.SetAutomationId(row, "shared:" + id);
        AutomationProperties.SetHelpText(row, $"{item.From.Display} · {item.Meeting?.MeetingDate ?? ""}");
        if (item.IsNew) AutomationProperties.SetItemStatus(row, item.ImportedVersion is null ? L.T("還沒匯入") : L.T("已更新"));
        row.ContextFlyout = RowMore.Context(menu => FillSharedMenu(menu, id));
        var update = RowMore.Attach(row, more, () => SharedSelected(id));
        sharedViews.Add(new SharedView(id, row, update));
        return row;
    }

    /// <summary>「與我分享」的右鍵和「⋯」（R125.5，同 Mac 的 sharedMenu）</summary>
    void FillSharedMenu(MenuFlyout menu, string id)
    {
        menu.Items.Clear();
        if (host.Controller.SharedWithMe.FirstOrDefault(i => i.Id == id) is not { } item) return;
        menu.Items.Add(Ui.Item(item.ImportedVersion is null ? L.T("匯入") : L.T("再匯入一次"), () => ImportShared(item), glyph: "\uE896"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item(L.T("從清單移除"), () => _ = host.Controller.HideShared(id), icon: Symbol.Remove));
    }

    /// <summary>
    /// 選到「與我分享」的一場時右邊的預覽（R125.5，同 Mac 的 SharedPreview）：標題、分享者、日期、長度、大小、帶了哪些內容、到期日；匯入、從清單移除
    /// </summary>
    FrameworkElement SharedPreview(EnterpriseShares.SharedItem item)
    {
        var controller = host.Controller;
        var language = MeetingLanguages.Parse(item.Meeting?.Language) ?? MeetingLanguage.Zh;
        var title = Ui.Heading(item.Title, language, 26);
        title.IsTextSelectionEnabled = true;
        var from = Ui.Text(L.F("%@（%@）分享", item.From.Display, item.From.Email), 14, color: Ui.Secondary);
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string name, string value)
        {
            var label = Ui.Text(name, 13, color: Ui.Secondary, wrap: false);
            var text = Ui.Text(value, 13);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(label, grid.RowDefinitions.Count - 1);
            Grid.SetRow(text, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(text, 1);
            grid.Children.Add(label);
            grid.Children.Add(text);
        }
        if (item.Meeting?.MeetingDate is { Length: > 0 } date) Row(L.T("日期"), date);
        if (item.Meeting?.DurationSeconds is int seconds) Row(L.T("長度"), L.Ui(TimeFormat.Duration(seconds, MeetingLanguages.Interface)));
        Row(L.T("大小"), Shiyin.Services.Capture.Recorder.ByteCount(item.Bytes));
        var parts = new List<string> { L.T("逐字稿、紀錄") };
        if (item.Contents.Audio) parts.Add(L.T("錄音"));
        if (item.Contents.UserNotes) parts.Add(L.T("我的筆記"));
        if (item.Contents.Documents) parts.Add(L.T("參考文件"));
        Row(L.T("內容"), string.Join(MeetingLanguages.Ui("、", ", "), parts));
        Row(L.T("到期"), item.ExpiresAt is { } stamp ? ShareDialog.Day(stamp) : L.T("不限期"));
        if (item.ImportedVersion is not null) Row(L.T("狀態"), item.IsNew ? L.T("分享者更新過，可以再匯入一次") : L.T("已匯入"));
        var note = Ui.Text(L.T("匯入後是你自己的一份，可以編輯、重新整理，跟分享者那份無關。"), 12, color: Ui.Secondary);
        var import = Sheet.Pill(item.ImportedVersion is null ? L.T("匯入") : L.T("再匯入一次"), Sheet.Kind.Primary,
            click: () => ImportShared(item));
        var hide = Sheet.Pill(L.T("從清單移除"), click: () => _ = controller.HideShared(item.Id));
        import.IsEnabled = hide.IsEnabled = !controller.ShareBusy;
        var buttons = Ui.Row(10, import, hide);
        if (controller.ShareBusy && controller.ShareProgress is double progress)
            buttons.Children.Add(new ProgressBar { Value = progress * 100, Maximum = 100, Width = 160, VerticalAlignment = VerticalAlignment.Center });
        var content = Ui.Column(14, title, from, grid, note, buttons);
        content.Padding = new Thickness(28);
        AutomationProperties.SetName(content, item.Title);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>點了分享的通知（R125.5）：重新查一次，選到那一場</summary>
    public async void ShowShared(string id)
    {
        await host.Controller.RefreshSharedWithMe(notify: false);
        if (host.Controller.SharedWithMe.Any(i => i.Id == id)) State.SelectShared(id);
    }

    /// <summary>「匯入」「再匯入一次」（R125.5）：之前匯入的那份還在時先問取代或另存</summary>
    /// async void：沒預期的例外在這裡接住顯示（WinUI 不會把 UI 執行緒上的例外交給 UnhandledException，程序直接結束）
    public async void ImportShared(EnterpriseShares.SharedItem item)
    {
        try
        {
            if (host.Controller.IsShareBusy()) return;
            var previous = host.Controller.PreviousImports(item.Id);
            List<long> replacing = [];
            if (previous.Count > 0)
            {
                if (await Dialogs.ChooseReimport(this, L.F("再匯入「%@」？", item.Title), null) is not { } replace) return;
                if (replace) replacing = previous;
            }
            await host.Controller.ImportShared(item, State.CurrentFolder, replacing);
        }
        catch (Exception e)
        {
            Log.Error($"import shared failed: {e.GetType().Name}");
            ShowError(e.Message);
        }
    }

    /// <summary>shiyin:share（網頁的「用拾音打開」，R125.6）：先拿標題、分享者，問過再匯入</summary>
    public async void OpenShareLink(string url)
    {
        try { await OpenShareLinkAsync(url); }
        catch (Exception e)
        {
            Log.Error($"open share link failed: {e.GetType().Name}");
            ShowError(e.Message);
        }
    }

    async Task OpenShareLinkAsync(string url)
    {
        if (EnterpriseShares.ParseShareLink(url, host.Enterprise.AllowLocalHttp) is not { } link)
        {
            ShowError(L.T("這個分享的連結不對"));
            return;
        }
        if (host.Controller.IsShareBusy()) return;
        EnterpriseShares.Summary summary;
        try { summary = await host.Controller.ShareSummary(link); }
        catch (Exception e) when (e is EnterpriseException or HttpRequestException)
        {
            ShowError(e.Message);
            return;
        }
        var title = summary.Meeting is { Title.Length: > 0 } m ? m.Title : L.T("未命名的會議");
        var message = L.F("匯入%@分享的「%@」？", summary.From.Display, title);
        var previous = host.Controller.PreviousImports(link.Id);
        List<long> replacing = [];
        if (previous.Count == 0)
        {
            var ok = await Dialogs.Confirm(this, message, L.F("來自 %@。匯入後是你自己的一份，可以編輯、重新整理。", EnterpriseServer.DisplayHost(link.Server)), L.T("匯入"));
            if (!ok) return;
        }
        else
        {
            if (await Dialogs.ChooseReimport(this, message, L.F("來自 %@。", EnterpriseServer.DisplayHost(link.Server))) is not { } replace) return;
            if (replace) replacing = previous;
        }
        await host.Controller.ImportShare(link, summary.From.Display, summary.Bytes, summary.Version, State.CurrentFolder, replacing, viaLink: true);
    }

    /// <summary>
    /// 一列：圖示、名字（所有會議、人物：點了換下面的列表）。右邊不放數量（R101.4），無障礙的值唸得到；
    /// 「所有會議」是回到預設的畫面，不畫選到的樣子，人物模式時「人物」亮
    /// </summary>
    ListViewItem ScopeRow(string name, int count, Scope scope, FrameworkElement icon)
    {
        var item = SidebarLook.Row(new ListViewItem
        {
            Tag = scope, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 36,
            Content = Ui.FollowForeground(Ui.Bar(icon, Ui.Text(name, 14, wrap: false), null, 10)),
        }, showSelected: scope.People);
        AutomationProperties.SetName(item, name);
        SidebarLook.Status(item, null, scope.People ? L.F("%lld 人", count) : L.F("%lld 場", count));
        AutomationProperties.SetAutomationId(item, "scope:" + (scope.People ? "people" : "all"));
        return item;
    }

    /// <summary>資料夾的動作（R100、同 Mac 的 FolderMenuItems）：右鍵、「⋯」</summary>
    void FillFolderMenu(MenuFlyout menu, long folder)
    {
        // 點資料夾是在原地展開（R101.2）；只看它從展開後的「顯示全部」（選單不放「只看這個資料夾」，R101.2、M§75）
        // 每一項有圖示、刪除是紅字（R101.4，同 Mac 的 FolderMenuItems）
        menu.Items.Add(MenuLook.Hint(Ui.Item(L.T("重新命名…"), () => RenameUi(new RenameTarget.Folder(folder)), icon: Symbol.Edit), MenuLook.Rename));
        menu.Items.Add(Ui.Item(State.RuleFor(folder) is null ? L.T("自動加入的條件…") : L.T("編輯自動加入的條件…"),
            () => _ = Dialogs.FolderRule(this, folder), icon: Symbol.Setting));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuLook.Hint(Ui.DangerItem(L.T("刪除資料夾…"), () => _ = ConfirmDeleteFolder(folder)), MenuLook.Delete));
    }

    // MARK: 顯示方式（R97、M§71）

    /// <summary>「顯示方式」按鈕：細線的滑桿圖示（SidebarLook.HeaderIcon），打開時才建選單（篩選的人、範本會變）</summary>
    Button ViewOptionsButton(bool trash)
    {
        var button = new Button();
        ConfigureViewOptions(button, trash);
        return button;
    }

    void ConfigureViewOptions(Button button, bool trash)
    {
        ToolTipService.SetToolTip(button, trash ? L.T("排序方式") : L.T("排序、分組、篩選會議"));
        AutomationProperties.SetName(button, L.T("顯示方式"));
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Opening += (_, _) => Ui.Guard(() => FillViewOptions(menu, trash));
        button.Flyout = menu;
        StyleViewOptions(button, force: true);
    }

    /// <summary>
    /// 「顯示方式」（R97、M§75 的 SidebarHeaderIcon，7fb4224）：跟「資料夾」的「+」同一種細線的圖示，Segoe Fluent 的 Equalizer（E9E9，橫的三條滑桿）
    ///（同 Mac 的 slider.horizontal.3）；沒篩選時灰色、滑過變亮，篩選中是強調色的字（原本有圈的漏斗、實心的強調色按鈕）。垃圾桶裡不篩選，一直是灰的
    /// </summary>
    void StyleViewOptions(Button button, bool force = false)
    {
        var active = button != viewOptions ? false : State?.Filter.IsActive == true;
        if (!force && button.Tag is bool shown && shown == active) return;
        button.Tag = active;
        SidebarLook.HeaderIcon(button, "\uE9E9", active);
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
        // 會議可以拖到資料夾、定期會議、標題、垃圾桶（R103.1）；垃圾桶裡、搜尋結果、人物不能拖
        list.CanDragItems = !State.ShowsPeople && !State.SearchActive && !State.ShowTrash;
        // 會議列表、人物列表收起來時只剩上面的標題列（R101.1）；垃圾桶、搜尋結果不收合
        var collapsed = SidebarCollapse.Collapsed(host.Settings);
        if (State.ShowsPeople)
        {
            if (!collapsed.Contains("people")) BuildPeopleList();
        }
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
                    var hitItem = SidebarLook.Row(new ListViewItem { Tag = hit, Content = Ui.FollowForeground(Ui.Column(2, Ui.Caption(KindLabel(hit.Kind)), snippet)) });
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
            var open = State.ShowTrash || !collapsed.Contains("meetings");
            // 「其他會議」是空的（會議都在資料夾裡，R101.6）
            if (open && State.ListShowsOthersOnly && State.FilteredMeetings.Count == 0)
                list.Items.Add(new ListViewItem
                {
                    Content = Ui.Text(L.T("不在資料夾裡的會議會列在這裡"), 13, color: Ui.Secondary), IsEnabled = false, IsTabStop = false, Padding = new Thickness(16, 8, 12, 4),
                });
            foreach (var group in open ? groups : [])
            {
                if (grouped)
                {
                    // 每一組的標題可以收起（R101.1）：SidebarItem，上下鍵走得到但選不到
                    var key = SidebarCollapse.GroupKey(group);
                    var groupOpen = !collapsed.Contains(key);
                    list.Items.Add(SectionItem(key, group.Title, groupOpen, top: 10));
                    if (!groupOpen) continue;
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
            // 右鍵和右邊的「⋯」（R85.7、R100）：跟人物頁的「更多」同一份（重新命名…、合併到 ▸、移除這個人…），打開時才建
            var personId = person.Id;
            void Fill(MenuFlyout menu)
            {
                if (State.People.FirstOrDefault(p => p.Id == personId) is { } current)
                    foreach (var entry in personPage.MenuItems(current, sidebar: true)) menu.Items.Add(entry);
            }
            var more = RowMore.Button(Fill);
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 1);
            Grid.SetColumn(more, 2);
            row.Children.Add(avatar);
            row.Children.Add(text);
            row.Children.Add(more);
            var item = SidebarLook.Row(new ListViewItem { Tag = person, Content = Ui.FollowForeground(row), HorizontalContentAlignment = HorizontalAlignment.Stretch });
            // 列裡是好幾個元件：螢幕閱讀器、UI Automation 讀的名稱
            AutomationProperties.SetName(item, name + MeetingLanguages.Ui("，", ", ") + summary);
            item.ContextFlyout = RowMore.Context(Fill);
            RowMore.Attach(item, more, () => item.IsSelected);
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
            var people = new System.Text.StringBuilder("people").Append(SidebarCollapse.IsSectionExpanded(host.Settings, "people"));
            foreach (var p in State.People)
                people.Append('\u001E').Append(p.Id).Append('\u001F').Append(p.Name).Append('\u001F').Append(p.MeetingCount)
                    .Append('\u001F').Append(p.LastMeetingDate).Append('\u001F').Append(p.IsMe);
            return people.ToString();
        }
        var text = new System.Text.StringBuilder(State.ShowTrash ? "trash" : "meetings");
        // 分組（R97.2）：每組的標題跟著分組方式、語言
        text.Append('\u001D').Append(State.EffectiveGrouping.Code()).Append('\u001D').Append(State.Sort.Code()).Append('\u001D').Append(State.ListShowsOthersOnly);
        // 收起來的段落（會議列表、分組的每一組，R101）
        text.Append('\u001D').Append(string.Join(',', SidebarCollapse.Collapsed(host.Settings).Order(StringComparer.Ordinal)));
        // 右鍵選單的「移到資料夾」列出所有資料夾：資料夾變了也要重建
        foreach (var (folderId, name, _) in State.Folders) text.Append('\u001D').Append(folderId).Append('\u001F').Append(name);
        foreach (var row in State.FilteredMeetings)
            text.Append('\u001E').Append(row.Id).Append('\u001F').Append(row.Meeting.DisplayTitle).Append('\u001F').Append(row.Meeting.MeetingDate)
                .Append('\u001F').Append(row.Meeting.FolderId).Append('\u001F').Append(row.Meeting.Language);
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
        // 右邊的「⋯」（R100）：滑過、選到時顯示，跟右鍵同一份選單
        var id = row.Id;
        var more = RowMore.Button(menu => FillRowMenu(menu, id));
        var content = Ui.Split(column, more, 4, new GridLength(1, GridUnitType.Star));
        Grid.SetColumn(column, 0);
        content.ColumnDefinitions[1].Width = GridLength.Auto;
        var item = SidebarLook.Row(new ListViewItem { Tag = row.Id, Content = Ui.FollowForeground(content), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        RowMore.Attach(item, more, () => item.IsSelected);
        return new RowView(item, column, status);
    }

    /// <summary>狀態、顏色、轉錄的進度條，只在值變了才設（每秒呼叫一次）</summary>
    void SetStatus(RowView view, MeetingRow row)
    {
        var status = host.Controller.Status(row);
        if (view.Status.Text != status.Label)
        {
            view.Status.Text = status.Label;
            // 對比主題：跟著列的前景色（選到、滑過時是反白的字），不用自己的顏色（Ui.FollowForeground）
            if (Ui.HighContrast) view.Status.ClearValue(TextBlock.ForegroundProperty);
            else view.Status.Foreground = status switch
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
        MenuLook.Apply(menu);
        // 選單的樣子（R105、design-windows §11.38）
        menu.Opening += (_, _) => Ui.Guard(() => { FillRowMenu(menu, id); MenuLook.Apply(menu); });
        return menu;
    }

    /// <param name="single">只這一場、不管多選（資料夾展開的會議，R101，同 Mac 的 MeetingMenuItems(ids: [id])）</param>
    void FillRowMenu(MenuFlyout menu, long id, bool single = false)
    {
        menu.Items.Clear();
        var multi = State.MultiSelected;
        List<long> ids = !single && multi.Count > 1 && multi.Contains(id) ? multi : [id];
        if (State.ShowTrash)
        {
            menu.Items.Add(Ui.Item(L.T("還原"), () => host.Controller.Restore(ids), icon: Symbol.Undo));
            menu.Items.Add(MenuLook.Hint(Ui.DangerItem(L.T("永久刪除…"), () => _ = ConfirmPurge(ids)), MenuLook.Delete));
            return;
        }
        // 先轉錄這場（R122、M§96.3）：有排隊等轉錄、還不在最前面時才有，放第一項
        if (PrioritizeTitle(ids) is { } prioritize)
        {
            menu.Items.Add(Ui.Item(prioritize, () => host.Controller.PrioritizeTranscription(ids, announce: true), icon: Symbol.Up));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        // 一場：重新命名…（重新命名的對話框，R100、R102）
        if (ids.Count == 1)
        {
            menu.Items.Add(MenuLook.Hint(Ui.Item(L.T("重新命名…"), () => RenameMeetingUi(id), icon: Symbol.Edit), MenuLook.Rename));
            // 編輯會議（R107.1）；編輯過的會議多一項還原，右邊寫還剩幾天（R107.8）
            foreach (var item in EditMenuItems(id)) menu.Items.Add(item);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(FolderSubMenu(ids, CommonFolder(ids)));
        menu.Items.Add(new MenuFlyoutSeparator());
        // 一場直接移（可以還原）；好幾場先確認（M§62）
        menu.Items.Add(MenuLook.Hint(ids.Count == 1 ? Ui.DangerItem(L.T("移到垃圾桶"), () => host.Controller.MoveToTrash(ids))
            : Ui.DangerItem(L.F("把 %lld 場會議移到垃圾桶…", ids.Count), () => _ = ConfirmTrash(ids)), MenuLook.Delete));
    }

    /// <summary>「先轉錄這場」的字（選單、多選畫面的按鈕，同 Mac 的 prioritizeTitle）；不能做（沒有排隊的、已經在最前面）是 null</summary>
    string? PrioritizeTitle(IReadOnlyList<long> ids)
    {
        var targets = AppController.PrioritizeTargets(State.TranscribeQueue, ids);
        if (targets.Count == 0) return null;
        if (ids.Count == 1) return L.T("先轉錄這場");
        return targets.Count == ids.Count ? L.F("先轉錄這 %lld 場", targets.Count) : L.F("先轉錄其中排隊的 %lld 場", targets.Count);
    }

    /// <summary>「編輯會議…」、編輯過的會議多「還原成編輯前的樣子…」（右邊「還有 N 天」）：右鍵、「⋯」、會議頁的「更多」共用</summary>
    public List<MenuFlyoutItem> EditMenuItems(long id)
    {
        var items = new List<MenuFlyoutItem>
        {
            Ui.Item(L.T("編輯會議…"), () => OpenEditor(id), icon: Symbol.Cut),
            // 分享（R125.1）：透過企業伺服器或用檔案
            Ui.Item(L.T("分享…"), () => _ = ShareDialog.Show(this, id), icon: Symbol.Share),
        };
        if (host.Db.EditRecord(id) is { } record)
            items.Add(MenuLook.Hint(Ui.Item(L.T("還原成編輯前的樣子…"), () => _ = ConfirmRestoreEdit(id), icon: Symbol.Undo), L.F("還有 %lld 天", record.DaysLeft())));
        return items;
    }

    /// <summary>這幾場會議都在同一個資料夾時是那個資料夾（FolderId 是 null：都不在資料夾裡），不一樣時是 null</summary>
    CommonFolder? CommonFolder(IReadOnlyList<long> ids) => AppController.CommonFolderOf(State.ShowTrash ? State.Trash : State.Meetings, ids);

    /// <summary>「移到資料夾」選單：既有的資料夾、新資料夾、移出資料夾（R9.2）；current 是這場會議的資料夾（null 是不在資料夾）</summary>
    public MenuFlyoutSubItem FolderMenu(long id, long? current) => FolderSubMenu([id], new CommonFolder(current));

    MenuFlyoutSubItem FolderSubMenu(IReadOnlyList<long> ids, CommonFolder? current)
    {
        var sub = new MenuFlyoutSubItem { Text = L.T("移到資料夾"), Icon = new SymbolIcon(Symbol.Folder) };
        foreach (var item in FolderMenuItems(ids, current)) sub.Items.Add(item);
        return sub;
    }

    /// <summary>
    /// 「移到資料夾」的項目（同 Mac 的 FolderMenu，M§62）：都在同一個資料夾（current）時不列那個資料夾、最後是「移出「名稱」」；
    /// 在不同的資料夾（current 是 null，多選時）列全部、最後是「移出資料夾」；都不在資料夾裡時沒有「移出」
    /// </summary>
    List<MenuFlyoutItemBase> FolderMenuItems(IReadOnlyList<long> ids, CommonFolder? current)
    {
        var items = new List<MenuFlyoutItemBase>();
        // 有自動加入條件的資料夾、空的資料夾沒有會議也列出來（M§72、§78）
        var others = State.Folders.Where(f => current is null || f.Id != current.FolderId).ToList();
        foreach (var (folderId, name, _) in others) items.Add(Ui.Item(name, () => host.Controller.MoveToFolder(ids, folderId), icon: Symbol.Folder));
        if (others.Count > 0) items.Add(new MenuFlyoutSeparator());
        items.Add(Ui.Item(L.T("新資料夾…"), () => { if (ids.Count > 0) RenameUi(new RenameTarget.NewFolder([.. ids])); }, icon: Symbol.NewFolder));
        if (current is null) items.Add(Ui.Item(L.T("移出資料夾"), () => host.Controller.MoveToFolder(ids, null), icon: Symbol.Remove));
        else if (current.FolderId is long folder)
            items.Add(Ui.Item(L.F("移出「%@」", State.FolderName(folder)), () => host.Controller.MoveToFolder(ids, null), icon: Symbol.Remove));
        return items;
    }

    // MARK: 匯出的進度、取消（R120、design-windows §11.53）

    readonly ExportProgress exports = new();
    /// <summary>
    /// 側欄下方匯出的那一條：開始時建一次，之後只換字和進度。BuildFooter 每次都重建其他提示，這一條也重建的話
    /// 字和進度條還在舊的 Row 裡，放進新的會丟 0x800F1000（同錄音的音量條、提問的輸入框）
    /// </summary>
    (FrameworkElement Row, TextBlock Text, ProgressBar Bar, HyperlinkButton Cancel)? exportRow;

    /// <summary>正在匯出的檔名；沒有在匯出時是 null（匯出對話框用來擋第二個匯出）</summary>
    public string? ExportingName => exports.Current?.Name;

    /// <summary>
    /// 開始匯出：側欄下方常駐「正在匯出 週會.m4a… 45%」、「取消」、進度條。回傳給匯出用的回報（在背景呼叫也可以），
    /// 用完 Dispose 拿掉那一條；已經有一個在匯出時回傳 null
    /// </summary>
    public ExportHandle? StartExport(string name, CancellationTokenSource cancel)
    {
        if (exports.TryStart(name, cancel) is not { } run) return null;
        var text = Ui.Text(ExportProgress.Status(run), 12, color: Ui.Secondary, wrap: false);
        text.VerticalAlignment = VerticalAlignment.Center;
        var link = Ui.Link(L.T("取消"), () => CancelExport(run.Id));
        // 檔名長的時候字變成「…」，「取消」一直在右邊、不會被擠出側欄，也不會跟著百分比移動
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(link, 1);
        line.Children.Add(text);
        line.Children.Add(link);
        var bar = new ProgressBar { Maximum = 1, IsIndeterminate = true };
        exportRow = (Ui.Column(2, line, bar), text, bar, link);
        BuildFooter();
        return new ExportHandle(this, run.Id);
    }

    void ReportExport(long id, double value)
    {
        if (!exports.Report(id, value) || exportRow is not { } row || exports.Current is not { } run) return;
        row.Bar.IsIndeterminate = false;
        row.Bar.Value = run.Value ?? 0;
        row.Text.Text = ExportProgress.Status(run);
    }

    void CancelExport(long id)
    {
        if (!exports.Cancel(id) || exportRow is not { } row || exports.Current is not { } run) return;
        row.Text.Text = ExportProgress.Status(run);
        row.Bar.IsIndeterminate = true;
        row.Cancel.IsEnabled = false;
    }

    void EndExport(long id)
    {
        if (!exports.End(id)) return;
        exportRow = null;
        BuildFooter();
    }

    /// <summary>一次匯出的回報：進度排回 UI 執行緒；Dispose（在 UI 執行緒）拿掉側欄下方那一條</summary>
    public sealed class ExportHandle(MainWindow window, long id) : IProgress<double>, IDisposable
    {
        public void Report(double value) => window.host.Post(() => window.ReportExport(id, value));
        public void Dispose() => window.EndExport(id);
    }

    void BuildFooter()
    {
        footer.Children.Clear();
        if (exportRow is { } export)
        {
            if (export.Row.Parent is Panel old) old.Children.Remove(export.Row);
            footer.Children.Add(export.Row);
        }
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
        // 會議拖到這裡＝移到垃圾桶（R103.1）：不確認，同右鍵的一場（可以還原，提示照舊）；拖過去時字換成青瓷綠
        if (!State.ShowTrash)
        {
            trash.AllowDrop = true;
            trash.DragOver += (_, e) =>
            {
                if (DraggingMeetings(e) is null) return;
                if (dropRow is { } old) ShowRowDrop(old.Item, old.Drop, null);
                dropRow = null;
                ShowHeaderDrop(false);
                ShowTrashDrop(trash);
                e.AcceptedOperation = DataPackageOperation.Move;
                Caption(e, L.T("移到垃圾桶"));
                e.Handled = true;
            };
            trash.DragLeave += (_, _) => ShowTrashDrop(null);
            trash.Drop += (_, e) =>
            {
                ShowTrashDrop(null);
                if (DraggingMeetings(e) is not { } ids) return;
                e.Handled = true;
                EndDrag();
                host.Controller.MoveToTrash(ids);
            };
        }
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
        // 搜尋結果點進去：清掉搜尋後看得到它在會議列表的哪裡（R101.1）
        State.ExpandToShow(hit.MeetingId);
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

    /// <summary>
    /// 從檔案總管拖到資料夾上的檔案（R103.3，同 Mac 的 importDropped）：匯入到那個資料夾（錄音、影片跳匯入的對話框，資料夾先設好）；
    /// 參考文件（PDF 這類）要拖到會議頁，全部是參考文件時說一聲。DEBUG 的 importinto 也走這裡
    /// </summary>
    public void ImportInto(IReadOnlyList<string> files, long folder)
    {
        var importable = files.Where(f => !DocumentReader.IsDroppedDocument(f)).ToList();
        if (importable.Count == 0)
        {
            ShowError(L.T("參考文件請拖到會議頁，會加進那場會議"));
            return;
        }
        var media = host.Controller.RequestImport(importable, folder, State.Selection);
        if (media.Count > 0) _ = Dialogs.Import(this, media, folder);
    }

    // 確認對話框開著時要處理的會議、那一句（DEBUG 的 state，同 Mac 的 deleteTargets、confirmPurge、confirmEmptyTrash、splitMeetingRequest）
    public List<long> DeleteTargets { get; private set; } = [];
    public List<long> PurgeTargets { get; private set; } = [];
    public bool ConfirmingEmptyTrash { get; private set; }
    public long? SplitMeetingRequest { get; private set; }

    public Task ConfirmTrash(long id) => ConfirmTrash([id]);

    /// <summary>移到垃圾桶前確認，寫出場數（R88.2）；確認的按鈕紅底（R106.2，同 Mac），Enter 是確認</summary>
    public async Task ConfirmTrash(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        DeleteTargets = [.. ids];
        bool ok;
        try
        {
            ok = await Dialogs.Confirm(this, ids.Count > 1 ? L.F("把 %lld 場會議移到垃圾桶？", ids.Count) : L.T("把這場會議移到垃圾桶？"),
                L.T("30 天內都可以從垃圾桶還原。"), L.T("移到垃圾桶"), destructive: true);
        }
        finally { DeleteTargets = []; }
        if (ok) host.Controller.MoveToTrash(ids);
    }

    public Task ConfirmPurge(long id) => ConfirmPurge([id]);

    /// <summary>永久刪除前確認，寫出場數（R88.2）；沒辦法還原，確認的按鈕紅底、Enter 是確認（R106.2）</summary>
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

    /// <summary>「清空垃圾桶…」（R88.3）：沒辦法還原，確認的按鈕紅底、Enter 是確認（R106.2）</summary>
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
    /// （跟設定頁的「刪除聲音特徵」同一個動作）；確認的按鈕紅底、Enter 是確認（R106.2）。移除的是正在看的人時回到沒選人
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

    static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// 側欄的 Enter／F2（R100、同 Mac 的 sidebarReturn）：人物模式選到人 → 重新命名那個人；選到一場會議（不是多選）→ 重新命名會議；
    /// 沒選會議而在看資料夾 → 重新命名它。垃圾桶、搜尋中不處理。回傳有沒有處理
    /// </summary>
    public bool SidebarRename()
    {
        if (State.ShowTrash || State.SearchActive) return false;
        if (State.ShowsPeople)
        {
            if (State.SelectedPerson is not long person) return false;
            RenamePersonUi(person);
            return true;
        }
        if (State.MultiSelected.Count > 1) return false;
        if (SelectedMeetings() is [var id]) RenameMeetingUi(id);
        else if (State.FolderFilter is long folder) RenameUi(new RenameTarget.Folder(folder));
        else return false;
        return true;
    }

    /// <summary>
    /// 側欄的 Delete（R88.2、R85.7、R100，同 Mac 的 sidebarDelete）：人物 → 移除；選到的會議（多選時全部）→ 移到垃圾桶（垃圾桶裡是永久刪除）；
    /// 沒選會議而在看資料夾 → 刪除資料夾。都先確認
    /// </summary>
    public bool SidebarDelete()
    {
        if (State.SearchActive) return false;
        if (State.ShowsPeople)
        {
            if (State.SelectedPerson is not long person) return false;
            _ = ConfirmRemovePerson(person);
            return true;
        }
        var ids = SelectedMeetings();
        if (ids.Count > 0) _ = State.ShowTrash ? ConfirmPurge(ids) : ConfirmTrash(ids);
        else if (!State.ShowTrash && State.FolderFilter is long folder) _ = ConfirmDeleteFolder(folder);
        else return false;
        return true;
    }

    /// <summary>
    /// 重新命名（R102、design-windows §11.35）：會議、資料夾、新資料夾、定期會議、人物、標記都打開同一種對話框；已經開著時不理
    /// </summary>
    public void RenameUi(RenameTarget target) => _ = RenameAndRefocus(target);

    /// <summary>
    /// 對話框關掉後焦點回到原本那一列（同 Mac：sheet 關掉後清單照樣有焦點，接著按 F2、Delete 都對）。ContentDialog 自己還原時落到視窗的第一個元件
    ///（錄音按鈕，2026-11-16 虛擬機）；存了之後清單可能重建（改了標題），等重畫完再找：原本的元件還在就用它，不在了就是清單選到的那一列、側欄的同一列
    /// </summary>
    async Task RenameAndRefocus(RenameTarget target)
    {
        if (Dialogs.RenamingTarget is not null || root.XamlRoot is not { } xamlRoot) return;
        var focused = FocusManager.GetFocusedElement(xamlRoot) as Control;
        var state = focused?.FocusState ?? FocusState.Unfocused;
        var inList = focused is ListViewItem item && list.Items.Contains(item);
        var remembered = focused is null ? "" : AutomationProperties.GetAutomationId(focused);
        await Dialogs.Rename(this, target);
        if (focused is null || state == FocusState.Unfocused) return;
        void Restore()
        {
            if (Dialogs.RenamingTarget is not null) return;
            // 清單重建時舊的列拿掉了，IsLoaded 還沒變（Unloaded 晚一點才來），看它還在不在清單裡
            var gone = focused is ListViewItem old && !list.Items.Contains(old) && !scopes.Items.Contains(old);
            Control? target = !gone && focused.IsLoaded ? focused
                : inList ? list.SelectedItem as ListViewItem
                : scopes.Items.OfType<ListViewItem>().FirstOrDefault(i => remembered.Length > 0 && AutomationProperties.GetAutomationId(i) == remembered);
            target?.Focus(state == FocusState.Pointer ? FocusState.Programmatic : state);
        }
        // 存了之後資料改了會重畫（0.15 秒內合併一次）：等重畫、排好版再找；沒改（取消）時 0.6 秒後
        var done = false;
        var timer = root.DispatcherQueue.CreateTimer();
        void Run()
        {
            if (done) return;
            done = true;
            State.Changed -= Run;
            timer.Stop();
            root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(Restore));
        }
        State.Changed += Run;
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Run();
        timer.Start();
    }

    /// <summary>
    /// 重新命名一場會議（側欄的右鍵、「⋯」、Enter／F2，R100）：重新命名的對話框（R102；日期、與會者照舊在會議頁的「編輯」改）
    /// </summary>
    public void RenameMeetingUi(long id) => RenameUi(new RenameTarget.Meeting(id));

    /// <summary>確認對話框開著時要刪的資料夾（id；DEBUG 的 state，同 Mac 的 confirmDeleteFolder）</summary>
    public long? DeletingFolder { get; private set; }

    /// <summary>
    /// 刪除資料夾（R100）：會議留著（移到「不在資料夾」），自動加入的條件一起刪；確認的按鈕紅底、Enter 是確認（R106.2）。
    /// 側欄的右鍵、「⋯」、Delete 都走這裡
    /// </summary>
    public async Task ConfirmDeleteFolder(long folder)
    {
        if (DeletingFolder is not null) return;
        var count = State.Meetings.Count(m => m.Meeting.FolderId == folder);
        DeletingFolder = folder;
        bool ok;
        try
        {
            ok = await Dialogs.Confirm(this, L.F("刪除資料夾「%@」？", State.FolderName(folder)),
                count > 0 ? L.F("裡面的 %lld 場會議會留著，移到「不在資料夾」。自動加入的條件也會刪掉。", count) : L.T("自動加入的條件也會刪掉。"),
                L.T("刪除資料夾"), destructive: true);
        }
        finally { DeletingFolder = null; }
        if (!ok) return;
        if (State.FolderFilter == folder) State.ShowMeetings(null);
        host.Controller.DeleteFolder(folder);
    }

    /// <summary>重新命名一個人（人物頁的「更多」、側欄右鍵、Enter／F2、DEBUG 的 personrenameui）：選那個人、打開重新命名的對話框（R102）</summary>
    public void RenamePersonUi(long id)
    {
        State.SelectPerson(id);
        RenameUi(new RenameTarget.Person(id));
    }

    /// <summary>「從這裡分成兩場…」（R90.1、M§64）：寫出從幾分幾秒開始、會怎麼分；30 天內可以還原（D180）。確認的按鈕是主要按鈕（不是刪除類，R106.2，同 Mac）</summary>
    public async Task ConfirmSplitMeeting(long meetingId, Segment segment)
    {
        if (segment.Id is not long segmentId) return;
        SplitMeetingRequest = segmentId;
        bool ok;
        try
        {
            ok = await Dialogs.Confirm(this, L.F("從 %@ 這句開始分成另一場會議？", TimeFormat.String(segment.Start)),
                L.T("這句和之後的逐字稿、錄音、標記重點變成新的一場，前面的留在這場；我的筆記、參考文件、提問留在這場。兩場都要重新整理會議紀錄。30 天內可以從「更多」還原。"),
                L.T("分成兩場"));
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
    public void ShowError(string message)
    {
        // 分享對話框開著時寫在對話框裡（同時只能開一個 ContentDialog）
        if (ShareDialog.Current is { } share)
        {
            share.ShowError(message);
            return;
        }
        _ = Dialogs.Alert(this, L.T("出了一點問題"),
            string.Join("\n", message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()));
    }
}
