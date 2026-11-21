namespace XinSpect;

/// <summary>
/// 一條 WHEA 記憶體錯誤記錄的來源資料：事件 ID、時間、以及 CPER 記憶體節區的位元組。
/// </summary>
/// <param name="EventId">WHEA-Logger 的事件 ID（1／17／18／19／23／46／47 僅是 RawData 候選；是否記憶體須由節區 GUID 判定）。</param>
/// <param name="RecordedAt">事件時間。</param>
/// <param name="Section">記憶體節區的原始位元組；讀不到為 <c>null</c>。</param>
/// <param name="Why">讀不到時的原因。</param>
public sealed record WheaMemoryRecord(int EventId, DateTimeOffset RecordedAt, byte[]? Section, string? Why)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public WheaEventEnvelope? Envelope { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EventIdentity { get; init; }
    public int? DescriptorIndex { get; init; }
    public uint? RecordSeverity { get; init; }
    public uint? SectionSeverity { get; init; }
}

/// <summary>
/// WHEA 記憶體錯誤節區的事實（U10 第三圈 WH-0003＋WH-0004）。
/// </summary>
/// <remarks>Candidate event count and validated memory section count are separate. Installed manifest contracts and SDK structures are checked before decoding; hardware payload evidence is not implied.</remarks>
public static class WheaMemoryFactsService
{
    public const string Category = "硬體錯誤";
    public const string SummaryKey = "whea.mem.summary";
    public const string BoundaryKey = "whea.mem.boundary";
    public const string RecordPrefix = "whea.mem.rec.";

    private const string Source = "WHEA-Logger 事件 ＋ CPER 記憶體節區（cper.h 逐欄位移）";

    /// <summary>最多逐筆列幾條記錄（其餘只進彙總）。記錄爆炸時不讓事實集跟著爆炸。</summary>
    public const int MaxListed = 16;

    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<WheaMemoryRecord> records, DateTimeOffset at)
    {
        var facts = CollectRecords(records, at);
        if (records is not WheaMemoryReadResult result) return facts;

        string window = $"事件窗 {result.WindowStartUtc:O} 至 {result.WindowEndUtc:O}";
        return facts.Select(fact =>
        {
            if (fact.Key != SummaryKey) return fact;
            if (result.Status == WheaMemoryReadStatus.Complete)
                return fact.Availability == FactAvailability.Present
                    ? fact with { Value = fact.Value + "・" + window }
                    : fact with { UnavailableReason = fact.UnavailableReason + "・" + window };
            var availability = result.Status switch
            {
                WheaMemoryReadStatus.InsufficientPrivilege => FactAvailability.InsufficientPrivilege,
                WheaMemoryReadStatus.Truncated => FactAvailability.Unknown,
                _ => FactAvailability.ReadError,
            };
            string reason = $"列舉狀態 {result.Status}；保留 {result.Count} 筆候選事件，非完整總數。"
                + window + "。" + result.Reason;
            return fact with { Availability = availability, NumericValue = null,
                Value = reason + "・" + fact.Value, UnavailableReason = reason + "・" + fact.UnavailableReason };
        }).ToArray();
    }

    private static IReadOnlyList<HardwareFact> CollectRecords(IReadOnlyList<WheaMemoryRecord> records, DateTimeOffset at)
    {
        if (records.Count == 0)
            return
            [
                new HardwareFact(SummaryKey, Category, "WHEA 記憶體錯誤彙總", "", "筆", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                    "這一輪沒有任何 WHEA 記憶體記錄可解——**「沒有記錄」與「沒有記憶體錯誤」是兩件事**："
                    + "事件記錄有保留期與輪替，而且記憶體錯誤若被韌體吞掉就不會有事件"),
                BoundaryFact(at),
            ];

        int eventCount = records.Count;
        int invalidEnvelopes = records.Count(r => r.Envelope is { Valid: false });
        int withoutMemory = records.Count(r => r.Envelope is { Valid: true } e && e.Cper!.Sections.Count == 0);
        var expanded = new List<WheaMemoryRecord>();
        foreach (var original in records)
        {
            if (original.Envelope is not { } envelope) { expanded.Add(original); continue; }
            if (!envelope.Valid || envelope.Cper!.Sections.Count == 0)
            {
                expanded.Add(original with { Section = null, Why = envelope.Reason ?? "已驗證 CPER 未含記憶體節區" });
                continue;
            }
            foreach (var section in envelope.Cper.Sections)
                expanded.Add(original with { Section = section.Bytes, EventIdentity = envelope.EventIdentity,
                    DescriptorIndex = section.DescriptorIndex, RecordSeverity = section.RecordSeverity,
                    SectionSeverity = section.SectionSeverity });
        }
        int sectionCount = expanded.Count(r => r.Section is not null);
        string envelopeNotes = $"・記憶體節區 {sectionCount} 筆；外層未通過驗證 {invalidEnvelopes} 筆事件；已驗證但無記憶體節區 {withoutMemory} 筆事件";
        var rejectedReasons = records.Where(r => r.Envelope is { Valid: false })
            .Select(r => SummaryReason(r.Envelope!.Reason ?? "事件外層未通過驗證")).Distinct().Take(4).ToArray();
        if (rejectedReasons.Length > 0) envelopeNotes += "・未解原因：" + string.Join("；", rejectedReasons);
        records = expanded;

        // 有記錄但**一筆都解不開**（節區讀不到／太短）：這是本版最可能的真實情形，
        // 因為事件 data 段的外層包裝本版不拆。那時的彙總必須說「N 筆事件、0 筆可解」，
        // 而不是印一句「N 筆 WHEA 記憶體記錄：」後面接空白。
        if (records.All(r => r.Section is null))
            return
            [
                new HardwareFact(SummaryKey, Category, "WHEA 記憶體錯誤彙總", "", "筆", Source,
                    FactTrustLevel.Reported, false, at, eventCount,
                    FactAvailability.Present, null) with
                {
                    Value = $"{eventCount} 筆 WHEA-Logger 事件落在時間窗內（最近 {WheaMemoryRecordsReader.WindowDays} 天），"
                        + envelopeNotes + "・沒有可解的記憶體節區——原因：" + SummaryReason(records[0].Why ?? "未提供")
                        + "・所以本鍵只說得出「有幾筆事件」，說不出「哪一種錯、哪一條 DIMM」"
                        + "・事件 ID 的分佈（"
                        + string.Join("、", records.GroupBy(r => r.EventId)
                            .OrderByDescending(g => g.Count())
                            .Select(g => $"{g.Key}×{g.Count()}"))
                        + "）也只是入口，不是錯誤類型：真正的類型在節區裡",
                },
                BoundaryFact(at),
            ];

        var facts = new List<HardwareFact>();
        var byType = new Dictionary<byte, int>();
        int unparsable = 0, unreadable = 0, unclassified = 0, withAddress = 0, withModule = 0, listed = 0;
        var typeOrder = new List<byte>();

        foreach (var r in records.OrderByDescending(x => x.RecordedAt))
        {
            if (r.Section is null)
            {
                unreadable++;
                continue;
            }
            var s = WheaMemorySectionDecoder.Parse(r.Section);
            if (s is null)
            {
                unparsable++;
                continue;
            }

            if (s.HasErrorType)
            {
                if (!byType.ContainsKey(s.ErrorType)) typeOrder.Add(s.ErrorType);
                byType[s.ErrorType] = byType.TryGetValue(s.ErrorType, out int n) ? n + 1 : 1;
            }
            else unclassified++;
            if (s.HasPhysicalAddress) withAddress++;
            if (s.HasModule || s.HasModuleHandle) withModule++;

            if (listed < MaxListed)
            {
                listed++;
                facts.Add(RecordFact(listed, r, s, at));
            }
        }

        facts.Insert(0, SummaryFact(eventCount, sectionCount, envelopeNotes, byType, typeOrder, unreadable, unparsable, unclassified,
            withAddress, withModule, listed, at));
        facts.Add(BoundaryFact(at));
        return facts;
    }

    // Public reasons are bounded even for injected envelopes; raw XML/control characters cannot leak here.
    private static string SummaryReason(string reason) =>
        new string(reason.Where(c => !char.IsControl(c)).Take(240).ToArray());

    private static HardwareFact SummaryFact(int total, int sectionCount, string envelopeNotes, Dictionary<byte, int> byType, List<byte> order,
        int unreadable, int unparsable, int unclassified, int withAddress, int withModule, int listed, DateTimeOffset at)
    {
        var parts = new List<string>();
        foreach (var code in order)
        {
            string? name = WheaMemorySectionDecoder.ErrorTypeName(code);
            parts.Add(name is null
                ? $"0x{code:X2}（**本版沒有這個碼的出處，不命名**）×{byType[code]}"
                : $"{name} 0x{code:X2}×{byType[code]}");
        }

        var notes = new List<string>();
        if (unclassified > 0) notes.Add($"{unclassified} 筆的錯誤類型未確認（ValidBits bit14 未設，不分類）");
        if (unreadable > 0) notes.Add($"{unreadable} 筆的節區讀不到");
        if (unparsable > 0) notes.Add($"{unparsable} 筆的節區不足 {WheaMemorySectionDecoder.SectionBytes} 位元組（不猜欄位）");
        if (listed < sectionCount - unparsable)
            notes.Add($"只逐筆列出最近 {listed} 筆，其餘只進這個彙總");

        string value = $"{total} 筆 WHEA-Logger 候選事件" + envelopeNotes + $"・節區錯誤類型分佈：{string.Join("、", parts)}"
            + $"・其中 {withAddress} 筆韌體有給實體位址、{withModule} 筆有給模組資訊"
            + (notes.Count > 0 ? "・" + string.Join("；", notes) : "")
            + "・分佈僅計入通過驗證的記憶體節區；候選事件不等同實體記憶體故障。"
            + "次數要跟保留期一起看（見 whea.mem.boundary）";

        // 數字只放「總筆數」——它是唯一由本服務數出來的量。
        return new HardwareFact(SummaryKey, Category, "WHEA 記憶體錯誤彙總", value, "筆", Source,
            FactTrustLevel.Measured, false, at, total);
    }

    private static HardwareFact RecordFact(int index, WheaMemoryRecord r, WheaMemorySection s, DateTimeOffset at)
    {
        var faces = new List<string>();
        if (r.RecordSeverity is { } recordSeverity) faces.Add($"RecordSeverity {recordSeverity}");
        if (r.SectionSeverity is { } sectionSeverity) faces.Add($"SectionSeverity {sectionSeverity}");
        if (s.HasErrorType)
        {
            string name = WheaMemorySectionDecoder.ErrorTypeName(s.ErrorType)
                ?? $"**0x{s.ErrorType:X2}——本版沒有這個碼的出處，不命名**";
            string hint = WheaMemorySectionDecoder.ErrorTypeHint(s.ErrorType) is { } h ? $"（{h}）" : "";
            faces.Add($"類型 {name}{hint}");
        }
        else faces.Add("類型：**韌體沒給**（ValidBits bit14 未設，不分類）");

        faces.Add(s.HasPhysicalAddress
            ? $"實體位址 0x{s.PhysicalAddress:X}"
            : "實體位址：**韌體沒給**（ValidBits bit1 未設）——不是「位址是 0」");
        if (s.HasPhysicalAddressMask) faces.Add($"位址遮罩 0x{s.PhysicalAddressMask:X}");
        if (s.HasNode) faces.Add($"NUMA 節點 {s.Node}");
        if (s.HasCard) faces.Add($"card {s.Card}");
        if (s.HasModule) faces.Add($"模組 {s.Module}");
        else if (!s.HasModuleHandle)
            faces.Add("模組：**韌體沒給**——不能指到某條 DIMM，只能說「有記憶體錯誤」");
        if (s.HasBank) faces.Add($"bank {s.Bank}");
        if (s.HasDevice) faces.Add($"device {s.Device}");
        if (s.HasRow) faces.Add($"row {s.Row}");
        if (s.HasColumn) faces.Add($"column {s.Column}");
        faces.Add(s.HasBitPosition
            ? $"出錯位元 {s.BitPosition}"
            : "出錯位元：**韌體沒給**");
        if (s.HasRequesterId) faces.Add($"RequesterId 0x{s.RequesterId:X16}");
        if (s.HasResponderId) faces.Add($"ResponderId 0x{s.ResponderId:X16}");
        if (s.HasTargetId) faces.Add($"TargetId 0x{s.TargetId:X16}");
        if (s.HasRankNumber) faces.Add($"rank {s.RankNumber}");
        if (s.HasCardHandle) faces.Add($"SMBIOS card handle 0x{s.CardHandle:X4}");
        if (s.HasModuleHandle) faces.Add($"SMBIOS 模組 handle 0x{s.ModuleHandle:X4}");
        if (s.HasErrorStatus)
            faces.Add($"ErrorStatus 0x{s.ErrorStatusRaw:X16}（type 0x{s.ErrorStatusType:X2}、{s.ErrorStatusFlags}）");

        string value = $"{r.RecordedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z 事件 {r.EventId}：{string.Join("・", faces)}"
            + "・界線：這是**單筆記錄的自述**，不判斷該換哪一條 RAM——"
            + "那要累積次數與同型號基線，兩者都不在這一筆裡";

        string identity = r.EventIdentity is { } eventId && r.DescriptorIndex is { } descriptor
            ? "e" + eventId + ".s" + descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : index.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        return new HardwareFact(RecordPrefix + identity,
            Category, $"WHEA 記憶體記錄 {index}", value, "", Source, FactTrustLevel.Reported,
            s.HasPhysicalAddress || s.HasRequesterId || s.HasResponderId || s.HasTargetId, at, null,
            FactAvailability.Present, null);
    }

    private static HardwareFact BoundaryFact(DateTimeOffset at) =>
        new(BoundaryKey, Category, "WHEA 記憶體節區的界線宣告",
            "① 本版**只解記憶體節區**（WHEA_MEMORY_ERROR_SECTION）：處理器節區、PCIe 節區、"
            + "韌體錯誤資訊參考都不在範圍；② **Extended 位元組與擴充欄位不解**——"
            + "本版未核定其位義，不據此推論另有附加節區；錯誤類型不能代替節區嚴重度或維修證據；"
            + "③ 每一欄都先看 ValidBits：**韌體沒填的欄位不印成 0**，"
            + "因為「Module 0」與「韌體沒說哪一條」在處置上完全是兩件事；"
            + "④ 「0 筆」不等於「沒有記憶體錯誤」：事件記錄有保留期，"
            + "而韌體吞掉的錯誤根本不會變成事件；⑤ **本服務不判斷該換哪一條 RAM**——"
            + "它只把韌體說的話逐欄攤開，累積次數與同型號基線是別把鍵的事",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);
}
