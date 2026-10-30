namespace XinSpect;

/// <summary>
/// 一條 WHEA 記憶體錯誤記錄的來源資料：事件 ID、時間、以及 CPER 記憶體節區的位元組。
/// </summary>
/// <param name="EventId">WHEA-Logger 的事件 ID（17／19／23／46／47 都可能是記憶體錯誤）。</param>
/// <param name="RecordedAt">事件時間。</param>
/// <param name="Section">記憶體節區的原始位元組；讀不到為 <c>null</c>。</param>
/// <param name="Why">讀不到時的原因。</param>
public sealed record WheaMemoryRecord(int EventId, DateTimeOffset RecordedAt, byte[]? Section, string? Why);

/// <summary>
/// WHEA 記憶體錯誤節區的事實（U10 第三圈 WH-0003＋WH-0004）。
/// </summary>
/// <remarks>
/// <para>
/// <b>回答什麼：</b>既有 <c>os.ev.whea.*</c> 只答「幾筆事件」。這一支答三件事：
/// <b>哪一種錯</b>（單一位元／多位／abort…）、<b>哪一條 DIMM</b>（模組號與 SMBIOS handle）、
/// 以及<b>韌體說哪些欄位有意義</b>。
/// </para>
/// <para>
/// <b>第三件最重要。</b>CPER 的每一欄都有一個 ValidBits 位元，韌體可以說「這個欄位我沒填」。
/// 不讀 ValidBits 就印欄位值，等於把「沒填」當成「填了 0」——那會讓「Module 0」看起來
/// 像「第 0 條 DIMM」，而實際上是「韌體沒說」。這個專案最在意的那類錯誤正是這一種。
/// </para>
/// <para>
/// <b>沒有新讀取面的第一版：</b>記錄由呼叫端提供。生產端要從事件記錄的 data 段取 CPER，
/// 而那一層的取用方式（<c>EventLogRecord.Properties</c> 的二進位 blob 佈局）本版沒有
/// 可核的出處，所以<b>本版不自己去拆</b>——呼叫端傳不進來就標 <c>NotApplicable</c>，
/// 並寫明缺的是什麼。這比拆錯位元組好。
/// </para>
/// </remarks>
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
        if (records.Count == 0)
            return
            [
                new HardwareFact(SummaryKey, Category, "WHEA 記憶體錯誤彙總", "", "筆", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                    "這一輪沒有任何 WHEA 記憶體記錄可解——**「沒有記錄」與「沒有記憶體錯誤」是兩件事**："
                    + "事件記錄有保留期與輪替，而且記憶體錯誤若被韌體吞掉就不會有事件"),
                BoundaryFact(at),
            ];

        // 有記錄但**一筆都解不開**（節區讀不到／太短）：這是本版最可能的真實情形，
        // 因為事件 data 段的外層包裝本版不拆。那時的彙總必須說「N 筆事件、0 筆可解」，
        // 而不是印一句「N 筆 WHEA 記憶體記錄：」後面接空白。
        if (records.All(r => r.Section is null))
            return
            [
                new HardwareFact(SummaryKey, Category, "WHEA 記憶體錯誤彙總", "", "筆", Source,
                    FactTrustLevel.Reported, false, at, records.Count,
                    FactAvailability.Present, null) with
                {
                    Value = $"{records.Count} 筆 WHEA-Logger 事件落在時間窗內（最近 {WheaMemoryRecordsReader.WindowDays} 天），"
                        + "但**一筆的節區都解不開**——原因：" + (records[0].Why ?? "未提供")
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
        int unparsable = 0, unreadable = 0, withAddress = 0, withModule = 0, listed = 0;
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

            if (!byType.ContainsKey(s.ErrorType)) typeOrder.Add(s.ErrorType);
            byType[s.ErrorType] = byType.TryGetValue(s.ErrorType, out int n) ? n + 1 : 1;
            if (s.HasPhysicalAddress) withAddress++;
            if (s.HasModule || s.HasModuleHandle) withModule++;

            if (listed < MaxListed)
            {
                listed++;
                facts.Add(RecordFact(listed, r, s, at));
            }
        }

        facts.Insert(0, SummaryFact(records.Count, byType, typeOrder, unreadable, unparsable,
            withAddress, withModule, listed, at));
        facts.Add(BoundaryFact(at));
        return facts;
    }

    private static HardwareFact SummaryFact(int total, Dictionary<byte, int> byType, List<byte> order,
        int unreadable, int unparsable, int withAddress, int withModule, int listed, DateTimeOffset at)
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
        if (unreadable > 0) notes.Add($"{unreadable} 筆的節區讀不到");
        if (unparsable > 0) notes.Add($"{unparsable} 筆的節區不足 {WheaMemorySectionDecoder.SectionBytes} 位元組（不猜欄位）");
        if (listed < total - unreadable - unparsable)
            notes.Add($"只逐筆列出最近 {listed} 筆，其餘只進這個彙總");

        string value = $"{total} 筆 WHEA 記憶體記錄：{string.Join("、", parts)}"
            + $"・其中 {withAddress} 筆韌體有給實體位址、{withModule} 筆有給模組資訊"
            + (notes.Count > 0 ? "・" + string.Join("；", notes) : "")
            + "・這是**已發生的事**，不是預測：它說「這台機器回報過這些錯誤」，"
            + "不說「錯誤還會再發生」。次數要跟保留期一起看（見 whea.mem.boundary）";

        // 數字只放「總筆數」——它是唯一由本服務數出來的量。
        return new HardwareFact(SummaryKey, Category, "WHEA 記憶體錯誤彙總", value, "筆", Source,
            FactTrustLevel.Measured, false, at, total);
    }

    private static HardwareFact RecordFact(int index, WheaMemoryRecord r, WheaMemorySection s, DateTimeOffset at)
    {
        string name = WheaMemorySectionDecoder.ErrorTypeName(s.ErrorType)
            ?? $"**0x{s.ErrorType:X2}——本版沒有這個碼的出處，不命名**";
        string hint = WheaMemorySectionDecoder.ErrorTypeHint(s.ErrorType) is { } h ? $"（{h}）" : "";

        var faces = new List<string>();
        faces.Add($"類型 {name}{hint}");
        if (s.HasErrorType is false)
            faces.Add("**韌體沒有說 ErrorType 這個欄位有意義**（ValidBits bit14 未設）——這一格的值不該當成事實");

        faces.Add(s.HasPhysicalAddress
            ? $"實體位址 0x{s.PhysicalAddress:X}（遮罩 0x{s.PhysicalAddressMask:X}）"
            : "實體位址：**韌體沒給**（ValidBits bit1 未設）——不是「位址是 0」");

        if (s.HasModule || s.HasModuleHandle)
        {
            string handle = s.HasModuleHandle ? $"、SMBIOS 模組 handle 0x{s.ModuleHandle:X4}" : "";
            faces.Add($"模組 {s.Module}（card {s.Card}、bank {s.Bank}、device {s.Device}、rank {s.RankNumber}{handle}）");
        }
        else
        {
            faces.Add("模組：**韌體沒給**——不能指到某條 DIMM，只能說「有記憶體錯誤」");
        }

        faces.Add(s.HasBitPosition
            ? $"出錯位元 {s.BitPosition}（row {s.Row}、column {s.Column}）"
            : "出錯位元：**韌體沒給**");

        var statusBits = new List<string>();
        if (s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.Address)) statusBits.Add("Address");
        if (s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.Control)) statusBits.Add("Control");
        if (s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.Data)) statusBits.Add("Data");
        if (s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.Responder)) statusBits.Add("Responder");
        if (s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.Requester)) statusBits.Add("Requester");
        if (s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.FirstError)) statusBits.Add("FirstError");
        if (s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.Overflow)) statusBits.Add("Overflow");
        faces.Add($"ErrorStatus 0x{s.ErrorStatusRaw:X16}（type 0x{s.ErrorStatusType:X2}"
            + (statusBits.Count > 0 ? "、" + string.Join("/", statusBits) : "、沒有旗標") + "）");

        string value = $"{r.RecordedAt:yyyy-MM-dd HH:mm:ss}Z 事件 {r.EventId}：{string.Join("・", faces)}"
            + "・界線：這是**單筆記錄的自述**，不判斷該換哪一條 RAM——"
            + "那要累積次數與同型號基線，兩者都不在這一筆裡";

        return new HardwareFact(RecordPrefix + index.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
            Category, $"WHEA 記憶體記錄 {index}", value, "", Source, FactTrustLevel.Reported, false, at,
            s.HasPhysicalAddress ? (double)s.PhysicalAddress : null,
            FactAvailability.Present, null);
    }

    private static HardwareFact BoundaryFact(DateTimeOffset at) =>
        new(BoundaryKey, Category, "WHEA 記憶體節區的界線宣告",
            "① 本版**只解記憶體節區**（WHEA_MEMORY_ERROR_SECTION）：處理器節區、PCIe 節區、"
            + "韌體錯誤資訊參考都不在範圍；② **Extended（ACPI 6.4 的擴充記憶體節區）不解**——"
            + "它的欄位本版沒有可核的出處，硬解只會製造看起來很專業的錯誤；"
            + "③ 每一欄都先看 ValidBits：**韌體沒填的欄位不印成 0**，"
            + "因為「Module 0」與「韌體沒說哪一條」在處置上完全是兩件事；"
            + "④ 「0 筆」不等於「沒有記憶體錯誤」：事件記錄有保留期，"
            + "而韌體吞掉的錯誤根本不會變成事件；⑤ **本服務不判斷該換哪一條 RAM**——"
            + "它只把韌體說的話逐欄攤開，累積次數與同型號基線是別把鍵的事",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);
}
