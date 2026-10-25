using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 第 270 輪 SMART 知識包的顯示接線（消費端）：
/// <see cref="StorageSmartService.AttributeName(byte, string?)"/> 只補強「廠商自訂」那條路徑
/// （既有對照表認得的 ID 逐字不變）、raw 六位元組按 <c>RawSemantics</c> 附註、
/// <see cref="KnowledgeLookupCollector.CollectSmart"/>／<c>ToSmartFact</c> 的統計與三態。
/// </summary>
public class SmartKnowledgeWiringTests
{
    private static readonly SmartVendor[] Vendors =
    [
        SmartVendor.Samsung, SmartVendor.Seagate, SmartVendor.WesternDigital,
        SmartVendor.Kingston, SmartVendor.Micron, SmartVendor.SanDisk,
    ];

    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>找一個六家表都未收錄的屬性 ID（負向測試用，不硬編碼表內容）。</summary>
    private static byte UnknownIdEverywhere()
    {
        for (int id = 255; id >= 1; id--)
            if (Vendors.All(v => SmartAttributeKnowledge.Lookup(v, (byte)id) is null))
                return (byte)id;
        throw new InvalidOperationException("六家表收錄了所有 ID——負向測試失去錨點");
    }

    // ── AttributeName：既有行為不變 ─────────────────────────────────────

    [Fact]
    public void 單參版行為逐字不變_既有測試的斷言照樣成立()
    {
        Assert.Equal("重配置磁區數", StorageSmartService.AttributeName(5));
        Assert.Equal("廠商自訂", StorageSmartService.AttributeName(0x63));
    }

    [Fact]
    public void 既有表認得的ID_帶型號也逐字不變()
    {
        Assert.Equal(StorageSmartService.AttributeName(5), StorageSmartService.AttributeName(5, "Samsung SSD 870 EVO"));
        Assert.Equal(StorageSmartService.AttributeName(199), StorageSmartService.AttributeName(199, "WDC WD10EZEX"));
    }

    [Fact]
    public void 未知ID加未收錄廠商_維持廠商自訂原樣()
    {
        byte id = UnknownIdEverywhere();
        Assert.Equal("廠商自訂", StorageSmartService.AttributeName(id, "Samsung SSD 870 EVO"));
        Assert.Equal("廠商自訂", StorageSmartService.AttributeName(id, null));
        Assert.Equal("廠商自訂", StorageSmartService.AttributeName(id, "Intel SSD 660p"));   // 六家之外
        Assert.Equal("廠商自訂", StorageSmartService.AttributeName(id, ""));                 // 空型號
    }

    [Fact]
    public void 未知ID加已收錄廠商_知識表補名稱()
    {
        // Samsung 表的 177（Wear Leveling Count）不在單參既有對照表裡——正是要被知識表補強的路徑。
        Assert.Equal("廠商自訂", StorageSmartService.AttributeName(177));
        string name = StorageSmartService.AttributeName(177, "SAMSUNG SSD 870 EVO");
        Assert.StartsWith("廠商自訂（", name);
        Assert.Contains("Wear Leveling Count", name);
    }

    [Fact]
    public void 廠商名映射_含別名與大小寫()
    {
        Assert.True(StorageSmartService.TryVendorFromModel("WDC WD10EZEX-08WN4A0", out var wdc));
        Assert.Equal(SmartVendor.WesternDigital, wdc);
        Assert.True(StorageSmartService.TryVendorFromModel("WD Blue SN570", out var wd));
        Assert.Equal(SmartVendor.WesternDigital, wd);
        Assert.True(StorageSmartService.TryVendorFromModel("Crucial MX500", out var crucial));
        Assert.Equal(SmartVendor.Micron, crucial);
        Assert.True(StorageSmartService.TryVendorFromModel("seagate BarraCuda", out var seg));
        Assert.Equal(SmartVendor.Seagate, seg);
        Assert.True(StorageSmartService.TryVendorFromModel("SanDisk Ultra II", out var sd));
        Assert.Equal(SmartVendor.SanDisk, sd);
        Assert.True(StorageSmartService.TryVendorFromModel("KINGSTON SA400M8240G", out var kg));
        Assert.Equal(SmartVendor.Kingston, kg);
        Assert.False(StorageSmartService.TryVendorFromModel("Intel SSD 660p", out _));
        Assert.False(StorageSmartService.TryVendorFromModel(null, out _));
    }

    // ── DecodeAtaAttributes：只有補強路徑變 ─────────────────────────────

    private static byte[] BuildSector(byte id1, byte id2)
    {
        var sector = new byte[512];
        sector[0] = 0x10; sector[1] = 0x80;
        int o = 2;
        sector[o] = id1; sector[o + 3] = 100; sector[o + 4] = 99;
        o = 2 + 12;
        sector[o] = id2; sector[o + 3] = 90; sector[o + 4] = 80; sector[o + 5] = 0x11;
        return sector;
    }

    [Fact]
    public void 無型號_名稱與raw欄位與舊版逐字相同()
    {
        byte id = UnknownIdEverywhere();
        var rows = StorageSmartService.DecodeAtaAttributes(BuildSector(5, id));
        Assert.Contains("5 重配置磁區數", rows[0].Name);
        Assert.EndsWith("（LE: 0）", rows[0].RawText);
        Assert.Equal($"{id} 廠商自訂", rows[1].Name);
        Assert.EndsWith("（LE: 17）", rows[1].RawText);
        Assert.DoesNotContain("；raw：", rows[1].RawText);
    }

    [Fact]
    public void 帶已收錄廠商型號_廠商自訂ID被補名稱且raw按語意附註()
    {
        var rows = StorageSmartService.DecodeAtaAttributes(BuildSector(177, 5), "Samsung SSD 870 EVO");
        Assert.StartsWith("177 廠商自訂（Wear Leveling Count", rows[0].Name);
        Assert.EndsWith("（LE: 0）", rows[0].RawText.Split("；raw：")[0]);   // 原樣照列不受附註影響
        Assert.Contains("；raw：", rows[0].RawText);                        // RawSemantics 附註
        // 既有表認得的 ID（5）照舊，不附註。
        Assert.Equal("5 重配置磁區數", rows[1].Name);
        Assert.DoesNotContain("；raw：", rows[1].RawText);
    }

    // ── CollectSmart／ToSmartFact：知識表路徑的統計 ─────────────────────

    private static SmartRow Row(string name, byte id) => new(name, "", "", "", id, null);

    [Fact]
    public void 鍵字面常數如規格寫在原始碼()
    {
        Assert.Equal("ata.smart.attr.lookup", KnowledgeLookupCollector.SmartAttrLookupKey);
    }

    [Fact]
    public void CollectSmart只統計走過知識表的列_既有表命中不計入()
    {
        var stats = KnowledgeLookupCollector.CollectSmart(
        [
            Row("177 廠商自訂（Wear Leveling Count（耗損平衡計數））", 177),   // 知識表命中
            Row("199 廠商自訂", 199),                                          // 未收錄
            Row("5 重配置磁區數", 5),                                          // 既有對照表——不計
            Row("12 電源循環", 12),                                            // 既有對照表——不計
            Row("── 硬體識別 ──", 0),                                          // 分隔列——不計
        ]);
        Assert.Equal(2, stats.Total);
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.NotListed);
        Assert.Single(stats.MissedKeys);
    }

    [Fact]
    public void ToSmartFact_無查詢NotApplicable_全命中Present_有缺Unknown()
    {
        var none = KnowledgeLookupCollector.ToSmartFact(At, KnowledgeLookupReport.LookupStats.Empty);
        Assert.Equal("ata.smart.attr.lookup", none.Key);
        Assert.Equal(FactAvailability.NotApplicable, none.Availability);
        Assert.Null(none.NumericValue);

        var allHit = KnowledgeLookupCollector.ToSmartFact(At,
            KnowledgeLookupReport.Summarize([(KnowledgeLookupReport.LookupOutcome.Hit, "177 廠商自訂（X）")]));
        Assert.Equal(FactAvailability.Present, allHit.Availability);
        Assert.Equal(1, allHit.NumericValue);

        var missing = KnowledgeLookupCollector.ToSmartFact(At,
            KnowledgeLookupReport.Summarize([
                (KnowledgeLookupReport.LookupOutcome.Hit, "177 廠商自訂（X）"),
                (KnowledgeLookupReport.LookupOutcome.NotListed, "199 廠商自訂"),
            ]));
        Assert.Equal(FactAvailability.Unknown, missing.Availability);   // 缺口不是錯誤
        Assert.Null(missing.NumericValue);
        Assert.Contains("199 廠商自訂", missing.Value);                  // 未收錄的鍵如實列出
    }
}
