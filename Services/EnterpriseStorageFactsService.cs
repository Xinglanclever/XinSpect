using System.Management;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// WP24 企業儲存偵測：iSCSI／MPIO／FC／NVMe-oF 的三態分離（usermode）。
/// 與 <see cref="OobFactsService"/> 同一套哲學——「本機沒有這種儲存」是 **NotApplicable**、
/// 「偵測路徑不存在」是 **NotSupported**、兩者都不是錯誤。iSCSI／MPIO 看服務狀態（SCM），
/// FC 看 WMI root\wmi 的 FC HBA 類別；NVMe-oF 沒有公開的 usermode 偵測路徑——如實標，不猜。
/// </summary>
public static class EnterpriseStorageFactsService
{
    private const string Category = "週邊匯流排";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        bool? iscsiRunning = null, bool? mpioServiceInstalled = null, int? fcAdapters = null)
    {
        iscsiRunning ??= ProbeServiceRunning("MSiSCSI");
        mpioServiceInstalled ??= ProbeServiceExists("Mpio");
        fcAdapters ??= ProbeFcAdapters();

        return
        [
            new HardwareFact("ent.iscsi", Category, "iSCSI 發起器",
                iscsiRunning is { } running ? (running ? "存在，服務執行中" : "存在，服務已安裝但未執行") : "", "",
                "服務控制管理員（MSiSCSI 服務）", FactTrustLevel.Reported, false, at, null,
                iscsiRunning is not null ? FactAvailability.Present : FactAvailability.ReadError,
                iscsiRunning is not null ? null : "MSiSCSI 服務狀態查詢失敗"),
            new HardwareFact("ent.mpio", Category, "MPIO（多重路徑 I/O）", "", "",
                "服務控制管理員（Mpio 服務）", FactTrustLevel.Reported, false, at, null,
                mpioServiceInstalled is true ? FactAvailability.Present : FactAvailability.NotSupported,
                mpioServiceInstalled is true ? null : "Mpio 服務未安裝：此機器未設定多重路徑儲存"),
            new HardwareFact("ent.fc", Category, "光纖通道（FC）HBA", "", "",
                "WMI root\\wmi MSFC_FCAdapterHBAAttributes", FactTrustLevel.Reported, false, at, null,
                fcAdapters > 0 ? FactAvailability.Present : FactAvailability.NotApplicable,
                fcAdapters > 0 ? null : "沒有 FC HBA 介面卡：此機器沒有光纖通道——無此硬體不是錯誤"),
            new HardwareFact("ent.nvmeof", Category, "NVMe-oF", "", "",
                "（偵測路徑聲明）", FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "偵測路徑未實作：NVMe-oF 沒有公開的 usermode 偵測 API——讀不到就是讀不到，不猜"),
        ];
    }

    // ── SCM（advapi32）——與 BackendEnvironmentService 同一套 P/Invoke 口徑 ──

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 0x0004;
    private const int ErrorServiceDoesNotExist = 1060;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out SvcStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SvcStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    private static bool? ProbeServiceRunning(string name)
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            IntPtr svc = OpenServiceW(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return null;
            try
            {
                return QueryServiceStatus(svc, out var s) && s.CurrentState == ServiceRunning;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static bool? ProbeServiceExists(string name)
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            IntPtr svc = OpenServiceW(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist ? false : null;
            CloseServiceHandle(svc);
            return true;
        }
        finally { CloseServiceHandle(scm); }
    }

    private static int? ProbeFcAdapters()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT InstanceName FROM MSFC_FCAdapterHBAAttributes");
            int count = 0;
            foreach (var _ in searcher.Get()) count++;
            return count;
        }
        catch { return null; }
    }
}
