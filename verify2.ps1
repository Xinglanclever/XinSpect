# Scrolled captures of the long pages (bench + toolbox) so below-the-fold cards are visible.
# ASCII-only comments per project convention.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$src = @"
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public class Cap2 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static void Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T;
    if (w < 1 || ht < 1) return;
    using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc();
      PrintWindow(h, dc, 2);
      g.ReleaseHdc(dc);
      bmp.Save(path, ImageFormat.Png);
    }
  }
}
"@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing

$exe = "C:\Users\Administrator\XinSpect\bin\Release\net10.0-windows\win-x64\XinSpect.exe"
$outDir = "C:\Users\Administrator\XinSpect\shots"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
$hwnd = [IntPtr]::Zero
for ($i=0; $i -lt 30; $i++) {
  $p.Refresh()
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
  Start-Sleep -Milliseconds 500
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "NO_WINDOW"; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

# select a nav ListItem by zero-based index
function Select-Nav([int]$idx) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  if ($idx -ge $items.Count) { Write-Output ("NAV_OOB " + $idx + "/" + $items.Count); return }
  $it = $items.Item($idx)
  $sip = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $sip.Select()
  Start-Sleep -Milliseconds 1200
}

# find the content-area ScrollViewer (right of the nav rail) that is vertically scrollable
function Get-ContentScroller {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Pane)
  # ScrollViewer surfaces as a ScrollPattern provider; enumerate all and pick the widest one on the right
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.OrCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)))))
  $best = $null; $bestLeft = -1
  for ($i=0; $i -lt $all.Count; $i++) {
    $e = $all.Item($i)
    try {
      $sp = $e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
      if (-not $sp.Current.VerticallyScrollable) { continue }
      $rc = $e.Current.BoundingRectangle
      if ($rc.X -gt $bestLeft) { $bestLeft = $rc.X; $best = $e }
    } catch { }
  }
  return $best
}

function Capture-Scrolled([string]$label, [double[]]$pcts) {
  $sv = Get-ContentScroller
  if ($sv -eq $null) { Write-Output ("NO_SCROLLER_" + $label); return }
  $sp = $sv.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
  foreach ($pct in $pcts) {
    try { $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct) } catch { }
    Start-Sleep -Milliseconds 900
    $path = Join-Path $outDir ("scroll_{0}_{1}.png" -f $label, [int]$pct)
    [Cap2]::Shot($hwnd, $path)
    Write-Output ("SHOT " + $path)
  }
}

# bench page = index 9 (long: 7 cards)
Select-Nav 9
Capture-Scrolled "bench" @(0, 30, 55, 80, 100)

# toolbox page = index 11
Select-Nav 11
Capture-Scrolled "toolbox" @(0, 50, 100)

Start-Sleep -Seconds 1
$p.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (!$p.HasExited) { $p.Kill() }
Write-Output "DONE"
