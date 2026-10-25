using System.Globalization;

namespace XinSpect;

/// <summary>
/// 把 <see cref="CopyKernelBandwidthService"/>（DeepTest U10-TS-0018／0019）接進
/// DeepBench 深測套件。界線照服務檔的誠實聲明：照實量 managed 呼叫到的實作
/// （Array.Copy／Array.Fill 走 CRT 路徑），不自行判定微碼選路；工作集 16 MiB 為 DRAM
/// 層級，與 memory.stream-bandwidth 的 STREAM 四型態不重疊也不重測。
/// </summary>
public sealed class CopyKernelBandwidthAdapter : IDeepBenchTest
{
    private const string TestId = "memory.copy-kernel-bandwidth";
    private const string MetricId = "ts.copy.kernel";

    public string Id => TestId;

    public Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        DateTime started = DateTime.UtcNow;
        try
        {
            // 服務是靜態收集、無中途取消鉤：取消檢查放在收集前後各一次。
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<HardwareFact> facts = CopyKernelBandwidthService.Collect(DateTimeOffset.UtcNow);
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
            $"memcpy／memset × CRT／SIMD／逐位元組（{present.Length} 個核×實作組合；工作集 {CopyKernelBandwidthService.WorkingSetMib} MiB）",
            [new DeepBenchMetric(
                MetricId, "搬運核帶寬對照", UnitOf(present), true, "Array.Copy/Array.Fill（CRT）與 Vector<byte> 迴圈（SIMD）與逐位元組迴圈",
                present.Select(f => f.NumericValue!.Value).ToArray(),
                present.Select(f => new DeepBenchMetricPoint(
                    f.NumericValue!.Value,
                    AxesFor(f),
                    [f.NumericValue!.Value])).ToArray())],
            ["背景負載會影響帶寬；每個組合的樣本為預算窗（200 ms）內的截斷窗。"], limitations,
            DeepBenchFailureKind.None, null);
    }

    private static Dictionary<string, string> AxesFor(HardwareFact fact)
    {
        // 鍵形 ts.copy.kernel.{memcpy|memset}.{crt|simd|byte}；解析失敗時整段塞進 kernel，不猜。
        string suffix = fact.Key.StartsWith(CopyKernelBandwidthService.KernelPrefix, StringComparison.Ordinal)
            ? fact.Key[CopyKernelBandwidthService.KernelPrefix.Length..]
            : fact.Key;
        int dot = suffix.IndexOf('.', StringComparison.Ordinal);
        string kernel = dot < 0 ? suffix : suffix[..dot];
        string implementation = dot < 0 ? "unknown" : suffix[(dot + 1)..];
        return new Dictionary<string, string>
        {
            ["kernel"] = kernel.Length > 0 ? kernel : "unknown",
            ["implementation"] = implementation.Length > 0 ? implementation : "unknown",
        };
    }

    private static string UnitOf(HardwareFact[] present) =>
        present[0].Unit.Length > 0 ? present[0].Unit : "GB/s";

    private static readonly string[] BaseLimitations =
    [
        "使用者明示觸發才跑；本測試不自動執行。",
        "量到的是本行程 managed 呼叫達成的帶寬，不是記憶體控制器上限。",
        "CRT 路徑照實量 managed 呼叫到的 Array.Copy／Array.Fill 實作，不自行判定微碼選路；FSRM／ERMS 的有無由 CPUID 特徵表判讀。",
        "工作集 16 MiB 為 DRAM 層級；讀／複製／加／三元 STREAM 型態由 memory.stream-bandwidth 收口，本測試的差異面是搬運核與填充核的實作對照。",
    ];

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消時不產生部分量測。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);

    internal static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [], ["環境不滿足時如實回報，不以其他實作冒充。"], DeepBenchFailureKind.Unsupported, error);

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
