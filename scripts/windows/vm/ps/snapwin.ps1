# 官網截圖用（docs/website.md §16.4）：截拾音的某個視窗（DWM 的外框，不含陰影），或把視窗放到左上角
#   snapwin.ps1 snap <視窗標題> <輸出.png>
#   snapwin.ps1 place <視窗標題> -Width <寬> -Height <高>（邏輯像素）
param([string]$Action, [string]$Title, [string]$Out, [int]$Width = 960, [int]$Height = 640)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr v);
  public static IntPtr Find(string title, uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p != pid || !IsWindowVisible(h)) return true;
      var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256); if (sb.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
'@
# 這個 PowerShell 要支援高 DPI，座標才是實際像素（不然會被系統換算）
[Win]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$proc = Get-Process Shiyin | Select-Object -First 1
# 主視窗用 MainWindowHandle（同名的視窗可能不只一個）
$h = if ($proc.MainWindowTitle -eq $Title) { $proc.MainWindowHandle } else { [Win]::Find($Title, [uint32]$proc.Id) }
if ($h -eq [IntPtr]::Zero) { throw "找不到視窗：$Title" }
$scale = [Win]::GetDpiForWindow($h) / 96.0
if ($Action -eq 'place') {
  [Win]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 500
  $ok = [Win]::SetWindowPos($h, [IntPtr]::Zero, 30, 30, [int]($Width * $scale), [int]($Height * $scale), 0x0014)
  Start-Sleep -Milliseconds 500
  $r = New-Object Win+RECT; [Win]::GetWindowRect($h, [ref]$r) | Out-Null
  "placed ok=$ok at scale $scale -> $($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)"
  return
}
$r = New-Object Win+RECT
[Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"saved $($bmp.Width)x$($bmp.Height) scale $scale"
