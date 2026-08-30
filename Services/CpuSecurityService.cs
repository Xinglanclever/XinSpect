using System.Collections.ObjectModel;

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
        // 走 WinRing0Bridge：2026-08-30 實測 PawnIO 的 IntelMsr 在本機對每個 MSR 都回報成功卻回 0，
        // 那會讓每一個免疫位元都顯示「否」、每一項系統緩解都顯示「未啟用」——全是假的壞消息。
        using var bridge = WinRing0Bridge.Create();
        var rows = new List<SecurityBitRow>();
        if (!bridge.Available)
        {
            rows.Add(new SecurityBitRow("MSR 橋接", "—（" + bridge.Error + "）；沒有讀到值，因此本卡片不做任何免疫宣告。"));
            return rows;
        }

        // CPUID 07H.0 EDX：位 29＝IA32_ARCH_CAPABILITIES 存在、位 26＝IBRS/IBPB、位 31＝SSBD。
        // 先問 CPUID 才能區分「處理器沒有這個 MSR」與「讀取失敗」——兩者都不該被寫成「否」。
        uint edx7 = 0;
        if (System.Runtime.Intrinsics.X86.X86Base.IsSupported
            && System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0).Eax >= 7)
            edx7 = (uint)System.Runtime.Intrinsics.X86.X86Base.CpuId(7, 0).Edx;
        bool hasArchCaps = (edx7 & (1u << 29)) != 0;
        bool hasSpecCtrl = (edx7 & ((1u << 26) | (1u << 31))) != 0;

        ulong? arch = hasArchCaps ? bridge.ReadMsrPair64(0x10A) : null;
        if (arch is { } a)
        {
            foreach (var (bit, name) in CpuSecurityDecoder.ArchCapsBits)
                rows.Add(new SecurityBitRow($"免疫：{name}", CpuSecurityDecoder.OnOff(a, bit)));
            rows.Add(new SecurityBitRow("原始值（0x10A）", $"0x{a:X16}"));
        }
        else if (!hasArchCaps)
        {
            rows.Add(new SecurityBitRow("ARCH_CAPABILITIES", "此處理器未實作（CPUID 07H.0 EDX 位 29 為 0：微碼未提供免疫宣告）"));
        }
        else
        {
            rows.Add(new SecurityBitRow("ARCH_CAPABILITIES", "—（CPUID 說有這個 MSR，但讀取失敗；不代表免疫位元為 0）"));
        }

        ulong? spec = hasSpecCtrl ? bridge.ReadMsrPair64(0x48) : null;
        if (spec is { } s)
        {
            rows.Add(new SecurityBitRow("系統啟用：IBRS", CpuSecurityDecoder.OnOff(s, 0)));
            rows.Add(new SecurityBitRow("系統啟用：STIBP", CpuSecurityDecoder.OnOff(s, 1)));
            rows.Add(new SecurityBitRow("系統啟用：SSBD", CpuSecurityDecoder.OnOff(s, 2)));
            rows.Add(new SecurityBitRow("原始值（0x48）", $"0x{s:X16}（本核當下值：這是每核心、可被作業系統隨時切換的暫存器）"));
        }
        else if (!hasSpecCtrl)
        {
            rows.Add(new SecurityBitRow("IA32_SPEC_CTRL", "此處理器未實作（CPUID 07H.0 EDX 位 26／31 皆為 0）"));
        }
        else
        {
            rows.Add(new SecurityBitRow("IA32_SPEC_CTRL", "—（讀取失敗；不代表緩解未啟用）"));
        }
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

    /// <summary>IA32_SPEC_CTRL（0x48）的啟用位元：位 0 IBRS、位 1 STIBP、<b>位 2 SSBD</b>（不是位 3）。</summary>
    public static (bool Ibrs, bool Stibp, bool Ssbd) DecodeSpecCtrl(ulong v)
        => ((v & 1) != 0, (v & 2) != 0, (v & 4) != 0);
}
