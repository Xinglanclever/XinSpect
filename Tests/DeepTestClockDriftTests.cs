using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-TS-0192 會話期時鐘漂移的契約：合成起訖樣本的漂移計算、短窗拒報（Unknown）、
/// 次序異常（ReadError）、Present 帶數值與 NTP 註記。
/// </summary>
public class DeepTestClockDriftTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private const double QpcFrequency = 10_000_000.0; // 0.1 µs 刻（常見值）

    [Fact]
    public void 漂移_牆鐘慢於單調鐘_負漂移帶數值()
    {
        var start = new ClockSessionDriftService.ClockSample(At, 0);
        var end = (At.AddMinutes(30).AddMilliseconds(-1200), 30 * 60 * (long)QpcFrequency);
        var f = ClockSessionDriftService.BuildFact(At, start, end, QpcFrequency);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(-1200.0, f.NumericValue!.Value, 3);
        Assert.Contains("慢了 1200", f.Value);
        Assert.Contains("NTP", f.Value); // 誠實註記：包含校時介入
        Assert.Null(f.UnavailableReason);
    }

    [Fact]
    public void 漂移_牆鐘快於單調鐘_正漂移()
    {
        var start = new ClockSessionDriftService.ClockSample(At, 0);
        var end = (At.AddMinutes(10).AddMilliseconds(300), 10 * 60 * (long)QpcFrequency);
        var f = ClockSessionDriftService.BuildFact(At, start, end, QpcFrequency);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(300.0, f.NumericValue!.Value, 3);
        Assert.Contains("快了 300", f.Value);
    }

    [Fact]
    public void 漂移_短窗拒報_不給被粒度噪音主導的數字()
    {
        var start = new ClockSessionDriftService.ClockSample(At, 0);
        var end = (At.AddSeconds(5), 5 * (long)QpcFrequency);
        var f = ClockSessionDriftService.BuildFact(At, start, end, QpcFrequency);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("60 秒", f.UnavailableReason);
    }

    [Fact]
    public void 漂移_次序異常_標ReadError()
    {
        var start = new ClockSessionDriftService.ClockSample(At, 1000);
        var end = (At.AddMinutes(5), 500);
        var f = ClockSessionDriftService.BuildFact(At, start, end, QpcFrequency);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("次序異常", f.UnavailableReason);
    }

    [Fact]
    public void 收集_注入起訖_不依賴真機時鐘()
    {
        var start = new ClockSessionDriftService.ClockSample(At, 0);
        var facts = ClockSessionDriftService.Collect(At, start,
            (At.AddMinutes(2).AddMilliseconds(50), 2 * 60 * (long)QpcFrequency), QpcFrequency);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(50.0, f.NumericValue!.Value, 3);
    }

    [Fact]
    public void Capture_起訖樣本自洽()
    {
        var s = ClockSessionDriftService.Capture();
        Assert.True(s.QpcTimestamp > 0);
        Assert.True(s.Utc <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void 牆鐘大幅回撥_保留累計負差而非誤判次序()
    {
        var start = new ClockSessionDriftService.ClockSample(At, 0);
        var f = ClockSessionDriftService.BuildFact(At, start,
            (At.AddSeconds(-180), 120 * (long)QpcFrequency), QpcFrequency);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(-300_000.0, f.NumericValue);
        Assert.Contains("NTP", f.Value);
        Assert.Null(f.UnavailableReason);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void 非法頻率_拒絕產生量測值(double frequency)
    {
        var start = new ClockSessionDriftService.ClockSample(At, 0);
        var f = ClockSessionDriftService.BuildFact(At, start,
            (At.AddMinutes(2), 120 * (long)QpcFrequency), frequency);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("頻率", f.UnavailableReason);
    }

    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(1e-300)]
    public void 換算溢位_不得讓Infinity流入快照(double frequency)
    {
        var start = new ClockSessionDriftService.ClockSample(At, 0);
        var f = ClockSessionDriftService.BuildFact(At, start,
            (At.AddMinutes(2), long.MaxValue), frequency);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("有限數值", f.UnavailableReason);
    }

    [Fact]
    public void Qpc大刻度_先相減保留差值且不溢位()
    {
        const long delta = 60_000_001;
        var closeStart = new ClockSessionDriftService.ClockSample(At, long.MaxValue - delta);
        var close = ClockSessionDriftService.BuildFact(At, closeStart,
            (At.AddTicks(600_000_010), long.MaxValue), 1_000_000);
        Assert.Equal(FactAvailability.Present, close.Availability);
        Assert.Equal(0.0, close.NumericValue!.Value, 6);

        var wideStart = new ClockSessionDriftService.ClockSample(At, long.MinValue);
        var wide = ClockSessionDriftService.BuildFact(At, wideStart,
            (At.AddSeconds(120), long.MaxValue), (double)ulong.MaxValue / 120);
        Assert.Equal(FactAvailability.Present, wide.Availability);
        Assert.Equal(0.0, wide.NumericValue!.Value, 6);
    }

    [Fact]
    public void 短窗門檻以Qpc判定_小差值不冒稱量測精度()
    {
        var start = new ClockSessionDriftService.ClockSample(At, 0);
        var below = ClockSessionDriftService.BuildFact(At, start,
            (At.AddHours(1), 60 * (long)QpcFrequency - 1), QpcFrequency);
        Assert.Equal(FactAvailability.Unknown, below.Availability);
        Assert.Null(below.NumericValue);
        var exact = ClockSessionDriftService.BuildFact(At, start,
            (At.AddSeconds(60), 60 * (long)QpcFrequency), QpcFrequency);
        Assert.Equal(FactAvailability.Present, exact.Availability);
        Assert.Equal(0.0, exact.NumericValue);
        Assert.Contains("不代表量測精度", exact.Value);
        Assert.Contains("非同步讀鐘誤差", exact.Value);
    }
}
