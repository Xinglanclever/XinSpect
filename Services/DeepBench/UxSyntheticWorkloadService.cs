using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XinSpect;

public sealed record SyntheticWorkloadStep(
    string StepId,
    double Milliseconds,
    double Throughput);

public sealed record SyntheticWorkloadIteration(
    int IterationIndex,
    IReadOnlyList<SyntheticWorkloadStep> Steps);

public sealed record SyntheticWorkloadMeasurement(IReadOnlyList<SyntheticWorkloadIteration> Iterations);

public sealed record SyntheticWorkloadSettings(int Iterations, int PayloadBytes);

public sealed record SyntheticWorkloadContext(
    SyntheticWorkloadSettings Settings,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public interface ISyntheticWorkloadEngine
{
    Task<SyntheticWorkloadMeasurement> MeasureAsync(SyntheticWorkloadContext context, CancellationToken cancellationToken);
}

public sealed class SyntheticWorkloadValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// 真實世界合成負載深測：序列執行三個常見應用程式樣式的 managed 步驟——
/// SHA-256 資料指紋（壓縮/雜湊管樣式）、確定性資料轉換（剖析/轉換管樣式）、合成 JSON 產生與往返剖析（序列化樣式）。
/// 量的是本程式 managed 程式碼的端到端步驟延遲；不宣稱代表任何實際應用程式。
/// </summary>
public sealed class UxSyntheticWorkloadService(ISyntheticWorkloadEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "ux.synthetic-workloads";

    public static string[] Limitations { get; } =
    [
        "三個步驟都是本程式的 managed 合成負載（SHA-256、確定性轉換、合成 JSON 往返），不是任何實際應用程式的錄製重播；延遲不外推成 UI 回應速度或軟體效能排名。",
        "步驟序列執行且只使用本行程資料；量不到 GPU、網路或使用者輸入的影響。",
        "JSON 步驟用簡單的鍵值文字協定模擬剖析成本，不是任何特定 JSON 函式庫的基準。",
        "各步驟延遲與吞吐並列輸出原始樣本；不合成單一總分。",
        "結果解讀界線：同機同場內的跨迭代趨勢有意義；跨機器比較不成立。",
    ];

    private readonly ISyntheticWorkloadEngine _engine = engine ?? new ManagedSyntheticWorkloadEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "規劃合成流程"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntheticWorkloadSettings settings = GetSettings(context.Profile);
            var engineContext = new SyntheticWorkloadContext(settings, context.Progress, cancellationToken);
            SyntheticWorkloadMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            Validate(measurement, settings);
            return CreateResult(context, started, settings, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (SyntheticWorkloadValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static SyntheticWorkloadSettings GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(8, 256 * 1024),
        DeepBenchRunProfile.Full => new(24, 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static string[] StepIds => ["sha256", "transform", "json-roundtrip"];

    private static void Validate(SyntheticWorkloadMeasurement measurement, SyntheticWorkloadSettings settings)
    {
        if (measurement.Iterations.Count != settings.Iterations)
            throw new SyntheticWorkloadValidationException("迭代數不完整；不推算缺失迭代。");
        foreach (SyntheticWorkloadIteration iteration in measurement.Iterations)
        {
            if (iteration.Steps.Count != StepIds.Length
                || iteration.Steps.Select(step => step.StepId).SequenceEqual(StepIds) == false)
                throw new SyntheticWorkloadValidationException($"第 {iteration.IterationIndex + 1} 迭代步驟不完整或順序不一致；不推算。");
            foreach (SyntheticWorkloadStep step in iteration.Steps)
            {
                if (!double.IsFinite(step.Milliseconds) || step.Milliseconds <= 0
                    || !double.IsFinite(step.Throughput) || step.Throughput <= 0)
                    throw new SyntheticWorkloadValidationException(
                        $"第 {iteration.IterationIndex + 1} 迭代步驟 {step.StepId} 出現非有限或非正數值；整場拒收。");
            }
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        SyntheticWorkloadSettings settings,
        SyntheticWorkloadMeasurement measurement)
    {
        var metrics = new List<DeepBenchMetric>();
        string[] stepUnits = ["MiB/s", "MiB/s", "MB/s"];
        string[] stepTitles = ["SHA-256 指紋（壓縮／雜湊管樣式）", "確定性資料轉換（剖析／轉換管樣式）", "合成 JSON 往返（序列化樣式）"];
        for (int stepIndex = 0; stepIndex < StepIds.Length; stepIndex++)
        {
            metrics.Add(new(
                $"ux.synthetic.{StepIds[stepIndex]}.throughput",
                stepTitles[stepIndex],
                stepUnits[stepIndex],
                true,
                $"{settings.PayloadBytes / 1024.0:0} KiB 負載 × {settings.Iterations} 迭代",
                [.. measurement.Iterations.Select(iteration => iteration.Steps[stepIndex].Throughput)],
                []));
        }

        double totalMedian = Median([.. measurement.Iterations.Select(iteration => iteration.Steps.Sum(step => step.Milliseconds))]);
        metrics.Add(new(
            "ux.synthetic.end-to-end.ms",
            "三步驟端到端延遲（中位數）",
            "ms",
            false,
            "序列執行三步驟的每迭代合計",
            [totalMedian],
            []));

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{settings.Iterations} 迭代 × 3 步驟；負載 {settings.PayloadBytes / 1024.0:0} KiB",
            metrics,
            [
                $"步驟序列執行：sha256 → transform → json-roundtrip；全部使用本行程即時生成的資料，沒有錄製負載、沒有外部檔案。",
                $"端到端中位數 {totalMedian:0.###} ms；跨迭代趨勢只在本機本場內部成立。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    internal static double Median(IReadOnlyList<double> samples)
    {
        double[] sorted = [.. samples];
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分迭代補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失步驟。"], kind, error);
}

/// <summary>managed 實測引擎：三步驟全部即時生成、逐迭代計時。</summary>
public sealed class ManagedSyntheticWorkloadEngine : ISyntheticWorkloadEngine
{
    public Task<SyntheticWorkloadMeasurement> MeasureAsync(
        SyntheticWorkloadContext context,
        CancellationToken cancellationToken)
        => Task.Run(() => Measure(context, cancellationToken), cancellationToken);

    private static SyntheticWorkloadMeasurement Measure(SyntheticWorkloadContext context, CancellationToken cancellationToken)
    {
        SyntheticWorkloadSettings settings = context.Settings;
        byte[] payload = new byte[settings.PayloadBytes];
        uint prng = 0x243F6A88u;
        for (int index = 0; index < payload.Length; index += 4)
        {
            prng ^= prng << 13; prng ^= prng >> 17; prng ^= prng << 5;
            payload[index] = (byte)prng;
            payload[index + 1] = (byte)(prng >> 8);
            payload[index + 2] = (byte)(prng >> 16);
            payload[index + 3] = (byte)(prng >> 24);
        }

        var iterations = new List<SyntheticWorkloadIteration>();
        for (int iterationIndex = 0; iterationIndex < settings.Iterations; iterationIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steps = new List<SyntheticWorkloadStep>
            {
                MeasureSha256(payload, iterationIndex),
                MeasureTransform(payload, iterationIndex),
                MeasureJsonRoundtrip(payload, iterationIndex),
            };
            context.Progress.Report(new DeepBenchProgress(
                UxSyntheticWorkloadService.TestId, iterationIndex, settings.Iterations,
                0.05 + 0.9 * iterationIndex / settings.Iterations,
                $"迭代 {iterationIndex + 1}/{settings.Iterations}"));
            iterations.Add(new SyntheticWorkloadIteration(iterationIndex, steps));
        }
        return new SyntheticWorkloadMeasurement(iterations);
    }

    private static SyntheticWorkloadStep MeasureSha256(byte[] payload, int iterationIndex)
    {
        long timestamp = Stopwatch.GetTimestamp();
        byte[] hash = SHA256.HashData(payload);
        double ms = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        if (hash.Length != 32)
            throw new SyntheticWorkloadValidationException("SHA-256 輸出長度異常；整場拒收。");
        return new SyntheticWorkloadStep(
            "sha256", ms, payload.Length / ms / 1024d / 1024d * 1000d);
    }

    private static SyntheticWorkloadStep MeasureTransform(byte[] payload, int iterationIndex)
    {
        var output = new byte[payload.Length];
        long timestamp = Stopwatch.GetTimestamp();
        uint prng = (uint)(0x9E3779B9u + iterationIndex);
        for (int index = 0; index < payload.Length; index += 4)
        {
            prng ^= prng << 13; prng ^= prng >> 17; prng ^= prng << 5;
            output[index] = (byte)(payload[index] ^ prng);
            output[index + 1] = (byte)(payload[index + 1] ^ (prng >> 8));
            output[index + 2] = (byte)(payload[index + 2] ^ (prng >> 16));
            output[index + 3] = (byte)(payload[index + 3] ^ (prng >> 24));
        }
        double ms = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        if (output.Length != payload.Length)
            throw new SyntheticWorkloadValidationException("轉換輸出長度異常；整場拒收。");
        return new SyntheticWorkloadStep(
            "transform", ms, payload.Length / ms / 1024d / 1024d * 1000d);
    }

    private static SyntheticWorkloadStep MeasureJsonRoundtrip(byte[] payload, int iterationIndex)
    {
        long timestamp = Stopwatch.GetTimestamp();
        string json = BuildSyntheticJson(payload);
        if (!VerifySyntheticJson(json, payload))
            throw new SyntheticWorkloadValidationException("合成 JSON 往返剖析驗證失敗；整場拒收。");
        double ms = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        return new SyntheticWorkloadStep(
            "json-roundtrip", ms, json.Length / ms / 1024d / 1024d * 1000d);
    }

    internal static string BuildSyntheticJson(byte[] payload)
    {
        var builder = new StringBuilder(payload.Length * 2 + 16);
        builder.Append('{');
        for (int index = 0; index + 4 <= payload.Length; index += 4)
        {
            if (index > 0) builder.Append(',');
            builder.Append("\"k").Append(index).Append("\":").Append(
                (payload[index] << 20 | payload[index + 1] << 12 | payload[index + 2] << 4 | payload[index + 3] >> 4) & 0xFFFFF);
        }
        builder.Append('}');
        return builder.ToString();
    }

    internal static bool VerifySyntheticJson(string json, byte[] payload)
    {
        int expectedPairs = payload.Length / 4;
        long sum = 0;
        int parsedPairs = 0;
        int position = 1;
        while (position < json.Length)
        {
            int keyStart = json.IndexOf("\"k", position, StringComparison.Ordinal);
            if (keyStart < 0) break;
            int colon = json.IndexOf(':', keyStart);
            if (colon < 0) return false;
            int comma = json.IndexOf(',', colon);
            int valueEnd = comma < 0 ? json.Length - 1 : comma;
            if (valueEnd <= colon)
                return false;
            if (!long.TryParse(json.AsSpan(colon + 1, valueEnd - colon - 1), out long value))
                return false;
            sum += value;
            parsedPairs++;
            position = valueEnd + 1;
        }
        return parsedPairs == expectedPairs && sum > 0;
    }
}
