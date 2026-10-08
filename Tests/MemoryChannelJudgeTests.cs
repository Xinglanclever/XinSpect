using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 記憶體通道配置判讀（純函式）。核心要釘住的是：
/// 「每支模組各佔一個通道」是理論上限計算的前提，而這個前提<b>可以被插槽配置推翻</b>——
/// 假設不成立時要說出來，否則算出來的達成率會被當成事實。
/// </summary>
public class MemoryChannelJudgeTests
{
    private static MemoryChannelEvidence Evidence(params string[] locators)
    {
        var channels = new HashSet<string>();
        foreach (string l in locators)
        {
            int i = l.IndexOf('_');
            channels.Add(i >= 0 && i + 1 < l.Length ? l[(i + 1)..(i + 2)] : l);
        }
        return new MemoryChannelEvidence(locators, channels.Count, locators.Length > 0, locators.Length);
    }

    // ── 分類：已知答案 ────────────────────────────────────────────────────

    [Fact]
    public void 每通道一條且插槽未滿_說還有空間並提醒成對插()
    {
        var v = MemoryChannelJudge.Judge(Evidence("DIMM_A1", "DIMM_B1"), slotsPerChannel: 1, totalSlots: 4);

        Assert.Equal(MemoryChannelJudge.ChannelKind.OnePerChannelRoomLeft, v.Kind);
        Assert.False(v.Attention);
        Assert.Contains("2 個空插槽", v.Headline);
        Assert.Contains("成對", v.Evidence);
    }

    [Fact]
    public void 每通道一條且插槽已滿_說是最理想插法()
    {
        var v = MemoryChannelJudge.Judge(
            Evidence("DIMM_A1", "DIMM_B1", "DIMM_C1", "DIMM_D1"), slotsPerChannel: 1, totalSlots: 4);

        Assert.Equal(MemoryChannelJudge.ChannelKind.OnePerChannelFull, v.Kind);
        Assert.Contains("插滿", v.Headline);
        Assert.Contains("最理想", v.Evidence);
    }

    [Fact]
    public void 同通道掛兩條_判為容量優先插法並說明頻寬影響()
    {
        var v = MemoryChannelJudge.Judge(
            Evidence("DIMM_A1", "DIMM_A2", "DIMM_B1", "DIMM_B2"), slotsPerChannel: 2, totalSlots: 4);

        Assert.Equal(MemoryChannelJudge.ChannelKind.TwoPerChannel, v.Kind);
        Assert.Contains("每通道 2 條", v.Headline);
        Assert.Contains("容量", v.Evidence);
        Assert.Contains("不是故障", v.Evidence);
    }

    [Fact]
    public void 全部擠在同一通道_判為最該提醒的插錯()
    {
        var v = MemoryChannelJudge.Judge(Evidence("DIMM_A1", "DIMM_A2"), slotsPerChannel: 2, totalSlots: 4);

        Assert.Equal(MemoryChannelJudge.ChannelKind.SingleChannelOnly, v.Kind);
        Assert.True(v.Attention);
        Assert.Contains("同一個通道", v.Headline);
        Assert.Contains("一半", v.Evidence);
    }

    [Fact]
    public void 通道推不出來_判為未知而不是猜()
    {
        var v = MemoryChannelJudge.Judge(new MemoryChannelEvidence(["SLOT1", "SLOT2"], 0, false, 2), 0, 8);

        Assert.Equal(MemoryChannelJudge.ChannelKind.Unknown, v.Kind);
        Assert.Contains("看不出通道編號", v.Headline);
        Assert.Contains("不猜", v.Evidence);
        Assert.False(v.Attention);
    }

    [Fact]
    public void 沒有模組_判為未知()
    {
        var v = MemoryChannelJudge.Judge(new MemoryChannelEvidence([], 0, false, 0), 0, 4);
        Assert.Equal(MemoryChannelJudge.ChannelKind.Unknown, v.Kind);
    }

    // ── 假設是否成立 ──────────────────────────────────────────────────────

    [Fact]
    public void 每通道一條_假設成立要明說成立()
    {
        string note = MemoryChannelJudge.PeakAssumptionNote(Evidence("DIMM_A1", "DIMM_B1"), 1);
        Assert.Contains("假設成立", note);
    }

    [Fact]
    public void 同通道掛兩條_假設站不住要說明達成率偏低是預期的()
    {
        // 這是這個方法存在的理由：上限值不變，但實測通常較低，
        // 不說的話使用者會以為達成率低是硬體有問題
        string note = MemoryChannelJudge.PeakAssumptionNote(Evidence("DIMM_A1", "DIMM_A2"), 2);

        Assert.Contains("2 支", note);
        Assert.Contains("上限值不變", note);
        Assert.Contains("預期", note);
    }

    [Fact]
    public void 通道不明_假設無法驗證要說僅供參考()
    {
        string note = MemoryChannelJudge.PeakAssumptionNote(
            new MemoryChannelEvidence(["SLOT1"], 0, false, 1), 0);

        Assert.Contains("無法驗證", note);
        Assert.Contains("以實測值為準", note);
    }

    // ── 佈局整合 ──────────────────────────────────────────────────────────

    [Fact]
    public void 佈局整合_每通道模組數要算出來()
    {
        var rows = new List<SmbiosDimmRow>
        {
            new("DIMM_A1", "", "8 GB", "DDR4", "3600 MT/s", "", "SK Hynix", "", "ZhuQue_8G_Y", "", 64, 72, 0x04),
            new("DIMM_A2", "", "8 GB", "DDR4", "3600 MT/s", "", "SK Hynix", "", "ZhuQue_8G_Y", "", 64, 72, 0x04),
            new("DIMM_B1", "", "8 GB", "DDR4", "3600 MT/s", "", "SK Hynix", "", "ZhuQue_8G_Y", "", 64, 72, 0x04),
        };

        var v = DimmLayout.Build(rows);

        Assert.Equal(2, v.SlotsPerChannel);   // 通道 A 有兩支
        Assert.False(string.IsNullOrWhiteSpace(v.ChannelHeadline));
        Assert.Contains("通道", v.ChannelHeadline);
    }

    [Fact]
    public void 佈局整合_四通道各一條_假設成立()
    {
        // 本機實況的形狀：A1／B1／C1／D1 各一條，四條通道各一支
        var rows = new List<SmbiosDimmRow>
        {
            new("DIMM_A1", "NODE 1", "8 GB", "DDR4", "3600 MT/s", "3600 MT/s", "SK Hynix", "", "ZhuQue_8G_Y", "", 64, 72, 0x04),
            new("DIMM_B1", "NODE 1", "8 GB", "DDR4", "3600 MT/s", "3600 MT/s", "SK Hynix", "", "ZhuQue_8G_Y", "", 64, 72, 0x04),
            new("DIMM_C1", "NODE 1", "8 GB", "DDR4", "3600 MT/s", "3600 MT/s", "SK Hynix", "", "ZJ-4000-C18-8G-RWMC", "", 64, 72, 0x04),
            new("DIMM_D1", "NODE 1", "8 GB", "DDR4", "3600 MT/s", "3600 MT/s", "SK Hynix", "", "ZJ-4000-C18-8G-RWMC", "", 64, 72, 0x04),
        };

        var v = DimmLayout.Build(rows, platformEcType: 0x03);

        Assert.Equal(1, v.SlotsPerChannel);
        Assert.Contains("假設成立", v.PeakAssumptionNote);
        // 型號不同（兩種）→ 應有型號不一致的提醒
        Assert.Contains(v.Notes, n => n.Contains("型號不同"));
    }
}
