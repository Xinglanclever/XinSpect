using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// SRAT 的事實輸出（ULTIMATE-1000 階段二 P2-007）。
///
/// <para>
/// <b>這一組補上 SLIT 缺的那一半。</b>SLIT 說的是「節點之間的距離」（相對值），
/// SRAT 說的是「誰屬於哪個節點」（歸屬）。兩者合起來才能回答
/// 「這個量測為什麼慢」——資料可能落在別的節點上。
/// </para>
///
/// <para>
/// <b>界線：這是韌體宣告的歸屬，不是 OS 實際的排程。</b>
/// OS 可以（而且常常）覆寫或忽略它。本服務只陳述韌體說了什麼——
/// 而且會把「本機有 SLIT 但沒有 SRAT」這種不對稱也說出來，
/// 因為那只代表其中一個來源缺席，不代表那件事不存在。
/// </para>
/// </summary>
public static class AcpiSratFactsService
{
    public const string SummaryKey = "ac.srat.summary";
    public const string ProcKey = "ac.srat.proc";
    public const string X2ApicKey = "ac.srat.x2apic";
    public const string MemKey = "ac.srat.mem";
    public const string GenericKey = "ac.srat.generic";
    public const string DomainKey = "ac.srat.domain";
    public const string UnrecordedKey = "ac.srat.unrecorded";

    private const string Category = "韌體";
    private const string Source = "ACPI 表列（SRAT）";

    public static IReadOnlyList<HardwareFact> Collect(IAcpiTableSource source, DateTimeOffset at)
    {
        if (!source.Available)
            return [Unavailable(SummaryKey, "SRAT 彙總", at, FactAvailability.NotSupported,
                "ACPI 表來源不可用（需要管理員權限或韌體不提供）")];

        byte[]? srat = null;
        foreach (var table in source.ReadAll())
        {
            if (AcpiTable.TryParseHeader(table, out var header)
                && string.Equals(header.Signature, "SRAT", StringComparison.OrdinalIgnoreCase))
            {
                srat = table;
                break;
            }
        }

        if (srat is null)
            return [Unavailable(SummaryKey, "SRAT 彙總", at, FactAvailability.NotApplicable,
                "表列裡沒有 SRAT——此平台未提供資源親和表；"
                + "沒有 SRAT 不代表沒有 NUMA，只代表韌體沒有用它描述歸屬"
                + "（單節點平台常見，不是錯誤）")];

        if (AcpiSrat.ReservedField(srat) is null)
            return [Unavailable(SummaryKey, "SRAT 彙總", at, FactAvailability.ReadError,
                "SRAT 表短於表頭所需長度——如實拒解，不猜")];

        var structures = AcpiSrat.Structures(srat);
        if (structures.Count == 0)
            return [Unavailable(SummaryKey, "SRAT 彙總", at, FactAvailability.ReadError,
                "SRAT 沒有任何可走訪的子結構——宣告與資料不符")];

        var procs = AcpiSrat.ProcessorAffinities(srat);
        var x2 = AcpiSrat.X2ApicAffinities(srat);
        var mems = AcpiSrat.MemoryAffinities(srat);
        var generics = AcpiSrat.GenericAffinities(srat);
        var counts = AcpiSrat.CountByType(srat);

        var facts = new List<HardwareFact>
        {
            Summary(at, procs, x2, mems, generics, structures.Count),
            ProcFact(at, procs),
            X2ApicFact(at, x2),
            MemFact(at, mems),
            DomainFact(at, procs, x2, mems),
            UnrecordedFact(at, counts),
        };

        if (generics.Count > 0)
            facts.Insert(5, GenericFact(at, generics));

        return facts;
    }

    private static HardwareFact Summary(DateTimeOffset at,
        IReadOnlyList<SratProcessorAffinity> procs, IReadOnlyList<SratProcessorAffinity> x2,
        IReadOnlyList<SratMemoryAffinity> mems, IReadOnlyList<SratGenericAffinity> generics, int structures)
    {
        int declared = procs.Count + x2.Count;
        string value = $"宣告 {declared} 個處理器歸屬（APIC {procs.Count}＋x2APIC {x2.Count}）、"
                     + $"{mems.Count} 段記憶體歸屬、{generics.Count} 個一般啟動器、共 {structures} 個子結構";

        // 只有歸屬沒有距離，或只有距離沒有歸屬——兩者都是「一邊缺席」的事實。
        string? reason = declared == 0 && mems.Count == 0
            ? "SRAT 存在但沒有任何處理器或記憶體歸屬——表是空的，那與「沒有 NUMA」不同："
              + "這張表被提供了卻沒有內容，值得指認"
            : null;

        return new HardwareFact(SummaryKey, Category, "SRAT 彙總", value, "個", Source,
            FactTrustLevel.Measured, false, at, declared, FactAvailability.Present, reason);
    }

    private static HardwareFact ProcFact(DateTimeOffset at, IReadOnlyList<SratProcessorAffinity> procs)
    {
        if (procs.Count == 0)
            return new HardwareFact(ProcKey, Category, "處理器歸屬（APIC）",
                "表中沒有傳統 APIC 的處理器歸屬", "個", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.NotApplicable,
                "平台走 x2APIC 時不會有這種結構——這與 x2APIC 清單有無互相印證");

        string value = string.Join("、", procs.Select(p =>
            $"APIC {p.ApicId}→節點 {p.ProximityDomain}{(p.Enabled ? "" : "（停用）")}"));

        int disabled = procs.Count(p => !p.Enabled);
        return new HardwareFact(ProcKey, Category, "處理器歸屬（APIC）", value, "個", Source,
            FactTrustLevel.Measured, false, at, procs.Count,
            disabled > 0 ? FactAvailability.Unknown : FactAvailability.Present,
            disabled > 0
                ? $"其中 {disabled} 個標為停用——那是韌體自述，不代表 OS 不會用它們"
                : null);
    }

    private static HardwareFact X2ApicFact(DateTimeOffset at, IReadOnlyList<SratProcessorAffinity> x2)
    {
        if (x2.Count == 0)
            return new HardwareFact(X2ApicKey, Category, "處理器歸屬（x2APIC）",
                "表中沒有 x2APIC 的處理器歸屬", "個", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.NotApplicable,
                "沒有 x2APIC 歸屬表示平台以傳統 APIC 描述——255 個處理器以上不可能只用傳統 APIC");

        string value = string.Join("、", x2.Select(p =>
            $"x2APIC {p.ApicId}→節點 {p.ProximityDomain}{(p.Enabled ? "" : "（停用）")}"));

        return new HardwareFact(X2ApicKey, Category, "處理器歸屬（x2APIC）", value, "個", Source,
            FactTrustLevel.Measured, false, at, x2.Count, FactAvailability.Present, null);
    }

    private static HardwareFact MemFact(DateTimeOffset at, IReadOnlyList<SratMemoryAffinity> mems)
    {
        if (mems.Count == 0)
            return new HardwareFact(MemKey, Category, "記憶體歸屬",
                "表中沒有記憶體歸屬結構", "段", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.Unknown,
                "沒有記憶體歸屬時，SRAT 只描述處理器——"
                + "那就回答不了「這塊記憶體屬於哪個節點」，而那是延遲解讀的關鍵");

        var parts = mems.Select(m =>
        {
            double gib = m.Length / (1024.0 * 1024 * 1024);
            return $"節點 {m.ProximityDomain}：0x{m.BaseAddress:X}（{gib:0.##} GiB）"
                 + (m.Enabled ? "" : "（停用）");
        });

        int disabled = mems.Count(m => !m.Enabled);
        return new HardwareFact(MemKey, Category, "記憶體歸屬", string.Join("；", parts), "段",
            Source, FactTrustLevel.Measured, false, at, mems.Count,
            disabled > 0 ? FactAvailability.Unknown : FactAvailability.Present,
            disabled > 0 ? $"其中 {disabled} 段標為停用——同樣是韌體自述" : null);
    }

    private static HardwareFact DomainFact(DateTimeOffset at,
        IReadOnlyList<SratProcessorAffinity> procs, IReadOnlyList<SratProcessorAffinity> x2,
        IReadOnlyList<SratMemoryAffinity> mems)
    {
        var domains = procs.Select(p => p.ProximityDomain)
            .Concat(x2.Select(p => p.ProximityDomain))
            .Concat(mems.Select(m => m.ProximityDomain))
            .Distinct().OrderBy(d => d).ToList();

        if (domains.Count == 0)
            return new HardwareFact(DomainKey, Category, "宣告的鄰近域",
                "沒有可列舉的鄰近域", "個", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有任何歸屬結構就沒有鄰近域可列");

        bool single = domains.Count == 1;
        return new HardwareFact(DomainKey, Category, "宣告的鄰近域",
            string.Join("、", domains.Select(d => $"節點 {d}")), "個", Source,
            FactTrustLevel.Derived, false, at, domains.Count,
            single ? FactAvailability.Present : FactAvailability.Present,
            single
                ? "只有一個鄰近域——跨節點行為在本機無從驗證（未驗證不代表可用）"
                : null);
    }

    private static HardwareFact GenericFact(DateTimeOffset at, IReadOnlyList<SratGenericAffinity> generics)
    {
        string value = string.Join("、", generics.Select(g =>
            $"控制代碼 {g.Handle}（型別 {g.DeviceHandleType}）→節點 {g.ProximityDomain}"));

        return new HardwareFact(GenericKey, Category, "一般啟動器歸屬", value, "個", Source,
            FactTrustLevel.Measured, false, at, generics.Count, FactAvailability.Present,
            "一般啟動器是「不是處理器、也不是記憶體」但仍有親和性的東西（例如 CXL 裝置、"
            + "加速器）——它讓 NUMA 的範圍超出處理器與記憶體");
    }

    private static HardwareFact UnrecordedFact(DateTimeOffset at, IReadOnlyDictionary<byte, int> counts)
    {
        var unknown = counts.Where(kv => AcpiSrat.DescribeType(kv.Key) is null).ToList();
        if (unknown.Count == 0)
            return new HardwareFact(UnrecordedKey, Category, "未收錄的子結構型別",
                "表中所有子結構型別都有解碼器", "種", Source, FactTrustLevel.Measured,
                false, at, 0, FactAvailability.Present, null);

        return new HardwareFact(UnrecordedKey, Category, "未收錄的子結構型別",
            string.Join("、", unknown.Select(kv => $"型別 {kv.Key}（{kv.Value} 個）")), "種",
            Source, FactTrustLevel.Measured, false, at, unknown.Count, FactAvailability.Unknown,
            "有本版沒有解碼器的子結構型別——它們只被計數、沒有被解讀（未收錄不等於表有問題）");
    }

    private static HardwareFact Unavailable(string key, string name, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at,
            null, availability, reason);
}
