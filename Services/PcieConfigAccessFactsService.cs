namespace XinSpect;

/// <summary>
/// 一次「同一個裝置、兩條路徑、同一組位元組」的比對結果（U10 第三圈 PC-0001）。
/// </summary>
/// <param name="Bus">匯流排號。</param>
/// <param name="Device">裝置號。</param>
/// <param name="Function">功能號。</param>
/// <param name="Legacy">舊式 0xCF8／0xCFC 讀到的 vendor/device ID；讀不到為 <c>null</c>。</param>
/// <param name="Ecam">ECAM／MMIO 讀到的同一個 DWORD；讀不到為 <c>null</c>。</param>
public sealed record ConfigAccessProbe(byte Bus, byte Device, byte Function, uint? Legacy, uint? Ecam)
{
    /// <summary>兩條路徑都讀到且值相同。</summary>
    public bool BothReadAndAgree => Legacy is { } l && Ecam is { } e && l == e;

    /// <summary>兩條路徑都讀到但值不同——這是真正值得看的訊號。</summary>
    public bool BothReadAndDisagree => Legacy is { } l && Ecam is { } e && l != e;

    /// <summary>只有一條路徑讀得到。</summary>
    public bool OnlyOneSide => (Legacy is null) != (Ecam is null);
}

/// <summary>
/// PCIe 設定空間「經哪個 API 讀」的宣告與對帳（U10 第三圈 PC-0001）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要這一支：</b>候選項 PC-0001 的理由是「同一張卡的兩個值來自兩個 API 時要看得出來」。
/// 本專案實際上同時有兩條讀設定空間的路：
/// </para>
/// <list type="number">
/// <item><b>舊式 0xCF8／0xCFC</b>（<see cref="IPciConfigReader"/>）：走 I/O 埠，
/// 只需要 ring0 的埠存取；<b>只能定址 256 位元組</b>，擴充設定空間（0x100–0xFFF）看不到。</item>
/// <item><b>ECAM</b>（MCFG 基底 ＋ <see cref="IMmioReader"/>）：走記憶體映射，
/// 每功能一頁 4 KiB；<b>需要 MMIO 後端</b>（本機未載入驅動時整條不可用）。</item>
/// </list>
/// <para>
/// 兩條路都能讀同一個 DWORD（vendor/device ID 在位移 0x00）。<b>讀同一格、比對</b>，
/// 才知道「這兩條路徑看到的是不是同一台裝置」——這比任何一條單獨的讀數都強。
/// </para>
/// <para>
/// <b>不自創第三條路。</b>本服務不新增任何讀取後端：舊式那條吃既有的
/// <see cref="IPciConfigReader"/>，ECAM 那條吃既有的 <see cref="IMmioReader"/> 與
/// <see cref="AcpiTable.McfgEntries"/>。它只是把兩條路的<b>可用性與一致性</b>說出來。
/// </para>
/// </remarks>
public static class PcieConfigAccessFactsService
{
    public const string Category = "PCIe";
    public const string SummaryKey = "pcie.access.summary";
    public const string AgreementKey = "pcie.access.agreement";
    public const string BoundaryKey = "pcie.access.boundary";

    private const string Source = "舊式 0xCF8／0xCFC（I/O 埠）與 ECAM（MCFG＋MMIO）兩條路徑的可用性與一致性";

    /// <summary>舊式路徑最多定址到這裡（PCI 3.0 §3.2.2.3.2：CONFIG_ADDRESS 的 register 欄是 6 位元）。</summary>
    public const uint LegacyConfigBytes = 256;

    /// <summary>ECAM 每個功能一頁的長度（PCIe Base Spec §7.2.2）。</summary>
    public const int EcamBytesPerFunction = 4096;

    /// <summary>
    /// 在 <paramref name="present"/> 裡挑一個「兩條路徑都該看得到」的裝置。
    /// </summary>
    /// <remarks>
    /// 為什麼挑 bus 0：舊式路徑只看得到 bus 0–255 與前 256 位元組，
    /// 而 bus 0 dev 0 fn 0 是<b>唯一一定同時符合兩條路徑視野</b>的位置。
    /// 挑一個 ECAM 看得到、舊式看不到的裝置來比，得到的「不一致」沒有意義。
    /// </remarks>
    public static (byte Bus, byte Device, byte Function) PickProbeTarget(
        IReadOnlyList<(byte Bus, byte Device, byte Function)> present) =>
        present.Count == 0 ? ((byte)0, (byte)0, (byte)0) : present[0];

    /// <summary>
    /// 實地讀一格兩邊：<b>bus 0 dev 0 fn 0 位移 0x00</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 固定挑 host bridge（0:0.0）而不是「清單裡第一個」：這是唯一在兩條路徑視野交集內、
    /// 且<b>每台 x86 機器都有</b>的位置。挑別的裝置來比，兩邊看到的可能根本不是同一個位址，
    /// 那時的「不一致」是假的。
    /// </para>
    /// <para>
    /// 任一邊不可用或讀不到就回 <c>null</c> 那一側——由 <see cref="Collect"/> 決定怎麼講。
    /// </para>
    /// </remarks>
    public static ConfigAccessProbe Probe(
        IPciConfigReader legacy, IMmioReader mmio, IAcpiTableSource acpi,
        byte bus = 0, byte device = 0, byte function = 0)
    {
        uint? legacyId = legacy.Available ? legacy.ReadDword(bus, device, function, 0x00) : null;

        uint? ecamId = null;
        if (acpi.Available && mmio.Available)
        {
            var segment0 = acpi.ReadAll().SelectMany(t => AcpiTable.McfgEntries(t))
                .FirstOrDefault(e => e.SegmentGroup == 0 && e.Base != 0);
            if (segment0 is not null)
            {
                ulong addr = segment0.Base + ((ulong)bus << 20 | (ulong)device << 15 | (ulong)function << 12);
                if (mmio.ReadBlock(addr, 0x10) is { Length: >= 4 } head)
                    ecamId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head);
            }
        }

        return new ConfigAccessProbe(bus, device, function, legacyId, ecamId);
    }

    public static IReadOnlyList<HardwareFact> Collect(
        IPciConfigReader legacy, IMmioReader mmio, IAcpiTableSource acpi,
        ConfigAccessProbe probe, DateTimeOffset at)
    {
        var entries = acpi.Available
            ? acpi.ReadAll().SelectMany(t => AcpiTable.McfgEntries(t)).ToList()
            : [];
        var segment0 = entries.FirstOrDefault(e => e.SegmentGroup == 0 && e.Base != 0);

        bool legacyOk = legacy.Available;
        bool ecamDeclared = segment0 is not null;
        bool ecamOk = ecamDeclared && mmio.Available;

        return
        [
            SummaryFact(legacyOk, legacy.UnavailableReason, ecamDeclared, segment0, ecamOk,
                mmio.Available, mmio.UnavailableReason, acpi.Available, acpi.UnavailableReason, entries.Count, at),
            AgreementFact(probe, legacyOk, ecamOk, at),
            BoundaryFact(at),
        ];
    }

    private static HardwareFact SummaryFact(
        bool legacyOk, string? legacyWhy,
        bool ecamDeclared, McfgEntry? segment0, bool ecamOk, bool mmioOk, string? mmioWhy,
        bool acpiOk, string? acpiWhy, int entryCount, DateTimeOffset at)
    {
        string legacyFace = legacyOk
            ? "可用（走 I/O 埠 0xCF8／0xCFC，需 ring0 的埠存取）"
            : $"不可用：{legacyWhy ?? "原因未提供"}";
        string ecamFace = ecamDeclared
            ? $"ECAM 基底 0x{segment0!.Base:X8}（bus {segment0.StartBus}-{segment0.EndBus}）"
              + (ecamOk ? "，MMIO 後端可用" : $"；但 MMIO 後端不可用：{mmioWhy ?? "原因未提供"}")
            : acpiOk
                ? "平台沒有 MCFG 的 segment 0 條目（或基底為 0）——沒有 ECAM 可循"
                : $"ACPI 表列舉不可用：{acpiWhy ?? "原因未提供"}，因此無從得知有沒有 MCFG";

        string value = $"舊式 0xCF8／0xCFC：{legacyFace}・ECAM：{ecamFace}"
            + $"・MCFG 條目 {entryCount} 筆（只採用 segment 0）"
            + "・這兩條路是同一件事的兩種通道，不是兩個來源：**一致不代表正確，只代表兩條通道通到同一台裝置**";
        return new HardwareFact(SummaryKey, Category, "PCIe 設定空間的存取方法", value, "", Source,
            FactTrustLevel.Derived, false, at, (legacyOk ? 1 : 0) + (ecamOk ? 1 : 0));
    }

    private static HardwareFact AgreementFact(ConfigAccessProbe p, bool legacyOk, bool ecamOk, DateTimeOffset at)
    {
        string where = $"{p.Bus:X2}:{p.Device:X2}.{p.Function}";
        string ids = p.Legacy is { } l
            ? $"0x{l:X8}（vendor 0x{l & 0xFFFF:X4}／device 0x{l >> 16:X4}）"
            : "讀不到";

        if (!legacyOk || !ecamOk)
        {
            string missing = !legacyOk && !ecamOk
                ? "兩條路徑都不可用"
                : !legacyOk ? "舊式 0xCF8／0xCFC 不可用" : "ECAM／MMIO 不可用";
            return new HardwareFact(AgreementKey, Category, "兩條設定空間路徑的一致性", "",
                "", Source, FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                $"{missing}——單邊不叫對帳，所以這一格不給「一致」也不給「不一致」");
        }

        if (p.BothReadAndAgree)
            return new HardwareFact(AgreementKey, Category, "兩條設定空間路徑的一致性",
                $"{where}：兩條路徑讀到同一個 DWORD {ids}——**一致**"
                + "・一致的意義有限：它證明兩條通道通到同一台裝置，不證明那個 ID 是對的"
                + "（要驗 ID 得回到匯流排拓撲與廠商資料庫，那是別把鍵的事）",
                "", Source, FactTrustLevel.Measured, false, at, p.Legacy);

        if (p.BothReadAndDisagree)
            return new HardwareFact(AgreementKey, Category, "兩條設定空間路徑的一致性",
                $"{where}：**兩條路徑的值不同**——舊式 0x{p.Legacy:X8}、ECAM 0x{p.Ecam:X8}"
                + "・這不是「其中一個比較新」：同一個 DWORD 在兩條路徑上不該不同"
                + "・可能的原因是舊式路徑的裝置號編碼被 ACPI 的 _PXM／MCFG 換算動過、或是某條路徑抓到了錯誤的位址"
                + "・**本版不判斷哪一個對**，只把兩個值並列",
                "", Source, FactTrustLevel.Measured, false, at, p.Legacy);

        // 兩條都「可用」但至少一邊讀不到這一格：這是通道可用性與這一格可讀性的差異。
        string legacySide = p.Legacy is { } ll ? $"0x{ll:X8}" : "讀不到";
        string ecamSide = p.Ecam is { } ee ? $"0x{ee:X8}" : "讀不到";
        return new HardwareFact(AgreementKey, Category, "兩條設定空間路徑的一致性",
            $"{where}：舊式 {legacySide}／ECAM {ecamSide}"
            + "・只有一邊讀到——單邊不叫對帳"
            + "・兩條路徑都「可用」不等於「每一格都讀得到」：ECAM 對不存在的功能回全 F，舊式對不存在的功能也可能回全 F 或 0",
            "", Source, FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
            "只讀到一邊，無從比對——這不是「不一致」，是「沒有兩邊可以比」");
    }

    private static HardwareFact BoundaryFact(DateTimeOffset at) =>
        new(BoundaryKey, Category, "PCIe 設定空間的界線宣告",
            $"① 舊式 0xCF8／0xCFC 只能定址 **{LegacyConfigBytes} 位元組**（register 欄 6 位元）："
            + "擴充能力鏈（0x100 起）與 AER、SR-IOV 那些一律看不到——那些只有 ECAM 讀得到；"
            + $"② ECAM 每功能一頁 **{EcamBytesPerFunction} 位元組**，涵蓋 0x000–0xFFF；"
            + "③ 兩條路徑都能讀的只有前 256 位元組裡「同一格」——本版只比 vendor/device ID 那一格，"
            + "**沒有逐格全面比對**（那會在每台機器上把 4 KiB×N 個功能再讀一遍，屬明示觸發）；"
            + "④ 本版不判斷「哪一條路徑才對」：兩條都是作業系統允許的通道，"
            + "不一致時如實並列，判斷留給人；"
            + "⑤ 舊式路徑需 ring0 的埠存取、ECAM 需 MMIO 後端——**兩者都可能在一台機器上同時不可用**，"
            + "那時本鍵只說「兩條都不可用」，不說「這台機器沒有 PCIe」",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);
}
