using Xunit;

namespace XinSpect.Tests;

public class MemoryEccWheaStressAdapterTests
{
    [Fact]
    public async Task 壓力完成且無WHEA時保留原始記憶體樣本()
    {
        var memory = new FakeMemoryService();
        memory.Rows.Add(new MemBandwidthRow("舊", 1, 999, 1, "舊"));
        var result = await new MemoryEccWheaStressAdapter(memory, new FakeWheaStore())
            .RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(1, memory.Runs);
        Assert.Equal("memory.ecc-whea-stress", result.TestId);
        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(5, result.Metrics.Count);
        Assert.Equal([42, 50], result.Metrics[0].Samples);
        Assert.Equal([88], result.Metrics[1].Samples);
        Assert.Equal([0], result.Metrics[2].Samples);
        Assert.Contains(result.Conditions, condition => condition.Contains("WHEA 總計 0", StringComparison.Ordinal));
        Assert.Contains(result.Limitations, limitation => limitation.Contains("不是 MemTest86", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 壓力窗內WHEA事件會標示Unstable並保留分類()
    {
        var memory = new FakeMemoryService();
        var store = new DuringRunWheaStore(level => level switch
        {
            1 => new WheaTimelineEvent(DateTime.Now, 3, 17, "修正的記憶體錯誤"),
            _ => new WheaTimelineEvent(DateTime.Now, 2, 99, "其他事件"),
        });

        var result = await new MemoryEccWheaStressAdapter(memory, store)
            .RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Equal([2], result.Metrics[2].Samples);
        Assert.Equal([1], result.Metrics[3].Samples);
        Assert.Equal([0], result.Metrics[4].Samples);
        Assert.Contains("記憶體修正 1", result.Error, StringComparison.Ordinal);
        Assert.Contains(result.Conditions, condition => condition.Contains("WHEA #17", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 記憶體工作負載失敗時不偽造整合結果()
    {
        var result = await new MemoryEccWheaStressAdapter(new EmptyMemoryService(), new FakeWheaStore())
            .RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.NotRun, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("未同時產生", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WHEA頻道不可用時整項失敗而不只回報頻寬()
    {
        var result = await new MemoryEccWheaStressAdapter(new FakeMemoryService(), new BrokenWheaStore())
            .RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.PlatformError, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("WHEA", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消會轉發且不輸出樣本()
    {
        var memory = new FakeMemoryService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new MemoryEccWheaStressAdapter(memory, new FakeWheaStore())
            .RunAsync(CreateContext(), cts.Token);

        Assert.Equal(1, memory.Runs);
        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class EmptyMemoryService : MemBandwidthService
    {
        public override Task RunAsync() => Task.CompletedTask;
    }

    private sealed class FakeMemoryService : MemBandwidthService
    {
        public int Runs;

        public override Task RunAsync()
        {
            Runs++;
            Rows.Clear();
            LoadedRows.Clear();
            Rows.Add(new MemBandwidthRow("讀取", 2, 42, 1, "raw"));
            Rows.Add(new MemBandwidthRow("三元運算", 4, 50, 1, "raw"));
            LoadedRows.Add(new LoadedLatencyRow(2, 42, 88, 1));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWheaStore(params WheaTimelineEvent[] events) : IWheaEventStore
    {
        public int Calls;

        public IReadOnlyList<WheaTimelineEvent> ReadSince(DateTime sinceLocal)
        {
            Calls++;
            return events.Where(item => item.Time >= sinceLocal).ToArray();
        }
    }

    private sealed class DuringRunWheaStore(Func<int, WheaTimelineEvent> factory) : IWheaEventStore
    {
        public IReadOnlyList<WheaTimelineEvent> ReadSince(DateTime sinceLocal) =>
        [
            factory(1) with { Time = sinceLocal.AddSeconds(2.5) },
            factory(2) with { Time = sinceLocal.AddSeconds(2.5) },
        ];
    }

    private sealed class BrokenWheaStore : IWheaEventStore
    {
        public IReadOnlyList<WheaTimelineEvent> ReadSince(DateTime sinceLocal) =>
            throw new InvalidOperationException("找不到 WHEA-Logger 頻道。");
    }
}
