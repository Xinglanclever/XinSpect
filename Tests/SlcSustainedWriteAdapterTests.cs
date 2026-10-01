using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class SlcSustainedWriteAdapterTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Quick與Full使用固定目標寫入量()
    {
        Assert.Equal(4L * GiB, SlcSustainedWriteAdapter.GetTargetBytes(DeepBenchRunProfile.Quick));
        Assert.Equal(16L * GiB, SlcSustainedWriteAdapter.GetTargetBytes(DeepBenchRunProfile.Full));
    }

    [Fact]
    public async Task 成功量測保留原始曲線並只刪除固定暫存檔()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-slc-sustained");
        string expectedPath = Path.Combine(root, "XinSpect.deepbench.tmp");
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 24 * GiB };
        var engine = new FakeSlcEngine();
        var service = new SlcSustainedWriteAdapter(root, 128 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(expectedPath, engine.LastPath);
        Assert.Equal(4L * GiB, engine.LastTargetBytes);
        Assert.Equal([expectedPath], fileSystem.DeletedPaths);
        Assert.Equal(
        [
            "storage.slc-sustained-write.mibps",
            "storage.slc-sustained-write.peak-mibps",
            "storage.slc-sustained-write.cliff-detected",
            "storage.slc-sustained-write.post-cliff-mibps"
        ], result.Metrics.Select(metric => metric.Id));
        var curve = result.Metrics.Single(metric => metric.Id == "storage.slc-sustained-write.mibps");
        Assert.Equal(engine.Samples.Select(sample => sample.Mbps), curve.Samples);
        Assert.Equal(1100, result.Metrics.Single(metric => metric.Id == "storage.slc-sustained-write.peak-mibps").Samples.Single());
        Assert.Equal(1, result.Metrics.Single(metric => metric.Id == "storage.slc-sustained-write.cliff-detected").Samples.Single());
        Assert.Equal(265, result.Metrics.Single(metric => metric.Id == "storage.slc-sustained-write.post-cliff-mibps").Samples.Single());
        string limitations = string.Join('\n', result.Limitations);
        Assert.Contains("曲線推導", limitations, StringComparison.Ordinal);
        Assert.Contains("不是韌體快取尺寸", limitations, StringComparison.Ordinal);
        Assert.Contains("本機此次暫存檔路徑", limitations, StringComparison.Ordinal);
        Assert.Contains(expectedPath, result.Configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非有限或非正數曲線樣本整場拒收()
    {
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 24 * GiB };
        var engine = new FakeSlcEngine
        {
            Samples = [new SlcSample(1, double.PositiveInfinity)]
        };
        var service = new SlcSustainedWriteAdapter(Path.GetTempPath(), 128 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("非有限或非正數", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 目標外可用空間不足時不啟動引擎()
    {
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 8 * GiB + 128 * 1024 * 1024 };
        var engine = new FakeSlcEngine();
        var service = new SlcSustainedWriteAdapter(Path.GetTempPath(), 128 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.Equal(0, engine.Calls);
        Assert.Contains("至少 4 GiB", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 引擎失敗仍嘗試刪除暫存檔()
    {
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 24 * GiB };
        fileSystem.Files[Path.Combine(fileSystem.Root, "XinSpect.deepbench.tmp")] = true;
        var engine = new FakeSlcEngine { Throw = new IOException("disk busy") };
        var service = new SlcSustainedWriteAdapter(fileSystem.Root, 128 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.PlatformError, result.FailureKind);
        Assert.Contains("disk busy", result.Error, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(fileSystem.Root, "XinSpect.deepbench.tmp"), fileSystem.DeletedPaths);
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class RecordingDiskFileSystem : IDiskIoFileSystem
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "xinspect-slc-sustained-" + Guid.NewGuid().ToString("N"));

        public long AvailableFreeSpace { get; set; }

        public Dictionary<string, bool> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CreatedPaths { get; } = [];
        public List<string> DeletedPaths { get; } = [];

        public long GetAvailableFreeSpace(string root) => AvailableFreeSpace;
        public bool Exists(string path) => Files.TryGetValue(path, out bool exists) && exists;
        public void Create(string path) { Files[path] = true; CreatedPaths.Add(path); }
        public void Delete(string path) { if (Files.Remove(path)) DeletedPaths.Add(path); }
    }

    private sealed class FakeSlcEngine : ISlcSustainedWriteEngine
    {
        public int Calls;
        public string? LastPath;
        public long LastTargetBytes;
        public IOException? Throw;
        public List<SlcSample> Samples { get; set; } =
        [
            new(1, 1100), new(2, 1050), new(3, 1000), new(4, 900),
            new(5, 300), new(6, 290), new(7, 280), new(8, 270),
            new(9, 260), new(10, 250), new(11, 240), new(12, 230)
        ];

        public Task<SlcSustainedWriteMeasurement> MeasureAsync(
            SlcSustainedWriteContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = context.TempFilePath;
            LastTargetBytes = context.TargetBytes;
            if (Throw is not null) throw Throw;
            context.FileSystem.Create(context.TempFilePath);
            return Task.FromResult(new SlcSustainedWriteMeasurement(Samples));
        }
    }
}