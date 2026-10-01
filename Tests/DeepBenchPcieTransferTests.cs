using Xunit;

namespace XinSpect.Tests;

public class DeepBenchPcieTransferTests
{
    private const long NominalBufferBytes = 256L * 1024 * 1024;

    [Theory]
    [InlineData(DeepBenchRunProfile.Quick, 2)]
    [InlineData(DeepBenchRunProfile.Full, 5)]
    public void Workload設定符合PCIe傳輸深測介面(DeepBenchRunProfile profile, int samples)
    {
        var workload = GpuPcieTransferService.GetWorkload(profile);

        Assert.Equal(samples, workload.Samples);
        Assert.Equal(NominalBufferBytes, workload.BufferBytes);
        Assert.Equal(NominalBufferBytes / 4, workload.ElementCount);
    }

    [Fact]
    public async Task 成功量測保留上傳與下載每輪頻寬()
    {
        var engine = new FakePcieEngine();
        var service = new GpuPcieTransferService(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(1, engine.Calls);
        DeepBenchMetric upload = result.Metrics.Single(metric => metric.Id == "gpu.pcie.upload-bandwidth-gbps");
        DeepBenchMetric download = result.Metrics.Single(metric => metric.Id == "gpu.pcie.download-bandwidth-gbps");
        Assert.Equal(2, upload.Samples.Count);
        Assert.Equal(2, download.Samples.Count);
        Assert.True(upload.HigherIsBetter);
        Assert.True(download.HigherIsBetter);
        Assert.Contains("Fake Hardware", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("256 MiB", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("獨立顯示記憶體", result.Configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 共用記憶體架構如實標示無實體PCIe()
    {
        var service = new GpuPcieTransferService(new FakePcieEngine { DedicatedVideoMemory = 0 });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Contains("共用記憶體", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("不是 PCIe link", result.Configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WARP結果永不會被接受為硬體GPU()
    {
        var service = new GpuPcieTransferService(
            new FakePcieEngine { AdapterName = "Microsoft Basic Render Driver" });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Contains("WARP", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task 驅動移除會歸類為DriverRejected()
    {
        var service = new GpuPcieTransferService(
            new FakePcieEngine { Throw = new GpuDeviceRemovedException(0x887A0006) });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.DriverRejected, result.FailureKind);
        Assert.Contains("0x887A0006", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非有限或非正數頻寬整場拒收()
    {
        var service = new GpuPcieTransferService(new FakePcieEngine
        {
            DownloadsOverride = [new(double.NaN, 5), new(18, 5)],
        });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("非有限或非正數", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 下載驗證視窗與CPU參考不符時整場拒收()
    {
        var service = new GpuPcieTransferService(new FakePcieEngine
        {
            WindowOverride = Enumerable.Repeat(0xDEADBEEFu, GpuPcieTransferService.WindowLength).ToArray(),
        });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("CPU 參考", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 預期視窗與上傳填充同源()
    {
        uint[] window = GpuPcieTransferService.ExpectedUploadWindow();

        Assert.Equal(GpuPcieTransferService.WindowLength, window.Length);
        Assert.Equal(0xC3000000u ^ 0u, window[0]);
        Assert.Equal(0xC3000000u ^ 4095u, window[^1]);
    }

    [Fact]
    public void 誠實限制不宣稱內部計時器且排除WARP()
    {
        string[] limitations = GpuPcieTransferService.Limitations;

        Assert.Contains(limitations, text => text.Contains("CopyResource", StringComparison.OrdinalIgnoreCase) && text.Contains("同步", StringComparison.Ordinal));
        Assert.DoesNotContain(limitations, text => text.Contains("驅動內部", StringComparison.Ordinal));
        Assert.Contains(limitations, text => text.Contains("WARP 已排除", StringComparison.Ordinal));
        Assert.Contains(limitations, text => text.Contains("共用記憶體", StringComparison.Ordinal));
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class FakePcieEngine : IGpuPcieTransferEngine
    {
        public int Calls;
        public string AdapterName { get; set; } = "Fake Hardware";
        public nuint DedicatedVideoMemory { get; set; } = unchecked((nuint)(8L * 1024 * 1024 * 1024));
        public Exception? Throw { get; init; }
        public IReadOnlyList<GpuPcieTransferSample>? DownloadsOverride { get; init; }
        public uint[]? WindowOverride { get; init; }

        public Task<GpuPcieTransferRun> MeasureAsync(
            GpuPcieTransferWorkload workload,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            var uploads = Enumerable.Range(1, workload.Samples)
                .Select(_ => new GpuPcieTransferSample(20, 10))
                .ToArray();
            IReadOnlyList<GpuPcieTransferSample> downloads;
            if (DownloadsOverride is not null)
            {
                downloads = DownloadsOverride;
            }
            else
            {
                uint[] window = WindowOverride ?? GpuPcieTransferService.ExpectedUploadWindow();
                uint checksum = 2166136261u;
                foreach (uint value in window)
                {
                    checksum ^= value;
                    checksum *= 16777619u;
                }

                downloads = Enumerable.Range(1, workload.Samples)
                    .Select(index => new GpuPcieTransferSample(18 + index, 11 + index * 0.5, checksum, window))
                    .ToArray();
            }

            return Task.FromResult(new GpuPcieTransferRun(
                AdapterName,
                0x0B000,
                workload.BufferBytes,
                DedicatedVideoMemory,
                uploads,
                downloads));
        }
    }
}
