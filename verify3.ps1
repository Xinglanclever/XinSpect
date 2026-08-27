# Functional smoke test: actually run SuperPI and the stress test via UI Automation,
# to validate the worker-thread + Progress-marshaling + UI-update path at runtime.
# ASCII-only comments per project convention.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$src = @"
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public class Cap3 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static void Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T;
    if (w < 1 || ht < 1) return;
    using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc);
      bmp.Save(path, ImageFormat.Png);
    }
  }
}
"@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing

$exe = "C:\Users\Administrator\XinSpect\bin\Release\net10.0-windows\win-x64\XinSpect.exe"
$outDir = "C:\Users\Administrator\XinSpect\shots"
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
$hwnd = [IntPtr]::Zero
for ($i=0; $i -lt 30; $i++) { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }; Start-Sleep -Milliseconds 500 }
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "NO_WINDOW"; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

function Select-Nav([int]$idx) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  $it = $items.Item($idx)
  $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 1200
}
function Invoke-ByName([string]$name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $b = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
  if ($b -eq $null) { Write-Output ("BTN_NOT_FOUND: " + $name); return $false }
  $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Output ("INVOKED: " + $name)
  return $true
}
function Set-Scroll([double]$pct) {
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)))
  $best=$null; $bl=-1
  for ($i=0; $i -lt $all.Count; $i++) { $e=$all.Item($i); try { $sp=$e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); if(-not $sp.Current.VerticallyScrollable){continue}; $rc=$e.Current.BoundingRectangle; if($rc.X -gt $bl){$bl=$rc.X;$best=$e} } catch {} }
  if ($best -ne $null) { $best.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct); Start-Sleep -Milliseconds 700 }
}

Select-Nav 9

# diagnostic: list every button Name on the bench page
$bcond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bcond)
Write-Output ("BUTTONS=" + $btns.Count)
for ($i=0; $i -lt $btns.Count; $i++) { Write-Output ("BTN[" + $i + "]='" + $btns.Item($i).Current.Name + "'") }

# ---- SuperPI: pick 1-wan digits (fast), run, wait for completion ----
Set-Scroll 80
Invoke-ByName "1 萬位"
Start-Sleep -Milliseconds 400
Invoke-ByName "開始計算"
Start-Sleep -Seconds 8
Set-Scroll 80
[Cap3]::Shot($hwnd, (Join-Path $outDir "func_superpi.png"))
Write-Output "SHOT func_superpi"

# ---- Stress test: start, observe a few seconds of live sampling, then stop ----
Set-Scroll 30
Invoke-ByName "開始烤機"
Start-Sleep -Seconds 5
Set-Scroll 30
[Cap3]::Shot($hwnd, (Join-Path $outDir "func_stress.png"))
Write-Output "SHOT func_stress"

Start-Sleep -Seconds 1
$p.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (!$p.HasExited) { $p.Kill() }
Write-Output "DONE"
