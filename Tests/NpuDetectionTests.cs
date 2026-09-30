using Xunit;

namespace XinSpect.Tests;

/// <summary>NPU 型號 → TOPS 查表的回歸測試：優先序與數字釘死，改表必須有意識地動測試。</summary>
public class NpuDetectionTests
{
    [Theory]
    // ── 裝置名比對（AMD／Qualcomm 的 NPU 裝置名帶型號）──
    [InlineData("AMD NPU Compute 3700", null, "~45")]
    [InlineData("AMD NPU Compute 3600", null, "~50")]
    [InlineData("Qualcomm Hexagon 520", null, "~80")]              // 特定型號贏過泛用 Hexagon
    [InlineData("Snapdragon X Elite Hexagon", null, "~45")]
    // ── CPU 名比對（Intel 的 NPU 裝置名是「Intel(R) AI Boost」，不帶平台資訊）──
    [InlineData("Intel(R) AI Boost", "Intel(R) Core(TM) Ultra 7 258V", "~48")]  // 後綴 V＝Lunar Lake
    [InlineData("Intel(R) AI Boost", "Intel(R) Core(TM) Ultra 9 285K", "~13")]  // K＝Arrow Lake
    [InlineData("Intel(R) AI Boost", "Intel(R) Core(TM) Ultra 7 155H", "~10")]  // 1xx H＝Meteor Lake
    [InlineData("Intel(R) AI Boost", "Intel(R) Core(TM) Ultra 5 226V", "~48")]  // 2xx V＝Lunar Lake
    // 兩路都不中 → 誠實說明
    [InlineData("Intel(R) AI Boost", null, "—")]
    public void LookupTops_命中與未命中(string device, string? cpuName, string expectedPrefix)
    {
        var r = NpuDetectionService.LookupTops(device, cpuName);
        Assert.StartsWith(expectedPrefix, r);
    }

    [Fact]
    public void 未命中時明說驅動沒給資訊而不是留白()
    {
        var r = NpuDetectionService.LookupTops("Unknown AI Accelerator");
        Assert.Contains("驅動也沒有提供算力資訊", r);
    }

    [Fact]
    public void 命中時標明是查表估計而非實測()
    {
        var r = NpuDetectionService.LookupTops("Lunar Lake");
        Assert.Contains("查表估計", r);
        Assert.Contains("非本機推論實測", r);
    }

    [Fact]
    public void 由CPU名判讀時說明平台依據()
    {
        var r = NpuDetectionService.LookupTops("Intel(R) AI Boost", "Core Ultra 7 258V");
        Assert.Contains("Lunar Lake", r);
        Assert.Contains("非本機推論實測", r);
    }
}
