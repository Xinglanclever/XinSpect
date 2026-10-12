using Xunit;

namespace XinSpect.Tests;

/// <summary>QS-004 單位換算層（UnitConverter）的守門：未知單位不猜、仿射溫度分開。</summary>
public class UnitConverterTests
{
    [Theory]
    [InlineData(2048, "KiB", "MiB", 2.0)]
    [InlineData(1, "GiB", "MiB", 1024)]
    [InlineData(3, "MHz", "GHz", 0.003)]
    [InlineData(5000, "ms", "s", 5.0)]
    [InlineData(2, "GB", "MB", 2000)]
    public void 換算_同基準單位_倍率正確(double input, string from, string to, double expected)
    {
        Assert.True(QueryInsightFactsService.UnitConverter.TryConvert(input, from, to, out var result));
        Assert.Equal(expected, result, precision: 6);
    }

    [Theory]
    [InlineData("MiB", "GHz")]
    [InlineData("B", "°C")]
    public void 換算_不同基準_拒絕(string from, string to)
    {
        Assert.False(QueryInsightFactsService.UnitConverter.TryConvert(1, from, to, out _));
    }

    [Theory]
    [InlineData("knob")]
    [InlineData("furlong")]
    public void 換算_未知單位_拒絕不猜(string unit)
    {
        Assert.False(QueryInsightFactsService.UnitConverter.IsKnown(unit));
        Assert.False(QueryInsightFactsService.UnitConverter.TryConvert(1, unit, "B", out _));
        Assert.False(QueryInsightFactsService.UnitConverter.TryConvert(1, "B", unit, out _));
    }

    [Fact]
    public void 溫度_攝氏華氏往返_誤差可忽略()
    {
        Assert.True(QueryInsightFactsService.UnitConverter.TryConvertTemperature(100, 'C', 'F', out var f));
        Assert.Equal(212, f, precision: 6);
        Assert.True(QueryInsightFactsService.UnitConverter.TryConvertTemperature(f, 'F', 'C', out var c));
        Assert.Equal(100, c, precision: 6);
    }

    [Fact]
    public void 溫度_不支援K氏或亂輸入_拒絕()
    {
        Assert.False(QueryInsightFactsService.UnitConverter.TryConvertTemperature(0, 'C', 'K', out _));
        Assert.False(QueryInsightFactsService.UnitConverter.TryConvertTemperature(0, 'x', 'C', out _));
    }

    [Fact]
    public void 正規化_回基準單位與倍率值()
    {
        var n = QueryInsightFactsService.UnitConverter.Normalize(2, "MiB");
        Assert.NotNull(n);
        Assert.Equal(2 * 1024 * 1024, n!.Value.Value);
        Assert.Equal("B", n.Value.Unit);
        Assert.Null(QueryInsightFactsService.UnitConverter.Normalize(2, "knob"));
    }
}
