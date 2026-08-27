$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$src = @"
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public class Cap {
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
      PrintWindow(h, dc, 2); // PW_RENDERFULLCONTENT
      g.ReleaseHdc(dc);
      bmp.Save(path, ImageFormat.Png);
    }
  }
  // For layered/transparent windows PrintWindow yields blank output, so grab the screen region instead.
  public static void ShotScreen(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T;
    if (w < 1 || ht < 1) return;
    using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, ht));
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
Start-Sleep -Seconds 6   # allow WMI static load + sensor init

# wait for main window handle
$hwnd = [IntPtr]::Zero
for ($i=0; $i -lt 30; $i++) {
  $p.Refresh()
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
  Start-Sleep -Milliseconds 500
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "NO_WINDOW"; exit 1 }
Write-Output ("HWND=" + $hwnd)

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

# find all ListItem elements (the nav rail)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Output ("NAV_ITEMS=" + $items.Count)

$names = @("overview","cpu","memory","motherboard","gpu","storage","network","processes","sensors","bench","health","toolbox","about")
for ($i=0; $i -lt $items.Count; $i++) {
  $it = $items.Item($i)
  try {
    $sip = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sip.Select()
  } catch { Write-Output ("SELECT_FAIL_" + $i) }
  Start-Sleep -Milliseconds 1100   # page transition + at least one 1s data tick
  $label = if ($i -lt $names.Count) { $names[$i] } else { "tab$i" }
  $path = Join-Path $outDir ("{0}_{1}.png" -f $i, $label)
  [Cap]::Shot($hwnd, $path)
  Write-Output ("SHOT " + $path)
}

# capture mini floating overlay (invoke MiniButton by AutomationId, then screen-grab the 2nd top-level window)
try {
  $idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "MiniButton")
  $btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
  if ($btn -ne $null) {
    $inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $inv.Invoke()
    Start-Sleep -Milliseconds 1600
    $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
    $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
    $shot = $false
    for ($k=0; $k -lt $wins.Count; $k++) {
      $w = $wins.Item($k)
      $wh = [IntPtr]$w.Current.NativeWindowHandle
      if ($wh -ne $hwnd -and $wh -ne [IntPtr]::Zero) {
        [Cap]::ShotScreen($wh, (Join-Path $outDir "12_mini.png"))
        Write-Output "SHOT mini"
        $shot = $true
      }
    }
    if (-not $shot) { Write-Output "NO_MINI_WINDOW" }
  } else { Write-Output "NO_MINI_BUTTON" }
} catch { Write-Output ("MINI_FAIL " + $_.Exception.Message) }

Start-Sleep -Seconds 1
$p.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (!$p.HasExited) { $p.Kill() }
Write-Output "DONE"
