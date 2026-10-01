using System.Globalization;

namespace XinSpect;

public sealed class CoreLatencyAdapter(CoreLatencyService service) : IDeepBenchTest
{
    private const string TestId = "topology.core-latency";

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        using CancellationTokenRegistration registration = cancellationToken.Register(service.Cancel);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CoreLatencyService.IsSupported) await service.RunAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Map(context, started, service);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
    }

    private static DeepBenchTestResult Map(DeepBenchRunContext context, DateTime started, CoreLatencyService service)
    {
        if (!CoreLatencyService.IsSupported)
        {
            return new("topology.core-latency", context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [], ["需要至少兩個邏輯處理器。"], DeepBenchFailureKind.Unsupported, "邏輯處理器少於兩個。");
        }

        int[] lps = service.Lps;
        double[,]? matrix = service.MatrixNs;
        if (lps.Length < 2 || matrix is null)
        {
            return new("topology.core-latency", context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算矩陣。"], DeepBenchFailureKind.NotRun, "舊服務未產生有效矩陣。");
        }

        List<double> samples = [];
        List<DeepBenchMetricPoint> points = [];
        for (int y = 0; y < matrix.GetLength(0); y++)
        for (int x = 0; x < matrix.GetLength(1); x++)
        {
            if (x == y || !double.IsFinite(matrix[y, x])) continue;
            double value = matrix[y, x];
            samples.Add(value);
            points.Add(new(value, new Dictionary<string, string>
            {
                ["fromLp"] = lps[y].ToString(CultureInfo.InvariantCulture),
                ["toLp"] = lps[x].ToString(CultureInfo.InvariantCulture)
            }, [value]));
        }

        return new(
            TestId, context.SessionId, context.Profile, started, DateTime.UtcNow,
            $"{lps.Length} LP 全矩陣，往返回合取中位數",
            [new DeepBenchMetric("topology.core.latency", "Core-to-core latency", "ns", false, "ping-pong", samples, points)],
            ["對應邏輯處理器會短暂滿載。"], ["使用者模式親和性與忙碌等待；不含排程器喚醒以外的核心内部路徑。"],
            DeepBenchFailureKind.None, null);
    }

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new("topology.core-latency", context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["部分矩陣不輸出成完整結果。"], DeepBenchFailureKind.Cancelled, "使用者取消。");
}

