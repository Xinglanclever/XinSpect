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
        Assert.Empty(fileSystem.DeletedPaths);
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
    public async Task 注入引擎保留結果但服務不按固定路徑建立或刪除()
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
        Assert.Empty(fileSystem.CreatedPaths);
        Assert.Empty(fileSystem.DeletedPaths);
        Assert.Contains("注入引擎未提供實際檔案路徑", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("128 MiB", result.Configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 注入引擎例外不觸發按路徑清理()
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
    public async Task 注入引擎取消不觸發按路徑清理()
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

    [Fact]
    public async Task 取得檔案期間取消會先關閉且不提交量測()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-matrix-acquire-cancel." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sentinel = Path.Combine(root, "XinSpect.deepbench.tmp");
        string? owned = null;
        Microsoft.Win32.SafeHandles.SafeFileHandle? acquired = null;
        using var cancellation = new CancellationTokenSource();
        int measurements = 0;
        try
        {
            File.WriteAllBytes(sentinel, [47, 71]);
            var engine = new DiskIoMatrixService.UnbufferedDiskIoEngine(
                directory =>
                {
                    var lease = OwnedTemporaryFile.Create(directory, "XinSpect.deepbench");
                    owned = lease.Path;
                    acquired = lease.Handle;
                    cancellation.Cancel();
                    return lease;
                },
                (context, handle, token) =>
                {
                    measurements++;
                    return Task.FromResult(new DiskIoMeasurement([FakeDiskIoEngine.ValidPoint]));
                });
            var context = new DiskIoEngineContext(sentinel, 64 * 1024 * 1024, 64 * 1024 * 1024,
                DeepBenchRunProfile.Quick, DiskIoMatrixKind.QdLadder, new Progress<DeepBenchProgress>(), new FakeDiskFileSystem());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.MeasureAsync(context, cancellation.Token));
            Assert.Equal(0, measurements);
            Assert.NotNull(acquired);
            Assert.True(acquired.IsClosed);
            Assert.NotNull(owned);
            Assert.False(File.Exists(owned));
            Assert.Equal(new byte[] { 47, 71 }, File.ReadAllBytes(sentinel));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task 提前取消不呼叫檔案factory()
    {
        int creates = 0;
        var engine = new DiskIoMatrixService.UnbufferedDiskIoEngine(_ =>
        {
            creates++;
            throw new InvalidOperationException("must not acquire");
        });
        string path = Path.Combine(Path.GetTempPath(), "uncreated-matrix", "XinSpect.deepbench.tmp");
        var context = new DiskIoEngineContext(path, 64 * 1024 * 1024, 64 * 1024 * 1024,
            DeepBenchRunProfile.Quick, DiskIoMatrixKind.QdLadder, new Progress<DeepBenchProgress>(), new FakeDiskFileSystem());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.MeasureAsync(context, new CancellationToken(true)));
        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task 檔案factory碰撞保留原bytes且不執行量測()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-matrix-collision." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "existing.tmp");
        int measurements = 0;
        try
        {
            File.WriteAllBytes(path, [19, 29]);
            var engine = new DiskIoMatrixService.UnbufferedDiskIoEngine(
                _ => OwnedTemporaryFile.CreateAtPath(path),
                (context, handle, token) =>
                {
                    measurements++;
                    return Task.FromResult(new DiskIoMeasurement([FakeDiskIoEngine.ValidPoint]));
                });
            var context = new DiskIoEngineContext(path, 64 * 1024 * 1024, 64 * 1024 * 1024,
                DeepBenchRunProfile.Quick, DiskIoMatrixKind.QdLadder, new Progress<DeepBenchProgress>(), new FakeDiskFileSystem());
            await Assert.ThrowsAsync<IOException>(() => engine.MeasureAsync(context, CancellationToken.None));
            Assert.Equal(0, measurements);
            Assert.Equal(new byte[] { 19, 29 }, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task 空間守衛拒絕保留同名既有檔案()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-fixed-plan-only");
        string existing = Path.Combine(root, "XinSpect.deepbench.tmp");
        var fs = new FakeDiskFileSystem { AvailableFreeSpace = 1 };
        fs.Files[existing] = true;
        var engine = new FakeDiskIoEngine();
        var service = new DiskIoMatrixService(DiskIoMatrixKind.QdLadder, root, 128 * 1024 * 1024, fs, engine);
        var result = await service.RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.True(fs.Exists(existing));
        Assert.Empty(fs.DeletedPaths);
        Assert.Empty(fs.CreatedPaths);
        Assert.Equal(0, engine.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task 本場handle保持到非同步操作真正結束(int terminal)
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-owned-matrix." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sentinel = Path.Combine(root, "XinSpect.deepbench.tmp");
        string? owned = null;
        Microsoft.Win32.SafeHandles.SafeFileHandle? handle = null;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<DiskIoMeasurement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task<DiskIoMeasurement>? running = null;
        try
        {
            File.WriteAllBytes(sentinel, [43]);
            var engine = new DiskIoMatrixService.UnbufferedDiskIoEngine(
                directory =>
                {
                    var lease = OwnedTemporaryFile.Create(directory, "XinSpect.deepbench");
                    owned = lease.Path;
                    return lease;
                },
                (context, opened, token) =>
                {
                    handle = opened;
                    entered.SetResult(true);
                    return finish.Task;
                });
            var context = new DiskIoEngineContext(sentinel, 64 * 1024 * 1024, 64 * 1024 * 1024,
                DeepBenchRunProfile.Quick, DiskIoMatrixKind.QdLadder, new Progress<DeepBenchProgress>(),
                new FakeDiskFileSystem());
            running = engine.MeasureAsync(context, cancellation.Token);
            await entered.Task;
            Assert.False(running.IsCompleted);
            Assert.NotNull(handle);
            Assert.False(handle.IsClosed);
            Assert.NotNull(owned);
            Assert.True(File.Exists(owned));
            // Sibling native calls may still be completing when one worker reports failure.
            Assert.False(handle.IsClosed);
            if (terminal == 1)
            {
                finish.SetCanceled();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            }
            else if (terminal == 2)
            {
                var failure = new IOException("fake final I/O failure after join");
                finish.SetException(failure);
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => running));
            }
            else if (terminal == 3)
            {
                cancellation.Cancel();
                Assert.False(running.IsCompleted);
                Assert.False(handle.IsClosed);
                finish.SetResult(new DiskIoMeasurement([FakeDiskIoEngine.ValidPoint]));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            }
            else
            {
                finish.SetResult(new DiskIoMeasurement([FakeDiskIoEngine.ValidPoint]));
                DiskIoMeasurement result = await running;
                Assert.Equal(owned, result.OwnedTempFilePath);
            }
            Assert.True(handle.IsClosed);
            Assert.False(File.Exists(owned));
            Assert.Equal(new byte[] { 43 }, File.ReadAllBytes(sentinel));
        }
        finally
        {
            cancellation.Cancel();
            finish.TrySetCanceled();
            if (running is not null)
            {
                try { await running; }
                catch { /* Preserve the original assertion failure; task is now fully settled. */ }
            }
            Directory.Delete(root, recursive: true);
        }
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
            if (Throw is not null) throw Throw;
            if (CancelInsideEngine) throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(new DiskIoMeasurement([Point]));
        }
    }
}

