param([uint32]$ProcId = 0)
$ErrorActionPreference = 'Continue'
if (-not $ProcId) { $ProcId = [uint32](Get-Process FrameGlass).Id }
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type @"
using System;using System.Text;using System.Runtime.InteropServices;using System.Collections.Generic;
public class Win {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  delegate bool EnumProc(IntPtr h, IntPtr l);
  public static List<string> Visible(uint pid) {
    var list = new List<string>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) return true;
      int n = GetWindowTextLength(h); var sb = new StringBuilder(n+2); GetWindowText(h, sb, n+2);
      RECT r; GetWindowRect(h, out r);
      list.Add("rect=" + r.L + "," + r.T + " " + (r.R-r.L) + "x" + (r.B-r.T)
        + " ex=0x" + GetWindowLongPtr(h,-20).ToString("X") + " title='" + sb + "'");
      return true; }, IntPtr.Zero);
    return list;
  }
}
"@
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
"pid=$ProcId  virtual=$($vs.Left),$($vs.Top) $($vs.Width)x$($vs.Height)"
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
  "screen $($s.DeviceName) primary=$($s.Primary) $($s.Bounds.Width)x$($s.Bounds.Height)"
}
[Win]::Visible($ProcId) | ForEach-Object { "  $_" }
