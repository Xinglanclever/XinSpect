using Xunit;

namespace XinSpect.Tests;

public sealed class TargetedPlatformTests
{
    [Theory]
    [InlineData("ASUS ROG RAMPAGE VI EXTREME OMEGA X299", "X299")]
    [InlineData("Intel C612 LPC Controller", "C612")]
    [InlineData("Intel C600 Series LPC Controller", "C600")]
    [InlineData("Intel C621A Series LPC Controller", "C621A")]
    [InlineData("Intel C422 Series LPC Controller", "C422")]
    [InlineData("Intel X58 Express Chipset", "X58")]
    [InlineData("Intel X79 Express Chipset", "X79")]
    [InlineData("AMD EPYC Root Complex", "EPYC")]
    [InlineData("Intel W790 Chipset", "W790")]
    public void 晶片組辨識支援指定伺服器工作站與高階桌機(string evidence, string expected)
        => Assert.Equal(expected, ChipsetAnalysisService.TryMatchChipset(evidence, "", ""));

    [Theory]
    [InlineData("X58", "Intel X58 Express")]
    [InlineData("X79", "Intel X79")]
    [InlineData("X99", "Intel X99")]
    [InlineData("X299", "Intel X299")]
    [InlineData("C602", "Intel C602")]
    [InlineData("C612", "Intel C612")]
    [InlineData("C621", "Intel C621")]
    [InlineData("C422", "Intel C422")]
    [InlineData("SP3", "AMD SP3／EPYC")]
    [InlineData("SP5", "AMD SP5／EPYC")]
    [InlineData("WRX80", "AMD WRX80")]
    [InlineData("WRX90", "AMD WRX90")]
    public void 平台參考資料逐一對應使用者指定家族(string code, string expectedName)
    {
        ChipsetInfo.Entry entry = ChipsetInfo.Resolve(null, null, code);
        Assert.Equal(expectedName, entry.Name);
        Assert.NotEmpty(entry.Features);
    }

    [Fact]
    public void 晶片組型號以完整代碼比對不把後綴冒認()
    {
        Assert.Equal("—", ChipsetInfo.Resolve(null, null, "X299A").Name);
        Assert.Equal("未知", ChipsetAnalysisService.TryMatchChipset("X299A", "", ""));
        Assert.Equal("C600", ChipsetAnalysisService.TryMatchChipset("Intel C600 Series", "", ""));
    }

    [Theory]
    [InlineData("LGA1366", "LGA 1366")]
    [InlineData("LGA2011", "LGA 2011")]
    [InlineData("LGA2011-3", "LGA 2011-v3")]
    [InlineData("LGA2066", "LGA 2066")]
    [InlineData("LGA3647", "LGA 3647")]
    [InlineData("LGA4677", "LGA 4677")]
    [InlineData("SP3", "SP3")]
    [InlineData("SP5", "SP5")]
    [InlineData("sTRX4", "sTRX4 / TR4")]
    public void 腳座辨識分開Intel舊平台與AMD伺服器工作站家族(string raw, string expected)
        => Assert.Equal(expected, CpuPinoutService.NormalizeSocketName(raw));

    [Fact]
    public void SP3與Threadripper不再合併成同一腳座()
    {
        Assert.NotEqual(CpuPinoutService.NormalizeSocketName("SP3"), CpuPinoutService.NormalizeSocketName("TR4"));
        Assert.NotEqual(CpuPinoutService.NormalizeSocketName("LGA2011"), CpuPinoutService.NormalizeSocketName("LGA2011-3"));
    }

    [Theory]
    [InlineData("AMD EPYC 9654", "SP5", "Server board", "server-workstation")]
    [InlineData("Intel Xeon W-2295", "LGA2066", "C422 board", "server-workstation")]
    [InlineData("Intel Core i9-7980XE", "LGA2066", "X299 board", "hedt")]
    [InlineData("Intel Core i7-980X", "LGA1366", "X58 board", "hedt")]
    [InlineData("Intel Core i9-14900K", "LGA1700", "Z790 board", "msdt")]
    [InlineData("Intel Core i7-11800H", "BGA", "MODT X99 board", "modt")]
    [InlineData("Intel Core i9-9900K", "LGA1151", "OEM desktop", "msdt")]
    public void 平台證據會建議對應深測套件(string cpu, string socket, string board, string expected)
        => Assert.Equal(expected, DeepBenchTargetCatalog.Suggest(cpu, socket, board).Id);
}
