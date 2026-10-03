$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$src = @"
using System; using System.Runtime.InteropServices; using System.Drawing; using System.Drawing.Imaging;
public class Cap4 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static void ShotScreen(IntPtr h, string path) {
    SetWindowPos(h, (IntPtr)(-1), 60, 60, 1220, 820, 0x0040);
    System.Threading.Thread.Sleep(1500);
    RECT r; GetWindowRect(h, out r);
    using (var bmp = new Bitmap(r.R-r.L, r.B-r.T, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      g.CopyFromScreen(r.L, r.T, 0, 0, new System.Drawing.Size(r.R-r.L, r.B-r.T));
      bmp.Save(path, ImageFormat.Png);
    }
  }
}
"@
Add-Type -TypeDefinition $src -ReferencedAssemblies @('System.Drawing','System.Runtime.InteropServices')
$p = Get-Process XinSpect | Where-Object { $_.MainWindowTitle } | Select-Object -First 1
[Cap4]::ShotScreen($p.MainWindowHandle, 'C:\Users\Administrator\XinSpect\verify-shots\fw-page.png')
Write-Output 'DONE'
