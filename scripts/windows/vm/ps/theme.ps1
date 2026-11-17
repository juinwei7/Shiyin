# theme.ps1 dark|light：切換應用程式和系統的深淺色，廣播 ImmersiveColorSet
param([string]$Mode = 'dark')
$v = if ($Mode -eq 'light') { 1 } else { 0 }
Set-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize -Name AppsUseLightTheme -Value $v
Set-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize -Name SystemUsesLightTheme -Value $v
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Bc { [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, UIntPtr w, string l, uint f, uint t, out UIntPtr r); }
'@
$r = [UIntPtr]::Zero
[Bc]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'ImmersiveColorSet', 2, 3000, [ref]$r) | Out-Null
"theme $Mode"
