# 展開會打開選單的元件（SplitButton 的箭頭、DropDownButton、子選單；ExpandCollapsePattern），列出選單裡的項目，有給項目就按它。
# 跟 uia.ps1 的 menu 不同：SplitButton 用 Invoke 會按到主要的動作，要用 Expand 才是箭頭（design-windows §15.4）
#   expand.ps1 <視窗> <名稱> [項目] [子選單的項目]
param([string]$Window, [string]$Target, [string]$Item, [string]$SubItem)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$procs = @(Get-Process Shiyin -ErrorAction SilentlyContinue | ForEach-Object Id)
function Windows { $A::RootElement.FindAll($T::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $procs -contains $_.Current.ProcessId } }
function Pattern($e, $p) { $x = $null; if ($e.TryGetCurrentPattern($p, [ref]$x)) { $x } else { $null } }
function MenuItems {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    @(Windows | ForEach-Object { $_.FindAll($T::Descendants, $cond) })
}
function Describe($e) {
    $state = ''
    if ($t = Pattern $e ([System.Windows.Automation.TogglePattern]::Pattern)) { $state = " [$($t.Current.ToggleState)]" }
    "$($e.Current.ControlType.ProgrammaticName.Replace('ControlType.', '')) '$($e.Current.Name)'$state$(if (-not $e.Current.IsEnabled) { ' (停用)' })"
}
function Press($e) {
    if ($p = Pattern $e ([System.Windows.Automation.InvokePattern]::Pattern)) { $p.Invoke(); "invoked $(Describe $e)" }
    elseif ($p = Pattern $e ([System.Windows.Automation.TogglePattern]::Pattern)) { $p.Toggle(); "toggled $(Describe $e)" }
    elseif ($p = Pattern $e ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)) { $p.Expand(); Start-Sleep -Milliseconds 800; "expanded $(Describe $e)" }
}
$w = Windows | Where-Object { $_.Current.Name -eq $Window } | Select-Object -First 1
if (-not $w) { throw "找不到視窗：$Window" }
$cond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Target)
$e = @($w.FindAll($T::Descendants, $cond)) | Where-Object { Pattern $_ ([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } | Select-Object -First 1
if (-not $e) { throw "找不到能展開的「$Target」" }
(Pattern $e ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
Start-Sleep -Milliseconds 1200
$items = MenuItems
if (-not $Item) { $items | ForEach-Object { Describe $_ }; return }
$found = $items | Where-Object { $_.Current.Name -eq $Item } | Select-Object -First 1
if (-not $found) { throw "選單裡沒有「$Item」（有：$(($items | ForEach-Object { $_.Current.Name }) -join '、')）" }
Press $found
if ($SubItem) {
    $sub = MenuItems | Where-Object { $_.Current.Name -eq $SubItem } | Select-Object -First 1
    if ($SubItem -eq '?') { MenuItems | ForEach-Object { Describe $_ }; return }
    if (-not $sub) { throw "子選單裡沒有「$SubItem」（有：$((MenuItems | ForEach-Object { $_.Current.Name }) -join '、')）" }
    Press $sub
}
