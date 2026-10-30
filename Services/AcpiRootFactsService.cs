namespace XinSpect;

/// <summary>
/// ACPI 根指標表這一層的事實（U10 第三圈 AC-0001＋AC-0002）。
/// </summary>
/// <remarks>
/// <para>
/// <b>回答什麼：</b>「這台機器的 ACPI 樹，從根到葉，哪幾層我們看得到」。具體四件：
/// 根指標表（RSDT／XSDT）在不在 OS 的列舉裡、韌體自述的 ACPI 世代（FADT 修訂號）、
/// FADT 指到的兩張表在哪、以及<b>RSDP 為什麼驗不了</b>。
/// </para>
/// <para>
/// <b>最重要的一句：</b>候選項 AC-0001 要求「RSDP 簽章＋傳統／延伸雙總和」自己驗一次。
/// 這一輪把「驗不了」也變成一條可查的事實，並且說清楚缺的是哪一層——
/// 這正是第三圈的主題（說清楚為什麼讀不到），而不是假裝驗過。
/// </para>
/// </remarks>
public static class AcpiRootFactsService
{
    public const string Category = "ACPI";
    public const string SummaryKey = "ac.root.summary";
    public const string DsdtKey = "ac.root.dsdt";
    public const string BoundaryKey = "ac.root.boundary";

    private const string Source = "EnumSystemFirmwareTables ＋ FADT 指標欄（唯讀）";

    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<byte[]> tables, DateTimeOffset at)
    {
        var parsed = new List<(string Sig, byte[] Bytes)>();
        foreach (var t in tables)
            if (AcpiTable.TryParseHeader(t, out var h)) parsed.Add((h.Signature, t));

        if (parsed.Count == 0)
            return
            [
                new HardwareFact(SummaryKey, Category, "ACPI 根指標表這一層", "", "張", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "這一輪沒有列舉到任何 ACPI 表——根指標表也無從判斷；不是「沒有根指標表」"),
                new HardwareFact(DsdtKey, Category, "FADT 指到的 FACS／DSDT", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "沒有 ACPI 表就沒有 FADT；不猜位址"),
                BoundaryFact(at),
            ];

        var sigs = parsed.Select(p => p.Sig).Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        bool hasRsdt = sigs.Contains("RSDT", StringComparer.Ordinal);
        bool hasXsdt = sigs.Contains("XSDT", StringComparer.Ordinal);

        var facp = parsed.FirstOrDefault(p => p.Sig == "FACP").Bytes;
        var fadt = facp is null ? null : AcpiFadt.Parse(facp);

        return
        [
            SummaryFact(parsed.Count, sigs.Count, hasRsdt, hasXsdt, fadt, at),
            DsdtFact(fadt, facp is not null, at),
            BoundaryFact(at),
        ];
    }

    private static HardwareFact SummaryFact(int tableCount, int signatureCount, bool hasRsdt, bool hasXsdt,
        AcpiFadtInfo? fadt, DateTimeOffset at)
    {
        string roots = (hasRsdt, hasXsdt) switch
        {
            (false, false) => "**RSDT 與 XSDT 都不在列舉裡**——根指標表這一層是 OS 沒給，不是韌體沒有",
            (true, false) => "RSDT 在列舉裡、XSDT 不在（ACPI 1.0 形狀，或 OS 只給 RSDT）",
            (false, true) => "XSDT 在列舉裡、RSDT 不在（ACPI 2.0 以後常見：OS 只給 XSDT）",
            (true, true) => "RSDT 與 XSDT 都在列舉裡",
        };
        string fadtFace = fadt is null
            ? "這份清單裡沒有 FADT（FACP）——韌體自述的 ACPI 世代無從取得"
            : $"FADT 修訂 {fadt.Revision}"
              + (fadt.MinorVersion > 0 ? $".{fadt.MinorVersion}" : "")
              + $"（延伸欄位{(fadt.HasExtendedFields ? "在" : "不在")}）";

        string value = $"列舉到 {tableCount} 張 ACPI 表（{signatureCount} 種簽章）：{roots}"
            + $"・{fadtFace}"
            + "・界線：這是**OS 給的表清單**；RSDP 本身與它指到的兩張根指標表在 usermode 拿不到（見 ac.root.boundary），"
            + $"所以「這台機器總共幾張表」的正確答案是「至少這 {tableCount} 張」";
        return new HardwareFact(SummaryKey, Category, "ACPI 根指標表這一層", value, "張", Source,
            FactTrustLevel.Measured, false, at, tableCount);
    }

    private static HardwareFact DsdtFact(AcpiFadtInfo? fadt, bool facpPresent, DateTimeOffset at)
    {
        if (!facpPresent)
            return new HardwareFact(DsdtKey, Category, "FADT 指到的 FACS／DSDT", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "這份表清單裡沒有 FADT（FACP），所以沒有指標可讀——不是「沒有 DSDT」");

        if (fadt is null)
            return new HardwareFact(DsdtKey, Category, "FADT 指到的 FACS／DSDT", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "FACP 表太短或簽章不符，解析不出指標欄——不猜位址");

        string agree = fadt.DsdtAgrees switch
        {
            true => "32 位與 64 位欄**相同**（這張表在 4 GiB 以下，只走 32 位指標的舊 OS 也看得到）",
            false => "**32 位與 64 位欄不同**——這張表只有 64 位欄指得到，也就是「只走 RSDT 的舊 OS 看不到它」",
            null => "只有 32 位欄（ACPI 1.0 形狀或表太短），沒有 64 位欄可比",
        };
        string xFields = fadt.XDsdt is { } xd && fadt.XFirmwareCtrl is { } xf
            ? $"・64 位欄：FACS 0x{xf:X}、DSDT 0x{xd:X}"
            : "";
        string fourGiB = fadt.XDsdt is { } x
            ? (AcpiFadt.IsBelow4GiB(x) ? "・兩者都在 4 GiB 以下" : "・**位址在 4 GiB 以上**：只有 XSDT 指得到")
            : "";

        string value = $"FADT 指到：FACS 0x{fadt.FirmwareCtrl:X}、DSDT 0x{fadt.Dsdt:X}{xFields}"
            + $"・{agree}{fourGiB}"
            + "・界線一：FADT 只告訴我們**這兩張**在哪；其餘表的位址在根指標表裡，而那一層讀不到，"
            + "所以「全部表都在 4 GiB 以下」這種話本版說不出口；"
            + "・界線二：位址是實體位址，**本版不去讀那些記憶體**——說得出位址不等於看得到內容";
        return new HardwareFact(DsdtKey, Category, "FADT 指到的 FACS／DSDT", value, "", Source,
            FactTrustLevel.Measured, false, at, null);
    }

    /// <summary>
    /// 界線宣告：RSDP 的雙總和為什麼本版驗不了。
    /// 這把鍵什麼都不測——它陳述的是範圍，與 AC-0001 那一項的處置。
    /// </summary>
    private static HardwareFact BoundaryFact(DateTimeOffset at) =>
        new(BoundaryKey, Category, "ACPI 根指標表的界線宣告",
            "RSDP 位於實體記憶體（EBDA 或 0xF0000–0xFFFFF 之間），"
            + "本版實測兩條 usermode 路徑皆不可得（`\\\\.\\PhysicalMemory` 回 ERROR_FILE_NOT_FOUND、"
            + "`\\\\?\\GLOBALROOT\\Device\\PhysicalMemory` 回 ERROR_INVALID_HANDLE），"
            + "因此 **RSDP 的簽章與雙總和（前 20 位元組、前 36 位元組各自加總為 0）本版無法第一手驗證**；"
            + "而 RSDT／XSDT 本身也不在 `EnumSystemFirmwareTables('ACPI')` 的清單裡。"
            + "所以「缺的表是沒有還是看不到」在本版的正確答案是：**這一層是 OS 沒給**——"
            + "不是韌體沒有，也不是本版漏讀。要真的驗 RSDP 需要核心層（或實體記憶體映射），"
            + "那不在本專案的 usermode 範圍內",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);
}
