using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 執行緒遷移的拓樸分層與彙整（純函式）。
///
/// <para>
/// 這一份守的核心是<b>分層必須由外而內判斷</b>：跨 NUMA 的兩顆核往往也跨末級快取，
/// 若先判 LLC 就會把跨 NUMA 的遷移歸成跨 LLC，代價最大的那一層就永遠是 0。
/// 另一條是「遷移率排序」——用絕對次數排只會把最忙的行程排在最前面，那是同義反覆。
/// </para>
/// </summary>
public class ThreadMigrationTests
{
    /// <summary>
    /// 一台假機器：2 個 NUMA 節點 × 2 片末級快取 × 每片 2 顆實體核心 × 每核 2 條 SMT。
    /// CPU 0–3 ＝ 節點 0 / LLC 0（核 0：0,1；核 1：2,3）
    /// CPU 4–7 ＝ 節點 0 / LLC 1（核 2：4,5；核 3：6,7）
    /// CPU 8–11＝ 節點 1 / LLC 2（核 4：8,9；核 5：10,11）
    /// </summary>
    private static CpuLayout Machine() => new(
        coreOf: [0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5],
        llcOf:  [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2],
        numaOf: [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1]);

    // ── 分層 ────────────────────────────────────────────────────

    [Fact]
    public void 同一顆邏輯處理器不算遷移()
        => Assert.Equal(MigrationHop.Same, Machine().Classify(3, 3));

    [Fact]
    public void SMT兄弟之間是最便宜的一層()
    {
        Assert.Equal(MigrationHop.SmtSibling, Machine().Classify(0, 1));
        Assert.Equal(MigrationHop.SmtSibling, Machine().Classify(11, 10));
    }

    [Fact]
    public void 同末級快取內換核()
    {
        // 核 0 → 核 1，同在 LLC 0
        Assert.Equal(MigrationHop.SameLlc, Machine().Classify(0, 2));
    }

    [Fact]
    public void 跨末級快取但同NUMA()
    {
        // LLC 0 → LLC 1，兩者都在節點 0
        Assert.Equal(MigrationHop.CrossLlc, Machine().Classify(1, 5));
    }

    [Fact]
    public void 跨NUMA要蓋過跨LLC()
    {
        // 這是最容易寫錯的地方：跨節點的兩顆核必然也跨 LLC，
        // 若先判 LLC，代價最大的那一層就永遠是 0。
        Assert.Equal(MigrationHop.CrossNuma, Machine().Classify(0, 8));
        Assert.Equal(MigrationHop.CrossNuma, Machine().Classify(9, 4));
    }

    [Fact]
    public void 超出範圍或讀不到的編號一律當成沒換核而不猜()
    {
        var m = Machine();
        Assert.Equal(MigrationHop.Same, m.Classify(-1, 3));
        Assert.Equal(MigrationHop.Same, m.Classify(3, 99));
        // 拓樸整片讀不到時也不該亂分層
        Assert.Equal(MigrationHop.Same, CpuLayout.Empty.Classify(0, 5));
        Assert.False(CpuLayout.Empty.IsKnown);
    }

    [Fact]
    public void 拓樸摘要數得出分片與節點()
    {
        var m = Machine();
        Assert.True(m.IsKnown);
        Assert.Equal(12, m.Count);
        Assert.Equal(3, m.LlcCount);
        Assert.Equal(2, m.NumaCount);
        // 讀不到 NUMA 資訊時回 1 而不是 0——除以 0 比讀不到更糟
        Assert.Equal(1, new CpuLayout([0, 0], [0, 0], [-1, -1]).NumaCount);
    }

    [Fact]
    public void 每一層都說得出丟掉了什麼()
    {
        foreach (var hop in new[] { MigrationHop.SmtSibling, MigrationHop.SameLlc,
                                    MigrationHop.CrossLlc, MigrationHop.CrossNuma })
        {
            Assert.False(string.IsNullOrWhiteSpace(CpuLayout.HopName(hop)));
            Assert.False(string.IsNullOrWhiteSpace(CpuLayout.HopCost(hop)));
        }
        Assert.Equal("", CpuLayout.HopCost(MigrationHop.Same));
    }

    // ── 彙整 ────────────────────────────────────────────────────

    private static Dictionary<(int From, int To), long> Pairs() => new()
    {
        [(0, 1)] = 500,     // SMT 兄弟
        [(0, 2)] = 300,     // 同 LLC
        [(1, 5)] = 150,     // 跨 LLC
        [(0, 8)] = 50,      // 跨 NUMA
        [(3, 3)] = 999,     // 沒換核：不該被算成遷移
    };

    [Fact]
    public void 分層加總不含沒換核的那一筆()
    {
        var m = Machine();
        Assert.Equal(1000, MigrationAggregator.TotalMigrations(Pairs(), m));

        var hops = MigrationAggregator.ByHop(Pairs(), m, seconds: 10);
        Assert.Equal(4, hops.Count);
        Assert.DoesNotContain(hops, h => h.Hop == MigrationHop.Same);
        Assert.Equal(1000, hops.Sum(h => h.Count));
        Assert.Equal(1.0, hops.Sum(h => h.Share), 6);
    }

    [Fact]
    public void 分層由便宜到昂貴排序且算得出每秒速率()
    {
        var hops = MigrationAggregator.ByHop(Pairs(), Machine(), seconds: 10);
        Assert.Equal([MigrationHop.SmtSibling, MigrationHop.SameLlc,
                      MigrationHop.CrossLlc, MigrationHop.CrossNuma],
                     hops.Select(h => h.Hop));
        Assert.Equal(50, hops[0].PerSecond, 6);          // 500 次 ÷ 10 秒
        Assert.Equal("50 %", hops[0].ShareText);
    }

    [Fact]
    public void 秒數為零時不除以零()
    {
        var hops = MigrationAggregator.ByHop(Pairs(), Machine(), seconds: 0);
        Assert.All(hops, h => Assert.True(double.IsFinite(h.PerSecond)));
    }

    [Fact]
    public void 逐核切入次數由多到少且佔比加總為一()
    {
        var rows = MigrationAggregator.ByCpu(new Dictionary<int, long> { [3] = 100, [0] = 700, [5] = 200 });
        Assert.Equal([0, 5, 3], rows.Select(r => r.Cpu));
        Assert.Equal(1.0, rows.Sum(r => r.Share), 6);
        Assert.Equal("70 %", rows[0].ShareText);
    }

    private static MigrationProcessRow Proc(string name, int pid, long sw, long mg, long cross = 0)
        => new() { Name = name, Pid = pid, Switches = sw, Migrations = mg, CrossLlc = cross };

    [Fact]
    public void 行程依遷移率排序而不是絕對次數()
    {
        // 「忙碌」切了十萬次、遷移一萬次（10%）；「彈跳」只切了一千次但遷移八百次（80%）。
        // 用絕對次數排會把「忙碌」排前面，那是同義反覆——最忙的當然遷移最多。
        var rows = MigrationAggregator.TopProcesses(
        [
            Proc("忙碌.exe", 100, 100_000, 10_000),
            Proc("彈跳.exe", 200, 1_000, 800),
        ]);
        Assert.Equal("彈跳.exe", rows[0].Name);
        Assert.Equal("80 %", rows[0].RatioText);
    }

    [Fact]
    public void 切入次數太少的行程不列入排名()
    {
        // 只切 3 次而其中 2 次換核＝67%，但那不代表什麼；不濾掉就會佔住第一名。
        var rows = MigrationAggregator.TopProcesses([Proc("雜訊.exe", 1, 3, 2), Proc("真的.exe", 2, 5_000, 500)]);
        Assert.Single(rows);
        Assert.Equal("真的.exe", rows[0].Name);
    }

    [Fact]
    public void 沒有切入次數時比率寫破折號而不是零或除以零()
        => Assert.Equal("—", Proc("空", 1, 0, 0).RatioText);

    // ── 判讀 ────────────────────────────────────────────────────

    [Fact]
    public void 完全沒收到事件時明說可能是權限不足()
    {
        string s = MigrationAggregator.Verdict(0, 0, 10, [], Machine());
        Assert.Contains("系統管理員", s);
        Assert.Contains("不代表", s);      // 不能被讀成「這台機器沒在切換執行緒」
    }

    [Fact]
    public void 判讀陳述數字但不下判決()
    {
        var m = Machine();
        var hops = MigrationAggregator.ByHop(Pairs(), m, 10);
        string s = MigrationAggregator.Verdict(20_000, 1_000, 10, hops, m);

        Assert.Contains("20,000", s);
        Assert.Contains("跨末級快取", s);
        Assert.Contains("跨 NUMA", s);
        // 刻意不下判決：遷移多寡取決於工作型態
        Assert.Contains("不是缺陷", s);
        Assert.DoesNotContain("有問題", s);
        Assert.DoesNotContain("建議", s);
    }

    [Fact]
    public void 單一NUMA節點的機器要明說不會有跨節點遷移()
    {
        var single = new CpuLayout([0, 0, 1, 1], [0, 0, 0, 0], [0, 0, 0, 0]);
        var hops = MigrationAggregator.ByHop(new Dictionary<(int, int), long> { [(0, 2)] = 10 }, single, 5);
        string s = MigrationAggregator.Verdict(100, 10, 5, hops, single);
        Assert.Contains("只有一個 NUMA 節點", s);
    }

    [Fact]
    public void 只有一片末級快取的機器要說清楚為什麼沒有跨LLC那一列()
    {
        // 這是實機（i9-7980XE，Windows 回報全核共用一片 L3）驗證出來的：
        // 不說明的話，「跨末級快取」那一列憑空消失會被讀成量測漏了。
        var single = new CpuLayout([0, 0, 1, 1], [0, 0, 0, 0], [0, 0, 0, 0]);
        Assert.Equal(1, single.LlcCount);
        var hops = MigrationAggregator.ByHop(new Dictionary<(int, int), long> { [(0, 2)] = 10 }, single, 5);
        Assert.DoesNotContain(hops, h => h.Hop == MigrationHop.CrossLlc);

        string s = MigrationAggregator.Verdict(100, 10, 5, hops, single);
        Assert.Contains("末級快取只有一片", s);
        Assert.Contains("最貴的一層", s);
    }
}
