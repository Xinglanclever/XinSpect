using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>一列黏滯位元的解讀。</summary>
public sealed class StickyBitRow
{
    public StickyBitRow(string name, string state) { Name = name; State = state; }
    public string Name { get; }
    public string State { get; }
    public string StateText => State;
}

/// <summary>
/// 黏滯節流位元（封裝層級）：IA32_PACKAGE_THERM_STATUS（0x1B1）的 log 位元是「發生過就保持為 1」的紀錄——
/// 使用者說「玩遊戲會頓」但開監控時又不頓，這個位元能回答「自開機以來到底有沒有撞過溫度牆／功耗牆」，
/// 不需要全程監控。唯讀，不寫入、不清除（清除與否交給使用者與韌體）。
/// </summary>
/// <remarks>
/// 誠實界線：本版只解碼經 Intel SDM 與社群除錯紀錄（CoreFreq／Framework）確認的兩個位元：
/// bit1（封裝熱狀態 log）與 bit11（封裝 PL2 功耗限制 log）。其餘位元不做解讀。
/// MSR 讀取走 <see cref="WinRing0Bridge"/>。2026-08-30 實測：PawnIO 的 IntelMsr 在本機對每個 MSR
/// 都回報成功卻回 0，用它會把「讀不到」偽裝成「從未觸發」，因此已改掉。
/// </remarks>
public sealed class ThermalStickyService : ObservableObject
{
    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ObservableCollection<StickyBitRow> Rows { get; } = [];

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
            var rows = await Task.Run(ReadSticky);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Status = "讀取完成。log 位元為黏滯性：發生過就保持為 1，直到重置或軟體清除（本程式不清除）。";
        }
        catch (Exception ex)
        {
            Status = "無法讀取 MSR：" + ex.Message + "（此功能需要管理員權限與 PawnIO 支援）";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private List<StickyBitRow> ReadSticky()
    {
        // 走 WinRing0Bridge：2026-08-30 實測 PawnIO 的 IntelMsr 在本機對每個 MSR 都回報成功卻回 0，
        // 那會讓三個黏滯位全部顯示「從未觸發」——把讀不到偽裝成好消息。
        using var bridge = WinRing0Bridge.Create();
        if (!bridge.Available)
            throw new InvalidOperationException("MSR 橋接無法初始化：" + bridge.Error);
        ulong v = bridge.ReadMsrPair64(0x1B1)
            ?? throw new InvalidOperationException("IA32_PACKAGE_THERM_STATUS（0x1B1）讀取失敗。");

        var rows = new List<StickyBitRow>
        {
            new("溫度牆紀錄（封裝熱狀態 log，bit1）", (v & 0x2) != 0 ? "曾觸發" : "從未觸發"),
            new("PL2 功耗牆紀錄（bit11）", (v & 0x800) != 0 ? "曾觸發" : "從未觸發"),
            new("目前封裝熱狀態（bit0，即時）", (v & 0x1) != 0 ? "正在降頻中" : "未作用"),
            new("原始值", $"0x{v:X16}"),
        };
        // 位 22:16 是溫度讀數，運作中的封裝不可能全為 0；整個暫存器為 0 時不該當成「從未觸發」。
        if (v == 0)
            rows.Add(new("⚠ 讀值可信度", "整個暫存器為 0（連溫度讀數欄都是 0）——這更像是沒真的讀到，而不是「從未觸發」。"));
        return rows;
    }
}
