using Xunit;

namespace XinSpect.Tests;

public class GpuCodecThroughputServiceTests
{
    [Fact]
    public async Task 輸出fps與mbps明示指標並標示硬體編碼器()
    {
        var measurement = new GpuCodecMeasurement(new GpuCodecRun(
            "NVIDIA H.264 Encoder MFT", 60, 1.0, 60.0, 8.0, 1_000_000));
        var service = new GpuCodecThroughputService(new FixedEngine(measurement));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            ["gpu.codec.encode.fps", "gpu.codec.output.mbps"],
            result.Metrics.Select(metric => metric.Id));
        Assert.Equal([60.0], result.Metrics[0].Samples);
        Assert.Equal([8.0], result.Metrics[1].Samples);
        string conditions = string.Join('\n', result.Conditions);
        Assert.Contains("NVIDIA H.264 Encoder MFT", conditions, StringComparison.Ordinal);
        Assert.Contains("未寫檔", conditions, StringComparison.Ordinal);
        Assert.Contains("無法逐幀宣稱", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 無硬體編碼器時回Unsupported不軟體冒充()
    {
        var service = new GpuCodecThroughputService(new ThrowingEngine(
            new GpuUnsupportedException("MFTEnumEx 未找到硬體 H.264 編碼器；本項不以軟體編碼冒充硬體 codec 吞吐。")));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.NotNull(result.Error);
        Assert.Contains("軟體", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 輸出為零或幀數不符或非正數都整場拒收()
    {
        var zeroOutput = new GpuCodecMeasurement(new GpuCodecRun("enc", 60, 1.0, 60.0, 0, 0));
        var wrongFrames = new GpuCodecMeasurement(new GpuCodecRun("enc", 30, 1.0, 30.0, 8.0, 1_000_000));
        var nonFinite = new GpuCodecMeasurement(new GpuCodecRun("enc", 60, 1.0, double.NaN, 8.0, 1_000_000));
        var noEncoderSource = new GpuCodecMeasurement(new GpuCodecRun("", 60, 1.0, 60.0, 8.0, 1_000_000));

        foreach (GpuCodecMeasurement measurement in new[] { zeroOutput, wrongFrames, nonFinite, noEncoderSource })
        {
            DeepBenchTestResult result = await RunAsync(new GpuCodecThroughputService(new FixedEngine(measurement)), DeepBenchRunProfile.Quick);
            Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
            Assert.Empty(result.Metrics);
        }
    }

    [Fact]
    public async Task 取消後不輸出部分編碼補值()
    {
        var service = new GpuCodecThroughputService(new CancellingEngine());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var context = new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());
        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("不輸出部分編碼補值", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public void Quick與Full使用不同幀數()
    {
        GpuCodecWorkload quick = GpuCodecThroughputService.GetWorkload(DeepBenchRunProfile.Quick);
        GpuCodecWorkload full = GpuCodecThroughputService.GetWorkload(DeepBenchRunProfile.Full);

        Assert.Equal(1280, quick.Width);
        Assert.Equal(720, quick.Height);
        Assert.Equal(60, quick.FrameCount);
        Assert.Equal(240, full.FrameCount);
        Assert.Equal(quick.BitrateBitsPerSecond, full.BitrateBitsPerSecond);
    }

    [Fact]
    public void 誠實界線明示全管線性質與不捆綁影片()
    {
        string limitations = string.Join('\n', GpuCodecThroughputService.Limitations);

        Assert.Contains("Sink Writer 全管線", limitations, StringComparison.Ordinal);
        Assert.Contains("沒有任何捆綁影片", limitations, StringComparison.Ordinal);
        Assert.Contains("不寫檔、不上傳", limitations, StringComparison.Ordinal);
        Assert.Contains("不合成單一總分", limitations, StringComparison.Ordinal);
        Assert.Contains("不是裸 MFT 編碼核心時間", limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void 合成Nv12幀逐幀有變化且尺寸正確()
    {
        var frame = new byte[320 * 240 * 3 / 2];
        MediaFoundationCodecEngine.FillSyntheticNv12(frame, 320, 240, 0);
        var frame2 = new byte[320 * 240 * 3 / 2];
        MediaFoundationCodecEngine.FillSyntheticNv12(frame2, 320, 240, 7);

        Assert.Equal(frame.Length, frame2.Length);
        Assert.NotEqual(frame.AsSpan(0, 100).ToArray(), frame2.AsSpan(0, 100).ToArray());
        Assert.Equal(128, frame[320 * 240]); // chroma 平面初始中性值
    }

    [Fact]
    public async Task Windows引擎能完成最小編碼實測()
    {
        var workload = new GpuCodecWorkload(320, 240, 8, 30, 1, 1_000_000);
        var context = new GpuCodecContext(workload, DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>(), CancellationToken.None);

        GpuCodecMeasurement measurement;
        try
        {
            measurement = await new MediaFoundationCodecEngine().MeasureAsync(context, CancellationToken.None);
        }
        catch (GpuUnsupportedException ex)
        {
            // 機器沒有硬體 H.264 編碼器時必須拒絕並給原因——不能靜默略過拒絕本身，
            // 也不能拿未量到的資料硬斷言：驗證拒絕訊息後才結束這一條。
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            return;
        }

        Assert.Equal(8, measurement.Run.Frames);
        Assert.True(measurement.Run.OutputBytes > 0);
        Assert.True(double.IsFinite(measurement.Run.FramesPerSecond) && measurement.Run.FramesPerSecond > 0);
        Assert.False(string.IsNullOrWhiteSpace(measurement.Run.HardwareEncoders));
    }

    private static async Task<DeepBenchTestResult> RunAsync(GpuCodecThroughputService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class FixedEngine(GpuCodecMeasurement measurement) : IGpuCodecEngine
    {
        public Task<GpuCodecMeasurement> MeasureAsync(GpuCodecContext context, CancellationToken cancellationToken)
            => Task.FromResult(measurement);
    }

    private sealed class ThrowingEngine(GpuUnsupportedException exception) : IGpuCodecEngine
    {
        public Task<GpuCodecMeasurement> MeasureAsync(GpuCodecContext context, CancellationToken cancellationToken)
            => Task.FromException<GpuCodecMeasurement>(exception);
    }

    private sealed class CancellingEngine : IGpuCodecEngine
    {
        public async Task<GpuCodecMeasurement> MeasureAsync(GpuCodecContext context, CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new GpuCodecMeasurement(new GpuCodecRun("enc", 0, 0, 0, 0, 0));
        }
    }
}
