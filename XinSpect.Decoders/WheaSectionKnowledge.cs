using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// CPER 節區型別對照的一條登記（U10 第四圈 WH-0005～0010 的命名層）。
/// </summary>
/// <param name="GuidText">UEFI 規格附錄 N.2.2 的節區定義 GUID（正典字串形式）。</param>
/// <param name="Name">英文名稱（規格原文）＋繁中語意。</param>
/// <param name="Note">補註（例如 CXL 子表的規格節號）。</param>
[SpecRef("UEFI 規格 v2.7 附錄 N.2.2 Section Type Definitions（經 Linux include/linux/cper.h 對照，kernel.org，2026-11-15 取得）")]
public sealed record WheaSectionTypeInfo(string GuidText, string Name, string Note);

/// <summary>
/// CPER 通知類型對照的一條登記（U10-WH-0015）。
/// </summary>
/// <param name="GuidText">UEFI 規格附錄 N.2.1 的通知類型 GUID。</param>
/// <param name="Name">英文名稱＋繁中語意。</param>
/// <param name="Note">補註。</param>
[SpecRef("UEFI 規格 v2.7 附錄 N.2.1 Notification Types（經 Linux include/linux/cper.h 對照，kernel.org，2026-11-15 取得）")]
public sealed record WheaNotifyTypeInfo(string GuidText, string Name, string Note);

/// <summary>
/// PCIe 節區 PortType 欄的一格登記（U10-WH-0007）。
/// </summary>
/// <param name="Code">WHEA_PCIEXPRESS_DEVICE_TYPE 的數值。</param>
/// <param name="Name">繁中語意（含英文原名）。</param>
[SpecRef("Windows SDK 10.0.26100.0 um/cper.h：enum _WHEA_PCIEXPRESS_DEVICE_TYPE")]
public sealed record WheaPciePortTypeInfo(int Code, string Name);

/// <summary>
/// WHEA 節區知識包（U10 第四圈批次一）：把 CPER 節區型別 GUID、通知類型 GUID、PCIe 埠型別碼
/// 三張對照表變成可查詢的資料。
/// </summary>
/// <remarks>
/// <para>
/// <b>這是知識包，不是行為。</b>它回答「這個 GUID／這個碼是什麼」，不做任何讀取、不解析任何
/// 位元組——第 326 輪已確立「事件 data 段的外層包裝沒有可核出處，不拆 blob」，本包延續同一界線：
/// 節區位元組由呼叫端提供；沒有呼叫端傳入就是 NotApplicable 並寫明缺什麼。
/// </para>
/// <para>
/// <b>唯一分母</b>：15 條節區型別、8 條通知類型、9 格 PCIe 埠型別（2／3 在 SDK 列舉中未定義，
/// 如實標「未定義」不猜）。這三個數字由 <c>WheaSectionKnowledgeTests</c> 釘住。
/// </para>
/// <para>
/// <b>為什麼 Processor Specific: IA64 不在表裡：</b>Linux cper.h 的 <c>CPER_SEC_PROC_IPF</c>
/// 呼叫 <c>GUID_INIT(...)</b> 時給了 9 個位元組參數（巨集只收 8 個）——該巨集自 IA64 移除後
/// 從未被展開，所以編譯器沒抓到；位元組序列因此不可信，本版<b>不命名其 GUID</b>（待核）。
/// x86 機器上此節區常態不存在（見 <c>whea.sec.ipf.legacy</c>）。
/// </para>
/// </remarks>
[SpecRef("UEFI 規格 v2.7 附錄 N.2.1／N.2.2＋Windows SDK 10.0.26100.0 um/cper.h＋Linux include/linux/cper.h 對照")]
public static class WheaSectionKnowledge
{
    /// <summary>節區型別對照表：15 條（IA64 故意不收，理由見類別註解）。</summary>
    [SpecRef("UEFI 規格 v2.7 附錄 N.2.2（Linux include/linux/cper.h 的 CPER_SEC_* 逐條對照）")]
    public static IReadOnlyList<WheaSectionTypeInfo> Sections { get; } =
    [
        new("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB", "Processor Generic（處理器泛用）", "CPER_SEC_PROC_GENERIC"),
        new("DC3EA0B0-A144-4797-B95B-53FA242B6E1D", "Processor Specific: X86/X86_64（處理器特定 x86/x64）", "CPER_SEC_PROC_IA"),
        new("E19E3D16-BC11-11E4-9CAA-C2051D5D46B0", "Processor Specific: ARM（處理器特定 ARM）", "CPER_SEC_PROC_ARM"),
        new("A5BC1114-6F64-4EDE-B863-3E83ED7C83B1", "Platform Memory（平台記憶體）", "CPER_SEC_PLATFORM_MEM——第 326 輪已解其內容（whea.mem.*）"),
        new("D995E954-BBC1-430F-AD91-B44DCB3C6F35", "PCIExpress（PCIe）", "CPER_SEC_PCIE"),
        new("81212A96-09ED-4996-9471-8D729C8E69ED", "Firmware Error Record Reference（韌體錯誤記錄參照）", "CPER_SEC_FW_ERR_REC_REF"),
        new("C5753963-3B84-4095-BF78-EDDAD3F9C9DD", "PCI/PCI-X Bus（PCI/PCI-X 匯流排）", "CPER_SEC_PCI_X_BUS"),
        new("EB5E4685-CA66-4769-B6A2-26068B001326", "PCI Component/Device（PCI 元件/裝置）", "CPER_SEC_PCI_DEV"),
        new("5B51FEF7-C79D-4434-8F1B-AA62DE3E2C64", "DMA Remapping Generic（DMA 重映射泛用）", "CPER_SEC_DMAR_GENERIC"),
        new("71761D37-32B2-45CD-A7D0-B0FEDD93E8CF", "Intel VT for Directed I/O specific DMAr（Intel VT-d 專屬 DMAr）", "CPER_SEC_DMAR_VT"),
        new("036F84E1-7F37-428C-A79E-575FDFAA84EC", "IOMMU specific DMAr（IOMMU 專屬 DMAr）", "CPER_SEC_DMAR_IOMMU"),
        new("80B9EFB4-52B5-4DE3-A777-68784B771048", "CXL Protocol Error Section（CXL 協定錯誤）", "CPER_SEC_CXL_PROT_ERR"),
        new("FBCD0A77-C260-417F-85A9-088B1621EBA6", "CXL General Media Event Record（CXL 一般媒體事件）", "CXL 3.0 §8.2.9.2.1.1"),
        new("601DCBB3-9C06-4EAB-B8AF-4E9BFB5C9624", "CXL DRAM Event Record（CXL DRAM 事件）", "CXL 3.0 §8.2.9.2.1.2"),
        new("FE927475-DD59-4339-A586-79BAB113B774", "CXL Memory Module Event Record（CXL 記憶體模組事件）", "CXL 3.0 §8.2.9.2.1.3"),
    ];

    /// <summary>通知類型對照表：8 條。</summary>
    [SpecRef("UEFI 規格 v2.7 附錄 N.2.1（Linux include/linux/cper.h 的 CPER_NOTIFY_* 逐條對照）")]
    public static IReadOnlyList<WheaNotifyTypeInfo> NotifyTypes { get; } =
    [
        new("2DCE8BB1-BDD7-450E-B9AD-9CF4EBD4F890", "CMC—Corrected Machine Check（已更正機器檢查）", "CPER_NOTIFY_CMC"),
        new("4E292F96-D843-4A55-A8C2-D481F27EBEEE", "CPE—Corrected Platform Error（已更正平台錯誤）", "CPER_NOTIFY_CPE"),
        new("E8F56FFE-919C-4CC5-BA88-65ABE14913BB", "MCE—Machine Check Exception（機器檢查例外）", "CPER_NOTIFY_MCE"),
        new("CF93C01F-1A16-4DFC-B8BC-9C4DAF67C104", "PCIe—PCI Express Error（PCI Express 錯誤）", "CPER_NOTIFY_PCIE"),
        new("CC5263E8-9308-454A-89D0-340BD39BC98E", "INIT—INIT Record（IPF 遺產）", "CPER_NOTIFY_INIT"),
        new("5BAD89FF-B7E6-42C9-814A-CF2485D6E98A", "NMI—Non-Maskable Interrupt（不可遮罩中斷）", "CPER_NOTIFY_NMI"),
        new("3D61A466-AB40-409A-A698-F362D464B38F", "BOOT—BOOT Error Record（開機錯誤記錄）", "CPER_NOTIFY_BOOT"),
        new("667DD791-C6B3-4C27-8A6B-0F8E722DEB41", "DMAR—DMA Remapping Error（DMA 重映射錯誤）", "CPER_NOTIFY_DMAR"),
    ];

    /// <summary>PCIe 埠型別碼表：9 格。code 2／3 在 SDK 列舉中未定義——不猜。</summary>
    [SpecRef("Windows SDK 10.0.26100.0 um/cper.h：enum _WHEA_PCIEXPRESS_DEVICE_TYPE（WheaPciExpressEndpoint=0 起）")]
    public static IReadOnlyList<WheaPciePortTypeInfo> PciePortTypes { get; } =
    [
        new(0, "Endpoint（端點）"),
        new(1, "Legacy Endpoint（舊式端點）"),
        new(4, "Root Port（根埠）"),
        new(5, "Upstream Switch Port（交換器上行埠）"),
        new(6, "Downstream Switch Port（交換器下行埠）"),
        new(7, "PCIe-to-PCI-X Bridge（PCIe 轉 PCI-X 橋）"),
        new(8, "PCI-X-to-Express Bridge（PCI-X 轉 PCIe 橋）"),
        new(9, "Root Complex Integrated Endpoint（根複合體整合端點）"),
        new(10, "Root Complex Event Collector（根複合體事件收集器）"),
    ];

    /// <summary>
    /// 查節區型別名稱。不分大小寫、容忍前後大括號與空白；查不到回 <c>null</c>——
    /// <b>未收錄的 GUID 不猜語意</b>（U10-WH-0013 政策）。
    /// </summary>
    [SpecRef("同 Sections 表的出處；查無回 null 的政策沿主目錄 U10-WH-0013「未知名節區 GUID 原始列出」")]
    public static string? SectionName(string? guidText) =>
        Lookup(Sections.Select(s => (s.GuidText, s.Name)), guidText);

    /// <summary>查通知類型名稱；查不到回 <c>null</c>（不猜）。</summary>
    [SpecRef("同 NotifyTypes 表的出處")]
    public static string? NotifyName(string? guidText) =>
        Lookup(NotifyTypes.Select(n => (n.GuidText, n.Name)), guidText);

    /// <summary>查 PCIe 埠型別名稱；2／3 與範圍外回 <c>null</c>——列舉裡沒定義的碼不命名。</summary>
    [SpecRef("Windows SDK 10.0.26100.0 um/cper.h enum _WHEA_PCIEXPRESS_DEVICE_TYPE：2／3 兩值未列於列舉")]
    public static string? PciePortName(int code)
    {
        foreach (var t in PciePortTypes)
            if (t.Code == code)
                return t.Name;
        return null;
    }

    private static string? Lookup(IEnumerable<(string GuidText, string Name)> table, string? guidText)
    {
        var key = NormalizeGuid(guidText);
        if (key is null) return null;
        foreach (var (guid, name) in table)
            if (NormalizeGuid(guid) == key)
                return name;
        return null;
    }

    private static string? NormalizeGuid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim().Trim('{', '}').Trim();
        return trimmed.Length == 36 ? trimmed.ToUpperInvariant() : null;
    }
}
