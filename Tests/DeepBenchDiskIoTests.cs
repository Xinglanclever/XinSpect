using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class DeepBenchDiskIoTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public async Task 空間守衛不足會先拒絕且不叫引擎()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-deepbench-disk");
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 8 * GiB + 128 * 1024 * 1024 - 1 };
        var engine = new FakeDiskIoEngine();
        var service = new DiskIoMatrixService(DiskIoMatrixKind.QdLadder, root, 128 * 1024 * 1024, fileSystem, engine);

        var result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.True(result.Error?.Contains("8 GB", StringComparison.Ordinal) ?? false, result.Error);
        Assert.Equal(0, engine.Calls);
        Assert.Empty(fileSystem.Files);
    }

    [Fact]
    public void 執行前會揭露唯一暫存路徑與預算()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-deepbench-disk");
        var plan = DiskIoMatrixService.CreatePlan(root, 256 * 1024 * 1024, 10 * GiB);

        Assert.Equal(Path.Combine(root, "XinSpect.deepbench.tmp"), plan.TempFilePath);
        Assert.Equal("XinSpect.deepbench.tmp", Path.GetFileName(plan.TempFilePath));
        Assert.Equal(256 * 1024 * 1024, plan.TempBudgetBytes);
        Assert.Equal(10 * GiB, plan.AvailableFreeSpaceBytes);
        Assert.True(plan.FreeSpaceAfterBudgetBytes >= 8 * GiB);
    }

    [Fact]
    public async Task 成功執行只建立預設暫存檔且結束必刪()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-deepbench-disk");
        string expected = Path.Combine(root, "XinSpect.deepbench.tmp");
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var engine = new FakeDiskIoEngine();
        var service = new DiskIoMatrixService(DiskIoMatrixKind.MixedReadWrite, root, 128 * 1024 * 1024, fileSystem, engine);

        var result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(expected, engine.LastPath);
        Assert.False(fileSystem.Exists(expected));
        Assert.Equal([expected], fileSystem.CreatedPaths);
        Assert.Equal([expected], fileSystem.DeletedPaths);
        Assert.Contains(expected, result.Configuration, StringComparison.Ordinal);
        Assert.Contains("128 MiB", result.Configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 引擎例外也會關閉並刪除暫存檔()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-deepbench-disk");
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var engine = new FakeDiskIoEngine { Throw = new IOException("disk disappeared") };
        var service = new DiskIoMatrixService(DiskIoMatrixKind.QdLadder, root, 128 * 1024 * 1024, fileSystem, engine);

        var result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.PlatformError, result.FailureKind);
        Assert.Contains("disk disappeared", result.Error, StringComparison.Ordinal);
        Assert.Empty(fileSystem.Files);
    }

    [Fact]
    public async Task 取消也會刪除暫存檔()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-deepbench-disk");
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var engine = new FakeDiskIoEngine { CancelInsideEngine = true };
        var service = new DiskIoMatrixService(DiskIoMatrixKind.QdLadder, root, 128 * 1024 * 1024, fileSystem, engine);

        var result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(fileSystem.Files);
    }

    [Fact]
    public async Task QD軸與混合讀寫軸都會保留點位樣本()
    {
        var qd = await RunFakeAsync(DiskIoMatrixKind.QdLadder);
        var mixed = await RunFakeAsync(DiskIoMatrixKind.MixedReadWrite);

        Assert.Equal("storage.qd-ladder", qd.TestId);
        Assert.Equal("storage.mixed-rw", mixed.TestId);
        Assert.Contains(qd.Metrics, metric => metric.Id == "storage.qd.read.iops" && metric.Points.All(point => point.Axes.ContainsKey("blockBytes") && point.Axes.ContainsKey("queueDepth")));
        Assert.Contains(qd.Metrics, metric => metric.Id == "storage.qd.read.mibps" && metric.Unit == "MiB/s");
        Assert.Contains(qd.Metrics, metric => metric.Id == "storage.qd.read.latency_us" && metric.Unit == "µs" && metric.Points.All(point => point.Samples.Count > 0));
        Assert.Contains(mixed.Metrics, metric => metric.Id == "storage.mixed.iops" && metric.Points.All(point => point.Axes["queueDepth"] == "16" && point.Axes.ContainsKey("readPercent")));
        Assert.Contains(mixed.Metrics, metric => metric.Id == "storage.mixed.mibps");
        Assert.Contains(mixed.Metrics, metric => metric.Id == "storage.mixed.latency_us");
    }

    [Fact]
    public async Task 非有限吞吐會整場拒收而不改成零()
    {
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var engine = new FakeDiskIoEngine { Point = FakeDiskIoEngine.ValidPoint with { ThroughputSamples = [1.25, double.NaN] } };
        var service = new DiskIoMatrixService(DiskIoMatrixKind.QdLadder, Path.GetTempPath(), 128 * 1024 * 1024, fileSystem, engine);

        var result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("非有限", result.Error, StringComparison.Ordinal);
        Assert.False(fileSystem.Exists(Path.Combine(Path.GetTempPath(), "XinSpect.deepbench.tmp")));
    }

    private static async Task<DeepBenchTestResult> RunFakeAsync(DiskIoMatrixKind kind) =>
        await new DiskIoMatrixService(kind, Path.GetTempPath(), 128 * 1024 * 1024, new FakeDiskFileSystem { AvailableFreeSpace = 10 * GiB }, new FakeDiskIoEngine())
            .RunAsync(CreateContext(), CancellationToken.None);

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class FakeDiskFileSystem : IDiskIoFileSystem
    {
        public long AvailableFreeSpace { get; set; }
        public Dictionary<string, bool> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CreatedPaths { get; } = [];
        public List<string> DeletedPaths { get; } = [];
        public long GetAvailableFreeSpace(string root) => AvailableFreeSpace;
        public bool Exists(string path) => Files.TryGetValue(path, out bool exists) && exists;
        public void Create(string path)
        {
            Files[path] = true;
            CreatedPaths.Add(path);
        }
        public void Delete(string path)
        {
            if (Files.Remove(path)) DeletedPaths.Add(path);
        }
    }

    private sealed class FakeDiskIoEngine : IDiskIoEngine
    {
        public static DiskIoPointMeasurement ValidPoint { get; } = new(
            4096, 16, 100, [1234.5, 1235.5], [4.82, 4.83], [2.1, 2.2, 2.3, 2.4]);

        public int Calls;
        public string? LastPath;
        public bool CancelInsideEngine;
        public Exception? Throw;
        public DiskIoPointMeasurement Point { get; set; } = ValidPoint;

        public Task<DiskIoMeasurement> MeasureAsync(DiskIoEngineContext context, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = context.TempFilePath;
            context.FileSystem.Create(context.TempFilePath);
            if (Throw is not null) throw Throw;
            if (CancelInsideEngine) throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(new DiskIoMeasurement([Point]));
        }
    }
}

