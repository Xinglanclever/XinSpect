using System.Diagnostics;
using System.Globalization;

namespace XinSpect;

public interface IBranchSpeculationEngine
{
    Task<BranchSpeculationMeasurement> MeasureAsync(
        BranchSpeculationContext context,
        CancellationToken cancellationToken);
}

public sealed record BranchSpeculationSettings(
    int InputsLength,
    int WarmupRounds,
    int MeasureRounds);

public sealed record BranchSpeculationContext(
    DeepBenchRunProfile Profile,
    BranchSpeculationSettings Settings,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public sealed record BranchPatternSamples(
    string PatternId,
    IReadOnlyList<double> Samples);

public sealed record BranchSpeculationMeasurement(
    IReadOnlyList<double> AlwaysSamples,
    IReadOnlyList<double> AlternateSamples,
    IReadOnlyList<double> ShortLoopSamples,
    IReadOnlyList<double> Random25Samples,
    IReadOnlyList<double> Random50Samples,
    IReadOnlyList<double> Random75Samples,
    IReadOnlyList<double> RandomHalfToAlwaysTimeRatios)
{
    public IReadOnlyList<BranchPatternSamples> Patterns =>
    [
        new("always", AlwaysSamples),
        new("alternate", AlternateSamples),
        new("short-loop", ShortLoopSamples),
        new("random25", Random25Samples),
        new("random50", Random50Samples),
        new("random75", Random75Samples),
    ];
}

public sealed class BranchSpeculationValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// Branch pattern matrix：用固定資料、不同分支序列量測 managed 可觀察吞吐變化。
/// 不讀取預測器計數器，也不宣稱可歸因單次 mispredict penalty。
/// </summary>
public sealed class BranchSpeculationService(
    IBranchSpeculationEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "cpu.branch-speculation";
    private const int Seed = 20261001;

    public static string[] PatternIds { get; } =
        ["always", "alternate", "short-loop", "random25", "random50", "random75"];

    public static string[] Limitations { get; } =
    [
        "量的是本程式 managed branch workload 的可觀察吞吐與時間比；不是硬體預測器計數器。",
        "圖樣間比率包含分支代價、快取、排程與 JIT 產生程式影響；不是 branch mispredict penalty。",
        "不同輸入長度、JIT 版本、頻率與背景負載都會改變樣本；不外推其他程式的分支行為。",
        "不使用特權 API；結果只代表本機 .NET/x64 執行環境。",
    ];

    private readonly IBranchSpeculationEngine _engine = engine ?? new WindowsBranchSpeculationEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "建立固定資料與分支圖樣"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BranchSpeculationSettings settings = GetSettings(context.Profile);
            var engineContext = new BranchSpeculationContext(
                context.Profile,
                settings,
                context.Progress,
                cancellationToken);
            BranchSpeculationMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            Validate(measurement);
            return CreateResult(context, started, settings, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (BranchSpeculationValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static BranchSpeculationSettings GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(512 * 1024, 1, 8),
        DeepBenchRunProfile.Full => new(2 * 1024 * 1024, 2, 24),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static bool[][] CreatePatterns(int length, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 8);
        var random = new Random(seed);
        return
        [
            [.. Enumerable.Repeat(true, length)],
            [.. Enumerable.Range(0, length).Select(index => index % 2 == 0)],
            [.. Enumerable.Range(0, length).Select(index => index % 8 != 7)],
            [.. Enumerable.Range(0, length).Select(_ => random.NextDouble() < 0.25)],
            [.. Enumerable.Range(0, length).Select(_ => random.NextDouble() < 0.50)],
            [.. Enumerable.Range(0, length).Select(_ => random.NextDouble() < 0.75)],
        ];
    }

    private static void Validate(BranchSpeculationMeasurement measurement)
    {
        int expected = measurement.AlwaysSamples.Count;
        if (measurement.AlternateSamples.Count != expected
            || measurement.ShortLoopSamples.Count != expected
            || measurement.Random25Samples.Count != expected
            || measurement.Random50Samples.Count != expected
            || measurement.Random75Samples.Count != expected
            || measurement.RandomHalfToAlwaysTimeRatios.Count != expected)
            throw new BranchSpeculationValidationException("六種分支圖樣樣本數不一致；不推算缺失樣本。");
        if (expected == 0)
            throw new BranchSpeculationValidationException("沒有任何 branch pattern 樣本；不輸出空結果。");
        if (!IsFinitePositive(measurement.AlwaysSamples)
            || !IsFinitePositive(measurement.AlternateSamples)
            || !IsFinitePositive(measurement.ShortLoopSamples)
            || !IsFinitePositive(measurement.Random25Samples)
            || !IsFinitePositive(measurement.Random50Samples)
            || !IsFinitePositive(measurement.Random75Samples)
            || !IsFinitePositive(measurement.RandomHalfToAlwaysTimeRatios))
            throw new BranchSpeculationValidationException("分支吞吐或比率出現非有限或非正數樣本；整場拒收。");
    }

    private static bool IsFinitePositive(IReadOnlyList<double> samples) =>
        samples.All(value => double.IsFinite(value) && value > 0);

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        BranchSpeculationSettings settings,
        BranchSpeculationMeasurement measurement)
    {
        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{settings.InputsLength / 1024.0:0.#} KiB inputs；{settings.MeasureRounds} rounds",
            [
                Metric("cpu.branchspec.always.mops", "Always-taken", "Mbranches/s", true, measurement.AlwaysSamples),
                Metric("cpu.branchspec.alternate.mops", "Alternating", "Mbranches/s", true, measurement.AlternateSamples),
                Metric("cpu.branchspec.short-loop.mops", "Short loop", "Mbranches/s", true, measurement.ShortLoopSamples),
                Metric("cpu.branchspec.random25.mops", "Random 25% taken", "Mbranches/s", true, measurement.Random25Samples),
                Metric("cpu.branchspec.random50.mops", "Random 50% taken", "Mbranches/s", true, measurement.Random50Samples),
                Metric("cpu.branchspec.random75.mops", "Random 75% taken", "Mbranches/s", true, measurement.Random75Samples),
                Metric(
                    "cpu.branchspec.random50-always-time.ratio",
                    "Random50 / always time",
                    "ratio",
                    false,
                    measurement.RandomHalfToAlwaysTimeRatios),
            ],
            [
                $"實測 {settings.MeasureRounds} 回；固定 byte 資料；圖樣順序 always → alternate → short-loop → random25/50/75。",
                "時間比率軸：always → random50；數值大於 1 表示隨機 50% 圖樣耗時更長。",
                "隨機序列使用固定 seed，只描述本機可重現工作負載，不是統計母體證明。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchMetric Metric(
        string id,
        string title,
        string unit,
        bool higherIsBetter,
        IReadOnlyList<double> samples) =>
        new(id, title, unit, higherIsBetter, "pinned managed branch matrix", samples, []);

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分圖樣補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失圖樣。"], kind, error);
}

/// <summary>Windows 使用者模式實作；單執行緒、固定輸入，不使用特權 API。</summary>
public sealed class WindowsBranchSpeculationEngine : IBranchSpeculationEngine
{
    public Task<BranchSpeculationMeasurement> MeasureAsync(
        BranchSpeculationContext context,
        CancellationToken cancellationToken)
        => Task.Run(() => Measure(context, cancellationToken), cancellationToken);

    private static BranchSpeculationMeasurement Measure(
        BranchSpeculationContext context,
        CancellationToken cancellationToken)
    {
        int length = context.Settings.InputsLength;
        byte[] data = [.. Enumerable.Range(0, length).Select(index => (byte)(32 + index % 192))];
        bool[][] patterns = BranchSpeculationService.CreatePatterns(length, BranchSpeculationService.PatternIds.Length + SeedValue());
        int totalScenarios = context.Settings.WarmupRounds + context.Settings.MeasureRounds;
        var always = new List<double>();
        var alternate = new List<double>();
        var shortLoop = new List<double>();
        var random25 = new List<double>();
        var random50 = new List<double>();
        var random75 = new List<double>();
        var ratios = new List<double>();

        for (int round = 0; round < totalScenarios; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isWarmup = round < context.Settings.WarmupRounds;
            context.Progress.Report(new DeepBenchProgress(
                BranchSpeculationService.TestId,
                isWarmup ? 0 : always.Count + 1,
                context.Settings.MeasureRounds,
                0.05 + 0.90 * (isWarmup ? 0 : always.Count + 1) / context.Settings.MeasureRounds,
                isWarmup ? $"warmup {round + 1}/{totalScenarios}" : $"量測 {always.Count + 1}/{context.Settings.MeasureRounds}"));

            (double Throughput, TimeSpan Elapsed) alwaysMeasured = MeasurePattern(patterns[0], data, cancellationToken);
            (double Throughput, TimeSpan Elapsed) alternateMeasured = MeasurePattern(patterns[1], data, cancellationToken);
            (double Throughput, TimeSpan Elapsed) shortLoopMeasured = MeasurePattern(patterns[2], data, cancellationToken);
            (double Throughput, TimeSpan Elapsed) random25Measured = MeasurePattern(patterns[3], data, cancellationToken);
            (double Throughput, TimeSpan Elapsed) random50Measured = MeasurePattern(patterns[4], data, cancellationToken);
            (double Throughput, TimeSpan Elapsed) random75Measured = MeasurePattern(patterns[5], data, cancellationToken);
            if (isWarmup) continue;

            always.Add(alwaysMeasured.Throughput);
            alternate.Add(alternateMeasured.Throughput);
            shortLoop.Add(shortLoopMeasured.Throughput);
            random25.Add(random25Measured.Throughput);
            random50.Add(random50Measured.Throughput);
            random75.Add(random75Measured.Throughput);
            if (alwaysMeasured.Elapsed.TotalSeconds <= 0 || random50Measured.Elapsed.TotalSeconds <= 0)
                throw new BranchSpeculationValidationException("分支計時時間異常。");
            double ratio = random50Measured.Elapsed.TotalSeconds / alwaysMeasured.Elapsed.TotalSeconds;
            if (!double.IsFinite(ratio) || ratio <= 0)
                throw new BranchSpeculationValidationException("隨機／固定分支時間比率非有限或非正數。");
            ratios.Add(ratio);
        }

        return new BranchSpeculationMeasurement(
            always, alternate, shortLoop, random25, random50, random75, ratios);
    }

    private static int SeedValue() => 20261001;

    private static (double Throughput, TimeSpan Elapsed) MeasurePattern(
        bool[] pattern,
        byte[] data,
        CancellationToken cancellationToken)
    {
        long timestamp = Stopwatch.GetTimestamp();
        long checksum = Execute(pattern, data);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(timestamp);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateResult(elapsed, checksum);
        return (pattern.Length / elapsed.TotalSeconds / 1_000_000d, elapsed);
    }

    private static long Execute(bool[] pattern, byte[] data)
    {
        long taken = 0;
        long notTaken = 1;
        for (int index = 0; index < pattern.Length; index++)
        {
            if (pattern[index])
                taken += data[index];
            else
                notTaken ^= data[index];
        }

        return taken ^ notTaken;
    }

    private static void ValidateResult(TimeSpan elapsed, long checksum)
    {
        if (elapsed <= TimeSpan.Zero)
            throw new BranchSpeculationValidationException("分支工作負載計時非正數。");
        if (checksum == 0)
            throw new BranchSpeculationValidationException("防刪除檢查碼為 0；分支工作負載沒有可驗證執行。");
    }
}
