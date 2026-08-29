using System.Collections.ObjectModel;
using LibreHardwareMonitor.PawnIo;

namespace XinSpect;

/// <summary>一列安全位元／緩解狀態。</summary>
public sealed class SecurityBitRow
{
    public SecurityBitRow(string key, string value) { Key = key; Value = value; }
    public string Key { get; }
    public string Value { get; }
}

/// <summary>
/// 安全緩解狀態：讓晶片自己說它免疫什麼。
/// IA32_ARCH_CAPABILITIES（0x10A）是 CPU 宣告的免疫位元（RDCL_NO＝免疫 Meltdown 類、
/// SSB_NO＝免疫 Speculative Store Bypass、MDS_NO＝免疫 MDS 系列……）；
/// IA32_SPEC_CTRL（0x48）則是作業系統目前實際啟用的緩解（IBRS／STIBP／SSBD）。
/// 兩者並列，就是「晶片說免疫 X、系統啟用 Y」的誠實呈現——全部量到，不需要會過期的漏洞資料庫。
/// </summary>
/// <remarks>
/// 誠實界線：只呈現位元事實；不宣稱「你受／不受某 CVE 影響」。若微碼未宣告任何免疫位元（值為 0），
/// 就照實說——那代表這顆晶片（或微碼版本）沒有提供免疫聲明，而非「你中鏢了」。
/// </remarks>
public sealed class CpuSecurityService : ObservableObject
{
    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ObservableCollection<SecurityBitRow> Rows { get; } = [];

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "讀取中…";
        Rows.Clear();
        try
        {
            var rows = await Task.Run(ReadAll);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Status = "讀取完成（本核讀值；免疫位元通常各核一致）。";
        }
        catch (Exception ex)
        {
            Status = "無法讀取 MSR：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private List<SecurityBitRow> ReadAll()
    {
        var msr = new IntelMsr();
        var rows = new List<SecurityBitRow>();

        if (msr.ReadMsr(0x10A, out ulong arch))
        {
            foreach (var (bit, name) in CpuSecurityDecoder.ArchCapsBits)
                rows.Add(new SecurityBitRow($"免疫：{name}", CpuSecurityDecoder.OnOff(arch, bit)));
            rows.Add(new SecurityBitRow("原始值（0x10A）", $"0x{arch:X16}"));
        }
        else
        {
            rows.Add(new SecurityBitRow("ARCH_CAPABILITIES", "此處理器未實作（微碼未提供免疫宣告）"));
        }

        if (msr.ReadMsr(0x48, out ulong spec))
        {
            rows.Add(new SecurityBitRow("系統啟用：IBRS", CpuSecurityDecoder.OnOff(spec, 0)));
            rows.Add(new SecurityBitRow("系統啟用：STIBP", CpuSecurityDecoder.OnOff(spec, 1)));
            rows.Add(new SecurityBitRow("系統啟用：SSBD", CpuSecurityDecoder.OnOff(spec, 3)));
        }
        msr.Close();
        return rows;
    }
}

/// <summary>安全位元解碼純函式（單元測試涵蓋）。</summary>
public static class CpuSecurityDecoder
{
    /// <summary>IA32_ARCH_CAPABILITIES 的免疫位元（Intel SDM 定義）。</summary>
    public static readonly (int Bit, string Name)[] ArchCapsBits =
    {
        (0, "RDCL_NO（免疫 Meltdown 類）"),
        (1, "IBRS_ALL（硬體支援 IBRS）"),
        (2, "RSBA（有 RSBA，需注意）"),
        (3, "SKIP_L1DFL_VMENTRY"),
        (4, "SSB_NO（免疫 Speculative Store Bypass）"),
        (5, "MDS_NO（免疫 MDS 系列）"),
        (6, "TAA_NO（免疫 TSX 非同步中止）"),
        (8, "MISC_PACKAGE_CTLS"),
        (10, "FB_CLEAR（免疫 Fill Buffer 清殘留）"),
        (13, "PSFD（免疫 Push 有限頻寬降頻）"),
    };

    public static string OnOff(ulong value, int bit) => (value & (1UL << bit)) != 0 ? "是" : "否";

    /// <summary>IA32_SPEC_CTRL（0x48）的啟用位元。</summary>
    public static (bool Ibrs, bool Stibp, bool Ssbd) DecodeSpecCtrl(ulong v)
        => ((v & 1) != 0, (v & 2) != 0, (v & 8) != 0);
}
