$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;using System.Runtime.InteropServices;using System.Collections.Generic;
public class Disp {
  [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc f, IntPtr data);
  [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr m, int t, out uint x, out uint y);
  [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szDevice; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  delegate bool MonitorEnumProc(IntPtr m, IntPtr hdc, IntPtr rect, IntPtr data);
  public static List<string> All() {
    var list = new List<string>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr m, IntPtr hdc, IntPtr rect, IntPtr data) {
      uint x, y; int hr = GetDpiForMonitor(m, 0, out x, out y);
      var mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(mi); GetMonitorInfo(m, ref mi);
      list.Add(mi.szDevice + " " + (mi.rcMonitor.R - mi.rcMonitor.L) + "x" + (mi.rcMonitor.B - mi.rcMonitor.T)
        + " at " + mi.rcMonitor.L + "," + mi.rcMonitor.T
        + "  dpi=" + x + "  scale=" + (x/96.0).ToString("0.00") + "  primary=" + ((mi.dwFlags & 1) != 0) + " hr=" + hr);
      return true; }, IntPtr.Zero);
    return list;
  }
}
"@
"=== monitors ==="
[Disp]::All() | ForEach-Object { $_ }
"=== registry scaling (per-user, applied) ==="
$key = "HKCU:\Control Panel\Desktop\PerMonitorSettings"
if (Test-Path $key) {
  Get-ChildItem $key | ForEach-Object {
    $id = $_.PSChildName
    $dpi = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DpiValue
    $idx = $_.PSChildName -replace '\D',''
    $name = ([System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.DeviceName -match "DISPLAY$idx$" }).DeviceName
    "  $name (DISPLAY$idx)  DpiValue=$dpi  -> " + [math]::Round(96 * (1 + [double]$dpi/1000)) + " dpi = " + [math]::Round(100 + [double]$dpi/10) + "%"
  }
} else { "  no PerMonitorSettings key" }
