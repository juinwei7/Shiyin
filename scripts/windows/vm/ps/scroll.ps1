# scroll.ps1 <視窗> list                       列出可以捲動的元件（第幾個、位置、捲動百分比、可見比例）
# scroll.ps1 <視窗> set <第幾個> <垂直百分比>   設垂直捲動的百分比
# scroll.ps1 <視窗> rows <名稱開頭>            列出某些元件的位置（例如時間 00:）
param([string]$Window, [string]$Action = 'list', [string]$A1, [string]$A2)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Dp2 { [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr v); }
'@
[Dp2]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$procs = @(Get-Process Shiyin | ForEach-Object Id)
$w = $AE::RootElement.FindAll($T::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $procs -contains $_.Current.ProcessId -and $_.Current.Name -eq $Window } | Select-Object -First 1
if (-not $w) { throw "找不到視窗 $Window" }
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::IsScrollPatternAvailableProperty, $true)
$all = @($w.FindAll($T::Descendants, $cond))
switch ($Action) {
  'list' {
    for ($i = 0; $i -lt $all.Count; $i++) {
      $e = $all[$i]; $p = $e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); $r = $e.Current.BoundingRectangle
      "$i $($e.Current.ControlType.ProgrammaticName) '$($e.Current.Name)' id=$($e.Current.AutomationId) rect=$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height) v=$($p.Current.VerticallyScrollable) vp=$([math]::Round($p.Current.VerticalScrollPercent,3)) vs=$([math]::Round($p.Current.VerticalViewSize,3))"
    }
  }
  'set' {
    $e = $all[[int]$A1]; $p = $e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $p.SetScrollPercent(-1, [double]$A2); Start-Sleep -Milliseconds 600
    "set $A1 -> $([math]::Round($p.Current.VerticalScrollPercent,3))"
  }
  'rows' {
    $c2 = [System.Windows.Automation.Condition]::TrueCondition
    $w.FindAll($T::Descendants, $c2) | Where-Object { $_.Current.Name -like "$A1*" } | ForEach-Object { $r = $_.Current.BoundingRectangle; "'$($_.Current.Name)' $($_.Current.ControlType.ProgrammaticName) $([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)" }
  }
}
