using Xunit;

namespace XinSpect.Tests;

/// <summary>紀年格式化：五種紀年的換算基準與自然排列格式。</summary>
public class EraCalendarTests
{
    private static readonly DateTime Sample = new(2026, 8, 28, 13, 5, 9);

    [Fact]
    public void Names_MatchEnumOrder()
    {
        // 下拉選單索引直接當作 EraMode 使用（MainViewModel.EraIndex），數量與順序必須對齊
        Assert.Equal(Enum.GetValues<EraMode>().Length, EraCalendar.Names.Length);
        // 使用者指定的順序：西元第一、民國第二、黃帝第三
        Assert.Equal(0, (int)EraMode.Gregorian);
        Assert.Equal(1, (int)EraMode.Minguo);
        Assert.Equal(2, (int)EraMode.Huangdi);
        Assert.Equal("西元紀年", EraCalendar.Names[(int)EraMode.Gregorian]);
        Assert.Equal("民國紀年", EraCalendar.Names[(int)EraMode.Minguo]);
        Assert.Equal("中華黃帝紀元", EraCalendar.Names[(int)EraMode.Huangdi]);
    }

    [Fact]
    public void Xuantong_名稱不冠大清而以括號標明朝代()
    {
        string name = EraCalendar.Names[(int)EraMode.Xuantong];
        Assert.Equal("宣統紀年〔大清〕", name);
        Assert.DoesNotContain("惡搞", name);
        Assert.False(name.StartsWith("大清", StringComparison.Ordinal));
    }

    [Fact]
    public void Gregorian_UsesNaturalOrder()
        => Assert.Equal("西元 2026 年 8 月 28 日  13:05:09", EraCalendar.Format(Sample, EraMode.Gregorian));

    [Theory]
    [InlineData(EraMode.Huangdi, "中華黃帝紀元 4724 年")]    // 2026 + 2698
    [InlineData(EraMode.Xuantong, "宣統 118 年")]            // 2026 − 1908
    [InlineData(EraMode.Minguo, "民國 115 年")]              // 2026 − 1911
    public void Eras_UseCorrectEpoch(EraMode mode, string expectedPrefix)
        => Assert.StartsWith(expectedPrefix, EraCalendar.Format(Sample, mode));

    [Fact]
    public void Doraemon_BeforeBirthYear_CountsDown()
        => Assert.StartsWith("哆啦A夢前 86 年", EraCalendar.Format(Sample, EraMode.Doraemon));

    [Fact]
    public void Doraemon_BirthYear_IsFirstYear()
        => Assert.StartsWith("哆啦A夢元年", EraCalendar.Format(new DateTime(2112, 1, 1), EraMode.Doraemon));

    [Fact]
    public void Doraemon_AfterBirthYear_CountsUp()
        => Assert.StartsWith("哆啦A夢 2 年", EraCalendar.Format(new DateTime(2113, 1, 1), EraMode.Doraemon));

    [Fact]
    public void AllModes_ContainTimeOfDay()
    {
        foreach (var mode in Enum.GetValues<EraMode>())
            Assert.Contains("13:05:09", EraCalendar.Format(Sample, mode));
    }
}
