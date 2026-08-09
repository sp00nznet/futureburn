<#
  capture-screenshots.ps1 — regenerate the README/docs screenshots.

  Launches the built WPF GUI, drives it with UI Automation (invokes each main-
  window tile), and captures each window to docs/screenshots/*.png via
  PrintWindow (PW_RENDERFULLCONTENT, so it captures the real DWM-composited
  content, not a black rectangle). Child-window detection uses a Win32 top-
  level-window enumeration filtered by the GUI's process id.

  Usage:
    powershell -ExecutionPolicy Bypass -File scripts/capture-screenshots.ps1

  Requires a desktop session (it shows real windows). Build the GUI first:
    dotnet build src/Futureburn.Gui/Futureburn.Gui.csproj -c Debug
#>
param(
  [string]$Exe    = "src/Futureburn.Gui/bin/Debug/net8.0-windows/Futureburn.Gui.exe",
  [string]$OutDir = "docs/screenshots"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public class Cap {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  // PER_MONITOR_AWARE_V2 = -4. Must run before any window/DC work, else a
  // scaled (e.g. 150%) display makes PrintWindow crop to the top-left of the
  // physically-larger window.
  public static void DpiAware() { try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { } }
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  public struct RECT { public int L, T, R, B; }

  public static List<IntPtr> TopWindows(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, p) => {
      uint wp; GetWindowThreadProcessId(h, out wp);
      if (wp == pid && IsWindowVisible(h) && GetWindowTextLength(h) > 0) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }

  public static void Shot(IntPtr h, string path) {
    RECT r;
    if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0)  // extended frame bounds
      GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T;
    if (w <= 0 || ht <= 0) { GetWindowRect(h, out r); w = r.R - r.L; ht = r.B - r.T; }
    using (var bmp = new Bitmap(w, ht))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr hdc = g.GetHdc();
      PrintWindow(h, hdc, 2);   // PW_RENDERFULLCONTENT
      g.ReleaseHdc(hdc);
      bmp.Save(path, ImageFormat.Png);
    }
  }
}
"@

$A   = [System.Windows.Automation.AutomationElement]
$TS  = [System.Windows.Automation.TreeScope]
$CTP = [System.Windows.Automation.AutomationElement]::ControlTypeProperty
$CT  = [System.Windows.Automation.ControlType]
$InvokePattern = [System.Windows.Automation.InvokePattern]

[Cap]::DpiAware()   # capture at physical resolution on scaled displays

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$OutDir  = (Resolve-Path -LiteralPath $OutDir).Path
$exePath = (Resolve-Path -LiteralPath $Exe).Path

Write-Host "Launching $exePath"
$proc = Start-Process $exePath -PassThru
$appPid  = [uint32]$proc.Id
Start-Sleep -Seconds 3

function Save-Window([IntPtr]$h, [string]$name) {
  [void][Cap]::SetForegroundWindow($h)
  Start-Sleep -Milliseconds 600
  [Cap]::Shot($h, (Join-Path $OutDir $name))
  Write-Host "  saved $name"
}

# Main window: the app's first visible titled top-level window.
$mainH = [Cap]::TopWindows($appPid) | Select-Object -First 1
if (-not $mainH) { throw "main window not found" }
Save-Window $mainH 'main-window.png'

# Main AutomationElement (for invoking tile buttons).
$root    = $A::RootElement
$appPidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
$mainEl  = $root.FindFirst($TS::Children, $appPidCond)
$btnCond = New-Object System.Windows.Automation.PropertyCondition($CTP, $CT::Button)

# (tile AutomationProperties.Name prefix, output file)
$tiles = @(
  @('Burn Audio CD', 'burn-audio-cd.png'),
  @('Burn Blu-ray',  'burn-dvd.png'),
  @('CD Info',       'cd-info.png'),
  @('Burn Label',    'burn-lightscribe.png'),
  @('Image Tools',   'image-tools.png')
)

foreach ($t in $tiles) {
  $title = $t[0]; $file = $t[1]
  $btn = $null
  foreach ($b in $mainEl.FindAll($TS::Descendants, $btnCond)) {
    if ($b.Current.Name -like "$title*") { $btn = $b; break }
  }
  if (-not $btn) { Write-Warning "tile '$title' not found"; continue }

  $before = [Cap]::TopWindows($appPid)
  $btn.GetCurrentPattern($InvokePattern::Pattern).Invoke()
  Start-Sleep -Milliseconds 1800   # window opens + enumerates drives

  $after = [Cap]::TopWindows($appPid)
  $new = $after | Where-Object { $before -notcontains $_ }
  if (-not $new) { Write-Warning "no window opened for '$title'"; continue }
  $child = @($new)[0]

  Save-Window $child $file
  [void][Cap]::PostMessage($child, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
  Start-Sleep -Milliseconds 500
}

try { $proc.CloseMainWindow() | Out-Null; Start-Sleep 1; if (-not $proc.HasExited) { $proc.Kill() } } catch { }
Write-Host "Done. Screenshots in $OutDir"
