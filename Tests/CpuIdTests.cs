using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>CPUID 解碼純函式：餘 synthetic 暫存器值驗證解讀，不執行真實 CPUID。</summary>
public class CpuIdTests
{
    [Fact]
    public void 品牌字串去除尾端空白與NUL()
    {
        var raw = new byte[48];
        System.Text.Encoding.ASCII.GetBytes("Genuine Intel(R) CPU").CopyTo(raw, 0);
        Assert.Equal("Genuine Intel(R) CPU", CpuIdService.Decoder.DecodeBrand(raw));
    }

    [Fact]
    public void 品牌字串_全空白回傳破折號()
    {
        Assert.Equal("—", CpuIdService.Decoder.DecodeBrand(new byte[48]));
    }

    [Fact]
    public void 快取子葉解碼_典型L1資料快取()
    {
        // type=1（資料）level=1 共用2核；8 路、1 分割、64B 行、64 集合 → 32 KB
        uint eax = 1 | (1u << 5) | (1u << 14);
        uint ebx = (7u << 22) | 63u;
        var row = CpuIdService.Decoder.DecodeCacheSubleaf(eax, ebx, 63, 0);
        Assert.NotNull(row);
        Assert.Equal("L1 資料", row.Level);
        Assert.Equal("32 KB", row.Capacity);
        Assert.Equal("8 路", row.Ways);
        Assert.Equal("64 B", row.Line);
        Assert.Equal("64", row.Sets);
        Assert.Equal("2 核共用", row.Shared);
        Assert.Equal("—", row.Inclusive);
    }

    [Fact]
    public void 快取子葉_type為0表示列舉終止()
    {
        Assert.Null(CpuIdService.Decoder.DecodeCacheSubleaf(0, 0, 0, 0));
    }

    [Fact]
    public void 位址位元_實體36虛擬48()
    {
        Assert.Equal("36 / 48 bits", CpuIdService.Decoder.DecodeAddressBits(36u | (48u << 8)));
    }

    [Fact]
    public void 拓樸子葉_核心層級十八個()
    {
        var row = CpuIdService.Decoder.DecodeTopologySubleaf(5, 18, 2u << 8, 1);
        Assert.NotNull(row);
        Assert.Equal("核心", row.Level);
        Assert.Equal("18 個", row.Count);
        Assert.Equal("右移 5 bits", row.Shift);
    }

    [Fact]
    public void 拓樸子葉_計數為0表示終止()
    {
        Assert.Null(CpuIdService.Decoder.DecodeTopologySubleaf(0, 0, 0, 2));
    }

    [Fact]
    public void 混合架構類型解碼()
    {
        Assert.Equal("—", CpuIdService.Decoder.DecodeHybrid(0));
        Assert.Contains("E-core", CpuIdService.Decoder.DecodeHybrid(0x20 | (0x5Cu << 8)));
        Assert.Contains("P-core", CpuIdService.Decoder.DecodeHybrid(0x40 | (0x97u << 8)));
    }

    [Fact]
    public void 標稱頻率解碼_leaf16()
    {
        Assert.Equal("基準 2200 MHz ・ 最大 4400 MHz ・ 匯流排 100 MHz",
            CpuIdService.Decoder.DecodeFreq16(2200, 4400, 100));
        Assert.Equal("—", CpuIdService.Decoder.DecodeFreq16(0, 0, 0));
    }

    [Fact]
    public void 標稱頻率解碼_leaf15由晶振與比值推得()
    {
        // 24 MHz 晶振 × 175/2 = 2.1 GHz
        var s = CpuIdService.Decoder.DecodeFreq15(2, 175, 24_000_000);
        Assert.Contains("GHz", s);
    }

    [Fact]
    public void 功能位元表_四元組與名稱皆不重複()
    {
        var keys = CpuIdService.Decoder.FeatureTable.Select(f => (f.Leaf, f.SubLeaf, f.Reg, f.Bit)).ToHashSet();
        Assert.Equal(CpuIdService.Decoder.FeatureTable.Length, keys.Count);

        var names = CpuIdService.Decoder.FeatureTable.Select(f => f.Name).ToHashSet();
        Assert.Equal(CpuIdService.Decoder.FeatureTable.Length, names.Count);
    }
}
