$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$src = @"
using System; using System.Runtime.InteropServices; using System.Drawing; using System.Drawing.Imaging;
public class Cap3 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static void Shot(IntPtr h, string path) {
    SetWindowPos(h, (IntPtr)(-1), 60, 60, 1220, 820, 0x0040);
    System.Threading.Thread.Sleep(1200);
    RECT r; GetWindowRect(h, out r);
    using (var bmp = new Bitmap(r.R-r.L, r.B-r.T, PixelFormat.Format32bppArgb))
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
[Cap3]::Shot($win.Current.NativeWindowHandle, 'C:\Users\Administrator\XinSpect\verify-shots\fw-page.png')
$texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$out = @()
foreach ($t in $texts) { $v = $t.Current.Name; if ($v -and $v.Length -gt 1) { $out += $v } }
$out | Out-File -FilePath 'C:\Users\Administrator\XinSpect\verify-shots\fw-text.txt' -Encoding UTF8
Write-Output "DONE: $($out.Count)"
