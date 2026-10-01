using System.Globalization;

namespace XinSpect;

public sealed class LoadedLatencyAdapter(MemBandwidthService service) : IDeepBenchTest
{
    private const string TestId = "memory.loaded-latency";

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        using CancellationTokenRegistration registration = cancellationToken.Register(service.Cancel);
        try
        {
            // 只重用同場產生的快照；跨場舊資料必須重跑，不得重蓋時間戳。
            DateTime snapshotStart = started, snapshotEnd = started;
            bool reused = service.LoadedRows.Count > 0
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
        var rows = service.LoadedRows.Where(row => double.IsFinite(row.LatencyNs)).ToArray();
        if (rows.Length == 0) return Failed(context, started, "舊服務未產生有效 loaded latency 資料。");
        if (!reused) LegacySnapshotStamp.Mark(service, context.SessionId, started, ended);

        var limitations = reused
            ? [.. BaseLimitations, $"串流與負載延遲共用同一次服務實測；本結果重用同場 {ended.ToLocalTime():HH:mm:ss} 完成的量測，未重新執行工作負載。"]
            : BaseLimitations;
        return new(
            TestId, context.SessionId, context.Profile, started, ended,
            $"施壓執行緒階梯 {rows.Length} 級；同時記錄延遲與頻寬",
            [new DeepBenchMetric(
                "memory.loaded.latency", "Loaded latency", "ns", false, "pointer chase + bandwidth loaders",
                rows.Select(row => row.LatencyNs).ToArray(),
                rows.Select(row => new DeepBenchMetricPoint(
                    row.LatencyNs,
                    new Dictionary<string, string>
                    {
                        ["loaders"] = row.Loaders.ToString(CultureInfo.InvariantCulture),
                        ["bandwidthGbps"] = row.Gbps.ToString(CultureInfo.InvariantCulture)
                    },
                    [row.LatencyNs])).ToArray())],
            ["施壓執行緒與追逐執行緒同時跑。"], limitations,
            DeepBenchFailureKind.None, null);
    }

    private static readonly string[] BaseLimitations =
        ["使用者模式量測；不是記憶體控制器內部計數器。"];

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消時不補樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");
    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);
}
