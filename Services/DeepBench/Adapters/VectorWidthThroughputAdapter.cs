namespace XinSpect;

/// <summary>
/// 把 <see cref="VectorWidthThroughputService"/>（DeepTest U10-TS-0003／0005／0006）接進
/// DeepBench 深測套件。界線照服務檔的誠實聲明：使用者明示觸發才跑；量的是本行程 JIT
/// 編譯程式碼的吞吐，不是 CPU 規格峰值；AVX-512 不支援時如實 Unsupported，不降級冒充。
/// </summary>
public sealed class VectorWidthThroughputAdapter : IDeepBenchTest
{
    private const string TestId = "cpu.vector-width-throughput";
    private const string MetricId = "ts.vector.flops";

    public string Id => TestId;

    public Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        DateTime started = DateTime.UtcNow;
        try
        {
            // 服務是靜態收集、無中途取消鉤：取消檢查放在收集前後各一次（與 CacheLatencyAdapter 同形）。
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<HardwareFact> facts = VectorWidthThroughputService.Collect(
                DateTimeOffset.UtcNow, probe: null, avx512Supported: System.Runtime.Intrinsics.X86.Avx512F.IsSupported);
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
            $"純量／128／256／512 位元 FP32 乘加鏈 × 單／多執行緒（{present.Length} 相位）",
            [new DeepBenchMetric(
                MetricId, "向量寬度階梯吞吐", UnitOf(present), true, "暫存器駐留 FP32 乘加鏈；managed Vector128/256/512<float>",
                present.Select(f => f.NumericValue!.Value).ToArray(),
                present.Select(f => new DeepBenchMetricPoint(
                    f.NumericValue!.Value,
                    AxesFor(f),
                    [f.NumericValue!.Value])).ToArray())],
            ["背景負載會影響吞吐；多執行緒相位以預算窗為界。"], limitations,
            DeepBenchFailureKind.None, null);
    }

    private static Dictionary<string, string> AxesFor(HardwareFact fact)
    {
        // 鍵形 ts.vector.flops.{寬度}.{st|mt}；解析失敗時整段塞進 width，不猜。
        string suffix = fact.Key.StartsWith(VectorWidthThroughputService.FamilyPrefix, StringComparison.Ordinal)
            ? fact.Key[VectorWidthThroughputService.FamilyPrefix.Length..]
            : fact.Key;
        int dot = suffix.IndexOf('.', StringComparison.Ordinal);
        string width = dot < 0 ? suffix : suffix[..dot];
        string phase = dot < 0 ? "" : suffix[(dot + 1)..];
        return new Dictionary<string, string>
        {
            ["width"] = width,
            ["phase"] = phase.Length > 0 ? phase : "unknown",
        };
    }

    private static string UnitOf(HardwareFact[] present) =>
        present[0].Unit.Length > 0 ? present[0].Unit : "GFLOPS";

    private static readonly string[] BaseLimitations =
    [
        "使用者明示觸發才跑；本測試不自動執行，也不在掃描路徑上。",
        "量到的是本行程 JIT 編譯程式碼的吞吐，不是 CPU 規格峰值。",
        "乘與加各計 1 FLOP；FMA 融合與否不改變計數，只改變實際達成率。",
        "AVX-512 不支援時如實回 NotSupported，不降級到 256 位元冒充。",
        "樣本為預算窗（單相位 300 ms）內的截斷窗；降頻效應由 CeilingService 的向量授權降頻窗收口，本測試不重測頻率。",
    ];

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消時不產生部分量測。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);

    internal static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [], ["環境不滿足時如實回報，不以其他寬度冒充。"], DeepBenchFailureKind.Unsupported, error);

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
