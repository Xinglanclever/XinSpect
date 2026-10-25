using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-TS-0218 ENUM 樹讀取延遲的契約：百分位計算、零節點不冒充、截斷標註、
/// 六態傳播；走訪以注入樣本取代，不依賴真機登錄檔內容。
/// </summary>
public class DeepTestRegistryLatencyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 百分位_最近鄰取法()
    {
        long[] sorted = [10, 20, 30, 40, 50];
        Assert.Equal(10, RegistryEnumLatencyService.Percentile(sorted, 0.01));
        Assert.Equal(30, RegistryEnumLatencyService.Percentile(sorted, 0.5));
        Assert.Equal(50, RegistryEnumLatencyService.Percentile(sorted, 0.95));
        Assert.Equal(50, RegistryEnumLatencyService.Percentile(sorted, 1.0));
        Assert.Equal(0, RegistryEnumLatencyService.Percentile([], 0.5));
    }

    [Fact]
    public void 事實_走訪樣本_p50p95與節點數()
    {
        var sample = new RegistryEnumLatencyService.EnumLatencySample(120,
            Enumerable.Repeat(0L, 100).Select(_ => 200L).ToList(), Truncated: false);
        var f = RegistryEnumLatencyService.BuildFact(At, sample);
        Assert.Equal(RegistryEnumLatencyService.Key, f.Key);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Null(f.UnavailableReason);
        Assert.Contains("120 個節點", f.Value);
        Assert.Contains("p50=", f.Value);
        Assert.Contains("µs", f.Value);
        Assert.Contains("不扣除", f.Value); // 誠實註記：含本程式 API 成本
        Assert.NotNull(f.NumericValue);
    }

    [Fact]
    public void 事實_截斷樣本_明確標未走完()
    {
        var sample = new RegistryEnumLatencyService.EnumLatencySample(2000,
            [5000, 6000], Truncated: true);
        var f = RegistryEnumLatencyService.BuildFact(At, sample);
        Assert.Contains("未走完全樹", f.Value);
        Assert.Contains("截斷集", f.Value);
    }

    [Fact]
    public void 事實_零節點_不以0µs冒充()
    {
        var f = RegistryEnumLatencyService.BuildFact(At, new(0, [], Truncated: false));
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("不以 0 µs 冒充", f.UnavailableReason);
    }

    [Fact]
    public void 事實_有節點無計時_標ReadError()
    {
        var f = RegistryEnumLatencyService.BuildFact(At, new(50, [], Truncated: false));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("量測未執行", f.UnavailableReason);
    }

    [Fact]
    public void 收集_注入樣本與讀取失敗傳播()
    {
        var ok = RegistryEnumLatencyService.Collect(At,
            () => new RegistryEnumLatencyService.EnumLatencySample(10, [100, 200], false));
        Assert.Equal(FactAvailability.Present, Assert.Single(ok).Availability);

        var failed = RegistryEnumLatencyService.Collect(At,
            () => throw new InvalidOperationException("找不到 Enum 樹"));
        var f = Assert.Single(failed);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("Enum 樹", f.UnavailableReason);
    }
}
