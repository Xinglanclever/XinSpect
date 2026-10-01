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
            if (service.Rows.Count == 0) await service.RunAsync().ConfigureAwait(false);
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
        var rows = service.Rows.Where(row => double.IsFinite(row.Gbps)).ToArray();
        if (rows.Length == 0) return Failed(context, started, "舊服務未產生有效 STREAM 資料。");
        return new(
            TestId, context.SessionId, context.Profile, started, DateTime.UtcNow,
            $"STREAM 式 {rows.Select(row => row.Kernel).Distinct().Count()} 種存取型態 × 執行緒階梯",
            [new DeepBenchMetric(
                "memory.stream.bandwidth", "STREAM bandwidth", "GB/s", true, "managed arrays",
                rows.Select(row => row.Gbps).ToArray(),
                rows.Select(row => new DeepBenchMetricPoint(
                    row.Gbps,
                    new Dictionary<string, string> { ["kernel"] = row.Kernel, ["threads"] = row.Threads.ToString(CultureInfo.InvariantCulture) },
                    [row.Gbps])).ToArray())],
            [$"理論上限對照：{service.PeakNote}"], ["量到的是本程式達成的頻寬，不是控制器絕對上限。"],
            DeepBenchFailureKind.None, null);
    }

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new("memory.stream-bandwidth", context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消時保留舊服務既有輸出，不補新樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");
    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new("memory.stream-bandwidth", context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);
}

