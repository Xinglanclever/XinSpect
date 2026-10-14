using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 檔案系統 metadata 微延遲（ST-010）的單元測試。
/// 這一項的風險不是算錯百分位（那是既有 <see cref="DiskLatencyStats"/> 的地盤，另有測試），
/// 而是<b>把「沒量」講成「很快」</b>——0 µs 與 NotApplicable 在這裡必須分得開，
/// 因為前者會被讀成「瞬間完成」。
/// </summary>
public class FsMetadataLatencyFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private const long TicksPerSecond = 10_000_000;   // 100 ns 一刻

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    /// <summary>把微秒換成計時器刻數（本檔的樣本都用這個造）。</summary>
    private static long Us(double us) => (long)Math.Round(us * TicksPerSecond / 1_000_000.0);

    private static FsLatencySample Sample(string op, params double[] us) =>
        new(op, us.Select(Us).ToList());

    // ── 未量測 ───────────────────────────────────────────────────────────

    [Fact]
    public void 未注入探針是不適用不是零微秒()
    {
        var facts = FsMetadataLatencyService.Collect(At);

        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.All(facts, f => Assert.Null(f.NumericValue));
        Assert.All(facts, f => Assert.Equal("", f.Value));
        Assert.All(facts, f => Assert.Contains("不等於 0 µs", f.UnavailableReason));
    }

    [Fact]
    public void 未量測的理由要说出為什麼沒量()
    {
        // 「沒量」必須附得上原因，否則讀者只看到一個空值無從判斷是壞了還是沒跑
        var reason = Fact(FsMetadataLatencyService.Collect(At), "ms.fslat.summary").UnavailableReason;
        Assert.Contains("同意閘門", reason);
        Assert.Contains("需要寫入", reason);
    }

    [Fact]
    public void 沒有樣本時也不以零冒充()
    {
        var f = Fact(FsMetadataLatencyService.Collect(At, [], TicksPerSecond), "ms.fslat.summary");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("不以 0 µs 冒充", f.UnavailableReason);
    }

    [Fact]
    public void 時基為零等於沒量()
    {
        // ticksPerSecond<=0 無法換算；換算不了就不該產出一個看起來像數字的東西
        var f = Fact(FsMetadataLatencyService.Collect(
            At, [Sample("create", 10, 20)], ticksPerSecond: 0), "ms.fslat.summary");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    // ── 有樣本 ───────────────────────────────────────────────────────────

    [Fact]
    public void 三種操作都有樣本時各報一列()
    {
        var facts = FsMetadataLatencyService.Collect(At,
            [Sample("create", 30, 40, 50), Sample("stat", 5, 6, 7), Sample("delete", 80, 90, 100)],
            TicksPerSecond);

        var detail = Fact(facts, "ms.fslat.detail");
        Assert.Equal(FactAvailability.Present, detail.Availability);
        Assert.Contains("create", detail.Value);
        Assert.Contains("stat", detail.Value);
        Assert.Contains("delete", detail.Value);
        Assert.Equal(9, detail.NumericValue);
    }

    [Fact]
    public void 摘要報的是最慢的那個操作的p99()
    {
        var facts = FsMetadataLatencyService.Collect(At,
            [Sample("create", 10, 20, 30), Sample("delete", 5000, 6000, 7000)], TicksPerSecond);

        var f = Fact(facts, "ms.fslat.summary");
        Assert.Contains("最高的 p99 在 delete", f.Value);
        Assert.True(f.NumericValue > 5000);
    }

    [Fact]
    public void 樣本不足時p99自己降級說明()
    {
        var facts = FsMetadataLatencyService.Collect(At, [Sample("create", 10, 20)], TicksPerSecond);

        Assert.Contains("p99 在此只是雜訊", Fact(facts, "ms.fslat.detail").Value);
    }

    [Fact]
    public void 樣本充足時不降級()
    {
        double[] many = Enumerable.Range(1, FsMetadataLatencyService.MinSamplesForP99 + 1)
            .Select(i => (double)i).ToArray();
        var facts = FsMetadataLatencyService.Collect(At, [Sample("stat", many)], TicksPerSecond);

        Assert.DoesNotContain("只是雜訊", Fact(facts, "ms.fslat.detail").Value);
    }

    // ── 界線文字 ─────────────────────────────────────────────────────────

    [Fact]
    public void 摘要說清楚量的是中繼資料不是資料()
    {
        var f = Fact(FsMetadataLatencyService.Collect(At, [Sample("create", 10)], TicksPerSecond),
            "ms.fslat.summary");
        Assert.Contains("量的是中繼資料不是資料", f.Value);
        Assert.Contains("不可互相推論", f.Value);
    }

    [Fact]
    public void 摘要承認是冷熱混合的樣本()
    {
        var f = Fact(FsMetadataLatencyService.Collect(At, [Sample("create", 10)], TicksPerSecond),
            "ms.fslat.summary");
        Assert.Contains("冷熱混合", f.Value);
        Assert.Contains("不是可以跨機器比大小的基準", f.Value);
    }

    [Fact]
    public void 摘要聲明計時成本沒有扣掉()
    {
        var f = Fact(FsMetadataLatencyService.Collect(At, [Sample("create", 10)], TicksPerSecond),
            "ms.fslat.summary");
        Assert.Contains("含計時成本且不扣", f.Value);
    }

    [Fact]
    public void 不報路徑()
    {
        var facts = FsMetadataLatencyService.Collect(At,
            [Sample("create", 10), Sample("stat", 10), Sample("delete", 10)], TicksPerSecond);
        Assert.Contains("不指定路徑", Fact(facts, "ms.fslat.detail").Value);
        Assert.DoesNotContain(":\\", Fact(facts, "ms.fslat.detail").Value);
    }

    [Fact]
    public void 鍵名就是目錄登記的那兩把()
    {
        var keys = FsMetadataLatencyService.Collect(At).Select(f => f.Key).ToList();
        Assert.Equal(new[] { "ms.fslat.detail", "ms.fslat.summary" },
            keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 單位換算在臨界點上不加錯()
    {
        Assert.EndsWith("µs", FsMetadataLatencyService.Us(999));
        Assert.EndsWith("ms", FsMetadataLatencyService.Us(1000));
    }

    [Fact]
    public void 操作順序固定不因執行順序而變()
    {
        // 報表要可重現：先注入 delete 再注入 create，逐操作列的順序仍應是 create→stat→delete
        var facts = FsMetadataLatencyService.Collect(At,
            [Sample("delete", 10), Sample("create", 10), Sample("stat", 10)], TicksPerSecond);

        string v = Fact(facts, "ms.fslat.detail").Value;
        Assert.True(v.IndexOf("create", StringComparison.Ordinal)
                  < v.IndexOf("stat", StringComparison.Ordinal));
        Assert.True(v.IndexOf("stat", StringComparison.Ordinal)
                  < v.IndexOf("delete", StringComparison.Ordinal));
    }
}
