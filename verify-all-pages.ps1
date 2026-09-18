# Automated page-by-page verification script
# Navigates through critical pages, captures screenshots and UI text
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$src = @"
using System; using System.Runtime.InteropServices; using System.Drawing; using System.Drawing.Imaging;
public class Cap {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static void Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T;
    using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc);
      bmp.Save(path, ImageFormat.Png);
    }
  }
}
"@
if (-not ('Cap' -as [type])) { Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing }

$p = Get-Process XinSpect | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
    [System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Output 'NO WINDOW'; exit 1 }

$outDir = 'C:\Users\Administrator\XinSpect\verify-shots'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# Helper: click a nav item by name (hex-encoded to avoid encoding issues)
function Navigate-To([string]$pageName) {
    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $pageName)
    $item = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
    if ($item) {
        try {
            $inv = $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $inv.Invoke()
        } catch {
            try {
                $sel = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $sel.Select()
            } catch { return $false }
        }
        return $true
    }
    return $false
}

# Helper: dump all text elements on the current page
function Dump-Text([string]$label) {
    Start-Sleep -Seconds 3
    $txtCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtCond)
    $out = New-Object System.Collections.ArrayList
    [void]$out.Add("=== $label (texts: $($all.Count)) ===")
    foreach ($e in $all) {
        $n = $e.Current.Name
        if ($n) { [void]$out.Add($n) }
    }
    return $out
}

# Helper: take screenshot
function Take-Shot([string]$name) {
    [Cap]::Shot($win.Current.NativeWindowHandle, "$outDir\$name.png")
}

# Pages to verify (using hex-escaped Chinese to avoid encoding issues)
$pages = @(
    @{ name = [string]::Concat([char]0x7E3D,[char]0x89BD); label = 'overview' },
    @{ name = [string]::Concat([char]0x8A18,[char]0x61B6,[char]0x9AD4); label = 'memory' },
    @{ name = [string]::Concat([char]0x611F,[char]0x6E2C,[char]0x5668); label = 'sensors' },
    @{ name = [string]::Concat('PCIe ',[char]0x5206,[char]0x6790); label = 'pcie-analysis' },
    @{ name = [string]::Concat([char]0x6676,[char]0x7247,[char]0x7D44,[char]0x5206,[char]0x6790); label = 'chipset-analysis' },
    @{ name = [string]::Concat([char]0x7E6A,[char]0x5716,[char]0x7BA1,[char]0x7DDA,[char]0x6E2C,[char]0x8A66); label = 'render-test' },
    @{ name = [string]::Concat([char]0x8A2D,[char]0x5B9A); label = 'settings' },
    @{ name = [string]::Concat([char]0x95DC,[char]0x65BC); label = 'about' }
)

$allText = New-Object System.Collections.ArrayList

foreach ($page in $pages) {
    $ok = Navigate-To $page.name
    if ($ok) {
        $texts = Dump-Text $page.label
        foreach ($t in $texts) { [void]$allText.Add($t) }
        Take-Shot $page.label
        Write-Output "DONE: $($page.label)"
    } else {
        [void]$allText.Add("=== $($page.label): NAVIGATION FAILED ===")
        Write-Output "FAIL: $($page.label)"
    }
}

$allText | Out-File -FilePath "$outDir\all-text.txt" -Encoding UTF8
Write-Output "ALL DONE. Results in $outDir"
