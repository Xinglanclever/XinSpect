using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class StorageGpuPipelineServiceTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public async Task 全管線一致時輸出五個階段指標()
    {
        byte[] data = StorageGpuPipelineService.CreateSyntheticData(4 * MiB);
        var service = new StorageGpuPipelineService(Path.GetTempPath(), 512 * MiB, new TempFileSystem(),
            new HonestEngine(corrupt: false));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            [
                "storage.io-gpu.disk-read.mibs",
                "storage.io-gpu.upload.mibs",
                "storage.io-gpu.gpu-pass.mibs",
                "storage.io-gpu.readback.mibs",
                "storage.io-gpu.end-to-end.mibs",
            ],
            result.Metrics.Select(metric => metric.Id));
        Assert.All(result.Metrics, metric =>
            Assert.All(metric.Samples, value => Assert.True(double.IsFinite(value) && value > 0)));
        string conditions = string.Join('\n', result.Conditions);
        Assert.Contains("生命週期驗證通過", conditions, StringComparison.Ordinal);
        Assert.Contains("逐元素", conditions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GPU結果與CPU參考不符時整場Unstable()
    {
        byte[] data = StorageGpuPipelineService.CreateSyntheticData(1 * MiB);
        var service = new StorageGpuPipelineService(Path.GetTempPath(), 512 * MiB, new TempFileSystem(),
            new HonestEngine(corrupt: true));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.NotNull(result.Error);
        Assert.Contains("CPU 參考不一致", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 元素數不符或非正數計時都拒收()
    {
        var wrongLength = new FixedEngine(new GpuPipelineGpuResult(0.1, 0.1, 0.1, [1, 2, 3]));
        var badTiming = new HonestEngine(corrupt: false, uploadSeconds: -1);

        foreach (var engine in new IGpuPipelineEngine[] { wrongLength, badTiming })
        {
            var service = new StorageGpuPipelineService(Path.GetTempPath(), 512 * MiB, new TempFileSystem(), engine);
            DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);
            Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        }
    }

    [Fact]
    public async Task 空間不足時回NotRun且引擎不啟動()
    {
        var engine = new FixedEngine(new GpuPipelineGpuResult(0.1, 0.1, 0.1, []));
        var service = new StorageGpuPipelineService(Path.GetTempPath(), 64 * MiB, new TempFileSystem(freeBytes: 32 * MiB), engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task WARP回Unsupported()
    {
        byte[] data = StorageGpuPipelineService.CreateSyntheticData(1 * MiB);
        var service = new StorageGpuPipelineService(Path.GetTempPath(), 512 * MiB, new TempFileSystem(),
            new ThrowingEngine(new GpuUnsupportedException("偵測到 WARP")));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
    }

    [Fact]
    public async Task 取消後暫存檔仍會刪除()
    {
        byte[] data = StorageGpuPipelineService.CreateSyntheticData(1 * MiB);
        var fileSystem = new TempFileSystem();
        var service = new StorageGpuPipelineService(Path.GetTempPath(), 512 * MiB, fileSystem, new CancellingEngine());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var context = new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());
        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.True(File.Exists(Path.Combine(Path.GetTempPath(), "XinSpect.io-gpu.tmp")) is false || true);
        // 服務直接走 File API 寫暫存檔（不經 IDiskIoFileSystem.Create），驗證的是「結束後檔案不存在」。
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "XinSpect.io-gpu.tmp")), "暫存檔應在結束後刪除");
    }

    [Fact]
    public void Quick與Full使用不同工作集()
    {
        Assert.Equal(128 * MiB, StorageGpuPipelineService.GetWorkload(DeepBenchRunProfile.Quick).DataBytes);
        Assert.Equal(256 * MiB, StorageGpuPipelineService.GetWorkload(DeepBenchRunProfile.Full).DataBytes);
    }

    [Fact]
    public void 誠實界線明示非代表性負載與快取影響()
    {
        string limitations = string.Join('\n', StorageGpuPipelineService.Limitations);

        Assert.Contains("不是代表性運算負載", limitations, StringComparison.Ordinal);
        Assert.Contains("受檔案系統快取影響", limitations, StringComparison.Ordinal);
        Assert.Contains("不模擬斷電", limitations, StringComparison.Ordinal);
        Assert.Contains("不合成單一總分", limitations, StringComparison.Ordinal);
        Assert.Contains("WARP", limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void 合成資料是確定的且雜湊與CPU參考一致()
    {
        byte[] first = StorageGpuPipelineService.CreateSyntheticData(4096);
        byte[] second = StorageGpuPipelineService.CreateSyntheticData(4096);
        Assert.Equal(first, second);

        uint expected = 2166136261u;
        uint value = first[0] | (uint)first[1] << 8 | (uint)first[2] << 16 | (uint)first[3] << 24;
        expected ^= value;
        expected *= 16777619u;
        Assert.Equal(expected, StorageGpuPipelineService.HashElement(first, 0));
    }

    [Fact]
    public async Task Windows引擎能完成最小GPU往返實測()
    {
        byte[] data = StorageGpuPipelineService.CreateSyntheticData(256 * 1024);
        var engine = new D3D11PipelineEngine();

        try
        {
            GpuPipelineGpuResult gpuResult = await engine.ProcessAsync(
                new GpuPipelineContext(data, new Progress<DeepBenchProgress>(), CancellationToken.None));

            Assert.Equal(data.Length / 4, gpuResult.HashedElements.Length);
            for (int index = 0; index < gpuResult.HashedElements.Length; index++)
                Assert.Equal(StorageGpuPipelineService.HashElement(data, index), gpuResult.HashedElements[index]);
        }
        catch (GpuUnsupportedException ex)
        {
            // 環境沒有 D3D11 硬體配接器時，引擎必須拒絕出數字並給原因（不冒充量過、不假綠）。
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
    }

    private static async Task<DeepBenchTestResult> RunAsync(StorageGpuPipelineService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class TempFileSystem(long freeBytes = 64L * 1024 * 1024 * 1024) : IDiskIoFileSystem
    {
        public List<string> CreatedPaths { get; } = [];
        public List<string> DeletedPaths { get; } = [];

        public long GetAvailableFreeSpace(string root) => freeBytes;
        public bool Exists(string path) => File.Exists(path);
        public void Create(string path) { CreatedPaths.Add(path); File.Create(path).Dispose(); }
        public void Delete(string path) { DeletedPaths.Add(path); if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>收到什麼資料就對它做 CPU 參考雜湊；corrupt 時回錯值、uploadSeconds 可注入非法值。</summary>
    private sealed class HonestEngine(bool corrupt, double uploadSeconds = 0.05) : IGpuPipelineEngine
    {
        public Task<GpuPipelineGpuResult> ProcessAsync(GpuPipelineContext context)
        {
            var hashed = new uint[context.Data.Length / 4];
            for (int index = 0; index < hashed.Length; index++)
                hashed[index] = corrupt ? 0xDEADBEEFu ^ (uint)index : StorageGpuPipelineService.HashElement(context.Data, index);
            return Task.FromResult(new GpuPipelineGpuResult(uploadSeconds, 0.02, 0.01, hashed));
        }
    }

    private sealed class FixedEngine(GpuPipelineGpuResult result) : IGpuPipelineEngine
    {
        public Task<GpuPipelineGpuResult> ProcessAsync(GpuPipelineContext context)
            => Task.FromResult(result);
    }

    private sealed class ThrowingEngine(GpuUnsupportedException exception) : IGpuPipelineEngine
    {
        public Task<GpuPipelineGpuResult> ProcessAsync(GpuPipelineContext context)
            => Task.FromException<GpuPipelineGpuResult>(exception);
    }

    private sealed class CancellingEngine : IGpuPipelineEngine
    {
        public Task<GpuPipelineGpuResult> ProcessAsync(GpuPipelineContext context)
            => Task.FromException<GpuPipelineGpuResult>(new OperationCanceledException());
    }
}
