using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Top-down Level 1 四桶運算的純函式測試。不接觸 PMU／MSR，只驗算式與邊界。
/// </summary>
public class TopDownMathTests
{
    [Fact]
    public void 四桶合計為百分之百()
    {
        // CLKS=1000 → SLOTS=4000；退休 2000（50%）、前端未供給 400（10%）、發射 2200（壞投機 5%）
        var (ret, bs, fe, be) = TopDownMath.Compute(1000, 400, 2000, 2200);
        Assert.Equal(50.0, ret, 3);
        Assert.Equal(5.0, bs, 3);
        Assert.Equal(10.0, fe, 3);
        Assert.Equal(35.0, be, 3);
        Assert.Equal(100.0, ret + bs + fe + be, 3);
    }

    [Fact]
    public void 發射少於退休時壞投機夾為零而非下溢()
    {
        // iss < ret 會發生（計數器讀取有時間差）。若以 ulong 相減會繞回接近 2^64、夾成 100%，
        // 把 Backend 擠成 0，整列失真——必須先轉 double。
        var (ret, bs, fe, be) = TopDownMath.Compute(100, 0, 300, 100);
        Assert.Equal(0.0, bs, 6);
        Assert.Equal(75.0, ret, 3);
        Assert.Equal(25.0, be, 3);
        Assert.Equal(0.0, fe, 6);
    }

    [Fact]
    public void 零周期回全零不除以零()
    {
        var (ret, bs, fe, be) = TopDownMath.Compute(0, 12345, 6789, 9999);
        Assert.Equal(0.0, ret);
        Assert.Equal(0.0, bs);
        Assert.Equal(0.0, fe);
        Assert.Equal(0.0, be);
    }

    [Fact]
    public void 前三桶超過百分之百時後端夾為零()
    {
        // 退休 80% ＋ 前端 40% 已超過 100%（SMT 或計數重疊時可能發生），Backend 不得為負。
        var (ret, _, fe, be) = TopDownMath.Compute(1000, 1600, 3200, 3200);
        Assert.Equal(80.0, ret, 3);
        Assert.Equal(40.0, fe, 3);
        Assert.Equal(0.0, be, 6);
    }

    [Fact]
    public void 單桶不超過百分之百()
    {
        // 退休插槽超過總插槽（不該發生，但計數器溢位／重疊時要夾住）
        var (ret, _, _, be) = TopDownMath.Compute(100, 0, 999_999, 999_999);
        Assert.Equal(100.0, ret, 6);
        Assert.Equal(0.0, be, 6);
    }

    [Fact]
    public void 每周期退休插槽上限為四()
    {
        Assert.Equal(2.0, TopDownMath.SlotsPerCycleRetired(1000, 2000), 6);
        Assert.Equal(4.0, TopDownMath.SlotsPerCycleRetired(1000, 99_999), 6);   // 夾在 4
        Assert.Equal(0.0, TopDownMath.SlotsPerCycleRetired(0, 1234), 6);        // 不除以零
    }

    [Fact]
    public void 解讀取占比最大的一桶()
    {
        Assert.Contains("後端", TopDownMath.Verdict(20, 5, 10, 65));
        Assert.Contains("前端", TopDownMath.Verdict(20, 5, 60, 15));
        Assert.Contains("錯誤推測", TopDownMath.Verdict(20, 55, 10, 15));
        Assert.Contains("退休", TopDownMath.Verdict(70, 5, 10, 15));
    }

    [Fact]
    public void 全零時解讀說明無法歸因()
        => Assert.Contains("無法歸因", TopDownMath.Verdict(0, 0, 0, 0));

    [Fact]
    public void 每周期插槽數為四()
        => Assert.Equal(4, TopDownMath.SlotsPerCycle);
}
