param([string]$Shot)

$ErrorActionPreference = 'Continue'

# Paths come from this script's location so a clone works anywhere. $dotnet is a fixed SDK path.
$proj = Split-Path -Parent $PSScriptRoot
$pub  = "$proj\dist"
if (-not $Shot) { $Shot = Join-Path $proj 'ui.png' }
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$out = @()

Get-Process FrameGlass | Stop-Process -Force
Start-Sleep -Seconds 2

# build + publish + deploy. The running exe was stopped above, so the file can be overwritten.
& $dotnet publish "$proj\FrameGlass.csproj" -c Release -v q --nologo -o $pub 2>&1 | Select-Object -Last 3 | ForEach-Object { $out += "publish: $_" }
Copy-Item "$pub\FrameGlass.exe" "$proj\FrameGlass.exe" -Force
Copy-Item "$pub\app.ico" "$proj\app.ico" -Force
Remove-Item "$proj\frameglass.log" -Force
$out += "deployed: $((Get-Item "$proj\FrameGlass.exe").Length) bytes"

$cfgFile = "$proj\config.json"
$backup = Get-Content $cfgFile -Raw
($backup -replace '"StartMinimized":\s*(true|false)', '"StartMinimized": false') | Set-Content $cfgFile
$p = Start-Process -FilePath "$proj\FrameGlass.exe" -WorkingDirectory $proj -PassThru
Start-Sleep -Seconds 9

Add-Type @"
using System;using System.Text;using System.Runtime.InteropServices;using System.Collections.Generic;
public class WEnum {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  delegate bool EnumProc(IntPtr h, IntPtr l);
  public static List<string> ForPid(uint pid) {
    var list = new List<string>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid) return true;
      int n = GetWindowTextLength(h); var sb = new StringBuilder(n+2); GetWindowText(h, sb, n+2);
      RECT r; GetWindowRect(h, out r);
      list.Add("  vis=" + IsWindowVisible(h) + " title='" + sb + "' rect=" + r.L + "," + r.T + " " + (r.R-r.L) + "x" + (r.B-r.T)
        + " ex=0x" + GetWindowLongPtr(h, -20).ToString("X"));
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@
$out += "pid: $($p.Id)"
$out += "windows:"
$out += [WEnum]::ForPid([uint32]$p.Id)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
$g.Dispose()
# crop the settings window for a close-up
$cw = 646; $ch = 671; $cx = -1283; $cy = 180
if ($bmp.Width -gt 0) {
  $cx = [Math]::Max(0, $cx - $b.Left); $cy = [Math]::Max(0, $cy - $b.Top)
  $cw = [Math]::Min($cw, $b.Width - $cx); $ch = [Math]::Min($ch, $b.Height - $cy)
  $crop = New-Object System.Drawing.Bitmap $cw, $ch
  $gc = [System.Drawing.Graphics]::FromImage($crop)
  $gc.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0,0,$cw,$ch), (New-Object System.Drawing.Rectangle $cx,$cy,$cw,$ch), [System.Drawing.GraphicsUnit]::Pixel)
  $gc.Dispose()
  $crop.Save((Join-Path $proj 'ui-crop.png'), [System.Drawing.Imaging.ImageFormat]::Png)
  $crop.Dispose()
}
$bmp.Dispose()

$p.Refresh()
$out += "alive: $($p.HasExited -eq $false)  ws: $([math]::Round($p.WorkingSet64/1MB,1)) MB"
$out += "--- log ---"
$out += (Get-Content "$proj\frameglass.log")
$backup | Set-Content $cfgFile
$out -join "`n" | Set-Content "$proj\ui-check.txt"



