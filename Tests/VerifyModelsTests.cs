using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 驗機事實模型：事實袋的取值語意。
/// </summary>
/// <remarks>
/// 這組測試存在的理由只有一句話：**「讀不到」不是 0**。
/// 二手驗機的每一條規則都在做算術，只要事實袋在缺值時回 0，
/// 「通電 0 小時卻寫入 8TB」這種假矛盾就會憑空長出來，而畫面上看起來完全合理。
/// 故取不到一律回 null，由引擎判成「無法判定」。
/// </remarks>
public class VerifyModelsTests
{
    private static VerifyFact Fact(FactId id, double? num, string value = "x") => new(
        id, "標籤", value, num, "", FactSource.Smbios, "SMBIOS Type 17 +0x15",
        NeedsAdmin: false, FactTrust.FirmwareReported, DateTime.UnixEpoch);

    [Fact]
    public void 事實袋_取不到的事實一律回報缺少()
    {
        var facts = new VerifyFacts([Fact(FactId.DimmCount, 2)]);

        Assert.True(facts.Has(FactId.DimmCount));
        Assert.Equal(2, facts.Num(FactId.DimmCount));

        Assert.False(facts.Has(FactId.NvmePowerOnHours));
        Assert.Null(facts.Num(FactId.NvmePowerOnHours));   // 不得回 0——0 是一個值，缺少不是
        Assert.Null(facts.Text(FactId.NvmePowerOnHours));
        Assert.Null(facts.Get(FactId.NvmePowerOnHours));
    }

    [Fact]
    public void 事實袋_同一個FactId重複裝入時以後者為準()
    {
        var facts = new VerifyFacts([Fact(FactId.DimmCount, 2), Fact(FactId.DimmCount, 4)]);
        Assert.Equal(4, facts.Num(FactId.DimmCount));
    }

    [Fact]
    public void 事實袋_數值為零與缺少必須分得開()
    {
        var facts = new VerifyFacts([Fact(FactId.NvmePercentageUsed, 0, "0 %")]);
        Assert.True(facts.Has(FactId.NvmePercentageUsed));
        Assert.Equal(0, facts.Num(FactId.NvmePercentageUsed));   // 真的讀到 0
    }

    [Fact]
    public void 事實_必須帶得出讀取方法與信賴度()
    {
        var f = Fact(FactId.DimmCount, 2);
        Assert.False(string.IsNullOrWhiteSpace(f.Method));
        Assert.Equal(FactSource.Smbios, f.Source);
        Assert.Equal(FactTrust.FirmwareReported, f.Trust);
        Assert.False(f.NeedsAdmin);
    }
}
