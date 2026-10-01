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

    private class FakeTopDownService : TopDownService
    {
        public int SampleCalls;
        public bool Support;

        public override bool RunSupportedProbe() => Support;

        public override Task SampleAsync(CancellationToken cancellationToken)
        {
            SampleCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            Buckets.Add(new TopDownBucket("退休 Retiring", "note", 64));
            Buckets.Add(new TopDownBucket("錯誤推測 Bad Speculation", "note", 8));
            Buckets.Add(new TopDownBucket("前端受限 Frontend Bound", "note", 6));
            Buckets.Add(new TopDownBucket("後端受限 Backend Bound", "note", 22));
            Rows.Add(new TopDownCoreRow(0, "0,4", true, 60, 10, 8, 22, 3));
            Rows.Add(new TopDownCoreRow(1, "1,5", true, 68, 6, 4, 22, 4));
            return Task.CompletedTask;
        }
    }

    private class EmptyTopDownService : FakeTopDownService
    {
        public override Task SampleAsync(CancellationToken cancellationToken)
        {
            SampleCalls++;
            return Task.CompletedTask;
        }
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
        // 同場重用必須標註，且時間窗反映實際量測而非重用當下。
        Assert.Contains(loadedResult.Limitations, limitation => limitation.Contains("重用同場", StringComparison.Ordinal));
        Assert.Equal(streamResult.StartedUtc, loadedResult.StartedUtc);
        Assert.Equal(streamResult.EndedUtc, loadedResult.EndedUtc);
    }

    [Fact]
    public async Task 跨場舊快照必須重跑不得重蓋時間戳()
    {
        var service = new FakeMemoryService();
        var first = await new StreamBandwidthAdapter(service).RunAsync(CreateContext(), CancellationToken.None);
        Guid secondSession = Guid.NewGuid();
        var secondContext = new DeepBenchRunContext(secondSession, DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

        var second = await new StreamBandwidthAdapter(service).RunAsync(secondContext, CancellationToken.None);

        Assert.Equal(2, service.Runs);
        Assert.Equal(secondSession, second.SessionId);
        Assert.True(second.StartedUtc >= first.EndedUtc, "第二場時間窗必須來自重跑，不得沿用第一場快照。");
        Assert.DoesNotContain(second.Limitations, limitation => limitation.Contains("重用同場", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 預先存在的舊快照在無戳記時也會重跑()
    {
        var service = new FakeMemoryService();
        service.Rows.Add(new MemBandwidthRow("讀取", 2, 42, 1, "stale"));
        service.LoadedRows.Add(new LoadedLatencyRow(2, 42, 88, 1));
        var context = CreateContext();

        var streamResult = await new StreamBandwidthAdapter(service).RunAsync(context, CancellationToken.None);
        var loadedResult = await new LoadedLatencyAdapter(service).RunAsync(context, CancellationToken.None);

        // 模擬 VM 共用服務在 session 開始前就有的資料：adapter 必須重跑，不重蓋時間戳。
        Assert.Equal(1, service.Runs);
        Assert.Equal(streamResult.StartedUtc, loadedResult.StartedUtc);
        Assert.Contains(loadedResult.Limitations, limitation => limitation.Contains("重用同場", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 取消的執行不留同場戳記()
    {
        var service = new FakeMemoryService();
        var context = CreateContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelled = await new StreamBandwidthAdapter(service).RunAsync(context, cts.Token);
        var retry = await new StreamBandwidthAdapter(service).RunAsync(context, CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Cancelled, cancelled.FailureKind);
        Assert.Equal(2, service.Runs);
        Assert.Equal(DeepBenchFailureKind.None, retry.FailureKind);
    }

    [Fact]
    public async Task 快取舊服務回報錯誤時不產生量測()
    {
        var service = new FakeCacheService();
        service.Rows.Add(new CacheLatencyRow("4 KB", 1.25, 0.1));
        typeof(CacheBenchService).GetField("_phase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, "錯誤");

        var result = await new CacheLatencyAdapter(service).RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.Empty(result.Metrics);
    }

    [Theory]
    [InlineData("4 KB", 4096)]
    [InlineData("64 MB", 67108864)]
    [InlineData("1.5 MB", 1572864)]
    [InlineData("garbage", 0)]
    [InlineData("", 0)]
    public void 工作集大小解析不靠魔術字串列舉(string text, long expected)
    {
        Assert.Equal(expected, CacheLatencyAdapter.ParseBytes(text));
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

    [Fact]
    public async Task TopDownAdapter保留逐核心四桶與Intel限制()
    {
        var service = new FakeTopDownService { Support = true };
        var result = await new TopDownAdapter(service).RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(1, service.SampleCalls);
        Assert.Equal("cpu.top-down", result.TestId);
        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(4, result.Metrics.Count);
        var retiring = result.Metrics.Single(metric => metric.Id == "cpu.topdown.retiring");
        Assert.Equal("%", retiring.Unit);
        Assert.Equal([60, 68], retiring.Samples);
        Assert.Equal("0", retiring.Points[0].Axes["physicalCore"]);
        Assert.Equal("1", retiring.Points[1].Axes["physicalCore"]);
        Assert.Contains("aggregate=64", retiring.Configuration, StringComparison.Ordinal);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("INT_MISC.RECOVERY_CYCLES", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TopDownAdapter不支援與空樣本不偽造數值()
    {
        var unsupported = new FakeTopDownService();
        var unsupportedResult = await new TopDownAdapter(unsupported).RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.Unsupported, unsupportedResult.FailureKind);
        Assert.Equal(0, unsupported.SampleCalls);
        Assert.Empty(unsupportedResult.Metrics);

        var empty = new EmptyTopDownService { Support = true };
        var emptyResult = await new TopDownAdapter(empty).RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.NotRun, emptyResult.FailureKind);
        Assert.Equal(1, empty.SampleCalls);
        Assert.Empty(emptyResult.Metrics);
        Assert.Contains("未產生", emptyResult.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TopDownAdapter取消不產生有效量測()
    {
        var service = new FakeTopDownService { Support = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new TopDownAdapter(service).RunAsync(CreateContext(), cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private static void SetField(object target, string field, object value) =>
        typeof(CoreLatencyService).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
