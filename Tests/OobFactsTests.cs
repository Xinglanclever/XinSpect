using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 帶外管理／RAID OOB 事實的契約：三態分離（無 BMC／無 RAID＝NotApplicable；有硬體但通路未實作＝
/// NotSupported；探測不可用＝如實標）、未施測聲明必在（A18 硬性要求 1）、本機探測（SMBIOS Type 38／
/// PCI 類別碼 0104/0107）。解碼器（SEL/FRU/SDR/MFI）未施測的聲明隨附。
/// </summary>
public class OobFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 無BMC與無RAID_NotApplicable_未施測聲明必在()
    {
        var facts = OobFactsService.Collect(At, bmcPresentProbe: () => false, raidPresentProbe: () => false);

        var ipmi = Assert.Single(facts, f => f.Key == "oob.ipmi");
        Assert.Equal(FactAvailability.NotApplicable, ipmi.Availability);
        Assert.Contains("本機無 BMC", ipmi.Value);
        Assert.Contains("未施測", ipmi.Value);
        Assert.Contains("僅支援 IPMI 訊息解碼", ipmi.Value); // 不宣稱支援 IPMI 本身

        var raid = Assert.Single(facts, f => f.Key == "raid.oob");
        Assert.Equal(FactAvailability.NotApplicable, raid.Availability);
        Assert.Contains("無 RAID 控制器", raid.Value);
        Assert.Contains("未施測", raid.Value);
        Assert.Contains("MFI 訊框解碼", raid.Value);
    }

    [Fact]
    public void 有硬體但通路未實作_NotSupported_與NotApplicable分離()
    {
        var facts = OobFactsService.Collect(At, bmcPresentProbe: () => true, raidPresentProbe: () => true);

        var ipmi = Assert.Single(facts, f => f.Key == "oob.ipmi");
        Assert.Equal(FactAvailability.NotSupported, ipmi.Availability);
        Assert.Contains("本機有 BMC", ipmi.Value);
        Assert.Contains("KCS 埠存取", ipmi.Value);

        var raid = Assert.Single(facts, f => f.Key == "raid.oob");
        Assert.Equal(FactAvailability.NotSupported, raid.Availability);
        Assert.Contains("有 RAID 控制器", raid.Value);
        Assert.Contains("通路未實作", raid.UnavailableReason);
    }

    [Fact]
    public void 探測不可用_如實標不推測()
    {
        var facts = OobFactsService.Collect(At, bmcPresentProbe: () => null, raidPresentProbe: () => null);
        Assert.All(facts, f =>
        {
            Assert.NotEqual(FactAvailability.NotApplicable, f.Availability);
            Assert.Contains("無法確認", f.UnavailableReason);
        });
    }

    [Fact]
    public void 本機探測_預設路徑不拋例外且回答或三態()
    {
        // 走真的 SMBIOS／PCI 探測（usermode 唯讀）——本機 ROG RAMPAGE VI EXTREME OMEGA：
        // 無 BMC、無 RAID 控制器，預期兩筆 NotApplicable；若環境變了，事實會誠實翻轉。
        var facts = OobFactsService.Collect(At);
        Assert.Equal(2, facts.Count);
        var ipmi = Assert.Single(facts, f => f.Key == "oob.ipmi");
        var raid = Assert.Single(facts, f => f.Key == "raid.oob");
        Assert.Contains("未施測", ipmi.Value);
        Assert.Contains("未施測", raid.Value);
    }
}
