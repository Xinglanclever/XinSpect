using Xunit;

namespace XinSpect.Tests;

public class DeepBenchViewModelTests
{
    [Fact]
    public void 深測中心註冊在效能頁後面且標示為進階頁()
    {
        int index = PageRegistry.Pages.ToList().FindIndex(page => page.Key == "deepbench");

        Assert.True(index > 0, "找不到 deepbench 頁");
        Assert.Equal("bench", PageRegistry.Pages[index - 1].Key);
        Assert.Equal("深測中心", PageRegistry.Pages[index].Title);
        Assert.Equal("監控", PageRegistry.Pages[index].Group);
        Assert.True(PageRegistry.Pages[index].Advanced);
        Assert.Contains("深測", PageRegistry.Pages[index].Keywords);
        Assert.Contains("benchmark", PageRegistry.Pages[index].Keywords);
    }

    [Fact]
    public void 主檢視模型共用既有深測服務實例()
    {
        var vm = new MainViewModel();

        Assert.Same(vm.Cache, vm.DeepBench.Cache);
        Assert.Same(vm.MemBandwidth, vm.DeepBench.MemBandwidth);
        Assert.Same(vm.CoreLatency, vm.DeepBench.CoreLatency);
        Assert.Same(vm.TopDown, vm.DeepBench.TopDown);
    }

    [Fact]
    public void 目錄顯示三十八項且延後項目不得被隱藏()
    {
        using var store = new TempHistoryStore();
        var vm = new DeepBenchViewModel(new CacheBenchService(), new MemBandwidthService(), new CoreLatencyService(), store.Store);

        Assert.Equal(38, vm.CatalogRows.Count);
        Assert.Equal(38, vm.CatalogRows.Select(row => row.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 38), vm.CatalogRows.Select(row => row.MatrixNumber));
        Assert.Contains(vm.CatalogRows, row => row.Status == DeepBenchTestStatus.Deferred && row.Id == "gpu.vram-bandwidth");
    }

    [Fact]
    public void 快速與完整檔都只選十五個已接入測項()
    {
        using var store = new TempHistoryStore();
        var vm = new DeepBenchViewModel(new CacheBenchService(), new MemBandwidthService(), new CoreLatencyService(), store.Store);
        string[] expected =
        [
            "cpu.aes-sha", "cpu.load-use-ilp-branch", "cpu.rdrand-rdseed", "topology.core-latency", "memory.cache-latency",
            "memory.stream-bandwidth", "memory.loaded-latency", "gpu.fp32-fp64-integer", "cpu.top-down",
            "storage.qd-ladder", "storage.mixed-rw", "storage.write-integrity",
            "storage.flush-durability",
            "storage.slc-sustained-write",
            "ux.network-stack-latency"
        ];

        vm.SelectedProfile = DeepBenchRunProfile.Quick;
        Assert.Equal(expected, vm.SelectedIds);

        vm.SelectedProfile = DeepBenchRunProfile.Full;
        Assert.Equal(expected, vm.SelectedIds);
    }

    [Fact]
    public async Task 沒有儲存根時啟動會指向五個儲存測項而不是開始高負載()
    {
        using var store = new TempHistoryStore();
        var vm = CreateViewModel(store.Store, new FixedDiskFileSystem(256L * 1024 * 1024 * 1024));

        await vm.StartAsync(CancellationToken.None);

        Assert.False(vm.IsRunning);
        Assert.Null(vm.LastState);
        string combined = string.Join('\n', vm.Errors);
        Assert.Contains("選擇儲存根", combined, StringComparison.Ordinal);
        Assert.Contains("storage.qd-ladder", combined, StringComparison.Ordinal);
        Assert.Contains("storage.mixed-rw", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 預算後空間少於八GB時不得啟動磁碟測試()
    {
        using var store = new TempHistoryStore();
        var vm = CreateViewModel(store.Store, new FixedDiskFileSystem(8L * 1024 * 1024 * 1024 + 64L * 1024 * 1024));
        vm.StorageRoot = "C:\\DeepBench";

        await vm.StartAsync(CancellationToken.None);

        Assert.False(vm.IsRunning);
        string combined = string.Join('\n', vm.Errors);
        Assert.Contains("8 GB", combined, StringComparison.Ordinal);
        Assert.Contains("storage.qd-ladder", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消會保存已完成項目並標示其餘未跑()
    {
        using var store = new TempHistoryStore();
        var fileSystem = new FixedDiskFileSystem(256L * 1024 * 1024 * 1024);
        var vm = new DeepBenchViewModel(
            new CacheBenchService(), new MemBandwidthService(), new CoreLatencyService(), store.Store,
            fileSystem, new DeepBenchOrchestrator(FakeTests()));
        vm.StorageRoot = "C:\\DeepBench";
        using var cts = new CancellationTokenSource();

        Task run = vm.StartAsync(cts.Token);
        cts.Cancel();
        await run;

        Assert.False(vm.IsRunning);
        Assert.Equal(DeepBenchRunState.Cancelled, vm.LastState);
        Assert.Equal(5, vm.ResultCards.Count);
        Assert.Equal("cpu.aes-sha", vm.ResultCards[0].TestId);
        Assert.Equal("cpu.load-use-ilp-branch", vm.ResultCards[1].TestId);
        Assert.Equal("cpu.rdrand-rdseed", vm.ResultCards[2].TestId);
        Assert.Equal("topology.core-latency", vm.ResultCards[3].TestId);
        Assert.Equal("memory.cache-latency", vm.ResultCards[4].TestId);
        Assert.Equal(DeepBenchFailureKind.None, vm.ResultCards[0].FailureKind);
        Assert.Equal(DeepBenchFailureKind.Cancelled, vm.ResultCards[4].FailureKind);
        Assert.Contains(vm.ResultCards.Select(card => card.StateText), text => text.Contains("取消", StringComparison.Ordinal));
    }

    [Fact]
    public void 結果卡保留原始樣本數可信度限制與錯誤()
    {
        Guid session = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        var metric = new DeepBenchMetric(
            "cpu.aes.cbc.throughput", "AES throughput", "MiB/s", true, "2 rounds",
            [800, 900, 1000], []);
        var result = new DeepBenchTestResult(
            "cpu.aes-sha", session, DeepBenchRunProfile.Quick, now, now.AddSeconds(1),
            "test", [metric], ["condition"], ["limitation"], DeepBenchFailureKind.None, null);

        var card = DeepBenchResultCard.From(result, "CPU");

        Assert.Equal("cpu.aes-sha", card.TestId);
        Assert.Equal(3, card.Metrics.Single().SampleCount);
        Assert.NotEqual(DeepBenchConfidence.Insufficient, card.Metrics.Single().Confidence);
        Assert.Contains(card.Limitations, limitation => limitation.Contains("limitation", StringComparison.Ordinal));
        Assert.False(card.HasErrorText);
        Assert.Contains("3", card.Metrics.Single().SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void 歷史列只呈現本機紀錄且保留場次狀態()
    {
        Guid session = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        var record = new DeepBenchRunRecord(
            session, DeepBenchRunProfile.Quick, now, now.AddSeconds(2), DeepBenchRunState.Completed, [], []);

        var row = DeepBenchHistoryRow.From(record);

        Assert.Equal(session, row.SessionId);
        Assert.Equal("完成", row.State);
        Assert.Contains("已完成", row.CompletionText, StringComparison.Ordinal);
    }

    private static DeepBenchViewModel CreateViewModel(DeepBenchRunStore store, FixedDiskFileSystem fileSystem) =>
        new(new CacheBenchService(), new MemBandwidthService(), new CoreLatencyService(), store, fileSystem);

    private static IReadOnlyList<IDeepBenchTest> FakeTests()
    {
        return
        [
            new FakeDeepBenchTest("cpu.aes-sha", () => SuccessfulResult("cpu.aes-sha")),
            new FakeDeepBenchTest("cpu.load-use-ilp-branch", () => SuccessfulResult("cpu.load-use-ilp-branch")),
            new FakeDeepBenchTest("cpu.rdrand-rdseed", () => SuccessfulResult("cpu.rdrand-rdseed")),
            new FakeDeepBenchTest("topology.core-latency", () => SuccessfulResult("topology.core-latency")),
            new FakeDeepBenchTest("memory.cache-latency", async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null!;
            }),
            new FakeDeepBenchTest("memory.stream-bandwidth", () => SuccessfulResult("memory.stream-bandwidth")),
            new FakeDeepBenchTest("memory.loaded-latency", () => SuccessfulResult("memory.loaded-latency")),
            new FakeDeepBenchTest("gpu.fp32-fp64-integer", () => SuccessfulResult("gpu.fp32-fp64-integer")),
            new FakeDeepBenchTest("cpu.top-down", () => SuccessfulResult("cpu.top-down")),
            new FakeDeepBenchTest("storage.qd-ladder", () => SuccessfulResult("storage.qd-ladder")),
            new FakeDeepBenchTest("storage.mixed-rw", () => SuccessfulResult("storage.mixed-rw")),
            new FakeDeepBenchTest("storage.write-integrity", () => SuccessfulResult("storage.write-integrity")),
            new FakeDeepBenchTest("storage.flush-durability", () => SuccessfulResult("storage.flush-durability")),
            new FakeDeepBenchTest("storage.slc-sustained-write", () => SuccessfulResult("storage.slc-sustained-write")),
            new FakeDeepBenchTest("ux.network-stack-latency", () => SuccessfulResult("ux.network-stack-latency")),
        ];
    }

    private static DeepBenchTestResult SuccessfulResult(string id)
    {
        DateTime now = DateTime.UtcNow;
        return new DeepBenchTestResult(
            id, Guid.NewGuid(), DeepBenchRunProfile.Quick, now, now, "fake",
            [new DeepBenchMetric(id + ".metric", "metric", "x", true, "fake", [1, 2, 3], [])],
            ["fake condition"], ["fake limitation"], DeepBenchFailureKind.None, null);
    }

    private sealed class FakeDeepBenchTest(
        string id,
        Func<DeepBenchTestResult> success) : IDeepBenchTest
    {
        private readonly Func<DeepBenchRunContext, CancellationToken, Task<DeepBenchTestResult>> _body =
            (_, _) => Task.FromResult(success());

        public FakeDeepBenchTest(string id, Func<DeepBenchRunContext, CancellationToken, Task<DeepBenchTestResult>> body)
            : this(id, () => null!)
        {
            _body = body;
        }

        public string Id => id;

        public Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken) =>
            _body(context, cancellationToken);
    }

    private sealed class FixedDiskFileSystem(long availableFreeSpace) : IDiskIoFileSystem
    {
        public long AvailableFreeSpace { get; set; } = availableFreeSpace;

        public long GetAvailableFreeSpace(string root) => AvailableFreeSpace;

        public bool Exists(string path) => false;

        public void Create(string path) { }

        public void Delete(string path) { }
    }

    private sealed class TempHistoryStore : IDisposable
    {
        public TempHistoryStore()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "XinSpectTests", Guid.NewGuid() + "-deepbench-history.json");
            Store = new DeepBenchRunStore(path);
        }

        public DeepBenchRunStore Store { get; }

        public void Dispose()
        {
            try { System.IO.File.Delete(Store.Path); } catch { /* temp cleanup */ }
        }
    }
}
