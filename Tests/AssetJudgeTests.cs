using System;
using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 機器識別與資產的判讀（純函式）。核心要釘住的是：組裝機與部分主機板的 SMBIOS 識別欄位
/// 填的是預設字串（「Default string」「To be filled by O.E.M.」），看起來像有值其實沒有——
/// 把它們當序號登錄，資產清單上就會出現一堆一模一樣的「Default string」。
/// </summary>
public class AssetJudgeTests
{
    private static AssetField F(string label, string value, bool placeholder = false, string source = "SMBIOS")
        => new(label, value, source, placeholder);

    // ── 機箱類型 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x11)]  // 工作站
    [InlineData(0x12)]  // 伺服器
    [InlineData(0x1F)]  // 主機式
    [InlineData(0x25)]  // 刀鋒機箱
    [InlineData(0x26)]  // 刀鋒伺服器機箱
    [InlineData(0x27)]  // 機架式
    [InlineData(0x32)]  // 嵌入式邊緣伺服器
    public void 伺服器類機箱_要認出來(byte code)
        => Assert.True(AssetJudge.IsServerChassisType(code));

    [Theory]
    [InlineData(0x03)]  // 桌上型
    [InlineData(0x07)]  // 塔式
    [InlineData(0x09)]  // 筆記型
    [InlineData(0x1C)]  // 膝上型
    [InlineData(0x00)]
    public void 一般用途機箱_不得誤判為伺服器(byte code)
        => Assert.False(AssetJudge.IsServerChassisType(code));

    // ── 全部未填：最該提醒的情況 ──────────────────────────────────────────

    [Fact]
    public void 全部是預設字串_判為無法唯一識別並說明後果()
    {
        // 本機實況的形狀：系統序號「System Serial Number」、機箱序號「Default string」
        var r = new AssetRecord(
        [
            F("系統序號", "System Serial Number", true),
            F("機箱序號", "Default string", true),
            F("資產標籤", "Default string", true),
        ], ChassisTypeCode: 0x03, ChassisTypeName: "桌上型（Desktop）", Uuid: "6EFA5D53-B040-A576-2CC1", IsServerChassis: false);

        var v = AssetJudge.Judge(r);

        Assert.Equal(0, v.UsableFields);
        Assert.Equal(3, v.PlaceholderFields);
        Assert.False(v.CanIdentify);
        Assert.Contains("無法唯一識別", v.Headline);
        Assert.Contains("形同虛設", v.Evidence);
        // 必須給出替代方案但不代為選擇
        Assert.Contains("磁碟序號", v.Evidence);
        Assert.Contains("不代為選擇", v.Evidence);
    }

    [Fact]
    public void 全部未填時_不得說成故障()
    {
        var r = new AssetRecord([F("系統序號", "Default string", true)], 0x03, "桌上型（Desktop）", "", false);
        var v = AssetJudge.Judge(r);
        Assert.Contains("不是故障", v.Evidence);
    }

    // ── 部分可用 ──────────────────────────────────────────────────────────

    [Fact]
    public void 主機板序號有值_判為可識別並列出未填的欄位()
    {
        // 本機實況：主機板序號 181242565700856 有值，其餘是預設字串
        var r = new AssetRecord(
        [
            F("主機板序號", "181242565700856"),
            F("系統序號", "System Serial Number", true),
            F("機箱序號", "Default string", true),
        ], ChassisTypeCode: 0x03, ChassisTypeName: "桌上型（Desktop）", Uuid: "6EFA5D53-B040-A576-2CC1", IsServerChassis: false);

        var v = AssetJudge.Judge(r);

        Assert.Equal(1, v.UsableFields);
        Assert.Equal(2, v.PlaceholderFields);
        Assert.True(v.CanIdentify);
        Assert.Contains("主機板序號", v.Headline);
        Assert.Contains("系統序號", v.Headline);   // 未填的也要列出來
    }

    [Fact]
    public void 伺服器機箱_要標明屬伺服器機房類()
    {
        var r = new AssetRecord([F("機箱序號", "ABC123")], 0x27, "機架式機箱（Rack Mount）", "", true);
        var v = AssetJudge.Judge(r);
        Assert.Contains("伺服器／機房類", v.Evidence);
    }

    [Fact]
    public void 一般機箱_要標明屬一般用途類()
    {
        var r = new AssetRecord([F("機箱序號", "ABC123")], 0x03, "桌上型（Desktop）", "", false);
        var v = AssetJudge.Judge(r);
        Assert.Contains("一般用途類", v.Evidence);
    }

    [Fact]
    public void 沒有任何欄位_判為未知()
    {
        var v = AssetJudge.Judge(new AssetRecord([], 0, "", "", false));
        Assert.Contains("讀不到", v.Headline);
        Assert.False(v.CanIdentify);
    }

    [Fact]
    public void 空字串與預設字串_都算未填()
    {
        var r = new AssetRecord([F("系統序號", ""), F("機箱序號", "Default string", true)], 0x03, "桌上型", "", false);
        var v = AssetJudge.Judge(r);
        Assert.Equal(0, v.UsableFields);
        Assert.Equal(2, v.PlaceholderFields);
    }

    [Fact]
    public void UUID要出現在依據裡()
    {
        var r = new AssetRecord([F("主機板序號", "X")], 0x03, "桌上型", "6EFA5D53-B040-A576-2CC1", false);
        Assert.Contains("6EFA5D53", AssetJudge.Judge(r).Evidence);
    }

    [Fact]
    public void 任何情況_Headline與Evidence都不得為空()
    {
        var records = new[]
        {
            new AssetRecord([F("主機板序號", "X")], 0x03, "桌上型", "U", false),
            new AssetRecord([F("系統序號", "Default string", true)], 0x03, "桌上型", "", false),
            new AssetRecord([], 0, "", "", false),
        };
        foreach (var r in records)
        {
            var v = AssetJudge.Judge(r);
            Assert.False(string.IsNullOrWhiteSpace(v.Headline));
            Assert.False(string.IsNullOrWhiteSpace(v.Evidence));
        }
    }
}
