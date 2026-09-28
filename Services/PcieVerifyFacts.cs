namespace XinSpect;

/// <summary>
/// PCIe 鏈路的驗機事實：取最劣一條（現行寬度／速度 vs 裝置能力），供 R-LNK-01 對帳。
/// </summary>
/// <remarks>
/// <b>唯讀</b>：走 <see cref="WinRing0Bridge"/> 讀 PCI 設定空間，只讀不寫。橋接不可用／非管理員時
/// <see cref="PcieLinkService.ScanAll"/> 會拋例外，呼叫端（<see cref="MachineVerdictService.FromLive"/>）
/// 吞掉即可——不產出事實，引擎自然判「無法判定」。<see cref="PcieLinkService.ScanAll"/> 回傳的列已依
/// 嚴重度由高到低排序，故 <c>rows[0]</c> 就是最劣的一條。
/// </remarks>
public static class PcieVerifyFacts
{
    public static IReadOnlyList<VerifyFact> Collect(DateTime now)
    {
        var (_, rows, _) = PcieLinkService.ScanAll();
        if (rows.Count == 0) return [];
        var w = rows[0];
        return
        [
            Fact(FactId.PcieCurWidth, w.CurWidth, "x", now),
            Fact(FactId.PcieMaxWidth, w.MaxWidth, "x", now),
            Fact(FactId.PcieCurSpeed, w.CurSpeed, "Gen", now),
            Fact(FactId.PcieMaxSpeed, w.MaxSpeed, "Gen", now),
        ];
    }

    private static VerifyFact Fact(FactId id, int v, string unit, DateTime now)
        => new(id, FactCatalog.Name(id), unit == "Gen" ? $"Gen{v}" : $"x{v}", v, unit,
               FactSource.PciConfig, "PCIe 能力結構：Link Capabilities（+0x0C）／Link Status（+0x12）",
               true, FactTrust.Native, now);
}
