using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shiyin.Localization;
using Windows.UI;

namespace Shiyin.App;

/// <summary>
/// 彈窗、輸入框的共用樣子（R106、M§80、design-windows §11.39；同 Mac 的 Views/SheetStyle.swift）：
/// 大標題、灰字說明、右上角 ✕；欄位上面灰色小標籤；細框圓角的輸入框（有焦點時框變亮，不用強調色的焦點線）；右下圓角按鈕
///（主要的字色當底、次要的淡底細框、刪除類紅底白字）。§11.35 的重新命名對話框抽出來的。對比主題時照平台（§11.3）
/// </summary>
public static class Sheet
{
    /// <summary>內容的寬：Mac 的 sheetChrome 440、480 減掉 ContentDialog 自己兩邊的內距 24</summary>
    public const double Width = 392;
    public const double WideWidth = 432;

    public enum Kind { Primary, Secondary, Destructive }

    // MARK: 標題

    /// <summary>標題下面的灰字說明（14 號、會換行）；要即時換字的（重新命名的合併提醒）自己留著這個 TextBlock</summary>
    public static TextBlock Subtitle(string text) => Ui.Text(text, 14, color: Ui.Secondary);

    /// <summary>
    /// 大標題（22 號粗體）、說明、標題列右邊的小字（「第 1 組，共 2 組」）、右上角 ✕（等於取消或關掉）。
    /// ✕ 不能 Tab（同 Mac 的 ✕ 沒有焦點；ContentDialog 打開時會把焦點給第一個能 Tab 的元件），鍵盤用 Esc
    /// </summary>
    public static Grid Header(string title, Action close, TextBlock? subtitle = null, TextBlock? trailing = null)
    {
        var heading = Ui.DialogTitle(title);
        heading.FontSize = 22;
        var x = SidebarLook.HeaderIcon(new Button(), "");
        x.Margin = new Thickness(0, -2, -6, 0);
        x.VerticalAlignment = VerticalAlignment.Top;
        x.IsTabStop = false;
        ToolTipService.SetToolTip(x, L.T("關閉"));
        AutomationProperties.SetName(x, L.T("關閉"));
        x.Click += (_, _) => close();
        FrameworkElement right = x;
        if (trailing is not null)
        {
            trailing.Margin = new Thickness(0, 3, 0, 0);
            trailing.VerticalAlignment = VerticalAlignment.Top;
            right = Ui.Row(10, trailing, x);
        }
        // 兩欄（不用 Ui.Bar：左邊空的那一欄照樣多一段間距，標題會比欄位往右縮 12）
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        g.Children.Add(Ui.Column(6, heading, subtitle));
        g.Children.Add(right);
        return g;
    }

    // MARK: 欄位

    /// <summary>欄位上面的灰色小標籤（13 號、次要文字，同 Mac 的 SheetFieldLabel）</summary>
    public static TextBlock Label(string text) => Ui.Text(text, 13, color: Ui.Secondary);

    /// <summary>小標籤＋欄位（上下間距 6）。欄位沒有無障礙名稱時用標籤的字</summary>
    public static StackPanel Labeled(string label, FrameworkElement field)
    {
        if (string.IsNullOrEmpty(AutomationProperties.GetName(field))) AutomationProperties.SetName(field, label);
        return Ui.Column(6, Label(label), field);
    }

    /// <summary>兩個欄位排成一列，各占一半（同 Mac 的 HStack 兩欄）</summary>
    public static Grid Pair(FrameworkElement left, FrameworkElement right)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    /// <summary>字色 opacity 的灰（淺色黑、深色白）</summary>
    static SolidColorBrush Ink(double opacity) => SidebarLook.Gray(opacity);

    static readonly SolidColorBrush Clear = new(Microsoft.UI.Colors.Transparent);

    /// <summary>
    /// 輸入框（同 Mac 的 sheetFieldBox）：字色 4% 的底、25% 的細框，滑過 40%、有焦點 75% 1.5（原本是底下一條 2 的強調色），
    /// 圓角 10、字 15、內距 12×8。compact（就地編輯）圓角 8、內距 9×5，字照原本（會議內容跟著文字大小）。多行的也一樣
    /// </summary>
    public static T Field<T>(T box, bool compact = false) where T : TextBox
    {
        box.CornerRadius = new CornerRadius(compact ? 8 : 10);
        box.Padding = compact ? new Thickness(9, 5, 9, 5) : new Thickness(12, 8, 12, 8);
        if (!compact) box.FontSize = 15;
        if (Ui.HighContrast) return box;
        var r = box.Resources;
        var fill = Ink(0.04);
        r["TextControlBackground"] = fill;
        r["TextControlBackgroundPointerOver"] = fill;
        r["TextControlBackgroundFocused"] = fill;
        r["TextControlBorderBrush"] = Ink(0.25);
        r["TextControlBorderBrushPointerOver"] = Ink(0.4);
        r["TextControlBorderBrushFocused"] = Ink(0.75);
        r["TextControlBorderThemeThickness"] = new Thickness(1);
        r["TextControlBorderThemeThicknessFocused"] = new Thickness(1.5);
        return box;
    }

    /// <summary>新的輸入框（彈窗用）：placeholder 也是無障礙名稱</summary>
    public static TextBox TextField(string text, string placeholder)
    {
        var box = Field(new TextBox { Text = text, PlaceholderText = placeholder });
        AutomationProperties.SetName(box, placeholder);
        return box;
    }

    /// <summary>
    /// 選單式的欄位（同 Mac 的 SheetMenuField）：ComboBox 用同一種框，撐滿欄寬；下拉的清單照平台。
    /// 有鍵盤焦點時左邊那一小條（Pill）原本是強調色，換成字色
    /// </summary>
    public static ComboBox Combo(ComboBox box)
    {
        box.Header = null;
        box.CornerRadius = new CornerRadius(10);
        box.FontSize = 15;
        box.MinHeight = 38;
        box.MinWidth = 0;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (Ui.HighContrast) return box;
        var r = box.Resources;
        var fill = Ink(0.04);
        r["ComboBoxBackground"] = fill;
        r["ComboBoxBackgroundPointerOver"] = Ink(0.06);
        r["ComboBoxBackgroundPressed"] = Ink(0.08);
        r["ComboBoxBackgroundFocused"] = fill;
        r["ComboBoxBorderBrush"] = Ink(0.25);
        r["ComboBoxBorderBrushPointerOver"] = Ink(0.4);
        r["ComboBoxBorderBrushPressed"] = Ink(0.75);
        r["ComboBoxBorderThemeThickness"] = new Thickness(1);
        r["ComboBoxItemPillFillBrush"] = Ink(0.75);
        box.Background = fill;
        box.BorderBrush = Ink(0.25);
        box.BorderThickness = new Thickness(1);
        return box;
    }

    /// <summary>日期（同 Mac 的 SheetDateField）：CalendarDatePicker 用同一種框，右邊的日曆圖示照平台（點了打開月曆）</summary>
    public static CalendarDatePicker Date(CalendarDatePicker picker)
    {
        picker.Header = null;
        picker.CornerRadius = new CornerRadius(10);
        picker.FontSize = 15;
        picker.MinHeight = 38;
        picker.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (Ui.HighContrast) return picker;
        var r = picker.Resources;
        var fill = Ink(0.04);
        r["CalendarDatePickerBackground"] = fill;
        r["CalendarDatePickerBackgroundPointerOver"] = Ink(0.06);
        r["CalendarDatePickerBackgroundPressed"] = Ink(0.08);
        r["CalendarDatePickerBorderBrush"] = Ink(0.25);
        r["CalendarDatePickerBorderBrushPointerOver"] = Ink(0.4);
        r["CalendarDatePickerBorderBrushPressed"] = Ink(0.75);
        r["CalendarDatePickerBorderThemeThickness"] = new Thickness(1);
        picker.Background = fill;
        picker.BorderBrush = Ink(0.25);
        picker.BorderThickness = new Thickness(1);
        return picker;
    }

    /// <summary>
    /// 一塊同樣框的底（勾選清單、唯讀的文字：Mac 的 .sheetFieldBox() 包住 ScrollView）。Border 的框線佔內容的寬度，內距照輸入框
    /// </summary>
    public static Border Box(UIElement child, bool focused = false)
    {
        var border = new Border { Child = child, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8) };
        if (Ui.HighContrast)
        {
            border.BorderBrush = Ui.SystemBrush(Windows.UI.ViewManagement.UIElementType.WindowText);
            border.BorderThickness = new Thickness(1);
            return border;
        }
        border.Background = Ink(0.04);
        border.BorderBrush = Ink(focused ? 0.75 : 0.25);
        border.BorderThickness = new Thickness(focused ? 1.5 : 1);
        return border;
    }

    // MARK: 按鈕

    /// <summary>刪除類的紅（Windows 的 critical 紅 #C42B1C）：深淺色同一個，白字的對比 5.9:1（Ui.Danger 深色模式是淡紅、配白字看不清）</summary>
    static SolidColorBrush Red(double opacity) => new(Color.FromArgb((byte)Math.Round(255 * opacity), 0xC4, 0x2B, 0x1C));

    /// <summary>
    /// 彈窗的圓角按鈕（同 Mac 的 SheetPillButtonStyle）：主要按鈕是字的顏色當底、視窗底色的字（淺色黑底白字、深色白底黑字），
    /// 刪除類紅底白字，滑過 85%、按下 75%、停用 35%；次要按鈕字色 6% 的底加 18% 的細框。圓角 10、高 34、最窄 68、左右 18。
    /// compact（就地編輯）：圓角 8、高 26、最窄 52、左右 12、字 13。對比主題時照平台（主要、刪除類是強調色的按鈕）
    /// </summary>
    public static Button Pill(string text, Kind kind = Kind.Secondary, bool compact = false, Action? click = null) =>
        Pill(new Button { Content = text }, kind, compact, click);

    public static T Pill<T>(T button, Kind kind = Kind.Secondary, bool compact = false, Action? click = null) where T : Button
    {
        button.MinWidth = compact ? 52 : 68;
        button.MinHeight = compact ? 26 : 34;
        button.Padding = compact ? new Thickness(12, 2, 12, 3) : new Thickness(18, 0, 18, 0);
        button.CornerRadius = new CornerRadius(compact ? 8 : 10);
        button.VerticalAlignment = VerticalAlignment.Center;
        if (compact) button.FontSize = 13;
        if (click is not null) button.Click += (_, _) => click();
        if (Ui.HighContrast)
        {
            if (kind != Kind.Secondary && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accent) button.Style = accent;
            return button;
        }
        var r = button.Resources;
        if (kind == Kind.Secondary)
        {
            r["ButtonBackground"] = Ink(0.06);
            r["ButtonBackgroundPointerOver"] = Ink(0.10);
            r["ButtonBackgroundPressed"] = Ink(0.14);
            r["ButtonBackgroundDisabled"] = Ink(0.03);
            foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" }) r[key] = Ui.Primary;
            foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" }) r[key] = Ink(0.18);
            r["ButtonBorderBrushDisabled"] = Ink(0.09);
            button.Background = Ink(0.06);
            button.Foreground = Ui.Primary;
            button.BorderBrush = Ink(0.18);
            return button;
        }
        Func<double, SolidColorBrush> fill = kind == Kind.Destructive ? Red : Ink;
        var ink = kind == Kind.Destructive ? new SolidColorBrush(Microsoft.UI.Colors.White) : new SolidColorBrush(Ui.IsDark ? Ui.HexColor("#202020") : Microsoft.UI.Colors.White);
        r["ButtonBackground"] = fill(1);
        r["ButtonBackgroundPointerOver"] = fill(0.85);
        r["ButtonBackgroundPressed"] = fill(0.75);
        r["ButtonBackgroundDisabled"] = fill(0.35);
        foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ButtonForegroundDisabled" }) r[key] = ink;
        foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" }) r[key] = Clear;
        button.Background = fill(1);
        button.Foreground = ink;
        button.BorderBrush = Clear;
        return button;
    }

    /// <summary>
    /// 次要按鈕但字是紅的（資料夾條件的「移除條件」，同 Mac 的 .foregroundStyle(.red)）。對比主題時照平台
    /// </summary>
    public static Button DangerText(Button button)
    {
        if (Ui.HighContrast) return button;
        foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" }) button.Resources[key] = Ui.Danger;
        button.Foreground = Ui.Danger;
        return button;
    }

    /// <summary>按鈕列：left（「分享…」「不是定期會議」「移除條件」）靠左，其他靠右，間距 10</summary>
    public static Grid Buttons(FrameworkElement? left, params Button[] right)
    {
        var row = Ui.Row(10, right);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        return Ui.Bar(left, null, row, 10);
    }
}

/// <summary>
/// 照 Sheet 樣子的對話框（R106、design-windows §11.39）：ContentDialog 內建的標題、按鈕都不用（內建的兩顆按鈕撐滿一整排、主要按鈕在左邊、
/// 底下一條灰色的按鈕區，樣式只能整組換；§11.35），標題、按鈕放在內容裡，按鈕區（CommandSpace）在 Opened 時收起來。
/// Enter：焦點不在按鈕、選單式的欄位、多行的輸入框上時做 Submit（主要按鈕停用時不做）；Esc、✕：Escape（沒給是關掉）。
/// 打開後焦點給 Focus（輸入框全選；ContentDialog 打開時會先給第一個能 Tab 的元件）
/// </summary>
public sealed class SheetDialog
{
    public ContentDialog Dialog { get; }
    public StackPanel Form { get; }
    /// <summary>Enter 做什麼（null：不理）；Primary 停用時不做</summary>
    public Action? Submit { get; set; }
    public Button? Primary { get; set; }
    /// <summary>Esc、✕ 做什麼（null：關掉）。可能的定期會議的「略過」</summary>
    public Action? Escape { get; set; }
    /// <summary>打開後給焦點的元件（輸入框會全選，SelectOnFocus 是 false 時不選）；沒給就不動</summary>
    public Control? Focus { get; set; }
    public bool SelectOnFocus { get; set; } = true;
    /// <summary>DEBUG 的 state（sheet）：開著的對話框的標題</summary>
    public string Title { get; }
    bool closing;

    public SheetDialog(MainWindow window, string title, double width = Sheet.Width, double spacing = 18)
    {
        Title = title;
        Dialog = new ContentDialog { XamlRoot = window.XamlRoot };
        // 深淺色跟著主視窗：開著拾音時換了 Windows 的深淺色，ContentDialog 照樣是開 app 時的顏色（2026-11-17 虛擬機：換成深色後對話框是淺色的底、
        // 字卻是 Ui 照深色給的白字，整個看不清楚）。Ui 的顏色照主視窗的 ActualTheme（Ui.IsDark），對話框也照它
        if (!Ui.HighContrast) Dialog.RequestedTheme = Ui.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        // 平台的最小高度 184：內容短的確認（一行說明）底下會多一塊空白（2026-11-17 虛擬機：移到垃圾桶的確認）
        Dialog.Resources["ContentDialogMinHeight"] = 0d;
        Form = new StackPanel { Spacing = spacing, Width = width };
        Dialog.Content = Form;
        Form.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape || e.Handled) return;
            e.Handled = true;
            RunEscape();
        }), handledEventsToo: true);
        // Enter 在往下傳的時候（PreviewKeyDown）就接住：ContentDialog 自己也收 Enter（沒有內建按鈕時會關掉、結果是 None），
        // 剛打開、視窗剛拿回前景、焦點還沒進到內容裡時按 Enter 就被它關掉了（2026-11-17 虛擬機：刪除資料夾的確認按 Enter 沒刪、對話框關掉）
        Dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || Submit is not { } submit) return;
            if (SkipsEnter(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            if (Primary is null || Primary.IsEnabled) submit();
        };
        // ContentDialog 自己也收 Esc（焦點不在內容裡時）：不是自己關的就照 Escape 做（「略過」這種不是關掉的）
        Dialog.Closing += (_, e) =>
        {
            if (!closing && Escape is not null)
            {
                e.Cancel = true;
                Escape();
                return;
            }
            closing = true;
        };
        // 焦點留在對話框裡：選單裡的項目打開對話框時，選單晚一點關掉、把焦點還給對話框後面的「⋯」或那一列（2026-11-17 虛擬機：
        // 之後的 Enter、Esc 都送到側欄，對話框關不掉）。焦點要跑到主視窗裡的元件時，留在原地（原本就在對話框裡）或給 Focus／Primary。
        // 下拉選單、月曆這些在對話框自己的 popup 裡，不在主視窗底下，不管
        var main = window.Content as UIElement;
        EventHandler<GettingFocusEventArgs> keepInside = (_, e) => Ui.Guard(() =>
        {
            if (closing || main is null || e.NewFocusedElement is not DependencyObject next) return;
            if (Within(next, Form) || !Within(next, main)) return;
            if (e.OldFocusedElement is DependencyObject old && Within(old, Form)) e.TryCancel();
            else if ((Focus ?? Primary) is { } fallback) e.TrySetNewFocusedElement(fallback);
        });
        Dialog.Opened += (_, _) => FocusManager.GettingFocus += keepInside;
        Dialog.Closed += (_, _) => FocusManager.GettingFocus -= keepInside;
        Dialog.Opened += (_, _) => Ui.Guard(() =>
        {
            if (Descendant(Dialog, "CommandSpace") is FrameworkElement commands) commands.Visibility = Visibility.Collapsed;
            if (Focus is not { } target) return;
            var keyed = false;
            Dialog.PreviewKeyDown += (_, _) => keyed = true;
            void Give()
            {
                if (keyed || closing) return;
                target.Focus(FocusState.Programmatic);
                if (target is TextBox box && SelectOnFocus) box.SelectAll();
            }
            target.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(Give));
            // 打開的動畫之後再確認一次（使用者還沒按鍵時）
            var timer = target.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(350);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => Ui.Guard(() =>
            {
                if (Dialog.XamlRoot is { } root && FocusManager.GetFocusedElement(root) != target) Give();
            });
            timer.Start();
        });
    }

    void RunEscape()
    {
        if (Escape is { } escape) escape();
        else Close();
    }

    /// <summary>✕ 用：同 Esc</summary>
    public void Cancel() => RunEscape();

    /// <summary>自己關掉（存好、取消）</summary>
    public void Close()
    {
        closing = true;
        Dialog.Hide();
    }

    /// <summary>這些元件自己處理 Enter（按鈕按下去、選單式的欄位打開或選一項、多行的輸入框換行）</summary>
    bool SkipsEnter(DependencyObject? source)
    {
        for (var e = source; e is not null && e != Form; e = VisualTreeHelper.GetParent(e))
            if (e is ButtonBase and not ToggleButton or ComboBox or ComboBoxItem or CalendarDatePicker or CalendarView or CalendarViewDayItem
                or TextBox { AcceptsReturn: true }) return true;
        return false;
    }

    static bool Within(DependencyObject element, DependencyObject ancestor)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (e == ancestor) return true;
        return false;
    }

    /// <summary>範本裡有名字的元件（ContentDialog 的按鈕區）</summary>
    public static DependencyObject? Descendant(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } && n == name) return child;
            if (Descendant(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>顯示（同時只開一個 ContentDialog，§11.2.1）；關掉後回來</summary>
    public async Task Show()
    {
        // ContentDialog 打開後自己會把焦點給「第一個」能 Tab 的元件，有時候比 Opened 排的工作晚（2026-11-17 虛擬機：清空垃圾桶的確認焦點在「取消」，
        // Enter 變成取消）：要給焦點的元件排在 Tab 順序的最前面，兩邊給的就是同一個
        if (Focus is { } first) first.TabIndex = 0;
        Dialogs.OpenSheet = Title;
        try { await Dialogs.Show(Dialog); }
        finally { if (Dialogs.OpenSheet == Title) Dialogs.OpenSheet = null; }
    }
}
