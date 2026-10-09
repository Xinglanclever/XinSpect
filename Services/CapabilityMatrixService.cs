namespace XinSpect;

/// <summary>
/// 本機能力矩陣（docs/PROGRAM-ULTIMATE-2026-10-10.md §5.11）：把散在各服務的「後端可用性」
/// 彙總成一片可查詢的 cap.* 事實——使用者看到「不適用」時，能立刻分清是
/// <b>環境不支援</b>（沒有 TPM 晶片）還是<b>通路未就緒</b>（驅動沒載）還是<b>收集沒跑到</b>（來源鍵缺席）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一版不新增任何探測：</b>每一條 cap.* 都是既有事實鍵的彙總（來源鍵與判定一一對映），
/// 所以它不會和來源事實打架——來源翻成三態，cap 跟著翻；來源是量到的，cap 才是可用的。
/// 來源事實這次沒出現（對應收集入口尚未跑），cap 如實回 Unknown＋「來源鍵缺席」——
/// 缺席不讀成「不支援」，這是 v2.36 六態守恆的同一條線。
/// </para>
/// <para>
/// <b>鍵以 MatrixRow 字面值呼叫：</b>覆蓋申報的掃描器認這個形狀（同 SupportedBit／CollectSigList
/// 的先例）——彙總鍵若經變數傳入，目錄就掃不到，又會變成「申報看不見的生產」。
/// </para>
/// </remarks>
public static class CapabilityMatrixService
{
    public const string Category = "能力矩陣";
    public const string Source = "彙總自既有事實鍵（見各行來源欄）";

    /// <summary>彙總：從本輪全部事實裡取九個來源鍵，产出 cap.* 行。</summary>
    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<HardwareFact> all, DateTimeOffset at)
    {
        var byKey = new Dictionary<string, HardwareFact>(StringComparer.Ordinal);
        foreach (var f in all)
            byKey[f.Key] = f;   // 同鍵多筆取最後（管線上每鍵唯一）

        return
        [
            MatrixRow("cap.msr", byKey, "backend.msr", "核心 MSR 讀取通路", at),
            MatrixRow("cap.mmio", byKey, "backend.mmio", "實體記憶體映射（MMIO）通路", at),
            MatrixRow("cap.pci", byKey, "pci.dev.00.0", "PCI 設定空間讀取", at),
            MatrixRow("cap.iop", byKey, "cmos.rtc_time", "I/O 埠讀寫（CMOS 為實證）", at),
            MatrixRow("cap.smbus", byKey, "smbus.tsod", "SMBus 交易（TSOD 為實證）", at),
            MatrixRow("cap.tpm", byKey, "tpm.present", "TPM 2.0 通道（TBS）", at),
            MatrixRow("cap.uefi.variables", byKey, "uefi.db", "UEFI 韌體變數讀取（需提權）", at),
            MatrixRow("cap.wmi", byKey, "asset.chassis", "WMI 資料層", at),
            MatrixRow("cap.pmu", byKey, "pmu.version", "架構 PMU（CPUID 0xA）", at),
        ];
    }

    private static HardwareFact MatrixRow(string capKey, Dictionary<string, HardwareFact> byKey,
        string sourceKey, string name, DateTimeOffset at)
    {
        if (!byKey.TryGetValue(sourceKey, out var src))
            return new HardwareFact(capKey, Category, $"能力：{name}", "", "",
                $"{Source}｜來源鍵 {sourceKey}", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown,
                $"來源鍵 {sourceKey} 在本次事實集缺席——無法判定（缺席不讀成不支援，也不讀成可用）");

        if (src.Availability == FactAvailability.Present)
            return new HardwareFact(capKey, Category, $"能力：{name}", "可用", "",
                $"{Source}｜來源 {sourceKey}＝Present", FactTrustLevel.Derived, false, at,
                src.NumericValue);

        // 來源三態：原封不動搬可用性與原因——cap 不重新解釋環境，只做彙總。
        return new HardwareFact(capKey, Category, $"能力：{name}", "", "",
            $"{Source}｜來源 {sourceKey}＝{src.Availability}", FactTrustLevel.Unknown, false, at, null,
            src.Availability, src.UnavailableReason);
    }
}
