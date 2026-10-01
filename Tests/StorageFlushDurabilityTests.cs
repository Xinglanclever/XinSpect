using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class StorageFlushDurabilityTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Quick與Full使用固定輪次與間隔Flush資料長度()
    {
        var quick = StorageFlushDurabilityService.GetWorkload(DeepBenchRunProfile.Quick);
        var full = StorageFlushDurabilityService.GetWorkload(DeepBenchRunProfile.Full);

        Assert.Equal(1, quick.Rounds);
        Assert.Equal(8L * 1024 * 1024, quick.DataLengthBytes);
        Assert.Equal(2, full.Rounds);
        Assert.Equal(32L * 1024 * 1024, full.DataLengthBytes);
        Assert.Equal(1024L * 1024, StorageFlushDurabilityService.FlushBlockBytes);
    }

    [Fact]
    public async Task 成功Flush量測只用固定暫存檔並保留原始樣本()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-flush-durability");
        string expectedPath = Path.Combine(root, "XinSpect.deepbench.tmp");
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var engine = new FakeFlushEngine();
        var service = new StorageFlushDurabilityService(root, 128 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(expectedPath, engine.LastPath);
        Assert.Equal([expectedPath], fileSystem.CreatedPaths);
        Assert.Equal([expectedPath], fileSystem.DeletedPaths);
        string[] expectedMetrics =
        [
            "storage.flush-durability.write-mibps",
            "storage.flush-durability.flush-latency-us",
            "storage.flush-durability.readback-mibps",
            "storage.flush-durability.flush-count",
            "storage.flush-durability.mismatch-count"
        ];
        Assert.Equal(expectedMetrics, result.Metrics.Select(metric => metric.Id));
        Assert.All(result.Metrics, metric => Assert.All(metric.Samples, sample => Assert.True(double.IsFinite(sample) && sample >= 0)));
        Assert.Contains("FlushToDisk 不模擬斷電", string.Join('\n', result.Limitations), StringComparison.Ordinal);
        Assert.Contains("本機此次暫存檔路徑", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 讀回不一致標為不穩定並保留位置摘要()
    {
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var engine = new FakeFlushEngine
        {
            Point = FakeFlushEngine.ValidPoint with { MismatchOffsets = [4096, 1048576] }
        };
        var service = new StorageFlushDurabilityService(Path.GetTempPath(), 128 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("4096", result.Error, StringComparison.Ordinal);
        Assert.Contains("1048576", result.Error, StringComparison.Ordinal);
        Assert.Contains(
            result.Metrics,
            metric => metric.Id == "storage.flush-durability.mismatch-count" && metric.Samples.Contains(2));
        Assert.False(fileSystem.Exists(Path.Combine(Path.GetTempPath(), "XinSpect.deepbench.tmp")));
    }

    [Fact]
    public async Task 空間守衛失敗不叫引擎且不建立檔案()
    {
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 8 * GiB + 128 * 1024 * 1024 - 1 };
        var engine = new FakeFlushEngine();
        var service = new StorageFlushDurabilityService(Path.GetTempPath(), 256 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.Equal(0, engine.Calls);
        Assert.Empty(fileSystem.CreatedPaths);
        Assert.Contains("保留 8 GB", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非有限或非正數Flush樣本整場拒收()
    {
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var engine = new FakeFlushEngine
        {
            Point = FakeFlushEngine.ValidPoint with { FlushLatenciesUs = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity] }
        };
        var service = new StorageFlushDurabilityService(Path.GetTempPath(), 128 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("非有限或非正數", result.Error, StringComparison.Ordinal);
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class RecordingDiskFileSystem : IDiskIoFileSystem
    {
        public long AvailableFreeSpace { get; set; }
        public Dictionary<string, bool> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CreatedPaths { get; } = [];
        public List<string> DeletedPaths { get; } = [];

        public long GetAvailableFreeSpace(string root) => AvailableFreeSpace;
        public bool Exists(string path) => Files.TryGetValue(path, out bool exists) && exists;
        public void Create(string path) { Files[path] = true; CreatedPaths.Add(path); }
        public void Delete(string path) { if (Files.Remove(path)) DeletedPaths.Add(path); }
    }

    private sealed class FakeFlushEngine : IFlushDurabilityEngine
    {
        public static StorageFlushDurabilityPoint ValidPoint { get; } = new(
            8 * 1024 * 1024,
            1024 * 1024,
            [620],
            [35, 36, 37, 38, 39, 40, 41, 42],
            [900],
            8,
            []);

        public int Calls;
        public string? LastPath;
        public StorageFlushDurabilityPoint Point { get; set; } = ValidPoint;

        public Task<StorageFlushDurabilityMeasurement> MeasureAsync(
            FlushDurabilityContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = context.TempFilePath;
            context.FileSystem.Create(context.TempFilePath);
            return Task.FromResult(new StorageFlushDurabilityMeasurement([Point]));
        }
    }
}
