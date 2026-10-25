using System.Globalization;

namespace XinSpect;

/// <summary>
/// 把 <see cref="TscCrossCoreSyncService"/>（DeepTest U10-TS-0024）接進 DeepBench 深測套件。
/// 界線照服務檔的誠實聲明：跨核 TSC 讀取走 WinRing0 橋的唯讀 MSR 0x10；未提供讀取通路時
/// 如實回 NotSupported，不改用親和性取樣冒充。skew 是量測值不是判決——QPC 中點夾取只抵銷
/// 線性延遲，數值含取樣延遲誤差。
/// </summary>
public sealed class TscCrossCoreSyncAdapter : IDeepBenchTest
{
    private const string TestId = "topology.tsc-cross-core-sync";
    private const string CoreMetricId = "ts.tsc.skew.core";
    private const string MaxMetricId = "ts.tsc.skew.max";

    public string Id => TestId;

    public Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        DateTime started = DateTime.UtcNow;
        try
        {
            // 服務是序列式逐核取樣、無中途取消鉤：取消檢查放在收集前後各一次。
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<HardwareFact> facts = Collect(started);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Map(context, started, facts));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Cancelled(context, started));
        }
    }

    /// <summary>
    /// 真實讀取通路：WinRing0 橋的唯讀 MSR 0x10（<see cref="CeilingService"/> 的 MeasureTscHz 同款）。
    /// 橋不可用時傳 null，服務會如實回 NotSupported——不冒充。
    /// </summary>
    private static IReadOnlyList<HardwareFact> Collect(DateTime started)
    {
        using WinRing0Bridge bridge = WinRing0Bridge.Create();
        Func<ulong?>? readTsc = bridge.Available ? () => bridge.ReadMsrPair64(0x10) : null;
        return TscCrossCoreSyncService.Collect(new DateTimeOffset(started, TimeSpan.Zero), readTsc);
    }

    private static DeepBenchTestResult Map(DeepBenchRunContext context, DateTime started, IReadOnlyList<HardwareFact> facts)
    {
        HardwareFact? max = facts.FirstOrDefault(f => f.Key == TscCrossCoreSyncService.SkewMaxKey);
        var coreFacts = facts.Where(f => f.Key.StartsWith(TscCrossCoreSyncService.SkewCorePrefix, StringComparison.Ordinal)).ToArray();
        var presentCores = coreFacts.Where(f => f.Availability == FactAvailability.Present && f.NumericValue is { }).ToArray();
        var blocked = facts.Where(f => f.Availability != FactAvailability.Present || f.NumericValue is null).ToArray();

        if (max is null || max.Availability != FactAvailability.Present || max.NumericValue is null)
        {
            // skew 本體不可得時整項不給量測：逐核頻率只是伴隨證據，不能冒充跨核偏移。
            return Blocked(context, started, blocked.Length > 0 ? blocked : (coreFacts.Length > 0 ? coreFacts : facts.ToArray()));
        }

        var limitations = new List<string>(BaseLimitations);
        limitations.AddRange(blocked.Select(f =>
            $"{f.Key} 未列入量測（{f.Availability}）：{f.UnavailableReason}"));

        var metrics = new List<DeepBenchMetric>();
        if (presentCores.Length > 0)
        {
            metrics.Add(new DeepBenchMetric(
                CoreMetricId, "TSC 逐核取樣頻率", UnitOf(presentCores), true, "WinRing0 唯讀 MSR 0x10；QPC 中點夾取；逐核 SetThreadGroupAffinity",
                presentCores.Select(f => f.NumericValue!.Value).ToArray(),
                presentCores.Select(f => new DeepBenchMetricPoint(
                    f.NumericValue!.Value,
                    AxesForCore(f),
                    [f.NumericValue!.Value])).ToArray()));
        }
        metrics.Add(new DeepBenchMetric(
            MaxMetricId, "TSC 跨核最大偏移", max.Unit.Length > 0 ? max.Unit : "ns", false,
            $"{presentCores.Length} 核外推至同一 QPC 參考時刻",
            [max.NumericValue!.Value],
            [new DeepBenchMetricPoint(max.NumericValue!.Value, new Dictionary<string, string>(), [max.NumericValue!.Value])]));

        return new(
            TestId, context.SessionId, context.Profile, started, DateTime.UtcNow,
            $"逐核 TSC 取樣（MSR 0x10）與跨核偏移；{presentCores.Length} 核完成取樣",
            metrics,
            ["背景負載與中斷會加大取樣抖動；逐核取樣為序列式（不同核可能不同群組）。"], limitations,
            DeepBenchFailureKind.None, null);
    }

    private static Dictionary<string, string> AxesForCore(HardwareFact fact)
    {
        // 鍵形 ts.tsc.skew.core.{全域索引}；解析失敗時原樣保留，不猜。
        string suffix = fact.Key.StartsWith(TscCrossCoreSyncService.SkewCorePrefix, StringComparison.Ordinal)
            ? fact.Key[TscCrossCoreSyncService.SkewCorePrefix.Length..]
            : fact.Key;
        return new Dictionary<string, string> { ["core"] = suffix.Length > 0 ? suffix : "unknown" };
    }

    private static string UnitOf(HardwareFact[] present) =>
        present[0].Unit.Length > 0 ? present[0].Unit : "MHz";

    private static readonly string[] BaseLimitations =
    [
        "使用者明示觸發才跑；本測試不自動執行。",
        "usermode 無 rdtsc 通路；跨核 TSC 讀取走 WinRing0 橋的唯讀 MSR 0x10，未提供讀取通路時如實回 NotSupported，不以親和性取樣冒充（親和性只能釘核，讀不到 per-core TSC 值）。",
        "skew 是量測值不是判決——QPC 中點夾取只抵銷線性延遲，數值含取樣延遲誤差，不判決時基可否信賴。",
        "核數超過 5000 ms 預算時取前段核並標註截斷，未涵蓋全部邏輯處理器。",
    ];

    internal static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消時不產生部分量測。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    internal static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], DeepBenchFailureKind.NotRun, error);

    internal static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [], ["沒有讀取通路或核數不足時如實回報，不以單核或親和性取樣冒充。"], DeepBenchFailureKind.Unsupported, error);

    /// <summary>skew 本體不可得時：ReadError 算 NotRun，NotSupported／NotApplicable 算 Unsupported。</summary>
    internal static DeepBenchTestResult Blocked(DeepBenchRunContext context, DateTime started, IReadOnlyList<HardwareFact> blocked)
    {
        HardwareFact? readError = blocked.FirstOrDefault(f => f.Availability == FactAvailability.ReadError);
        if (readError is { } failure) return Failed(context, started, failure.UnavailableReason ?? "量測失敗。");
        if (blocked.Count == 0) return Failed(context, started, "服務未產生任何事實——不推算。");
        HardwareFact first = blocked[0];
        return Unsupported(context, started, first.UnavailableReason ?? first.Key);
    }
}
