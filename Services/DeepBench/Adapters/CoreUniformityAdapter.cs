using System.Globalization;

namespace XinSpect;

/// <summary>
/// 把 <see cref="CoreUniformityService"/>（DeepTest U10-TS-0026）接進 DeepBench 深測套件。
/// 界線照服務檔的誠實聲明：偏差門檻 ±15% 是啟發式——超門檻的核標「疑似」不判決
/// （背景中斷、SMT 兄弟核搶佔、暖機不足都會造成偏差，需複測確認）；量的是本行程的
/// 運算吞吐（負載面），不是頻率判決。
/// </summary>
public sealed class CoreUniformityAdapter : IDeepBenchTest
{
    private const string TestId = "topology.core-uniformity";
    private const string CoreMetricId = "ts.core.uniformity.core";
    private const string SpreadMetricId = "ts.core.uniformity.spread";

    public string Id => TestId;

    public Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        DateTime started = DateTime.UtcNow;
        try
        {
            // 服務是靜態逐核輪跑、無中途取消鉤：取消檢查放在收集前後各一次。
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<HardwareFact> facts = CoreUniformityService.Collect(DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Map(context, started, facts));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Cancelled(context, started));
        }
    }

    private static DeepBenchTestResult Map(DeepBenchRunContext context, DateTime started, IReadOnlyList<HardwareFact> facts)
    {
        HardwareFact? spread = facts.FirstOrDefault(f => f.Key == CoreUniformityService.UniformityPrefix + "spread");
        var coreFacts = facts.Where(f => f.Key.StartsWith(CoreUniformityService.UniformityPrefix + "core.", StringComparison.Ordinal)).ToArray();
        var presentCores = coreFacts.Where(f => f.Availability == FactAvailability.Present && f.NumericValue is { }).ToArray();
        var blocked = facts.Where(f => f.Availability != FactAvailability.Present || f.NumericValue is null).ToArray();

        if (presentCores.Length == 0)
        {
            // 逐核吞吐是本項的本體；spread 只是總覽，不能拿總覽冒充逐核量測。
            return Blocked(context, started, blocked.Length > 0 ? blocked : facts.ToArray());
        }

        var limitations = new List<string>(BaseLimitations);
        limitations.AddRange(blocked.Select(f =>
            $"{f.Key} 未列入量測（{f.Availability}）：{f.UnavailableReason}"));

        var metrics = new List<DeepBenchMetric>
        {
            new(
                CoreMetricId, "逐核吞吐", UnitOf(presentCores), true,
                $"同一運算鏈逐核釘選輪跑；偏差門檻 ±{CoreUniformityService.DeviationThresholdFraction * 100:0}%（啟發式）",
                presentCores.Select(f => f.NumericValue!.Value).ToArray(),
                presentCores.Select(f => new DeepBenchMetricPoint(
                    f.NumericValue!.Value,
                    AxesForCore(f),
                    [f.NumericValue!.Value])).ToArray()),
        };
        if (spread is { } spreadFact && spreadFact.Availability == FactAvailability.Present && spreadFact.NumericValue is { })
        {
            metrics.Add(new DeepBenchMetric(
                SpreadMetricId, "逐核一致性跨幅", spreadFact.Unit.Length > 0 ? spreadFact.Unit : "%", false,
                "最快／最慢跨幅（中位數基準）",
                [spreadFact.NumericValue.Value],
                [new DeepBenchMetricPoint(spreadFact.NumericValue.Value, new Dictionary<string, string>(), [spreadFact.NumericValue.Value])]));
        }

        return new(
            TestId, context.SessionId, context.Profile, started, DateTime.UtcNow,
            $"同一運算鏈逐核釘選輪跑（{presentCores.Length} 核完成取樣）",
            metrics,
            ["背景中斷與 SMT 兄弟核搶佔會造成偏差；單一輪的吞吐差不足以定罪，需複測確認。"], limitations,
            DeepBenchFailureKind.None, null);
    }

    private static Dictionary<string, string> AxesForCore(HardwareFact fact)
    {
        // 鍵形 ts.core.uniformity.core.{全域索引}；解析失敗時原樣保留，不猜。
        string prefix = CoreUniformityService.UniformityPrefix + "core.";
        string suffix = fact.Key.StartsWith(prefix, StringComparison.Ordinal) ? fact.Key[prefix.Length..] : fact.Key;
        return new Dictionary<string, string> { ["core"] = suffix.Length > 0 ? suffix : "unknown" };
    }

    private static string UnitOf(HardwareFact[] present) =>
        present[0].Unit.Length > 0 ? present[0].Unit : "MOPS";

    private static readonly string[] BaseLimitations =
    [
        "使用者明示觸發才跑；本測試不自動執行。",
        $"偏差門檻 ±{CoreUniformityService.DeviationThresholdFraction * 100:0}% 是啟發式——超門檻的核在事實值標「疑似被限流」，不判決；背景中斷、SMT 兄弟核搶佔、暖機不足都會造成偏差。",
        "量到的是本行程運算鏈的負載面吞吐，不是頻率判決（頻率面檢查在 PlatformReconciliationFactsService 的逐核對稱性與 PowerPolicyService 的限頻顆數）。",
        "每核取樣預算 200 ms，工作量隨預算自適應截斷並標註。",
        "逐核釘選為本行程自己的執行緒（SetThreadGroupAffinity、唯讀運算）；取不到拓撲時如實回報，不猜核數。",
    ];

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消時不產生部分量測。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);

    internal static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [], ["取不到拓撲或全部釘核失敗時如實回報，不以單核或猜測冒充。"], DeepBenchFailureKind.Unsupported, error);

    /// <summary>逐核事實都不可得時：ReadError 算 NotRun，NotSupported／NotApplicable 算 Unsupported。</summary>
    internal static DeepBenchTestResult Blocked(DeepBenchRunContext context, DateTime started, IReadOnlyList<HardwareFact> blocked)
    {
        HardwareFact? readError = blocked.FirstOrDefault(f => f.Availability == FactAvailability.ReadError);
        if (readError is { } failure) return Failed(context, started, failure.UnavailableReason ?? "量測失敗。");
        if (blocked.Count == 0) return Failed(context, started, "服務未產生任何事實——不推算。");
        HardwareFact first = blocked[0];
        return Unsupported(context, started, first.UnavailableReason ?? first.Key);
    }
}
