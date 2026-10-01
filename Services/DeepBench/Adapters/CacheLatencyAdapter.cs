using System.Globalization;

namespace XinSpect;

public sealed class CacheLatencyAdapter(CacheBenchService service) : IDeepBenchTest
{
    private const string TestId = "memory.cache-latency";

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        DateTime started = DateTime.UtcNow;
        using CancellationTokenRegistration registration = cancellationToken.Register(service.Cancel);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await service.RunAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Map(context, started, service);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
    }

    private static DeepBenchTestResult Map(DeepBenchRunContext context, DateTime started, CacheBenchService service)
    {
        var rows = service.Rows.Where(row => double.IsFinite(row.LatencyNs)).ToArray();
        if (rows.Length == 0 || service.Phase.Contains("失敗", StringComparison.Ordinal))
        {
            return Failed(context, started, service.Phase.Contains("失敗", StringComparison.Ordinal) ? service.StatusLine : "舊服務未產生有效快取延遲資料。");
        }

        return new DeepBenchTestResult(
            TestId, context.SessionId, context.Profile, started, DateTime.UtcNow,
            $"工作集 {rows.Length} 級；64B 亂序單循環指標追逐",
            [new DeepBenchMetric(
                "memory.cache.latency", "Cache / memory latency ladder", "ns", false, "pointer chase",
                rows.Select(row => row.LatencyNs).ToArray(),
                rows.Select(row => new DeepBenchMetricPoint(
                    row.LatencyNs,
                    new Dictionary<string, string> { ["workingSetText"] = row.SizeText, ["workingSetBytes"] = ParseBytes(row.SizeText).ToString(CultureInfo.InvariantCulture) },
                    [row.LatencyNs])).ToArray())],
            ["背景負載會影響延遲。"], ["使用者模式指標追逐；推估快取層級，不是硬體效能計數器。"],
            DeepBenchFailureKind.None, null);
    }

    internal static int ParseBytes(string text) => text.ToUpperInvariant() switch
    {
        "4 KB" => 4 * 1024,
        "16 KB" => 16 * 1024,
        "32 KB" => 32 * 1024,
        "128 KB" => 128 * 1024,
        "256 KB" => 256 * 1024,
        "512 KB" => 512 * 1024,
        "1 MB" => 1024 * 1024,
        "4 MB" => 4 * 1024 * 1024,
        "8 MB" => 8 * 1024 * 1024,
        "16 MB" => 16 * 1024 * 1024,
        "32 MB" => 32 * 1024 * 1024,
        "64 MB" => 64 * 1024 * 1024,
        _ => 0
    };

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new("memory.cache-latency", context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消前未完成全部工作集。"], DeepBenchFailureKind.Cancelled, "使用者取消；未產生可信量測。");

    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new("memory.cache-latency", context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);
}

