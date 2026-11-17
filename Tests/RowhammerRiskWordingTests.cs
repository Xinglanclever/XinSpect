using Xunit;
namespace XinSpect.Tests;

/// <summary>Translation guard for the newly explicit physical-pressure exception. No pressure calls.</summary>
public sealed class RowhammerRiskWordingTests
{
    private const string LevelTwoTitle = "第二級：硬體寫入、合成威脅與危險記憶體壓力（高風險；預設關閉）";
    private const string LevelTwoNotice = "必須先開啟第一級。合成威脅情境使用合成資料；Rowhammer 則使用實體記憶體施壓，可能損壞其他處理程序資料，須專用測試機與逐次同意。硬體寫入會改變即時設定，仍需逐項確認。關閉本級不會自動還原已套用的值，還原控制仍可使用；還原許可不能授權壓力測試。";

    [Fact]
    public void EnglishPreservesPhysicalRiskAndSyntheticScenarioDistinction()
    {
        string title = Assert.IsType<string>(EnglishStrings.Lookup(LevelTwoTitle));
        string notice = Assert.IsType<string>(EnglishStrings.Lookup(LevelTwoNotice));
        Assert.Contains("dangerous memory stress", title);
        Assert.Contains("Synthetic threat scenarios use synthetic data.", notice);
        Assert.Contains("Rowhammer uses real memory", notice);
        Assert.Contains("other processes", notice);
        Assert.Contains("consent for each operation", notice);
        Assert.Contains("Recovery permission cannot authorize memory stress.", notice);
        Assert.Equal(LevelTwoNotice, EnglishStrings.Reverse(notice));
        Assert.NotNull(EnglishStrings.Lookup(RowhammerProbeService.GateNotice));
    }

    [Fact]
    public void SimplifiedPreservesPhysicalRiskAndConsentScope()
    {
        // Existing managed OpenCC text conversion; no language globals or hardware are changed.
        string notice = LanguageService.ToSimplified(LevelTwoNotice);
        Assert.Contains("合成威胁情境使用合成数据", notice);
        Assert.Contains("Rowhammer", notice);
        Assert.Contains("其他处理程序数据", notice);
        Assert.Contains("逐次同意", notice);
        Assert.DoesNotContain('\0', notice);
    }
}
