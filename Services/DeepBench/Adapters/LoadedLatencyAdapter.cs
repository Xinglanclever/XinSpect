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
            if (service.LoadedRows.Count == 0) await service.RunAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Map(context, started, service);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
    }

    private static DeepBenchTestResult Map(DeepBenchRunContext context, DateTime started, MemBandwidthService service)
    {
        var rows = service.LoadedRows.Where(row => double.IsFinite(row.LatencyNs)).ToArray();
        if (rows.Length == 0) return Failed(context, started, "舊服務未產生有效 loaded latency 資料。");
        return new(
            TestId, context.SessionId, context.Profile, started, DateTime.UtcNow,
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
            ["施壓執行緒與追逐執行緒同時跑。"], ["使用者模式量測；不是記憶體控制器内部計數器。"],
            DeepBenchFailureKind.None, null);
    }

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new("memory.loaded-latency", context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消時不補樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");
    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new("memory.loaded-latency", context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);
}

