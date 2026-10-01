using System.Diagnostics;

namespace XinSpect;

public interface IGpuVramBandwidthEngine
{
    Task<GpuVramBandwidthRun> MeasureAsync(GpuVramBandwidthWorkload workload, CancellationToken cancellationToken);
}

public sealed record GpuVramBandwidthSample(
    double BandwidthGBps,
    double LatencyMs,
    uint Checksum,
    uint[] ReadbackWindow);

public sealed record GpuVramBandwidthRun(
    string AdapterName,
    uint FeatureLevel,
    long BufferBytes,
    IReadOnlyList<GpuVramBandwidthSample> Samples);

public sealed record GpuVramBandwidthWorkload(
    int Samples,
    int PassesPerSample,
    long BufferBytes,
    int ElementCount,
    int DispatchX,
    int DispatchY,
    int ThreadGroupSize);

/// <summary>
/// D3D11 硬體 GPU VRAM 串流讀寫頻寬實測：256 MiB 工作集（不足自動降 64／16 MiB 並如實標示），
/// 每 pass 對全緩衝做讀取＋XOR 寫回；讀與寫的流量都計入，不選 WARP、不推算內部時間。
/// </summary>
public sealed class GpuVramBandwidthService : IDeepBenchTest
{
    public const string TestId = "gpu.vram-bandwidth";
    public const int DispatchX = 4096;
    public const int ThreadGroupSize = 256;
    public const int WindowLength = 4096;
    public const long NominalBufferBytes = 256L * 1024 * 1024;
    public const uint XorKey = 0x5A5A5A5Au;
    private const long MiB = 1024 * 1024;

    public static string HlslSource => """
        RWStructuredBuffer<uint> Data : register(u0);

        [numthreads(256,1,1)]
        void CSMain(uint3 tid : SV_DispatchThreadID)
        {
            uint index = tid.y * 4096u + tid.x;
            Data[index] = Data[index] ^ 0x5A5A5A5Au;
        }
        """;

    public static string[] Limitations { get; } =
    [
        "計時為 API 觀察的 dispatch 輪加上 GPU 同步，不是驅動或晶片內部計時器；讀與寫的流量都計入。",
        "WARP 已排除；只有硬體配接器結果會被接受，不會把軟體渲染說成 GPU 實測。",
        "iGPU 與共用記憶體架構量到的是共享記憶體頻寬，不是專用 VRAM；結果依工作集大小、驅動與電源狀態而變。",
        "單一 D3D11 串流工作負載，不可外推 PCIe、3D 管線或遊戲效能。",
    ];

    private readonly IGpuVramBandwidthEngine _engine;

    public GpuVramBandwidthService(IGpuVramBandwidthEngine? engine = null)
    {
        _engine = engine ?? new D3D11VramBandwidthEngine();
    }

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GpuVramBandwidthWorkload workload = GetWorkload(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.05, "建立硬體 D3D11 串流頻寬量測"));
            GpuVramBandwidthRun run = await _engine.MeasureAsync(workload, cancellationToken).ConfigureAwait(false);
            ValidateRun(workload, run);

            IReadOnlyList<DeepBenchMetric> metrics =
            [
                CreateMetric(
                    "gpu.vram.bandwidth-gbps",
                    "Streaming read+write bandwidth",
                    "GB/s",
                    true,
                    run.Samples.Select(sample => sample.BandwidthGBps).ToArray()),
                CreateMetric(
                    "gpu.vram.pass-latency-ms",
                    "Dispatch loop latency",
                    "ms",
                    false,
                    run.Samples.Select(sample => sample.LatencyMs).ToArray()),
            ];
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.95, "整理原始樣本"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                $"{run.AdapterName}；Feature Level 0x{run.FeatureLevel:X4}；{run.BufferBytes / MiB} MiB 工作集；HLSL cs_5_0；{workload.PassesPerSample} passes/sample；每 pass 讀+寫全緩衝",
                metrics,
                [
                    $"保留 {run.Samples.Count} 輪原始頻寬與延遲；每輪 {workload.PassesPerSample} 個 pass，每 pass 讀+寫 {run.BufferBytes / MiB} MiB。",
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

    internal static GpuVramBandwidthWorkload GetWorkload(DeepBenchRunProfile profile)
    {
        (int samples, int passes) = profile switch
        {
            DeepBenchRunProfile.Quick => (2, 3),
            DeepBenchRunProfile.Full => (5, 5),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };
        return FromBuffer(NominalBufferBytes, samples, passes);
    }

    internal static GpuVramBandwidthWorkload FromBuffer(long bufferBytes, int samples, int passes)
    {
        if (bufferBytes % 4 != 0 || bufferBytes / 4 % ThreadGroupSize != 0)
            throw new ArgumentException($"緩衝大小 {bufferBytes} 必須對齊 {ThreadGroupSize} 個 uint。");
        int elementCount = checked((int)(bufferBytes / 4));
        int totalGroups = elementCount / ThreadGroupSize;
        if (totalGroups % DispatchX != 0)
            throw new ArgumentException($"工作群組數 {totalGroups} 必須被 DispatchX={DispatchX} 整除。");
        return new GpuVramBandwidthWorkload(
            samples,
            passes,
            bufferBytes,
            elementCount,
            DispatchX,
            totalGroups / DispatchX,
            ThreadGroupSize);
    }

    /// <summary>緩衝初始為全零；第 sampleIndex 輪結束後累計 (sampleIndex+1)×passes 次 XOR。</summary>
    internal static uint[] ExpectedWindow(int sampleIndex, int passes, int length = WindowLength)
    {
        uint expectedValue = (long)(sampleIndex + 1) * passes % 2 == 1 ? XorKey : 0u;
        return Enumerable.Repeat(expectedValue, length).ToArray();
    }

    private static void ValidateRun(GpuVramBandwidthWorkload workload, GpuVramBandwidthRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run.AdapterName);
        if (GpuFp32ComputeService.IsWarp(run.AdapterName))
            throw new GpuUnsupportedException("偵測到 WARP（Microsoft Basic Render Driver）；本項不輸出硬體 GPU 結果。");
        if (run.FeatureLevel < 0x0B00)
            throw new GpuUnsupportedException($"D3D11 Feature Level 0x{run.FeatureLevel:X4} 低於必要值 0x0B00。");
        if (run.Samples.Count == 0)
            throw new InvalidOperationException("VRAM 頻寬沒有任何樣本；不補值。");
        if (run.Samples.Count != workload.Samples)
            throw new InvalidOperationException($"樣本數不符：需要 {workload.Samples}，收到 {run.Samples.Count}。");
        if (run.BufferBytes <= 0 || run.BufferBytes % 4 != 0)
            throw new InvalidOperationException("緩衝大小無效。");
        if (run.Samples.Any(sample =>
                !double.IsFinite(sample.BandwidthGBps) || sample.BandwidthGBps <= 0 ||
                !double.IsFinite(sample.LatencyMs) || sample.LatencyMs <= 0))
        {
            throw new InvalidOperationException("VRAM 頻寬出現非有限或非正數吞吐/延遲；整場拒收。");
        }
        for (int index = 0; index < run.Samples.Count; index++)
        {
            GpuVramBandwidthSample sample = run.Samples[index];
            if (sample.Checksum == 0)
                throw new InvalidOperationException("VRAM readback checksum 為零；判定資料無效。");
            uint[] expected = ExpectedWindow(index, workload.PassesPerSample);
            if (sample.ReadbackWindow.Length != expected.Length || !sample.ReadbackWindow.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("VRAM readback 視窗與 CPU 參考不符；結果不可信，整場拒收。");
        }
    }

    private static DeepBenchMetric CreateMetric(string id, string title, string unit, bool higherIsBetter, double[] samples) => new(
        id,
        title,
        unit,
        higherIsBetter,
        $"4096×64 groups; 256 threads/group; {samples.Length} samples",
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
public sealed class D3D11VramBandwidthEngine : IGpuVramBandwidthEngine
{
    public Task<GpuVramBandwidthRun> MeasureAsync(GpuVramBandwidthWorkload workload, CancellationToken cancellationToken) =>
        D3D11Native.MeasureVramAsync(workload, cancellationToken);
}
