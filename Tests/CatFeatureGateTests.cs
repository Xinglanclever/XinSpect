using Xunit;

namespace XinSpect.Tests;

public sealed class CatFeatureGateTests
{
    [Theory]
    [InlineData("oc")]
    [InlineData("gpuoc")]
    public void 超頻頁必須先通過貓咪確認(string key)
    {
        PageDef page = Assert.Single(PageRegistry.Pages, p => p.Key == key);

        Assert.True(page.RequiresCatConsent);
    }

    [Fact]
    public void 超頻確認仍保留既有風險確認()
    {
        PageDef page = Assert.Single(PageRegistry.Pages, p => p.Key == "oc");

        Assert.True(page.RequiresRiskConsent);
        Assert.True(page.RequiresCatConsent);
    }

    [Theory]
    [InlineData(AppLanguage.Traditional, "嘶——！")]
    [InlineData(AppLanguage.Simplified, "哈——！")]
    [InlineData(AppLanguage.English, "Hiss—!")]
    public void 依目前語言回應(AppLanguage language, string expected)
        => Assert.Equal(expected, CatFeatureGate.ReactionText(language));
}
