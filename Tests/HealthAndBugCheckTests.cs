using Xunit;

namespace XinSpect.Tests;

/// <summary>健康分級門檻：溫度／負載／磁碟空間的四級（含未知）判定。</summary>
public class HealthTests
{
    [Fact]
    public void MissingReading_IsNeutral()
    {
        Assert.Equal(Severity.Neutral, Health.Cpu(null));
        Assert.Equal(Severity.Neutral, Health.Gpu(null));
        Assert.Equal(Severity.Neutral, Health.Disk(null));
    }

    [Theory]
    [InlineData(45, Severity.Good)]
    [InlineData(79.9, Severity.Good)]
    [InlineData(80, Severity.Warning)]
    [InlineData(89.9, Severity.Warning)]
    [InlineData(90, Severity.Serious)]
    [InlineData(99.9, Severity.Serious)]
    [InlineData(100, Severity.Critical)]
    public void CpuTemp_Thresholds(double t, Severity expected) => Assert.Equal(expected, Health.Cpu(t));

    [Theory]
    [InlineData(55, Severity.Good)]
    [InlineData(60, Severity.Warning)]
    [InlineData(75, Severity.Serious)]
    [InlineData(87, Severity.Critical)]
    public void GpuTemp_Thresholds(double t, Severity expected) => Assert.Equal(expected, Health.Gpu(t));

    [Theory]
    [InlineData(40, Severity.Good)]
    [InlineData(45, Severity.Warning)]
    [InlineData(55, Severity.Serious)]
    [InlineData(65, Severity.Critical)]
    public void DiskTemp_Thresholds(double t, Severity expected) => Assert.Equal(expected, Health.Disk(t));

    [Theory]
    [InlineData(0, Severity.Good)]
    [InlineData(49.9, Severity.Good)]
    [InlineData(50, Severity.Warning)]
    [InlineData(80, Severity.Serious)]
    [InlineData(95, Severity.Critical)]
    [InlineData(100, Severity.Critical)]
    public void Load_Thresholds(double p, Severity expected) => Assert.Equal(expected, Health.Load(p));

    [Theory]
    [InlineData(10, Severity.Good)]
    [InlineData(70, Severity.Warning)]
    [InlineData(85, Severity.Serious)]
    [InlineData(95, Severity.Critical)]
    public void Space_IsLooserThanLoad(double pct, Severity expected) => Assert.Equal(expected, Health.Space(pct));

    [Fact]
    public void Space_At60_IsGood_WhereLoadWouldWarn()
    {
        // 磁碟日常高占用屬正常，故 60% 已用仍為良好，而 60% 負載已是警告
        Assert.Equal(Severity.Good, Health.Space(60));
        Assert.Equal(Severity.Warning, Health.Load(60));
    }
}

/// <summary>藍屏停止代碼對照表。</summary>
public class BugCheckTests
{
    [Theory]
    [InlineData(0x0000000Au, "IRQL_NOT_LESS_OR_EQUAL")]
    [InlineData(0x0000001Au, "MEMORY_MANAGEMENT")]
    [InlineData(0x00000124u, "WHEA_UNCORRECTABLE_ERROR")]
    [InlineData(0x00000133u, "DPC_WATCHDOG_VIOLATION")]
    [InlineData(0x00000116u, "VIDEO_TDR_ERROR")]
    public void KnownCodes_ResolveToName(uint code, string expectedName)
    {
        var (name, reason) = BugCheck.Lookup(code);
        Assert.Equal(expectedName, name);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void UnknownCode_FallsBackWithoutThrowing()
    {
        var (name, reason) = BugCheck.Lookup(0xDEADBEEF);
        Assert.Contains("未收錄", name);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void EveryKnownCode_HasBothNameAndReason()
    {
        // 抽樣整表：任一項留空都會讓藍屏頁顯示空白列
        uint[] codes = [0x0A, 0x19, 0x1A, 0x1E, 0x24, 0x3B, 0x4A, 0x50, 0x7E, 0x7F, 0x9F,
                        0xBE, 0xC2, 0xC5, 0xD1, 0xEF, 0xF4, 0x116, 0x117, 0x119, 0x124, 0x133, 0x139, 0x13A, 0x14F];
        foreach (var c in codes)
        {
            var (name, reason) = BugCheck.Lookup(c);
            Assert.DoesNotContain("未收錄", name);
            Assert.False(string.IsNullOrWhiteSpace(reason), $"停止代碼 0x{c:X} 缺少原因提示");
        }
    }
}
