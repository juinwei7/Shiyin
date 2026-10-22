# 官網截圖用（docs/website.md §16.4）：在虛擬機裡用真的滑鼠點拾音的某個元件（照 UI Automation 的位置），點完把滑鼠移開
#   clickname.ps1 <視窗標題> <元件名稱> [第幾個]（名稱有 * 時比對清單項目）
param([string]$Window, [string]$Name, [int]$Index = 0)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Mouse {
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(80); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
}
'@
[Mouse]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$procs = @(Get-Process Shiyin | ForEach-Object Id)
$w = $A::RootElement.FindAll($T::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $procs -contains $_.Current.ProcessId -and $_.Current.Name -eq $Window } | Select-Object -First 1
if (-not $w) { throw "找不到視窗 $Window" }
# 名稱有 * 時照萬用字元比對（清單的項目名稱是「標題，日期，狀態」）
$all = if ($Name.Contains('*')) { @($w.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $Name -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem }) }
       else { @($w.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Name)))) }
if ($all.Count -le $Index) { throw "找不到 $Name" }
$r = $all[$Index].Current.BoundingRectangle
[Mouse]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
Start-Sleep -Milliseconds 300
[Mouse]::SetCursorPos(3000, 2000) | Out-Null
"clicked $Name at $([int]$r.X),$([int]$r.Y)"
