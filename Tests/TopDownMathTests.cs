using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Top-down Level 1 四桶運算的純函式測試。不接觸 PMU／MSR，只驗算式與邊界。
/// </summary>
/// <remarks>
/// 1.6 的關鍵改動：分母的「每周期配發插槽數」不再是寫死的 4，而是由
/// <see cref="MicroarchProfile"/> 依 CPUID 查出後傳進來。因此每個算式測試都要指定寬度，
/// 並且要有測試釘住「同一組計數器、不同寬度 ⇒ 不同比例」——否則寬度回歸成常數也不會有人發現。
/// </remarks>
public class TopDownMathTests
{
    [Fact]
    public void 四桶合計為百分之百()
    {
        // CLKS=1000、寬度 4 → SLOTS=4000；退休 2000（50%）、前端未供給 400（10%）、發射 2200（壞投機 5%）
        var (ret, bs, fe, be) = TopDownMath.Compute(1000, 400, 2000, 2200, 4);
        Assert.Equal(50.0, ret, 3);
        Assert.Equal(5.0, bs, 3);
        Assert.Equal(10.0, fe, 3);
        Assert.Equal(35.0, be, 3);
        Assert.Equal(100.0, ret + bs + fe + be, 3);
    }

    [Fact]
    public void 分母隨管線寬度改變_同一組計數器在寬機器上比例較小()
    {
        // 這正是 1.5.x 的錯誤所在：寬度寫死 4，在 Ice Lake（5）與 Golden Cove（6）上
        // 分母偏小，四桶百分比整組偏大，而畫面上看起來仍是「一組合理的數字」。
        var w4 = TopDownMath.Compute(1000, 400, 2000, 2200, 4);
        var w5 = TopDownMath.Compute(1000, 400, 2000, 2200, 5);
        var w6 = TopDownMath.Compute(1000, 400, 2000, 2200, 6);

        Assert.Equal(50.0, w4.Retiring, 3);
        Assert.Equal(40.0, w5.Retiring, 3);   // 2000 / (1000×5)
        Assert.Equal(2000 / 6000.0 * 100, w6.Retiring, 3);
        Assert.True(w4.Retiring > w5.Retiring && w5.Retiring > w6.Retiring);

        // 前端桶同樣被分母帶動，不是只有退休桶。
        Assert.Equal(10.0, w4.Frontend, 3);
        Assert.Equal(8.0, w5.Frontend, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-4)]
    public void 寬度不明時回全零而不是硬套一個分母(int width)
    {
        var (ret, bs, fe, be) = TopDownMath.Compute(1000, 400, 2000, 2200, width);
        Assert.Equal(0.0, ret);
        Assert.Equal(0.0, bs);
        Assert.Equal(0.0, fe);
        Assert.Equal(0.0, be);
    }

    [Fact]
    public void 發射少於退休時壞投機夾為零而非下溢()
    {
        // iss < ret 會發生（計數器讀取有時間差）。若以 ulong 相減會繞回接近 2^64、夾成 100%，
        // 把 Backend 擠成 0，整列失真——必須先轉 double。
        var (ret, bs, fe, be) = TopDownMath.Compute(100, 0, 300, 100, 4);
        Assert.Equal(0.0, bs, 6);
        Assert.Equal(75.0, ret, 3);
        Assert.Equal(25.0, be, 3);
        Assert.Equal(0.0, fe, 6);
    }

    [Fact]
    public void 零周期回全零不除以零()
    {
        var (ret, bs, fe, be) = TopDownMath.Compute(0, 12345, 6789, 9999, 4);
        Assert.Equal(0.0, ret);
        Assert.Equal(0.0, bs);
        Assert.Equal(0.0, fe);
        Assert.Equal(0.0, be);
    }

    [Fact]
    public void 前三桶超過百分之百時後端夾為零()
    {
        // 退休 80% ＋ 前端 40% 已超過 100%（SMT 或計數重疊時可能發生），Backend 不得為負。
        var (ret, _, fe, be) = TopDownMath.Compute(1000, 1600, 3200, 3200, 4);
        Assert.Equal(80.0, ret, 3);
        Assert.Equal(40.0, fe, 3);
        Assert.Equal(0.0, be, 6);
    }

    [Fact]
    public void 單桶不超過百分之百()
    {
        // 退休插槽超過總插槽（不該發生，但計數器溢位／重疊時要夾住）
        var (ret, _, _, be) = TopDownMath.Compute(100, 0, 999_999, 999_999, 4);
        Assert.Equal(100.0, ret, 6);
        Assert.Equal(0.0, be, 6);
    }

    [Fact]
    public void 每周期退休插槽夾在該架構的寬度上限()
    {
        Assert.Equal(2.0, TopDownMath.SlotsPerCycleRetired(1000, 2000, 4), 6);
        Assert.Equal(4.0, TopDownMath.SlotsPerCycleRetired(1000, 99_999, 4), 6);   // 寬度 4 → 夾在 4
        Assert.Equal(6.0, TopDownMath.SlotsPerCycleRetired(1000, 99_999, 6), 6);   // 寬度 6 → 夾在 6
        Assert.Equal(0.0, TopDownMath.SlotsPerCycleRetired(0, 1234, 4), 6);        // 不除以零
        Assert.Equal(0.0, TopDownMath.SlotsPerCycleRetired(1000, 1234, 0), 6);     // 寬度不明不出數字
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
}
