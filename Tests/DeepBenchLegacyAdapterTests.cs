using System.Reflection;
using Xunit;

namespace XinSpect.Tests;

public class DeepBenchLegacyAdapterTests
{
    private sealed class FakeCacheService : CacheBenchService
    {
        public int Runs;
        public bool CancelCalled;
        public override Task RunAsync()
        {
            Runs++;
            return Task.CompletedTask;
        }
        public override void Cancel() => CancelCalled = true;
    }

    private sealed class EmptyMemoryService : MemBandwidthService
    {
        public override Task RunAsync() => Task.CompletedTask;
    }
    private sealed class FakeCoreLatencyService : CoreLatencyService
    {
        public override Task RunAsync() => Task.CompletedTask;
    }

    private sealed class FakeMemoryService : MemBandwidthService
    {
        public int Runs;
        public override Task RunAsync()
        {
            Runs++;
            Rows.Add(new MemBandwidthRow("讀取", 2, 42, 1, "raw"));
            LoadedRows.Add(new LoadedLatencyRow(2, 42, 88, 1));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task 快取Adapter會執行並保留每個工作集點位()
    {
        var service = new FakeCacheService();
        service.Rows.Add(new CacheLatencyRow("4 KB", 1.25, 0.1));
        service.Rows.Add(new CacheLatencyRow("64 MB", 88.5, 1));
        var result = await new CacheLatencyAdapter(service).RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal("memory.cache-latency", result.TestId);
        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        var metric = Assert.Single(result.Metrics);
        Assert.Equal("memory.cache.latency", metric.Id);
        Assert.Equal("ns", metric.Unit);
        Assert.Equal(2, metric.Points.Count);
        Assert.Equal("4096", metric.Points[0].Axes["workingSetBytes"]);
        Assert.Equal(88.5, metric.Points[1].Samples[0]);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("指標追逐", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 記憶體服務只跑一次並同時供STREAM與LoadedLatency使用()
    {
        var service = new FakeMemoryService();
        var stream = new StreamBandwidthAdapter(service);
        var loaded = new LoadedLatencyAdapter(service);
        var context = CreateContext();

        var streamResult = await stream.RunAsync(context, CancellationToken.None);
        var loadedResult = await loaded.RunAsync(context, CancellationToken.None);

        Assert.Equal(1, service.Runs);
        Assert.Equal("memory.stream-bandwidth", streamResult.TestId);
        Assert.Equal("memory.loaded-latency", loadedResult.TestId);
        Assert.Equal("GB/s", streamResult.Metrics[0].Unit);
        Assert.Equal("ns", loadedResult.Metrics[0].Unit);
        Assert.All(streamResult.Metrics[0].Points, point => Assert.Equal("2", point.Axes["threads"]));
        Assert.Equal("2", loadedResult.Metrics[0].Points[0].Axes["loaders"]);
    }

    [Fact]
    public async Task 取消會轉發且結果標示Cancelled()
    {
        var service = new FakeCacheService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var adapter = new CacheLatencyAdapter(service);

        var result = await adapter.RunAsync(CreateContext(), cts.Token);

        Assert.True(service.CancelCalled);
        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task 空快照不偽造數值()
    {
        var result = await new CacheLatencyAdapter(new FakeCacheService()).RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.True(result.Error?.Contains("未產生", StringComparison.Ordinal) ?? false, result.Error);
    }

    [Fact]
    public async Task 記憶體空快照不偽造STREAM或LoadedLatency數值()
    {
        var service = new EmptyMemoryService();
        var streamResult = await new StreamBandwidthAdapter(service).RunAsync(CreateContext(), CancellationToken.None);
        var loadedResult = await new LoadedLatencyAdapter(service).RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.NotRun, streamResult.FailureKind);
        Assert.Equal(DeepBenchFailureKind.NotRun, loadedResult.FailureKind);
        Assert.Empty(streamResult.Metrics);
        Assert.Empty(loadedResult.Metrics);
        Assert.True(streamResult.Error?.Contains("未產生", StringComparison.Ordinal) ?? false, streamResult.Error);
        Assert.True(loadedResult.Error?.Contains("未產生", StringComparison.Ordinal) ?? false, loadedResult.Error);
    }

    [Fact]
    public async Task 核心矩陣會保留來源與目的地軸()
    {
        var service = new FakeCoreLatencyService();
        int[] lps = [2, 7];
        double[,] matrix = { { double.NaN, 131 }, { 127, double.NaN } };
        SetField(service, "_lps", lps);
        SetField(service, "_matrixNs", matrix);

        var result = await new CoreLatencyAdapter(service).RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal("topology.core-latency", result.TestId);
        var metric = Assert.Single(result.Metrics);
        Assert.Equal(2, metric.Points.Count);
        Assert.Equal("2", metric.Points[0].Axes["fromLp"]);
        Assert.Equal("7", metric.Points[0].Axes["toLp"]);
        Assert.Equal(131, metric.Points[0].Value);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("使用者模式", StringComparison.Ordinal));
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private static void SetField(object target, string field, object value) =>
        typeof(CoreLatencyService).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
