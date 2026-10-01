using System.Diagnostics;

namespace XinSpect;

public sealed record ConfidenceAuditCase(
    string CaseId,
    string ExpectedConfidence,
    DeepBenchConfidence ActualConfidence,
    int SampleCount,
    int InvalidSampleCount,
    bool Matched);

public sealed record ConfidenceAuditMeasurement(
    IReadOnlyList<ConfidenceAuditCase> Cases,
    int ElapsedMicroseconds);

public sealed record ConfidenceAuditContext(
    DeepBenchRunProfile Profile,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public interface IConfidenceAuditEngine
{
    Task<ConfidenceAuditMeasurement> AuditAsync(ConfidenceAuditContext context, CancellationToken cancellationToken);
}

public sealed class ConfidenceAuditValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// 可信度引擎深測：用已知特性的合成樣本集（緊密、發散、含 NaN/Infinity、單樣本、全無效、離群、混池配置）
/// 逐案例驗證統計引擎的分類行為——NaN 不滲漏、百分位不內插、CV 分級與混池判定都必須落在預期答案上。
/// 量的是分類決策的一致性與耗時；它不量任何硬體。
/// </summary>
public sealed class ConfidenceEngineService(IConfidenceAuditEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "confidence.engine";

    public static string[] Limitations { get; } =
    [
        "本項不量任何硬體：它是可信度統計引擎的自我稽核，用已知答案的合成樣本集驗證分類行為，與其他 37 個測項的量測性質不同。",
        "預期答案來自引擎的公開規格（NaN 不滲漏、百分位取最近排名不內插、CV<2 為 High、CV<8 為 Medium、其餘 Low、無效樣本為 Insufficient、混池配置回 Insufficient）；規格改動時本測項同步更新。",
        "稽核只涵蓋本程式使用的統計規則；不宣稱涵蓋任何通用統計學標準。",
        "耗時指標只是參考（純 managed 計算），不是效能宣稱。",
    ];

    private readonly IConfidenceAuditEngine _engine = engine ?? new ManagedConfidenceAuditEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "準備稽核案例"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var engineContext = new ConfidenceAuditContext(context.Profile, context.Progress, cancellationToken);
            ConfidenceAuditMeasurement measurement = await _engine
                .AuditAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            Validate(measurement);
            return CreateResult(context, started, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (ConfidenceAuditValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    /// <summary>已知答案案例；回傳 (案例 ID、樣本、預期可信度)。</summary>
    internal static (string CaseId, double[] Samples, DeepBenchConfidence Expected)[] BuildCases() =>
    [
        ("tight-samples", [100.0, 100.2, 99.8, 100.1, 99.9, 100.05, 99.95, 100.1], DeepBenchConfidence.High),
        ("moderate-samples", [100.0, 104.0, 97.0, 103.0, 98.0, 102.0, 99.0, 101.0], DeepBenchConfidence.Medium),
        ("wild-samples", [50.0, 200.0, 60.0, 180.0, 70.0, 190.0, 55.0, 195.0], DeepBenchConfidence.Low),
        ("single-sample", [42.0], DeepBenchConfidence.Insufficient),
        ("all-invalid", [double.NaN, double.PositiveInfinity, double.NegativeInfinity], DeepBenchConfidence.Insufficient),
        ("nan-does-not-leak", [100.0, 100.1, double.NaN, 99.9, 100.05, 100.0, double.PositiveInfinity, 99.95], DeepBenchConfidence.High),
    ];

    private static void Validate(ConfidenceAuditMeasurement measurement)
    {
        if (measurement.Cases.Count == 0)
            throw new ConfidenceAuditValidationException("稽核沒有輸出任何案例；整場拒收。");
        foreach (ConfidenceAuditCase caseResult in measurement.Cases)
        {
            if (!caseResult.Matched)
                throw new ConfidenceAuditValidationException(
                    $"案例 {caseResult.CaseId} 分類不符（預期 {caseResult.ExpectedConfidence}、實得 {caseResult.ActualConfidence}）；統計引擎行為改變，整場拒收。");
            if (!double.IsFinite((double)measurement.ElapsedMicroseconds) || measurement.ElapsedMicroseconds < 0)
                throw new ConfidenceAuditValidationException("稽核耗時非有限或負數；整場拒收。");
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        ConfidenceAuditMeasurement measurement)
    {
        var metrics = new List<DeepBenchMetric>
        {
            new(
                "confidence.audit.matched-cases",
                "分類一致的案例數",
                "cases",
                true,
                $"{measurement.Cases.Count} 個已知答案案例",
                [measurement.Cases.Count(c => c.Matched)],
                []),
            new(
                "confidence.audit.elapsed.us",
                "稽核耗時（參考值）",
                "µs",
                false,
                "純 managed 統計計時，不是效能宣稱",
                [measurement.ElapsedMicroseconds],
                []),
        };

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{measurement.Cases.Count} 個已知答案案例自我稽核",
            metrics,
            [
                $"案例：{string.Join("、", measurement.Cases.Select(c => $"{c.CaseId}={c.ActualConfidence}{(c.Matched ? "" : "（不符！）")}"))}。",
                "分類行為與統計引擎規格一致；本項與其他量測測項性質不同，數值不參與跨域比較。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分稽核補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["稽核不完整時不推算缺失案例。"], kind, error);
}

/// <summary>managed 稽核引擎：直接呼叫 DeepBenchMeasurementStatistics 逐案例驗證。</summary>
public sealed class ManagedConfidenceAuditEngine : IConfidenceAuditEngine
{
    public Task<ConfidenceAuditMeasurement> AuditAsync(
        ConfidenceAuditContext context,
        CancellationToken cancellationToken)
        => Task.Run(() => Audit(context, cancellationToken), cancellationToken);

    private static ConfidenceAuditMeasurement Audit(ConfidenceAuditContext context, CancellationToken cancellationToken)
    {
        long timestamp = Stopwatch.GetTimestamp();
        var cases = new List<ConfidenceAuditCase>();
        foreach ((string caseId, double[] samples, DeepBenchConfidence expected) in ConfidenceEngineService.BuildCases())
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeepBenchMeasurementSummary summary = DeepBenchMeasurementStatistics.FromSamples(samples);
            bool matched = summary.Confidence == expected;
            cases.Add(new ConfidenceAuditCase(
                caseId, expected.ToString(), summary.Confidence, summary.Count, summary.InvalidSampleCount, matched));
        }

        // 混池配置：兩個不同軸值組合的點必須被判定為混池。
        DeepBenchMetric pooledMetric = CreatePooledMetric();
        bool poolsDetected = DeepBenchMeasurementStatistics.PoolsDistinctConfigurations(pooledMetric);
        if (!poolsDetected)
        {
            cases.Add(new ConfidenceAuditCase("pooled-configurations", "Insufficient", DeepBenchConfidence.Low,
                0, 0, false));
        }
        else
        {
            cases.Add(new ConfidenceAuditCase("pooled-configurations", "Insufficient", DeepBenchConfidence.Insufficient,
                0, 0, true));
        }

        int elapsedUs = checked((int)(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds * 1000d));
        return new ConfidenceAuditMeasurement(cases, elapsedUs);
    }

    private static DeepBenchMetric CreatePooledMetric() =>
        new("audit.pooled", "稽核用", "x", true, "稽核",
            [],
            [
                new(1.0, new Dictionary<string, string> { ["kernel"] = "a", ["threads"] = "1" }, [1.0]),
                new(2.0, new Dictionary<string, string> { ["kernel"] = "b", ["threads"] = "4" }, [2.0]),
            ]);
}
