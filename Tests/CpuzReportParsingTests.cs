using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// CPU-Z 報告解析：以合成的報告行陣列（鍵＜TAB＞值、區段標頭獨立成行）驗證
/// 處理器／主機板／記憶體時序／每條 SPD／顯示卡五個區塊的欄位還原與範圍切割。
/// </summary>
public class CpuzReportParsingTests
{
    /// <summary>一行「＜TAB＞鍵＜TAB＞值」。CPU-Z 以縮排表示層級，解析器會去除前導 Tab。</summary>
    private static string KV(string key, string value) => "\t" + key + "\t" + value;

    /// <summary>一行只有鍵、值為空（例：Display adapter 0 標頭列）。</summary>
    private static string K(string key) => "\t" + key + "\t";

    private static string[] SampleReport() =>
    [
        "Processors Information",
        KV("Codename", "Raptor Lake"),
        KV("Specification", "13th Gen Intel(R) Core(TM) i7-13700K"),
        KV("Package (platform ID)", "Socket 1700 LGA (0x4)"),
        KV("Technology", "10 nm"),
        KV("CPUID", "B.7.1"),
        KV("Extended CPUID", "6.B7"),
        KV("Core Stepping", "B0"),
        KV("Microcode Revision", "0x11D"),
        KV("Core Speed", "5400.0 MHz"),
        KV("Multiplier x Bus Speed", "54.0 x 100.0 MHz"),
        KV("Stock frequency", "3400 MHz"),
        KV("TDP Limit", "125.0 Watts"),
        KV("Tjmax", "100.0 °C"),
        KV("Voltage 0", "1.325 Volts (VID)"),
        KV("Max non-turbo ratio", "34x"),
        KV("Max turbo ratio", "54x"),
        KV("Max efficiency ratio", "8x"),
        KV("Power Max (PL1)", "125.000 W"),
        KV("PL1 Time Window", "28 s"),
        KV("Short Power Max (PL2)", "253.000 W"),
        KV("Ratio 1 cores", "54x"),
        KV("Ratio 2 cores", "54x"),
        KV("Ratio 8 cores", "53x"),
        KV("L1 Data cache", "8 x 48 KBytes, 12-way"),
        KV("L1 Instruction cache", "8 x 32 KBytes, 8-way"),
        KV("L2 cache", "8 x 2048 KBytes, 16-way"),
        KV("L3 cache", "30 MBytes, 12-way"),
        KV("Instructions sets", "MMX, SSE, SSE2, AVX2, AVX-VNNI"),
        "Thread dumps",
        KV("CPU Thread 0", "APIC ID 0"),
        "BIOS",
        KV("UEFI", "Yes"),
        KV("BIOS Vendor", "American Megatrends Inc."),
        KV("BIOS MSG", "2801"),
        KV("BIOS Date", "08/28/2026"),
        "Chipset",
        KV("Northbridge", "Intel Raptor Lake rev. 01"),
        KV("Southbridge", "Intel Z790 rev. 11"),
        KV("Bus Specification", "PCI-Express 5.0 (16.0 GT/s)"),
        KV("Graphic Interface", "PCI-Express"),
        KV("PCI-E Link Width", "x16"),
        KV("PCI-E Link Speed", "16.0 GT/s"),
        KV("Memory Type", "DDR5"),
        KV("Memory Size", "32 GBytes"),
        KV("Channels", "Dual"),
        KV("Memory Frequency", "3000.1 MHz (1:30)"),
        KV("CAS# latency (CL)", "30.0"),
        KV("RAS# to CAS# delay (tRCD)", "38.0"),
        KV("RAS# Precharge (tRP)", "38.0"),
        KV("Cycle Time (tRAS)", "76.0"),
        KV("Row Refresh Cycle Time (tRFC)", "884.0"),
        KV("Command Rate (CR)", "2T"),
        KV("Host Bridge", "0x A700"),
        KV("Uncore Frequency", "4200.0 MHz"),
        KV("Mainboard Model", "ROG STRIX Z790-A GAMING (0x000 - 0x000)"),
        KV("LPCIO Vendor", "Nuvoton"),
        KV("LPCIO Model", "NCT6798D"),
        "Memory SPD",
        KV("DIMM #", "1"),
        KV("Memory type", "DDR5"),
        KV("Module format", "UDIMM"),
        KV("Module Manufacturer(ID)", "G.Skill (0x0000009D)"),
        KV("Size", "16 GBytes"),
        KV("Max bandwidth", "DDR5-4800 (2400 MHz)"),
        KV("Part number", "F5-6000J3038F16G "),
        KV("Manufacturing date", "Year 2024, Week 12"),
        KV("Nominal Voltage", "1.10 Volts"),
        KV("XMP", "XMP 3.0 supported"),
        KV("JEDEC timings table", "CL-tRCD-tRP-tRAS-tRC-tRFC @ frequency"),
        KV("JEDEC #1", "28-34-34-64-98-n.a @ 1800 MHz"),
        KV("JEDEC #2", "30-36-36-70-106-n.a @ 2000 MHz"),
        KV("XMP profile", "XMP-6000"),
        KV("Specification", "DDR5-6000"),
        KV("VDD Voltage", "1.350 Volts"),
        KV("Max CL", "30.0"),
        KV("XMP timings table", "CL-tRCD-tRP-tRAS-tRC-tRFC @ frequency"),
        KV("XMP #1", "30-38-38-76-114-n.a @ 3000 MHz"),
        KV("DIMM #", "2"),
        KV("Memory type", "DDR5"),
        KV("Module Manufacturer(ID)", "G.Skill (0x0000009D)"),
        KV("Size", "16 GBytes"),
        KV("Part number", "F5-6000J3038F16G"),
        KV("JEDEC timings table", "CL-tRCD-tRP-tRAS-tRC-tRFC @ frequency"),
        KV("JEDEC #1", "28-34-34-64-98-n.a @ 1800 MHz"),
        // 十六進位傾印區前 CPU-Z 會再印一次 DIMM 標頭 → 不得成為第三張空卡片
        KV("DIMM #", "3"),
        "SPD registers",
        KV("DIMM #", "1"),
        KV("00", "23 11 12 0E 86 00 00 08"),
        "Display Adapters",
        K("Display adapter 0 (primary)"),
        KV("Name", "NVIDIA GeForce RTX 4070"),
        KV("Board Manufacturer", "ASUSTeK Computer Inc."),
        KV("Codename", "AD104"),
        KV("Core family", "Ada Lovelace"),
        KV("Technology", "5 nm"),
        KV("Cores", "5888"),
        KV("ROP Units", "80"),
        KV("TM Units", "184"),
        KV("Memory type", "GDDR6X"),
        KV("Memory size", "12 GBytes"),
        KV("Memory bus width", "192 bits"),
        KV("Vendor ID", "0x10DE"),
        KV("Model ID", "0x2786"),
        KV("Power Limit", "200.0 W"),
        KV("Thermal Limit", "88 °C"),
        KV("Driver version", "580.88"),
        KV("WDDM Model", "3.2"),
        KV("Performance Level", "Base"),
        KV("Core clock", "1920.0 MHz"),
        KV("Memory clock", "1313.0 MHz"),
        KV("Performance Level", "Boost"),
        KV("Core clock", "2475.0 MHz"),
        KV("Memory clock", "1313.0 MHz"),
        K("Display adapter 1"),
        KV("Name", "Intel(R) UHD Graphics 770"),
        KV("Memory size", "128 MBytes"),
        "AI Devices",
        // AI 區段在 Display Adapters 之後，其 Name 不得覆寫最後一張顯示卡
        KV("Name", "Intel AI Boost NPU"),
    ];

    private static CpuzReport ParseSample()
    {
        var r = new CpuzReport();
        CpuzReportService.Parse(SampleReport(), r);
        return r;
    }

    // ── 處理器 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Cpu_ReadsCoreFields()
    {
        var c = ParseSample().Cpu;
        Assert.True(c.Loaded);
        Assert.Equal("Raptor Lake", c.Codename);
        Assert.Equal("Socket 1700 LGA (0x4)", c.Package);
        Assert.Equal("10 nm", c.Technology);
        Assert.Equal("125.0 Watts", c.TdpLimit);
        Assert.Equal("1.325 Volts (VID)", c.CoreVoltage);
        Assert.Equal("30 MBytes, 12-way", c.L3);
    }

    [Fact]
    public void Cpu_CollectsTurboRatioTable()
    {
        var c = ParseSample().Cpu;
        Assert.True(c.HasTurboRatios);
        Assert.Equal(3, c.TurboRatios.Count);
        Assert.Contains("8 核　53x", c.TurboRatios);
    }

    [Fact]
    public void Cpu_DoesNotReadPastThreadDumps()
    {
        // Specification 於 SPD 的 XMP 區段亦出現，若範圍切錯會被覆寫為 "DDR5-6000"
        Assert.StartsWith("13th Gen Intel", ParseSample().Cpu.Specification);
    }

    // ── 主機板 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Board_ReadsBiosAndChipset()
    {
        var b = ParseSample().Board;
        Assert.True(b.Loaded);
        Assert.Equal("American Megatrends Inc.", b.BiosVendor);
        Assert.Equal("Intel Z790 rev. 11", b.Southbridge);
        Assert.Equal("x16", b.PcieLinkWidth);
        Assert.Equal("NCT6798D", b.LpcioModel);
    }

    [Fact]
    public void Board_StripsHexSuffixFromModel()
        => Assert.Equal("ROG STRIX Z790-A GAMING", ParseSample().Board.Model);

    [Fact]
    public void Board_LocalizesChannelCount()
        => Assert.Equal("雙通道", ParseSample().Board.Channels);

    // ── 記憶體時序 ────────────────────────────────────────────────────────────

    [Fact]
    public void Timings_ReadsPrimaryTimings()
    {
        var t = ParseSample().Timings;
        Assert.True(t.Loaded);
        Assert.Equal("已由 CPU-Z 讀取", t.Status);
        Assert.Equal("30", t.CL);        // "30.0" 的尾綴 .0 應被去除
        Assert.Equal("38", t.TRCD);
        Assert.Equal("884", t.TRFC);
        Assert.Equal("2T", t.CommandRate);
    }

    [Fact]
    public void Timings_DerivesDataRateFromFrequency()
    {
        var t = ParseSample().Timings;
        Assert.Equal(3000.1, t.DramFrequencyMHz, 3);
        Assert.Equal("DDR5-6000", t.DataRateText);   // 3000.1 MHz × 2 → 取整至十位
    }

    [Fact]
    public void Timings_ReportMissingTimings_KeepsHonestStatus()
    {
        var r = new CpuzReport();
        CpuzReportService.Parse(["Processors Information", KV("Codename", "Zen 5")], r);
        Assert.False(r.Timings.Loaded);
        Assert.Equal("報告中找不到時序資訊", r.Timings.Status);
    }

    // ── SPD ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Spd_ParsesEachModule_AndDropsTrailingEmptyHeader()
    {
        var spd = ParseSample().Spd;
        Assert.Equal(2, spd.Count);                  // 傾印區前多出的 DIMM #3 空模組須被剔除
        Assert.Equal("DIMM #1", spd[0].Slot);
        Assert.Equal("DIMM #2", spd[1].Slot);
    }

    [Fact]
    public void Spd_ReadsManufacturerWithoutIdCode()
        => Assert.Equal("G.Skill", ParseSample().Spd[0].Manufacturer);

    [Fact]
    public void Spd_TrimsPartNumber()
        => Assert.Equal("F5-6000J3038F16G", ParseSample().Spd[0].PartNumber);

    [Fact]
    public void Spd_SeparatesJedecAndXmpTables()
    {
        var m = ParseSample().Spd[0];
        Assert.True(m.HasJedec);
        Assert.Equal(2, m.Jedec.Count);
        Assert.Equal("JEDEC #1", m.Jedec[0].Label);

        Assert.True(m.HasXmp);
        var xmp = Assert.Single(m.XmpProfiles);
        Assert.Equal("XMP-6000", xmp.Name);
        Assert.Equal("DDR5-6000", xmp.Specification);
        Assert.Equal("1.350 Volts", xmp.Voltage);
        Assert.Equal("30", xmp.MaxCL);
        Assert.True(xmp.HasTimings);
        Assert.Equal("XMP #1", Assert.Single(xmp.Timings).Label);
        Assert.Equal("XMP-6000", m.XmpSummary);
    }

    [Fact]
    public void Spd_ModuleWithoutXmp_ReportsNone()
    {
        var m = ParseSample().Spd[1];
        Assert.False(m.HasXmp);
        Assert.Equal("無", m.XmpSummary);
        Assert.Single(m.Jedec);
    }

    // ── 顯示卡 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gpus_ParseBothAdapters_AndMarkPrimary()
    {
        var g = ParseSample().Gpus;
        Assert.Equal(2, g.Count);
        Assert.True(g[0].Primary);
        Assert.False(g[1].Primary);
        Assert.Equal("NVIDIA GeForce RTX 4070（主要）", g[0].Title);
        Assert.Equal("Intel(R) UHD Graphics 770", g[1].Name);
    }

    [Fact]
    public void Gpus_SplitBaseAndBoostClocksByPerformanceLevel()
    {
        var g = ParseSample().Gpus[0];
        Assert.Equal("1920.0 MHz", g.BaseCoreClock);
        Assert.Equal("2475.0 MHz", g.BoostCoreClock);
        Assert.Equal("1313.0 MHz", g.BaseMemClock);
        Assert.Equal("1313.0 MHz", g.BoostMemClock);
    }

    [Fact]
    public void Gpus_ReadDeepSpecFields()
    {
        var g = ParseSample().Gpus[0];
        Assert.Equal("AD104", g.Codename);
        Assert.Equal("5888", g.Cores);
        Assert.Equal("192 bits", g.MemoryBusWidth);
        Assert.Equal("0x2786", g.ModelId);
        Assert.Equal("580.88", g.DriverVersion);
    }

    [Fact]
    public void Gpus_StopAtAiDevicesSection()
    {
        // AI Devices 內的 Name 若被吃進來，最後一張顯示卡會變成 NPU
        var g = ParseSample().Gpus[^1];
        Assert.DoesNotContain("NPU", g.Name);
    }

    // ── 韌性 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyReport_DoesNotThrow_AndReportsNothingLoaded()
    {
        var r = new CpuzReport();
        CpuzReportService.Parse([], r);
        Assert.False(r.Cpu.Loaded);
        Assert.False(r.Board.Loaded);
        Assert.False(r.Timings.Loaded);
        Assert.Empty(r.Spd);
        Assert.Empty(r.Gpus);
    }

    [Fact]
    public void ReportWithoutSectionHeaders_DoesNotThrow()
    {
        var r = new CpuzReport();
        CpuzReportService.Parse(["隨機文字", "", "\t", KV("Codename", "Raptor Lake")], r);
        Assert.False(r.Cpu.Loaded);   // 無 Processors Information 標頭 → 不解析處理器
    }
}
