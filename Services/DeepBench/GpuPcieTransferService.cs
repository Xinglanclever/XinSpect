using System.Diagnostics;

namespace XinSpect;

public interface IGpuPcieTransferEngine
{
    Task<GpuPcieTransferRun> MeasureAsync(GpuPcieTransferWorkload workload, CancellationToken cancellationToken);
}

public sealed record GpuPcieTransferSample(
    double BandwidthGBps,
    double LatencyMs,
    uint Checksum = 0,
    uint[]? Window = null);

public sealed record GpuPcieTransferRun(
    string AdapterName,
    uint FeatureLevel,
    long BufferBytes,
    nuint DedicatedVideoMemory,
    IReadOnlyList<GpuPcieTransferSample> Uploads,
    IReadOnlyList<GpuPcieTransferSample> Downloads);

public sealed record GpuPcieTransferWorkload(
    int Samples,
    long BufferBytes,
    int ElementCount);

/// <summary>
/// D3D11 硬體 GPU PCIe 上傳／下載頻寬實測：staging(Map WRITE)→CopyResource→default 為上傳，
/// default→CopyResource→staging(Map READ) 為下載；256 MiB 工作集（不足降 64／16 MiB 並如實標示）。
/// iGPU／共用記憶體沒有實體 PCIe 傳輸，結果會如實標示為記憶體搬移而非 PCIe link。
/// </summary>
public sealed class GpuPcieTransferService : IDeepBenchTest
{
    public const string TestId = "gpu.pcie-transfer";
    public const int WindowLength = 4096;
    public const long NominalBufferBytes = 256L * 1024 * 1024;
    public const uint UploadPatternBase = 0xC3000000u;
    private const long MiB = 1024 * 1024;

    public static string[] Limitations { get; } =
    [
        "計時為 API 觀察的 CopyResource 加上 GPU 同步，不是驅動或晶片內部計時器；CPU 填 staging 的前置成本不計入頻寬。",
        "WARP 已排除；只有硬體配接器結果會被接受，不會把軟體渲染說成 GPU 實測。",
        "iGPU 與共用記憶體架構沒有實體 PCIe 傳輸，結果反映的是記憶體搬移，不是 PCIe link 頻寬，設定欄會如實標示。",
        "下載只逐項驗證每輪 16 KiB 視窗；結果依驅動、電源狀態與系統負載而變，不可外推遊戲或串流效能。",
    ];

    private readonly IGpuPcieTransferEngine _engine;

    public GpuPcieTransferService(IGpuPcieTransferEngine? engine = null)
    {
        _engine = engine ?? new D3D11PcieTransferEngine();
    }

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GpuPcieTransferWorkload workload = GetWorkload(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.05, "建立硬體 D3D11 PCIe 傳輸量測"));
            GpuPcieTransferRun run = await _engine.MeasureAsync(workload, cancellationToken).ConfigureAwait(false);
            ValidateRun(workload, run);

            IReadOnlyList<DeepBenchMetric> metrics =
            [
                CreateMetric(
                    "gpu.pcie.upload-bandwidth-gbps",
                    "PCIe upload bandwidth",
                    "GB/s",
                    true,
                    run.Uploads.Select(sample => sample.BandwidthGBps).ToArray()),
                CreateMetric(
                    "gpu.pcie.download-bandwidth-gbps",
                    "PCIe download bandwidth",
                    "GB/s",
                    true,
                    run.Downloads.Select(sample => sample.BandwidthGBps).ToArray()),
            ];
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.95, "整理原始樣本"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                Describe(run),
                metrics,
                [
                    $"保留 {run.Uploads.Count} 輪上傳與 {run.Downloads.Count} 輪下載原始頻寬；每輪各搬移 {run.BufferBytes / MiB} MiB。",
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

    internal static GpuPcieTransferWorkload GetWorkload(DeepBenchRunProfile profile)
    {
        int samples = profile switch
        {
            DeepBenchRunProfile.Quick => 2,
            DeepBenchRunProfile.Full => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };
        return FromBuffer(NominalBufferBytes, samples);
    }

    internal static GpuPcieTransferWorkload FromBuffer(long bufferBytes, int samples)
    {
        if (bufferBytes <= 0 || bufferBytes % 4 != 0)
            throw new ArgumentException($"緩衝大小 {bufferBytes} 必須對齊 4 bytes。");
        if (bufferBytes / 4 < WindowLength)
            throw new ArgumentException($"緩衝必須至少容納 {WindowLength} 個驗證視窗元素。");
        return new GpuPcieTransferWorkload(samples, bufferBytes, checked((int)(bufferBytes / 4)));
    }

    /// <summary>與 native 上傳填充同源：data[i] = 0xC3000000 ^ i。</summary>
    internal static uint[] ExpectedUploadWindow(int length = WindowLength) =>
        Enumerable.Range(0, length).Select(index => UploadPatternBase ^ (uint)index).ToArray();

    private static string Describe(GpuPcieTransferRun run)
    {
        string common = $"{run.AdapterName}；Feature Level 0x{run.FeatureLevel:X4}；{run.BufferBytes / MiB} MiB buffer；";
        return run.DedicatedVideoMemory > 0
            ? $"{common}獨立顯示記憶體 {run.DedicatedVideoMemory / MiB} MiB"
            : $"{common}共用記憶體（無實體 PCIe 傳輸，量到的是記憶體搬移，不是 PCIe link）";
    }

    private static void ValidateRun(GpuPcieTransferWorkload workload, GpuPcieTransferRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run.AdapterName);
        if (GpuFp32ComputeService.IsWarp(run.AdapterName))
            throw new GpuUnsupportedException("偵測到 WARP（Microsoft Basic Render Driver）；本項不輸出硬體 GPU 結果。");
        if (run.FeatureLevel < 0x0B00)
            throw new GpuUnsupportedException($"D3D11 Feature Level 0x{run.FeatureLevel:X4} 低於必要值 0x0B00。");
        if (run.BufferBytes != workload.BufferBytes)
            throw new InvalidOperationException($"緩衝大小不符：需要 {workload.BufferBytes}，收到 {run.BufferBytes}。");
        if (run.Uploads.Count != workload.Samples || run.Downloads.Count != workload.Samples)
            throw new InvalidOperationException($"樣本數不符：需要各 {workload.Samples}，收到上傳 {run.Uploads.Count}、下載 {run.Downloads.Count}。");

        if (run.Uploads.Any(sample =>
                !double.IsFinite(sample.BandwidthGBps) || sample.BandwidthGBps <= 0 ||
                !double.IsFinite(sample.LatencyMs) || sample.LatencyMs <= 0) ||
            run.Downloads.Any(sample =>
                !double.IsFinite(sample.BandwidthGBps) || sample.BandwidthGBps <= 0 ||
                !double.IsFinite(sample.LatencyMs) || sample.LatencyMs <= 0))
        {
            throw new InvalidOperationException("PCIe 傳輸出現非有限或非正數吞吐/延遲；整場拒收。");
        }

        uint[] expected = ExpectedUploadWindow();
        foreach (GpuPcieTransferSample sample in run.Downloads)
        {
            if (sample.Checksum == 0)
                throw new InvalidOperationException("PCIe 下載 checksum 為零；判定資料無效。");
            if (sample.Window is null || sample.Window.Length != expected.Length || !sample.Window.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("PCIe 下載視窗與 CPU 參考不符；結果不可信，整場拒收。");
        }
    }

    private static DeepBenchMetric CreateMetric(string id, string title, string unit, bool higherIsBetter, double[] samples) => new(
        id,
        title,
        unit,
        higherIsBetter,
        $"{samples.Length} samples; CopyResource + Flush timed",
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
public sealed class D3D11PcieTransferEngine : IGpuPcieTransferEngine
{
    public Task<GpuPcieTransferRun> MeasureAsync(GpuPcieTransferWorkload workload, CancellationToken cancellationToken) =>
        D3D11Native.MeasurePcieTransferAsync(workload, cancellationToken);
}
