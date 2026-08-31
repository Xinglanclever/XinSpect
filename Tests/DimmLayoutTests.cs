using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 插槽配置圖的純邏輯：通道推斷、佔用判定、分組排序與判讀文字。
/// 重點在「推不出通道時要老實說推不出來」——猜錯通道會害人把記憶體插到錯的槽。
/// </summary>
public class DimmLayoutTests
{
    private static SmbiosDimmRow Row(string locator, string bank = "", string size = "未安裝",
                                     string part = "", string speed = "", string vendor = "")
        => new(locator, bank, size, "DDR5", speed, "", vendor, "", part, "");

    [Theory]
    [InlineData("DIMM_A1", "", "A", 1)]
    [InlineData("DIMM_B2", "", "B", 2)]
    [InlineData("DIMMA1", "", "A", 1)]
    [InlineData("ChannelA-DIMM0", "", "A", 0)]
    [InlineData("Controller0-ChannelB-DIMM1", "", "C0-B", 1)]
    [InlineData("A1_DIMM0", "", "A", 1)]
    [InlineData("DIMM_A", "", "A", int.MaxValue)]
    [InlineData("P1-DIMMA1", "", "P1-A", 1)]
    [InlineData("CPU0_DIMMB2", "", "C0-B", 2)]
    [InlineData("P2-DIMML1", "", "P2-L", 1)]
    public void 認得出常見的通道命名(string locator, string bank, string ch, int idx)
    {
        var (c, i) = DimmLayout.ParseChannel(locator, bank);
        Assert.Equal(ch, c);
        Assert.Equal(idx, i);
    }

    [Fact]
    public void 兩顆處理器的通道A不會被併成同一個()
    {
        var v = DimmLayout.Build([
            Row("P1-DIMMA1", size: "16 GB"),
            Row("P2-DIMMA1", size: "16 GB"),
        ]);

        Assert.True(v.ChannelsKnown);
        Assert.Equal(2, v.Channels.Count);
        // 兩顆 CPU 各插一條是正確插法，不能叫人「把第二條移到另一個通道」
        Assert.DoesNotContain("一半", v.Detail);
        Assert.Contains("2 個通道", v.Detail);
        Assert.Contains("P1", string.Join("|", v.Channels.Select(c => c.Name)));
    }

    [Fact]
    public void 插槽限定詞不會被當成通道字母()
    {
        // P1 的 P 是插槽編號；通道字母要取 A，不能取 P
        Assert.Equal("A", DimmLayout.ParseChannel("P1-DIMMA1", "").Channel[3..]);
    }

    [Fact]
    public void Locator與Bank矛盾時不給通道()
    {
        // Locator 明寫 B，Bank 卻寫 CHANNEL A：兩邊打架，寧可說不知道也別指錯插槽
        var (ch, idx) = DimmLayout.ParseChannel("DIMM_B1", "CHANNEL A");
        Assert.Equal("", ch);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void 只寫DIMM0的板子不猜通道()
    {
        var (ch, idx) = DimmLayout.ParseChannel("DIMM0", "BANK 0");
        Assert.Equal("", ch);
        Assert.Equal(0, idx);          // 序號還是拿得到
    }

    [Fact]
    public void Locator沒寫時退而讀Bank()
    {
        var (ch, _) = DimmLayout.ParseChannel("DIMM 1", "CHANNEL B");
        Assert.Equal("B", ch);
    }

    [Fact]
    public void 不把單字裡的字母當成通道()
    {
        Assert.Equal("", DimmLayout.ParseChannel("Bottom-Slot 1(left)", "").Channel);
        Assert.Equal("", DimmLayout.ParseChannel("SODIMM 1", "").Channel);
    }

    [Fact]
    public void 空清單不會炸也不假裝有資料()
    {
        var v = DimmLayout.Build([]);
        Assert.False(v.HasData);
        Assert.Empty(v.Channels);
        Assert.Equal(0, v.SlotCount);
    }

    [Fact]
    public void 四槽插兩條分成兩個通道()
    {
        var v = DimmLayout.Build([
            Row("DIMM_A1"),
            Row("DIMM_A2", size: "16 GB", part: "KF560C36-16", speed: "6000 MT/s"),
            Row("DIMM_B1"),
            Row("DIMM_B2", size: "16 GB", part: "KF560C36-16", speed: "6000 MT/s"),
        ]);

        Assert.True(v.ChannelsKnown);
        Assert.Equal(4, v.SlotCount);
        Assert.Equal(2, v.OccupiedCount);
        Assert.Equal(2, v.Channels.Count);
        Assert.Equal("通道 A", v.Channels[0].Name);
        Assert.Equal(1, v.Channels[0].Occupied);
        Assert.Contains("2 個通道", v.Detail);
        Assert.Contains("還有 2 個空插槽", string.Join("\n", v.Notes));
    }

    [Fact]
    public void 兩條都插同一通道時要明白說出來()
    {
        var v = DimmLayout.Build([
            Row("DIMM_A1", size: "8 GB"),
            Row("DIMM_A2", size: "8 GB"),
            Row("DIMM_B1"),
            Row("DIMM_B2"),
        ]);

        Assert.Contains("通道 A", v.Detail);
        Assert.Contains("一半", v.Detail);
    }

    [Fact]
    public void 推不出通道就退成單一群組並說明()
    {
        var v = DimmLayout.Build([Row("DIMM0", size: "8 GB"), Row("DIMM1")]);

        Assert.False(v.ChannelsKnown);
        Assert.Single(v.Channels);
        Assert.Equal("插槽", v.Channels[0].Name);
        Assert.Contains("看不出通道編號", v.Detail);
    }

    [Fact]
    public void 全部回報未安裝時指向韌體而不是說沒有記憶體()
    {
        var v = DimmLayout.Build([Row("DIMM_A1"), Row("DIMM_B1")]);

        Assert.Equal(0, v.OccupiedCount);
        Assert.Contains("韌體沒有填寫", v.Detail);
    }

    [Fact]
    public void 容量與型號不一致要各自提醒()
    {
        var v = DimmLayout.Build([
            Row("DIMM_A1", size: "8 GB", part: "AAA", speed: "3200 MT/s"),
            Row("DIMM_B1", size: "16 GB", part: "BBB", speed: "2666 MT/s"),
        ]);

        string notes = string.Join("\n", v.Notes);
        Assert.Contains("容量不一致", notes);
        Assert.Contains("型號不同", notes);
        Assert.Contains("速率不一致", notes);
    }

    [Fact]
    public void 佔用判定不把佔位字樣當成模組()
    {
        var v = DimmLayout.Build([Row("DIMM_A1", size: "—"), Row("DIMM_B1", size: "0")]);
        Assert.Equal(0, v.OccupiedCount);
        Assert.All(v.Slots, s => Assert.Equal("空", s.Detail));
    }

    [Fact]
    public void 空槽的說明不透露規格()
    {
        var v = DimmLayout.Build([Row("DIMM_A1")]);
        var slot = v.Slots[0];
        Assert.Equal("", slot.SizeText);
        Assert.Contains("未安裝模組", slot.Tip);
    }

    [Fact]
    public void 已安裝的說明帶上廠牌型號容量速率()
    {
        var v = DimmLayout.Build([
            Row("DIMM_A2", size: "16 GB", part: "KF560C36-16", speed: "6000 MT/s", vendor: "Kingston"),
        ]);
        var slot = v.Slots[0];
        Assert.Equal("A2", slot.Label);
        Assert.True(slot.Occupied);
        Assert.Contains("Kingston", slot.Tip);
        Assert.Contains("KF560C36-16", slot.Tip);
        Assert.Contains("16 GB", slot.Detail);
        Assert.Contains("6000 MT/s", slot.Detail);
    }

    [Fact]
    public void 同通道內按序號排序()
    {
        var v = DimmLayout.Build([Row("DIMM_A2"), Row("DIMM_A1"), Row("DIMM_B2"), Row("DIMM_B1")]);
        Assert.Equal(["A1", "A2", "B1", "B2"], v.Slots.Select(s => s.Label));
    }

    [Fact]
    public void 未命名插槽有可讀的替代標籤()
    {
        var v = DimmLayout.Build([Row("")]);
        Assert.Equal("（未命名插槽）", v.Slots[0].Label);
    }
}
