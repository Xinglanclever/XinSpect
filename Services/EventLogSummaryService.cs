using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.Json;

namespace XinSpect;

/// <summary>一筆系統事件記錄條目（彙整前形態）。Level：1 嚴重、2 錯誤、3 警告、4 資訊。</summary>
public sealed record EvtEntry(int Level, string Provider, ushort Id, DateTimeOffset Time);

/// <summary>
/// WP15 系統與軟體層：事件記錄摘要（Windows System log，usermode 零特權）。
/// 彙整只回答「最近 7 天嚴重＋錯誤有幾筆、最常見的來源×事件 ID 是哪些」——這是健康度指紋，
/// 不下故障結論。匯出為 canonical JSON（camelCase、只含探測給的欄位）。讀不到如實三態。
/// </summary>
public static class EventLogSummaryService
{
    private const string Category = "系統與軟體";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<EvtEntry>?>? probe = null)
    {
        var entries = (probe ?? FetchEventLog)();
        if (entries is null)
            return [new HardwareFact("evt.system.7d", Category, "7 天內嚴重／錯誤事件", "", "",
                "Windows 事件記錄 System log（EventLogReader）", FactTrustLevel.Reported, false, at, null,
                FactAvailability.ReadError, "System log 查詢失敗——事件記錄讀不到就是不猜")];

        var recent = entries.Where(e => e.Time >= at.AddDays(-7) && e.Level is 1 or 2).ToList();
        var top = recent.GroupBy(e => (e.Provider, e.Id))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Provider, StringComparer.Ordinal)
            .Take(3).Select(g => $"{g.Key.Provider} {g.Key.Id}×{g.Count()}");
        string value = recent.Count == 0
            ? "0 筆（最近 7 天沒有嚴重或錯誤事件）"
            : $"{recent.Count} 筆；最常見：" + string.Join("、", top);

        return [new HardwareFact("evt.system.7d", Category, "7 天內嚴重／錯誤事件", value, "筆",
            "Windows 事件記錄 System log（EventLogReader）", FactTrustLevel.Reported, false, at, recent.Count)];
    }

    /// <summary>把事件條目匯出成 canonical JSON。回筆數。</summary>
    public static int ExportJson(string path, IReadOnlyList<EvtEntry> entries)
    {
        Json.Export(JsonOptions, path, entries);
        return entries.Count;
    }

    /// <summary>事件記錄通路（極薄）：最近 7 天 System log 的嚴重／錯誤；查詢失敗回 null 標三態。</summary>
    public static IReadOnlyList<EvtEntry>? FetchEventLog()
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName)
            {
                ReverseDirection = true,
            };
            var entries = new List<EvtEntry>();
            using var reader = new EventLogReader(query);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            while (reader.ReadEvent() is { } evt)
            {
                using (evt)
                {
                    if (evt.TimeCreated < cutoff) break; // 反向讀到 7 天外即止
                    int level = evt.Level ?? 0;
                    if (level is 1 or 2)
                        entries.Add(new EvtEntry(level, evt.ProviderName ?? "", (ushort)(evt.Id & 0xFFFF),
                            new DateTimeOffset(evt.TimeCreated.Value, TimeSpan.Zero)));
                }
            }
            return entries;
        }
        catch { return null; }
    }
}

/// <summary>JSON 匯出的共用出口（避免每個服務自帶 serializer 設定）。</summary>
internal static class Json
{
    public static void Export(JsonSerializerOptions options, string path, object payload) =>
        File.WriteAllText(path, JsonSerializer.Serialize(payload, options));
}
