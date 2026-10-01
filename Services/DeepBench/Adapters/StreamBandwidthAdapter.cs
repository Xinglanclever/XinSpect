using System.Globalization;

namespace XinSpect;

public sealed class StreamBandwidthAdapter(MemBandwidthService service) : IDeepBenchTest
{
    private const string TestId = "memory.stream-bandwidth";

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        using CancellationTokenRegistration registration = cancellationToken.Register(service.Cancel);
        try
        {
            // 只重用同場產生的快照；跨場舊資料必須重跑，不得重蓋時間戳。
            DateTime snapshotStart = started, snapshotEnd = started;
            bool reused = service.Rows.Count > 0
                && LegacySnapshotStamp.IsFreshFor(service, context.SessionId, out snapshotStart, out snapshotEnd);
            DateTime measurementStart = started;
            DateTime measurementEnd = started;
            if (!reused)
            {
                await service.RunAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                measurementEnd = DateTime.UtcNow;
            }
            else
            {
                measurementStart = snapshotStart;
                measurementEnd = snapshotEnd;
            }
            return Map(context, measurementStart, measurementEnd, service, reused);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
    }

    private static DeepBenchTestResult Map(
        DeepBenchRunContext context, DateTime started, DateTime ended, MemBandwidthService service, bool reused)
    {
        var rows = service.Rows.Where(row => double.IsFinite(row.Gbps)).ToArray();
        if (rows.Length == 0) return Failed(context, started, "舊服務未產生有效 STREAM 資料。");
        if (!reused) LegacySnapshotStamp.Mark(service, context.SessionId, started, ended);

        var limitations = reused
            ? [.. BaseLimitations, $"串流與負載延遲共用同一次服務實測；本結果重用同場 {ended.ToLocalTime():HH:mm:ss} 完成的量測，未重新執行工作負載。"]
            : BaseLimitations;
        return new(
            TestId, context.SessionId, context.Profile, started, ended,
            $"STREAM 式 {rows.Select(row => row.Kernel).Distinct().Count()} 種存取型態 × 執行緒階梯",
            [new DeepBenchMetric(
                "memory.stream.bandwidth", "STREAM bandwidth", "GB/s", true, "managed arrays",
                rows.Select(row => row.Gbps).ToArray(),
                rows.Select(row => new DeepBenchMetricPoint(
                    row.Gbps,
                    new Dictionary<string, string> { ["kernel"] = row.Kernel, ["threads"] = row.Threads.ToString(CultureInfo.InvariantCulture) },
                    [row.Gbps])).ToArray())],
            [$"理論上限對照：{service.PeakNote}"], limitations,
            DeepBenchFailureKind.None, null);
    }

    private static readonly string[] BaseLimitations =
        ["量到的是本程式達成的頻寬，不是控制器絕對上限。"];

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消時保留舊服務既有輸出，不補新樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");
    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);
}
