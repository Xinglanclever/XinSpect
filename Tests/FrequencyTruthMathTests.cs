using Xunit;

namespace XinSpect.Tests;

/// <summary>頻率真相的純函式測試：MSR 位元解讀與換算。不接觸硬體。</summary>
public class FrequencyTruthMathTests
{
    [Fact]
    public void 解本機實測的PLATFORM_INFO()
    {
        // 本機（i9-7980XE）實測值 0x80070C2CF3011A00：
        // 位 15:8 = 0x1A = 26（2.6 GHz 基頻）、位 47:40 = 0x0C = 12（1.2 GHz 最低效能）、
        // 位 55:48 = 0x07 = 7、位 28 = 1（倍頻解鎖，K/X 系列）。
        var (maxNt, minEff, minOp, unlocked) = FrequencyTruthMath.DecodePlatformInfo(0x80070C2CF3011A00);
        Assert.Equal(26, maxNt);
        Assert.Equal(12, minEff);
        Assert.Equal(7, minOp);
        Assert.True(unlocked);
    }

    [Fact]
    public void 倍頻鎖定時位二十八為零()
        => Assert.False(FrequencyTruthMath.DecodePlatformInfo(0x00000C0000011A00).RatioUnlocked);

    [Fact]
    public void BCLK由實測TSC除以基頻倍頻得出()
    {
        Assert.Equal(100.0, FrequencyTruthMath.BclkMhz(2_600_000_000.0, 26), 6);
        // 非 100 MHz 的平台不能被硬套成 100
        Assert.Equal(99.77, FrequencyTruthMath.BclkMhz(2_594_000_000.0, 26), 2);
    }

    [Fact]
    public void BCLK在倍頻為零或頻率無效時回零不除以零()
    {
        Assert.Equal(0.0, FrequencyTruthMath.BclkMhz(2_600_000_000.0, 0));
        Assert.Equal(0.0, FrequencyTruthMath.BclkMhz(0, 26));
        Assert.Equal(0.0, FrequencyTruthMath.BclkMhz(double.NaN, 26));
    }

    [Fact]
    public void 本機的0x1AE被判為分組式核心數門檻()
    {
        // 本機實測 0x1AE = 0x1C1812100C080402 → 2,4,8,12,16,18,24,28（遞增）
        Assert.True(FrequencyTruthMath.LooksLikeCoreCountFormat(0x1C1812100C080402));
    }

    [Fact]
    public void 全等或遞減的0x1AE不是核心數門檻()
    {
        Assert.False(FrequencyTruthMath.LooksLikeCoreCountFormat(0x2A2A2A2A2A2A2A2A));  // 全等＝倍頻
        Assert.False(FrequencyTruthMath.LooksLikeCoreCountFormat(0x2223242526272829));  // 遞減
        Assert.False(FrequencyTruthMath.LooksLikeCoreCountFormat(0));                   // 全零
        Assert.False(FrequencyTruthMath.LooksLikeCoreCountFormat(0x2A));                // 只有一組，不足以判定
    }

    [Fact]
    public void 分組式倍頻表逐組配對()
    {
        var g = FrequencyTruthMath.DecodeTurboGroups(0x2A2A2A2A2A2A2A2A, 0x1C1812100C080402);
        Assert.Equal(8, g.Count);
        Assert.Equal((2, 42), g[0]);
        Assert.Equal((18, 42), g[5]);
        Assert.Equal((28, 42), g[7]);
    }

    [Fact]
    public void 倍頻或核心數為零的組別跳過()
    {
        var g = FrequencyTruthMath.DecodeTurboGroups(0x0000_0000_0000_2A28, 0x0000_0000_0000_0402);
        Assert.Equal(2, g.Count);
        Assert.Equal((2, 0x28), g[0]);
        Assert.Equal((4, 0x2A), g[1]);
    }

    [Fact]
    public void 傳統倍頻表以位元組序對應一到八核()
    {
        var t = FrequencyTruthMath.DecodeLegacyTurboTable(0x0000_0000_0022_2426);
        Assert.Equal(3, t.Count);
        Assert.Equal((1, 0x26), t[0]);   // 1 核 38×
        Assert.Equal((2, 0x24), t[1]);
        Assert.Equal((3, 0x22), t[2]);
    }

    [Fact]
    public void 有效時脈比值在計數器回繞時回零()
    {
        Assert.Equal(0.0, FrequencyTruthMath.AperfMperfRatio(100, 100, 0, 50));   // MPERF 未前進
        Assert.Equal(0.0, FrequencyTruthMath.AperfMperfRatio(100, 50, 0, 50));    // MPERF 回繞
        Assert.Equal(0.0, FrequencyTruthMath.AperfMperfRatio(0, 100, 50, 10));    // APERF 回繞
    }

    [Fact]
    public void 有效時脈比值可大於一即為渦輪()
    {
        // 本機實測 lp2：比值 1.613、TSC 2.6 GHz → 約 4.2 GHz
        double r = FrequencyTruthMath.AperfMperfRatio(0, 1000, 0, 1613);
        Assert.Equal(1.613, r, 6);
        Assert.Equal(4193.8, r * 2_600_000_000.0 / 1e6, 1);
    }

    [Fact]
    public void 有效時脈比值可小於一即為降頻()
        => Assert.Equal(0.462, FrequencyTruthMath.AperfMperfRatio(1000, 2000, 500, 962), 6);

    [Fact]
    public void CPUID比例與反推晶振()
    {
        // 本機實測 CPUID 0x15：EBX=216、EAX=2 → 108；晶振欄 ECX=0（未回報）。
        // 實測 TSC 約 2591 MHz ÷ 108 ≒ 24.0 MHz，正是標準 24 MHz 晶振。
        double ratio = FrequencyTruthMath.TscRatio(216, 2);
        Assert.Equal(108.0, ratio, 6);
        double implied = FrequencyTruthMath.ImpliedCrystalMhz(2_591_200_000.0, ratio);
        Assert.Equal(23.99, implied, 2);
        Assert.Contains("24", FrequencyTruthMath.DescribeCrystal(implied));
    }

    [Fact]
    public void 比例的分子或分母為零時回零()
    {
        Assert.Equal(0.0, FrequencyTruthMath.TscRatio(0, 2));
        Assert.Equal(0.0, FrequencyTruthMath.TscRatio(216, 0));
        Assert.Equal(0.0, FrequencyTruthMath.ImpliedCrystalMhz(2.6e9, 0));
        Assert.Equal("", FrequencyTruthMath.DescribeCrystal(0));
    }

    [Fact]
    public void 反推晶振不接近標準值時如實說僅供參考()
        => Assert.Contains("僅供參考", FrequencyTruthMath.DescribeCrystal(31.7));
}
