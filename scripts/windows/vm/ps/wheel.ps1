# 官網截圖用（docs/website.md §16.4）：在虛擬機裡用真的滑鼠滾輪捲動拾音的頁面，捲完把滑鼠移到 AwayX、AwayY（移到視窗外，捲軸、提示框才會收起來）
#   wheel.ps1 <X> <Y> <格數，負的往下> [AwayX] [AwayY] [ctrl]（實際像素）
#   ctrl：按住 Ctrl 捲（心智圖的 Ctrl＋滾輪縮放，design-windows §11.24）；先把拾音拉到前面（同 uia.ps1 的 key）
param([int]$X, [int]$Y, [int]$Notches = -3, [int]$AwayX = 5, [int]$AwayY = 5, [string]$Mod = '')
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class WheelMouse2 {
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out P p);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  public static bool Front(IntPtr h) {
    uint me = GetCurrentThreadId(), fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
    if (fg != 0 && fg != me) AttachThreadInput(me, fg, true);
    var ok = SetForegroundWindow(h);
    if (fg != 0 && fg != me) AttachThreadInput(me, fg, false);
    return ok;
  }
}
'@
[WheelMouse2]::SetThreadDpiAwarenessContext([IntPtr]-4) | Out-Null
$p = New-Object WheelMouse2+P
[WheelMouse2]::SetCursorPos($X, $Y) | Out-Null
[WheelMouse2]::mouse_event(1, 1, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300
[WheelMouse2]::GetCursorPos([ref]$p) | Out-Null; "at $($p.X),$($p.Y)"
$ctrl = $Mod -eq 'ctrl'
if ($ctrl) {
  $h = Get-Process Shiyin -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 -ExpandProperty MainWindowHandle
  if ($h) { "front $([WheelMouse2]::Front($h))" }
  Start-Sleep -Milliseconds 300
  [WheelMouse2]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 80
}
for ($i = 0; $i -lt [Math]::Abs($Notches); $i++) {
  [WheelMouse2]::mouse_event(0x0800, 0, 0, 120 * [Math]::Sign($Notches), [IntPtr]::Zero)
  Start-Sleep -Milliseconds 120
}
if ($ctrl) { [WheelMouse2]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
Start-Sleep -Milliseconds 400
[WheelMouse2]::SetCursorPos($AwayX, $AwayY) | Out-Null
[WheelMouse2]::mouse_event(1, 1, 0, 0, [IntPtr]::Zero)
"wheeled $Notches"
