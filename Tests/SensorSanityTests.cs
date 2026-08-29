using LibreHardwareMonitor.Hardware;
using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 感測讀值合理性閘門測試：驗證「寧可顯示『—』，也不顯示假數字」——
/// NaN／±∞／哨兵值／離譜跳點一律回 null，落在物理範圍內的真實讀值則原樣通過。
/// </summary>
public class SensorSanityTests
{
    // ── 非數值：所有型別一律擋掉 ──────────────────────────────────

    [Theory]
    [InlineData(SensorType.Temperature)]
    [InlineData(SensorType.Load)]
    [InlineData(SensorType.Clock)]
    [InlineData(SensorType.Voltage)]
    [InlineData(SensorType.Power)]
    [InlineData(SensorType.Fan)]
    [InlineData(SensorType.Level)]
    [InlineData(SensorType.Data)]
    [InlineData(SensorType.Factor)]
    public void NotANumber_IsNeverUsable(SensorType type)
    {
        Assert.Null(SensorSanity.Plausible(type, float.NaN));
        Assert.Null(SensorSanity.Plausible(type, float.PositiveInfinity));
        Assert.Null(SensorSanity.Plausible(type, float.NegativeInfinity));
        Assert.Null(SensorSanity.Plausible(type, (float?)null));
    }

    // ── 溫度 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(28.5)]
    [InlineData(62)]
    [InlineData(99.9)]
    [InlineData(130)]      // 上界仍算真實讀值
    public void RealTemperature_PassesThroughUnchanged(double c)
        => Assert.Equal(c, SensorSanity.Plausible(SensorType.Temperature, (float)c)!.Value, 3);

    [Theory]
    [InlineData(0)]        // 運轉中的機器沒有 0 °C 的核心：這是「未讀到」的編碼
    [InlineData(-40)]
    [InlineData(-273.15)]
    [InlineData(255)]      // 常見哨兵值
    [InlineData(6553.5)]   // 0xFFFF 半刻度
    public void ImpossibleTemperature_IsRejected(double c)
        => Assert.Null(SensorSanity.Plausible(SensorType.Temperature, (float)c));

    // ── 百分比類（負載 / 風扇輸出 / 水位）────────────────────────

    [Theory]
    [InlineData(SensorType.Load, 0)]        // 閒置核心
    [InlineData(SensorType.Load, 47.3)]
    [InlineData(SensorType.Load, 100)]
    [InlineData(SensorType.Control, 0)]     // 零轉速模式
    [InlineData(SensorType.Level, 0)]       // 壽命歸零的磁碟：0 必須是可用讀值
    [InlineData(SensorType.Level, 97)]
    public void RealPercent_PassesThrough(SensorType type, double p)
        => Assert.Equal(p, SensorSanity.Plausible(type, (float)p)!.Value, 3);

    [Theory]
    [InlineData(100.4, 100)]     // 量化誤差：夾回 100
    [InlineData(-0.3, 0)]        // 量化誤差：夾回 0
    public void PercentQuantisationError_IsClampedNotDropped(double raw, double expected)
        => Assert.Equal(expected, SensorSanity.Plausible(SensorType.Load, (float)raw)!.Value, 3);

    [Theory]
    [InlineData(6553.5)]
    [InlineData(255)]
    [InlineData(-50)]
    public void ImpossiblePercent_IsRejected(double p)
        => Assert.Null(SensorSanity.Plausible(SensorType.Load, (float)p));

    // ── 頻率 / 電壓 ───────────────────────────────────────────────

    [Theory]
    [InlineData(3593.6)]
    [InlineData(12_000)]
    public void RealClock_PassesThrough(double mhz)
        => Assert.Equal(mhz, SensorSanity.Plausible(SensorType.Clock, (float)mhz)!.Value, 3);

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(99_999)]
    public void ImpossibleClock_IsRejected(double mhz)
        => Assert.Null(SensorSanity.Plausible(SensorType.Clock, (float)mhz));

    [Theory]
    [InlineData(1.312)]      // 核心電壓
    [InlineData(12.096)]     // 主機板 +12 V 軌：上界必須容得下
    public void RealVoltage_PassesThrough(double v)
        => Assert.Equal(v, SensorSanity.Plausible(SensorType.Voltage, (float)v)!.Value, 3);

    [Theory]
    [InlineData(0)]
    [InlineData(-1.2)]
    [InlineData(999)]
    public void ImpossibleVoltage_IsRejected(double v)
        => Assert.Null(SensorSanity.Plausible(SensorType.Voltage, (float)v));

    // ── 功耗 / 風扇轉速：0 可能為真，只擋負值與離譜值 ────────────

    [Theory]
    [InlineData(SensorType.Power, 0)]
    [InlineData(SensorType.Power, 65.4)]
    [InlineData(SensorType.Fan, 0)]          // 零轉速模式
    [InlineData(SensorType.Fan, 1180)]
    public void ZeroIsRealForPowerAndFan(SensorType type, double v)
        => Assert.Equal(v, SensorSanity.Plausible(type, (float)v)!.Value, 3);

    [Theory]
    [InlineData(SensorType.Power, -5)]
    [InlineData(SensorType.Power, 99_999)]
    [InlineData(SensorType.Fan, -1)]
    [InlineData(SensorType.Fan, 65_535)]
    public void ImpossiblePowerOrFan_IsRejected(SensorType type, double v)
        => Assert.Null(SensorSanity.Plausible(type, (float)v));

    // ── 未知型別：不假裝知道它的物理界線 ─────────────────────────

    [Fact]
    public void UnknownType_OnlyRejectsNonNumbers()
    {
        Assert.Equal(123456, SensorSanity.Plausible(SensorType.Factor, 123456f)!.Value, 3);
        Assert.Null(SensorSanity.Plausible(SensorType.Factor, float.NaN));
    }

    [Fact]
    public void IsUsable_MirrorsPlausible()
    {
        Assert.True(SensorSanity.IsUsable(SensorType.Temperature, 55f));
        Assert.False(SensorSanity.IsUsable(SensorType.Temperature, 0f));
        Assert.False(SensorSanity.IsUsable(SensorType.Temperature, float.NaN));
    }
}
