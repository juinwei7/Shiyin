using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Core;

namespace Shiyin.App.Views;

/// <summary>
/// 編輯會議的畫面（R107、M§81.5、design-windows §11.40）：主視窗上的覆蓋層（不是 ContentDialog：裡面還要再開確認和選會議的彈窗，
/// 同時只能開一個 ContentDialog）。時間軸、試聽、跟著播放的逐字稿、動作列、摘要、取消／套用
/// </summary>
public sealed class MeetingEditView : IDisposable
{
    readonly AppHost host;
    readonly MainWindow window;
    public MeetingEditor Editor { get; }
    public long MeetingId { get; }
    public Grid Root { get; } = new() { RowSpacing = 12 };
    /// <summary>
    /// 放進卡片的是這個：卡片夠高時跟原本一樣（Root 的高度＝卡片），太矮時（150% 縮放的筆電、視窗拉到最小）Root 撐到逐字稿至少留
    /// ListMinHeight、整張卡片上下捲，套用、取消不會被切掉（2026-11-17 虛擬機 150%：逐字稿剩 0、下面的按鈕被切掉一半）
    /// </summary>
    public ScrollViewer Host { get; } = new()
    {
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ZoomMode = ZoomMode.Disabled,
    };
    const double ListMinHeight = 112;
    readonly EditTimeline timeline;
    readonly EditPreview preview;
    readonly TextBlock subtitle = Sheet.Subtitle("");
    readonly Button play = new() { Width = 32, Height = 32, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(16) };
    readonly TextBlock time = Ui.Text("00:00", 15, wrap: false);
    readonly StackPanel legend = new() { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly Slider zoom = new() { Width = 90, Minimum = 0, Maximum = Math.Log(MeetingEditor.MaxZoom), StepFrequency = 0.01, VerticalAlignment = VerticalAlignment.Center };
    readonly Button zoomOut, zoomIn;
    readonly Button setStart, setEnd, deleteSelection, cutButton, mergeButton;
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Extended, IsItemClickEnabled = true, Padding = new Thickness(0, 2, 0, 2) };
    readonly Dictionary<long, Row> rows = [];
    readonly TextBlock summary = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly Button apply;
    readonly CancellationTokenSource peaksCancel;
    long? currentLine, hovered;
    string? rowsKey, footerKey, legendKey;
    bool updatingZoom, updatingSelection, closed;

    /// <summary>DEBUG 的 editorstate、state：目前打開的編輯畫面</summary>
    public static MeetingEditView? Current { get; private set; }

    /// <summary>關掉（取消、套用、放棄）：主視窗收起覆蓋層</summary>
    public event Action? Closed;

    sealed record Row(ListViewItem Item, Grid Grid, TextBlock Time, TextBlock Text, Button Action);

    static readonly Lazy<DataTemplate> RowTemplate = new(() => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><ContentControl IsTabStop=\"False\" HorizontalContentAlignment=\"Stretch\" HorizontalAlignment=\"Stretch\"/></DataTemplate>"));

    public MeetingEditView(AppHost host, MainWindow window, long meetingId)
        : this(host, window, meetingId, new MeetingEditor(host.Controller, meetingId), new CancellationTokenSource(), load: true) { }

    /// <summary>load false：接手另一個畫面的編輯狀態（Rebuilt，換深淺色、對比主題時重建畫面）</summary>
    MeetingEditView(AppHost host, MainWindow window, long meetingId, MeetingEditor editor, CancellationTokenSource peaks, bool load)
    {
        this.host = host;
        this.window = window;
        MeetingId = meetingId;
        Editor = editor;
        peaksCancel = peaks;
        preview = new EditPreview(Editor, host.Settings);
        Editor.StopPlayback = preview.Stop;
        timeline = new EditTimeline(Editor, Seek, RevealPlayhead);
        Current = this;

        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            Root.RowDefinitions.Add(new RowDefinition { Height = h });
        Add(Sheet.Header(L.T("編輯會議"), () => _ = Cancel(), subtitle), 0);
        timeline.Root.Height = 0;   // Refresh 時照有沒有合併給高度
        Add(timeline.Root, 1);

        // 播放列：試聽、播放頭的時間、說明、說話者、縮放
        play.Click += (_, _) => preview.Toggle();
        // 對比主題照平台的按鈕（有框）：灰色的圓在對比主題看不出是按鈕
        if (!Ui.HighContrast)
        {
            play.Resources["ButtonBackground"] = SidebarLook.Gray(0.1);
            play.Resources["ButtonBackgroundPointerOver"] = SidebarLook.Gray(0.16);
            play.Resources["ButtonBackgroundPressed"] = SidebarLook.Gray(0.22);
            play.Background = SidebarLook.Gray(0.1);
            play.BorderThickness = new Thickness(0);
        }
        zoomOut = Ui.GlyphButton("", L.T("縮小時間軸"), () => SetZoom(Editor.Zoom / 1.5));
        zoomIn = Ui.GlyphButton("", L.T("放大時間軸（也可以在時間軸上按住 Ctrl 滾動滑鼠滾輪）"), () => SetZoom(Editor.Zoom * 1.5));
        AutomationProperties.SetName(zoom, L.T("時間軸的縮放"));
        zoom.ValueChanged += (_, e) => { if (!updatingZoom) Ui.Guard(() => SetZoom(Math.Exp(e.NewValue))); };
        var hint = Ui.Text(L.T("試聽時跳過刪掉、裁掉的部分"), 13, color: Ui.Secondary, wrap: false);
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        hint.VerticalAlignment = VerticalAlignment.Center;
        time.VerticalAlignment = VerticalAlignment.Center;
        var transport = new Grid { ColumnSpacing = 12 };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var zoomRow = Ui.Row(2, zoomOut, zoom, zoomIn);
        zoomRow.VerticalAlignment = VerticalAlignment.Center;
        UIElement[] parts = [play, time, hint, legend, zoomRow];
        for (var i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)parts[i], i);
            transport.Children.Add(parts[i]);
        }
        Add(transport, 2);

        // 動作列（同 Mac 的 SheetPillButtonStyle compact）：設成開頭、設成結尾、刪掉選取的範圍、分成兩場、合併另一場
        setStart = Action(EdgeIcon(true), L.T("設成開頭"), L.T("把開頭的把手移到播放頭：前面的部分裁掉"), () => Editor.SetStart(Editor.Playhead));
        setEnd = Action(EdgeIcon(false), L.T("設成結尾"), L.T("把結尾的把手移到播放頭：後面的部分裁掉"), () => Editor.SetEnd(Editor.Playhead));
        deleteSelection = Action(Glyph(""), L.T("刪掉選取的範圍"), L.T("在波形上拖曳選一段，再按這裡（或 Delete）刪掉"), Editor.DeleteSelection);
        cutButton = Action(Glyph(""), L.T("在這裡分成兩場"), "", Editor.ToggleCut);
        mergeButton = Action(Glyph(""), L.T("合併另一場…"), "", () => _ = MergeOrUnmerge());
        var actions = new FlowPanel { Spacing = 8, LineSpacing = 8 };
        foreach (var b in new[] { setStart, setEnd, deleteSelection, cutButton, mergeButton }) actions.Children.Add(b);
        Add(actions, 3);

        // 逐字稿：標題、說明、分隔線
        var heading = Ui.Text(L.T("逐字稿"), 13, bold: true, color: Ui.Secondary, wrap: false);
        var help = Ui.Text(L.T("點一句從那裡播放；按住 Ctrl、Shift 選好幾句，按右鍵刪掉、設成開頭或結尾、從這句分成兩場"), 12, color: Ui.Secondary, wrap: false);
        help.TextTrimming = TextTrimming.CharacterEllipsis;
        help.HorizontalAlignment = HorizontalAlignment.Right;
        help.VerticalAlignment = VerticalAlignment.Bottom;
        var listHeader = Ui.Bar(heading, help, null, 16);
        listHeader.BorderBrush = Ui.Divider;
        listHeader.BorderThickness = new Thickness(0, 0, 0, 1);
        listHeader.Padding = new Thickness(0, 0, 0, 6);
        Add(listHeader, 4);
        list.ItemTemplate = RowTemplate.Value;
        AutomationProperties.SetName(list, L.T("逐字稿"));
        list.ContainerContentChanging += (_, e) => Ui.Guard(() =>
        {
            if (e.ItemContainer.ContentTemplateRoot is not ContentControl slot) return;
            var line = (MeetingEditor.Line)e.Item;
            if (e.InRecycleQueue)
            {
                slot.Content = null;
                if (rows.TryGetValue(line.Id, out var old) && old.Item == e.ItemContainer) rows.Remove(line.Id);
            }
            else Bind((ListViewItem)e.ItemContainer, slot, line);
            e.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            e.Handled = true;
        });
        list.SelectionChanged += (_, _) => Ui.Guard(() =>
        {
            if (updatingSelection) return;
            Editor.SetSelectedLines(list.SelectedItems.OfType<MeetingEditor.Line>().Select(l => l.Id));
            RefreshRows(force: true);
        });
        // 點一句從那裡播放（Ctrl、Shift 點選只是選取）
        list.ItemClick += (_, e) => Ui.Guard(() =>
        {
            if (Down(VirtualKey.Control) || Down(VirtualKey.Shift) || e.ClickedItem is not MeetingEditor.Line line) return;
            Seek(line.Start);
        });
        list.KeyDown += (_, e) => Ui.Guard(() =>
        {
            if (e.Key != VirtualKey.Delete) return;
            var targets = Editor.Lines.Where(l => Editor.SelectedLines.Contains(l.Id)).ToList();
            if (targets.Count == 0) return;
            if (targets.Any(l => Editor.Kept(l.Mid))) Editor.DeleteLines(targets); else Editor.RestoreLines(targets);
            e.Handled = true;
        });
        Add(list, 5);

        // 下面：摘要、取消、套用…
        apply = Sheet.Pill(L.T("套用…"), Sheet.Kind.Primary, click: () => _ = ConfirmApply());
        var footer = Ui.Bar(null, summary, Ui.Row(10, Sheet.Pill(L.T("取消"), click: () => _ = Cancel()), apply), 12);
        Add(footer, 6);

        // 鍵盤（R107.9）：空白鍵試聽、Enter 套用、Esc 取消（焦點在按鈕、輸入框上時照它們自己的）
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) => Ui.Guard(() =>
        {
            if (e.Key is not (VirtualKey.Space or VirtualKey.Enter or VirtualKey.Escape)) return;
            var focused = Root.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
            if (focused is TextBox || focused is ButtonBase && e.Key != VirtualKey.Escape) return;
            switch (e.Key)
            {
                case VirtualKey.Space: preview.Toggle(); break;
                case VirtualKey.Enter: if (apply.IsEnabled) _ = ConfirmApply(); break;
                default: _ = Cancel(); break;
            }
            e.Handled = true;
        })), handledEventsToo: false);

        Host.Content = Root;
        Host.SizeChanged += (_, _) => Ui.Guard(FitHeight);
        // 動作列換行、合併多一行標籤、字變大：別的列變高了，重新算一次（值變了才設，不會一直重排）
        Root.LayoutUpdated += (_, _) => Ui.Guard(FitHeight);

        Editor.Changed += Refresh;
        if (load) Editor.Load();
        list.ItemsSource = Editor.Lines;
        // 重建的畫面：逐字稿選了的句子照舊選著
        if (!load && Editor.SelectedLines.Count > 0)
        {
            updatingSelection = true;
            foreach (var line in Editor.Lines.Where(l => Editor.SelectedLines.Contains(l.Id))) list.SelectedItems.Add(line);
            updatingSelection = false;
        }
        LoadPeaks();
        Refresh();
    }

    void Add(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        Root.Children.Add(element);
    }

    /// <summary>Root 的高度：卡片的高度，或是逐字稿以外的列加上逐字稿至少 ListMinHeight（比卡片高時卡片可以捲）</summary>
    void FitHeight()
    {
        var viewport = Host.ViewportHeight;
        if (viewport <= 0 || closed) return;
        var others = Root.Children.OfType<FrameworkElement>().Where(e => !ReferenceEquals(e, list)).Sum(e => e.DesiredSize.Height);
        var height = Math.Max(viewport, others + Root.RowSpacing * (Root.RowDefinitions.Count - 1) + ListMinHeight);
        if (Math.Abs(height - (double.IsNaN(Root.Height) ? 0 : Root.Height)) > 0.5) Root.Height = height;
    }

    static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    static FontIcon Glyph(string glyph) => new() { Glyph = glyph, FontSize = 13 };

    /// <summary>「→|」「|←」：箭頭加一條直線（同 Mac 的 arrow.right.to.line、arrow.left.to.line）</summary>
    static FrameworkElement EdgeIcon(bool start)
    {
        var arrow = new FontIcon { Glyph = start ? "" : "", FontSize = 11 };
        // 直線用 PathIcon：跟著按鈕的字色（停用時變灰、對比主題、滑過），Rectangle 的話一直是 Ui.Primary
        var bar = new PathIcon { Data = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 1.5, 12) }, VerticalAlignment = VerticalAlignment.Center };
        return start ? Ui.Row(1, arrow, bar) : Ui.Row(1, bar, arrow);
    }

    Button Action(FrameworkElement icon, string text, string tooltip, Action click)
    {
        var label = Ui.Text(text, 13, wrap: false);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = Sheet.Pill(new Button { Content = Ui.Row(6, icon, label), Tag = label }, compact: true, click: click);
        AutomationProperties.SetName(button, text);
        if (tooltip.Length > 0) ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    static void Relabel(Button button, string text, string? tooltip)
    {
        ((TextBlock)button.Tag).Text = text;
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, tooltip);
    }

    void SetZoom(double value)
    {
        Editor.SetZoom(value);
        timeline.Update();
        timeline.CenterPlayhead();
    }

    /// <summary>移播放頭：試聽中的話停下來、從新的位置接著播（同 Mac 的 seek）</summary>
    void Seek(double t)
    {
        var playing = Editor.IsPlaying;
        preview.Stop();
        Editor.Seek(t);
        if (playing) preview.Play();
    }

    /// <summary>在時間軸上點了：逐字稿捲到播放頭那一句</summary>
    void RevealPlayhead()
    {
        if (CurrentLine() is { } line) list.ScrollIntoView(line, ScrollIntoViewAlignment.Leading);
    }

    MeetingEditor.Line? CurrentLine()
    {
        var lines = Editor.Lines;
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var m = (lo + hi) / 2;
            if (lines[m].Start <= Editor.Playhead) { found = m; lo = m + 1; } else hi = m - 1;
        }
        return found >= 0 ? lines[found] : null;
    }

    // MARK: 讀波形

    void LoadPeaks()
    {
        foreach (var part in Editor.PartsNeedingPeaks())
        {
            Editor.LoadingPeaks.Add(part.Id);
            var (id, path) = (part.Id, part.AudioPath!);
            _ = Task.Run(async () =>
            {
                float[]? values = null;
                try { values = await host.Controller.Peaks(path, MeetingEditor.PeakStep, peaksCancel.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) when (e is IOException or InvalidDataException or System.Runtime.InteropServices.COMException) { Log.Error($"edit peaks failed: {e.GetType().Name}"); }
                // 看 peaksCancel 不看 closed：換主題重建畫面時（Rebuilt）新的畫面接手同一個 peaksCancel
                host.Post(() => { if (!peaksCancel.IsCancellationRequested) Editor.SetPeaks(id, values); });
            });
        }
    }

    // MARK: 重畫

    void Refresh()
    {
        if (closed) return;
        var merged = Editor.MergedPart is not null;
        timeline.Root.Height = (merged ? 38 : 18) + 4 + EditTimeline.TrackHeight + 4 + 16 + 16 + 2;
        timeline.Update();
        subtitle.Text = Editor.Subtitle;
        play.Content = new FontIcon { Glyph = Editor.IsPlaying ? "" : "", FontSize = 12 };
        AutomationProperties.SetName(play, Editor.IsPlaying ? L.T("暫停") : L.T("試聽"));
        ToolTipService.SetToolTip(play, Editor.IsPlaying ? L.T("暫停（空白鍵）") : L.T("試聽（空白鍵）：跳過刪掉、裁掉的部分"));
        time.Text = TimeFormat.String(Editor.Playhead);
        updatingZoom = true;
        zoom.Value = Math.Log(Editor.Zoom);
        updatingZoom = false;
        zoomOut.IsEnabled = Editor.Zoom > 1;
        zoomIn.IsEnabled = Editor.Zoom < MeetingEditor.MaxZoom;
        deleteSelection.IsEnabled = Editor.Selection is not null;
        Relabel(cutButton, Editor.Cut is null ? L.T("在這裡分成兩場") : L.T("拿掉切點"),
            merged ? L.T("合併時不能同時分成兩場") : L.T("在播放頭的位置切開，後半變成新的一場"));
        cutButton.IsEnabled = Editor.Cut is not null || Editor.CanCut;
        Relabel(mergeButton, merged ? L.T("取消合併") : L.T("合併另一場…"),
            Editor.Cut is not null ? L.T("有切點時不能同時合併") : L.T("把另一場會議接到這場，照開始的時間排先後"));
        ((FontIcon)((StackPanel)mergeButton.Content).Children[0]).Glyph = merged ? "" : "";
        mergeButton.IsEnabled = Editor.Cut is null;
        RefreshLegend();
        RefreshFooter();
        RefreshRows();
    }

    void RefreshLegend()
    {
        var speakers = Editor.Legend();
        var key = string.Join(",", speakers) + Ui.ThemeKey;
        if (key == legendKey) return;
        legendKey = key;
        legend.Children.Clear();
        foreach (var (name, idx) in speakers.Take(4))
            legend.Children.Add(Ui.Row(5, new Ellipse { Width = 8, Height = 8, Fill = Ui.Speaker(idx), VerticalAlignment = VerticalAlignment.Center }, Ui.Text(name, 13, wrap: false)));
        if (speakers.Count > 4) legend.Children.Add(Ui.Text($"+{speakers.Count - 4}", 13, color: Ui.Secondary, wrap: false));
    }

    void RefreshFooter()
    {
        var (main, detail) = Editor.Summary;
        var key = main + detail + Editor.HasChanges + Ui.ThemeKey;
        if (key == footerKey) return;
        footerKey = key;
        summary.Inlines.Clear();
        summary.Inlines.Add(new Run { Text = main, FontWeight = FontWeights.Bold });
        summary.Inlines.Add(new Run { Text = detail, Foreground = Ui.Secondary });
        AutomationProperties.SetName(summary, main + detail);
        apply.IsEnabled = Editor.HasChanges && Editor.Plan.Duration >= 1;
    }

    // MARK: 逐字稿

    void Bind(ListViewItem item, ContentControl slot, MeetingEditor.Line line)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        foreach (var w in new[] { new GridLength(56), new GridLength(100), new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var stamp = Ui.Text(TimeFormat.String(line.Start), 13, color: Ui.Celadon, wrap: false);
        stamp.HorizontalAlignment = HorizontalAlignment.Right;
        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        // 對比主題：說話者的顏色都是字的顏色，點不畫（選到的列是反白的底，字的顏色的點看不清楚）
        if (line.Speaker is int idx && !Ui.HighContrast) who.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Ui.Speaker(idx), VerticalAlignment = VerticalAlignment.Center });
        var name = Ui.Text(line.SpeakerName, 13, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        who.Children.Add(name);
        var text = Ui.Text(line.Text, Ui.C(14), maxLines: 2);
        var action = Sheet.Pill(new Button { Content = "" }, compact: true);
        action.Visibility = Visibility.Collapsed;
        action.Click += (_, _) => PrimaryAction(line);
        Grid.SetColumn(who, 1);
        Grid.SetColumn(text, 2);
        Grid.SetColumn(action, 3);
        grid.Children.Add(stamp);
        grid.Children.Add(who);
        grid.Children.Add(text);
        grid.Children.Add(action);
        var row = new Row(item, grid, stamp, text, action);
        grid.PointerEntered += (_, _) =>
        {
            hovered = line.Id;
            action.Visibility = Visibility.Visible;
        };
        grid.PointerExited += (_, _) =>
        {
            if (hovered == line.Id) hovered = null;
            if (!Editor.SelectedLines.Contains(line.Id)) action.Visibility = Visibility.Collapsed;
        };
        // 對比主題：列裡的字跟著列的前景色（選到時是反白的字；自己給的青瓷綠、字的顏色在反白的底上看不見）
        slot.Content = Ui.FollowForeground(grid);
        rows[line.Id] = row;
        // 右鍵：從這裡播放、刪掉／恢復這幾句、設成開頭結尾、從這句分成兩場（打開時才建：對象是選取的那幾句）
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => Ui.Guard(() => FillMenu(menu, line));
        item.ContextFlyout = menu;
        UpdateRow(row, line);
    }

    string ActionTitle(MeetingEditor.Line line) =>
        Editor.Kept(line.Mid) ? L.T("刪掉") : line.Mid < Editor.Start ? L.T("從這句開始") : line.Mid > Editor.End ? L.T("到這句結束") : L.T("恢復");

    void PrimaryAction(MeetingEditor.Line line)
    {
        if (Editor.Kept(line.Mid)) Editor.DeleteLines(Editor.TargetLines(line));
        else if (line.Mid < Editor.Start) Editor.SetStart(line.Start);
        else if (line.Mid > Editor.End) Editor.SetEnd(line.End);
        else Editor.RestoreLines(Editor.TargetLines(line));
    }

    void FillMenu(MenuFlyout menu, MeetingEditor.Line line)
    {
        menu.Items.Clear();
        var targets = Editor.TargetLines(line);
        var anyKept = targets.Any(l => Editor.Kept(l.Mid));
        var anyRemoved = targets.Any(l => !Editor.Kept(l.Mid) && l.Mid >= Editor.Start && l.Mid <= Editor.End);
        menu.Items.Add(Ui.Item(L.T("從這裡播放"), () => { Seek(line.Start); if (!Editor.IsPlaying) preview.Play(); }, icon: Symbol.Play));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (anyKept)
            menu.Items.Add(Ui.Item(targets.Count > 1 ? L.F("刪掉這 %lld 句", targets.Count) : L.T("刪掉這句"), () => Editor.DeleteLines(targets), icon: Symbol.Delete));
        if (anyRemoved)
            menu.Items.Add(Ui.Item(targets.Count > 1 ? L.F("恢復這 %lld 句", targets.Count) : L.T("恢復這句"), () => Editor.RestoreLines(targets), icon: Symbol.Undo));
        var start = Ui.Item(L.T("從這句開始（設成開頭）"), () => Editor.SetStart(targets.Min(l => l.Start)));
        start.Icon = new FontIcon { Glyph = "" };
        menu.Items.Add(start);
        var end = Ui.Item(L.T("到這句結束（設成結尾）"), () => Editor.SetEnd(targets.Max(l => l.End)));
        end.Icon = new FontIcon { Glyph = "" };
        menu.Items.Add(end);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item(L.T("從這句分成兩場"), () => Editor.CutAt(line.Start),
            enabled: Editor.MergedPart is null && Editor.Kept(line.Mid) && line.Start > Editor.Start + 1, icon: Symbol.Cut));
    }

    /// <summary>刪掉、裁掉的句子變灰、刪除線；滑過或選到的那句右邊有按鈕；播放到的那句淡淡的底</summary>
    void UpdateRow(Row row, MeetingEditor.Line line)
    {
        var kept = Editor.Kept(line.Mid);
        row.Text.TextDecorations = kept ? Windows.UI.Text.TextDecorations.None : Windows.UI.Text.TextDecorations.Strikethrough;
        // 對比主題靠刪除線分（字的顏色跟著列，FollowForeground）
        if (!Ui.HighContrast) row.Text.Foreground = kept ? Ui.Primary : Ui.Secondary;
        var title = ActionTitle(line);
        row.Action.Content = title;
        AutomationProperties.SetName(row.Action, title);
        var selected = Editor.SelectedLines.Contains(line.Id);
        row.Action.Visibility = selected || hovered == line.Id ? Visibility.Visible : Visibility.Collapsed;
        var playing = line.Id == currentLine && !selected;
        if (Ui.HighContrast)
        {
            // 對比主題：播放到的那句一圈反白顏色的框（同逐字稿分頁，§11.3）
            row.Grid.BorderBrush = playing ? Ui.SystemBrush(Windows.UI.ViewManagement.UIElementType.Highlight) : null;
            row.Grid.BorderThickness = new Thickness(playing ? 1 : 0);
        }
        else row.Grid.Background = playing ? SidebarLook.Gray(0.07) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(row.Item, $"{TimeFormat.String(line.Start)} {line.SpeakerName} {line.Text}" + (kept ? "" : L.T("（刪掉）")));
    }

    void RefreshRows(bool force = false)
    {
        if (!ReferenceEquals(list.ItemsSource, Editor.Lines))
        {
            rows.Clear();
            list.ItemsSource = Editor.Lines;
        }
        // 刪掉句子後編輯狀態清掉了選取（同 Mac）：清單的選取也拿掉（按鈕、右鍵、Delete 都一樣），不然清單看起來還選著、再按 Delete 沒反應
        if (Editor.SelectedLines.Count == 0 && list.SelectedItems.Count > 0)
        {
            updatingSelection = true;
            list.SelectedItems.Clear();
            updatingSelection = false;
            force = true;
        }
        var current = CurrentLine()?.Id;
        var key = string.Join(",", Editor.Deleted) + $"|{Editor.Start}|{Editor.End}|{current}|{Ui.ThemeKey}";
        if (!force && key == rowsKey) return;
        var moved = current != currentLine;
        currentLine = current;
        rowsKey = key;
        foreach (var line in Editor.Lines)
            if (rows.TryGetValue(line.Id, out var row)) UpdateRow(row, line);
        // 試聽中逐字稿跟著播放的位置捲
        if (moved && Editor.IsPlaying && CurrentLine() is { } playing) list.ScrollIntoView(playing);
    }

    // MARK: 合併、取消、套用

    async Task MergeOrUnmerge()
    {
        if (Editor.MergedPart is not null)
        {
            Editor.Unmerge();
            return;
        }
        if (await Dialogs.MergePicker(window, MeetingId) is not { } other || closed) return;
        Editor.Merge(other);
        LoadPeaks();
    }

    /// <summary>有改動時先問（放棄這些改動？）；沒有就直接關</summary>
    public async Task Cancel()
    {
        if (closed) return;
        if (Editor.HasChanges && !await Dialogs.Confirm(window, L.T("放棄這些改動？"), L.T("編輯畫面上的裁切、刪掉的段落、切點和合併都不會套用。"), L.T("放棄"), destructive: true))
            return;
        Close();
    }

    /// <summary>套用前說清楚會怎樣（M§81.5）；確認後關掉編輯畫面、在背景套用（會議頁上方顯示「正在套用編輯…」）</summary>
    public async Task ConfirmApply()
    {
        if (closed || !Editor.HasChanges || Editor.Plan.Duration < 1) return;
        preview.Stop();
        var plan = Editor.Plan;
        var cut = Editor.PlanCut;
        if (!await Dialogs.Confirm(window, L.T("套用這些改動？"), Editor.ApplyMessage(host.Settings.SummarizerReady), L.T("套用")) || closed) return;
        Close();
        await host.Controller.ApplyMeetingEdit(MeetingId, plan, cut);
    }

    public void Close()
    {
        if (closed) return;
        Dispose();
        Closed?.Invoke();
    }

    public void Dispose() => Release(cancelPeaks: true);

    void Release(bool cancelPeaks)
    {
        if (closed) return;
        closed = true;
        if (cancelPeaks) peaksCancel.Cancel();
        preview.Dispose();
        Editor.Changed -= Refresh;
        if (Current == this) Current = null;
    }

    /// <summary>
    /// 換了深淺色、對比主題：用同一份編輯狀態（把手、刪掉的段落、選取、切點、合併、縮放、讀好的波形）重建畫面，這個畫面收掉。
    /// 畫面的顏色是建的時候決定的（平台的按鈕、Sheet.Pill 在對比主題另有一套），只重畫時間軸的話說明文字、按鈕會留在舊的顏色
    /// </summary>
    public MeetingEditView Rebuilt()
    {
        preview.Stop();
        var view = new MeetingEditView(host, window, MeetingId, Editor, peaksCancel, load: false);
        Release(cancelPeaks: false);
        return view;
    }

    /// <summary>打開後焦點給時間軸（空白鍵、← → 馬上能用）</summary>
    public void FocusTimeline() => timeline.Surface.Focus(FocusState.Programmatic);

    /// <summary>DEBUG 的 state：試聽的時間（新時間軸上）、是不是模擬的時鐘</summary>
    public (double Time, bool Simulated) PreviewState => preview.State;

    // DEBUG 的 editorstate（同 Mac：背景的視窗收不到點擊，截圖、驗畫面用）
    public void SetZoomForDebug(double value) => SetZoom(value);
    public void TogglePlayForDebug() => preview.Toggle();

    public void MergeForDebug(Meeting other)
    {
        Editor.Merge(other);
        LoadPeaks();
    }

    public void SelectLinesForDebug(IEnumerable<long> ids)
    {
        var set = ids.ToHashSet();
        list.SelectedItems.Clear();
        foreach (var line in Editor.Lines.Where(l => set.Contains(l.Id))) list.SelectedItems.Add(line);
    }
}

/// <summary>
/// 試聽（R107.3、R107.4）：照片段接好的錄音（Platform 的 EditPreviewSource）交給 MediaPlayer，播放頭照 EditPlan.Source 換回這條時間軸。
/// DEBUG 的 --set debugMutePreview=YES：不出聲、不用 MediaPlayer，照真的時間走（虛擬機沒有音效裝置，MediaPlayer 不會前進；同 Mac 的 -debugMutePreview）
/// </summary>
sealed class EditPreview : IDisposable
{
    readonly MeetingEditor editor;
    readonly AppSettings settings;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    MediaPlayer? player;
    Platform.EditPreviewSource? source;
    EditPlan? plan;
    Stopwatch? clock;
    double clockStart;

    public EditPreview(MeetingEditor editor, AppSettings settings)
    {
        this.editor = editor;
        this.settings = settings;
        timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += (_, _) => Ui.Guard(Tick);
    }

    bool Simulated =>
#if DEBUG
        settings.Flag("debugMutePreview", false);
#else
        false;
#endif

    public (double Time, bool Simulated) State => (Position(), clock is not null);

    double Position()
    {
        if (clock is not null) return clockStart + clock.Elapsed.TotalSeconds;
        try { return player?.PlaybackSession.Position.TotalSeconds ?? 0; }
        catch (System.Runtime.InteropServices.COMException) { return 0; }
    }

    public void Toggle()
    {
        if (editor.IsPlaying) Stop(); else Play();
    }

    public void Play()
    {
        Stop();
        var p = editor.Plan;
        if (p.Duration <= 0) return;
        plan = p;
        var start = editor.PreviewStart(p);
        if (Simulated)
        {
            clockStart = start;
            clock = Stopwatch.StartNew();
        }
        else
        {
            try
            {
                var sources = editor.Parts.Where(x => x.AudioPath is not null).ToDictionary(x => x.Id, x => x.AudioPath!);
                source = new Platform.EditPreviewSource(p, sources);
                player = new MediaPlayer { AutoPlay = false };
                var opened = player;
                player.MediaOpened += (_, _) =>
                {
                    try
                    {
                        opened.PlaybackSession.Position = TimeSpan.FromSeconds(start);
                        opened.Play();
                    }
                    catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { Log.Error($"preview: {e.GetType().Name}"); }
                };
                player.Source = MediaSource.CreateFromMediaStreamSource(source.Source);
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or IOException)
            {
                // 沒有音訊輸出裝置時 MediaPlayer 會丟 0x800F1000（同 Player）：播不出聲音就算了
                Log.Error($"preview: {e.GetType().Name} 0x{e.HResult:X8}");
                Stop();
                return;
            }
        }
        editor.IsPlaying = true;
        if (editor.Timeline(start, p) is double t) editor.Follow(t);
        timer.Start();
    }

    void Tick()
    {
        if (!editor.IsPlaying || plan is null) return;
        var seconds = Position();
        if (seconds >= plan.Duration - 0.05)
        {
            Stop();
            return;
        }
        if (editor.Timeline(seconds, plan) is double t) editor.Follow(t);
    }

    public void Stop()
    {
        timer.Stop();
        clock = null;
        if (player is not null)
        {
            try
            {
                player.Pause();
                player.Source = null;
            }
            catch (System.Runtime.InteropServices.COMException) { }
            player.Dispose();
            player = null;
        }
        source?.Dispose();
        source = null;
        plan = null;
        if (!editor.IsPlaying) return;
        editor.IsPlaying = false;
        editor.Follow(editor.Playhead);
    }

    public void Dispose() => Stop();
}
