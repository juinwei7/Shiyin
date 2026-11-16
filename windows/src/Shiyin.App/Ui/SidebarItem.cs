using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Shiyin.App;

/// <summary>
/// 側欄清單裡的動作列（R101、design-windows §11.34）：段落標題、資料夾和定期會議（點了在原地展開）、展開的會議、「顯示全部 N 場」、分組的標題。
/// 是清單的一列（上下鍵走得到、滑過有底色，跟其他列一樣），但點了不會變成選取：使用者選到時清單把選取還原（MainWindow 的 SelectionChanged；
/// 只看某個資料夾時程式選那一列，照舊是選到的樣子），動作只在點一下（Tapped）、Enter／空白鍵（焦點在這一列本身時）、UI Automation 的 Invoke 時做。
/// 可以收合的（expanded 不是 null）多一個 UI Automation 的 ExpandCollapse
/// </summary>
public sealed partial class SidebarItem : ListViewItem
{
    readonly Action activate;
    /// <summary>自己處理的按鍵（展開的會議：Enter／F2 重新命名、Delete）；回傳 true 是處理了</summary>
    readonly Func<VirtualKey, bool>? key;

    /// <summary>段落標題展開了沒；不是段落標題時 null</summary>
    public bool? Expanded { get; }

    /// <summary>
    /// 可以拖的列（資料夾、定期會議、展開的會議，R103、design-windows §11.36）：拖曳開始時把要拖的東西放進 DataPackage。
    /// 左鍵按下不交給 ListViewItem（下面的 OnPointerPressed），ListView 自己的拖曳不會開始，所以按住移動超過 4（系統的 SM_CXDRAG）時自己 StartDragAsync；
    /// 沒移動就是原本的點一下（Tapped）
    /// </summary>
    public Action<DataPackage>? DragData { get; set; }
    Point? pressedAt;

    public SidebarItem(Action activate, bool? expanded = null, Func<VirtualKey, bool>? key = null)
    {
        this.activate = activate;
        this.key = key;
        Expanded = expanded;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        // ListViewItem 自己會處理點擊（選取），所以要 handledEventsToo；從列裡的按鈕（「⋯」）來的不算
        AddHandler(TappedEvent, new TappedEventHandler((_, e) => { if (!FromButton(e.OriginalSource as DependencyObject)) Activate(); }), true);
        PreviewKeyDown += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, this)) return;
            if (this.key?.Invoke(e.Key) == true) e.Handled = true;
            else if (e.Key is VirtualKey.Enter or VirtualKey.Space)
            {
                e.Handled = true;
                Activate();
            }
        };
        PointerEntered += (_, _) => hovering = true;
        PointerExited += (_, _) => hovering = false;
        // 範本套上之後才有 ListViewItemPresenter；Loaded 之後 ListViewItem 還會設一次 presenter 的狀態，排在後面再套（不然一出現時的灰底不見）
        Loaded += (_, _) =>
        {
            ApplyBackplate();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(ApplyBackplate));
        };
        DragStarting += (_, e) =>
        {
            if (DragData is null)
            {
                e.Cancel = true;
                return;
            }
            e.AllowedOperations = DataPackageOperation.Move;
            e.Data.RequestedOperation = DataPackageOperation.Move;
            Ui.Guard(() => DragData(e.Data));
        };
    }

    public void Activate() => Ui.Guard(activate);

    /// <summary>
    /// 整列的底（展開的會議選到時的灰底、拖東西進來時的青瓷綠，R101.4、R103.4）；null 換回原本的。直接改 ListViewItemPresenter 的
    /// Background、PointerOverBackground、Selected*：改 ListViewItem.Background 的話滑過（拖曳經過時也是滑過的狀態）就被蓋掉，
    /// 拿掉時畫面也不一定換回來（2026-11-16 虛擬機：拖過去的資料夾一直是青瓷綠）
    /// </summary>
    public void Backplate(Brush? brush)
    {
        backplate = brush;
        hasBackplate = true;
        ApplyBackplate();
    }

    Brush? backplate;
    bool hasBackplate;
    bool hovering;
    (Brush? Over, Brush? Selected, Brush? SelectedOver)? original;

    void ApplyBackplate()
    {
        if (!hasBackplate) return;
        // 平常的狀態畫的是 ListViewItem.Background（改 presenter 的 Background 沒有用，2026-11-16 虛擬機）；滑過、選到的狀態是 presenter 的
        Background = backplate ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (Presenter() is { } p)
        {
            original ??= (p.PointerOverBackground, p.SelectedBackground, p.SelectedPointerOverBackground);
            var o = original.Value;
            p.PointerOverBackground = backplate ?? o.Over;
            p.SelectedBackground = backplate ?? o.Selected;
            p.SelectedPointerOverBackground = backplate ?? o.SelectedOver;
        }
        // 底色換了要等狀態換了才重畫（2026-11-16 虛擬機：展開的會議選到時的灰底一直沒出來、拖走後的青瓷綠留著）：切一次狀態
        if (!IsSelected && IsLoaded)
        {
            VisualStateManager.GoToState(this, hovering ? "Normal" : "PointerOver", false);
            VisualStateManager.GoToState(this, hovering ? "PointerOver" : "Normal", false);
        }
    }

    /// <summary>範本裡的 ListViewItemPresenter（範本的根或根底下幾層）</summary>
    ListViewItemPresenter? Presenter() => FindPresenter(this, 3);

    static ListViewItemPresenter? FindPresenter(DependencyObject parent, int depth)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ListViewItemPresenter p) return p;
            if (depth > 1 && FindPresenter(child, depth - 1) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// 左鍵、觸控按下：不交給 ListViewItem（它會選取這一列，清單把選取還原時焦點跳回選到的那一列、捲走，2026-11-16 虛擬機），
    /// 自己拿焦點；動作在 Tapped。從列裡的按鈕來的（已經處理過）、右鍵照舊
    /// </summary>
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Focus(FocusState.Pointer);
            // 從列裡的按鈕（「⋯」）來的已經處理過，不會到這裡
            pressedAt = DragData is null ? null : e.GetCurrentPoint(this).Position;
            e.Handled = true;
            return;
        }
        base.OnPointerPressed(e);
    }

    /// <summary>按住左鍵移動超過 4 就開始拖曳（DragStarting 填資料）</summary>
    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (pressedAt is not { } start || DragData is null) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            pressedAt = null;
            return;
        }
        if (Math.Abs(point.Position.X - start.X) <= 4 && Math.Abs(point.Position.Y - start.Y) <= 4) return;
        pressedAt = null;
        _ = StartDrag(point);
    }

    async Task StartDrag(Microsoft.UI.Input.PointerPoint point)
    {
        try { await StartDragAsync(point); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Shiyin.Services.Log.Error($"drag failed: {e.HResult:X8}");
        }
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        pressedAt = null;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        pressedAt = null;
        base.OnPointerCanceled(e);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (!e.Handled && e.GetCurrentPoint(this).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
        {
            pressedAt = null;
            e.Handled = true;
            return;
        }
        base.OnPointerReleased(e);
    }

    bool FromButton(DependencyObject? source)
    {
        for (var e = source; e is not null && !ReferenceEquals(e, this); e = VisualTreeHelper.GetParent(e))
            if (e is ButtonBase) return true;
        return false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>ListView 的項目（ListViewItemDataAutomationPeer）把不認得的 pattern 交給容器的 peer，就是這個</summary>
    sealed partial class Peer(SidebarItem owner) : ListViewItemAutomationPeer(owner), IInvokeProvider, IExpandCollapseProvider
    {
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Invoke => this,
            PatternInterface.ExpandCollapse when owner.Expanded is not null => this,
            _ => base.GetPatternCore(patternInterface),
        };

        public void Invoke() => owner.Activate();

        public ExpandCollapseState ExpandCollapseState => owner.Expanded == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public void Expand()
        {
            if (owner.Expanded == false) owner.Activate();
        }

        public void Collapse()
        {
            if (owner.Expanded == true) owner.Activate();
        }
    }
}
