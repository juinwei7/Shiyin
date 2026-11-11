# 在桌面工作階段裡按系統匣的拾音圖示右鍵、選選單裡的一項（design-windows §15.4）：tray-menu.ps1 [選項，預設「結束拾音」]
# 圖示通常在「顯示隱藏的圖示」裡：先找，找不到才打開（已經開著時再按會關掉）。右鍵沒有 UIA 的模式，用 SetCursorPos＋mouse_event（虛擬機裡的滑鼠，不是 Mac 的）
param([string]$Item = '結束拾音')   # PowerShell 的變數不分大小寫：下面不能再用 $item
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class M {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
'@
[M]::SetProcessDPIAware() | Out-Null
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$P = [System.Windows.Automation.InvokePattern]
$tray = $A::RootElement.FindFirst($T::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'Shell_TrayWnd')))
function Find-Icon {
  foreach ($w in $A::RootElement.FindAll($T::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    foreach ($e in $w.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
      if ($e.Current.Name -like '拾音*' -and $e.Current.AutomationId -ne 'Appid: velopack.ShiyinApp' -and -not $e.Current.IsOffscreen) { return $e }
    }
  }
}
$icon = Find-Icon
if (-not $icon) {
  $chevron = $tray.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, '顯示隱藏的圖示')))
  $chevron.GetCurrentPattern($P::Pattern).Invoke()
  Start-Sleep 1.5
  $icon = Find-Icon
}
if ($icon) { "icon: $($icon.Current.Name) $($icon.Current.BoundingRectangle)" }
if (-not $icon) { 'no tray icon'; return }
$r = $icon.Current.BoundingRectangle
[M]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
Start-Sleep -Milliseconds 300
[M]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [M]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep 1.5
$procs = @(Get-Process Shiyin | ForEach-Object Id)
$found = $null
foreach ($w in $A::RootElement.FindAll($T::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($procs -notcontains $w.Current.ProcessId) { continue }
  $f = $w.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Item)))
  if ($f) { $found = $f }
}
if (-not $found) { "no menu item: $Item"; return }
"item: $($found.Current.ControlType.ProgrammaticName)"
$found.GetCurrentPattern($P::Pattern).Invoke()
"invoked $Item"
