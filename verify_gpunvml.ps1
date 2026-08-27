# Screenshots of the GPU overclock page (nav index 11) at three scroll positions.
# Read-only: it only selects the page and captures the window; no Apply button is clicked.
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
public class CapG {
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

$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Output ("NAV_COUNT=" + $items.Count)
$idx = 4
if ($idx -ge $items.Count) { Write-Output "NAV_OOB"; $p.Kill(); exit 1 }
$sip = $items.Item($idx).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$sip.Select()
Start-Sleep -Seconds 3

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
if ($best -eq $null) { Write-Output "NO_SCROLLER" } else {
  $sp = $best.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
  foreach ($pct in 0, 50, 100) {
    try { $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct) } catch { }
    Start-Sleep -Milliseconds 900
    $path = Join-Path $outDir ("gpunvml_{0}.png" -f [int]$pct)
    [CapG]::Shot($hwnd, $path)
    Write-Output ("SHOT " + $path)
  }
}

# Dump every text element on the page so numeric readbacks can be checked without OCR.
$tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$texts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)
Write-Output "--- TEXTS ---"
for ($i=0; $i -lt $texts.Count; $i++) {
  $n = $texts.Item($i).Current.Name
  if ($n -and $n.Trim().Length -gt 0) { Write-Output $n }
}

Start-Sleep -Seconds 1
$p.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (!$p.HasExited) { $p.Kill() }
Write-Output "DONE"
