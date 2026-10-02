using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>後端與環境事實的輸入集合：全部由呼叫方探測後餵入，讓事實生成本體保持純函式可單測。</summary>
public sealed record BackendEnvironmentInputs(
    bool MsrAvailable, string? MsrBackendName, string? MsrUnavailableReason,
    bool MmioAvailable, string? MmioBackendName, string? MmioUnavailableReason,
    uint? CodeIntegrityOptions, int? SecureBootEnabled, string? PawnIoStatus);

/// <summary>
/// 後端與環境事實（V7 §2.2／§2.4）：誰在服務 MSR／MMIO、本機核心程式碼完整性狀態、
/// 以及依環境矩陣的後端裁決。探測全部唯讀、usermode 取得（登錄檔＋NtQuerySystemInformation＋SCM），
/// 讀不到就標三態，不推測。
/// </summary>
/// <remarks>
/// CodeIntegrity 位元語義以 <see cref="PlatformTrustDecoder.CodeIntegrityFlags"/> 為單一齣處
/// （0x0002 測試簽章、0x0400 HVCI 核心模式）——改位元表時兩邊（與測試）一起動。
/// </remarks>
public static class BackendEnvironmentService
{
    private const string Category = "後端與環境";

    /// <summary>CodeIntegrity 選項中的測試簽章位元（PlatformTrustDecoder.CodeIntegrityFlags 同款）。</summary>
    public const uint CiFlagTestSigning = 0x0002;

    /// <summary>CodeIntegrity 選項中的 HVCI 核心模式位元（PlatformTrustDecoder.CodeIntegrityFlags 同款）。</summary>
    public const uint CiFlagHvciKmci = 0x0400;

    public static IReadOnlyList<HardwareFact> BuildFacts(BackendEnvironmentInputs x, DateTimeOffset at) =>
    [
        BuildBackendFact("backend.msr", "MSR 後端", x.MsrAvailable, x.MsrBackendName, x.MsrUnavailableReason, at),
        BuildBackendFact("backend.mmio", "MMIO 後端", x.MmioAvailable, x.MmioBackendName, x.MmioUnavailableReason, at),
        BuildHvciFact(x, at),
        BuildTestSigningFact(x, at),
        BuildSecureBootFact(x, at),
        BuildPawnIoFact(x, at),
        BuildDecisionFact(x, at),
    ];

    /// <summary>生產組合：從真實讀取器與系統探測收集輸入後產生事實。探測可注入供測試。</summary>
    public static IReadOnlyList<HardwareFact> Collect(IKernelMsrReader msr, IMmioReader mmio, DateTimeOffset at,
        Func<uint?>? codeIntegrityProbe = null, Func<int?>? secureBootProbe = null, Func<string?>? pawnIoProbe = null) =>
        BuildFacts(new BackendEnvironmentInputs(
            MsrAvailable: msr.Available,
            MsrBackendName: msr.BackendName,
            MsrUnavailableReason: msr.Available ? null : msr.UnavailableReason,
            MmioAvailable: mmio.Available,
            MmioBackendName: mmio.BackendName,
            MmioUnavailableReason: mmio.Available ? null : mmio.UnavailableReason,
            CodeIntegrityOptions: (codeIntegrityProbe ?? ReadCodeIntegrityOptions)(),
            SecureBootEnabled: (secureBootProbe ?? ReadSecureBootEnabled)(),
            PawnIoStatus: (pawnIoProbe ?? ProbePawnIo)()), at);

    private static HardwareFact BuildBackendFact(string key, string name, bool available, string? backendName,
        string? reason, DateTimeOffset at)
    {
        const string source = "MmioBackendSelector / IKernelMsrReader";
        return available
            ? new HardwareFact(key, Category, name, string.IsNullOrWhiteSpace(backendName) ? "可用（後端名未標示）" : backendName,
                "", source, FactTrustLevel.Measured, false, at)
            : new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Measured, false, at,
                Availability: FactAvailability.NotSupported,
                UnavailableReason: string.IsNullOrWhiteSpace(reason) ? "無可用後端" : reason);
    }

    private static HardwareFact BuildHvciFact(BackendEnvironmentInputs x, DateTimeOffset at)
    {
        const string key = "platform.hvci", name = "HVCI（記憶體完整性）", source = "NtQuerySystemInformation(103) CodeIntegrityOptions";
        return x.CodeIntegrityOptions is not { } options
            ? Unavailable(key, name, source, at, FactAvailability.ReadError, "CodeIntegrityOptions 讀取失敗")
            : new HardwareFact(key, Category, name,
                (options & CiFlagHvciKmci) != 0 ? "開啟" : "關閉", "", source, FactTrustLevel.Measured, false, at,
                NumericValue: (options & CiFlagHvciKmci) != 0 ? 1 : 0);
    }

    private static HardwareFact BuildTestSigningFact(BackendEnvironmentInputs x, DateTimeOffset at)
    {
        const string key = "platform.testsigning", name = "測試簽章模式", source = "NtQuerySystemInformation(103) CodeIntegrityOptions";
        return x.CodeIntegrityOptions is not { } options
            ? Unavailable(key, name, source, at, FactAvailability.ReadError, "CodeIntegrityOptions 讀取失敗")
            : new HardwareFact(key, Category, name,
                (options & CiFlagTestSigning) != 0
                    ? "測試簽章模式開啟（允許未經微軟簽署的核心驅動載入）"
                    : "關閉", "", source, FactTrustLevel.Measured, false, at,
                NumericValue: (options & CiFlagTestSigning) != 0 ? 1 : 0);
    }

    private static HardwareFact BuildSecureBootFact(BackendEnvironmentInputs x, DateTimeOffset at)
    {
        const string key = "platform.secure_boot", name = "UEFI Secure Boot", source = "登錄檔 SecureBoot\\State\\UEFISecureBootEnabled";
        return x.SecureBootEnabled switch
        {
            1 => new HardwareFact(key, Category, name, "開啟", "", source, FactTrustLevel.Reported, false, at, NumericValue: 1),
            0 => new HardwareFact(key, Category, name, "關閉", "", source, FactTrustLevel.Reported, false, at, NumericValue: 0),
            _ => Unavailable(key, name, source, at, FactAvailability.NotSupported,
                "登錄鍵不存在（可能為 Legacy BIOS 或未提供）或無法讀取"),
        };
    }

    private static HardwareFact BuildPawnIoFact(BackendEnvironmentInputs x, DateTimeOffset at)
    {
        const string key = "platform.pawnio", name = "PawnIO 服務", source = "SCM QueryServiceStatus";
        return x.PawnIoStatus switch
        {
            "running" => new HardwareFact(key, Category, name, "服務執行中", "", source, FactTrustLevel.Reported, false, at),
            "stopped" => new HardwareFact(key, Category, name, "已註冊但未執行", "", source, FactTrustLevel.Reported, false, at),
            "not-installed" => Unavailable(key, name, source, at, FactAvailability.NotSupported, "未安裝（服務未註冊）"),
            _ => Unavailable(key, name, source, at, FactAvailability.ReadError, "無法查詢服務狀態"),
        };
    }

    /// <summary>環境矩陣裁決（V7 §2.4）：HVCI 關 → WinRing0 主力；HVCI 開 → PawnIO／自寫驅動路徑（本版 PawnIO 模組未整合）。</summary>
    private static HardwareFact BuildDecisionFact(BackendEnvironmentInputs x, DateTimeOffset at)
    {
        const string key = "backend.environment_decision", name = "後端裁決（環境矩陣）", source = "PROGRAM-EVEREST-V7 §2.4";
        return x.CodeIntegrityOptions is not { } options
            ? Unavailable(key, name, source, at, FactAvailability.ReadError, "HVCI 狀態讀不到，無法裁決")
            : new HardwareFact(key, Category, name,
                (options & CiFlagHvciKmci) != 0
                    ? "HVCI 開啟：WinRing0 可能被封鎖，應走 PawnIO／自寫驅動路徑（本版 PawnIO 模組未整合，MSR/MMIO 可能整組三態）"
                    : "HVCI 關閉：WinRing0 為主力後端；XsRegProbe 為允許清單備援",
                "", source, FactTrustLevel.Derived, false, at);
    }

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Measured, false, at,
            Availability: availability, UnavailableReason: reason);

    // ── 系統探測（唯讀；全部可由測試以注入的委派取代）──

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int length, out int returned);

    private const int SystemCodeIntegrityInformation = 103;

    /// <summary>讀核心程式碼完整性選項（SYSTEM_CODEINTEGRITY_INFORMATION）；失敗回 null，不推測。</summary>
    public static uint? ReadCodeIntegrityOptions()
    {
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(buffer, 0, 8);       // SYSTEM_CODEINTEGRITY_INFORMATION.Length 必須先填
            Marshal.WriteInt32(buffer, 4, 0);
            int rc = NtQuerySystemInformation(SystemCodeIntegrityInformation, buffer, 8, out _);
            return rc != 0 ? null : (uint)Marshal.ReadInt32(buffer, 4);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>UEFI Secure Boot 開關（登錄檔）：1 開／0 關／null 鍵不存在或無法讀取。</summary>
    public static int? ReadSecureBootEnabled()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            var v = key?.GetValue("UEFISecureBootEnabled");
            return v is null ? null : Convert.ToInt32(v);
        }
        catch { return null; }
    }

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

    /// <summary>PawnIO 服務狀態：「running」／「stopped」／「not-installed」／null（SCM 都開不了，無法回答）。</summary>
    public static string? ProbePawnIo()
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            IntPtr svc = OpenServiceW(scm, "PawnIO", ServiceQueryStatus);
            if (svc == IntPtr.Zero) return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist ? "not-installed" : null;
            try
            {
                return QueryServiceStatus(svc, out var s) && s.CurrentState == ServiceRunning ? "running" : "stopped";
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
