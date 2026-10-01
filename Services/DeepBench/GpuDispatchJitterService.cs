using System.Globalization;

namespace XinSpect;

public interface IGpuDispatchJitterEngine
{
    Task<GpuDispatchJitterRun> MeasureAsync(GpuDispatchJitterWorkload workload, CancellationToken cancellationToken);
}

public readonly record struct GpuDispatchJitterSample(
    double PerDispatchLatencyUs,
    uint Checksum,
    uint Sentinel);

public sealed record GpuDispatchJitterRun(
    string AdapterName,
    uint FeatureLevel,
    int DispatchesPerSample,
    nuint DedicatedVideoMemory,
    IReadOnlyList<GpuDispatchJitterSample> Samples);

public sealed record GpuDispatchJitterWorkload(
    int Samples,
    int DispatchesPerSample);

/// <summary>
/// D3D11 硬體 GPU kernel jitter：每輪送出一批 dispatch 後一次 Flush，量「API 送出＋同步」的
/// 每次攤提延遲；不做 driver 內部時間戳推算，讀回 sentinel 只驗證 kernel 真的執行。
/// </summary>
public sealed class GpuDispatchJitterService : IDeepBenchTest
{
    public const string TestId = "gpu.dispatch-jitter";
    public const uint Sentinel = 0xA17F0001u;

    public static string HlslSource => """
        RWStructuredBuffer<uint> Output : register(u0);

        [numthreads(64,1,1)]
        void CSMain(uint3 gid : SV_DispatchThreadID)
        {
            if (gid.x == 0)
                Output[0] = 0xA17F0001u;
        }
        """;

    public static string[] Limitations { get; } =
    [
        "計時是 CPU 觀察的一批 D3D11 Dispatch 加上一次 Flush；不含 CPU 讀回驗證時間，也不是驅動內部 GPU timestamp。",
        "每場先做 8 次未列入結果的暖機 dispatch，降低首次裝置喚醒與 shader 上傳造成的假高峰。",
        "sentinel 只驗證 kernel 已執行；本項量排程與同步延遲，不宣稱代表遊戲 frame time 或繪圖管線成本。",
        "排程抖動受作業系統、驅動、電源狀態與背景負載影響；原始樣本全部保留，不平均掉尖峰。",
    ];

    private readonly IGpuDispatchJitterEngine _engine;

    public GpuDispatchJitterService(IGpuDispatchJitterEngine? engine = null)
    {
        _engine = engine ?? new D3D11DispatchJitterEngine();
    }

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GpuDispatchJitterWorkload workload = GetWorkload(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.05, "建立 D3D11 dispatch jitter 量測"));
            GpuDispatchJitterRun run = await _engine.MeasureAsync(workload, cancellationToken).ConfigureAwait(false);
            ValidateRun(workload, run);

            double[] latencies = run.Samples.Select(sample => sample.PerDispatchLatencyUs).ToArray();
            double median = Percentile(latencies, 50);
            double p95 = Percentile(latencies, 95);
            double p99 = Percentile(latencies, 99);
            double jitterSpread = p95 - median;
            double dispatchesPerSecond = 1_000_000d / median;

            IReadOnlyList<DeepBenchMetric> metrics =
            [
                CreateRawMetric(latencies),
                CreateSummaryMetric("gpu.dispatch.latency-p50-us", "Dispatch latency p50", "us", false, median),
                CreateSummaryMetric("gpu.dispatch.latency-p95-us", "Dispatch latency p95", "us", false, p95),
                CreateSummaryMetric("gpu.dispatch.latency-p99-us", "Dispatch latency p99", "us", false, p99),
                CreateSummaryMetric("gpu.dispatch.jitter-p95-minus-p50-us", "Dispatch jitter spread", "us", false, jitterSpread),
                CreateSummaryMetric("gpu.dispatch.sync-throughput", "Synced dispatch throughput", "dispatch/s", true, dispatchesPerSecond),
            ];

            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.95, "整理原始延遲與百分位"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                Describe(run),
                metrics,
                [
                    $"保留 {run.Samples.Count} 輪原始每-dispatch延遲；每輪 {run.DispatchesPerSample} 次 dispatch 後同步一次。",
                    "p50／p95／p99 並列顯示；jitter spread 是 p95−p50 的絕對差，不合成總分。",
                ],
                Limitations,
                DeepBenchFailureKind.None,
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (GpuDeviceRemovedException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.DriverRejected, exception.Message);
        }
        catch (GpuUnsupportedException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unsupported, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static GpuDispatchJitterWorkload GetWorkload(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(32, 16),
        DeepBenchRunProfile.Full => new(128, 16),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static uint ExpectedChecksum()
    {
        uint checksum = 2166136261u;
        foreach (uint value in (uint[])[Sentinel, 0, 0, 0, 0, 0, 0, 0])
        {
            checksum ^= value;
            checksum *= 16777619u;
        }

        return checksum;
    }

    internal static double Percentile(double[] values, double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentile, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);
        if (values.Length == 0)
            throw new ArgumentException("百分位至少需要一個樣本。");

        double[] sorted = [.. values.OrderBy(value => value)];
        int index = (int)Math.Ceiling(percentile / 100d * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static string Describe(GpuDispatchJitterRun run) =>
        $"{run.AdapterName}；Feature Level 0x{run.FeatureLevel:X4}；{run.Samples.Count} 輪 × {run.DispatchesPerSample} dispatch；" +
        (run.DedicatedVideoMemory > 0
            ? $"獨立顯示記憶體 {run.DedicatedVideoMemory / (1024 * 1024)} MiB"
            : "共用記憶體架構");

    private static void ValidateRun(GpuDispatchJitterWorkload workload, GpuDispatchJitterRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run.AdapterName);
        if (GpuFp32ComputeService.IsWarp(run.AdapterName))
            throw new GpuUnsupportedException("偵測到 WARP（Microsoft Basic Render Driver）；本項不輸出硬體 GPU 結果。");
        if (run.FeatureLevel < 0x0B00)
            throw new GpuUnsupportedException($"D3D11 Feature Level 0x{run.FeatureLevel:X4} 低於必要值 0x0B00。");
        if (run.DispatchesPerSample != workload.DispatchesPerSample)
            throw new InvalidOperationException($"每輪 dispatch 數不符：需要 {workload.DispatchesPerSample}，收到 {run.DispatchesPerSample}。");
        if (run.Samples.Count != workload.Samples)
            throw new InvalidOperationException($"樣本數不符：需要 {workload.Samples}，收到 {run.Samples.Count}。");
        if (run.Samples.Any(sample =>
                !double.IsFinite(sample.PerDispatchLatencyUs) || sample.PerDispatchLatencyUs <= 0))
            throw new InvalidOperationException("Dispatch latency 出現非有限或非正數值；整場拒收。");
        uint expectedChecksum = ExpectedChecksum();
        if (run.Samples.Any(sample => sample.Sentinel != Sentinel || sample.Checksum != expectedChecksum))
            throw new InvalidOperationException("Dispatch sentinel 或 checksum 與 CPU 參考不符；結果不可信，整場拒收。");
    }

    private static DeepBenchMetric CreateRawMetric(double[] latencies) => new(
        "gpu.dispatch.sync-latency-us",
        "Dispatch-to-sync latency",
        "us",
        false,
        $"{latencies.Length} samples; dispatch batch + Flush timed",
        latencies,
        latencies.Select((value, index) => new DeepBenchMetricPoint(
            value,
            new Dictionary<string, string> { ["round"] = (index + 1).ToString(CultureInfo.InvariantCulture) },
            [value])).ToArray());

    private static DeepBenchMetric CreateSummaryMetric(
        string id,
        string title,
        string unit,
        bool higherIsBetter,
        double value) => new(
        id,
        title,
        unit,
        higherIsBetter,
        "derived from retained raw samples",
        [value],
        [new DeepBenchMetricPoint(value, new Dictionary<string, string>(), [value])]);

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消前不輸出未完成 GPU 樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失或無效 dispatch 樣本。"], kind, error);
}

/// <summary>預設的 D3D11 硬體 engine；測試可注入假的 engine。</summary>
public sealed class D3D11DispatchJitterEngine : IGpuDispatchJitterEngine
{
    public Task<GpuDispatchJitterRun> MeasureAsync(GpuDispatchJitterWorkload workload, CancellationToken cancellationToken) =>
        D3D11Native.MeasureDispatchJitterAsync(workload, cancellationToken);
}
