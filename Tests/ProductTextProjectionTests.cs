using Xunit;

namespace XinSpect.Tests;

public sealed class ProductTextProjectionTests
{
    [Fact]
    public void ExternalReadingCountTemplateTranslatesWithoutChangingTheCount()
        => Assert.Equal("3 readings", ProductTextProjection.LocalizeKnown("3 筆讀數", english: true, simplified: false));

    [Fact]
    public void UnknownProviderPayloadIsReturnedVerbatim()
    {
        const string raw = "provider reason: C:\\記憶體\\sensor.dat / 原始錯誤";
        Assert.Equal(raw, ProductTextProjection.LocalizeKnown(raw, english: true, simplified: false));
        Assert.Equal("provider item 個", ProductTextProjection.LocalizeKnown("provider item 個", english: true, simplified: false));
        Assert.Equal("provider item 個", ProductTextProjection.LocalizeKnown("provider item 個", english: false, simplified: true));
    }

    [Fact]
    public void MixedCatalogTemplateKeepsCapturedProviderTextVerbatim()
    {
        const string rawReason = "provider: C:\\記憶體\\sensor.dat／讀取失敗";
        string source = "來源不可採信：ReadError／Reported。" + rawReason;
        string projected = ProductTextProjection.LocalizeKnown(source, english: true, simplified: false);
        Assert.Equal("Source cannot be trusted: ReadError/Reported. " + rawReason, projected);
    }

    [Fact]
    public void SensorLogPrefixAndExceptionPayloadAreProjectedSeparately()
    {
        const string raw = "C:\\記憶體\\logs\\sensor.csv: access denied";
        Assert.Equal("Logging failed: " + raw,
            ProductTextProjection.LocalizeKnown("記錄失敗：", english: true, simplified: false) + raw);
        Assert.Equal("记录失败：" + raw,
            ProductTextProjection.LocalizeKnown("記錄失敗：", english: false, simplified: true) + raw);
    }

    [Fact]
    public void ExternalQueryFailureTemplateKeepsExceptionPayloadVerbatim()
    {
        const string raw = "Access denied at C:\\記憶體\\probe.bin";
        Assert.Equal("Query failed: " + raw,
            ProductTextProjection.LocalizeKnown("查詢失敗：" + raw, english: true, simplified: false));
    }

    [Fact]
    public void ExternalSourceRowLocalizesProductDetailAndPreservesProviderError()
    {
        const string raw = "provider error: C:\\記憶體\\sensor.bin";
        var oldEnglish = LanguageService.IsEnglish;
        var oldSimplified = LanguageService.IsSimplified;
        try
        {
            LanguageService.SetEnglishForTests(true);
            Assert.Equal("Not detected", new SourceStatus("HWiNFO", false, "", ProductDetail: "未偵測到").DisplayDetail);
            Assert.Equal(raw, new SourceStatus("HWiNFO", false, raw).DisplayDetail);
        }
        finally
        {
            if (oldEnglish) LanguageService.SetEnglishForTests(true);
            else { LanguageService.SetEnglishForTests(false); LanguageService.SetSimplifiedForTests(oldSimplified); }
        }
    }
}
