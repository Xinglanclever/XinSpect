using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>事實鍵的物理合理域檢查（QS-001）。</summary>
public class KeyRangeGuardTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 5, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, double? value) =>
        new(key, "測試", key, value?.ToString() ?? "", "", "測試", FactTrustLevel.Measured, false, At, value);

    [Fact]
    public void 域表本身要站得住_每條都有單位與依據且上下界不倒反()
    {
        Assert.NotEmpty(KeyRangeGuardService.Rules);
        foreach (var r in KeyRangeGuardService.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.KeyPrefix));
            Assert.True(r.Min < r.Max, $"{r.KeyPrefix}：上下界倒反了");
            Assert.False(string.IsNullOrWhiteSpace(r.Unit), $"{r.KeyPrefix}：沒寫單位");
            Assert.True(r.Note.Length >= 10, $"{r.KeyPrefix}：沒有寫物理依據");
        }
        var prefixes = KeyRangeGuardService.Rules.Select(r => r.KeyPrefix).ToList();
        Assert.Equal(prefixes.Count, prefixes.Distinct().Count());
    }

    [Fact]
    public void 域內與超域分得清楚()
    {
        Assert.Equal(RangeVerdict.InDomain, KeyRangeGuardService.Check(Fact("cpu.tjmax", 105), out var rule));
        Assert.NotNull(rule);
        Assert.Equal(RangeVerdict.OutOfDomain, KeyRangeGuardService.Check(Fact("cpu.tjmax", 999), out _));
        Assert.Equal(RangeVerdict.NoNumeric, KeyRangeGuardService.Check(Fact("cpu.tjmax", null), out _));
        Assert.Equal(RangeVerdict.NoRule, KeyRangeGuardService.Check(Fact("some.unknown.key", 1), out _));
    }

    [Fact]
    public void 有超域值時_彙總標Unknown並指名那一條()
    {
        var fact = KeyRangeGuardService.Collect(At,
        [
            Fact("boot.duration_ms", 12_000),
            Fact("cpu.tjmax", 999),
            Fact("nic.count", 4),
            Fact("沒有登記規則的鍵", 1),
            Fact("usb.devices", null),
        ]);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("超域 1 條", fact.Value);
        Assert.Contains("cpu.tjmax=999", fact.Value);
        Assert.Contains("無規則 1 條", fact.Value);
        Assert.Contains("非數值 1 條", fact.Value);
        Assert.Contains("不改寫成 0", fact.Value);
        Assert.Contains("cpu.tjmax", fact.UnavailableReason ?? "");
    }

    [Fact]
    public void 全部域內時_標Present且說沒有超域值()
    {
        var fact = KeyRangeGuardService.Collect(At, [Fact("cpu.tjmax", 100), Fact("mon.count", 2)]);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("域內 2 條", fact.Value);
        Assert.Contains("沒有超域值", fact.Value);
    }

    [Fact]
    public void 沒有事實可檢時_標不適用而不是通過()
    {
        var fact = KeyRangeGuardService.Collect(At, []);

        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Contains("不是通過", fact.UnavailableReason ?? "");
    }
}
