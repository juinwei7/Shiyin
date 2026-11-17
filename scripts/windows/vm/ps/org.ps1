# org.ps1 <視窗> <清單項目（萬用字元）> <選單項目> <子選單項目|-> <輸出.png|->
# 「整理」截圖用：真的滑鼠右鍵點側欄的一場會議，滑到選單項目讓子選單展開，再滑到子選單的一項（- 不滑），
# 同一個程序裡截主視窗（DWM 外框，CopyFromScreen；下一個 PowerShell 會搶到前景、選單會關掉）
param([string]$Window, [string]$Row, [string]$Item, [string]$Sub = '-', [string]$Out = '-')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Og {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
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
  public static void MoveTo(int x, int y) {
    int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
    mouse_event(0x8001, (int)((x * 65535L + w / 2) / Math.Max(1, w - 1)), (int)((y * 65535L + h / 2) / Math.Max(1, h - 1)), 0, UIntPtr.Zero);
  }
  public static void Glide(int x0, int y0, int x1, int y1, int steps) {
    for (int i = 1; i <= steps; i++) { MoveTo(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps); System.Threading.Thread.Sleep(25); }
  }
}
'@
[Og]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$procs = @(Get-Process Shiyin | ForEach-Object Id)
function Wins { $A::RootElement.FindAll($T::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $procs -contains $_.Current.ProcessId } }
function MenuItems { $c = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem); @(Wins | ForEach-Object { $_.FindAll($T::Descendants, $c) }) }
$w = Wins | Where-Object { $_.Current.Name -eq $Window } | Select-Object -First 1
if (-not $w) { throw "找不到視窗 $Window" }
$rowEl = @($w.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $Row -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $_.Current.BoundingRectangle.Width -gt 0 -and -not [double]::IsInfinity($_.Current.BoundingRectangle.X) }) | Select-Object -First 1
if (-not $rowEl) { throw "找不到 $Row（或不在畫面上）" }
$r = $rowEl.Current.BoundingRectangle
$h = [IntPtr]$w.Current.NativeWindowHandle
"front $([Og]::Front($h))"
Start-Sleep -Milliseconds 400
$x = [int]($r.X + 70); $y = [int]($r.Y + $r.Height / 2)
[Og]::SetCursorPos($x, $y) | Out-Null; [Og]::MoveTo($x, $y); Start-Sleep -Milliseconds 150
[Og]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); [Og]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 1000
"row at $([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height); right-clicked $x,$y"
MenuItems | ForEach-Object { $mr = $_.Current.BoundingRectangle; "  menu '$($_.Current.Name)' $([int]$mr.X),$([int]$mr.Y) $([int]$mr.Width)x$([int]$mr.Height)" }
$cx = $x; $cy = $y
if ($Item -and $Item -ne '-') {
  $mi = MenuItems | Where-Object { $_.Current.Name -eq $Item } | Select-Object -First 1
  if (-not $mi) { throw "選單裡沒有 $Item" }
  $mr = $mi.Current.BoundingRectangle
  $tx = [int]($mr.X + $mr.Width * 0.4); $ty = [int]($mr.Y + $mr.Height / 2)
  [Og]::Glide($cx, $cy, $tx, $ty, 12); $cx = $tx; $cy = $ty
  Start-Sleep -Milliseconds 1500
  "hover '$Item' at $tx,$ty"
  MenuItems | ForEach-Object { $mr = $_.Current.BoundingRectangle; "  menu '$($_.Current.Name)' $([int]$mr.X),$([int]$mr.Y) $([int]$mr.Width)x$([int]$mr.Height)" }
  if ($Sub -and $Sub -ne '-') {
    $si = MenuItems | Where-Object { $_.Current.Name -eq $Sub } | Select-Object -First 1
    if (-not $si) { throw "子選單裡沒有 $Sub" }
    $sr = $si.Current.BoundingRectangle
    $sx = [int]($sr.X + $sr.Width * 0.5); $sy = [int]($sr.Y + $sr.Height / 2)
    # 先水平移進子選單（不要斜著經過別的項目，子選單才不會關掉）
    [Og]::Glide($cx, $cy, $sx, $cy, 10); [Og]::Glide($sx, $cy, $sx, $sy, 8)
    Start-Sleep -Milliseconds 1200
    "hover sub '$Sub' at $sx,$sy"
  }
}
if ($Out -and $Out -ne '-') {
  $d = New-Object Og+RECT
  [Og]::DwmGetWindowAttribute($h, 9, [ref]$d, 16) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($d.R - $d.L), ($d.B - $d.T)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($d.L, $d.T, 0, 0, $bmp.Size)
  $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  "saved $($bmp.Width)x$($bmp.Height) from $($d.L),$($d.T)"
  # 整個畫面也存一份（看選單有沒有超出視窗）
  $full = New-Object System.Drawing.Bitmap 1920, 1200
  [System.Drawing.Graphics]::FromImage($full).CopyFromScreen(0, 0, 0, 0, $full.Size)
  $full.Save($Out.Replace('.png', '-screen.png'), [System.Drawing.Imaging.ImageFormat]::Png)
}
