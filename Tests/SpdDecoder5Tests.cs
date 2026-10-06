using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// DDR5 SPD 純解碼器（JESD400-5）的契約：欄位編碼規則的金標向量、CRC-16/XMODEM 的已知
/// 檢查值、以及「不合法就不解」的三態。位移與編碼對照過開源參照（spdr 的 JESD400-5
/// 解碼器＋Linux 核心 spd5118.c），2026-10-07 抓取。
/// <b>全部標「未在本機驗證」</b>：本機沒有 DDR5 硬體，向量是從編碼規則手算的合成資料。
/// </summary>
public class SpdDecoder5Tests
{
    /// <summary>造一份合成的 DDR5 UDIMM 映像：16Gb×1 die×1 rank/通道×2 子通道 x32、x8 IC、16GB 模組。</summary>
    private static byte[] Ddr5Image()
    {
        var img = new byte[SpdReader.Ddr5Size];

        void Set(int off, byte v) => img[off] = v;
        void SetLe16(int off, ushort v) { img[off] = (byte)v; img[off + 1] = (byte)(v >> 8); }

        Set(0, 0x30);                     // bits[6:4]＝3 → SPD 1024 bytes
        Set(1, 0x12);                     // SPD revision 1.2（純 nibble）
        Set(2, SpdReader.Ddr5TypeCode);   // key type 0x12
        Set(3, 0x02);                     // UDIMM、非 hybrid
        Set(4, 0x04);                     // bits[4:0]＝4（16Gb/die）、bits[7:5]＝0（Monolithic 1 die）
        Set(5, 0x21);                     // rows＝16+1=17、cols＝10+1=11
        Set(6, 0x20);                     // bits[7:5]＝1 → x8
        Set(7, 0x20);                     // bank groups＝2、banks/group＝1（bits[2:0]＝0）
        img[20] = 0xA0; img[21] = 0x01;   // 0x01A0 = 416 ps（DDR5-4800）
        SetLe16(22, 833);                 // tCKavg max 833 ps（DDR5-2400 上限）
        // CAS latencies bytes 24-28：支援 CL 40 與 42 → bit = (40-20)/2 = 10、(42-20)/2 = 11
        Set(24, 0x00);
        Set(25, 0x0C);                    // bits 10、11 → CL 40、42（遮罩是全域 40-bit）
        Set(26, 0x00); Set(27, 0x00); Set(28, 0x00);
        SetLe16(30, 16667);               // tAA 16667 ps ≈ CL40 @ 4800
        SetLe16(32, 16667);               // tRCD
        SetLe16(34, 16667);               // tRP
        SetLe16(36, 32000);               // tRAS 32000 ps
        SetLe16(38, 48667);               // tRC 48667 ps
        SetLe16(40, 15000);               // tWR 15000 ps
        SetLe16(42, 295);                 // tRFC1 295 ns
        SetLe16(44, 235);                 // tRFC2 235 ns
        SetLe16(46, 192);                 // tRFCsb 192 ns
        // bank-group 類三元組 [ps u16][nCK u8]
        SetLe16(70, 4000); Set(72, 4);    // tRRD_L
        SetLe16(73, 4000); Set(75, 4);    // tCCD_L
        Set(234, 0x00);                   // bits[5:3]＝0 → 每通道 1 rank（基 1）
        Set(235, 0x22);                   // bits[2:0]＝2 → 32-bit 子通道、bits[7:5]＝1 → 每模組 2 子通道
        // 製造區
        Set(512, 0x80); Set(513, 0xAD);   // bank 1、代碼 0x2D（含奇同位位元 → 0xAD）→ SK Hynix
        Set(514, 0x01);                   // location
        Set(515, 0x25); Set(516, 0x18);   // 2025 年第 18 週（BCD）
        img[517] = 0xDE; img[518] = 0xAD; img[519] = 0xBE; img[520] = 0xEF;
        byte[] pn = "HMAA1GX6MCR6N-XN"u8.ToArray();
        Array.Copy(pn, 0, img, 521, pn.Length);
        Set(551, 0x12);                   // module revision
        Set(552, 0x80); Set(553, 0xAD);   // DRAM mfr 同 SK Hynix
        Set(554, 0x00);                   // stepping
        return img;
    }

    [Fact]
    public void CRC16是XMODEM_已知檢查值釘死()
    {
        // CRC-16/XMODEM 的公開檢查值："123456789" → 0x31C3
        Assert.Equal(0x31C3, SpdDecoder5.Crc16("123456789"u8));
    }

    [Fact]
    public void 身分區_模組型別密度組織金標()
    {
        var img = Ddr5Image();
        var s = SpdDecoder5.Decode(img)!;

        Assert.True(s.SpdSizeBytes == 1024, $"raw[0]=0x{img[0]:X2} len={img.Length} decoded={s.SpdSizeBytes}");
        Assert.Equal("1.2", s.SpdRevision);
        Assert.Equal("UDIMM", s.ModuleType);
        Assert.False(s.Hybrid);
        Assert.Equal(16, s.DensityGigabitsPerDie);   // byte 4 bits[4:0]＝4
        Assert.Equal("Monolithic", s.Package);
        Assert.Equal(1, s.DieCount);                 // byte 4 bits[7:5]＝0
        Assert.Equal(17, s.RowAddressBits);          // 16+1
        Assert.Equal(11, s.ColumnAddressBits);       // 10+1
        Assert.Equal(8, s.IoWidth);                  // x8
        Assert.Equal(2, s.BankGroups);
        Assert.Equal(1, s.BanksPerBankGroup);
        Assert.Equal(1, s.RanksPerChannel);          // byte 234 bits[5:3]＝1 → 基 1
        Assert.False(s.RankMixAsymmetric);
        Assert.Equal(2, s.ChannelsPerDimm);          // byte 235 bits[7:5]＝1 → 2 子通道
        Assert.Equal(32, s.PrimaryBusWidthBits);     // byte 235 bits[2:0]＝2 → 32-bit 子通道
    }

    [Fact]
    public void 時序區_皮秒直讀與tRFC奈秒與CAS遮罩金標()
    {
        var s = SpdDecoder5.Decode(Ddr5Image())!;

        Assert.Equal(416, s.TckAvgMinPs);
        Assert.Equal(833, s.TckAvgMaxPs);
        Assert.Equal(4800, s.BaseDataRateMtS);       // 2000000/416 = 4807 → 圓整 4800
        Assert.Equal([40, 42], s.CasLatencies);      // 40-bit 遮罩 bits 10、11 → CL 20+2i
        Assert.Equal(16667, s.TaaPs);
        Assert.Equal(32000, s.TrasPs);
        Assert.Equal(295, s.Trfc1Ns);                // tRFC 族是奈秒
        Assert.Equal(235, s.Trfc2Ns);
        Assert.Equal(192, s.TrfcSbNs);
        Assert.Equal(new Ddr5TimingPair(4000, 4), s.TRrdL);
        Assert.Equal(new Ddr5TimingPair(4000, 4), s.TCcdL);
    }

    [Fact]
    public void 製造區_廠商日期序號料號金標()
    {
        var s = SpdDecoder5.Decode(Ddr5Image())!;

        Assert.Equal("SK Hynix", s.ModuleManufacturer.Name);
        Assert.True(s.ModuleManufacturer.ParityOk);
        Assert.Equal("SK Hynix", s.DramManufacturer.Name);
        Assert.Equal(2025, s.ManufactureYear);
        Assert.Equal(18, s.ManufactureWeek);
        Assert.Equal("DEADBEEF", s.SerialHex);
        Assert.Equal("HMAA1GX6MCR6N-XN", s.PartNumber);
    }

    [Fact]
    public void 容量推算_16Gb乘1die乘1rank乘2子通道乘32除8金標()
    {
        var s = SpdDecoder5.Decode(Ddr5Image())!;
        // 16Gb × 1 die × 1 rank × 2 子通道 × (32/8) = 128 Gb = 16384 MiB（16GB 模組）
        Assert.Equal(16384, s.CapacityMib);
    }

    [Fact]
    public void CRC寫對時Valid_寫錯時Invalid()
    {
        var img = Ddr5Image();
        ushort crc = SpdDecoder5.Crc16(img.AsSpan(0, 510));
        img[510] = (byte)crc; img[511] = (byte)(crc >> 8);
        Assert.True(SpdDecoder5.Decode(img)!.BaseCrc.Valid);

        img[300] ^= 0xFF;                            // 改內容不重算 CRC
        Assert.False(SpdDecoder5.Decode(img)!.BaseCrc.Valid);
    }

    [Fact]
    public void 不是DDR5或長度不足就回null()
    {
        var img = Ddr5Image();
        img[2] = 0x0C;                               // DDR4 的 key type
        Assert.Null(SpdDecoder5.Decode(img));

        Assert.Null(SpdDecoder5.Decode(new byte[100]));
    }

    [Fact]
    public void BCD不合法的日期兩邊都回null()
    {
        var img = Ddr5Image();
        img[515] = 0x2F;                             // 年的低位 nibble 非 BCD
        var s = SpdDecoder5.Decode(img)!;
        Assert.Null(s.ManufactureYear);
        Assert.Null(s.ManufactureWeek);              // 週本身合法，但半個日期不是日期——跟 DDR4 同規則
    }
}
