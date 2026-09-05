using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SPD 的幾何、時序與 XMP 逐組解碼。
/// </summary>
/// <remarks>
/// <para>
/// 這一組把記憶體頁對 CPU-Z 的依賴拿掉。目前那一頁的 SPD 深度資料是
/// <c>ViewModels/StartupSequence.cs</c> 呼叫 <c>CpuzReportService.ReadAsync()</c> 解析報告文字來的
/// ——使用者得先裝 CPU-Z 並跑過一次。這些欄位全都在我們自己讀得到的 512 位元組裡。
/// </para>
/// <para>
/// 每一條斷言旁邊都註明對應的 CPU-Z 讀值。那份報告與基準檔是同一批位元組的<b>獨立解讀</b>，
/// 所以位移或編碼寫錯會立刻對不上——這是這個專案能拿到的最強驗證。
/// </para>
/// </remarks>
public class SpdDetailTests
{
    private static SpdSnapshot Decode(int dimm)
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"spd-ddr4-real-dimm{dimm}.bin"));
        var s = SpdDecoder.Decode(raw);
        Assert.NotNull(s);
        return s!;
    }

    // ---- 幾何：容量是算出來的，不是抄來的 ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void 三條都是8GB的單rank無ECC_UDIMM(int dimm)
    {
        var g = Decode(dimm).Geometry;

        Assert.Equal(SpdModuleType.Udimm, g.ModuleType);       // CPU-Z：Module format UDIMM
        Assert.Equal(8192, g.CapacityMib);                     // CPU-Z：Size 8192 MBytes
        Assert.Equal(8192, g.SdramDensityMbits);               // 位元組 4 低四位＝5 → 8 Gb 顆粒
        Assert.Equal(1, g.Ranks);
        Assert.Equal(8, g.DeviceWidthBits);
        Assert.Equal(64, g.BusWidthBits);
        Assert.Equal(0, g.EccBits);
        Assert.Equal(1200, g.NominalMillivolts);               // CPU-Z：Nominal Voltage 1.20 Volts
    }

    // ---- JEDEC 時序：以皮秒存，測試才能做精確比較 ----

    [Fact]
    public void 第一條的JEDEC時序原始值()
    {
        var t = Decode(1).Timings;

        Assert.Equal(625, t.TckMinPs);                         // 5 MTB × 125 ps → 1600 MHz
        Assert.Equal(3200, t.MaxJedecDataRate);                // CPU-Z：Max JEDEC DDR4-3200
        Assert.Equal(13750, t.TaaPs);
        Assert.Equal(13750, t.TrcdPs);
        Assert.Equal(13750, t.TrpPs);
        Assert.Equal(32000, t.TrasPs);
        Assert.Equal(45750, t.TrcPs);
        Assert.Equal(2500, t.TrrdSPs);
        Assert.Equal(4900, t.TrrdLPs);                         // 40 MTB － 100 ps 細調
        Assert.Equal(5000, t.TccdLPs);
        Assert.Equal(15000, t.TwrPs);
        Assert.Equal(350000, t.Trfc1Ps);
        Assert.Equal(21000, t.TfawPs);
    }

    /// <summary>
    /// CPU-Z 的 JEDEC 時序表第 12 列是「22.0-22-22-52-74 @ 1600 MHz」。同一組原始值換算到
    /// 1600 MHz 必須得到同一列——這是把「解出來的皮秒」與「別人算出來的時鐘週期」對帳。
    /// </summary>
    [Fact]
    public void 換算到1600MHz要對上CPUZ那一列()
    {
        var t = Decode(1).Timings;

        Assert.Equal(22, SpdTimings.ClocksAt(t.TaaPs, 625));
        Assert.Equal(22, SpdTimings.ClocksAt(t.TrcdPs, 625));
        Assert.Equal(22, SpdTimings.ClocksAt(t.TrpPs, 625));
        Assert.Equal(52, SpdTimings.ClocksAt(t.TrasPs, 625));
        Assert.Equal(74, SpdTimings.ClocksAt(t.TrcPs, 625));
    }

    /// <summary>
    /// 支援的 CAS 延遲是位元圖（位元組 20–23，從 CL7 起算）。CPU-Z 對第一條列出 13 列
    /// JEDEC 時序，對第二條列出的最高標準頻率是 DDR4-2133——兩邊都要對得上。
    /// </summary>
    [Fact]
    public void 支援的CAS延遲位元圖()
    {
        Assert.Equal([10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 22, 24], Decode(1).Timings.SupportedCas);
        Assert.Equal([10, 11, 12, 13, 14, 15, 16], Decode(2).Timings.SupportedCas);
    }

    [Fact]
    public void 第二條的最高標準頻率比較低()
    {
        var t = Decode(2).Timings;

        Assert.Equal(938, t.TckMinPs);                         // 8 MTB － 62 ps 細調
        Assert.Equal(2133, t.MaxJedecDataRate);                // CPU-Z：Max JEDEC DDR4-2133
        Assert.Equal(33000, t.TrasPs);
        Assert.Equal(46750, t.TrcPs);
    }

    // ---- XMP 逐組 profile ----

    /// <summary>
    /// CPU-Z 對第一條列出兩組：XMP-3602（1.400 V、0.555 ns／1801 MHz、Max CL 18、
    /// tRP／tRCD 12.50、tRAS 32.00、tRC 44.50、tRRD 3.13）與 XMP-4000（1.400 V、0.500 ns／2000 MHz、
    /// Max CL 20、tRP／tRCD 13.75、tRAS 32.00、tRC 47.00、tRRD 8.75）。每一格都要對上。
    /// </summary>
    [Fact]
    public void 第一條的兩組XMP要逐格對上CPUZ()
    {
        var xmp = Decode(1).XmpProfiles;
        Assert.Equal(2, xmp.Count);

        var p1 = xmp[0];
        Assert.Equal(1400, p1.Millivolts);
        Assert.Equal(555, p1.TckMinPs);
        Assert.Equal(3602, p1.DataRate);                   // CPU-Z 的標題就是 XMP-3602
        Assert.Equal(18, p1.CasLatency);                   // Max CL 18.0
        Assert.Equal(12500, p1.TrcdPs);
        Assert.Equal(12500, p1.TrpPs);
        Assert.Equal(32000, p1.TrasPs);
        Assert.Equal(44500, p1.TrcPs);
        Assert.Equal(3125, p1.TrrdSPs);                    // CPU-Z 顯示 3.13 ns

        var p2 = xmp[1];
        Assert.Equal(1400, p2.Millivolts);
        Assert.Equal(500, p2.TckMinPs);
        Assert.Equal(4000, p2.DataRate);                   // XMP-4000
        Assert.Equal(20, p2.CasLatency);
        Assert.Equal(13750, p2.TrcdPs);
        Assert.Equal(13750, p2.TrpPs);
        Assert.Equal(32000, p2.TrasPs);
        Assert.Equal(47000, p2.TrcPs);
        Assert.Equal(8750, p2.TrrdSPs);                    // CPU-Z 顯示 8.75 ns
    }

    /// <summary>
    /// CPU-Z 的報告沒把第二條的 XMP 內容列出來，所以這一組沒有獨立來源可對——但解出來的東西
    /// 必須和料號自己說的一致：<c>ZJ-4000-C18</c> ＝ DDR4-4000、CL18。這是另一種形式的對帳。
    /// </summary>
    [Fact]
    public void 第二條的XMP要和料號自己說的一致()
    {
        var p = Assert.Single(Decode(2).XmpProfiles);

        Assert.Equal(500, p.TckMinPs);
        Assert.Equal(4000, p.DataRate);                     // 料號：ZJ-4000
        Assert.Equal(18, p.CasLatency);                     // 料號：C18
        Assert.Equal(1450, p.Millivolts);                   // DDR4-4000 C18 套件的典型電壓
    }

    /// <summary>
    /// 換算到 profile 自己的 tCK，要得到 CPU-Z 那一列「18.0-23-23-58-81 @ 1801 MHz」。
    /// </summary>
    [Fact]
    public void XMP換算到自己的頻率要對上CPUZ那一列()
    {
        var p = Decode(1).XmpProfiles[0];

        Assert.Equal(18, p.CasLatency);
        Assert.Equal(23, SpdTimings.ClocksAt(p.TrcdPs, p.TckMinPs));
        Assert.Equal(23, SpdTimings.ClocksAt(p.TrpPs, p.TckMinPs));
        Assert.Equal(58, SpdTimings.ClocksAt(p.TrasPs, p.TckMinPs));
        Assert.Equal(81, SpdTimings.ClocksAt(p.TrcPs, p.TckMinPs));
    }

    [Fact]
    public void 沒有XMP時是空清單而不是null()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-real-dimm1.bin"));
        raw[384] = 0x00;

        Assert.Empty(SpdDecoder.Decode(raw)!.XmpProfiles);
    }

    [Fact]
    public void 未知的模組型別要照實回報而不是猜UDIMM()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-real-dimm1.bin"));
        raw[3] = 0x0F;

        Assert.Equal(SpdModuleType.Unknown, SpdDecoder.Decode(raw)!.Geometry.ModuleType);
    }

    /// <summary>
    /// 容量是由顆粒密度、顆粒寬度、rank 數與匯流排寬度算出來的。任一格是保留值就算不出來，
    /// 這時要回 0 讓上層說「讀不到」，不能拿一個看起來合理的數字充數。
    /// </summary>
    [Fact]
    public void 顆粒密度是保留值時算不出容量()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-real-dimm1.bin"));
        raw[4] = 0x8F;                                      // 低四位 0xF＝保留

        Assert.Equal(0, SpdDecoder.Decode(raw)!.Geometry.CapacityMib);
    }
}
