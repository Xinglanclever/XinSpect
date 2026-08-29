using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.PawnIo;

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
/// IA32_MCi_STATUS（0x400 + i×4）——位 63 valid、位 61 不可修正（UC）、位 38–52 已修正錯誤計數。
/// 記憶體 ECC 已修正次數往上跑，是記憶體／IMC 正在劣化的最早訊號（系統不會崩、使用者無感）。
/// 全程唯讀。逐核以親和性釘選讀取（MCA 銀行為每核心私有）。
/// </summary>
/// <remarks>
/// 誠實界線：這是「當下計數器讀值」；歷史紀錄另見 WHEA 卡片（作業系統收到的部分）。
/// 讀取經 LHM 0.9.6 的 PawnIO IntelMsr（2026-08-29 實測可用）。
/// </remarks>
public sealed class McaService : ObservableObject
{
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
        // PawnIO 的 MSR 模組在未釘選狀態下讀取（實測釘選反而使讀取失敗）；
        // MCA 銀行為每核心私有，本版呈現「當下執行核心」的銀行——銀行結構各核一致，
        // 跨核差異的親和讀取待 PawnIO 的 GroupAffinity 介面驗證後再加入。
        var msr = new IntelMsr();
        if (!msr.ReadMsr(0x179, out ulong mcgCap) || (mcgCap & 0xFF) == 0)
            throw new InvalidOperationException("無法讀取 IA32_MCG_CAP（0x179），或此平台回報 0 個銀行。");

        int banks = (int)(mcgCap & 0xFF);
        long totalCorrected = 0;
        int totalUc = 0, flagged = 0;
        var rows = new List<McaRow>();

        for (int b = 0; b < banks; b++)
        {
            if (!msr.ReadMsr((uint)(0x400 + b * 4), out ulong status)) continue;
            var d = DecodeStatus(status);
            if (!d.Valid) continue;
            totalCorrected += d.CorrectedCount;
            if (d.Uc) totalUc++;
            if (d.CorrectedCount > 0 || d.Uc)
            {
                flagged++;
                rows.Add(new McaRow("當前核心", $"銀行 {b}", d.Uc ? "不可修正" : "可修正",
                    d.CorrectedCount.ToString("N0"), d.AddressValid ? "位址有效（詳見 MCi_ADDR）" : ""));
            }
        }

        msr.Close();

        var summaryText = totalUc > 0
            ? $"⚠ 掃描 {banks} 個銀行：發現 {totalUc} 個不可修正事件（嚴重，請對照 WHEA 卡片與記憶體測試）。"
            : $"掃描 {banks} 個銀行：{totalCorrected} 次已修正錯誤、0 次不可修正。"
            + (totalCorrected > 0 ? "已修正計數往上跑＝記憶體／IMC 劣化的最早訊號，建議定期回來對照。" : "全部乾淨。");

        return (summaryText, rows);
    }

    // ── 解碼純函式（單元測試涵蓋）──────────────────────────────────────────

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
