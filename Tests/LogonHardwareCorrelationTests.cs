using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-SE-0061（<c>sec.logon.hw.correlate</c>）：裝置插入×登入事件時間窗交集。
/// 合成時間戳（probe 注入）；重點：窗口邊界、方向（登入後才算）、
/// 帳號面遮蔽（只輸出次數，無帳號名／SID／時間點原值）、讀不到三態。
/// </summary>
public class LogonHardwareCorrelationTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset T(double minutes) => At.AddMinutes(minutes);

    [Fact]
    public void 裝置事件落在登入後窗口內_算一次交集()
    {
        var s = LogonHardwareCorrelationFactsService.Correlate(
            [T(0), T(60)],
            [T(5), T(65)]);
        Assert.Equal(2, s.LogonEvents);
        Assert.Equal(2, s.DeviceArrivalEvents);
        Assert.Equal(2, s.CorrelatedArrivals);
    }

    [Fact]
    public void 窗口邊界_剛好十分鐘算在內_超過一分鐘不算()
    {
        var s = LogonHardwareCorrelationFactsService.Correlate([T(0)], [T(10)]);
        Assert.Equal(1, s.CorrelatedArrivals);
        var s2 = LogonHardwareCorrelationFactsService.Correlate([T(0)], [T(10).AddSeconds(1)]);
        Assert.Equal(0, s2.CorrelatedArrivals);
    }

    [Fact]
    public void 登入前的裝置事件不算交集_不追溯()
    {
        var s = LogonHardwareCorrelationFactsService.Correlate([T(10)], [T(0)]);
        Assert.Equal(0, s.CorrelatedArrivals);
    }

    [Fact]
    public void 自訂窗口_純函式可調()
    {
        var s = LogonHardwareCorrelationFactsService.Correlate([T(0)], [T(20)], TimeSpan.FromMinutes(30));
        Assert.Equal(1, s.CorrelatedArrivals);
    }

    [Fact]
    public void 交集存在時_事實Present且數值為交集次數_不洩帳號面()
    {
        var fact = LogonHardwareCorrelationFactsService.Collect(At,
            logonProbe: () => [T(0), T(60)],
            deviceProbe: () => [T(3), T(75), T(200)]);   // 只有 T(3) 落在 T(0) 後 10 分鐘內
        Assert.Equal("sec.logon.hw.correlate", fact.Key);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(1, fact.NumericValue);
        Assert.False(fact.Sensitive);
        Assert.Contains("2 次", fact.Value);          // 登入事件次數
        Assert.Contains("3 次", fact.Value);          // 裝置事件次數
        Assert.Contains("不輸出帳號名或 SID", fact.Value);
        // 遮蔽檢查：值裡不得出現任何 ISO 時間點原值（只允許「近 N 天」與次數）。
        Assert.DoesNotContain("12:0", fact.Value);
        Assert.DoesNotContain("2026-10-25T", fact.Value);
    }

    [Fact]
    public void 任一log讀不到_如實ReadError且不帶數值()
    {
        var missingLogon = LogonHardwareCorrelationFactsService.Collect(At,
            logonProbe: () => null, deviceProbe: () => [T(1)]);
        Assert.Equal(FactAvailability.ReadError, missingLogon.Availability);
        Assert.Null(missingLogon.NumericValue);
        Assert.Contains("Security log", missingLogon.UnavailableReason ?? "");

        var missingDevice = LogonHardwareCorrelationFactsService.Collect(At,
            logonProbe: () => [T(0)], deviceProbe: () => null);
        Assert.Equal(FactAvailability.ReadError, missingDevice.Availability);
        Assert.Null(missingDevice.NumericValue);
        Assert.Contains("Kernel-PnP", missingDevice.UnavailableReason ?? "");
    }

    [Fact]
    public void 零交集也是誠實的零_不是讀不到()
    {
        var fact = LogonHardwareCorrelationFactsService.Collect(At,
            logonProbe: () => [T(0)],
            deviceProbe: () => [T(500)]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(0, fact.NumericValue);   // 量到 0 個交集——與「讀不到」是不同的斷言
    }
}
