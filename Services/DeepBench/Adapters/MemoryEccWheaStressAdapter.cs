using System.Globalization;

namespace XinSpect;

/// <summary>
/// 以既有使用者模式記憶體頻寬工作負載施壓，並只把同一時間窗內的 WHEA 事件當作硬體證據。
/// 不是 MemTest86，也不宣稱能注入或偵測每一次 ECC 修正。
/// </summary>
public sealed class MemoryEccWheaStressAdapter(
    MemBandwidthService service,
    IWheaEventStore? eventStore = null) : IDeepBenchTest
{
    public const string TestId = "memory.ecc-whea-stress";

    private readonly IWheaEventStore _eventStore = eventStore ?? new WheaTimelineStore();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        DateTime windowStart = DateTime.Now;
        using CancellationTokenRegistration registration = cancellationToken.Register(service.Cancel);

        IReadOnlyList<WheaTimelineEvent> events;
        DateTime windowEnd;
        try
        {
            service.Rows.Clear();
            service.LoadedRows.Clear();
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.05, "啟動記憶體壓力與 WHEA 觀察窗"));
            await service.RunAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // WHEA 事件可能晚於負載結束落盤；固定短等待只換取更完整的事件窗，不推算遺漏事件。
            await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            windowEnd = DateTime.Now;
            events = _eventStore
                .ReadSince(windowStart.AddSeconds(-2))
                .Where(item => item.Time >= windowStart && item.Time <= windowEnd)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, $"記憶體壓力或 WHEA 觀察未完成：{exception.Message}");
        }

        var bandwidthRows = service.Rows.Where(row => double.IsFinite(row.Gbps) && row.Gbps > 0).ToArray();
        var latencyRows = service.LoadedRows.Where(row => double.IsFinite(row.LatencyNs) && row.LatencyNs > 0).ToArray();
        if (bandwidthRows.Length == 0 || latencyRows.Length == 0)
        {
            return Failed(context, started, DeepBenchFailureKind.NotRun, "記憶體工作負載未同時產生有效頻寬與負載延遲資料。");
        }

        int corrected = events.Count(item => item.Id == 17);
        int uncorrected = events.Count(item => item.Id == 18);
        int critical = events.Count(item => item.Level == 1);
        int errors = events.Count(item => item.Level == 2);
        int warnings = events.Count(item => item.Level == 3);

        var conditions = new List<string>
        {
            $"WHEA 觀察窗：{windowStart:HH:mm:ss}–{windowEnd:HH:mm:ss}；事件落盤延遲已保留 1 秒觀察期。",
            $"WHEA 總計 {events.Count} 筆；記憶體修正 {corrected}、記憶體不可修正 {uncorrected}。",
            $"層級：重大 {critical}、錯誤 {errors}、警告 {warnings}。",
        };
        foreach (WheaTimelineEvent item in events.Take(3))
            conditions.Add($"WHEA #{item.Id}：{item.Message}");

        string? error = events.Count == 0
            ? null
            : $"壓力窗內出現 {events.Count} 筆 WHEA 事件（記憶體修正 {corrected}、不可修正 {uncorrected}）；量測保留，但不當作健康結果。";

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"既有 MemBandwidth 工作負載；{bandwidthRows.Count()} 個頻寬點、{latencyRows.Length} 個負載延遲點",
            [
                CreateBandwidthMetric(bandwidthRows),
                CreateLatencyMetric(latencyRows),
                CreateCountMetric("memory.whea.total", "WHEA events during stress", events.Count),
                CreateCountMetric("memory.whea.memory-corrected", "Corrected memory WHEA events", corrected),
                CreateCountMetric("memory.whea.memory-uncorrected", "Uncorrected memory WHEA events", uncorrected),
            ],
            conditions,
            [
                "這是使用者模式頻寬壓力加 WHEA 時間窗關聯；不是 MemTest86，也不是 ECC 錯誤注入測試。",
                "WHEA 事件只能證明同窗紀錄，不能單靠時間相關證明因果；事件延遲落盤時可能觀察不到。",
                "沒有 ECC 的平台也會執行頻寬壓力；WHEA 無事件表示本次未紀錄錯誤，不是保證零缺陷。",
            ],
            events.Count == 0 ? DeepBenchFailureKind.None : DeepBenchFailureKind.Unstable,
            error);
    }

    private static DeepBenchMetric CreateBandwidthMetric(MemBandwidthRow[] rows) => new(
        "memory.stress.bandwidth",
        "Memory stress bandwidth",
        "GB/s",
        true,
        "existing MemBandwidth kernels under WHEA observation",
        rows.Select(row => row.Gbps).ToArray(),
        rows.Select(row => new DeepBenchMetricPoint(
            row.Gbps,
            new Dictionary<string, string>
            {
                ["kernel"] = row.Kernel,
                ["threads"] = row.Threads.ToString(CultureInfo.InvariantCulture),
            },
            [row.Gbps])).ToArray());

    private static DeepBenchMetric CreateLatencyMetric(LoadedLatencyRow[] rows) => new(
        "memory.stress.loaded-latency",
        "Memory stress loaded latency",
        "ns",
        false,
        "existing loaded-latency ladder under WHEA observation",
        rows.Select(row => row.LatencyNs).ToArray(),
        rows.Select(row => new DeepBenchMetricPoint(
            row.LatencyNs,
            new Dictionary<string, string>
            {
                ["loaders"] = row.Loaders.ToString(CultureInfo.InvariantCulture),
                ["bandwidthGbps"] = row.Gbps.ToString(CultureInfo.InvariantCulture),
            },
            [row.LatencyNs])).ToArray());

    private static DeepBenchMetric CreateCountMetric(string id, string title, int count) => new(
        id,
        title,
        "events",
        false,
        "WHEA-Logger events filtered to stress window",
        [count],
        [new DeepBenchMetricPoint(count, new Dictionary<string, string>(), [count])]);

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消後不輸出壓力或 WHEA 樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["觀察窗不完整時不輸出整合結論。"], kind, error);
}
