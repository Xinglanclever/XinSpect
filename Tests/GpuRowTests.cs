using Xunit;
using XinSpect;

namespace XinSpect.Tests;

public class GpuRowTests
{
    [Fact]
    public void GpuRow_HotSpotAndVramTemp_TextReflectsValue()
    {
        var row = new GpuRow("Test GPU");

        Assert.Equal("—", row.HotSpotText);
        Assert.Equal("—", row.VramTempText);

        row.HotSpotC = 87;
        row.VramTempC = 92;

        Assert.Equal("87 °C", row.HotSpotText);
        Assert.Equal("92 °C", row.VramTempText);
    }

    [Fact]
    public void GpuRow_HotSpotAndVramTemp_NullShowsDash()
    {
        var row = new GpuRow("Test GPU");

        row.HotSpotC = 80;
        row.VramTempC = 85;
        row.HotSpotC = null;
        row.VramTempC = null;

        Assert.Equal("—", row.HotSpotText);
        Assert.Equal("—", row.VramTempText);
    }
}
