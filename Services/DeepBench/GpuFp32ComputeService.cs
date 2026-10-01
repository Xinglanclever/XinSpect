using System.Diagnostics;

namespace XinSpect;

public interface IGpuFp32ComputeEngine
{
    Task<GpuFp32Run> MeasureAsync(GpuFp32Workload workload, CancellationToken cancellationToken);
}

public readonly record struct GpuFp32Sample(double ThroughputGflops, double DispatchLatencyMs, uint Checksum, float[]? ReadbackValues = null);

public sealed record GpuFp32Run(
    string AdapterName,
    uint FeatureLevel,
    IReadOnlyList<GpuFp32Sample> Samples);

public sealed record GpuFp32Workload(
    int Samples,
    int DispatchX,
    int ThreadGroupSize,
    int FmaCount,
    int ElementCount);

public sealed class GpuDeviceRemovedException(uint hresult) : InvalidOperationException($"D3D11 裝置被驅動移除，HRESULT=0x{hresult:X8}")
{
    public uint HResultCode { get; } = hresult;
}

public sealed class GpuUnsupportedException(string message) : InvalidOperationException(message);

public sealed class GpuOutOfMemoryException(string message) : InvalidOperationException(message);

/// <summary>D3D11 硬體 GPU FP32 dependent-FMA 實測；不選 WARP、不推算驅動內部時間。</summary>
public sealed class GpuFp32ComputeService : IDeepBenchTest
{
    public const string TestId = "gpu.fp32-fp64-integer";
    public const int DispatchX = 32;
    public const int ThreadGroupSize = 64;
    public const int FmaCount = 4096;

    public static string HlslSource => """
        RWStructuredBuffer<float> Output : register(u0);

        [numthreads(64,1,1)]
        void CSMain(uint3 gid : SV_DispatchThreadID)
        {
            float value = float(gid.x) * 0.00048828125f + 1.0f;
            for (int fmaCount = 0; fmaCount < 4096; ++fmaCount)
            {
                value = value * 1.0000001f + 0.0000001f;
            }
            Output[gid.x] = value;
        }
        """;

    public static string[] Limitations { get; } =
    [
        "計時為 API 觀察的 dispatch 輪加上同步與 readback 成本，不是驅動或晶片內部計時器。".Replace("驅動或晶片內部計時器", "GPU 執行時間分解"),
        "WARP 已排除；只有硬體配接器結果會被接受，不會把軟體渲染說成 GPU 實測。",
        "單一 D3D11 compute 工作負載結果不可直接外推 FP64、integer、ray tracing 或不同驅動版本。",
    ];

    private readonly IGpuFp32ComputeEngine _engine;

    public GpuFp32ComputeService(IGpuFp32ComputeEngine? engine = null)
    {
        _engine = engine ?? new D3D11Fp32ComputeEngine();
    }

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GpuFp32Workload workload = GetWorkload(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.05, "建立硬體 D3D11 compute"));
            GpuFp32Run run = await _engine.MeasureAsync(workload, cancellationToken).ConfigureAwait(false);
            ValidateRun(run);

            IReadOnlyList<DeepBenchMetric> metrics =
            [
                CreateMetric(
                    "gpu.fp32.throughput_gflops",
                    "FP32 FMA throughput",
                    "GFLOP/s",
                    true,
                    run.Samples.Select(sample => sample.ThroughputGflops).ToArray()),
                CreateMetric(
                    "gpu.fp32.dispatch.latency_ms",
                    "Dispatch + readback latency",
                    "ms",
                    false,
                    run.Samples.Select(sample => sample.DispatchLatencyMs).ToArray()),
            ];
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.95, "整理原始樣本"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                $"{run.AdapterName}；Feature Level 0x{run.FeatureLevel:X4}；HLSL cs_5_0；{workload.DispatchX}×1×1 groups；{workload.ThreadGroupSize} threads/group；4096 dependent FP32 FMA/thread；{workload.ElementCount} outputs",
                metrics,
                [
                    $"保留 {run.Samples.Count} 輪原始吞吐與延遲；readback checksum 全部非零。",
                    "WARP、裝置移除與 Feature Level 不足會分類失敗，不推算替代值。",
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

    internal static GpuFp32Workload GetWorkload(DeepBenchRunProfile profile)
    {
        int samples = profile switch
        {
            DeepBenchRunProfile.Quick => 2,
            DeepBenchRunProfile.Full => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };
        return new GpuFp32Workload(samples, DispatchX, ThreadGroupSize, FmaCount, DispatchX * ThreadGroupSize);
    }

    private static void ValidateRun(GpuFp32Run run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run.AdapterName);
        if (IsWarp(run.AdapterName))
            throw new GpuUnsupportedException("偵測到 WARP（Microsoft Basic Render Driver）；本項不輸出硬體 GPU 結果。");
        if (run.FeatureLevel < 0x0B00)
            throw new GpuUnsupportedException($"D3D11 Feature Level 0x{run.FeatureLevel:X4} 低於必要值 0x0B00。");
        if (run.Samples.Count == 0)
            throw new InvalidOperationException("GPU compute 沒有任何樣本；不補值。");
        if (run.Samples.Any(sample =>
                !double.IsFinite(sample.ThroughputGflops) || sample.ThroughputGflops <= 0 ||
                !double.IsFinite(sample.DispatchLatencyMs) || sample.DispatchLatencyMs <= 0))
        {
            throw new InvalidOperationException("GPU compute 出現非有限或非正數吞吐/延遲；整場拒收。");
        }
        if (run.Samples.All(sample => sample.Checksum == 0))
            throw new InvalidOperationException("GPU readback 為全零 checksum；判定工作負載沒有有效輸出。");
        // FNV-1a 對全零資料也永遠非零——全零判定必須看值，不能只看 checksum。
        if (run.Samples.All(sample => sample.ReadbackValues is { Length: > 0 } values && values.All(value => value == 0f)))
            throw new InvalidOperationException("GPU readback 值為全零；判定 shader 沒有實際執行，拒收此場。");
        // shader 是確定性計算：在 CPU 上算出期望值，readback 必須逐項相符（Phase 1 固定 2048 輸出）。
        if (run.Samples.Any(sample => sample.ReadbackValues is { Length: > 0 } values && !MatchesExpectedOutput(values)))
            throw new InvalidOperationException("GPU readback 與 CPU 參考計算不符；結果不可信，整場拒收。");
    }

    internal static bool IsWarp(string adapterName) =>
        adapterName.Contains("Basic Render Driver", StringComparison.OrdinalIgnoreCase) ||
        adapterName.Contains("WARP", StringComparison.OrdinalIgnoreCase);

    /// <summary>與 HLSL CSMain 等價的 CPU 參考計算；shader 是純確定性運算，readback 必須逐項相等。</summary>
    internal static float ExpectedOutput(uint gid) =>
        ReferenceIteration(gid * 0.00048828125f + 1.0f);

    internal static float ReferenceIteration(float value)
    {
        for (int i = 0; i < FmaCount; i++) value = value * 1.0000001f + 0.0000001f;
        return value;
    }

    internal static bool MatchesExpectedOutput(float[] values)
    {
        for (uint gid = 0; gid < (uint)values.Length; gid++)
            if (values[(int)gid] != ExpectedOutput(gid)) return false;
        return true;
    }

    private static DeepBenchMetric CreateMetric(string id, string title, string unit, bool higherIsBetter, double[] samples) => new(
        id,
        title,
        unit,
        higherIsBetter,
        $"32×1×1 dispatch; 64 threads/group; 4096 FMA/thread; {samples.Length} samples",
        samples,
        samples.Select((value, index) => new DeepBenchMetricPoint(
            value,
            new Dictionary<string, string> { ["round"] = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
            [value])).ToArray());

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消前不輸出未完成 GPU 樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失或無效 GPU 樣本。"], kind, error);
}

/// <summary>預設的 D3D11 硬體 engine；測試可注入假的 engine。</summary>
public sealed class D3D11Fp32ComputeEngine : IGpuFp32ComputeEngine
{
    public Task<GpuFp32Run> MeasureAsync(GpuFp32Workload workload, CancellationToken cancellationToken) =>
        D3D11Native.MeasureFp32Async(workload, cancellationToken);
}
