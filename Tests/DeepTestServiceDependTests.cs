using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-TS-0219 服務相依深度統計的契約：深度計算（DFS＋環路防護）、
/// 統計彙總、逐筆「查不到」不抹掉整份拓撲、六態傳播。合成資料，不碰真 SCM。
/// </summary>
public class DeepTestServiceDependTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private static ServiceDependencyDepthService.SvcRow Row(string name, params string[] deps) =>
        new(name, "核心驅動", deps, DependsOnReadable: true);

    [Fact]
    public void 深度_鏈狀相依逐層累加()
    {
        var cache = new Dictionary<string, int>();
        var byName = new Dictionary<string, ServiceDependencyDepthService.SvcRow>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = Row("a", "b"),
            ["b"] = Row("b", "c"),
            ["c"] = Row("c"),
        };
        Assert.Equal(2, ServiceDependencyDepthService.DepthOf("a", byName, cache, new HashSet<string>(), out bool cycle));
        Assert.False(cycle);
    }

    [Fact]
    public void 深度_環路防護_回負一不無限遞迴()
    {
        var cache = new Dictionary<string, int>();
        var byName = new Dictionary<string, ServiceDependencyDepthService.SvcRow>(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = Row("x", "y"),
            ["y"] = Row("y", "x"),
        };
        Assert.Equal(-1, ServiceDependencyDepthService.DepthOf("x", byName, cache, new HashSet<string>(), out bool cycle));
        Assert.True(cycle);
    }

    [Fact]
    public void 深度_相依清單查不到的視為終點但不冒充零依賴統計()
    {
        var cache = new Dictionary<string, int>();
        var byName = new Dictionary<string, ServiceDependencyDepthService.SvcRow>(StringComparer.OrdinalIgnoreCase)
        {
            ["driver"] = new ServiceDependencyDepthService.SvcRow("driver", "核心驅動", [], DependsOnReadable: false),
        };
        Assert.Equal(0, ServiceDependencyDepthService.DepthOf("driver", byName, cache, new HashSet<string>(), out _));
    }

    [Fact]
    public void 統計_最長鏈與被依賴最多與類型分布()
    {
        var rows = new List<ServiceDependencyDepthService.SvcRow>
        {
            Row("a", "b"),
            new("b", "使用者服務（自有程序）", ["c"], true),
            new("c", "使用者服務（自有程序）", [], true),
            new("d", "核心驅動", [], true),
            new ServiceDependencyDepthService.SvcRow("e", "核心驅動", [], DependsOnReadable: false),
        };
        var f = ServiceDependencyDepthService.BuildFact(At, rows);
        Assert.Equal(ServiceDependencyDepthService.Key, f.Key);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(2, f.NumericValue); // 最長鏈 a→b→c = 2 層
        Assert.Contains("最長相依鏈 2 層（a）", f.Value);
        Assert.Contains("零相依 2 個", f.Value);
        Assert.Contains("被依賴最多：b（1 個服務依賴它）", f.Value);
        Assert.Contains("1 個鍵的 Type 值讀不到", f.Value);
        Assert.Contains("核心驅動×3", f.Value);
        Assert.Contains("使用者服務（自有程序）×2", f.Value);
        Assert.Null(f.UnavailableReason);
    }

    [Fact]
    public void 收集_零服務_不以0冒充()
    {
        var facts = ServiceDependencyDepthService.Collect(At, () => []);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("不以 0 冒充", f.UnavailableReason);
    }

    [Fact]
    public void 收集_SCM失敗_標ReadError()
    {
        var facts = ServiceDependencyDepthService.Collect(At,
            () => throw new InvalidOperationException("SCM 無法開啟"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("SCM", f.UnavailableReason);
    }

    [Fact]
    public void 收集_注入樣本()
    {
        var facts = ServiceDependencyDepthService.Collect(At, () =>
        [
            Row("a", "b"),
            Row("b"),
        ]);
        Assert.Equal(FactAvailability.Present, Assert.Single(facts).Availability);
    }
}
