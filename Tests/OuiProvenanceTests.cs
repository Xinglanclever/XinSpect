using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// MAC OUI 的<b>查詢統計</b>（第 285 輪）——與第 284 輪的 PCI 同一個病根：
/// 回收端靠「值裡有沒有出現某句中文」判斷，而不是靠結構化的事實。
///
/// <para>
/// <b>這一批抓到一個真的會說謊的缺陷：</b><c>CollectOui</c> 的分類順序是
/// 未收錄 → 無法解析 → 沒有實體網路介面 → 其餘算命中。而 WMI 查詢失敗時
/// <c>NicHealthFactsService</c> 產生的值是<b>空字串</b>（狀態在 <c>Availability</c> 上，
/// 不在文字裡）——於是「一條網卡都沒讀到」被算成<b>一次命中</b>，
/// <c>net.oui</c> 會印出「1 次查詢全部命中」。
/// </para>
///
/// <para>
/// 修法與 PCI 一致：出處寫成固定標記（產生端與回收端共用常數），
/// 而「這次查詢有沒有發生」看 <c>Availability</c>——那是結構化欄位，
/// 不是一行給人讀的字。
/// </para>
/// </summary>
public class OuiProvenanceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<HardwareFact> Macs(params (string Name, string Mac)[] adapters) =>
        NicHealthFactsService.CollectMacVendors(At, probe: () => adapters.ToList());

    private static HardwareFact Bare(string value,
        FactAvailability availability = FactAvailability.Present) =>
        new("nic.mac.0", "網路", "MAC 廠商對照", value, "",
            "WMI Win32_NetworkAdapter＋OUI 知識庫", FactTrustLevel.Reported, false, At, null, availability);

    // ── 產生端：出處寫成固定標記 ────────────────────────────────────────

    [Theory]
    [InlineData("A0-AF-1D-11-22-33", OuiKnowledge.OuiOrigin.Hit)]
    [InlineData("AA-BB-CC-DD-EE-FF", OuiKnowledge.OuiOrigin.Unlisted)]
    [InlineData("not-a-mac", OuiKnowledge.OuiOrigin.Unparsable)]
    [InlineData("", OuiKnowledge.OuiOrigin.Unparsable)]
    public void 查詢本身的出處三態(string mac, OuiKnowledge.OuiOrigin expected)
        => Assert.Equal(expected, OuiKnowledge.OriginOf(mac));

    [Fact]
    public void 描述一定帶名稱出處標記()
    {
        Assert.Contains(OuiKnowledge.OriginClausePrefix, OuiKnowledge.Describe("A0-AF-1D-11-22-33"));
        Assert.Contains(OuiKnowledge.CuratedToken, OuiKnowledge.Describe("A0-AF-1D-11-22-33"));
        Assert.Contains(OuiKnowledge.UnlistedToken, OuiKnowledge.Describe("AA-BB-CC-DD-EE-FF"));
        Assert.Contains(OuiKnowledge.UnparsableToken, OuiKnowledge.Describe("not-a-mac"));

        // 原始 MAC 要留在值裡——未收錄時那是使用者自己去查的依據。
        Assert.Contains("AA-BB-CC", OuiKnowledge.Describe("AA-BB-CC-DD-EE-FF"));
    }

    [Fact]
    public void 出處代號是穩定字串_不是靠中文句子推斷()
    {
        Assert.Equal("名稱出處：", OuiKnowledge.OriginClausePrefix);
        Assert.Equal("手工表", OuiKnowledge.CuratedToken);
        Assert.Equal("未收錄", OuiKnowledge.UnlistedToken);
        Assert.Equal("無法解析", OuiKnowledge.UnparsableToken);
    }

    // ── 回收端：查詢有沒有發生，看 Availability ────────────────────────

    [Fact]
    public void 讀不到WMI時_回收端不得把它算成命中()
    {
        // 這是本輪抓到的缺陷：值裡沒有那三句中文，於是被歸成「命中」。
        var facts = NicHealthFactsService.CollectMacVendors(At, probe: () => null);
        Assert.Equal(FactAvailability.ReadError, facts.Single().Availability);

        var stats = KnowledgeLookupCollector.CollectOui(facts);

        Assert.Equal(0, stats.Total);        // 查詢根本沒發生
        Assert.Equal(0, stats.Hits);         // 更不能算命中

        var fact = KnowledgeLookupReport.ToOuiFact(At, stats);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有查詢", fact.Value);
        Assert.DoesNotContain("全部命中", fact.Value);
    }

    [Fact]
    public void 沒有實體網卡不是查詢失敗_也不進統計()
    {
        var facts = Macs();                                  // 空清單＝這台沒有實體網卡
        Assert.Equal(FactAvailability.NotApplicable, facts.Single().Availability);

        var stats = KnowledgeLookupCollector.CollectOui(facts);
        Assert.Equal(0, stats.Total);
        Assert.Equal(0, stats.Unparsable);   // 「沒有東西可查」不是「輸入聽不懂」
    }

    [Fact]
    public void 回收端照出處標記分類()
    {
        var stats = KnowledgeLookupCollector.CollectOui(Macs(
            ("乙太網路", "A0-AF-1D-11-22-33"),
            ("Wi-Fi", "AA-BB-CC-DD-EE-FF")));

        Assert.Equal(2, stats.Total);
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.NotListed);
        Assert.Equal(0, stats.Unparsable);
        Assert.Contains("AA-BB-CC", stats.MissedKeys.Single());   // 未收錄要把原始碼留下
    }

    [Fact]
    public void 值裡沒有出處標記的Present事實算無法分類而不是命中()
    {
        // 生產端改版後忘了帶出處時，統計要說「無從分類」而不是安靜地算命中。
        var stats = KnowledgeLookupCollector.CollectOui([Bare("A0-AF-1D-11-22-33（Intel）")]);

        Assert.Equal(1, stats.Total);
        Assert.Equal(0, stats.Hits);
        Assert.Equal(1, stats.Unparsable);
    }

    [Fact]
    public void 逐介面的值要帶出處標記_不再只是括號裡一個名字()
    {
        var facts = Macs(("乙太網路", "A0-AF-1D-11-22-33"));

        var value = facts.Single(f => f.Key == "nic.mac.0").Value;
        Assert.Contains(OuiKnowledge.OriginClausePrefix, value);
        Assert.Contains(OuiKnowledge.CuratedToken, value);
    }

    [Fact]
    public void 知識表只有一層_所以出處不會冒充登錄檔()
    {
        // OUI 表是本專案收錄的 IEEE 前綴子集，沒有第二層。值裡若出現「登錄檔」，
        // 就是有人在拿 PCI 的兩層架構描述 OUI——那會讓 18 筆的表看起來像兩萬筆。
        Assert.DoesNotContain("登錄檔", OuiKnowledge.Describe("A0-AF-1D-11-22-33"));
        Assert.DoesNotContain("登錄檔", OuiKnowledge.Describe("AA-BB-CC-DD-EE-FF"));
    }
}
