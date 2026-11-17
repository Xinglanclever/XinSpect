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

    [Theory]
    [InlineData(1, 128L * 1024 * 1024)]
    [InlineData(10L * GiB, 1)]
    public async Task RejectedPreflightRetainsExistingUnownedPlanFile(long freeBytes, long budgetBytes)
    {
        string root = Path.GetTempPath();
        string existing = Path.Combine(root, "XinSpect.deepbench.tmp");
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = freeBytes };
        fileSystem.Files[existing] = true;
        var engine = new FakeFlushEngine();
        var result = await new StorageFlushDurabilityService(root, budgetBytes, fileSystem, engine)
            .RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.Equal(0, engine.Calls);
        Assert.True(fileSystem.Exists(existing));
        Assert.Empty(fileSystem.CreatedPaths);
        Assert.Empty(fileSystem.DeletedPaths);
    }

    [Fact]
    public async Task PrecancelledRunRetainsExistingUnownedFile()
    {
        string root = Path.GetTempPath();
        string existing = Path.Combine(root, "XinSpect.deepbench.tmp");
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        fileSystem.Files[existing] = true;
        var engine = new FakeFlushEngine();
        var result = await new StorageFlushDurabilityService(root, 128L * 1024 * 1024, fileSystem, engine)
            .RunAsync(CreateContext(), new CancellationToken(true));
        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Equal(0, engine.Calls);
        Assert.True(fileSystem.Exists(existing));
        Assert.Empty(fileSystem.CreatedPaths);
        Assert.Empty(fileSystem.DeletedPaths);
    }

    [Fact]
    public async Task DefaultNativeRunRefusesBeforeAnyFilesystemCall()
    {
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var result = await new StorageFlushDurabilityService(Path.GetTempPath(), 128L * 1024 * 1024, fileSystem)
            .RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.Contains("尚未驗證重開物件所有權", result.Error);
        Assert.Empty(result.Metrics);
        Assert.Equal(0, fileSystem.Queries);
        Assert.Empty(fileSystem.CreatedPaths);
        Assert.Empty(fileSystem.DeletedPaths);
    }

    [Fact]
    public async Task InjectedLateSuccessAfterCancellationIsRejectedWithoutServiceDeletion()
    {
        using var cancel = new CancellationTokenSource();
        var completion = new TaskCompletionSource<StorageFlushDurabilityMeasurement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        string existing = Path.Combine(Path.GetTempPath(), "XinSpect.deepbench.tmp");
        fileSystem.Files[existing] = true;
        var running = new StorageFlushDurabilityService(Path.GetTempPath(), 128L * 1024 * 1024,
            fileSystem, new PendingEngine(completion.Task)).RunAsync(CreateContext(), cancel.Token);
        cancel.Cancel();
        Assert.False(running.IsCompleted);
        Assert.True(fileSystem.Exists(existing));
        completion.SetResult(new StorageFlushDurabilityMeasurement([FakeFlushEngine.ValidPoint]));
        var result = await running;
        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.True(fileSystem.Exists(existing));
        Assert.Empty(fileSystem.DeletedPaths);
    }

    [Fact]
    public async Task InjectedLateFaultAfterCancellationRemainsPlatformErrorWithoutServiceDeletion()
    {
        using var cancel = new CancellationTokenSource();
        var completion = new TaskCompletionSource<StorageFlushDurabilityMeasurement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileSystem = new RecordingDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        string existing = Path.Combine(Path.GetTempPath(), "XinSpect.deepbench.tmp");
        fileSystem.Files[existing] = true;
        var running = new StorageFlushDurabilityService(Path.GetTempPath(), 128L * 1024 * 1024,
            fileSystem, new PendingEngine(completion.Task)).RunAsync(CreateContext(), cancel.Token);
        cancel.Cancel();
        Assert.False(running.IsCompleted);
        completion.SetException(new IOException("injected late flush failure"));
        var result = await running;
        Assert.Equal(DeepBenchFailureKind.PlatformError, result.FailureKind);
        Assert.Contains("injected late flush failure", result.Error);
        Assert.Empty(result.Metrics);
        Assert.True(fileSystem.Exists(existing));
        Assert.Empty(fileSystem.DeletedPaths);
    }

    private sealed class PendingEngine(Task<StorageFlushDurabilityMeasurement> pending) : IFlushDurabilityEngine
    {
        public Task<StorageFlushDurabilityMeasurement> MeasureAsync(FlushDurabilityContext context, CancellationToken token)
            => pending;
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class RecordingDiskFileSystem : IDiskIoFileSystem
    {
        public int Queries;
        public long AvailableFreeSpace { get; set; }
        public Dictionary<string, bool> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CreatedPaths { get; } = [];
        public List<string> DeletedPaths { get; } = [];

        public long GetAvailableFreeSpace(string root) { Queries++; return AvailableFreeSpace; }
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
            try { return Task.FromResult(new StorageFlushDurabilityMeasurement([Point])); }
            finally { context.FileSystem.Delete(context.TempFilePath); }
        }
    }
}
