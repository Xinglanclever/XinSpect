namespace XinSpect;

/// <summary>
/// PPTT 拓撲事實（Vol2 AC-017）：把 ACPI PPTT 的處理器／快取節點攤成事實，
/// 並與作業系統看到的邏輯處理器並列——<b>兩個來源不一致是事實，不是判決</b>。
/// </summary>
/// <remarks>
/// PPTT 是 ACPI 6.2+ 才有的表，且不是每個平台都提供；沒有這張表就明說沒有（不是讀不到）。
/// 韌體自述的拓撲與 CPUID／OS 的拓撲本來就可能不同（例如 SMT 揭露方式、離線核心），
/// 因此這裡只並列數字與差異，不挑一邊說另一邊錯。
/// </remarks>
public static class PpttFactsService
{
    private const string Category = "ACPI";

    public static IReadOnlyList<HardwareFact> Collect(IAcpiTableSource acpi, DateTimeOffset at, int? logicalProcessors = null)
    {
        if (!acpi.Available)
            return Na(at, acpi.UnavailableReason ?? "無法列舉 ACPI 表", FactAvailability.InsufficientPrivilege);

        byte[]? pptt = null;
        foreach (var t in acpi.ReadAll())
            if (t.Length >= AcpiTable.HeaderLength && t[..4].SequenceEqual("PPTT"u8)) { pptt = t; break; }

        if (pptt is null)
            return Na(at, "本機未列舉到 PPTT 表（ACPI 6.2+ 才引入，多數消費級平台未提供）", FactAvailability.NotApplicable);

        var procs = AcpiPptt.Processors(pptt);
        var caches = AcpiPptt.Caches(pptt);
        int packages = procs.Count(p => p.IsPhysicalPackage);
        int threads = procs.Count(p => p.IsThread);
        int leaves = procs.Count(p => p.IsLeaf);

        string compare = logicalProcessors is { } lp
            ? $"；作業系統回報 {lp} 個邏輯處理器" + (lp == procs.Count ? "（與 PPTT 節點數一致）" : $"（與 PPTT 節點數 {procs.Count} 不一致——兩邊都列出，不挑一邊說另一邊錯）")
            : "";
        string cacheText = caches.Count > 0
            ? "；快取節點：" + string.Join("、", caches.Select(c => c.SizeValid ? $"{c.Size / 1024} KiB" : "大小未回報"))
            : "；無快取節點";

        return
        [
            new HardwareFact("ac.pptt.count", Category, "PPTT 處理器節點數",
                procs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "個節點",
                "ACPI PPTT（Processor Properties Topology Table）", FactTrustLevel.Measured, false, at, procs.Count),
            new HardwareFact("ac.pptt.summary", Category, "PPTT 拓撲摘要",
                $"處理器節點 {procs.Count}（實體封裝 {packages}、執行緒 {threads}、葉節點 {leaves}）" + cacheText + compare,
                "", "ACPI PPTT；欄位核對 Linux actbl2.h", FactTrustLevel.Measured, false, at),
        ];
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason, FactAvailability availability)
        =>
        [
            new("ac.pptt.count", Category, "PPTT 處理器節點數", "", "", "ACPI PPTT", FactTrustLevel.Unknown, false, at, null, availability, reason),
            new("ac.pptt.summary", Category, "PPTT 拓撲摘要", "", "", "ACPI PPTT", FactTrustLevel.Unknown, false, at, null, availability, reason),
        ];
}
