using Xunit;

namespace XinSpect.Tests;

/// <summary>Vol2 FS-007（回收桶審計）與 FS-010（任務歷史痕跡）事實產出的測試：注入探針，不碰真實檔案系統／事件記錄。</summary>
public sealed class ForensicReadyFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    // ── FS-007 回收桶 ────────────────────────────────────────────────────

    [Fact]
    public void 回收桶_兩顆磁碟_合計條目與大小()
    {
        var facts = RecycleBinFactsService.Collect(At,
            () => ["C:", "D:"],
            root => root == "C:"
                ? new RecycleBinFactsService.BinStat("C:", 3, 2 * 1024 * 1024, null)
                : new RecycleBinFactsService.BinStat("D:", 0, 0, null));

        Assert.Equal("3", facts.Single(f => f.Key == "fs.bin.count").Value);
        Assert.Equal("2.0", facts.Single(f => f.Key == "fs.bin.size_mb").Value);
        string roots = facts.Single(f => f.Key == "fs.bin.roots").Value;
        Assert.Contains("2/2 個磁碟可讀", roots);
        Assert.Contains("C:3 條", roots);
    }

    [Fact]
    public void 回收桶_一顆讀不到_不當成0條且列出原因()
    {
        var facts = RecycleBinFactsService.Collect(At,
            () => ["C:", "D:"],
            root => root == "C:"
                ? new RecycleBinFactsService.BinStat("C:", 5, 0, null)
                : throw new UnauthorizedAccessException("拒絕存取"));

        Assert.Equal("5", facts.Single(f => f.Key == "fs.bin.count").Value);
        string roots = facts.Single(f => f.Key == "fs.bin.roots").Value;
        Assert.Contains("1/2", roots);
        Assert.Contains("拒絕存取", roots);
    }

    [Fact]
    public void 回收桶_列舉失敗_三把鍵都標ReadError()
    {
        var facts = RecycleBinFactsService.Collect(At, () => throw new InvalidOperationException("no drives"));
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    // ── FS-010 任務歷史痕跡 ──────────────────────────────────────────────

    [Fact]
    public void 任務痕跡_登錄與刪除分開計數()
    {
        var now = new DateTimeOffset(2026, 10, 11, 3, 0, 0, TimeSpan.Zero);
        var facts = TaskHistoryFactsService.Collect(At, () =>
        [
            new TaskHistoryFactsService.TaskTrace(now.AddHours(-1), 106, "\\MyTask1"),
            new TaskHistoryFactsService.TaskTrace(now.AddHours(-2), 106, "\\MyTask2"),
            new TaskHistoryFactsService.TaskTrace(now.AddHours(-3), 102, "\\OldTask"),
        ]);

        Assert.Equal("3", facts.Single(f => f.Key == "fs.taskhist.count").Value);
        Assert.Contains("登錄 2 筆", facts.Single(f => f.Key == "fs.taskhist.summary").Value);
        Assert.Contains("刪除 1 筆", facts.Single(f => f.Key == "fs.taskhist.summary").Value);
        Assert.Contains("MyTask1", facts.Single(f => f.Key == "fs.taskhist.last").Value);
    }

    [Fact]
    public void 任務痕跡_記錄未啟用標NotSupported_不寫成0筆()
    {
        var facts = TaskHistoryFactsService.Collect(At, () => null);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
        Assert.Contains("不等於", facts[0].UnavailableReason!);
    }

    [Fact]
    public void 任務痕跡_查詢丟例外標ReadError()
    {
        var facts = TaskHistoryFactsService.Collect(At, () => throw new InvalidOperationException("query failed"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    [Fact]
    public void 任務痕跡_記錄開著但沒事件_說清楚是沒發生()
    {
        var facts = TaskHistoryFactsService.Collect(At, () => []);
        Assert.Equal("0", facts.Single(f => f.Key == "fs.taskhist.count").Value);
        Assert.Contains("記錄是開著的", facts.Single(f => f.Key == "fs.taskhist.last").Value);
    }

    // ── NG-006 覆蓋統計 ─────────────────────────────────────────────────

    [Fact]
    public void 覆蓋統計_覆蓋豁免未覆蓋三段計數()
    {
        var facts = CoverageFactsService.Collect(At,
            ["sa.exposure.rdp", "asset.uuid", "no.rule.here"],
            [("R1", (IReadOnlyList<string>)["sa.exposure.rdp"])]);

        Assert.Equal("3", facts.Single(f => f.Key == "ng.coverage.keys").Value);
        Assert.Equal("1", facts.Single(f => f.Key == "ng.coverage.covered").Value);
        Assert.Equal("1", facts.Single(f => f.Key == "ng.coverage.exempt").Value);   // asset. 是明文豁免前綴
        Assert.Equal("1", facts.Single(f => f.Key == "ng.coverage.uncovered").Value);
        Assert.Contains("no.rule.here", facts.Single(f => f.Key == "ng.coverage.headline").Value);
    }

    [Fact]
    public void 覆蓋統計_未覆蓋不是錯誤_措辭不得說通過()
    {
        var facts = CoverageFactsService.Collect(At, ["zz.uncovered"], []);
        string headline = facts.Single(f => f.Key == "ng.coverage.headline").Value;
        Assert.DoesNotContain("通過", headline);
        Assert.Contains("尚未被任何規則碰到", headline);
    }
}
