using System.Globalization;

namespace XinSpect;

public sealed class TopDownAdapter(TopDownService service) : IDeepBenchTest
{
    private const string TestId = "cpu.top-down";

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!service.RunSupportedProbe())
            {
                return Unsupported(context, started, service.SupportText);
            }

            await service.SampleAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Map(context, started, service);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
    }

    private static DeepBenchTestResult Map(DeepBenchRunContext context, DateTime started, TopDownService service)
    {
        var rows = service.Rows.Where(row => row.Valid).ToArray();
        if (rows.Length == 0 || service.Buckets.Count < 4 || service.Phase.Contains("錯誤", StringComparison.Ordinal))
        {
            return Failed(context, started, "未產生有效 Top-down 樣本；" + service.Phase + "；" + service.StatusLine);
        }

        var metrics = service.Buckets.Select(bucket => new DeepBenchMetric(
            MetricId(bucket.Name),
            bucket.Name,
            "%",
            false,
            $"aggregate={bucket.Percent.ToString("0.###", CultureInfo.InvariantCulture)}%; slotsPerCycle={service.SlotsPerCycle}",
            rows.Select(row => BucketValue(bucket, row)).ToArray(),
            rows.Select(row => new DeepBenchMetricPoint(
                BucketValue(bucket, row),
                new Dictionary<string, string>
                {
                    ["physicalCore"] = row.Core.ToString(CultureInfo.InvariantCulture),
                    ["logicalProcessors"] = row.LpText
                },
                [BucketValue(bucket, row)])).ToArray())).ToArray();

        return new(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{service.MicroarchText}；{rows.Length}/{service.Rows.Count} 顆有效核心；{service.WindowMs} ms/core",
            metrics,
            [service.MicroarchText, service.SupportText],
            [
                "Level 1 公式不含 INT_MISC.RECOVERY_CYCLES，Bad Speculation 可能略低估、Backend Bound 可能略高估。",
                "逐核心序列取樣；樣本時間點不同，背景負載會直接影響四桶分佈。",
                "取樣期間閒置的核心顯示為 —，不進入百分比樣本；不從缺失資料推算。"
            ],
            DeepBenchFailureKind.None,
            null);
    }

    private static double BucketValue(TopDownBucket bucket, TopDownCoreRow row) => bucket.Name switch
    {
        _ when bucket.Name.Contains("退休", StringComparison.Ordinal) => row.Retiring,
        _ when bucket.Name.Contains("錯誤推測", StringComparison.Ordinal) => row.BadSpec,
        _ when bucket.Name.Contains("前端", StringComparison.Ordinal) => row.Frontend,
        _ => row.Backend
    };

    private static string MetricId(string bucketName) => bucketName switch
    {
        _ when bucketName.Contains("退休", StringComparison.Ordinal) => "cpu.topdown.retiring",
        _ when bucketName.Contains("錯誤推測", StringComparison.Ordinal) => "cpu.topdown.bad-speculation",
        _ when bucketName.Contains("前端", StringComparison.Ordinal) => "cpu.topdown.frontend-bound",
        _ => "cpu.topdown.backend-bound"
    };

    internal static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string reason) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [], ["不套用不相容事件配方。"], DeepBenchFailureKind.Unsupported, reason);

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取樣未完成，不產生推算。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["未產生有效 Top-down 樣本。"], DeepBenchFailureKind.NotRun, error);
}
