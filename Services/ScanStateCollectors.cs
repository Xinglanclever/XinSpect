using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 知識表查詢的<b>真實統計</b>收集（ULTIMATE-1000 階段一 P1-039／P1-040 的接線）。
///
/// <para>
/// <b>為什麼不能只呼叫一次 <c>LookupOui("aa-bb-cc")</c> 就交差：</b>
/// 那把鍵要回答的是「這一輪的查詢命中幾次、未收錄幾筆」——
/// 呼叫一次假的值會得到一把看起來正常、實際上毫無意義的統計。
/// <b>那比不接線更糟</b>：不接線至少誠實地什麼都沒有。
/// </para>
///
/// <para>
/// 所以這一支從<b>既有事實集</b>裡把查詢結果收回來：
/// <c>pci.dev.*</c> 的值裡已經含 <c>PciKnowledge</c> 的描述（含「未收錄」字樣），
/// <c>nic.mac.*</c> 的值裡已經含 <c>OuiKnowledge</c> 的廠商名。
/// 這裡只做「讀回既有欄位並分類」——<b>不重新查表、不猜</b>。
/// </para>
/// </summary>
public static class KnowledgeLookupCollector
{
    /// <summary>從既有事實集回收 PCI 廠商查詢的統計。</summary>
    public static KnowledgeLookupReport.LookupStats CollectPci(IEnumerable<HardwareFact> facts)
    {
        var lookups = new List<(KnowledgeLookupReport.LookupOutcome, string)>();
        foreach (var f in facts.Where(f => f.Key.StartsWith("pci.dev.", StringComparison.Ordinal)))
        {
            string value = f.Value ?? "";
            // Bus0InventoryService 的值裡，可用性由「（未收錄）」與「讀取失敗」兩種字樣區分；
            // 其餘都算命中——這不是重新判斷，是讀回它當初寫下的結論。
            var outcome =
                value.Contains("未收錄", StringComparison.Ordinal) ? KnowledgeLookupReport.LookupOutcome.NotListed
                : value.Contains("讀取失敗", StringComparison.Ordinal) ? KnowledgeLookupReport.LookupOutcome.Unparsable
                : KnowledgeLookupReport.LookupOutcome.Hit;
            lookups.Add((outcome, value));
        }
        return KnowledgeLookupReport.Summarize(lookups);
    }

    /// <summary>從既有事實集回收 OUI 查詢的統計。</summary>
    public static KnowledgeLookupReport.LookupStats CollectOui(IEnumerable<HardwareFact> facts)
    {
        var lookups = new List<(KnowledgeLookupReport.LookupOutcome, string)>();
        foreach (var f in facts.Where(f => f.Key.StartsWith("nic.mac.", StringComparison.Ordinal)))
        {
            string value = f.Value ?? "";
            var outcome =
                value.Contains("未收錄", StringComparison.Ordinal) ? KnowledgeLookupReport.LookupOutcome.NotListed
                : value.Contains("無法解析", StringComparison.Ordinal) ? KnowledgeLookupReport.LookupOutcome.Unparsable
                : value.Contains("沒有實體網路介面", StringComparison.Ordinal) ? KnowledgeLookupReport.LookupOutcome.Unparsable
                : KnowledgeLookupReport.LookupOutcome.Hit;
            lookups.Add((outcome, value));
        }
        return KnowledgeLookupReport.Summarize(lookups);
    }
}

/// <summary>
/// 知識包的宣告輸出（P1-033 – P1-038 的接線）。
/// <b>輸入是載入結果</b>——它與 <c>KnowledgePackService.Collect</c> 同一次載入，
/// 不重跑檔案讀取。
/// </summary>
public static class KnowledgePackReportCollector
{
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, KnowledgePackLoad load,
        int swaps, int recordedSwaps, int lookups, int misses)
        =>
        [
            KnowledgePackReport.ToVersionFact(at, load),
            KnowledgePackReport.ToDigestFact(at, load),
            KnowledgePackReport.ToSnapshotFact(at, load),
            KnowledgePackReport.ToChannelFact(at),          // 沒有更新通道是設計，不是缺陷
            KnowledgePackReport.ToSwapAuditFact(at, swaps, recordedSwaps, null),
            KnowledgePackReport.ToMissFact(at, lookups, misses, []),
        ];
}

/// <summary>
/// 域知識包的宣告輸出（P1-041 – P1-043 的接線）。
/// <b>每一把都需要本機側的真實值</b>——讀不到時三把鍵都回「本機讀不到」，
/// 而不是回一個看起來像「沒問題」的空白。
/// </summary>
public static class DomainKnowledgeCollector
{
    /// <summary>從既有事實集回收微碼修訂版；回收不到就是讀不到。</summary>
    public static string? MicrocodeRevision(IEnumerable<HardwareFact> facts) =>
        facts.FirstOrDefault(f => f.Key == "cpu.microcode" && f.Availability == FactAvailability.Present)?.Value;

    /// <summary>從既有事實集回收 dbx 的 SVN；解析不出來就是讀不到。</summary>
    public static int? DbxSvn(IEnumerable<HardwareFact> facts)
    {
        var f = facts.FirstOrDefault(x => x.Key == "secure.dbx.svn" && x.Availability == FactAvailability.Present);
        if (f?.NumericValue is { } v) return (int)v;
        return int.TryParse(f?.Value, out int parsed) ? parsed : null;
    }

    /// <summary>從既有事實集回收電源型號。</summary>
    public static string? PsuModel(IEnumerable<HardwareFact> facts) =>
        facts.FirstOrDefault(f => f.Key.StartsWith("psu.", StringComparison.Ordinal)
                               && f.Availability == FactAvailability.Present)?.Value;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IEnumerable<HardwareFact> facts, IReadOnlyList<string>? microcodeCves = null)
    {
        var list = facts.ToList();
        string? rev = MicrocodeRevision(list);
        int? svn = DbxSvn(list);
        string? psu = PsuModel(list);

        return
        [
            DomainKnowledgeReport.ToMicrocodeCveFact(at, rev,
                rev is null ? DomainKnowledgeReport.Coverage.NoLocalValue
                            : DomainKnowledgeReport.Coverage.NotCovered,
                microcodeCves ?? []),
            DomainKnowledgeReport.ToDbxFact(at, svn, knownLatestSvn: null),
            DomainKnowledgeReport.ToPsuFact(at, psu, knownWattage: null),
        ];
    }
}

/// <summary>
/// 出網政策與資料主權的輸出（P1-049／P1-050／P1-051／P1-054 的接線）。
/// </summary>
public static class EgressReportCollector
{
    /// <summary>本專案實際登記的出網通路——與 docs/DATA-SOVEREIGNTY.md §2 同一份清單。</summary>
    public static IReadOnlyList<EgressPolicy.AllowEntry> Registered { get; } =
    [
        new("送出使用者回饋", EgressPolicy.TriggerKind.UserInitiated, "使用者按下送出，內容由他本人確認"),
        new("AI 說明", EgressPolicy.TriggerKind.UserInitiated, "使用者主動提問；未按不送"),
        new("網路測速", EgressPolicy.TriggerKind.UserInitiated, "量測對象就是網路本身，由使用者指定"),
        new("延遲量測", EgressPolicy.TriggerKind.UserInitiated, "使用者指定目標位址"),
        new("檢查更新", EgressPolicy.TriggerKind.UserInitiated, "只查版本號，不上傳本機任何資料"),
    ];

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyCollection<HardwareFact> facts, int leftMachine, IReadOnlyList<string> destinations,
        SensitivityPolicy.Level declaredClass)
    {
        int total = facts.Count;
        return
        [
            EgressPolicy.ToAllowlistFact(at, Registered),
            // 本輪沒有觸發任何出網——但「沒有觸發」與「沒有通路」分開講。
            EgressPolicy.ToTriggerFact(at, Registered.Count, []),
            DataSovereigntyReport.ToEgressReportFact(at, total, total - leftMachine, leftMachine, destinations),
            DataSovereigntyReport.ToReportClassFact(at, facts, declaredClass),
        ];
    }
}
