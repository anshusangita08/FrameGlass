# Creates or refreshes the Start Menu shortcut for FrameGlass.
#
# There is no installer, and this is the piece of one that matters: without a Start Menu entry the
# app has no icon in the Start menu or in the app list, whatever the executable carries.
#
# The shortcut points straight at the executable, so Windows takes its icon from the same embedded
# image the tray and the taskbar use - there is no second copy to keep in step. The executable
# requests elevation in its manifest, so launching from here raises the usual UAC prompt; the
# shortcut does not need an "Run as administrator" flag of its own.
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\install-shortcut.ps1 [-ExePath <path>]

param(
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) { $ExePath = Join-Path $root 'FrameGlass.exe' }

if (-not (Test-Path $ExePath)) {
    Write-Error "FrameGlass.exe not found at $ExePath. Publish first (dotnet publish -c Release -o dist), or pass -ExePath."
}
$ExePath = (Resolve-Path $ExePath).Path

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$link = Join-Path $startMenu 'FrameGlass.lnk'

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($link)
$sc.TargetPath = $ExePath
$sc.WorkingDirectory = Split-Path -Parent $ExePath
$sc.Description = 'Frametime, temperature and load overlay for Windows games'
$sc.IconLocation = "$ExePath,0"
$sc.Save()

Write-Output "shortcut: $link"
Write-Output "target  : $ExePath"
Write-Output ""
Write-Output "The icon comes from the executable's embedded app.ico, which is the same image the tray"
Write-Output "and the taskbar use. Re-run this after moving the app to point the shortcut at its new home."
