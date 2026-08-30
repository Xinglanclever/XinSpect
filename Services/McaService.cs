using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>一列被標記的 MCA 事件（僅列出有內容的銀行）。</summary>
public sealed class McaRow
{
    public McaRow(string core, string bank, string kind, string corrected, string detail)
    { Core = core; Bank = bank; Kind = kind; Corrected = corrected; Detail = detail; }
    public string Core { get; }
    public string Bank { get; }
    public string Kind { get; }
    public string Corrected { get; }
    public string Detail { get; }
}

/// <summary>
/// 機器檢查架構（MCA）銀行掃描：IA32_MCG_CAP（0x179）取銀行數，逐核逐銀行讀
/// IA32_MCi_STATUS（<b>0x401 + i×4</b>）——位 63 valid、位 61 不可修正（UC）、位 38–52 已修正錯誤計數。
/// 記憶體 ECC 已修正次數往上跑，是記憶體／IMC 正在劣化的最早訊號（系統不會崩、使用者無感）。
/// 全程唯讀。逐核以親和性釘選讀取（MCA 銀行為每核心私有）。
/// </summary>
/// <remarks>
/// 誠實界線：這是「當下計數器讀值」；歷史紀錄另見 WHEA 卡片（作業系統收到的部分）。
/// 讀取走 <see cref="WinRing0Bridge"/>。2026-08-30 實測：PawnIO 的 IntelMsr 在本機
/// <b>對每個 MSR 都回報成功卻回 0</b>（含 0x179／0x8B），因此不能用它——回 true 不代表讀到值。
/// </remarks>
public sealed class McaService : ObservableObject
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, ulong affinityMask);

    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _summary = "—";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public ObservableCollection<McaRow> Rows { get; } = [];

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "掃描中…";
        Rows.Clear();
        try
        {
            var (summary, rows) = await Task.Run(ScanAll);
            Summary = summary;
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
        }
        catch (Exception ex)
        {
            Summary = "無法讀取 MCA：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private (string Summary, List<McaRow> Rows) ScanAll()
    {
        using var bridge = WinRing0Bridge.Create();
        if (!bridge.Available)
            throw new InvalidOperationException("MSR 橋接無法初始化：" + bridge.Error);

        ulong processMask = (ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64();
        var lps = CoreLatencyService.LogicalProcessorsFromMask(processMask).ToArray();
        if (lps.Length == 0)
            throw new InvalidOperationException("取不到可用的邏輯處理器清單。");

        // 銀行數：MCG_CAP 位 7:0。逐核心讀（各核應一致，不一致時如實列出）。
        int banks = 0;
        var capPerLp = new Dictionary<int, ulong?>();
        foreach (int lp in lps)
        {
            ulong? cap = ReadPinned(bridge, lp, 0x179, processMask);
            capPerLp[lp] = cap;
            if (cap is not null) banks = Math.Max(banks, (int)(cap.Value & 0xFF));
        }
        if (banks == 0)
            throw new InvalidOperationException("IA32_MCG_CAP（0x179）讀不到，或此平台回報 0 個銀行。");

        long totalCorrected = 0;
        int totalUc = 0, unreadable = 0;
        var rows = new List<McaRow>();

        foreach (int lp in lps)
        {
            int lpBanks = capPerLp[lp] is { } c ? (int)(c & 0xFF) : 0;
            for (int b = 0; b < lpBanks; b++)
            {
                ulong? status = ReadPinned(bridge, lp, StatusMsr(b), processMask);
                if (status is null) { unreadable++; continue; }
                var d = DecodeStatus(status.Value);
                if (!d.Valid) continue;
                totalCorrected += d.CorrectedCount;
                if (d.Uc) totalUc++;
                rows.Add(new McaRow($"LP {lp}", $"銀行 {b}", d.Uc ? "不可修正" : "可修正",
                    d.CorrectedCount.ToString("N0"), d.AddressValid ? "位址有效（詳見 MCi_ADDR）" : ""));
            }
        }

        string scope = $"掃描 {lps.Length} 個邏輯處理器 × {banks} 個銀行";
        string tail = unreadable > 0 ? $"（{unreadable} 個銀行讀不到，未計入）" : "";
        var summaryText = totalUc > 0
            ? $"⚠ {scope}{tail}：發現 {totalUc} 個不可修正事件（嚴重，請對照 WHEA 卡片與記憶體測試）。"
            : totalCorrected > 0
                ? $"{scope}{tail}：{totalCorrected:N0} 次已修正錯誤、0 次不可修正。已修正計數往上跑＝記憶體／IMC 劣化的最早訊號，建議定期回來對照。"
                : $"{scope}{tail}：所有銀行的 valid 位皆為 0——沒有任何被標記的事件。這代表「開機到現在沒被記錄」，不是「硬體保證無誤」。";

        return (summaryText, rows);
    }

    /// <summary>釘選到指定邏輯處理器讀一個 MSR（MCA 銀行為每核心私有，不釘選等於重複讀同一顆）。</summary>
    private static ulong? ReadPinned(WinRing0Bridge bridge, int lp, uint msr, ulong restoreMask)
    {
        if (SetThreadAffinityMask(GetCurrentThread(), 1UL << lp) == IntPtr.Zero) return null;
        try { return bridge.ReadMsrPair64(msr); }
        finally { SetThreadAffinityMask(GetCurrentThread(), restoreMask); }
    }

    // ── 解碼純函式（單元測試涵蓋）──────────────────────────────────────────

    /// <summary>
    /// 銀行 i 的 STATUS 暫存器位址。每個銀行占四個 MSR：CTL(0x400+4i)、STATUS(0x401+4i)、
    /// ADDR(0x402+4i)、MISC(0x403+4i)。<b>讀 CTL 當 STATUS 是 1.4.0 的錯誤</b>——CTL 通常全為 1，
    /// 位 63 恆為 1 會被誤判成 valid，位 38–52 也會被讀成假的已修正計數。
    /// </summary>
    public static uint StatusMsr(int bank) => (uint)(0x401 + bank * 4);

    /// <summary>MCi_STATUS 解碼（Intel SDM：位 63 valid、61 UC、58 ADDR valid、38–52 已修正計數）。</summary>
    public static (bool Valid, bool Uc, bool AddressValid, bool MiscValid, long CorrectedCount) DecodeStatus(ulong status)
    {
        bool valid = (status >> 63 & 1) != 0;
        bool uc = (status >> 61 & 1) != 0;
        bool addrValid = (status >> 58 & 1) != 0;
        bool miscValid = (status >> 59 & 1) != 0;
        long corrected = (long)((status >> 38) & 0x7FFF);
        return (valid, uc, addrValid, miscValid, corrected);
    }
}
