# REAL write test, deliberately only in the SAFE direction, and self-restoring:
#   1) temp target 84 -> 83 -> back to 84   (lower = cooler = safer; driver clamps to 65..93)
#   2) core freq delta 0 -> -50 MHz -> back to 0  (a DOWNCLOCK, never an overclock)
# The point is to prove the write actually lands (readback changes), because rc=0 on a
# same-value write proves nothing. Every step reads back and the original value is restored.
# ASCII-only comments per project convention.
$ErrorActionPreference = 'Stop'

$src = @"
using System;
using System.Runtime.InteropServices;
public static class Ap {
  // ---- NVML ----
  [DllImport("nvml.dll", EntryPoint="nvmlInit_v2")] public static extern int NvmlInit();
  [DllImport("nvml.dll", EntryPoint="nvmlShutdown")] public static extern int NvmlShutdown();
  [DllImport("nvml.dll", EntryPoint="nvmlDeviceGetHandleByIndex_v2")] public static extern int NvmlDev(uint i, out IntPtr d);
  [DllImport("nvml.dll", EntryPoint="nvmlDeviceGetTemperatureThreshold")] public static extern int GetThresh(IntPtr d, uint w, out uint v);
  [DllImport("nvml.dll", EntryPoint="nvmlDeviceSetTemperatureThreshold")] public static extern int SetThresh(IntPtr d, uint w, ref int v);
  // ---- NVAPI ----
  [DllImport("nvapi64.dll", EntryPoint="nvapi_QueryInterface", CallingConvention=CallingConvention.Cdecl)]
  public static extern IntPtr QI(uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Fn0();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int FnEnum([Out] IntPtr[] h, out int c);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int FnBuf(IntPtr gpu, IntPtr buf);
  static IntPtr gpu = IntPtr.Zero;
  public static string NvapiInit() {
    var init = (Fn0)Marshal.GetDelegateForFunctionPointer(QI(0x0150E828), typeof(Fn0));
    int rc = init(); if (rc != 0) return "INIT_RC=" + rc;
    var en = (FnEnum)Marshal.GetDelegateForFunctionPointer(QI(0xE5AC921F), typeof(FnEnum));
    var h = new IntPtr[64]; int n = 0;
    rc = en(h, out n); if (rc != 0) return "ENUM_RC=" + rc;
    gpu = h[0]; return "OK";
  }
  public static int Get(uint id, byte[] buf, int ver) {
    IntPtr fp = QI(id); if (fp == IntPtr.Zero) return -9999;
    var fn = (FnBuf)Marshal.GetDelegateForFunctionPointer(fp, typeof(FnBuf));
    IntPtr p = Marshal.AllocHGlobal(buf.Length);
    try {
      for (int i = 0; i < buf.Length; i++) Marshal.WriteByte(p, i, 0);
      Marshal.WriteInt32(p, 0, (int)((uint)buf.Length | ((uint)ver << 16)));
      int rc = fn(gpu, p); Marshal.Copy(p, buf, 0, buf.Length); return rc;
    } finally { Marshal.FreeHGlobal(p); }
  }
  public static int Set(uint id, byte[] buf) {
    IntPtr fp = QI(id); if (fp == IntPtr.Zero) return -9999;
    var fn = (FnBuf)Marshal.GetDelegateForFunctionPointer(fp, typeof(FnBuf));
    IntPtr p = Marshal.AllocHGlobal(buf.Length);
    try { Marshal.Copy(buf, 0, p, buf.Length); return fn(gpu, p); }
    finally { Marshal.FreeHGlobal(p); }
  }
}
"@
Add-Type -TypeDefinition $src

function U([long]$v) { return [uint32]($v -band 4294967295) }
$GET_PS = U 0x6FF81213
$SET_PS = U 0x0F4DAE6B
$PS = 7416

# ---------------- 1) NVML temperature target ----------------
Write-Output ("NvmlInit rc=" + [Ap]::NvmlInit())
$dev = [IntPtr]::Zero
[Ap]::NvmlDev(0, [ref]$dev) | Out-Null
$orig = 0
[Ap]::GetThresh($dev, 5, [ref]$orig) | Out-Null
Write-Output ("ACOUSTIC_CURR before = $orig C")

$want = [int]$orig - 1
$v = $want
$rc = [Ap]::SetThresh($dev, 5, [ref]$v)
$after = 0
[Ap]::GetThresh($dev, 5, [ref]$after) | Out-Null
Write-Output ("SET -> $want  rc=$rc  driverReturned=$v  readback=$after C   CHANGED=" + ($after -ne $orig))

$v2 = [int]$orig
$rc2 = [Ap]::SetThresh($dev, 5, [ref]$v2)
$back = 0
[Ap]::GetThresh($dev, 5, [ref]$back) | Out-Null
Write-Output ("RESTORE -> $orig  rc=$rc2  readback=$back C   RESTORED=" + ($back -eq $orig))

# ---------------- 2) NVAPI core freq delta (downclock only) ----------------
# Build the MINIMAL Pstates20 struct that the driver accepts for Set (the full 7416-byte
# read-back struct returns -104 NOT_SUPPORTED on this driver).
function Set-CoreDelta([int]$kHz) {
  $g = New-Object byte[] $PS
  if (0 -ne [Ap]::Get($GET_PS, $g, 2)) { return -1 }
  # locate P0 + domain 0 to copy the driver's own typeId and clamp to its range
  $typ = 1; $mn = 0; $mx = 0
  $numC = [BitConverter]::ToUInt32($g, 12)
  for ($j = 0; $j -lt $numC -and $j -lt 8; $j++) {
    $cb = 20 + 8 + $j * 44
    if ([BitConverter]::ToUInt32($g, $cb) -eq 0) {
      $typ = [BitConverter]::ToUInt32($g, $cb + 4)
      $mn = [BitConverter]::ToInt32($g, $cb + 16)
      $mx = [BitConverter]::ToInt32($g, $cb + 20)
      break
    }
  }
  if ($mx -ne 0 -or $mn -ne 0) { $kHz = [Math]::Max($mn, [Math]::Min($mx, $kHz)) }
  $s = New-Object byte[] $PS
  [BitConverter]::GetBytes([uint32]($PS -bor (2 -shl 16))).CopyTo($s, 0)
  [BitConverter]::GetBytes([uint32]1).CopyTo($s, 8)     # numPstates = 1
  [BitConverter]::GetBytes([uint32]1).CopyTo($s, 12)    # numClocks  = 1
  [BitConverter]::GetBytes([uint32]0).CopyTo($s, 20)    # pstateId = P0
  [BitConverter]::GetBytes([uint32]0).CopyTo($s, 28)    # domainId = GRAPHICS
  [BitConverter]::GetBytes([uint32]$typ).CopyTo($s, 32) # typeId from driver
  [BitConverter]::GetBytes([int32]$kHz).CopyTo($s, 40)  # freqDelta.value
  return [Ap]::Set($SET_PS, $s)
}
function Get-CoreDelta {
  $g = New-Object byte[] $PS
  if (0 -ne [Ap]::Get($GET_PS, $g, 2)) { return 999999 }
  return [BitConverter]::ToInt32($g, 20 + 8 + 12)
}

Write-Output ("NvapiInit: " + [Ap]::NvapiInit())
$d0 = Get-CoreDelta
Write-Output ("core delta before = $d0 kHz")
$rc = Set-CoreDelta -50000
$d1 = Get-CoreDelta
Write-Output ("SET -> -50000 kHz (a DOWNCLOCK) rc=$rc readback=$d1 kHz  CHANGED=" + ($d1 -ne $d0))
$rc = Set-CoreDelta 0
$d2 = Get-CoreDelta
Write-Output ("RESTORE -> 0 rc=$rc readback=$d2 kHz  RESTORED=" + ($d2 -eq 0))

[Ap]::NvmlShutdown() | Out-Null
Write-Output "APPLY_TEST_DONE"
