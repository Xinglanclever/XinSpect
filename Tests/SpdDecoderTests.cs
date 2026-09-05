using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// DDR4 SPD 的純函式解碼器。
/// </summary>
/// <remarks>
/// <para>
/// 基準檔是<b>這台機器上三條模組的真實 512 位元組</b>（見 <c>Fixtures/README.md</c> 的來源與遮蔽規則）。
/// 更難得的是同一份來源裡還有 CPU-Z 對同一串位元組的獨立解讀，所以下面每一條斷言都有第二個
/// 來源可以對帳——這比自己傾印再自己驗更強：位移錯了會立刻和 CPU-Z 對不上。
/// </para>
/// <para>
/// 這正是 1.9.1-B1 的教訓。那次 NVMe 位移錯誤帶了三個發佈版，因為測試資料是照著錯誤的實作
/// 位移合成出來的：合成資料只證明程式跟自己一致，不證明它跟規格一致。
/// </para>
/// </remarks>
public class SpdDecoderTests
{
    private static byte[] Fixture(int dimm)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"spd-ddr4-real-dimm{dimm}.bin"));

    private static SpdSnapshot Decode(int dimm)
    {
        var s = SpdDecoder.Decode(Fixture(dimm));
        Assert.NotNull(s);
        return s!;
    }

    // ---- 真實位元組：與 CPU-Z 的獨立解讀對帳 ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void 三條模組的兩段CRC都通過(int dimm)
    {
        var s = Decode(dimm);

        Assert.True(s.BaseCrc.Valid, $"基本段 CRC：存 0x{s.BaseCrc.Stored:X4}／算 0x{s.BaseCrc.Computed:X4}");
        Assert.True(s.ModuleCrc.Valid, $"模組段 CRC：存 0x{s.ModuleCrc.Stored:X4}／算 0x{s.ModuleCrc.Computed:X4}");
        Assert.Equal(126, s.BaseCrc.SpanBytes);          // 位元組 0 的 bit 7 為 0＝涵蓋 0–125
    }

    [Fact]
    public void 第一條模組的每一格都要對得上CPUZ的解讀()
    {
        var s = Decode(1);

        Assert.Equal(SpdReader.Ddr4TypeCode, s.TypeCode);
        Assert.Equal(0x8F73, s.BaseCrc.Stored);
        Assert.Equal("ZhuQue_8G_Y", s.PartNumber);          // CPU-Z：ZhuQue_8G_Y
        Assert.Equal(2024, s.ManufactureYear);              // CPU-Z：Year 24
        Assert.Equal(51, s.ManufactureWeek);                // CPU-Z：Week 51
        Assert.Equal(0x43, s.DramStepping);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void 模組廠與顆粒廠都解成SKHynix(int dimm)
    {
        var s = Decode(dimm);

        // CPU-Z 對這三條的兩個欄位都報 SK Hynix (AD…)
        Assert.Equal(0x80AD, s.ModuleManufacturer.Raw);
        Assert.Equal(1, s.ModuleManufacturer.Bank);
        Assert.Equal(0x2D, s.ModuleManufacturer.Code);
        Assert.Equal("SK Hynix", s.ModuleManufacturer.Name);
        Assert.Equal("SK Hynix", s.DramManufacturer.Name);
        Assert.True(s.ModuleManufacturer.ParityOk);
        Assert.True(s.DramManufacturer.ParityOk);
    }

    /// <summary>
    /// 這一條是整個功能的示範案例。SMBIOS 對這條模組回報的序號是全 0，而問題一直是
    /// 「模組的 SPD 裡沒燒序號，還是 BIOS 沒把它抄過來？」——直讀 SPD 給出了定論：
    /// <b>SPD 自己的序號與製造日期都是空的。</b>不是 BIOS 的問題。
    /// </summary>
    [Fact]
    public void 第二條模組的製造日期是空的_要回null而不是2000年第0週()
    {
        var s = Decode(2);

        Assert.Equal("ZJ-4000-C18-8G-RWMC", s.PartNumber);
        Assert.Null(s.ManufactureYear);
        Assert.Null(s.ManufactureWeek);
        Assert.Equal(0x00, s.DramStepping);
    }

    [Fact]
    public void 序號欄位讀的是325到328()
    {
        // 基準檔入庫前把序號遮成同長度的 'XXXX'（見 Fixtures/README.md）；
        // 斷言遮蔽後的值即可證明位移正確。實機原值記錄在該 README 裡。
        Assert.Equal("58585858", Decode(1).SerialHex);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void XMP標頭與啟用的組數(int dimm, int profiles)
    {
        var xmp = Decode(dimm).Xmp;

        Assert.NotNull(xmp);
        Assert.Equal("2.0", xmp!.Revision);                 // CPU-Z：XMP yes, rev. 2.0
        Assert.Equal(profiles, xmp.ProfileCount);           // CPU-Z 對第一條列出兩組（3602／4000）
    }

    // ---- 邊界情況：這些才用合成資料 ----

    [Fact]
    public void CRC被動過就要抓出來_並且把存的與算的都說出來()
    {
        var raw = Fixture(1);
        raw[10] ^= 0xFF;                                    // 改一格內容但不重算 CRC
        var s = SpdDecoder.Decode(raw)!;

        Assert.False(s.BaseCrc.Valid);
        Assert.Equal(0x8F73, s.BaseCrc.Stored);             // 存的還是原來那個
        Assert.NotEqual(s.BaseCrc.Stored, s.BaseCrc.Computed);
        Assert.True(s.ModuleCrc.Valid);                     // 另一段不受影響
    }

    [Fact]
    public void 位元組0的bit7決定CRC涵蓋範圍()
    {
        var raw = Fixture(1);
        raw[0] |= 0x80;                                     // 宣告涵蓋 0–116
        var s = SpdDecoder.Decode(raw)!;

        Assert.Equal(117, s.BaseCrc.SpanBytes);
        Assert.False(s.BaseCrc.Valid);                      // 原本的 CRC 是照 126 位元組算的
    }

    [Theory]
    [InlineData((byte)0x00, (byte)0x00)]                    // 完全沒燒
    [InlineData((byte)0x24, (byte)0x00)]                    // 有年沒週
    [InlineData((byte)0x1A, (byte)0x51)]                    // 年不是合法 BCD
    [InlineData((byte)0x24, (byte)0x99)]                    // 第 99 週不存在
    [InlineData((byte)0xFF, (byte)0xFF)]                    // 讀到全 F
    public void 製造日期不合法時一律回null(byte year, byte week)
    {
        var raw = Fixture(1);
        raw[323] = year;
        raw[324] = week;
        var s = SpdDecoder.Decode(raw)!;

        Assert.Null(s.ManufactureYear);
        Assert.Null(s.ManufactureWeek);
    }

    [Fact]
    public void 料號含不可列印字元時視為沒有料號()
    {
        var raw = Fixture(1);
        raw[329] = 0x00;
        raw[330] = 0x1F;

        Assert.Equal("", SpdDecoder.Decode(raw)!.PartNumber);
    }

    [Fact]
    public void 未收錄的廠商代碼要報出bank與代碼_不得亂猜名字()
    {
        var raw = Fixture(1);
        raw[320] = 0x82;                                    // 兩個 continuation＝bank 3
        raw[321] = 0x7B;                                    // 代碼 0x7B（未收錄）
        var m = SpdDecoder.Decode(raw)!.ModuleManufacturer;

        Assert.Equal(3, m.Bank);
        Assert.Equal(0x7B, m.Code);
        Assert.Contains("未知", m.Name);
        Assert.Contains("0x7B", m.Name);
    }

    [Fact]
    public void 廠商代碼的奇同位不對時要標出來()
    {
        var raw = Fixture(1);
        raw[321] = 0x2D;                                    // SK Hynix 的代碼但同位位元沒設
        var m = SpdDecoder.Decode(raw)!.ModuleManufacturer;

        Assert.False(m.ParityOk);
        Assert.Equal("SK Hynix", m.Name);                   // 代碼仍認得，只是欄位沒照規格寫
    }

    [Fact]
    public void 沒有XMP魔術數時回null()
    {
        var raw = Fixture(1);
        raw[384] = 0x00;

        Assert.Null(SpdDecoder.Decode(raw)!.Xmp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(511)]
    public void 位元組數不足512時不解讀(int length)
        => Assert.Null(SpdDecoder.Decode(new byte[length]));

    [Fact]
    public void 不是DDR4就不解讀()
    {
        var raw = Fixture(1);
        raw[2] = 0x12;                                      // DDR5

        Assert.Null(SpdDecoder.Decode(raw));
    }

    /// <summary>CRC16 的實作用真實位元組驗過（三條模組的兩段共六個值全部相符），這裡釘住演算法本身。</summary>
    [Fact]
    public void CRC16是XMODEM的那一種()
    {
        Assert.Equal(0x31C3, SpdDecoder.Crc16("123456789"u8));
        Assert.Equal(0x0000, SpdDecoder.Crc16([]));
    }
}
