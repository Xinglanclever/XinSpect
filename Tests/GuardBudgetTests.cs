using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-064（守門執行時間預算）的守門。
///
/// <para>這一組自己也必須跑得快——它守的是「守門不能太貴」，自己太貴就是自相矛盾。
/// </para>
/// </summary>
public class GuardBudgetTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 預算_標準預算是兩段式的()
    {
        var b = GuardBudget.Budget.Standard("測試守門");
        Assert.True(b.Soft < b.Hard, "提醒線必須低於硬線，否則永遠不會有預警");
    }

    [Fact]
    public void 預算_在提醒線內不說話()
    {
        var b = new GuardBudget.Budget("快守門", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        Assert.Null(b.Judge(TimeSpan.FromMilliseconds(50)));
        Assert.False(b.IsHardViolation(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void 預算_超過提醒線只提醒不判失敗()
    {
        var b = new GuardBudget.Budget("中速守門", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        string? verdict = b.Judge(TimeSpan.FromMilliseconds(1500));

        Assert.NotNull(verdict);
        Assert.Contains("正在變貴", verdict);
        Assert.False(b.IsHardViolation(TimeSpan.FromMilliseconds(1500)));
    }

    [Fact]
    public void 預算_超過硬線要說出為什麼太貴的守門等於沒有守門()
    {
        var b = new GuardBudget.Budget("慢守門", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50));
        string? verdict = b.Judge(TimeSpan.FromMilliseconds(500));

        Assert.NotNull(verdict);
        Assert.Contains("太貴的守門會被跳過，等於沒有守門", verdict);
        Assert.True(b.IsHardViolation(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void 預算_量測會回傳耗時與判定()
    {
        var b = new GuardBudget.Budget("量測守門", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        var (elapsed, verdict) = b.Measure(() => { /* 什麼都不做 */ });

        Assert.True(elapsed >= TimeSpan.Zero);
        Assert.Null(verdict);
    }

    [Fact]
    public void 耗時申報_沒有量到時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            GuardBudget.ToFact(At, []).Availability);
    }

    [Fact]
    public void 耗時申報_全在預算內時標Present並說出最慢的一條()
    {
        var fact = GuardBudget.ToFact(At,
        [
            ("快", TimeSpan.FromMilliseconds(10), false),
            ("較慢", TimeSpan.FromMilliseconds(400), false),
        ]);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("較慢", fact.Value);
        Assert.Null(fact.UnavailableReason);
    }

    [Fact]
    public void 耗時申報_有超支時標未確認並指名()
    {
        var fact = GuardBudget.ToFact(At,
        [
            ("正常", TimeSpan.FromMilliseconds(10), false),
            ("爆掉", TimeSpan.FromSeconds(30), true),
        ]);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("爆掉", fact.Value);
        Assert.Contains("會被跳過", fact.UnavailableReason);
        Assert.Equal(1.0, fact.NumericValue);
    }
}
