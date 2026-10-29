using System;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 電池健康三件事（U10-BT-0002～BT-0004，第 316 輪）：設計容量與滿充容量分列、損耗公式成文、
/// 循環次數「沒揭露 ≠ 0」。這一組的重點全在**三態**：沒有電池（NotApplicable）、
/// 有電池但欄位沒回報（Unknown）、讀取失敗（ReadError）。
/// </summary>
public class BatteryHealthFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static BatteryHealthFactsService.Sample Full(long design = 50_000, long full = 45_000, long? cycles = 120)
        => new(1, design, full, cycles);

    private static HardwareFact One(BatteryHealthFactsService.Sample? sample, string key)
    {
        var facts = BatteryHealthFactsService.Collect(sample, At);
        return Assert.Single(facts, f => f.Key == key);
    }

    [Fact]
    public void 設計與滿充要分列成兩把鍵而不是壓成一句()
    {
        var facts = BatteryHealthFactsService.Collect(Full(), At);
        var design = Assert.Single(facts, f => f.Key == BatteryHealthFactsService.DesignKey);
        var full = Assert.Single(facts, f => f.Key == BatteryHealthFactsService.FullKey);

        Assert.Equal(50_000, design.NumericValue);
        Assert.Equal(45_000, full.NumericValue);
        Assert.Contains("50,000 mWh", design.Value);
        Assert.Contains("45,000 mWh", full.Value);
        Assert.Equal(4, facts.Count);
    }

    [Fact]
    public void 損耗率的值要把公式寫出來()
    {
        var wear = One(Full(50_000, 45_000), BatteryHealthFactsService.WearKey);
        Assert.Equal(10.0, wear.NumericValue);
        Assert.Contains("10.0%", wear.Value);
        Assert.Contains("(1 − 滿充", wear.Value);
        Assert.Contains("四捨五入", wear.Value);
    }

    [Fact]
    public void 損耗率四捨五入到小數一位()
    {
        // (1 − 44,444 ÷ 50,000) × 100 = 11.112% → 11.1%
        var wear = One(Full(50_000, 44_444), BatteryHealthFactsService.WearKey);
        Assert.Equal(11.1, wear.NumericValue);
        Assert.Contains("11.1%", wear.Value);
    }

    [Fact]
    public void 滿充高於設計時損耗記零而且要在值裡說明()
    {
        var wear = One(Full(50_000, 52_000), BatteryHealthFactsService.WearKey);
        Assert.Equal(FactAvailability.Present, wear.Availability);
        Assert.Equal(0.0, wear.NumericValue);
        Assert.Contains("高於設計", wear.Value);
        Assert.Contains("損耗記 0", wear.Value);
    }

    [Fact]
    public void 缺設計容量時損耗算不出來而且不得給零()
    {
        var facts = BatteryHealthFactsService.Collect(new BatteryHealthFactsService.Sample(1, null, 45_000, 10), At);
        var design = Assert.Single(facts, f => f.Key == BatteryHealthFactsService.DesignKey);
        var wear = Assert.Single(facts, f => f.Key == BatteryHealthFactsService.WearKey);

        Assert.Equal(FactAvailability.Unknown, design.Availability);
        Assert.Null(design.NumericValue);
        Assert.Contains("不是 0 mWh", design.UnavailableReason);

        Assert.Equal(FactAvailability.Unknown, wear.Availability);
        Assert.Contains("算不出損耗", wear.UnavailableReason);
        Assert.DoesNotContain("0%", wear.Value);
    }

    [Fact]
    public void 循環次數沒回報時要寫沒揭露而不是零次()
    {
        foreach (long? cycles in new long?[] { null })
        {
            var cycle = One(Full(cycles: cycles), BatteryHealthFactsService.CycleKey);
            Assert.Equal(FactAvailability.Unknown, cycle.Availability);
            Assert.Null(cycle.NumericValue);
            Assert.DoesNotContain("0 次", cycle.Value);
            Assert.Contains("不等於「0 次」", cycle.UnavailableReason);
        }
    }

    [Fact]
    public void 循環次數有回報時要寫成次數()
    {
        var cycle = One(Full(cycles: 120), BatteryHealthFactsService.CycleKey);
        Assert.Equal(120, cycle.NumericValue);
        Assert.Contains("120 次", cycle.Value);
    }

    [Fact]
    public void 沒有電池是NotApplicable而不是讀不到()
    {
        var facts = BatteryHealthFactsService.Collect(new BatteryHealthFactsService.Sample(0, null, null, null), At);
        Assert.Equal(4, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.NotApplicable, f.Availability);
            Assert.Contains("不適用", f.UnavailableReason);
            Assert.Contains("不是讀不到", f.UnavailableReason);
        });
    }

    [Fact]
    public void 讀取失敗是ReadError而且理由與沒有電池不同()
    {
        var facts = BatteryHealthFactsService.Collect(null, At);
        Assert.Equal(4, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Contains("不是「沒有電池」", f.UnavailableReason);
        });
    }

    [Fact]
    public void 六態契約_Present不帶理由_其餘必帶理由且不帶數字()
    {
        var samples = new[]
        {
            Full(),
            new BatteryHealthFactsService.Sample(1, null, null, null),
            new BatteryHealthFactsService.Sample(0, null, null, null),
            null,
        };

        foreach (var sample in samples)
        {
            foreach (var fact in BatteryHealthFactsService.Collect(sample, At))
            {
                if (fact.Availability == FactAvailability.Present)
                {
                    Assert.Null(fact.UnavailableReason);
                    continue;
                }
                Assert.False(string.IsNullOrWhiteSpace(fact.UnavailableReason));
                Assert.Null(fact.NumericValue);
                Assert.Equal("", fact.Value);
            }
        }
    }

    [Fact]
    public void 既有的電池健康摘要也不得把沒揭露的循環次數寫成零次()
    {
        // 第 316 輪順手修掉的老問題：ReadBattery 從來沒查過循環次數，卻固定傳 0，
        // 於是每一台有電池的機器都被寫成「循環 0」——那不是沒讀到，是編出來的。
        var fact = PlatformGovernanceFactsService.BatteryFact(At,
            () => new PlatformGovernanceFactsService.BatteryState(1, 50_000, 45_000, null, null));

        Assert.Contains("循環 未回報", fact.Value);
        Assert.DoesNotContain("循環 0", fact.Value);
    }

    [Fact]
    public void 既有的電池健康摘要也要把有回報的循環次數寫成次數()
    {
        var fact = PlatformGovernanceFactsService.BatteryFact(At,
            () => new PlatformGovernanceFactsService.BatteryState(1, 50_000, 45_000, 120, null));
        Assert.Contains("循環 120 次", fact.Value);
    }

    [Fact]
    public void 四把鍵都不重複而且都帶單位()
    {
        var facts = BatteryHealthFactsService.Collect(Full(), At);
        Assert.Equal(4, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Unit)));
    }
}
