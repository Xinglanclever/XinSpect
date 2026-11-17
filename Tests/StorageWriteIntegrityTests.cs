using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class StorageWriteIntegrityTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Quick與Full使用固定圖樣輪次和不同資料長度()
    {
        var quick = StorageWriteIntegrityService.GetWorkload(DeepBenchRunProfile.Quick);
        var full = StorageWriteIntegrityService.GetWorkload(DeepBenchRunProfile.Full);

        Assert.Equal(1, quick.Rounds);
        Assert.Equal(8L * 1024 * 1024, quick.DataLengthBytes);
        Assert.Equal(2, full.Rounds);
        Assert.Equal(32L * 1024 * 1024, full.DataLengthBytes);
    }

    [Fact]
    public void 圖樣確定生成且錯誤位置保留相對位移()
    {
        byte[] first = StorageWriteIntegrityService.CreatePattern(StorageWriteIntegrityService.PatternId.Prng, 64, 0);
        byte[] second = StorageWriteIntegrityService.CreatePattern(StorageWriteIntegrityService.PatternId.Prng, 64, 0);
        byte[] different = StorageWriteIntegrityService.CreatePattern(StorageWriteIntegrityService.PatternId.Prng, 64, 1);

        Assert.Equal(second, first);
        Assert.NotEqual(different, first);

        byte[] expected = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] actual = [1, 9, 3, 4, 5, 9, 7, 8];
        var mismatches = StorageWriteIntegrityService.FindMismatches(expected, actual, 4096, 2);

        Assert.Equal([4097, 4101], mismatches);
    }

    [Fact]
    public async Task 注入引擎管理自己的檔案並保留原始樣本()
    {
        string root = Path.Combine(Path.GetTempPath(), "xinspect-write-integrity");
        string expected = Path.Combine(root, "XinSpect.deepbench.tmp");
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var engine = new FakeIntegrityEngine();
        var service = new StorageWriteIntegrityService(root, 128 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(expected, engine.LastPath);
        Assert.Equal([expected], fileSystem.CreatedPaths);
        Assert.Equal([expected], fileSystem.DeletedPaths);
        string[] expectedMetrics =
        [
            "storage.write-integrity.write-mibps",
            "storage.write-integrity.verify-mibps",
            "storage.write-integrity.latency-us",
            "storage.write-integrity.mismatch-count"
        ];
        Assert.Equal(expectedMetrics, result.Metrics.Select(metric => metric.Id));
        Assert.All(result.Metrics, metric => Assert.All(metric.Samples, sample => Assert.True(double.IsFinite(sample) && sample >= 0)));
        Assert.Contains("不模擬斷電", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 驗證錯誤標為不穩定並回報位置摘要()
    {
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        var engine = new FakeIntegrityEngine
        {
            Point = FakeIntegrityEngine.ValidPoint with { MismatchOffsets = [4096, 8192, 12288] }
        };
        var service = new StorageWriteIntegrityService(Path.GetTempPath(), 128 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("4096", result.Error, StringComparison.Ordinal);
        Assert.Contains("8192", result.Error, StringComparison.Ordinal);
        Assert.Contains("12288", result.Error, StringComparison.Ordinal);
        Assert.Contains(result.Metrics, metric => metric.Id == "storage.write-integrity.mismatch-count" && metric.Samples.Contains(3));
        Assert.False(fileSystem.Exists(Path.Combine(Path.GetTempPath(), "XinSpect.deepbench.tmp")));
    }

    [Fact]
    public async Task 空間守衛失敗不叫引擎且不建立檔案()
    {
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 8 * GiB + 128 * 1024 * 1024 - 1 };
        var engine = new FakeIntegrityEngine();
        var service = new StorageWriteIntegrityService(Path.GetTempPath(), 256 * 1024 * 1024, fileSystem, engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.Equal(0, engine.Calls);
        Assert.Empty(fileSystem.CreatedPaths);
    }

    [Fact]
    public async Task GuardFailureDoesNotDeleteExistingUnownedPlanPath()
    {
        string root = Path.GetTempPath();
        string existing = Path.Combine(root, "XinSpect.deepbench.tmp");
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 1 };
        fileSystem.Files[existing] = true;
        var service = new StorageWriteIntegrityService(root, 128 * 1024 * 1024, fileSystem, new FakeIntegrityEngine());
        var result = await service.RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.True(fileSystem.Exists(existing));
        Assert.Empty(fileSystem.CreatedPaths);
        Assert.Empty(fileSystem.DeletedPaths);
    }

    [Fact]
    public async Task SuccessfulInjectedEngineDoesNotAuthorizeServicePathDeletion()
    {
        string root = Path.GetTempPath();
        string existing = Path.Combine(root, "XinSpect.deepbench.tmp");
        var fileSystem = new FakeDiskFileSystem { AvailableFreeSpace = 10 * GiB };
        fileSystem.Files[existing] = true;
        var engine = new FakeIntegrityEngine { SimulateOwnedFixture = false };
        var result = await new StorageWriteIntegrityService(root, 128 * 1024 * 1024, fileSystem, engine)
            .RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.True(fileSystem.Exists(existing));
        Assert.Empty(fileSystem.DeletedPaths);
    }

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
        public void Create(string path) { Files[path] = true; CreatedPaths.Add(path); }
        public void Delete(string path) { if (Files.Remove(path)) DeletedPaths.Add(path); }
    }

    private sealed class FakeIntegrityEngine : IWriteIntegrityEngine
    {
        public static StorageWriteIntegrityPoint ValidPoint { get; } = new(
            "prng", 8 * 1024 * 1024, [400, 401], [900, 901], [50, 51], []);

        public bool SimulateOwnedFixture = true;
        public int Calls;
        public string? LastPath;
        public StorageWriteIntegrityPoint Point { get; set; } = ValidPoint;

        public Task<StorageWriteIntegrityMeasurement> MeasureAsync(
            WriteIntegrityContext context, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = context.TempFilePath;
            if (!SimulateOwnedFixture) return Task.FromResult(new StorageWriteIntegrityMeasurement([Point], context.TempFilePath));
            context.FileSystem.Create(context.TempFilePath);
            try { return Task.FromResult(new StorageWriteIntegrityMeasurement([Point], context.TempFilePath)); }
            finally { context.FileSystem.Delete(context.TempFilePath); }
        }
    }
}
