using System.Globalization;

namespace XinSpect;

/// <summary>
/// 把直讀回來的 SPD 轉成記憶體頁在用的顯示模型。
/// </summary>
/// <remarks>
/// <para>
/// 記憶體頁原本只有一條資料來源：解析 CPU-Z 報告的文字（<c>CpuzReportService</c>），
/// 也就是使用者得先裝 CPU-Z 並跑過一次。這一層讓同一張卡片可以由自己讀到的位元組填滿，
/// 並在「來源」那一列說清楚是哪一條。
/// </para>
/// <para>
/// <b>不重現 CPU-Z 那張 13 列的 JEDEC 降頻時序表。</b>那 13 列是 CPU-Z 自己的推導（每個支援的
/// CL 配一個頻率），而它的頻率欄算不出同樣的值——例如 CL10 那列它寫 733 MHz，從 tAA 13.75 ns
/// 推是 727 MHz；CL20 那列它寫 1466 MHz 而同一個取整規則會得到 CL21。要對上就得猜它的規則。
/// </para>
/// <para>
/// 所以這裡給的是<b>模組裡真正存著的東西</b>：各項時序的奈秒原始值，加上一列換算到這條模組
/// 自己最高標準頻率的時鐘週期（那一列與 CPU-Z 的 JEDEC #12 逐格相同，是驗證過的），
/// 以及每一組 XMP 在它自己頻率下的時鐘週期。奈秒值是事實，時鐘週期是隨頻率而變的推導。
/// </para>
/// </remarks>
public static class SpdDisplay
{
    public static List<SpdModule> ToDisplay(IEnumerable<SpdDirectRead> reads)
        => reads.Select((r, i) => One(r, i + 1)).ToList();

    private static SpdModule One(SpdDirectRead r, int number)
    {
        var s = r.Decoded;
        var t = s.Timings;
        var top = s.XmpProfiles.Count > 0 ? s.XmpProfiles.Max(p => p.DataRate) : t.MaxJedecDataRate;

        var m = new SpdModule
        {
            // 位址本身不唯一：HEDT 平台有兩組匯流排，兩條不同的模組都會是 0x50。
            // 所以編號在前（沿用 CPU-Z 的「DIMM #n」寫法），完整血統放在「來源」那一列。
            Slot = $"DIMM #{number}（0x{r.Address:X2}）",
            Source = "直讀 SPD ・ " + r.Bus,
            MemoryType = "DDR4",
            ModuleFormat = ModuleFormat(s.Geometry.ModuleType),
            Size = s.Geometry.CapacityMib > 0 ? $"{s.Geometry.CapacityMib} MBytes" : "—",
            Manufacturer = s.ModuleManufacturer.Name,
            DramManufacturer = s.DramManufacturer.Name,
            PartNumber = s.PartNumber.Length > 0 ? s.PartNumber : "—",
            MaxBandwidth = Rate(top),
            MaxJedec = Rate(t.MaxJedecDataRate),
            ManufacturingDate = s.ManufactureYear is null
                ? "—（SPD 裡沒有燒製造日期）"
                : $"Week {s.ManufactureWeek}/Year {s.ManufactureYear % 100:00}",
            NominalVoltage = s.Geometry.NominalMillivolts > 0
                ? Volts(s.Geometry.NominalMillivolts)
                : "—",
            Xmp = s.Xmp is null ? "no" : $"yes, rev. {s.Xmp.Revision}",
            Checksum = Checksum(s),
        };

        AddJedecRows(m.Jedec, t);
        foreach (var p in s.XmpProfiles) m.XmpProfiles.Add(Profile(p));
        return m;
    }

    private static string ModuleFormat(SpdModuleType type) => type switch
    {
        SpdModuleType.Rdimm => "RDIMM",
        SpdModuleType.Udimm => "UDIMM",
        SpdModuleType.SoDimm => "SO-DIMM",
        SpdModuleType.LrDimm => "LRDIMM",
        SpdModuleType.MiniRdimm => "Mini-RDIMM",
        SpdModuleType.MiniUdimm => "Mini-UDIMM",
        SpdModuleType.SoRdimm72Bit => "72b-SO-RDIMM",
        SpdModuleType.SoUdimm72Bit => "72b-SO-UDIMM",
        SpdModuleType.SoDimm16Bit => "16b-SO-DIMM",
        SpdModuleType.SoDimm32Bit => "32b-SO-DIMM",
        _ => "—",
    };

    /// <summary>「基本段 OK ・ 模組段 OK」，或指出存的與算的各是多少——那是「被改寫過」的直接證據。</summary>
    private static string Checksum(SpdSnapshot s)
    {
        string One(string name, SpdCrc c) => c.Valid
            ? $"{name} OK"
            : $"{name}不符（存 0x{c.Stored:X4}／算 0x{c.Computed:X4}）";
        return One("基本段", s.BaseCrc) + " ・ " + One("模組段", s.ModuleCrc);
    }

    private static string Rate(int dataRate)
        => dataRate > 0 ? $"DDR4-{dataRate} ({dataRate / 2} MHz)" : "—";

    private static string Volts(int millivolts)
        => (millivolts / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " Volts";

    /// <summary>皮秒 → 奈秒字串。tCK 用三位小數（CPU-Z 也是），其餘兩位。</summary>
    private static string Ns(int ps, int decimals = 2)
        => (ps / 1000.0).ToString("0." + new string('0', decimals), CultureInfo.InvariantCulture) + " ns";

    private static string Clocks(int tckPs, params int[] timesPs)
        => string.Join("-", timesPs.Select(x => SpdTimings.ClocksAt(x, tckPs)));

    private static void AddJedecRows(List<SpdTiming> rows, SpdTimings t)
    {
        void Row(string label, string values) => rows.Add(new SpdTiming { Label = label, Values = values });

        if (t.TckMinPs > 0)
            Row($"CL-tRCD-tRP-tRAS-tRC @ {Rate(t.MaxJedecDataRate)}",
                Clocks(t.TckMinPs, t.TaaPs, t.TrcdPs, t.TrpPs, t.TrasPs, t.TrcPs));

        Row("最小週期 tCK", Ns(t.TckMinPs, 3));
        Row("tAA（CL）", Ns(t.TaaPs));
        Row("tRCD", Ns(t.TrcdPs));
        Row("tRP", Ns(t.TrpPs));
        Row("tRAS", Ns(t.TrasPs));
        Row("tRC", Ns(t.TrcPs));
        Row("tRRD_S ／ tRRD_L", Ns(t.TrrdSPs) + " ／ " + Ns(t.TrrdLPs));
        Row("tCCD_L", Ns(t.TccdLPs));
        Row("tWR", Ns(t.TwrPs));
        Row("tWTR_S ／ tWTR_L", Ns(t.TwtrSPs) + " ／ " + Ns(t.TwtrLPs));
        Row("tRFC1 ／ tRFC2 ／ tRFC4", Ns(t.Trfc1Ps) + " ／ " + Ns(t.Trfc2Ps) + " ／ " + Ns(t.Trfc4Ps));
        Row("tFAW", Ns(t.TfawPs));
        if (t.SupportedCas.Count > 0) Row("支援的 CL", string.Join("、", t.SupportedCas));
    }

    private static XmpProfile Profile(SpdXmpProfile p)
    {
        var x = new XmpProfile
        {
            Name = $"XMP-{p.DataRate}",
            Specification = Rate(p.DataRate),
            Voltage = Volts(p.Millivolts),
            MaxCL = p.CasLatency.ToString("0.0", CultureInfo.InvariantCulture),
        };
        x.Timings.Add(new SpdTiming
        {
            Label = "CL-tRCD-tRP-tRAS-tRC",
            Values = Clocks(p.TckMinPs, p.TaaPs, p.TrcdPs, p.TrpPs, p.TrasPs, p.TrcPs),
        });
        x.Timings.Add(new SpdTiming { Label = "最小週期 tCK", Values = Ns(p.TckMinPs, 3) });
        x.Timings.Add(new SpdTiming { Label = "tRCD ／ tRP", Values = Ns(p.TrcdPs) + " ／ " + Ns(p.TrpPs) });
        x.Timings.Add(new SpdTiming { Label = "tRAS ／ tRC", Values = Ns(p.TrasPs) + " ／ " + Ns(p.TrcPs) });
        x.Timings.Add(new SpdTiming { Label = "tRRD_S", Values = Ns(p.TrrdSPs) });
        return x;
    }
}
