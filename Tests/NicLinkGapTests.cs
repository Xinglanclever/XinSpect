using System;
using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 網卡落差判讀（純函式）。這一組測試要釘住的核心是：
/// <b>一張 PCIe 網卡受兩個上限約束</b>——PCIe 匯流排（速度×寬度）與乙太網路線路（協商速率），
/// 而只報一個「1 Gbps」等於什麼都沒說，看不出是插槽只給 x1、是接在 1G 交換器上、還是驅動被鎖住。
/// </summary>
public class NicLinkGapTests
{
    private static NicLinkSample Sample(
        string name = "乙太網路", string desc = "Marvell 10G Ethernet connection",
        bool up = true, string media = "802.3",
        int curSp = 3, int curW = 2, int maxSp = 3, int maxW = 4,
        ulong eth = 1_000_000_000, ulong[]? supported = null)
        => new(name, desc, up, media, curSp, curW, maxSp, maxW, eth,
               supported ?? [100_000_000, 1_000_000_000, 2_500_000_000, 5_000_000_000, 10_000_000_000]);

    // ── 頻寬換算：編碼效率不能漏 ──────────────────────────────────────────

    [Fact]
    public void 頻寬換算_Gen3要扣128b130b而Gen2要扣8b10b()
    {
        // Gen3 x4：8 GT/s × 4 × 128/130 = 31.5 Gbit/s
        Assert.Equal(8e9 * 4 * 128.0 / 130.0, NicLinkGap.PcieBandwidthBps(3, 4), 1);
        // Gen2 x4：5 GT/s × 4 × 8/10 = 16 Gbit/s（漏扣會多算 25%）
        Assert.Equal(16e9, NicLinkGap.PcieBandwidthBps(2, 4), 1);
    }

    [Fact]
    public void 頻寬換算_速度或寬度為零一律回零()
    {
        Assert.Equal(0, NicLinkGap.PcieBandwidthBps(0, 4));
        Assert.Equal(0, NicLinkGap.PcieBandwidthBps(3, 0));
        Assert.Equal(0, NicLinkGap.PcieBandwidthBps(99, 4));   // 未知世代不硬套
    }

    // ── 分類：已知答案 ────────────────────────────────────────────────────

    [Fact]
    public void 未連線_不比較鏈路()
    {
        var v = NicLinkGap.Judge(Sample(up: false, eth: 0));

        Assert.Equal(NicLinkGap.GapKind.NotConnected, v.Kind);
        Assert.False(v.Attention);
        Assert.Contains("未連線", v.Headline);
    }

    [Fact]
    public void PCIe寬度不足_判為寬度受限且值得查()
    {
        // 本機實況：8 GT/s x2 而能力是 8 GT/s x4
        var v = NicLinkGap.Judge(Sample(curSp: 3, curW: 2, maxSp: 3, maxW: 4, eth: 1_000_000_000));

        Assert.Equal(NicLinkGap.GapKind.PcieWidthLimited, v.Kind);
        Assert.True(v.Attention);
        Assert.Contains("x2", v.Headline);
        Assert.Contains("x4", v.Headline);
        Assert.Contains("通道分配表", v.Evidence);
    }

    [Fact]
    public void 十G線路但PCIe只給x1_要明說線路跑不滿()
    {
        // Gen3 x1 單向約 7.9 Gbit/s < 10 Gbit/s：真實的效能上限，不是顯示問題
        var v = NicLinkGap.Judge(Sample(curSp: 3, curW: 1, maxSp: 3, maxW: 1, eth: 10_000_000_000));

        Assert.True(v.Kind is NicLinkGap.GapKind.PcieWidthLimited or NicLinkGap.GapKind.PcieStarvedForLine);

        var r = NicLinkGap.Judge(Sample(curSp: 3, curW: 1, maxSp: 3, maxW: 1, eth: 10_000_000_000));
        Assert.Contains("跑不滿", r.Evidence);
    }

    [Fact]
    public void 寬度沒掉但供給不足_判為匯流排餵不飽線路()
    {
        // x1 剛好等於能力（所以沒有寬度落差），但 10G 線路需要更多
        var v = NicLinkGap.Judge(Sample(curSp: 3, curW: 1, maxSp: 3, maxW: 1, eth: 10_000_000_000));

        Assert.Equal(NicLinkGap.GapKind.PcieStarvedForLine, v.Kind);
        Assert.True(v.Attention);
        Assert.Contains("PCIe 供給", v.Headline);
        Assert.Contains("雙向", v.Evidence);   // 必須說明為什麼要算雙向
    }

    [Fact]
    public void 線路協商低於能力_判為協商較低且列出三個常見原因()
    {
        // 本機實況：10G 網卡跑 1 Gbps
        var v = NicLinkGap.Judge(Sample(curSp: 3, curW: 4, maxSp: 3, maxW: 4, eth: 1_000_000_000));

        Assert.Equal(NicLinkGap.GapKind.LineNegotiatedLower, v.Kind);
        Assert.Contains("1 Gbps", v.Headline);
        Assert.Contains("10 Gbps", v.Headline);
        Assert.Contains("線材", v.Evidence);
        Assert.Contains("對端", v.Evidence);
        Assert.Contains("驅動", v.Evidence);
        Assert.False(v.Attention);   // 不是故障：線路是兩端協商的結果
    }

    [Fact]
    public void 全部到位_判為相符()
    {
        var v = NicLinkGap.Judge(Sample(curSp: 3, curW: 4, maxSp: 3, maxW: 4, eth: 10_000_000_000));

        Assert.Equal(NicLinkGap.GapKind.None, v.Kind);
        Assert.False(v.Attention);
        Assert.Contains("10 Gbps", v.Headline);
    }

    [Fact]
    public void 虛擬介面_不套PCIe判讀且如實說明不適用()
    {
        // Meta Tunnel／Wintun 這類軟體通道：沒有 PCIe 端點、速率是名目值
        var v = NicLinkGap.Judge(new NicLinkSample("MX-300HL", "Meta Tunnel", true, "IP",
            0, 0, 0, 0, 100_000_000_000, []));

        Assert.Equal(NicLinkGap.GapKind.Unknown, v.Kind);
        Assert.False(v.Attention);
        Assert.Contains("不適用", v.Evidence);
    }

    [Fact]
    public void 沒有介面名稱_判為未知而不是相符()
    {
        var v = NicLinkGap.Judge(new NicLinkSample("", "", true, "", 0, 0, 0, 0, 0, []));
        Assert.Equal(NicLinkGap.GapKind.Unknown, v.Kind);
    }

    // ── 不得誤導 ──────────────────────────────────────────────────────────

    [Fact]
    public void 讀不到支援速率清單_不得因此判為相符()
    {
        // 清單為空＝讀不到，不是「沒有更高速率可支援」
        var v = NicLinkGap.Judge(Sample(curSp: 3, curW: 4, maxSp: 3, maxW: 4,
            eth: 1_000_000_000, supported: []));

        Assert.Equal(NicLinkGap.GapKind.None, v.Kind);   // PCIe 與線路都比對過了，只能說相符
        Assert.DoesNotContain("支援到", v.Headline);      // 但不得宣稱「已達最高支援速率」
    }

    [Fact]
    public void 任何分類_Headline與Evidence都不得為空()
    {
        var samples = new[]
        {
            Sample(),
            Sample(up: false, eth: 0),
            Sample(curW: 2, maxW: 4),
            Sample(curW: 4, maxW: 4, eth: 10_000_000_000),
            Sample(curW: 1, maxW: 1, eth: 10_000_000_000),
            new NicLinkSample("", "", true, "", 0, 0, 0, 0, 0, []),
            new NicLinkSample("X", "Y", true, "IP", 0, 0, 0, 0, 100_000_000_000, []),
        };
        foreach (var s in samples)
        {
            var v = NicLinkGap.Judge(s);
            Assert.False(string.IsNullOrWhiteSpace(v.Headline));
            Assert.False(string.IsNullOrWhiteSpace(v.Evidence));
        }
    }

    // ── 速率字串 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0UL, "—")]
    [InlineData(100_000_000UL, "100 Mbps")]
    [InlineData(1_000_000_000UL, "1 Gbps")]
    [InlineData(2_500_000_000UL, "2.5 Gbps")]
    [InlineData(10_000_000_000UL, "10 Gbps")]
    [InlineData(100_000_000_000UL, "100 Gbps")]
    public void 速率格式化_不產生小數點過多的Gbps(ulong bps, string expected)
        => Assert.Equal(expected, NicLinkGap.Bps(bps));

    [Theory]
    [InlineData("10 Gbps", 10_000_000_000UL)]
    [InlineData("2.5 Gbps", 2_500_000_000UL)]
    [InlineData("100 Mbps", 100_000_000UL)]
    public void 驅動回報的速率字串_要能解析(string text, ulong expected)
        => Assert.Equal(expected, NicLinkFactsService.ParseSpeed(text));

    [Theory]
    [InlineData("Auto Negotiation")]
    [InlineData("")]
    [InlineData("半雙工")]
    public void 非速率字串_回null不猜(string text)
        => Assert.Null(NicLinkFactsService.ParseSpeed(text));
}
