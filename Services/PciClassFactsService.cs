namespace XinSpect;

/// <summary>
/// 類別碼三層解碼（U10 第三圈 PC-0004，第 317 輪）：class／subclass／progIF 分層陳述。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要拆三層：</b>既有的裝置描述把三層壓成一句「顯示控制器／VGA 相容顯示」，
/// 而真正的辨識力在第三層：同一個「序列匯流排／USB」，progIF 0x20 是 EHCI（USB 2.0）、
/// 0x30 是 XHCI（USB 3.x）——那是兩代不同的控制器，混成一個名字之後就分不出來了。
/// </para>
/// <para>
/// <b>沒收錄就說沒收錄：</b>每一層各自查表，查不到就寫「本版沒有這一層的名稱，只報原值」。
/// 不會拿子類別的名字去填 progIF，也不會因為基礎類別有名字就假裝三層都有。
/// </para>
/// <para>純函式：輸入是讀回來的原始類別碼，這支不碰設定空間。</para>
/// </remarks>
public static class PciClassFactsService
{
    /// <summary>三層解碼總覽（靜態鍵）。</summary>
    public const string SummaryKey = "pci.class.summary";

    /// <summary>逐功能的類別三層家族：<c>pci.class.dev.{裝置:02x}.{功能}</c>（成員＝這台機器掃到幾個功能）。</summary>
    public const string DevicePrefix = "pci.class.dev.";

    private const string Category = "PCI 裝置";
    private const string Source = "PCI 設定空間 0x08＋PciKnowledge 三層對照表";

    /// <summary>一個功能的原始類別碼（設定空間 0x08 的 dword）。</summary>
    public sealed record Sample(byte Device, byte Function, uint ClassRaw);

    /// <summary>把樣本攤成逐功能事實＋一筆總覽。</summary>
    /// <param name="readErrors">走訪時讀不到類別碼的功能數（如實寫進總覽，不併進「沒有名稱」）。</param>
    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<Sample> samples, DateTimeOffset at, int readErrors = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var facts = new List<HardwareFact>(samples.Count + 1);
        int full = 0, toSub = 0, toBase = 0;

        foreach (var sample in samples)
        {
            var cls = PciKnowledge.DecodeClassCode(sample.ClassRaw);
            string baseName = PciKnowledge.BaseClassName(cls.BaseClass) ?? Unlisted;
            string subName = PciKnowledge.SubClassName(cls.BaseClass, cls.SubClass) ?? Unlisted;
            string progName = PciKnowledge.ProgIfName(cls.BaseClass, cls.SubClass, cls.ProgIf) ?? Unlisted;

            bool hasBase = baseName != Unlisted;
            bool hasSub = subName != Unlisted;
            bool hasProg = progName != Unlisted;
            if (hasBase && hasSub && hasProg) full++;
            else if (hasBase && hasSub) toSub++;
            else toBase++;

            string value =
                $"基礎類別 0x{cls.BaseClass:X2}「{baseName}」／"
                + $"子類別 0x{cls.SubClass:X2}「{subName}」／"
                + $"progIF 0x{cls.ProgIf:X2}「{progName}」"
                + $"，revision 0x{cls.Revision:X2}";

            facts.Add(new HardwareFact($"{DevicePrefix}{sample.Device:x2}.{sample.Function}", Category,
                $"PCI 0:{sample.Device:X2}.{sample.Function} 類別", value, "", Source,
                FactTrustLevel.Measured, false, at, hasProg ? 3 : hasSub ? 2 : 1, FactAvailability.Present, null));
        }

        facts.Add(SummaryFact(samples.Count, full, toSub, toBase, readErrors, at));
        return facts.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    private const string Unlisted = "本版沒有這一層的名稱，只報原值";

    private static HardwareFact SummaryFact(int functions, int full, int toSub, int toBase, int readErrors, DateTimeOffset at)
    {
        if (functions == 0)
        {
            // 六態契約：非 Present 不帶值、不帶數字，理由裡把「沒掃到」與「解碼失敗」分開。
            return new HardwareFact(SummaryKey, Category, "類別碼三層解碼", "", "個", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "這台機器沒有回報任何 PCI 功能（bus 0 掃不到裝置）——不是解碼失敗，也不是「都沒有類別碼」。");
        }

        string value = $"{functions} 個功能：三層都有名稱 {full} 個、只到子類別 {toSub} 個、只到基礎類別 {toBase} 個。"
                       + "「只到某一層」代表對照表沒有那一格，不是裝置沒有宣告。"
                       + (readErrors > 0 ? $"另有 {readErrors} 個功能的類別碼讀取失敗（如實缺席，不計入上面三個數字）。" : "");

        return new HardwareFact(SummaryKey, Category, "類別碼三層解碼", value, "個", Source,
            FactTrustLevel.Derived, false, at, functions, FactAvailability.Present, null);
    }
}
