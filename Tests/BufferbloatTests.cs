using Xunit;

namespace XinSpect.Tests;

/// <summary>Bufferbloat 評級：滿載延遲膨脹（loaded − idle）的分級門檻與邊界。</summary>
public class BufferbloatTests
{
    [Theory]
    [InlineData(10, 10, "A+")]     // 無膨脹
    [InlineData(10, 15, "A+")]     // +5 ms 邊界內
    [InlineData(10, 16, "A")]      // +6 ms
    [InlineData(10, 40, "A")]      // +30 ms 邊界
    [InlineData(10, 41, "B")]      // +31 ms
    [InlineData(10, 70, "B")]      // +60 ms 邊界
    [InlineData(10, 71, "C")]      // +61 ms
    [InlineData(10, 210, "C")]     // +200 ms 邊界
    [InlineData(8, 209, "F")]      // +201 ms
    public void 依延遲膨脹分級(double idle, double loaded, string expected)
        => Assert.Equal(expected, Bufferbloat.Grade(idle, loaded).Grade);

    [Fact]
    public void 滿載延遲低於閒置時膨脹夾為零_判為極佳()
    {
        var r = Bufferbloat.Grade(20, 12);   // 量測抖動造成 loaded < idle
        Assert.Equal(0, r.InflationMs);
        Assert.Equal("A+", r.Grade);
        Assert.Equal(Severity.Good, r.Severity);
    }

    [Fact]
    public void 嚴重膨脹判為Critical並附白話()
    {
        var r = Bufferbloat.Grade(8, 300);
        Assert.Equal(Severity.Critical, r.Severity);
        Assert.Equal(292, r.InflationMs);
        Assert.False(string.IsNullOrWhiteSpace(r.Label));
    }
}
