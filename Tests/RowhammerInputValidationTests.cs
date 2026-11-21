using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// R367：只驗純大小驗證，不呼叫單輪／多輪壓力入口，也不配置探測緩衝區。
/// 既有 RowhammerProbeTests 的壓力執行案例不屬於本定向驗證。
/// </summary>
public sealed class RowhammerInputValidationTests
{
    [Theory]
    [InlineData(1u, 1_048_576)]
    [InlineData(2u, 2_097_152)]
    [InlineData(256u, 268_435_456)]
    [InlineData(2047u, 2_146_435_072)]
    public void 合法大小_純換算涵蓋熱點且不溢位(uint targetMegabytes, int expectedBytes)
    {
        int bytes = RowhammerProbeService.ValidateTargetMegabytes(targetMegabytes);

        Assert.Equal(expectedBytes, bytes);
        Assert.True(bytes > 0x2000); // 最大熱點偏移必須在緩衝區內。
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2048u)]
    [InlineData(4095u)]
    [InlineData(4096u)]
    [InlineData(4097u)]
    [InlineData(4_294_967_295u)]
    public void 零與超界大小_在換算配置前拒絕(uint targetMegabytes)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            RowhammerProbeService.ValidateTargetMegabytes(targetMegabytes));

        Assert.Equal("targetMegabytes", ex.ParamName);
        Assert.Equal(targetMegabytes, Assert.IsType<uint>(ex.ActualValue));
    }
}
