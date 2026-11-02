param([string]$Out, [string]$Name = "win", [string]$Title = "FrameGlass Settings", [switch]$Overlay)
$ErrorActionPreference = 'Continue'

# Defaults are derived from this script's own location, so a clone works anywhere. These were
# absolute paths to the author's machine, which broke the script for everyone else.
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $PSScriptRoot) 'shots' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# Per-monitor DPI aware BEFORE anything touches a window. A DPI-unaware process gets virtualised
# coordinates: a 1350px window on a 150% display is reported as 900px, and CopyFromScreen then
# captures the wrong part of the screen entirely. Every size in this script is physical pixels.
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class DpiAwake {
  [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  public static void Wake() {
    if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
    if (SetProcessDpiAwarenessContext(new IntPtr(-2))) return;
    SetProcessDPIAware();
  }
}
"@ -Language CSharp
[DpiAwake]::Wake()

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public class Cap {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  delegate bool EnumProc(IntPtr h, IntPtr l);
  public static List<IntPtr> Visible(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
  public static string Title(IntPtr h) {
    int n = GetWindowTextLength(h); var sb = new StringBuilder(n+2); GetWindowText(h, sb, n+2); return sb.ToString();
  }
  public static bool IsLayered(IntPtr h) { long e = GetWindowLongPtrW(h, -20).ToInt64(); return (e & 0x80000L) != 0; }
  public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
  public static int DpiOf(IntPtr h) { return GetDeviceCaps(GetDC(h), 88); }
}
"@ -Language CSharp

Add-Type -AssemblyName System.Drawing
$procid = [uint32](Get-Process FrameGlass).Id
$handles = [Cap]::Visible($procid)
"pid=$procid visible windows: $($handles.Count)"

$target = $null
if ($Overlay) {
  $target = $handles | Where-Object { [Cap]::IsLayered($_) } | Select-Object -First 1
  if (-not $target) { "no layered window"; exit 1 }
} else {
  $target = $handles | Where-Object { [Cap]::Title($_) -eq $Title } | Select-Object -First 1
  if (-not $target) { "window '$Title' not found"; exit 1 }
}

$r = [Cap]::Rect($target)
$w = $r.R - $r.L
$ht = $r.B - $r.T
"hwnd=$target title='$([Cap]::Title($target))' rect=$($r.L),$($r.T) ${w}x${ht} dpi=$([Cap]::DpiOf($target))"
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save((Join-Path $Out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"shot: $Out\$Name.png (${w}x${ht} physical)"
