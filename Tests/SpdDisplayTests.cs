using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 直讀的 SPD → 記憶體頁顯示模型。
/// </summary>
/// <remarks>
/// 記憶體頁原本只有一條資料來源：解析 CPU-Z 報告的文字，也就是使用者得先裝 CPU-Z 並跑過一次。
/// 這一組驗的是同一張卡片改由自己讀到的位元組填滿之後，每一格的字串是什麼。
/// <para>
/// 有一件刻意不做的事：<b>不重現 CPU-Z 那張 13 列的 JEDEC 降頻時序表</b>。那 13 列是 CPU-Z
/// 自己的推導，而它的頻率欄算不出同樣的值（CL10 那列它寫 733 MHz，同一組原始值推是 727 MHz）。
/// 要對上就得猜它的取整規則，而這個專案的規矩是不猜。所以這裡給的是模組裡真正存著的奈秒值，
/// 加上一列換算到最高標準頻率的時鐘週期——那一列與 CPU-Z 的 JEDEC #12 逐格相同。
/// </para>
/// </remarks>
public class SpdDisplayTests
{
    private static SpdModule Display(int dimm, string bus = "測試匯流排")
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"spd-ddr4-real-dimm{dimm}.bin"));
        var read = new SpdDirectRead(bus, 0x50, raw, SpdDecoder.Decode(raw)!);
        return Assert.Single(SpdDisplay.ToDisplay([read]));
    }

    [Fact]
    public void 基本欄位照CPUZ的寫法填()
    {
        var m = Display(1);

        Assert.Equal("DIMM #1（0x50）", m.Slot);
        Assert.Equal("DDR4", m.MemoryType);
        Assert.Equal("UDIMM", m.ModuleFormat);              // CPU-Z：Module format UDIMM
        Assert.Equal("8192 MBytes", m.Size);                // CPU-Z：Size 8192 MBytes
        Assert.Equal("SK Hynix", m.Manufacturer);
        Assert.Equal("SK Hynix", m.DramManufacturer);
        Assert.Equal("ZhuQue_8G_Y", m.PartNumber);
        Assert.Equal("DDR4-4000 (2000 MHz)", m.MaxBandwidth);   // CPU-Z：Max bandwidth DDR4-4000 (2000 MHz)
        Assert.Equal("DDR4-3200 (1600 MHz)", m.MaxJedec);       // CPU-Z：Max JEDEC DDR4-3200 (1600 MHz)
        Assert.Equal("Week 51/Year 24", m.ManufacturingDate);   // CPU-Z：Manufacturing date Week 51/Year 24
        Assert.Equal("1.200 Volts", m.NominalVoltage);          // CPU-Z：Nominal Voltage 1.20 Volts
        Assert.Equal("yes, rev. 2.0", m.Xmp);                   // CPU-Z：XMP yes, rev. 2.0
    }

    /// <summary>每一筆事實都要說得出自己的血統——同一張卡片可能來自兩種可信度完全不同的來源。</summary>
    [Fact]
    public void 來源要寫明是直讀的哪一條匯流排()
    {
        Assert.Equal("直讀 SPD ・ 處理器 iMC SMBus（16:1E.5 第 0 組）",
            Display(1, "處理器 iMC SMBus（16:1E.5 第 0 組）").Source);

        // 沒填就是預設值，代表那一筆是解析 CPU-Z 報告來的
        Assert.Equal("CPU-Z 報告", new SpdModule().Source);
    }

    [Fact]
    public void 校驗結果兩段都要報()
    {
        Assert.Equal("基本段 OK ・ 模組段 OK", Display(1).Checksum);
    }

    /// <summary>被改寫過而沒重算校驗的那顆，要把存的與算的都擺出來——那是直接證據。</summary>
    [Fact]
    public void 校驗不符時要說出存的與算的各是多少()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-self-read-tampered.bin"));
        var m = Assert.Single(SpdDisplay.ToDisplay([new SpdDirectRead("匯流排", 0x52, raw, SpdDecoder.Decode(raw)!)]));

        Assert.Equal("基本段不符（存 0x242D／算 0x6A67） ・ 模組段 OK", m.Checksum);
    }

    [Fact]
    public void SPD沒燒製造日期時要說是沒燒_而不是留一個橫線讓人以為讀不到()
    {
        Assert.Equal("—（SPD 裡沒有燒製造日期）", Display(2).ManufacturingDate);
    }

    /// <summary>第一列是換算到最高標準頻率的時鐘週期，與 CPU-Z 的 JEDEC #12「22.0-22-22-52-74 @ 1600 MHz」相同。</summary>
    [Fact]
    public void JEDEC第一列與CPUZ那一列相同()
    {
        var jedec = Display(1).Jedec;

        Assert.Equal("CL-tRCD-tRP-tRAS-tRC @ DDR4-3200 (1600 MHz)", jedec[0].Label);
        Assert.Equal("22-22-22-52-74", jedec[0].Values);
    }

    [Fact]
    public void 奈秒原始值逐項列出()
    {
        var rows = Display(1).Jedec.ToDictionary(r => r.Label, r => r.Values);

        Assert.Equal("0.625 ns", rows["最小週期 tCK"]);
        Assert.Equal("13.75 ns", rows["tAA（CL）"]);
        Assert.Equal("32.00 ns", rows["tRAS"]);
        Assert.Equal("45.75 ns", rows["tRC"]);
        Assert.Equal("2.50 ns ／ 4.90 ns", rows["tRRD_S ／ tRRD_L"]);
        Assert.Equal("350.00 ns ／ 260.00 ns ／ 160.00 ns", rows["tRFC1 ／ tRFC2 ／ tRFC4"]);
        Assert.Equal("10、11、12、13、14、15、16、17、18、19、20、22、24", rows["支援的 CL"]);
    }

    /// <summary>
    /// CPU-Z 對第一條列出 XMP-3602（1.400 V、Max CL 18.0）與 XMP-4000（1.400 V、Max CL 20.0），
    /// 而 XMP #1 那一列是「18.0-23-23-58-81 @ 1801 MHz」。
    /// </summary>
    [Fact]
    public void 兩組XMP的標題與時鐘週期都對上CPUZ()
    {
        var xmp = Display(1).XmpProfiles;
        Assert.Equal(2, xmp.Count);

        Assert.Equal("XMP-3602", xmp[0].Name);
        Assert.Equal("DDR4-3602 (1801 MHz)", xmp[0].Specification);
        Assert.Equal("1.400 Volts", xmp[0].Voltage);
        Assert.Equal("18.0", xmp[0].MaxCL);
        Assert.Equal("18-23-23-58-81", xmp[0].Timings[0].Values);
        Assert.Equal("0.555 ns", xmp[0].Timings[1].Values);

        Assert.Equal("XMP-4000", xmp[1].Name);
        Assert.Equal("1.400 Volts", xmp[1].Voltage);
        Assert.Equal("20.0", xmp[1].MaxCL);
    }

    /// <summary>
    /// HEDT 平台有兩組匯流排，兩條不同的模組都會是 0x50。光靠位址分不出來——
    /// 卡片標題會變成一模一樣的兩張，使用者無法知道哪一張對應哪一條。
    /// </summary>
    [Fact]
    public void 兩條不同匯流排上的同位址模組要分得出來()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-real-dimm1.bin"));
        var s = SpdDecoder.Decode(raw)!;
        var list = SpdDisplay.ToDisplay([
            new SpdDirectRead("第 0 組", 0x50, raw, s),
            new SpdDirectRead("第 1 組", 0x50, raw, s),
        ]);

        Assert.Equal("DIMM #1（0x50）", list[0].Slot);
        Assert.Equal("DIMM #2（0x50）", list[1].Slot);
        Assert.NotEqual(list[0].Header, list[1].Header);
        Assert.Equal("直讀 SPD ・ 第 1 組", list[1].Source);
    }

    [Fact]
    public void 摘要與標題沿用既有規則()
    {
        var m = Display(1);

        Assert.Equal("XMP-3602 ・ XMP-4000", m.XmpSummary);
        Assert.Contains("ZhuQue_8G_Y", m.Header);
        Assert.True(m.HasJedec);
        Assert.True(m.HasXmp);
    }
}
