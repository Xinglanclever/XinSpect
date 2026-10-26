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
/// <c>pci.dev.*</c> 的值末尾有 <see cref="PciIdLookup.OriginClausePrefix"/> 那段兩段式出處說明
/// （第 284 輪起），<c>nic.mac.*</c> 的值裡含 <c>OuiKnowledge</c> 的廠商名。
/// 這裡只做「讀回既有欄位並分類」——<b>不重新查表、不猜</b>。
/// </para>
///
/// <para>
/// <b>分類依據是常數不是自然語言：</b>兩個方向都以
/// <see cref="PciIdLookup.CuratedToken"/>／<see cref="PciIdLookup.RegistryToken"/>／
/// <see cref="PciIdLookup.UnlistedToken"/> 為準。相鄰的中文不是契約——
/// 改一個字就會讓統計安靜地改口，而統計看起來永遠正常。
/// </para>
/// </summary>
public static class KnowledgeLookupCollector
{
    /// <summary>
    /// 從既有事實集回收 PCI 名稱的統計。<b>一次掃描、兩個單位</b>：
    /// <c>Devices</c> 逐裝置（給 <c>kp.miss</c> 的「幾次查詢、幾次查不到」），
    /// <c>Names</c> 逐名稱段（給 <c>pci.id</c> 的三層出處）。
    ///
    /// <para>
    /// <b>為什麼要有兩個單位而不是挑一個：</b>「這台機器有幾張卡的名字沒對到」
    /// 與「這些名字分別是誰說的」是兩個問題。硬用一個數字回答兩個問題，
    /// 就是先前那個缺陷的形狀——一張卡只要廠商認得就算命中，
    /// 而從兩萬多筆社群登錄檔撈出來的名字，在統計上與本專案核對過的 15 家一模一樣。
    /// </para>
    /// </summary>
    public static (KnowledgeLookupReport.LookupStats Devices, KnowledgeLookupReport.PciNameStats Names)
        CollectPci(IEnumerable<HardwareFact> facts)
    {
        int curated = 0, registry = 0, unlisted = 0, unparsable = 0;
        int units = 0, unitsHit = 0, unitsMissed = 0, unitsUnparsable = 0;

        foreach (var f in facts.Where(f => f.Key.StartsWith("pci.dev.", StringComparison.Ordinal)))
        {
            units++;
            var one = ClassifyPciValue(f.Value ?? "");
            curated += one.Curated;
            registry += one.Registry;
            unlisted += one.Unlisted;
            unparsable += one.Unclassifiable;

            // 裝置級：沒有出處段落＝無從判定（不是命中）；有任何一段無法分類或未收錄＝查不到。
            if (one.HasClause is false || one.Unclassifiable > 0) unitsUnparsable++;
            else if (one.Unlisted > 0) unitsMissed++;
            else unitsHit++;
        }

        var devices = new KnowledgeLookupReport.LookupStats(
            units, unitsHit, unitsMissed, unitsUnparsable, []);
        return (devices, new KnowledgeLookupReport.PciNameStats(curated, registry, unlisted, unparsable));
    }

    /// <summary>一條 <c>pci.dev.*</c> 值的分類結果。</summary>
    private readonly record struct PciValueTally(
        bool HasClause, int Curated, int Registry, int Unlisted, int Unclassifiable);

    /// <summary>
    /// 把一條 <c>pci.dev.*</c> 的值拆成名稱段並各自歸類。
    ///
    /// <para>
    /// <b>沒有出處說明的值算「無法分類」——不是命中。</b>無從分類的數字不能充當已確認；
    /// 而「有出處段落但其中一段讀不懂」也必須落在無法分類，不能併進未收錄：
    /// 「表沒收錄這一筆」與「我們的分類程式看不懂自己寫的字」是兩件事。
    /// </para>
    /// </summary>
    private static PciValueTally ClassifyPciValue(string value)
    {
        int at = value.IndexOf(PciIdLookup.OriginClausePrefix, StringComparison.Ordinal);
        if (at < 0) return new PciValueTally(false, 0, 0, 0, 1);

        int curated = 0, registry = 0, unlisted = 0, bad = 0;
        string clause = value[(at + PciIdLookup.OriginClausePrefix.Length)..];
        foreach (string segment in clause.Split('、'))
        {
            switch (ClassifySegment(segment))
            {
                case PciIdLookup.NameOrigin.Curated: curated++; break;
                case PciIdLookup.NameOrigin.Registry: registry++; break;
                case PciIdLookup.NameOrigin.Unlisted: unlisted++; break;
                default: bad++; break;
            }
        }
        if (curated + registry + unlisted == 0) bad = 1;   // 有段落卻一段都讀不懂
        return new PciValueTally(true, curated, registry, unlisted, bad);
    }

    /// <summary>一段出處說明（`廠商=手工表`／`裝置=登錄檔 2026.10.01`）→ 出處層。</summary>
    private static PciIdLookup.NameOrigin? ClassifySegment(string segment)
    {
        if (segment.Contains(PciIdLookup.UnlistedToken, StringComparison.Ordinal)) return PciIdLookup.NameOrigin.Unlisted;
        if (segment.Contains(PciIdLookup.CuratedToken, StringComparison.Ordinal)) return PciIdLookup.NameOrigin.Curated;
        if (segment.Contains(PciIdLookup.RegistryToken, StringComparison.Ordinal)) return PciIdLookup.NameOrigin.Registry;
        return null;
    }

    /// <summary>
    /// 從既有事實集回收 OUI 查詢的統計（第 285 輪改成看結構化欄位與固定標記）。
    ///
    /// <para>
    /// <b>這裡修掉一個真的會說謊的缺陷：</b>舊寫法比對「值裡有沒有出現某句中文」，
    /// 而 WMI 查詢失敗時值欄是<b>空字串</b>（狀態在 <c>Availability</c> 上）……
    /// 於是「一條網卡都沒讀到」被算成<b>一次命中</b>，<c>net.oui</c> 印「1 次查詢全部命中」。
    /// 現在分三關：①<b>查詢有沒有發生</b>看 <c>Availability</c>（非 Present 一律不進統計，
    /// 因為那不是查詢結果）；②出處看 <see cref="OuiKnowledge.OriginClausePrefix"/> 那段標記；
    /// ③有值卻沒有那段標記＝無從分類，<b>不是命中</b>。
    /// </para>
    /// </summary>
    public static KnowledgeLookupReport.LookupStats CollectOui(IEnumerable<HardwareFact> facts)
    {
        var lookups = new List<(KnowledgeLookupReport.LookupOutcome, string)>();
        foreach (var f in facts.Where(f => f.Key.StartsWith("nic.mac.", StringComparison.Ordinal)))
        {
            // 讀不到／沒有網卡：查詢沒有發生。失敗本身由那一把鍵自己申報，不在彙總裡重複算一筆。
            if (f.Availability != FactAvailability.Present) continue;

            string value = f.Value ?? "";
            lookups.Add((ClassifyOuiValue(value), value));
        }
        return KnowledgeLookupReport.Summarize(lookups);
    }

    /// <summary>一條 <c>nic.mac.*</c> 值的查詢結果——讀的是值末尾那段固定標記。</summary>
    private static KnowledgeLookupReport.LookupOutcome ClassifyOuiValue(string value)
    {
        int at = value.IndexOf(OuiKnowledge.OriginClausePrefix, StringComparison.Ordinal);
        if (at < 0) return KnowledgeLookupReport.LookupOutcome.Unparsable;   // 沒有出處段：無從分類

        string clause = value[(at + OuiKnowledge.OriginClausePrefix.Length)..];
        if (clause.Contains(OuiKnowledge.UnparsableToken, StringComparison.Ordinal))
            return KnowledgeLookupReport.LookupOutcome.Unparsable;
        if (clause.Contains(OuiKnowledge.UnlistedToken, StringComparison.Ordinal))
            return KnowledgeLookupReport.LookupOutcome.NotListed;
        if (clause.Contains(OuiKnowledge.CuratedToken, StringComparison.Ordinal))
            return KnowledgeLookupReport.LookupOutcome.Hit;
        return KnowledgeLookupReport.LookupOutcome.Unparsable;               // 有段落但讀不懂
    }

    /// <summary>知識表鍵字面（供掃描器看見；註冊由主協調者做）：SMART 廠商屬性對照。</summary>
    public const string SmartAttrLookupKey = "ata.smart.attr.lookup";

    /// <summary>
    /// 從 SMART 屬性列回收知識表查詢的統計（第 270 輪 SMART 知識包的消費端接線）。
    /// 只有名字含「廠商自訂」的列才走過知識表：名字是「廠商自訂（…）」＝命中；
    /// 原樣「廠商自訂」＝未收錄（廠商未收錄與 ID 未收錄對知識表而言同為缺口）。
    /// 既有對照表命中的 ID 不經知識表，<b>不計入</b>——這裡統計的是知識表那一條路徑，不是全部屬性。
    /// </summary>
    public static KnowledgeLookupReport.LookupStats CollectSmart(IEnumerable<SmartRow> rows)
    {
        var lookups = new List<(KnowledgeLookupReport.LookupOutcome, string)>();
        foreach (var r in rows)
        {
            if (!r.Name.Contains("廠商自訂", StringComparison.Ordinal)) continue;
            bool hit = r.Name.Contains("廠商自訂（", StringComparison.Ordinal);
            lookups.Add((hit ? KnowledgeLookupReport.LookupOutcome.Hit
                             : KnowledgeLookupReport.LookupOutcome.NotListed, r.Name));
        }
        return KnowledgeLookupReport.Summarize(lookups);
    }

    /// <summary>產生 <c>ata.smart.attr.lookup</c>。形狀比照 PCI／OUI 的覆蓋申報：命中→Present、有未收錄→Unknown（不是錯誤）、無查詢→NotApplicable。</summary>
    public static HardwareFact ToSmartFact(DateTimeOffset at, KnowledgeLookupReport.LookupStats stats)
    {
        const string name = "SMART 廠商屬性對照";
        const string source = "SMART 屬性知識表（離線，六家）";
        if (stats.Total == 0)
            return new HardwareFact(SmartAttrLookupKey, "知識與更新", name,
                "本輪沒有「廠商自訂」屬性需要對照", "次", source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有廠商自訂屬性，知識表未被查詢");

        string detail = stats.NotListed == 0 && stats.Unparsable == 0
            ? $"{stats.Total} 次查詢全部命中"
            : $"{stats.Total} 次查詢：命中 {stats.Hits}、未收錄 {stats.NotListed}"
              + (stats.MissedKeys.Count > 0
                  ? "；未收錄：" + string.Join("、", stats.MissedKeys.Take(10))
                  : "");
        var availability = stats.Total == stats.Hits ? FactAvailability.Present : FactAvailability.Unknown;
        string? reason = availability == FactAvailability.Present ? null
            : $"{stats.NotListed} 筆未收錄——如實顯示「廠商自訂」而不猜名稱；要補的是知識表";
        return new HardwareFact(SmartAttrLookupKey, "知識與更新", name, detail, "次", source,
            FactTrustLevel.Derived, false, at, stats.Hits, availability, reason);
    }

    /// <summary>
    /// 第 274 輪：把統計接上<b>這一輪真的讀到的屬性列</b>的探針結果
    /// （<see cref="SmartLookupProbe.Result"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>為什麼不是在 <c>ToSmartFact(at, stats)</c> 裡就分三態：</b>那個版本只知道「知識表被查了幾次」，
    /// 不知道「為什麼沒有列」——沒有 ATA 碟、逾時、讀到了但零屬性，三者在統計上長得一模一樣
    /// （都是 0 次查詢），但在斷言上是<b>三件不同的事</b>。把探針結論一起傳進來，
    /// 那把鍵才說得出「是沒有對象」「是讀不到」還是「讀到了但沒有屬性」。
    /// </para>
    /// <para>
    /// <b>讀不到不編數字：</b>非 <see cref="SmartLookupProbe.ProbeStatus.Ok"/> 的三態一律
    /// <c>NumericValue = null</c>（<see cref="HardwareFact"/> 的不變式），統計只在 Ok 時成立。
    /// </para>
    /// </remarks>
    public static HardwareFact ToSmartFact(DateTimeOffset at, KnowledgeLookupReport.LookupStats stats,
        SmartLookupProbe.Result probe)
    {
        const string name = "SMART 廠商屬性對照";
        const string source = "SMART 屬性知識表（離線，六家）＋本輪 ATA SMART READ DATA 直讀";

        switch (probe.Status)
        {
            case SmartLookupProbe.ProbeStatus.ReadError:
                return new HardwareFact(SmartAttrLookupKey, "知識與更新", name,
                    "讀不到任何一顆 ATA 碟的 SMART 屬性列，知識表未被查詢", "次", source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    (probe.Reason ?? "讀不到，原因不明") + "；" + probe.CoverageNote);

            case SmartLookupProbe.ProbeStatus.NotApplicable:
                return new HardwareFact(SmartAttrLookupKey, "知識與更新", name,
                    "沒有可讀的 ATA 通道碟，知識表未被查詢", "次", source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    probe.CoverageNote);

            case SmartLookupProbe.ProbeStatus.Unknown:
                return new HardwareFact(SmartAttrLookupKey, "知識與更新", name,
                    "讀到了，但這顆碟沒有回報任何 SMART 屬性列——知識表未被查詢", "次", source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                    (probe.Reason ?? "碟回應了，但屬性表是空的") + "；" + probe.CoverageNote);
        }

        // Ok：統計走既有形狀（同一套判定），另外把「幾顆被略過、為什麼」寫進值裡——
        // 略過不是錯誤，但它是讀者判斷「這份統計涵蓋到哪裡」的必要資訊。
        var plain = ToSmartFact(at, stats);
        return new HardwareFact(plain.Key, plain.Category, plain.Name,
            plain.Value + "；" + probe.CoverageNote,
            plain.Unit, source, plain.Trust, plain.Sensitive, at,
            plain.NumericValue, plain.Availability, plain.UnavailableReason);
    }

    /// <summary>
    /// 第 274 輪：<b>本輪還沒讀過碟</b>時的 <c>ata.smart.attr.lookup</c>。
    /// </summary>
    /// <remarks>
    /// <b>為什麼要有這一把，而不是「沒讀就讓鍵消失」：</b>鍵消失與「讀了、只是沒有可對照的屬性」
    /// 在報告上長得一樣（都沒有這把鍵／都是 0 次），而前者是<b>這條收集鏈沒跑</b>。
    /// 沿用第 271 輪的判準：先讓鍵存在、並如實說出它現在是什麼狀態，不造假查詢次數。
    /// </remarks>
    public static HardwareFact ToSmartNotLoaded(DateTimeOffset at)
        => new(SmartAttrLookupKey, "知識與更新", "SMART 廠商屬性對照",
            "本輪尚未讀取 SMART 屬性列，知識表未被查詢", "次",
            "SMART 屬性知識表（離線，六家）", FactTrustLevel.Unknown, false, at,
            null, FactAvailability.Unknown,
            "載入點（EvidenceLabService.LoadSmartLookupStats 由 EvidenceCollection.LoadUsermodeFacts 呼叫）"
            + "這一輪沒有跑：這不是「沒有廠商自訂屬性」，是「沒有讀」");
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
