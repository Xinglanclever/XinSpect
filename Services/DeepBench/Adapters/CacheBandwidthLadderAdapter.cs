using System.Globalization;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 把 <see cref="CacheBandwidthLadderService"/>（DeepTest U10-TS-0015／0016／0017）接進
/// DeepBench 深測套件。界線照服務檔的誠實聲明：工作集尺寸是啟發式（非 CPUID 快取葉解出的
/// 每核精確幾何）；主記憶體層級由 memory.stream-bandwidth 收口，本測試不重測 DRAM；
/// 延遲階梯（ns）由 CacheBenchService 的指標追逐收口，帶寬與延遲不互冒。
/// </summary>
public sealed class CacheBandwidthLadderAdapter : IDeepBenchTest
{
    private const string TestId = "memory.cache-bandwidth-ladder";
    private const string MetricId = "ts.cache.bw";

    public string Id => TestId;

    public Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        DateTime started = DateTime.UtcNow;
        try
        {
            // 服務是靜態收集、無中途取消鉤：取消檢查放在收集前後各一次。
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<HardwareFact> facts = CacheBandwidthLadderService.Collect(DateTimeOffset.UtcNow);
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
        var present = facts.Where(f => f.Availability == FactAvailability.Present && f.NumericValue is { }).ToArray();
        var blocked = facts.Where(f => f.Availability != FactAvailability.Present || f.NumericValue is null).ToArray();
        if (present.Length == 0) return Blocked(context, started, blocked);

        var limitations = new List<string>(BaseLimitations);
        limitations.AddRange(blocked.Select(f =>
            $"{f.Key} 未列入量測（{f.Availability}）：{f.UnavailableReason}"));

        return new(
            TestId, context.SessionId, context.Profile, started, DateTime.UtcNow,
            $"L1／L2／L3 尺寸工作集讀取帶寬（{present.Length} 級）",
            [new DeepBenchMetric(
                MetricId, "快取讀取帶寬階梯", UnitOf(present), true, "managed Vector<float> 讀取累加迴圈",
                present.Select(f => f.NumericValue!.Value).ToArray(),
                present.Select(f => new DeepBenchMetricPoint(
                    f.NumericValue!.Value,
                    AxesFor(f),
                    [f.NumericValue!.Value])).ToArray())],
            ["背景負載會影響帶寬；每層樣本為預算窗（300 ms）內的截斷窗。"], limitations,
            DeepBenchFailureKind.None, null);
    }

    private static Dictionary<string, string> AxesFor(HardwareFact fact)
    {
        // 鍵形 ts.cache.bw.{l1|l2|l3}；工作集從事實說明文字解析，解析不到回 0（與 CacheLatencyAdapter 同立場：不猜）。
        string suffix = fact.Key.StartsWith(CacheBandwidthLadderService.LevelPrefix, StringComparison.Ordinal)
            ? fact.Key[CacheBandwidthLadderService.LevelPrefix.Length..]
            : fact.Key;
        return new Dictionary<string, string>
        {
            ["level"] = suffix.Length > 0 ? suffix : "unknown",
            ["workingSetBytes"] = ParseWorkingSetBytes(fact.Value).ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>解析「（工作集 32.0 KB）」式標示；無法辨識時回 0，軸值原樣保留。</summary>
    internal static long ParseWorkingSetBytes(string text)
    {
        Match m = WorkingSetPattern.Match(text);
        if (!m.Success
            || !double.TryParse(m.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double kb)
            || kb < 0)
        {
            return 0;
        }
        return (long)(kb * 1024);
    }

    private static readonly Regex WorkingSetPattern =
        new(@"工作集\s*(?<size>[0-9]+(?:\.[0-9]+)?)\s*KB", RegexOptions.Compiled);

    private static string UnitOf(HardwareFact[] present) =>
        present[0].Unit.Length > 0 ? present[0].Unit : "GB/s";

    private static readonly string[] BaseLimitations =
    [
        "使用者明示觸發才跑；本測試不自動執行。",
        "量到的是本行程 managed 讀取迴圈達成的帶寬，不是快取的硬體上限。",
        "工作集尺寸是啟發式（32 KB／512 KB／16 MB），不是從 CPUID 快取葉解出的每核精確幾何；對照該機快取規格請看事實值內的工作集大小。",
        "主記憶體層級由 memory.stream-bandwidth（STREAM 四型態）收口，本測試不重測 DRAM。",
        "延遲階梯（ns）由 CacheBenchService 指標追逐收口；帶寬與延遲是兩個形狀，不互冒。",
    ];

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消時不產生部分量測。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);

    internal static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [], ["環境不滿足時如實回報，不以其他層級冒充。"], DeepBenchFailureKind.Unsupported, error);

    /// <summary>全部事實都不可得時：ReadError 算 NotRun，其餘（NotSupported／NotApplicable）算 Unsupported。</summary>
    internal static DeepBenchTestResult Blocked(DeepBenchRunContext context, DateTime started, IReadOnlyList<HardwareFact> blocked)
    {
        HardwareFact? readError = blocked.FirstOrDefault(f => f.Availability == FactAvailability.ReadError);
        if (readError is { } failure) return Failed(context, started, failure.UnavailableReason ?? "量測失敗。");
        if (blocked.Count == 0) return Failed(context, started, "服務未產生任何事實——不推算。");
        HardwareFact first = blocked[0];
        return Unsupported(context, started, first.UnavailableReason ?? first.Key);
    }
}
