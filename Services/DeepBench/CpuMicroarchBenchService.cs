using System.Diagnostics;
using System.Globalization;

namespace XinSpect;

/// <summary>
/// CPU 載入使用延遲、ILP 觀察吞吐與分支圖樣敏感度深測。
/// 結果只描述本機 .NET/x64 JIT 與 Stopwatch 可觀察的工作負載行為，不宣稱 CPU 規格值。
/// </summary>
public sealed class CpuMicroarchBenchService(CpuMicroarchBenchService.CpuMicroarchWorkload? workloadOverride = null) : IDeepBenchTest
{
    private const string TestId = "cpu.load-use-ilp-branch";
    private const int Seed = 20261001;

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CpuMicroarchWorkload workload = workloadOverride ?? GetWorkload(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.05, "配置與暖機"));
            MicroarchSamples samples = await Task.Run(
                () => MeasureAll(workload, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.95, "整理原始樣本"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                $"載入鏈 {workload.LoadChainLength}；ILP {workload.IlpLength}；分支 {workload.BranchLength}；{workload.MeasuredRounds} 輪",
                [
                    Metric("cpu.loaduse.dependent-load-latency", "Dependent load latency", "ns/load", false, samples.LoadUse),
                    Metric("cpu.ilp.degree-1", "ILP degree 1 throughput", "Mops/s", true, samples.Ilp1),
                    Metric("cpu.ilp.degree-2", "ILP degree 2 throughput", "Mops/s", true, samples.Ilp2),
                    Metric("cpu.ilp.degree-4", "ILP degree 4 throughput", "Mops/s", true, samples.Ilp4),
                    Metric("cpu.ilp.degree-8", "ILP degree 8 throughput", "Mops/s", true, samples.Ilp8),
                    Metric("cpu.branch.predictable-throughput", "Predictable branch throughput", "Mbranches/s", true, samples.PredictableBranch),
                    Metric("cpu.branch.random-throughput", "Random branch throughput", "Mbranches/s", true, samples.RandomBranch),
                    RatioMetric(samples),
                ],
                [
                    $"每個計時段落先執行 {workload.WarmUpRounds} 輪暖機，暖機不計入樣本。",
                    "載入鏈的下一個索引由前一次載入結果決定；ILP 使用 1/2/4/8 條獨立整數鏈。",
                    "所有結果先累積成非零防刪除檢查碼，避免 JIT 把工作負載刪除。",
                ],
                [
                    "這是使用者模式 managed 程式在虛擬位址上的觀察值；不是 CPU 資料表延遲、ISA 認證或最大規格。",
                    "ILP 吞吐包含載入、整數運算、迴圈與 JIT 產生程式的成本；不是分離的硬體執行單元數。",
                    "分支比值包含資料區域性、快取與 JIT 影響；不是直接量測 branch mispredict penalty。",
                    "背景負載、頻率轉換、溫度與排程延遲會直接改變樣本。",
                ],
                DeepBenchFailureKind.None,
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (Exception exception)
        {
            return Failed(context, started, exception.Message);
        }
    }

    internal static CpuMicroarchWorkload GetWorkload(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(1, 2, 256 * 1024, 256 * 1024, 256 * 1024),
        DeepBenchRunProfile.Full => new(2, 6, 1024 * 1024, 1024 * 1024, 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static int[] CreateLoadChain(int length, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 8);
        var random = new Random(seed);
        return Enumerable.Range(0, length).Select(_ => random.Next(length)).ToArray();
    }

    internal static int[] CreateBranchInputs(int length, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 8);
        var random = new Random(seed + 1);
        return Enumerable.Range(0, length).Select(_ => random.Next(length)).ToArray();
    }

    internal static MicroarchSamples MeasureAll(CpuMicroarchWorkload workload, CancellationToken cancellationToken)
    {
        Validate(workload);
        int[] loadChain = CreateLoadChain(workload.LoadChainLength, Seed);
        int[] ilpInputs = CreateBranchInputs(workload.IlpLength, Seed + 2);
        int[] randomBranchInputs = CreateBranchInputs(workload.BranchLength, Seed + 3);
        int[] predictableBranchInputs = CreatePredictableBranchInputs(workload.BranchLength);

        List<double> loadUse = [];
        List<double> ilp1 = [];
        List<double> ilp2 = [];
        List<double> ilp4 = [];
        List<double> ilp8 = [];
        List<double> predictableBranch = [];
        List<double> randomBranch = [];
        List<double> branchRatio = [];
        long checksum = 0;

        for (int round = 0; round < workload.MeasuredRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (List<double> Samples, long Checksum) load = MeasureLoadChain(
                loadChain, workload.WarmUpRounds, cancellationToken);
            loadUse.AddRange(load.Samples);
            checksum = unchecked(checksum + load.Checksum);

            (List<double> Samples, long Checksum) degree1 = MeasureIlp(ilpInputs, 1, workload.WarmUpRounds, cancellationToken);
            (List<double> Samples, long Checksum) degree2 = MeasureIlp(ilpInputs, 2, workload.WarmUpRounds, cancellationToken);
            (List<double> Samples, long Checksum) degree4 = MeasureIlp(ilpInputs, 4, workload.WarmUpRounds, cancellationToken);
            (List<double> Samples, long Checksum) degree8 = MeasureIlp(ilpInputs, 8, workload.WarmUpRounds, cancellationToken);
            ilp1.AddRange(degree1.Samples);
            ilp2.AddRange(degree2.Samples);
            ilp4.AddRange(degree4.Samples);
            ilp8.AddRange(degree8.Samples);
            checksum = unchecked(checksum + degree1.Checksum + degree2.Checksum + degree4.Checksum + degree8.Checksum);

            (List<double> Predictable, List<double> Random, List<double> Ratios, long Checksum) branch =
                MeasureBranch(
                    predictableBranchInputs,
                    randomBranchInputs,
                    workload.WarmUpRounds,
                    cancellationToken);
            predictableBranch.AddRange(branch.Predictable);
            randomBranch.AddRange(branch.Random);
            branchRatio.AddRange(branch.Ratios);
            checksum = unchecked(checksum + branch.Checksum);
        }

        if (checksum == 0)
        {
            throw new InvalidOperationException("防刪除檢查碼為 0；工作負載沒有產生可驗證執行。");
        }

        return new MicroarchSamples(
            [.. loadUse], [.. ilp1], [.. ilp2], [.. ilp4], [.. ilp8],
            [.. predictableBranch], [.. randomBranch], [.. branchRatio], checksum);
    }

    private static (List<double> Samples, long Checksum) MeasureLoadChain(
        int[] chain, int warmUpRounds, CancellationToken cancellationToken)
    {
        int index = 0;
        long checksum = 0;
        for (int warmUp = 0; warmUp < warmUpRounds; warmUp++)
        {
            for (int i = 0; i < chain.Length; i++)
            {
                index = chain[index];
            }
        }

        var samples = new List<double>();
        for (int round = 0; round < 1; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long timestamp = Stopwatch.GetTimestamp();
            for (int i = 0; i < chain.Length; i++)
            {
                index = chain[index];
                checksum += index;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(timestamp);
            ValidateElapsed(elapsed);
            samples.Add(elapsed.TotalMilliseconds * 1_000_000.0 / chain.Length);
        }

        if (index < 0 || index >= chain.Length)
        {
            throw new InvalidOperationException("依賴載入鏈索引離開有效範圍。");
        }

        return (samples, checksum == 0 ? 1 : checksum);
    }

    private static (List<double> Samples, long Checksum) MeasureIlp(
        int[] inputs, int degree, int warmUpRounds, CancellationToken cancellationToken)
    {
        long warmChecksum = 0;
        for (int warmUp = 0; warmUp < warmUpRounds; warmUp++)
        {
            warmChecksum += ExecuteIlp(inputs, degree);
        }

        var samples = new List<double>();
        long checksum = 0;
        for (int round = 0; round < 1; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long timestamp = Stopwatch.GetTimestamp();
            long result = ExecuteIlp(inputs, degree);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(timestamp);
            checksum += result;
            ValidateElapsed(elapsed);
            samples.Add(inputs.Length / elapsed.TotalSeconds / 1_000_000.0);
        }

        return (samples, unchecked(checksum + warmChecksum));
    }

    private static long ExecuteIlp(int[] inputs, int degree)
    {
        if (inputs.Length % degree != 0)
        {
            throw new InvalidOperationException("ILP 工作負載長度必須能被平行鏈數整除。");
        }

        long a = 1;
        long b = 2;
        long c = 3;
        long d = 4;
        long e = 5;
        long f = 6;
        long g = 7;
        long h = 8;

        switch (degree)
        {
            case 1:
                for (int i = 0; i < inputs.Length; i++)
                {
                    a = a * 1664525L + inputs[i] + 1013904223L;
                }
                break;
            case 2:
                for (int i = 0; i < inputs.Length; i += 2)
                {
                    a = a * 1664525L + inputs[i] + 1013904223L;
                    b = b * 1664525L + inputs[i + 1] + 1013904223L;
                }
                break;
            case 4:
                for (int i = 0; i < inputs.Length; i += 4)
                {
                    a = a * 1664525L + inputs[i] + 1013904223L;
                    b = b * 1664525L + inputs[i + 1] + 1013904223L;
                    c = c * 1664525L + inputs[i + 2] + 1013904223L;
                    d = d * 1664525L + inputs[i + 3] + 1013904223L;
                }
                break;
            case 8:
                for (int i = 0; i < inputs.Length; i += 8)
                {
                    a = a * 1664525L + inputs[i] + 1013904223L;
                    b = b * 1664525L + inputs[i + 1] + 1013904223L;
                    c = c * 1664525L + inputs[i + 2] + 1013904223L;
                    d = d * 1664525L + inputs[i + 3] + 1013904223L;
                    e = e * 1664525L + inputs[i + 4] + 1013904223L;
                    f = f * 1664525L + inputs[i + 5] + 1013904223L;
                    g = g * 1664525L + inputs[i + 6] + 1013904223L;
                    h = h * 1664525L + inputs[i + 7] + 1013904223L;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(degree), degree, "只支援 1/2/4/8 條鏈。");
        }

        long combined = degree >= 8 ? a ^ b ^ c ^ d ^ e ^ f ^ g ^ h
            : degree >= 4 ? a ^ b ^ c ^ d
            : degree >= 2 ? a ^ b
            : a;
        return combined == 0 ? 1 : combined;
    }

    private static (List<double> Predictable, List<double> Random, List<double> Ratios, long Checksum) MeasureBranch(
        int[] predictableInputs, int[] randomInputs, int warmUpRounds, CancellationToken cancellationToken)
    {
        int threshold = predictableInputs.Length / 2;
        long predictableWarm = 0;
        long randomWarm = 0;
        for (int warmUp = 0; warmUp < warmUpRounds; warmUp++)
        {
            predictableWarm += ExecuteBranch(predictableInputs, threshold);
            randomWarm += ExecuteBranch(randomInputs, threshold);
        }

        var predictableSamples = new List<double>();
        var randomSamples = new List<double>();
        var ratios = new List<double>();
        long checksum = 0;
        for (int round = 0; round < 1; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long timestamp = Stopwatch.GetTimestamp();
            long predictableResult = ExecuteBranch(predictableInputs, threshold);
            TimeSpan predictableElapsed = Stopwatch.GetElapsedTime(timestamp);

            timestamp = Stopwatch.GetTimestamp();
            long randomResult = ExecuteBranch(randomInputs, threshold);
            TimeSpan randomElapsed = Stopwatch.GetElapsedTime(timestamp);

            ValidateElapsed(predictableElapsed);
            ValidateElapsed(randomElapsed);
            double predictable = predictableInputs.Length / predictableElapsed.TotalSeconds / 1_000_000.0;
            double random = randomInputs.Length / randomElapsed.TotalSeconds / 1_000_000.0;
            if (predictable <= 0)
            {
                throw new InvalidOperationException("可預測分支吞吐非正數；不計算比值。");
            }

            predictableSamples.Add(predictable);
            randomSamples.Add(random);
            ratios.Add(predictableElapsed.TotalSeconds / randomElapsed.TotalSeconds);
            checksum = unchecked(checksum + predictableResult + randomResult + predictableWarm + randomWarm);
        }

        return (predictableSamples, randomSamples, ratios, checksum == 0 ? 1 : checksum);
    }

    private static long ExecuteBranch(int[] inputs, int threshold)
    {
        long taken = 0;
        long notTaken = 0;
        foreach (int value in inputs)
        {
            if (value < threshold)
            {
                taken += value;
            }
            else
            {
                notTaken += value;
            }
        }

        long combined = taken ^ notTaken;
        return combined == 0 ? 1 : combined;
    }

    private static int[] CreatePredictableBranchInputs(int length)
    {
        int threshold = length / 2;
        return Enumerable.Range(0, length).Select(index => index % 64 < 32 ? threshold / 2 : threshold).ToArray();
    }

    private static void Validate(CpuMicroarchWorkload workload)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.WarmUpRounds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.MeasuredRounds, 1);
        ValidateLength(workload.LoadChainLength);
        ValidateLength(workload.IlpLength);
        ValidateLength(workload.BranchLength);
        if (workload.IlpLength % 8 != 0 || workload.BranchLength % 2 != 0)
        {
            throw new ArgumentException("ILP 長度須能被 8 整除，分支長度須能被 2 整除。");
        }
    }

    private static void ValidateLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 8);
    }

    private static void ValidateElapsed(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("計時輪時間非正數；不產生樣本。");
        }
    }

    private static DeepBenchMetric Metric(
        string id, string title, string unit, bool higherIsBetter, IReadOnlyList<double> samples) => new(
        id,
        title,
        unit,
        higherIsBetter,
        $"rounds={samples.Count}",
        samples,
        samples.Select((value, index) => new DeepBenchMetricPoint(
            value,
            new Dictionary<string, string> { ["round"] = (index + 1).ToString(CultureInfo.InvariantCulture) },
            [value])).ToArray());

    private static DeepBenchMetric RatioMetric(MicroarchSamples samples)
    {
        DeepBenchMetric metric = Metric(
            "cpu.branch.random-predictable-ratio",
            "Random branch time / predictable branch time",
            "x",
            false,
            samples.BranchRatio);
        return metric;
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消前未配置或計時任何工作負載。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不從未完成輪次推算。"], DeepBenchFailureKind.PlatformError, error);

    internal sealed record MicroarchSamples(
        IReadOnlyList<double> LoadUse,
        IReadOnlyList<double> Ilp1,
        IReadOnlyList<double> Ilp2,
        IReadOnlyList<double> Ilp4,
        IReadOnlyList<double> Ilp8,
        IReadOnlyList<double> PredictableBranch,
        IReadOnlyList<double> RandomBranch,
        IReadOnlyList<double> BranchRatio,
        long Checksum);

    public readonly record struct CpuMicroarchWorkload(
        int WarmUpRounds,
        int MeasuredRounds,
        int LoadChainLength,
        int IlpLength,
        int BranchLength);
}
