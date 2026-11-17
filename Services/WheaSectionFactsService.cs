namespace XinSpect;

/// <summary>
/// WHEA 節區知識包的事實面（U10 第四圈批次一：WH-0005～0010 命名層＋WH-0012／0013／0014／0015）。
/// </summary>
/// <remarks>
/// <para>
/// <b>回答什麼：</b>既有 <c>os.ev.whea.*</c> 只答「幾筆事件」、第 326 輪的 <c>whea.mem.*</c>
/// 只解記憶體節區內容。這一支把<b>解碼表本身</b>攤開成事實：這台機器的 WHEA 事件裡可能出現的
/// 節區型別 GUID 是哪些、通知類型是哪些、PCIe 節區的 PortType 碼怎麼念——讓「已修正的硬體錯誤」
/// 這句話接得上「屬於哪一類硬體」。
/// </para>
/// <para>
/// <b>沒有新讀取面：</b>純投影，Collect 不做 I/O、不開行程。本知識包只列對照表與政策；
/// 已接受事件的有界 CPER 記憶體、PCIe、Processor Generic 與 XPF 安全投影由各事實服務處理，不代表所有節區均已支援。
/// </para>
/// </remarks>
public static class WheaSectionFactsService
{
    public const string Category = "硬體錯誤";
    public const string SummaryKey = "whea.sec.kp.summary";
    public const string SectionsKey = "whea.sec.kp.sections";
    public const string NotifyKey = "whea.sec.kp.notify";
    public const string PcieDevTypeKey = "whea.sec.pcie.devtype";
    public const string IpfLegacyKey = "whea.sec.ipf.legacy";
    public const string BoundaryKey = "whea.sec.boundary";

    private const string Source =
        "UEFI 規格 v2.7 附錄 N.2.1／N.2.2＋Windows SDK cper.h（10.0.26100.0）＋Linux include/linux/cper.h 對照（kernel.org，2026-11-15）";

    /// <summary>純函式：知識包內容不隨機器變化，任何時間點收集結果一致。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at)
    {
        int sections = WheaSectionKnowledge.Sections.Count;
        int notify = WheaSectionKnowledge.NotifyTypes.Count;
        int ports = WheaSectionKnowledge.PciePortTypes.Count;

        return
        [
            new HardwareFact(SummaryKey, Category, "WHEA 節區知識包彙總",
                $"收錄 {sections} 條節區型別 GUID、{notify} 條通知類型 GUID、{ports} 格 PCIe 埠型別碼"
                + "・這是**解碼表**（讓事件與節區說的話可以被讀懂），不代表這台機器曾出現這些節區；"
                + "已接受事件的記憶體、PCIe 與 Processor Generic 節區另由 whea.mem／whea.pcie／whea.proc 事實列出（見 whea.sec.boundary）"
                + "・Processor Specific: IA64 故意不收：Linux cper.h 該條的 GUID_INIT 位元組參數 9 個（巨集收 8 個），"
                + "巨集自 IA64 移除後從未被展開所以沒人發現——位元組序列不可信，本版不命名其 GUID",
                "條", Source, FactTrustLevel.Measured, false, at, sections),
            new HardwareFact(SectionsKey, Category, "節區型別對照表",
                string.Join("\n", WheaSectionKnowledge.Sections.Select(s => $"{s.GuidText}＝{s.Name}（{s.Note}）")),
                "條", Source, FactTrustLevel.Measured, false, at, sections),
            new HardwareFact(NotifyKey, Category, "通知類型對照表",
                string.Join("\n", WheaSectionKnowledge.NotifyTypes.Select(n => $"{n.GuidText}＝{n.Name}（{n.Note}）")),
                "條", Source, FactTrustLevel.Measured, false, at, notify),
            new HardwareFact(PcieDevTypeKey, Category, "PCIe 埠型別碼表",
                string.Join("、", WheaSectionKnowledge.PciePortTypes.Select(p => $"{p.Code}＝{p.Name}"))
                + "・**code 2／3 在 SDK 列舉中未定義**——本表不收，查表回 null（不猜）；來源：enum _WHEA_PCIEXPRESS_DEVICE_TYPE",
                "格", Source, FactTrustLevel.Measured, false, at, ports),
            new HardwareFact(IpfLegacyKey, Category, "IPF／CPE 遺產節區", "",
                "", Source, FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                "x86／x64 機器常態不存在 Itanium 節區（UEFI 規格 N.2.2 的 IA64 條目僅 IA64 平台產生）"
                + "・本版**連其 GUID 都不命名**：Linux include/linux/cper.h 的 CPER_SEC_PROC_IPF 把 9 個位元組"
                + "傳給只收 8 個的 GUID_INIT 巨集（IA64 支援移除後該巨集從未被展開，編譯器沒機會抓）"
                + "・兩份公開源（UEFI 規格附錄 N 與 Linux cper.h）無法對出一致的位元組序列＝**待核**，"
                + "照專案規則無出處不出值"),
            new HardwareFact(BoundaryKey, Category, "WHEA 節區知識包的界線宣告",
                "① 本知識包只有**對照表**：節區型別 GUID、通知類型、PCIe 埠型別；處理器專屬／PCI-X 節區的"
                + "逐欄解碼未全實作；記憶體、PCIe 與 Processor Generic 已接受事件的有界投影由 whea.mem／whea.pcie／whea.proc 服務處理；XPF 處理器專屬安全檢查欄位與 context 結構描述由 whea.xpf 處理，身分／位址／暫存器資料不輸出；"
                + "② **本知識包不做 I/O**——不自行讀事件或拆 blob；已接受事件外層須通過版本、長度與描述符驗證；"
                + "未取得可驗證節區不等於硬體不存在，也不代表沒有錯誤；"
                + "③ **未收錄的 GUID 政策**（U10-WH-0013）：原始列出、不猜語意——查表回 null 就是「知識包沒收錄」，"
                + "不是「不存在」；"
                + "④ **GHES 泛用錯誤狀態塊**（U10-WH-0014）：本版只有出處宣告（cper.h／HEST），沒有解碼實作，"
                + "不冒充已解碼；"
                + "⑤ 通知類型表 8 條是「UEFI 規格定義的通知類型」，不是「這台機器用過的通知類型」——後者要看事件，"
                + "兩件事不能混",
                "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null),
        ];
    }
}
