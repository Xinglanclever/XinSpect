using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// DMAR 的事實輸出（ULTIMATE-1000 階段二 P2-010）。
///
/// <para>
/// <b>這一組回答的是「DMA 保護的邊界在哪裡」：</b>
/// 哪些裝置的 DMA 要經過 IOMMU 轉譯、哪些記憶體區域是那個保護的例外、
/// 以及有沒有裝置落在这份表的管理範圍之外。
/// </para>
///
/// <para>
/// <b>界線：只陳述韌體宣告了什麼，不判定保護是否有效。</b>
/// 這份表說有 RMRR，不等於那個區域現在沒有被保護（OS 可能另外處理），
/// 也不等於有被保護（IOMMU 可能沒啟用）。事實層的責任是把邊界指出來，
/// 讓使用者知道「這一塊是設計上留的洞」，而不是替他下結論。
/// </para>
/// </summary>
public static class AcpiDmarFactsService
{
    public const string SummaryKey = "ac.dmar.summary";
    public const string DrhdKey = "ac.dmar.drhd";
    public const string RmrrKey = "ac.dmar.rmrr";
    public const string AtsrKey = "ac.dmar.atsr";
    public const string CoverageKey = "ac.dmar.coverage";
    public const string UnrecordedKey = "ac.dmar.unrecorded";

    private const string Category = "韌體";
    private const string Source = "ACPI 表列（DMAR）";

    /// <summary>從 ACPI 表來源收集 DMAR 事實。表缺席＝不適用；表壞掉＝讀取失敗。</summary>
    public static IReadOnlyList<HardwareFact> Collect(IAcpiTableSource source, DateTimeOffset at)
    {
        if (!source.Available)
            return [Unavailable(SummaryKey, "DMAR 彙總", at, FactAvailability.NotSupported,
                "ACPI 表來源不可用（需要管理員權限或韌體不提供）")];

        byte[]? dmar = null;
        foreach (var table in source.ReadAll())
        {
            if (AcpiTable.TryParseHeader(table, out var header)
                && string.Equals(header.Signature, "DMAR", StringComparison.OrdinalIgnoreCase))
            {
                dmar = table;
                break;
            }
        }

        if (dmar is null)
            return [Unavailable(SummaryKey, "DMAR 彙總", at, FactAvailability.NotApplicable,
                "表列裡沒有 DMAR——此平台未提供 DMA 重映射表；"
                + "沒有這張表不代表沒有 IOMMU，只代表韌體沒有用它描述範圍")];

        var h = AcpiDmar.Header(dmar);
        if (h is null)
            return [Unavailable(SummaryKey, "DMAR 彙總", at, FactAvailability.ReadError,
                "DMAR 表短於表頭所需長度——如實拒解，不猜")];

        var structures = AcpiDmar.Structures(dmar);
        if (structures.Count == 0)
            return [Unavailable(SummaryKey, "DMAR 彙總", at, FactAvailability.ReadError,
                "DMAR 沒有任何可走訪的子結構——宣告與資料不符")];

        var drhds = AcpiDmar.Drhds(dmar);
        var rmrrs = AcpiDmar.Rmrrs(dmar);
        var atsrs = AcpiDmar.Atsrs(dmar);
        var counts = AcpiDmar.CountByType(dmar);

        return
        [
            Summary(at, h.Value, drhds, rmrrs, structures.Count),
            DrhdFact(at, drhds),
            RmrrFact(at, rmrrs),
            AtsrFact(at, atsrs),
            CoverageFact(at, drhds),
            UnrecordedFact(at, counts),
        ];
    }

    private static HardwareFact Summary(DateTimeOffset at, (byte HostAddressWidth, byte Flags) h,
        IReadOnlyList<DmarDrhd> drhds, IReadOnlyList<DmarRmrr> rmrrs, int structures)
    {
        bool intrRemap = (h.Flags & 1) != 0;
        bool x2apicOptOut = (h.Flags & 2) != 0;
        bool dmaCtrlOptIn = (h.Flags & 4) != 0;

        string value = $"主機位址寬度 {h.HostAddressWidth} 位元；"
                     + $"{drhds.Count} 個硬體單位、{rmrrs.Count} 個保留記憶體區域、共 {structures} 個子結構；"
                     + $"中斷重映射 {(intrRemap ? "已宣告" : "未宣告")}、"
                     + $"x2APIC 退出 {(x2apicOptOut ? "有" : "無")}、"
                     + $"DMA 控制平台退出 {(dmaCtrlOptIn ? "有" : "無")}";

        // 沒有任何 DRHD 涵蓋全機時，這是 DMA 保護的真實缺口，必須說出來。
        string? reason = drhds.Count == 0
            ? "表裡沒有任何硬體單位——這份 DMAR 只宣告了保留區域，沒有宣告轉譯範圍"
            : !drhds.Any(d => d.CoversWholeMachine)
                ? "沒有任何硬體單位宣告涵蓋全機——有些裝置的 DMA 不在這份表的轉譯範圍內，"
                  + "這是事實陳述；那些裝置是否真的需要轉譯，不在本表的回答範圍"
                : null;

        return new HardwareFact(SummaryKey, Category, "DMAR 彙總", value, "個", Source,
            FactTrustLevel.Measured, false, at, drhds.Count, FactAvailability.Present, reason);
    }

    private static HardwareFact DrhdFact(DateTimeOffset at, IReadOnlyList<DmarDrhd> drhds)
    {
        if (drhds.Count == 0)
            return new HardwareFact(DrhdKey, Category, "IOMMU 硬體單位",
                "表裡沒有硬體單位結構", "個", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.Unknown,
                "沒有硬體單位代表這份表沒有宣告任何轉譯範圍——"
                + "那與「IOMMU 沒有運作」是兩件事，本表只能回答前者");

        string value = string.Join("；", drhds.Select(d =>
            $"段 {d.Segment} 基底 0x{d.RegisterBase:X}"
            + (d.CoversWholeMachine ? "（涵蓋全機）" : "（未涵蓋全機）")
            + (d.DeviceScopes.Count > 0 ? $"、{d.DeviceScopes.Count} 段裝置範圍" : "")));

        int whole = drhds.Count(d => d.CoversWholeMachine);
        return new HardwareFact(DrhdKey, Category, "IOMMU 硬體單位", value, "個", Source,
            FactTrustLevel.Measured, false, at, drhds.Count,
            whole == 0 ? FactAvailability.Unknown : FactAvailability.Present,
            whole == 0
                ? "沒有任何單位宣告涵蓋全機——有些裝置的 DMA 不在這份表的轉譯範圍內"
                : null);
    }

    private static HardwareFact RmrrFact(DateTimeOffset at, IReadOnlyList<DmarRmrr> rmrrs)
    {
        if (rmrrs.Count == 0)
            return new HardwareFact(RmrrKey, Category, "保留記憶體區域（例外清單）",
                "沒有保留區域——這份表宣告的轉譯範圍內沒有例外", "個", Source,
                FactTrustLevel.Measured, false, at, 0, FactAvailability.Present,
                "沒有例外是好消息，但它只代表韌體沒有宣告例外，不代表沒有其他路徑繞過轉譯");

        // 計算每個區域的大小，讓「洞有多大」變成一個可讀的數字。
        var parts = rmrrs.Select(r =>
        {
            ulong size = r.Limit >= r.BaseAddress ? r.Limit - r.BaseAddress + 1 : 0;
            string devices = r.DeviceScopes.Count == 0
                ? "無裝置範圍"
                : $"{r.DeviceScopes.Count} 段裝置";
            return $"0x{r.BaseAddress:X}–0x{r.Limit:X}（{size / (1024.0 * 1024.0):0.0} MB、{devices}）";
        });

        return new HardwareFact(RmrrKey, Category, "保留記憶體區域（例外清單）",
            string.Join("；", parts), "個", Source, FactTrustLevel.Measured, false, at,
            rmrrs.Count, FactAvailability.Unknown,
            $"{rmrrs.Count} 個保留區域是 IOMMU 保護的例外——這些區域的 DMA 不受該保護，"
            + "這是設計上留的洞（典型是舊式 USB／顯示控制器需要固定實體位址）；"
            + "OS 可能另外處理它們，這一項只陳述韌體宣告了例外");
    }

    private static HardwareFact AtsrFact(DateTimeOffset at, IReadOnlyList<DmarAtsr> atsrs)
    {
        if (atsrs.Count == 0)
            return new HardwareFact(AtsrKey, Category, "位址轉譯服務",
                "沒有宣告任何 ATS 裝置範圍", "個", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.NotApplicable,
                "沒有 ATS 宣告是常態——這一項只在使用者空間驅動或 SR-IOV 環境才需要");

        string value = string.Join("；", atsrs.Select(a =>
            $"段 {a.Segment}、{a.DeviceScopes.Count} 段裝置"
            + ((a.Flags & 1) != 0 ? "（允許 ATS）" : "（不允許 ATS）")));

        return new HardwareFact(AtsrKey, Category, "位址轉譯服務", value, "個", Source,
            FactTrustLevel.Measured, false, at, atsrs.Count, FactAvailability.Present,
            "ATS 允許裝置自行快取轉譯結果——它在效能上是好事，但快取的失效必須正確處理");
    }

    private static HardwareFact CoverageFact(DateTimeOffset at, IReadOnlyList<DmarDrhd> drhds)
    {
        int scoped = drhds.Sum(d => d.DeviceScopes.Count);
        if (drhds.Count == 0)
            return new HardwareFact(CoverageKey, Category, "裝置範圍覆蓋",
                "沒有可計算的範圍", "段", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有硬體單位就沒有範圍可算");

        // 有單位涵蓋全機＝範圍完整；否則就是部分覆蓋。
        bool whole = drhds.Any(d => d.CoversWholeMachine);
        return new HardwareFact(CoverageKey, Category, "裝置範圍覆蓋",
            whole
                ? $"{drhds.Count} 個單位中有宣告涵蓋全機者，另有 {scoped} 段明確裝置範圍"
                : $"{drhds.Count} 個單位共 {scoped} 段明確裝置範圍，沒有任何單位涵蓋全機",
            "段", Source, FactTrustLevel.Derived, false, at, scoped,
            whole ? FactAvailability.Present : FactAvailability.Unknown,
            whole
                ? null
                : "沒有單位涵蓋全機時，這份表描述的是「部分覆蓋」——"
                  + "落在那些範圍之外的裝置，其 DMA 行為不在本表的陳述範圍內");
    }

    private static HardwareFact UnrecordedFact(DateTimeOffset at, IReadOnlyDictionary<ushort, int> counts)
    {
        var unknown = counts.Where(kv => AcpiDmar.DescribeType(kv.Key) is null).ToList();
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
