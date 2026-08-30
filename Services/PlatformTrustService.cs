using System.Collections.ObjectModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>一列平台可信度事實。</summary>
public sealed class TrustRow
{
    public TrustRow(string key, string value, string note = "") { Key = key; Value = value; Note = note; }
    public string Key { get; }
    public string Value { get; }
    public string Note { get; }
}

/// <summary>
/// 平台可信度：這台機器上「MSR／TSC／PMU 的讀值算不算原生」的資格說明。
/// 若 VBS／HVCI 開啟，Windows 本身跑在 Hyper-V 之上，MSR 可能被攔截或回假值、TSC 可能被偏移，
/// 那麼本程式所有 MSR 卡片（Top-down、頻率真相、RDT、MCA、安全位元）的可信度都要跟著打折——
/// 這件事必須寫在使用者看得到的地方，而不是藏在程式碼註解裡。
/// </summary>
/// <remarks>
/// 資料來源：CPUID 0x1 ECX 位 31（hypervisor 存在）＋ 0x40000000 簽章（<b>只有在位 31 為 1 時才能讀</b>——
/// 不支援的 CPUID 葉會回傳最大標準葉的內容，裸機上直接讀 0x40000000 會拿到 0x16 的值並解出假廠商字串）、
/// <c>Win32_DeviceGuard</c>（VBS／HVCI 的設定與執行狀態）、<c>NtQuerySystemInformation(103)</c>
/// （核心程式碼完整性選項）、CPUID 0x80000007 EDX 位 8（Invariant TSC）。
/// 誠實界線：只讀、不下「你安全／不安全」的結論，只回答「讀到的數字可不可信」。
/// </remarks>
public sealed class PlatformTrustService : ObservableObject
{
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int length, out int returned);

    private const int SystemCodeIntegrityInformation = 103;

    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _verdict = "—";
    /// <summary>一句話結論：本機的 MSR／TSC／PMU 讀值是否為原生。</summary>
    public string Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }

    public ObservableCollection<TrustRow> Rows { get; } = [];

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "讀取中…";
        try
        {
            var (rows, verdict) = await Task.Run(ReadAll);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Verdict = verdict;
            Status = "讀取完成（全部唯讀）。";
        }
        catch (Exception ex)
        {
            Status = "讀取失敗：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private (List<TrustRow> Rows, string Verdict) ReadAll()
    {
        var rows = new List<TrustRow>();

        // ── 1. Hypervisor ──
        bool hyper = false;
        string signature = "";
        if (X86Base.IsSupported)
        {
            hyper = ((uint)X86Base.CpuId(1, 0).Ecx & 0x8000_0000) != 0;
            if (hyper)
            {
                var r = X86Base.CpuId(unchecked((int)0x40000000), 0);
                signature = PlatformTrustDecoder.HypervisorSignature((uint)r.Ebx, (uint)r.Ecx, (uint)r.Edx);
            }
        }
        rows.Add(new TrustRow("Hypervisor 存在位（CPUID 1 ECX 位 31）", hyper ? "是" : "否",
            hyper ? "作業系統跑在某個 hypervisor 之上（含 Windows 自己開 VBS 的情況）。"
                  : "沒有 hypervisor 介入：MSR 由硬體直接回應。"));
        if (hyper)
            rows.Add(new TrustRow("Hypervisor 簽章（CPUID 0x40000000）",
                PlatformTrustDecoder.HypervisorVendor(signature),
                $"原始簽章「{signature}」。"));

        // ── 2. VBS／HVCI（Win32_DeviceGuard）──
        uint? vbsStatus = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard", "SELECT * FROM Win32_DeviceGuard");
            bool found = false;
            foreach (ManagementObject m in searcher.Get())
            {
                found = true;
                vbsStatus = ToUInt(m["VirtualizationBasedSecurityStatus"]);
                var configured = ToUIntArray(m["SecurityServicesConfigured"]);
                var running = ToUIntArray(m["SecurityServicesRunning"]);
                var available = ToUIntArray(m["AvailableSecurityProperties"]);

                rows.Add(new TrustRow("VBS 狀態", PlatformTrustDecoder.DescribeVbsStatus(vbsStatus),
                    "VBS 開啟時 Windows 本身是 Hyper-V 上的一個分割區。"));
                rows.Add(new TrustRow("已設定的安全服務", PlatformTrustDecoder.DescribeServices(configured),
                    "「已設定」是政策要求，不代表真的跑起來了。"));
                rows.Add(new TrustRow("正在執行的安全服務", PlatformTrustDecoder.DescribeServices(running),
                    "只有這一列成立才真的生效——HVCI 執行中會影響 MSR／PMU 的可信度。"));
                rows.Add(new TrustRow("平台可用的安全屬性", PlatformTrustDecoder.DescribeProperties(available),
                    "硬體／韌體具備的條件，與是否啟用無關。"));
                break;
            }
            if (!found)
                rows.Add(new TrustRow("Win32_DeviceGuard", "—（查詢成功但沒有實例）", "此 Windows 版本可能未提供該類別。"));
        }
        catch (Exception ex)
        {
            rows.Add(new TrustRow("Win32_DeviceGuard", "—（" + ex.Message + "）", "讀不到就是讀不到，不推測 VBS 狀態。"));
        }

        // ── 3. 核心程式碼完整性（NtQuerySystemInformation 103）──
        rows.Add(ReadCodeIntegrity());

        // ── 4. TSC 是否恆定（所有計時的前提）──
        bool invariantTsc = false;
        if (X86Base.IsSupported)
        {
            uint maxExt = (uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax;
            if (maxExt >= 0x80000007)
            {
                uint edx = (uint)X86Base.CpuId(unchecked((int)0x80000007), 0).Edx;
                invariantTsc = (edx & (1u << 8)) != 0;
                rows.Add(new TrustRow("Invariant TSC（CPUID 0x80000007 EDX 位 8）", invariantTsc ? "是" : "否",
                    invariantTsc ? "TSC 不隨頻率與 C 狀態變化：可當時間基準（頻率真相卡片即以此為前提）。"
                                 : "TSC 會隨頻率漂移：任何以 TSC 換算時間的結果都不可信。"));
            }
            else
            {
                rows.Add(new TrustRow("Invariant TSC", "—（處理器未提供 CPUID 0x80000007）"));
            }
        }

        return (rows, PlatformTrustDecoder.Verdict(hyper, vbsStatus, invariantTsc));
    }

    private static TrustRow ReadCodeIntegrity()
    {
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(buffer, 0, 8);       // SYSTEM_CODEINTEGRITY_INFORMATION.Length 必須先填
            Marshal.WriteInt32(buffer, 4, 0);
            int rc = NtQuerySystemInformation(SystemCodeIntegrityInformation, buffer, 8, out _);
            if (rc != 0)
                return new TrustRow("核心程式碼完整性（NtQSI 103）", $"—（NTSTATUS 0x{rc:X8}）", "讀取失敗，不推測狀態。");
            uint options = (uint)Marshal.ReadInt32(buffer, 4);
            return new TrustRow("核心程式碼完整性（NtQSI 103）",
                PlatformTrustDecoder.DescribeCodeIntegrity(options), $"原始 Options＝0x{options:X8}。");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint? ToUInt(object? v)
    {
        try { return v is null ? null : Convert.ToUInt32(v); }
        catch { return null; }
    }

    private static uint[] ToUIntArray(object? v)
    {
        if (v is null) return [];
        try
        {
            if (v is uint[] u) return u;
            if (v is int[] i) return i.Select(x => (uint)x).ToArray();
            if (v is Array a) return a.Cast<object>().Select(Convert.ToUInt32).ToArray();
        }
        catch { }
        return [];
    }
}
