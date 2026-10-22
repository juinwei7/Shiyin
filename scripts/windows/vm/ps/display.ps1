# 改虛擬機的解析度（docs/website.md §16.4）：display.ps1 list | current | set <寬> <高>。UTM 會把高度定在視窗的高度
param([string]$Action = 'list', [int]$Width = 0, [int]$Height = 0, [int]$Scale = 0)
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Disp {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
    public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, int param, IntPtr pv, uint flags);
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  public static DEVMODE New() { var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return d; }
  public static string List(string dev) {
    var seen = new System.Collections.Generic.SortedSet<string>(); var d = New();
    for (int i = 0; EnumDisplaySettings(dev, i, ref d); i++) seen.Add(d.dmPelsWidth.ToString("D4") + "x" + d.dmPelsHeight);
    return string.Join(" ", seen);
  }
  public static string Current(string dev) { var d = New(); bool ok = EnumDisplaySettings(dev, -1, ref d); return ok + " " + d.dmPelsWidth + "x" + d.dmPelsHeight + " size " + d.dmSize; }
  public static int Set(int w, int h) { var d = New(); EnumDisplaySettings(null, -1, ref d); d.dmPelsWidth = w; d.dmPelsHeight = h; d.dmFields = 0x80000 | 0x100000; return ChangeDisplaySettings(ref d, 0); }
}
'@
switch ($Action) {
  'list' { [Disp]::List($null); [Disp]::List('\\.\DISPLAY1') }
  'set' { "change: " + [Disp]::Set($Width, $Height) }
  'scale' {
    # SPI_SETLOGICALDPIOVERRIDE (0x9F)：相對於建議縮放的級數（設定 app 用的同一個）
    "scale: " + [Disp]::SystemParametersInfo(0x9F, $Scale, [IntPtr]::Zero, 1)
  }
  'current' { [Disp]::Current($null) + " dpi " + [Disp]::GetDpiForSystem() }
}
