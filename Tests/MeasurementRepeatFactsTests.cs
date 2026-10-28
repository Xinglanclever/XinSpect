using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 逐鍵重複性（U10 第三圈 MT-0001，第 312 輪）。
///
/// <para>
/// 這一組要釘住的是「重複量測不要變成另一種假精確」：一輪不算重複、缺值不補、
/// 不給穩定度分數、不把不同語意的鍵放在同一個閾值上比較。
/// </para>
/// </summary>
public sealed class MeasurementRepeatFactsTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-29T12:00:00+00:00");

    private static MeasurementRepeatFactsService.RepeatSample S(string key, double? value, string? name = null)
        => new(key, name ?? key, value);

    private static IReadOnlyList<MeasurementRepeatFactsService.RepeatSample> Round(params MeasurementRepeatFactsService.RepeatSample[] items)
        => items;

    [Fact]
    public void 只有一輪不算重複()
    {
        var facts = MeasurementRepeatFactsService.Collect([Round(S("cpu.util", 12))], At);
        var summary = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, summary.Availability);
        Assert.Contains("一輪是單次讀值", summary.UnavailableReason);
    }

    [Fact]
    public void 每輪都讀不到時要說沒有東西可比()
    {
        var facts = MeasurementRepeatFactsService.Collect([
            Round(S("cpu.util", null)),
            Round(S("cpu.util", null)),
        ], At);

        var summary = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, summary.Availability);
        Assert.Contains("沒有東西可比", summary.UnavailableReason);
    }

    [Fact]
    public void 固定值的跨度是零且漂移是零()
    {
        var facts = MeasurementRepeatFactsService.Collect([
            Round(S("mem.available", 1000)),
            Round(S("mem.available", 1000)),
            Round(S("mem.available", 1000)),
        ], At);

        var key = Assert.Single(facts, f => f.Key == MeasurementRepeatFactsService.FamilyPrefix + "mem.available");
        Assert.Contains("跨度 0", key.Value);
        Assert.Contains("首末漂移 0", key.Value);
        Assert.Equal(0, key.NumericValue);
        Assert.Equal(FactAvailability.Present, key.Availability);
    }

    [Fact]
    public void 跳動的值要寫出最小最大與漂移()
    {
        var facts = MeasurementRepeatFactsService.Collect([
            Round(S("cpu.util", 10)),
            Round(S("cpu.util", 30)),
            Round(S("cpu.util", 20)),
        ], At);

        var key = Assert.Single(facts, f => f.Key.EndsWith("cpu.util", StringComparison.Ordinal));
        Assert.Contains("最小 10", key.Value);
        Assert.Contains("最大 30", key.Value);
        Assert.Contains("跨度 20", key.Value);
        Assert.Contains("首末漂移 +10", key.Value);
        Assert.Equal(20, key.NumericValue);
    }

    [Fact]
    public void 缺值的輪次不補_標成Unknown並說幾輪沒讀到()
    {
        var facts = MeasurementRepeatFactsService.Collect([
            Round(S("cpu.util", 10)),
            Round(),
            Round(S("cpu.util", 14)),
        ], At);

        var key = Assert.Single(facts, f => f.Key.EndsWith("cpu.util", StringComparison.Ordinal));
        Assert.Equal(FactAvailability.Unknown, key.Availability);
        Assert.Contains("3 輪中 2 輪有值", key.Value);
        Assert.Contains("1 輪沒有讀到", key.UnavailableReason);
    }

    [Fact]
    public void 鍵會依輪次對齊_不是依位置()
    {
        // 第二輪少了 mem、多了別的鍵：對齊要按鍵名，不然會拿 A 的值去比 B。
        var facts = MeasurementRepeatFactsService.Collect([
            Round(S("cpu.util", 10), S("mem.available", 500)),
            Round(S("disk.busy", 7), S("cpu.util", 12)),
        ], At);

        var cpu = Assert.Single(facts, f => f.Key.EndsWith("cpu.util", StringComparison.Ordinal));
        Assert.Contains("最小 10", cpu.Value);
        Assert.Contains("最大 12", cpu.Value);

        var mem = Assert.Single(facts, f => f.Key.EndsWith("mem.available", StringComparison.Ordinal));
        Assert.Equal(FactAvailability.Unknown, mem.Availability);
    }

    [Fact]
    public void 鍵只留合法字元()
    {
        var facts = MeasurementRepeatFactsService.Collect([
            Round(S("GPU/Fan:RPM", 900)),
            Round(S("GPU/Fan:RPM", 950)),
        ], At);

        var key = Assert.Single(facts, f => f.Key.StartsWith(MeasurementRepeatFactsService.FamilyPrefix, StringComparison.Ordinal));
        Assert.Equal(MeasurementRepeatFactsService.FamilyPrefix + "gpufanrpm", key.Key);
    }

    [Fact]
    public void 總覽要寫出相對漂移最大的那把鍵()
    {
        var facts = MeasurementRepeatFactsService.Collect([
            Round(S("a", 100), S("b", 10)),
            Round(S("a", 101), S("b", 20)),
        ], At);

        var summary = Assert.Single(facts, f => f.Key == MeasurementRepeatFactsService.SummaryKey);
        Assert.Contains("2 輪", summary.Value);
        Assert.Contains("「b」", summary.Value);
        Assert.Equal(2, summary.NumericValue);
    }

    [Fact]
    public void 家族與靜態鍵都登記了()
    {
        Assert.True(FactKeyDynamicCatalog.Matches(MeasurementRepeatFactsService.FamilyPrefix + "cpu.util"));
        Assert.Contains(MeasurementRepeatFactsService.SummaryKey, FactKeyCatalog.Keys);
        // 彙總鍵不能被家族吃掉：它是靜態鍵，不屬於逐鍵家族。
        Assert.False(FactKeyDynamicCatalog.Matches(MeasurementRepeatFactsService.SummaryKey));
    }
}
