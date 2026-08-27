# Scrolled captures of the 6 info pages enriched with industrial-grade params.
# ASCII-only comments per project convention. Targets the published single-file exe.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$src = @"
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public class CapP {
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

$exe = "C:\Users\Administrator\XinSpect\publish\XinSpect.exe"
$outDir = "C:\Users\Administrator\XinSpect\shots"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8
$hwnd = [IntPtr]::Zero
for ($i=0; $i -lt 30; $i++) {
  $p.Refresh()
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
  Start-Sleep -Milliseconds 500
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "NO_WINDOW"; exit 1 }
Write-Output ("HWND=" + $hwnd)
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

function Select-Nav([int]$idx) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  if ($idx -ge $items.Count) { Write-Output ("NAV_OOB " + $idx + "/" + $items.Count); return }
  $it = $items.Item($idx)
  $sip = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $sip.Select()
  Start-Sleep -Milliseconds 1300
}

function Get-ContentScroller {
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)))
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
    $path = Join-Path $outDir ("param_{0}_{1}.png" -f $label, [int]$pct)
    [CapP]::Shot($hwnd, $path)
    Write-Output ("SHOT " + $path)
  }
}

$pages = @{ 1 = "cpu"; 2 = "memory"; 3 = "board"; 4 = "gpu"; 5 = "storage"; 6 = "network" }
foreach ($idx in 1..6) {
  Select-Nav $idx
  Capture-Scrolled $pages[$idx] @(0, 50, 100)
}

Start-Sleep -Seconds 1
$p.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (!$p.HasExited) { $p.Kill() }
Write-Output "DONE"
