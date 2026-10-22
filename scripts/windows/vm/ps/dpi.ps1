# 改虛擬機的顯示縮放（docs/website.md §16.4）：dpi.ps1 [百分比]，不給時只讀目前的。用設定 app 用的同一個介面（DisplayConfigSetDeviceInfo），app 不用重開
param([int]$Percent = 0)
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Dpi {
  [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }
  [StructLayout(LayoutKind.Sequential)] public struct SourceInfo { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct TargetInfo { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PathInfo { public SourceInfo sourceInfo; public TargetInfo targetInfo; public uint flags; }
  [StructLayout(LayoutKind.Sequential)] public struct ModeInfo { public uint infoType; public uint id; public LUID adapterId; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] data; }
  [StructLayout(LayoutKind.Sequential)] public struct Header { public int type; public uint size; public LUID adapterId; public uint id; }
  [StructLayout(LayoutKind.Sequential)] public struct GetScale { public Header header; public int minRel; public int curRel; public int maxRel; }
  [StructLayout(LayoutKind.Sequential)] public struct SetScale { public Header header; public int rel; }
  [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
  [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] PathInfo[] p, ref uint nm, [Out] ModeInfo[] m, IntPtr topo);
  [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref GetScale g);
  [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SetScale s);
  static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };
  static PathInfo First() {
    uint np, nm; GetDisplayConfigBufferSizes(2, out np, out nm);
    var p = new PathInfo[np]; var m = new ModeInfo[nm];
    QueryDisplayConfig(2, ref np, p, ref nm, m, IntPtr.Zero); return p[0];
  }
  public static string Read() {
    var path = First();
    var g = new GetScale(); g.header.type = -3; g.header.size = (uint)Marshal.SizeOf(typeof(GetScale)); g.header.adapterId = path.sourceInfo.adapterId; g.header.id = path.sourceInfo.id;
    int r = DisplayConfigGetDeviceInfo(ref g);
    int rec = Array.IndexOf(Steps, 100) - g.minRel;
    return "ret " + r + " min " + g.minRel + " cur " + g.curRel + " max " + g.maxRel + " -> recommended " + Steps[Math.Abs(g.minRel)] + "% current " + Steps[Math.Abs(g.minRel) + g.curRel] + "%";
  }
  public static string Set(int percent) {
    var path = First();
    var g = new GetScale(); g.header.type = -3; g.header.size = (uint)Marshal.SizeOf(typeof(GetScale)); g.header.adapterId = path.sourceInfo.adapterId; g.header.id = path.sourceInfo.id;
    DisplayConfigGetDeviceInfo(ref g);
    int recIdx = Math.Abs(g.minRel);
    int want = Array.IndexOf(Steps, percent) - recIdx;
    if (want < g.minRel || want > g.maxRel) return "out of range: rel " + want + " (min " + g.minRel + ", max " + g.maxRel + ")";
    var s = new SetScale(); s.header.type = -4; s.header.size = (uint)Marshal.SizeOf(typeof(SetScale)); s.header.adapterId = path.sourceInfo.adapterId; s.header.id = path.sourceInfo.id; s.rel = want;
    return "set ret " + DisplayConfigSetDeviceInfo(ref s);
  }
}
'@
if ($Percent -gt 0) { [Dpi]::Set($Percent) }
[Dpi]::Read()
