Add-Type @"
using System; using System.Runtime.InteropServices;
public class WC {
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
$p = Get-Process XinSpect | Where-Object { $_.MainWindowTitle } | Select-Object -First 1
# WM_SYSCOMMAND SC_CLOSE（0x0602）
[WC]::SendMessage($p.MainWindowHandle, 0x0112, [IntPtr]0x0602, [IntPtr]::Zero) | Out-Null
Start-Sleep -Seconds 4
if (Get-Process XinSpect -ErrorAction SilentlyContinue) { Write-Output 'STILL RUNNING' } else { Write-Output 'CLOSED' }
