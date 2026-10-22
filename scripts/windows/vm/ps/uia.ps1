# UI Automation（在桌面工作階段裡跑，design-windows §15.4）：不送按鍵，用元件的名稱操作拾音的視窗
#   uia.ps1 windows                       列出拾音的視窗
#   uia.ps1 dump <視窗> [深度]             列出元件（類型、名稱、AutomationId）
#   uia.ps1 click <視窗> <名稱> [第幾個]    按下（Invoke／Toggle／選取／展開）
#   uia.ps1 open <視窗> <名稱> [第幾個]     按下會打開選單的按鈕（另一個程序去按，不等選單關掉）
#   uia.ps1 menu <視窗> <按鈕> [選項]       打開選單，列出選項；有給選項就按它（同一次做完，中間不能換程序，不然選單會被關掉）
#   uia.ps1 choose <視窗> <下拉選單> <選項>  下拉選單選一項（展開、選取、收起）
#   uia.ps1 set <視窗> <名稱> <文字>        填輸入框（ValuePattern）
#   uia.ps1 close <視窗>                   關視窗（WindowPattern）
param([string]$Action, [string]$Window, [string]$Target, [string]$Extra)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
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
    'close' { $p = Try-Pattern (Find-Window $Window) ([System.Windows.Automation.WindowPattern]::Pattern); $p.Close(); "closed '$Window'" }
    default { throw "不認得的動作：$Action" }
}
