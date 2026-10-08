using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class DeepBenchIntegrationTests
{
    [Fact]
    public void 報告保留同場深測的原始證據與限制()
    {
        var vm = new MainViewModel
        {
            DeepBench = SampleViewModel(),
        };
        DeepBenchRunRecord record = SampleRecord();
        vm.DeepBench.CurrentRecord = record;
        vm.DeepBench.ResultCards.Add(DeepBenchResultCard.From(record.Results[0]));
        vm.DeepBench.Insights.Add(record.Insights[0]);
        vm.DeepBench.History.Add(DeepBenchHistoryRow.From(record));

        string report = ReportService.BuildMarkdownForTests(vm);

        Assert.Contains("Deep Bench 深測", report, StringComparison.Ordinal);
        Assert.Contains(record.SessionId.ToString(), report, StringComparison.Ordinal);
        Assert.Contains("Quick", report, StringComparison.Ordinal);
        Assert.Contains("完成", report, StringComparison.Ordinal);
        Assert.Contains("已完成 2 項", report, StringComparison.Ordinal);
        Assert.Contains("AES throughput", report, StringComparison.Ordinal);
        Assert.Contains("rounds=2", report, StringComparison.Ordinal);
        Assert.Contains("High", report, StringComparison.Ordinal);
        Assert.Contains("disk-error", report, StringComparison.Ordinal);
        Assert.Contains("fake limitation", report, StringComparison.Ordinal);
        Assert.Contains("同場證據", report, StringComparison.Ordinal);
        Assert.Contains("不上傳", report, StringComparison.Ordinal);
        Assert.DoesNotContain("加權總分", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 損壞深測歷史在報告中明示而不裝作健康()
    {
        string path = Path.Combine(Path.GetTempPath(), "XinSpectTests", Guid.NewGuid() + "-deepbench-history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{corrupt");
        var store = new DeepBenchRunStore(path);
        Assert.Empty(store.LoadRecent());
        var vm = new MainViewModel
        {
            DeepBench = new DeepBenchViewModel(
                new CacheBenchService(), new MemBandwidthService(), new CoreLatencyService(), store),
        };
        try
        {
            string report = ReportService.BuildMarkdownForTests(vm);
            Assert.Contains(store.LastLoadError!.Replace("|", "\\|"), report, StringComparison.Ordinal);
            Assert.Contains("不推算舊結果", report, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void Help說明暫存檔高負載取消與本機歷史()
    {
        string combined = string.Join('\n', new[]
        {
            HelpCatalog.Find("deepbench/深測中心")?.What,
            HelpCatalog.Find("deepbench/Run Session")?.Does,
            HelpCatalog.Find("deepbench/Run Session")?.Safety,
            HelpCatalog.Find("deepbench/本機歷史")?.Does,
            HelpCatalog.Find("deepbench/本機歷史")?.Safety,
        });

        Assert.Contains("XinSpect.deepbench.tmp", combined, StringComparison.Ordinal);
        Assert.Contains("刪除", combined, StringComparison.Ordinal);
        Assert.Contains("滿載", combined, StringComparison.Ordinal);
        Assert.Contains("取消", combined, StringComparison.Ordinal);
        Assert.Contains("混合核心放置", combined, StringComparison.Ordinal);
        Assert.Contains("CPUID 0x1A", combined, StringComparison.Ordinal);
        Assert.Contains("P-core / E-core", combined, StringComparison.Ordinal);
        Assert.Contains("五種放置", combined, StringComparison.Ordinal);
        Assert.Contains("NUMA／TLB／大分頁", combined, StringComparison.Ordinal);
        Assert.Contains("SeLockMemoryPrivilege", combined, StringComparison.Ordinal);
        Assert.Contains("DRAM 映射推論", combined, StringComparison.Ordinal);
        Assert.Contains("僅供推論", combined, StringComparison.Ordinal);
        Assert.Contains("GPU 光柵／紋理", combined, StringComparison.Ordinal);
        Assert.Contains("不外推成遊戲效能", combined, StringComparison.Ordinal);
        Assert.Contains("Unsupported", combined, StringComparison.Ordinal);
        Assert.Contains("deepbench-history.json", combined, StringComparison.Ordinal);
        Assert.Contains("不上傳", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Changelog_最新版為Latest且歷史Everest條目保留()
    {
        // 2026-10-03 系列更名：Everest → Olympus，版號 2.1.0 → 2.2.0（使用者指示）。
        // 歷史 2.1.0 條目原封不動；新內容折疊進 2.2.0 Olympus 條目。
        ChangeEntry entry = Assert.Single(ChangelogCatalog.Entries, item => item.Version == "2.1.0");
        Assert.Equal("2.29", ChangelogCatalog.Latest);
        string combined = string.Join('\n', entry.Items);

        Assert.Contains("Deep Bench 深測中心", combined, StringComparison.Ordinal);
        Assert.Contains("38", combined, StringComparison.Ordinal);
        Assert.Contains("三十八個已接入測項", combined, StringComparison.Ordinal);
        Assert.Contains("混合核心放置", combined, StringComparison.Ordinal);
        Assert.Contains("CPUID 0x1A", combined, StringComparison.Ordinal);
        Assert.Contains("P-core / E-core", combined, StringComparison.Ordinal);
        Assert.Contains("五種放置", combined, StringComparison.Ordinal);
        Assert.Contains("NUMA／TLB／大分頁", combined, StringComparison.Ordinal);
        Assert.Contains("SeLockMemoryPrivilege", combined, StringComparison.Ordinal);
        Assert.Contains("不宣稱量到 DTLB 規格", combined, StringComparison.Ordinal);
        Assert.Contains("DRAM 映射推論", combined, StringComparison.Ordinal);
        Assert.Contains("不宣稱確定 row／bank／rank 映射", combined, StringComparison.Ordinal);
        Assert.Contains("GPU 光柵／紋理", combined, StringComparison.Ordinal);
        Assert.Contains("不外推成遊戲效能", combined, StringComparison.Ordinal);
        Assert.Contains("GPU codec 吞吐", combined, StringComparison.Ordinal);
        Assert.Contains("I/O→GPU 管線", combined, StringComparison.Ordinal);
        Assert.Contains("多域 Gauntlet", combined, StringComparison.Ordinal);
        Assert.Contains("真實世界合成負載", combined, StringComparison.Ordinal);
        Assert.Contains("可信度引擎自我稽核", combined, StringComparison.Ordinal);
        Assert.Contains("不加權", combined, StringComparison.Ordinal);
        Assert.Contains("Unsupported", combined, StringComparison.Ordinal);
        Assert.Contains("SLC 持續寫入", combined, StringComparison.Ordinal);
        Assert.Contains("VRAM", combined, StringComparison.Ordinal);
        Assert.Contains("TCP loopback", combined, StringComparison.Ordinal);
        Assert.Contains("WASAPI", combined, StringComparison.Ordinal);
        Assert.Contains("Present", combined, StringComparison.Ordinal);
        Assert.Contains("電源狀態", combined, StringComparison.Ordinal);
        Assert.Contains("睿頻爬升恢復", combined, StringComparison.Ordinal);
        Assert.Contains("吞吐衰退", combined, StringComparison.Ordinal);
        Assert.Contains("RDRAND / RDSEED", combined, StringComparison.Ordinal);
        Assert.Contains("Deferred", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void 報告對混池指標逐點列出而不給跨配置平均()
    {
        var vm = new MainViewModel
        {
            DeepBench = SampleViewModel(),
        };
        Guid session = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        var ladder = new DeepBenchMetric(
            "memory.stream.bandwidth", "STREAM bandwidth", "GB/s", true, "managed arrays",
            [30, 210],
            [
                new DeepBenchMetricPoint(30, new Dictionary<string, string> { ["kernel"] = "讀取", ["threads"] = "1" }, [30]),
                new DeepBenchMetricPoint(210, new Dictionary<string, string> { ["kernel"] = "三元運算", ["threads"] = "8" }, [210]),
            ]);
        var result = new DeepBenchTestResult(
            "memory.stream-bandwidth", session, DeepBenchRunProfile.Quick, now, now.AddSeconds(1),
            "managed arrays", [ladder], [], ["fake limitation"], DeepBenchFailureKind.None, null);
        vm.DeepBench.CurrentRecord = new DeepBenchRunRecord(
            session, DeepBenchRunProfile.Quick, now, now.AddSeconds(2), DeepBenchRunState.Completed, [result], []);

        string report = ReportService.BuildMarkdownForTests(vm);

        Assert.Contains("STREAM bandwidth（kernel=讀取 threads=1）", report, StringComparison.Ordinal);
        Assert.Contains("STREAM bandwidth（kernel=三元運算 threads=8）", report, StringComparison.Ordinal);
        Assert.Contains("210 GB/s（1 samples）", report, StringComparison.Ordinal);
        Assert.DoesNotContain("平均 120", report, StringComparison.Ordinal);
    }

    [Fact]
    public void 報告對取消場次不宣稱已完成()
    {
        var vm = new MainViewModel
        {
            DeepBench = SampleViewModel(),
        };
        Guid session = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        var result = new DeepBenchTestResult(
            "cpu.aes-sha", session, DeepBenchRunProfile.Quick, now, now.AddSeconds(1), "rounds=2",
            [new DeepBenchMetric("cpu.aes.cbc.throughput", "AES throughput", "MiB/s", true, "rounds=2", [990, 1000, 1010], [])],
            ["25 °C"], ["fake limitation"], DeepBenchFailureKind.None, null);
        vm.DeepBench.CurrentRecord = new DeepBenchRunRecord(
            session, DeepBenchRunProfile.Quick, now, now.AddSeconds(2), DeepBenchRunState.Cancelled, [result], []);

        string report = ReportService.BuildMarkdownForTests(vm);

        Assert.Contains("取消前產生 1 項結果", report, StringComparison.Ordinal);
        Assert.DoesNotContain("已完成 1 項", report, StringComparison.Ordinal);
    }

    private static DeepBenchViewModel SampleViewModel()
    {
        string path = Path.Combine(Path.GetTempPath(), "XinSpectTests", Guid.NewGuid() + "-deepbench-history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new DeepBenchViewModel(
            new CacheBenchService(), new MemBandwidthService(), new CoreLatencyService(),
            new DeepBenchRunStore(path));
    }

    private static DeepBenchRunRecord SampleRecord()
    {
        Guid session = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        var result = new DeepBenchTestResult(
            "cpu.aes-sha", session, DeepBenchRunProfile.Quick, now, now.AddSeconds(1), "rounds=2",
            [new DeepBenchMetric("cpu.aes.cbc.throughput", "AES throughput", "MiB/s", true, "rounds=2", [990, 1000, 1010], [])],
            ["25 °C"], ["fake limitation"], DeepBenchFailureKind.None, null);
        var insight = new DeepBenchInsight("同場證據", "只引用本場測項。", ["cpu.aes-sha"]);
        var failed = new DeepBenchTestResult(
            "storage.mixed-rw", session, DeepBenchRunProfile.Quick, now.AddSeconds(1), now.AddSeconds(2),
            "not run", [], [], ["此項未產生可信量測。"],
            DeepBenchFailureKind.PlatformError, "disk-error");
        return new DeepBenchRunRecord(
            session, DeepBenchRunProfile.Quick, now, now.AddSeconds(2),
            DeepBenchRunState.CompletedWithFailures, [result, failed], [insight]);
    }
}
