using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP26 開機計時的契約：開機耗時來自 Diagnostics-Performance 事件記錄的 Event 100
/// （BootTime，毫秒）——Windows 自己量的，本工具只解讀不評級；最近一次開機時間戳來自
/// Win32_OperatingSystem.LastBootUpTime。事件缺席（如記錄被清）＝NotSupported 不是錯誤。
/// </summary>
public class BootTimingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 開機計時_耗時與時間戳逐項釘值()
    {
        var facts = XinSpect.BootTimingFactsService.Collect(At,
            bootEventProbe: () => 38_540, lastBootProbe: () => At.AddHours(-2));

        var duration = Assert.Single(facts, f => f.Key == "boot.duration_ms");
        Assert.Equal(FactAvailability.Present, duration.Availability);
        Assert.Equal(38540.0, duration.NumericValue);
        Assert.Contains("38.5 秒", duration.Value);

        var last = Assert.Single(facts, f => f.Key == "boot.last_time");
        Assert.Contains("2026-10-02 22:00", last.Value);
    }

    [Fact]
    public void 開機計時_事件缺席是NotSupported_查詢失敗是ReadError()
    {
        var absent = XinSpect.BootTimingFactsService.Collect(At,
            bootEventProbe: () => null, lastBootProbe: () => null);
        var duration = Assert.Single(absent, f => f.Key == "boot.duration_ms");
        Assert.Equal(FactAvailability.NotSupported, duration.Availability);
        Assert.Contains("沒有開機事件", duration.UnavailableReason);

        var fail = XinSpect.BootTimingFactsService.Collect(At,
            bootEventProbe: () => throw new InvalidOperationException("log busy"), lastBootProbe: () => null);
        Assert.Equal(FactAvailability.ReadError,
            Assert.Single(fail, f => f.Key == "boot.duration_ms").Availability);
    }
}
