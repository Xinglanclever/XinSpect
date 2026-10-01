using Xunit;

namespace XinSpect.Tests;

public class DeepBenchGpuComputeTests
{
    [Fact]
    public void ShaderSource會以Cs50編譯且包含必要工作負載()
    {
        D3D11ShaderCompilation compilation = D3D11Native.CompileShader(
            GpuFp32ComputeService.HlslSource, "CSMain", "cs_5_0");

        Assert.True(compilation.Succeeded, compilation.Error);
        Assert.True(compilation.BytecodeLength > 0);
        Assert.Contains("RWStructuredBuffer<float>", GpuFp32ComputeService.HlslSource, StringComparison.Ordinal);
        Assert.Contains("[numthreads(64,1,1)]", GpuFp32ComputeService.HlslSource, StringComparison.Ordinal);
        Assert.Contains("fmaCount < 4096", GpuFp32ComputeService.HlslSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeepBenchRunProfile.Quick, 2)]
    [InlineData(DeepBenchRunProfile.Full, 5)]
    public void Workload設定符合D3D11Compute深測介面(DeepBenchRunProfile profile, int samples)
    {
        var workload = GpuFp32ComputeService.GetWorkload(profile);

        Assert.Equal(samples, workload.Samples);
        Assert.Equal(32, workload.DispatchX);
        Assert.Equal(64, workload.ThreadGroupSize);
        Assert.Equal(4096, workload.FmaCount);
        Assert.Equal(2048, workload.ElementCount);
    }

    [Fact]
    public async Task 成功執行保留每輪吞吐延遲與有限驗證()
    {
        var engine = new FakeGpuEngine([37, 41]);
        var service = new GpuFp32ComputeService(engine);

        var result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Contains(result.Metrics, metric => metric.Id == "gpu.fp32.throughput_gflops" && metric.Samples.Count == 2);
        Assert.Contains(result.Metrics, metric => metric.Id == "gpu.fp32.dispatch.latency_ms" && metric.Samples.Count == 2);
        Assert.Contains("Fake Hardware", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("cs_5_0", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("32×1×1", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("4096", result.Configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 全零讀回會判定不穩定而不是成功()
    {
        var service = new GpuFp32ComputeService(new FakeGpuEngine([0, 0]));

        var result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("全零", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task 驅動移除會歸類為DriverRejected()
    {
        var service = new GpuFp32ComputeService(new FakeGpuEngine { Throw = new GpuDeviceRemovedException(0x887A0006) });

        var result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.DriverRejected, result.FailureKind);
        Assert.Contains("0x887A0006", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WARP結果永不會被接受為硬體GPU()
    {
        var engine = new FakeGpuEngine([1, 2]) { AdapterName = "Microsoft Basic Render Driver" };
        var service = new GpuFp32ComputeService(engine);

        var result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Contains("WARP", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 誠實限制不宣稱驅動內部時間或WARP硬體()
    {
        string[] limitations = GpuFp32ComputeService.Limitations;

        Assert.Contains(limitations, text => text.Contains("dispatch", StringComparison.OrdinalIgnoreCase) && text.Contains("readback", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(limitations, text => text.Contains("驅動內部", StringComparison.Ordinal));
        Assert.Contains(limitations, text => text.Contains("WARP 已排除", StringComparison.Ordinal));
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class FakeGpuEngine : IGpuFp32ComputeEngine
    {
        private readonly uint[]? _checksums;

        public FakeGpuEngine(uint[] checksums)
        {
            _checksums = checksums;
        }

        public FakeGpuEngine()
        {
        }

        public string AdapterName { get; set; } = "Fake Hardware";
        public Exception? Throw { get; init; }

        public Task<GpuFp32Run> MeasureAsync(GpuFp32Workload workload, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            uint[] checksums = _checksums ?? Enumerable.Range(1, workload.Samples).Select(index => (uint)(index + 9)).ToArray();
            var samples = Enumerable.Range(1, workload.Samples).Select(index => new GpuFp32Sample(
                100 + index,
                0.2 + index * 0.01,
                checksums[index - 1])).ToArray();
            return Task.FromResult(new GpuFp32Run(AdapterName, 0x0B000, samples));
        }
    }
}
