using System;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SMART 廠商屬性知識表（ULTIMATE-10000 第 14 列）的契約：六家表各自 ID 排序無重複、
/// 每列有名稱與出處、raw／normalized 欄位語意齊備、未知 ID／未知廠商回未知不丟例外、
/// 數量有下限（查到多少算多少，訊息如實）、大小有上限（防腐化式膨脹）。
/// </summary>
public class SmartAttributeKnowledgeTests
{
    private static readonly SmartVendor[] Vendors =
    [
        SmartVendor.Samsung, SmartVendor.Seagate, SmartVendor.WesternDigital,
        SmartVendor.Kingston, SmartVendor.Micron, SmartVendor.SanDisk,
    ];

    private const int MinRowsPerVendor = 15;   // 下限：查證得到的才收——實際收了多少就是多少
    private const int MaxRowsPerVendor = 45;   // 上限：防止沒有出處的列被慢慢塞進來
    private const int MaxRowsTotal = 200;

    // ── 表的形狀 ─────────────────────────────────────────────────────────

    [Fact]
    public void 六家表各自_ID嚴格升冪無重複()
    {
        foreach (var vendor in Vendors)
        {
            var ids = SmartAttributeKnowledge.Table(vendor).Select(a => (int)a.Id).ToList();
            Assert.NotEmpty(ids);
            Assert.Equal(ids.Distinct().ToList(), ids);   // 無重複
            Assert.Equal(ids.OrderBy(x => x).ToList(), ids);   // 升冪
        }
    }

    [Fact]
    public void 每列有名稱出處與raw_normalized語意齊備()
    {
        foreach (var vendor in Vendors)
        {
            foreach (var row in SmartAttributeKnowledge.Table(vendor))
            {
                Assert.False(string.IsNullOrWhiteSpace(row.Name), $"{vendor} 屬性 {row.Id} 缺名稱");
                Assert.False(string.IsNullOrWhiteSpace(row.RawSemantics), $"{vendor} 屬性 {row.Id} 缺 raw 語意");
                Assert.False(string.IsNullOrWhiteSpace(row.NormalizedSemantics), $"{vendor} 屬性 {row.Id} 缺 normalized 語意");
                Assert.False(string.IsNullOrWhiteSpace(row.Unit), $"{vendor} 屬性 {row.Id} 缺單位");
                // 出處要能稽核：含抓取日期標記
                Assert.False(string.IsNullOrWhiteSpace(row.Source), $"{vendor} 屬性 {row.Id} 缺出處");
                Assert.Contains(SmartAttributeKnowledge.FetchedDate, row.Source);
            }
        }
    }

    [Fact]
    public void 數量下限_每家至少已解碼十五列()
    {
        foreach (var vendor in Vendors)
        {
            int actual = SmartAttributeKnowledge.Count(vendor);
            Assert.True(actual >= MinRowsPerVendor,
                $"{vendor} 收錄 {actual} 列，低於下限 {MinRowsPerVendor}——查證不足，缺的如實缺，但下限是守門線");
        }
    }

    [Fact]
    public void 大小守門_單家與總量都有上限防膨脹()
    {
        int total = 0;
        foreach (var vendor in Vendors)
        {
            int actual = SmartAttributeKnowledge.Count(vendor);
            Assert.True(actual <= MaxRowsPerVendor,
                $"{vendor} 收錄 {actual} 列，超過上限 {MaxRowsPerVendor}——無出處的列不得慢慢塞進表");
            total += actual;
        }
        Assert.True(total <= MaxRowsTotal, $"六家合計 {total} 列，超過上限 {MaxRowsTotal}");
    }

    // ── 查詢契約 ─────────────────────────────────────────────────────────

    [Fact]
    public void 已知廠商已知ID命中_欄位與出處一致()
    {
        var samsung177 = SmartAttributeKnowledge.Lookup(SmartVendor.Samsung, 177);
        Assert.NotNull(samsung177);
        Assert.Contains("Wear Leveling", samsung177.Name);
        Assert.Contains("平均抹寫", samsung177.RawSemantics);
        Assert.True(samsung177.VendorSpecific);

        var seagate188 = SmartAttributeKnowledge.Lookup(SmartVendor.Seagate, 188);
        Assert.NotNull(seagate188);
        Assert.Contains("5 秒", seagate188.RawSemantics);   // Seagate 專有的逾時分欄

        var kingston194 = SmartAttributeKnowledge.Lookup(SmartVendor.Kingston, 194);
        Assert.NotNull(kingston194);
        Assert.Contains("[3-2]", kingston194.RawSemantics);   // Kingston：[3-2] 是最高溫

        var micron202 = SmartAttributeKnowledge.Lookup(SmartVendor.Micron, 202);
        Assert.NotNull(micron202);
        Assert.Contains("100−raw", micron202.NormalizedSemantics);

        var sandisk233 = SmartAttributeKnowledge.Lookup(SmartVendor.SanDisk, 233);
        Assert.NotNull(sandisk233);
        Assert.Equal("GiB", sandisk233.Unit);
    }

    [Fact]
    public void 未知ID回null不丟例外()
    {
        Assert.Null(SmartAttributeKnowledge.Lookup(SmartVendor.Samsung, 254));
        Assert.Null(SmartAttributeKnowledge.Lookup(SmartVendor.Seagate, 100));
        Assert.Null(SmartAttributeKnowledge.Lookup(SmartVendor.WesternDigital, 240));
        Assert.Null(SmartAttributeKnowledge.Lookup(SmartVendor.Kingston, 0));
        Assert.Null(SmartAttributeKnowledge.Lookup(SmartVendor.Micron, 253));   // drivedb.h 標 Unkn——不收
        Assert.Null(SmartAttributeKnowledge.Lookup(SmartVendor.SanDisk, 170));  // Unknown_Marvell_Attr——不收
    }

    [Fact]
    public void 字串廠商正規化_未知廠商回null不丟例外()
    {
        // 別名與大小寫
        Assert.NotNull(SmartAttributeKnowledge.Lookup("samsung", 177));
        Assert.NotNull(SmartAttributeKnowledge.Lookup("SANDISK", 233));
        Assert.NotNull(SmartAttributeKnowledge.Lookup("WD", 200));
        Assert.NotNull(SmartAttributeKnowledge.Lookup("WDC", 194));
        Assert.NotNull(SmartAttributeKnowledge.Lookup("Western Digital", 194));
        Assert.NotNull(SmartAttributeKnowledge.Lookup("Crucial", 202));   // Crucial＝Micron 消費品牌

        // 未知廠商與空值——「未知」，不是例外
        Assert.Null(SmartAttributeKnowledge.Lookup("Toshiba", 9));
        Assert.Null(SmartAttributeKnowledge.Lookup("", 9));
        Assert.Null(SmartAttributeKnowledge.Lookup(null, 9));
    }

    [Fact]
    public void 嘗試正規化_認得回true_認不得回false()
    {
        Assert.True(SmartAttributeKnowledge.TryNormalizeVendor(" Samsung ", out var v1));
        Assert.Equal(SmartVendor.Samsung, v1);

        Assert.True(SmartAttributeKnowledge.TryNormalizeVendor("micron", out var v2));
        Assert.Equal(SmartVendor.Micron, v2);

        Assert.False(SmartAttributeKnowledge.TryNormalizeVendor("HGST", out _));
        Assert.False(SmartAttributeKnowledge.TryNormalizeVendor(null, out _));
    }

    // ── raw／normalized 分開陳述的關鍵列（供應商編碼差異要真的寫進 raw 欄）──

    [Fact]
    public void 供應商raw編碼差異有寫進raw欄()
    {
        // Samsung 官方文件：241 raw ×512＝位元組
        var samsung241 = SmartAttributeKnowledge.Lookup(SmartVendor.Samsung, 241);
        Assert.Contains("512", samsung241!.RawSemantics);

        // Samsung 溫度類：tempminmax 字組拆解要進 raw 欄
        var samsung190 = SmartAttributeKnowledge.Lookup(SmartVendor.Samsung, 190);
        Assert.Contains("tempminmax", samsung190!.RawSemantics);

        // Micron 9：raw24(raw8)＝低三位元組小時
        var micron9 = SmartAttributeKnowledge.Lookup(SmartVendor.Micron, 9);
        Assert.Contains("0–2", micron9!.RawSemantics);

        // Seagate 1：多欄位打包——raw 不是單純錯誤計數
        var seagate1 = SmartAttributeKnowledge.Lookup(SmartVendor.Seagate, 1);
        Assert.Contains("多個欄位", seagate1!.RawSemantics);
    }

    [Fact]
    public void 誠實標記_文件未給公式的normalized不硬編()
    {
        // Samsung 官方文件只描述 raw——normalized 欄必須如實說「未給公式／不猜」
        var samsung5 = SmartAttributeKnowledge.Lookup(SmartVendor.Samsung, 5);
        Assert.Contains("文件未給", samsung5!.NormalizedSemantics);

        // WD KB 只列 ID 與分類——raw 欄必須如實說「未提供拆解」
        var wd5 = SmartAttributeKnowledge.Lookup(SmartVendor.WesternDigital, 5);
        Assert.Contains("未提供", wd5!.RawSemantics);
    }
}
