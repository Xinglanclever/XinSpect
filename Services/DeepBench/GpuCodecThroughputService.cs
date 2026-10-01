using System.Diagnostics;

namespace XinSpect;

public sealed record GpuCodecWorkload(
    int Width,
    int Height,
    int FrameCount,
    int FramerateNumerator,
    int FramerateDenominator,
    int BitrateBitsPerSecond);

public sealed record GpuCodecRun(
    string HardwareEncoders,
    int Frames,
    double ElapsedSeconds,
    double FramesPerSecond,
    double OutputMegabitsPerSecond,
    long OutputBytes);

public sealed record GpuCodecMeasurement(GpuCodecRun Run);

public sealed record GpuCodecContext(
    GpuCodecWorkload Workload,
    DeepBenchRunProfile Profile,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public interface IGpuCodecEngine
{
    Task<GpuCodecMeasurement> MeasureAsync(GpuCodecContext context, CancellationToken cancellationToken);
}

public sealed class GpuCodecValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// GPU codec 吞吐深測：合成 NV12 幀經 Media Foundation Sink Writer（硬體轉換啟用）編碼成記憶體內 H.264 位元流。
/// 不捆綁影片、不寫檔；量的是 Sink Writer 全管線吞吐，不宣稱裸編碼核心的驅動內部時間。
/// </summary>
public sealed class GpuCodecThroughputService(IGpuCodecEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "gpu.codec-throughput";

    public static string[] Limitations { get; } =
    [
        "量的是 Media Foundation Sink Writer 全管線（含色彩轉換、編碼與 MP4 封裝）的牆鐘吞吐；不是裸 MFT 編碼核心時間，也不是驅動內部 GPU timestamp。",
        "前置以 MFTEnumEx 確認硬體 H.264 編碼器存在並啟用 MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS；Media Foundation 實際選用的編碼器無法逐幀宣稱，如實標示。",
        "輸入是本行程即時生成的合成移動圖樣（對角漸層 NV12），沒有任何捆綁影片；複雜度與真實影片不同，碼率與幀率不外推。",
        "輸出寫進記憶體內位元流，不寫檔、不上傳；編碼失敗或輸出為零整場拒收，不補值。",
        "fps 與 Mbps 兩個明示指標並列；不合成單一總分。",
    ];

    private readonly IGpuCodecEngine _engine = engine ?? new MediaFoundationCodecEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "檢查硬體編碼器"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GpuCodecWorkload workload = GetWorkload(context.Profile);
            var engineContext = new GpuCodecContext(workload, context.Profile, context.Progress, cancellationToken);
            GpuCodecMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            Validate(measurement, workload);
            return CreateResult(context, started, workload, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (GpuUnsupportedException exception)
        {
            return Unsupported(context, started, exception.Message);
        }
        catch (GpuCodecValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static GpuCodecWorkload GetWorkload(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(1280, 720, 60, 30, 1, 4_000_000),
        DeepBenchRunProfile.Full => new(1280, 720, 240, 30, 1, 4_000_000),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    private static void Validate(GpuCodecMeasurement measurement, GpuCodecWorkload workload)
    {
        if (measurement.Run.Frames != workload.FrameCount)
            throw new GpuCodecValidationException("編碼幀數與規劃不一致；不推算。");
        if (measurement.Run.OutputBytes <= 0)
            throw new GpuCodecValidationException("編碼輸出為零；codec 未實際工作，整場拒收。");
        if (!double.IsFinite(measurement.Run.FramesPerSecond) || measurement.Run.FramesPerSecond <= 0
            || !double.IsFinite(measurement.Run.OutputMegabitsPerSecond) || measurement.Run.OutputMegabitsPerSecond <= 0)
            throw new GpuCodecValidationException("編碼吞吐出現非有限或非正數值；整場拒收。");
        if (string.IsNullOrWhiteSpace(measurement.Run.HardwareEncoders))
            throw new GpuCodecValidationException("沒有可宣稱的硬體編碼器來源；整場拒收。");
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        GpuCodecWorkload workload,
        GpuCodecMeasurement measurement)
    {
        GpuCodecRun run = measurement.Run;
        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{workload.Width}×{workload.Height} NV12；{workload.FrameCount} 幀 @ {workload.FramerateNumerator / workload.FramerateDenominator} fps；目標 {workload.BitrateBitsPerSecond / 1_000_000.0:0} Mbps",
            [
                new(
                    "gpu.codec.encode.fps",
                    "Sink Writer 編碼幀率",
                    "fps",
                    true,
                    "牆鐘時間／幀數",
                    [run.FramesPerSecond],
                    []),
                new(
                    "gpu.codec.output.mbps",
                    "輸出位元流碼率",
                    "Mbps",
                    true,
                    "輸出位元組 × 8 ／ 牆鐘時間",
                    [run.OutputMegabitsPerSecond],
                    []),
            ],
            [
                $"硬體編碼器（MFTEnumEx）：{run.HardwareEncoders}。",
                $"輸出 {run.OutputBytes / 1024.0:0} KiB H.264 位元流（記憶體內，未寫檔）；牆鐘 {run.ElapsedSeconds:0.##} 秒。",
                "量的是 Sink Writer 全管線吞吐；Media Foundation 實際選用的編碼器無法逐幀宣稱，如實標示。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string reason) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [],
            Limitations, DeepBenchFailureKind.Unsupported, reason);

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分編碼補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失結果。"], kind, error);
}
