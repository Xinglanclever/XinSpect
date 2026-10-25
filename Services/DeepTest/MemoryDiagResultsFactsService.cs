using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>
/// U10-TS-0051「Windows 記憶體診斷結果讀取」（唯讀；資料面跨 OS 域）：<c>os.memdiag.results.cross</c>。
/// </summary>
/// <remarks>
/// <para>
/// 來源：事件檢視器 <c>Microsoft-Windows-MemoryDiagnostics-Results/Operational</c> 頻道。
/// 已知事件 ID（Windows Memory Diagnostic 結果提供者）：1101＝測試完成、未偵測到錯誤；
/// 1102＝測試完成、偵測到錯誤。其餘 ID 照實列出計數、<b>不判決</b>。
/// </para>
/// <para>
/// 誠實界線：「頻道不存在」（此 Windows 安裝沒有這條結果記錄）、「頻道讀取失敗」、
/// 「頻道在但零事件」（未曾執行或已被清除）是三件不同的事，分別落到
/// <see cref="FactAvailability.NotSupported"/>／<see cref="FactAvailability.ReadError"/>／
/// 照實的 Present 值——不拿「0 筆」冒充「跑過沒錯誤」。
/// </para>
/// </remarks>
public static class MemoryDiagResultsFactsService
{
    private const string Category = "記憶體";
    public const string Key = "os.memdiag.results.cross";
    public const string ChannelName = "Microsoft-Windows-MemoryDiagnostics-Results/Operational";
    public const string Source = "Windows 事件檢視器 Microsoft-Windows-MemoryDiagnostics-Results/Operational（唯讀）";

    /// <summary>一筆結果事件：ID、時間、首行內文。</summary>
    public sealed record MemDiagEvent(int Id, DateTimeOffset Time, string Message);

    /// <summary>收集記憶體診斷結果。測試以注入事件清單取代真實事件讀取。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<string, IReadOnlyList<MemDiagEvent>>? readChannel = null)
    {
        IReadOnlyList<MemDiagEvent> events;
        try { events = (readChannel ?? RealRead)(ChannelName); }
        catch (EventLogNotFoundException)
        {
            return [Fail(at, FactAvailability.NotSupported,
                "結果頻道不存在（此 Windows 安裝未提供記憶體診斷結果記錄）")];
        }
        catch (Exception ex)
        {
            return [Fail(at, FactAvailability.ReadError, "事件頻道讀取失敗：" + ex.Message)];
        }
        return [BuildFact(at, events)];
    }

    /// <summary>純函式：事件清單 → 事實（供測試直接驗解讀與六態傳播）。</summary>
    public static HardwareFact BuildFact(DateTimeOffset at, IReadOnlyList<MemDiagEvent> events)
    {
        var (value, availability, reason) = Summarize(events);
        return new HardwareFact(Key, Category, "Windows 記憶體診斷結果", value, "", Source,
            availability == FactAvailability.Present ? FactTrustLevel.Measured : FactTrustLevel.Unknown,
            false, at, null, availability, reason);
    }

    /// <summary>
    /// 純函式：結果判讀。1101／1102 有官方語義（無錯誤／偵測到錯誤）；
    /// 零事件是 Present 的事實值（「未曾執行或已清除」），不是 NotSupported——頻道讀到了。
    /// </summary>
    public static (string Value, FactAvailability Availability, string? UnavailableReason) Summarize(
        IReadOnlyList<MemDiagEvent> events)
    {
        if (events.Count == 0)
            return ("零筆結果事件：本機未留下任何記憶體診斷結果（未曾執行，或事件已被清除）",
                FactAvailability.Present, null);

        int ok = events.Count(e => e.Id == 1101);
        int bad = events.Count(e => e.Id == 1102);
        var last = events.Max(e => e.Time);
        string verdict = bad > 0 ? "最近一次診斷偵測到錯誤（1102）"
            : ok > 0 ? "最近一次診斷未偵測到錯誤（1101）"
            : "有結果事件，但沒有 1101／1102 可判讀";
        var others = events.Where(e => e.Id is not (1101 or 1102))
            .GroupBy(e => e.Id).OrderBy(g => g.Key)
            .Select(g => $"#{g.Key}×{g.Count()}");
        string value = $"{verdict}；共 {events.Count} 筆（1101×{ok}、1102×{bad}"
            + (others.Any() ? "、其他 " + string.Join("・", others) : "")
            + $"),最後一次 {last:yyyy-MM-dd HH:mm:ss} UTC";
        return (value, FactAvailability.Present, null);
    }

    // ── 真實事件讀取通路（薄；測試以注入委派取代）──

    private static IReadOnlyList<MemDiagEvent> RealRead(string channel)
    {
        var list = new List<MemDiagEvent>();
        var query = new EventLogQuery(channel, PathType.LogName,
            "*[System[TimeCreated[timediff(@SystemTime) <= 7776000000]]]"); // 近 90 天
        using var reader = new EventLogReader(query);
        while (list.Count < 200)
        {
            using EventRecord? rec = reader.ReadEvent();
            if (rec is null) break;
            string message;
            try { message = rec.FormatDescription() ?? ""; }
            catch { message = "（事件內文無法格式化：提供者資訊缺失）"; }
            var firstLine = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                is { Length: > 0 } lines ? lines[0] : "(無內文)";
            list.Add(new MemDiagEvent(rec.Id, rec.TimeCreated ?? DateTimeOffset.MinValue, firstLine));
        }
        return list;
    }

    private static HardwareFact Fail(DateTimeOffset at, FactAvailability availability, string reason) =>
        new(Key, Category, "Windows 記憶體診斷結果", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
