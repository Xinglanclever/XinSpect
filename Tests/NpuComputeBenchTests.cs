using Xunit;

namespace XinSpect.Tests;

/// <summary>僅驗證 CPU SIMD 乘加的純 FLOP 計數；不啟動真正基準或硬體負載。</summary>
public sealed class NpuComputeBenchTests
{
    [Fact]
    public void CountFlops_DefaultWorkloadCountsElementsOnce()
    {
        // 400 萬個元素、50 次，每個元素一乘一加；向量 lane 數不可再乘一次。
        Assert.Equal(400_000_000L, NpuComputeBenchService.CountFlops(4_000_000, 50));
    }

    [Theory]
    [InlineData(1, 1, 2L)]
    [InlineData(3, 7, 42L)]
    [InlineData(17, 3, 102L)]
    [InlineData(0, 0, 0L)]
    [InlineData(0, 50, 0L)]
    [InlineData(17, 0, 0L)]
    public void CountFlops_IncludesScalarTailsAndAcceptsZeroWork(int elements, int iterations, long expected)
    {
        Assert.Equal(expected, NpuComputeBenchService.CountFlops(elements, iterations));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(-1, -1)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(1, int.MinValue)]
    public void CountFlops_RejectsNegativeArgumentsEvenWhenOtherArgumentIsZero(int elements, int iterations)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NpuComputeBenchService.CountFlops(elements, iterations));
    }

    [Fact]
    public void CountFlops_UsesLongArithmeticWithoutIntIntermediateOverflow()
    {
        Assert.Equal(4_294_967_294L, NpuComputeBenchService.CountFlops(int.MaxValue, 1));
        // 兩個參數皆為非負 int 時，最大合法結果仍小於 long.MaxValue。
        // checked 保護不代表應拒絕這組合法輸入；不得先以 int 相乘而溢位。
        Assert.Equal(9_223_372_028_264_841_218L,
            NpuComputeBenchService.CountFlops(int.MaxValue, int.MaxValue));
    }
}
