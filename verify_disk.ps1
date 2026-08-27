# Targeted disk-test verifier: invoke the disk card's Start button by AutomationId
# (x:Name DiskBenchStart) to avoid the ambiguous "Start test" label shared with the cache card.
# ASCII-only comments; save as UTF-8 with BOM for the Chinese nav handling.
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
public class CapD {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static void Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T; if (w < 1 || ht < 1) return;
    using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); bmp.Save(path, ImageFormat.Png); }
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

$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$items.Item(9).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 1200

function Invoke-ById([string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  $b = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
  if ($b -eq $null) { Write-Output ("ID_NOT_FOUND: " + $id); return $false }
  $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Output ("INVOKED_ID: " + $id + " (Name='" + $b.Current.Name + "')")
  return $true
}
function Set-Scroll([double]$pct) {
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)))
  $best=$null; $bl=-1
  for ($i=0; $i -lt $all.Count; $i++) { $e=$all.Item($i); try { $sp=$e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); if(-not $sp.Current.VerticallyScrollable){continue}; $rc=$e.Current.BoundingRectangle; if($rc.X -gt $bl){$bl=$rc.X;$best=$e} } catch {} }
  if ($best -ne $null) { $best.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct); Start-Sleep -Milliseconds 700 }
}

Set-Scroll 100
Invoke-ById "DiskBenchStart"
# 512 MB seq write+read + 2x3s random; allow generous time then screenshot mid/late run and final
Start-Sleep -Seconds 12
Set-Scroll 100
[CapD]::Shot($hwnd, (Join-Path $outDir "func_disk_run.png"))
Write-Output "SHOT func_disk_run"
Start-Sleep -Seconds 14
Set-Scroll 100
[CapD]::Shot($hwnd, (Join-Path $outDir "func_disk_done.png"))
Write-Output "SHOT func_disk_done"

Start-Sleep -Seconds 1
$p.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (!$p.HasExited) { $p.Kill() }
Write-Output "DONE"
