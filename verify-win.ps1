Add-Type @"
using System; using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
$p = Get-Process XinSpect | Where-Object { $_.MainWindowTitle } | Select-Object -First 1
$h = $p.MainWindowHandle
Write-Output "handle=$h iconic=$([W]::IsIconic($h))"
[W]::ShowWindow($h, 9) | Out-Null
Start-Sleep -Seconds 2
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Seconds 1
$r = New-Object W+RECT
[W]::GetWindowRect($h, [ref]$r) | Out-Null
Write-Output "rect: $($r.L),$($r.T) - $($r.R),$($r.B)"
