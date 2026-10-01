using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class StorageIocpEngineServiceTests
{
    [Theory]
    [InlineData(DeepBenchRunProfile.Quick, 4)]
    [InlineData(DeepBenchRunProfile.Full, 6)]
    public void IOCP工作負載涵蓋Block與QueueDepth(DeepBenchRunProfile profile, int expected)
    {
        Assert.Equal(expected, StorageIocpEngineService.GetConfigurations(profile).Length);
        Assert.All(StorageIocpEngineService.GetConfigurations(profile), point =>
        {
            Assert.Equal(0, point.BlockBytes % StorageIocpEngineService.Alignment);
            Assert.True(point.QueueDepth > 0);
            Assert.True(point.Operations > 0);
        });
    }

    [Fact]
    public void 控制面固定檔名揭露路徑並保留八GB()
    {
        var plan = StorageIocpEngineService.CreatePlan("C:\\Bench", 64L * 1024 * 1024, 9L * 1024 * 1024 * 1024);

        Assert.Equal("XinSpect.iocp.tmp", Path.GetFileName(plan.TempFilePath));
        Assert.StartsWith("C:\\Bench", plan.TempFilePath, StringComparison.Ordinal);
        Assert.Equal(9596567552L, plan.FreeSpaceAfterBudgetBytes);
        Assert.Null(StorageIocpEngineService.ValidatePlan(plan));
    }

    [Theory]
    [InlineData("C:\\Other\\data.tmp", 64L * 1024 * 1024, 9L * 1024 * 1024 * 1024)]
    [InlineData("C:\\Bench\\XinSpect.iocp.tmp", 63L * 1024 * 1024, 9L * 1024 * 1024 * 1024)]
    [InlineData("C:\\Bench\\XinSpect.iocp.tmp", 64L * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    public void 不安全計畫會拒絕而不是啟動(string path, long budget, long free)
    {
        var plan = new StorageIocpPlan("C:\\Bench\\", path, budget, free, free - budget);

        Assert.NotNull(StorageIocpEngineService.ValidatePlan(plan));
    }

    [Fact]
    public async Task 成功量測保留六項原始指標並清理暫存檔()
    {
        using var directory = new TempDirectory();
        var fileSystem = new FakeFileSystem();
        var service = new StorageIocpEngineService(
            directory.Path,
            64L * 1024 * 1024,
            fileSystem,
            new FakeIocpEngine());

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(6, result.Metrics.Count);
        Assert.Contains("storage.iocp.write-iops", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("storage.iocp.read-latency-us", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("XinSpect.iocp.tmp", result.Configuration, StringComparison.Ordinal);
        Assert.Single(fileSystem.Created);
        Assert.Single(fileSystem.Deleted);
    }

    [Fact]
    public async Task 無效測量整場拒收且暫存檔仍清理()
    {
        using var directory = new TempDirectory();
        var fileSystem = new FakeFileSystem();
        var service = new StorageIocpEngineService(
            directory.Path,
            64L * 1024 * 1024,
            fileSystem,
            new FakeIocpEngine { Invalid = true });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("非有限或非正數", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Metrics);
        Assert.Single(fileSystem.Deleted);
    }

    [Fact]
    public async Task 量測例外仍保證清理並歸類PlatformError()
    {
        using var directory = new TempDirectory();
        var fileSystem = new FakeFileSystem();
        var service = new StorageIocpEngineService(
            directory.Path,
            64L * 1024 * 1024,
            fileSystem,
            new FakeIocpEngine { Throw = new InvalidOperationException("IOCP failed") });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.PlatformError, result.FailureKind);
        Assert.Contains("IOCP failed", result.Error, StringComparison.Ordinal);
        Assert.Single(fileSystem.Deleted);
    }

    [Fact]
    public void 誠實限制明示原生IOCP與驗證界線()
    {
        string[] limitations = StorageIocpEngineService.Limitations;

        Assert.Contains(limitations, text => text.Contains("CreateIoCompletionPort", StringComparison.Ordinal) && text.Contains("OVERLAPPED", StringComparison.Ordinal));
        Assert.Contains(limitations, text => text.Contains("不是 .NET async worker", StringComparison.Ordinal));
        Assert.Contains(limitations, text => text.Contains("sentinel", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(limitations, text => text.Contains("Flush durability", StringComparison.OrdinalIgnoreCase));
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "XinSpectTests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private sealed class FakeFileSystem : IDiskIoFileSystem
    {
        public List<string> Created { get; } = [];
        public List<string> Deleted { get; } = [];

        public long GetAvailableFreeSpace(string root) => 9L * 1024 * 1024 * 1024;
        public bool Exists(string path) => Created.Contains(path, StringComparer.OrdinalIgnoreCase);
        public void Create(string path) => Created.Add(path);
        public void Delete(string path) => Deleted.Add(path);
    }

    private sealed class FakeIocpEngine : IStorageIocpEngine
    {
        public bool Invalid { get; init; }
        public Exception? Throw { get; init; }

        public Task<StorageIocpMeasurement> MeasureAsync(StorageIocpContext context, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            double[] iops = Invalid ? [100, -1] : [100, 120];
            var point = new StorageIocpPoint(
                4096,
                1,
                iops,
                iops,
                iops,
                iops,
                iops,
                iops);
            return Task.FromResult(new StorageIocpMeasurement([point]));
        }
    }
}
