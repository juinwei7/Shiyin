using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    }

    public void Activate() => Ui.Guard(activate);

    /// <summary>
    /// 左鍵、觸控按下：不交給 ListViewItem（它會選取這一列，清單把選取還原時焦點跳回選到的那一列、捲走，2026-11-16 虛擬機），
    /// 自己拿焦點；動作在 Tapped。從列裡的按鈕來的（已經處理過）、右鍵照舊
    /// </summary>
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Focus(FocusState.Pointer);
            e.Handled = true;
            return;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (!e.Handled && e.GetCurrentPoint(this).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
        {
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
