# UI Automation（在桌面工作階段裡跑，design-windows §15.4）：不送按鍵，用元件的名稱操作拾音的視窗
#   uia.ps1 windows                       列出拾音的視窗
#   uia.ps1 dump <視窗> [深度]             列出元件（類型、名稱、AutomationId）
#   uia.ps1 click <視窗> <名稱> [第幾個]    按下（Invoke／Toggle／選取／展開）
#   uia.ps1 open <視窗> <名稱> [第幾個]     按下會打開選單的按鈕（另一個程序去按，不等選單關掉）
#   uia.ps1 menu <視窗> <按鈕> [選項]       打開選單，列出選項；有給選項就按它（同一次做完，中間不能換程序，不然選單會被關掉）
#   uia.ps1 choose <視窗> <下拉選單> <選項>  下拉選單選一項（展開、選取、收起）
#   uia.ps1 set <視窗> <名稱> <文字>        填輸入框（ValuePattern）
#   uia.ps1 close <視窗>                   關視窗（WindowPattern）
#   uia.ps1 show <視窗> <名稱> [第幾個]     捲到看得到那個元件（ScrollItemPattern，截圖前用）
#   uia.ps1 rclick <視窗> <名稱> [第幾個] [選項] [子選項]  真的滑鼠右鍵點那個元件（打開 ContextFlyout；UI Automation 沒有開右鍵選單的方法），
#                                         再點選單裡的選項（沒給就列出選單）。同一次做完：下一個 PowerShell 會搶到焦點，選單會被關掉
#   uia.ps1 lclick <視窗> <名稱> [第幾個] [選項] [子選項]  真的滑鼠左鍵點（DropDownButton 用 Invoke 打不開選單時）
#   uia.ps1 ctrlclick <視窗> <名稱> [第幾個] 按住 Ctrl 點（清單多選）
#   uia.ps1 key <視窗> <虛擬鍵碼（十六進位）> [ctrl]  按一個鍵（2E 是 Delete）。這四個是虛擬機桌面上真的輸入，不會送到 Mac
param([string]$Action, [string]$Window, [string]$Target, [string]$Extra, [string]$Item1, [string]$Item2)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class RealInput {
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  // 前景鎖：先接上目前前景視窗的輸入佇列，SetForegroundWindow 才不會被擋
  public static bool Front(IntPtr h) {
    uint me = GetCurrentThreadId(), fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
    if (fg != 0 && fg != me) AttachThreadInput(me, fg, true);
    var ok = SetForegroundWindow(h);
    if (fg != 0 && fg != me) AttachThreadInput(me, fg, false);
    return ok;
  }
  public static void Click(int x, int y, bool right, bool ctrl) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
    if (ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero);
    mouse_event(right ? 8u : 2u, 0, 0, 0, UIntPtr.Zero); mouse_event(right ? 16u : 4u, 0, 0, 0, UIntPtr.Zero);
    if (ctrl) keybd_event(0x11, 0, 2, UIntPtr.Zero);
  }
}
'@
# UI Automation 的座標是實際像素：宣告支援高 DPI，滑鼠才不會被換算到別的地方（同 clickname.ps1）
[RealInput]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$procs = @(Get-Process Shiyin -ErrorAction SilentlyContinue | ForEach-Object Id)
function Windows {
    $A::RootElement.FindAll($T::Children, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $procs -contains $_.Current.ProcessId }
}
function Find-Window([string]$name) {
    $w = Windows | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if (-not $w) { throw "找不到視窗：$name（有：$((Windows | ForEach-Object { $_.Current.Name }) -join '、')）" }
    $w
}
function Describe($e) { "$($e.Current.ControlType.ProgrammaticName.Replace('ControlType.', '')) '$($e.Current.Name)' id=$($e.Current.AutomationId)$(if (-not $e.Current.IsEnabled) { ' (停用)' })" }
function Dump($e, [int]$depth, [int]$max, [string]$indent) {
    if ($depth -gt $max) { return }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $c = $walker.GetFirstChild($e)
    while ($c) {
        "$indent$(Describe $c)"
        Dump $c ($depth + 1) $max ($indent + '  ')
        $c = $walker.GetNextSibling($c)
    }
}
function Find-Element($w, [string]$name, [int]$index) {
    # 「名稱#2」：同名的第 2 個（從 0 算），給沒有 index 參數的動作（menu）用
    if ($name -match '^(.*)#(\d+)$') { $name = $Matches[1]; $index = [int]$Matches[2] }
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    $all = @($w.FindAll($T::Descendants, $cond))
    # 同名的元件（例如側欄狀態的文字「完成」和編輯器的「完成」按鈕）：能按的排前面
    $actionable = { param($e) $e.GetCurrentPropertyValue($A::IsInvokePatternAvailableProperty) -or $e.GetCurrentPropertyValue($A::IsTogglePatternAvailableProperty) -or $e.GetCurrentPropertyValue($A::IsSelectionItemPatternAvailableProperty) -or $e.GetCurrentPropertyValue($A::IsExpandCollapsePatternAvailableProperty) }
    $all = @(@($all | Where-Object { & $actionable $_ }) + @($all | Where-Object { -not (& $actionable $_) }))
    if ($all.Count -le $index) { throw "找不到「$name」（第 $index 個；共 $($all.Count) 個）" }
    $all[$index]
}
function Try-Pattern($e, $pattern) { $p = $null; if ($e.TryGetCurrentPattern($pattern, [ref]$p)) { $p } else { $null } }
switch ($Action) {
    'windows' { Windows | ForEach-Object { $r = $_.Current.BoundingRectangle; "'$($_.Current.Name)' $([int]$r.Width)x$([int]$r.Height)" } }
    'dump' { $w = Find-Window $Window; Describe $w; Dump $w 0 ([int]($(if ($Target) { $Target } else { 6 }))) '  ' }
    'click' {
        $e = Find-Element (Find-Window $Window) $Target ([int]$(if ($Extra) { $Extra } else { 0 }))
        if ($p = Try-Pattern $e ([System.Windows.Automation.InvokePattern]::Pattern)) { $p.Invoke(); "invoked $(Describe $e)" }
        elseif ($p = Try-Pattern $e ([System.Windows.Automation.TogglePattern]::Pattern)) { $p.Toggle(); "toggled $(Describe $e) -> $($p.Current.ToggleState)" }
        elseif ($p = Try-Pattern $e ([System.Windows.Automation.SelectionItemPattern]::Pattern)) { $p.Select(); "selected $(Describe $e)" }
        elseif ($p = Try-Pattern $e ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)) { $p.Expand(); "expanded $(Describe $e)" }
        else { throw "「$Target」不能按：$(Describe $e)" }
    }
    'open' {
        # Invoke 打開選單時會等到選單關掉才回來：交給另一個 PowerShell 去按，等 1.5 秒讓選單出現
        $child = "Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes; & '$PSCommandPath' click '$($Window -replace "'", "''")' '$($Target -replace "'", "''")' '$Extra'"
        Start-Process powershell.exe -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', $child | Out-Null
        Start-Sleep -Milliseconds 1500
        "opened '$Target'"
    }
    'menu' {
        $child = "Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes; & '$PSCommandPath' click '$($Window -replace "'", "''")' '$($Target -replace "'", "''")'"
        Start-Process powershell.exe -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', $child | Out-Null
        Start-Sleep -Milliseconds 1500
        # 選單在 app 的視窗裡（popup），也可能是另一個最上層的視窗
        $cond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
        $items = @(Windows | ForEach-Object { $_.FindAll($T::Descendants, $cond) })
        if (-not $Extra) { $items | ForEach-Object { Describe $_ } }
        else {
            $item = $items | Where-Object { $_.Current.Name -eq $Extra } | Select-Object -First 1
            if (-not $item) { throw "選單裡沒有「$Extra」（有：$(($items | ForEach-Object { $_.Current.Name }) -join '、')）" }
            if ($p = Try-Pattern $item ([System.Windows.Automation.InvokePattern]::Pattern)) { $p.Invoke(); "invoked $(Describe $item)" }
            elseif ($p = Try-Pattern $item ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)) { $p.Expand(); "expanded $(Describe $item)" }
        }
    }
    'choose' {
        $w = Find-Window $Window
        $combo = @($w.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Target))) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox }) | Select-Object -First 1
        if (-not $combo) { throw "找不到下拉選單「$Target」" }
        $ec = Try-Pattern $combo ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ec.Expand(); Start-Sleep -Milliseconds 600
        $cond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $items = @(Windows | ForEach-Object { $_.FindAll($T::Descendants, $cond) }) | Where-Object { $_.Current.Name -eq $Extra }
        if (-not $items) { $ec.Collapse(); throw "下拉選單裡沒有「$Extra」" }
        (Try-Pattern $items[0] ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        Start-Sleep -Milliseconds 300
        try { $ec.Collapse() } catch { }
        "chose '$Extra' in '$Target'; value: $(try { (Try-Pattern $combo ([System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()[0].Current.Name } catch { '?' })"
    }
    'set' {
        $e = Find-Element (Find-Window $Window) $Target 0
        $p = Try-Pattern $e ([System.Windows.Automation.ValuePattern]::Pattern)
        if (-not $p) { throw "「$Target」不能輸入：$(Describe $e)" }
        $p.SetValue($Extra); "set $(Describe $e)"
    }
    'show' {
        $e = Find-Element (Find-Window $Window) $Target ([int]$(if ($Extra) { $Extra } else { 0 }))
        $p = Try-Pattern $e ([System.Windows.Automation.ScrollItemPattern]::Pattern)
        if (-not $p) { throw "「$Target」不能捲到：$(Describe $e)" }
        $p.ScrollIntoView(); "shown $(Describe $e)"
    }
    'close' { $p = Try-Pattern (Find-Window $Window) ([System.Windows.Automation.WindowPattern]::Pattern); $p.Close(); "closed '$Window'" }
    { $_ -in 'rclick', 'lclick', 'ctrlclick' } {
        $w = Find-Window $Window
        $e = Find-Element $w $Target ([int]$(if ($Extra) { $Extra } else { 0 }))
        $r = $e.Current.BoundingRectangle
        # 先把拾音的視窗拉到前面：不然 Ctrl 會送到跑這個腳本的 PowerShell（排程工作開的，會搶到前景），拾音看不到 Ctrl
        $front = [RealInput]::Front([IntPtr]$w.Current.NativeWindowHandle)
        Start-Sleep -Milliseconds 400
        # 點元件的左邊（長的清單項目、整列的文字，中間可能是別的元件）
        [RealInput]::Click([int]($r.X + [Math]::Min($r.Width / 2, 60)), [int]($r.Y + $r.Height / 2), $Action -eq 'rclick', $Action -eq 'ctrlclick')
        Start-Sleep -Milliseconds 800
        "$Action $(Describe $e)$(if (-not $front) { '（拉到前面失敗）' })"
        if ($Action -eq 'ctrlclick') { return }
        # 打開的選單：同一個程序裡接著選（下一個 PowerShell 開起來會搶到焦點，選單就被關掉了）；沒給選項就列出來
        $cond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
        foreach ($name in @($Item1, $Item2) | Where-Object { $_ }) {
            $item = @(Windows | ForEach-Object { $_.FindAll($T::Descendants, $cond) }) | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
            if (-not $item) { throw "選單裡沒有「$name」（有：$((@(Windows | ForEach-Object { $_.FindAll($T::Descendants, $cond) }) | ForEach-Object { $_.Current.Name }) -join '、')）" }
            $ir = $item.Current.BoundingRectangle
            [RealInput]::Click([int]($ir.X + [Math]::Min($ir.Width / 2, 60)), [int]($ir.Y + $ir.Height / 2), $false, $false)
            Start-Sleep -Milliseconds 700
            "chose $(Describe $item)"
        }
        if (-not $Item1) { @(Windows | ForEach-Object { $_.FindAll($T::Descendants, $cond) }) | ForEach-Object { "  $(Describe $_)" } }
    }
    'key' {
        # 對拾音的視窗按一個鍵（虛擬鍵碼，十六進位，例如 2E 是 Delete）；Extra 給 ctrl 時按住 Ctrl。焦點在哪個元件就送到哪裡
        $w = Find-Window $Window
        $front = [RealInput]::Front([IntPtr]$w.Current.NativeWindowHandle)
        Start-Sleep -Milliseconds 400
        $vk = [Convert]::ToByte($Target, 16)
        if ($Extra -eq 'ctrl') { [RealInput]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero) }
        [RealInput]::keybd_event($vk, 0, 0, [UIntPtr]::Zero); [RealInput]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
        if ($Extra -eq 'ctrl') { [RealInput]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 800
        "key $Target$(if ($Extra) { "+$Extra" })$(if (-not $front) { '（拉到前面失敗）' })"
    }
    default { throw "不認得的動作：$Action" }
}
