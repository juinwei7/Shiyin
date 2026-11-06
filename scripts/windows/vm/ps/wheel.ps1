# 官網截圖用（docs/website.md §16.4）：在虛擬機裡用真的滑鼠滾輪捲動拾音的頁面，捲完把滑鼠移到 AwayX、AwayY（移到視窗外，捲軸、提示框才會收起來）
#   wheel.ps1 <X> <Y> <格數，負的往下> [AwayX] [AwayY]（實際像素）
param([int]$X, [int]$Y, [int]$Notches = -3, [int]$AwayX = 5, [int]$AwayY = 5)
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class WheelMouse2 {
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out P p);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, IntPtr e);
}
'@
[WheelMouse2]::SetThreadDpiAwarenessContext([IntPtr]-4) | Out-Null
$p = New-Object WheelMouse2+P
[WheelMouse2]::SetCursorPos($X, $Y) | Out-Null
[WheelMouse2]::mouse_event(1, 1, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300
[WheelMouse2]::GetCursorPos([ref]$p) | Out-Null; "at $($p.X),$($p.Y)"
for ($i = 0; $i -lt [Math]::Abs($Notches); $i++) {
  [WheelMouse2]::mouse_event(0x0800, 0, 0, 120 * [Math]::Sign($Notches), [IntPtr]::Zero)
  Start-Sleep -Milliseconds 120
}
Start-Sleep -Milliseconds 400
[WheelMouse2]::SetCursorPos($AwayX, $AwayY) | Out-Null
[WheelMouse2]::mouse_event(1, 1, 0, 0, [IntPtr]::Zero)
"wheeled $Notches"
