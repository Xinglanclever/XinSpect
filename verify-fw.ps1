$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Runtime.InteropServices
$src = @"
using System; using System.Runtime.InteropServices; using System.Drawing; using System.Drawing.Imaging;
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
Add-Type -TypeDefinition $src -ReferencedAssemblies @('System.Drawing','System.Runtime.InteropServices')
$p = Get-Process XinSpect | Where-Object { $_.MainWindowTitle } | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Output 'NO WINDOW'; exit 1 }
$name = '韌體安全'
$nameCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
$item = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
if (-not $item) { Write-Output 'NAV FAILED'; exit 1 }
try { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
catch { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
Start-Sleep -Seconds 5
$texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsTextPatternAvailableProperty, $true)))
$out = @()
foreach ($t in $texts) { $v = $t.Current.Name; if ($v -and $v.Length -gt 1) { $out += $v } }
$out | Out-File -FilePath 'C:\Users\Administrator\XinSpect\verify-shots\fw-text.txt' -Encoding UTF8
[Cap2]::Shot($win.Current.NativeWindowHandle, 'C:\Users\Administrator\XinSpect\verify-shots\fw-page.png')
Write-Output "DONE: $($out.Count) text elements"
