using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>
/// 靜默呼叫工具箱內建的 CPU-Z（-txt 產生報告，隱藏視窗、自動結束），
/// 解析出完整的處理器 / 主機板 / 記憶體時序 / 每條 SPD / 顯示卡深度規格。
/// 完全 best-effort：找不到 CPU-Z 或失敗時僅回報狀態字串，WMI 基本資訊仍可用（不彈窗）。
/// 報告為系統 ANSI（本機非 UTF-8），以 Latin1 讀取以保全 ASCII 欄位與 °C（0xB0）等單位符號。
/// </summary>
public static class CpuzReportService
{
    public static async Task<CpuzReport> ReadAsync()
    {
        return await Task.Run(() =>
        {
            var r = new CpuzReport();
            string? exe = LocateCpuz();
            if (exe is null)
            {
                r.Status = "找不到 CPU-Z，略過深度規格（WMI 基本資訊仍可用）";
                r.Timings.Status = r.Status;
                return r;
            }

            // 以 8.3 短檔名形式取得暫存目錄，確保路徑不含空白（CPU-Z 的 -txt= 不吃引號）
            string baseName = Path.Combine(ShortenPath(Path.GetTempPath()), $"xinspect_cpuz_{Environment.ProcessId}");
            string txt = baseName + ".txt";
            TryDelete(txt);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"-txt={baseName}",   // 不可加引號
                    UseShellExecute = true,           // WindowStyle=Hidden 需搭配 ShellExecute
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) { r.Status = "無法啟動 CPU-Z"; r.Timings.Status = r.Status; return r; }

                if (!p.WaitForExit(25000))
                {
                    try { p.Kill(true); } catch { }
                }
                for (int i = 0; i < 12 && !File.Exists(txt); i++) Thread.Sleep(300);
                if (!File.Exists(txt)) { r.Status = "CPU-Z 未產生報告"; r.Timings.Status = r.Status; return r; }

                var lines = File.ReadAllText(txt, Encoding.Latin1).Replace("\r", "").Split('\n');
                Parse(lines, r);
                r.Ran = true;
                r.Status = "已由 CPU-Z 讀取深度規格";
            }
            catch (Exception ex)
            {
                r.Status = "讀取 CPU-Z 報告失敗：" + ex.Message;
                r.Timings.Status = r.Status;
            }
            finally
            {
                TryDelete(txt);
            }
            return r;
        });
    }

    // ---- 解析 ----------------------------------------------------------------

    private static void Parse(string[] L, CpuzReport r)
    {
        int cpuStart = Header(L, "Processors Information");
        int threadDumps = Header(L, "Thread dumps", cpuStart + 1);
        int biosStart = Header(L, "BIOS", Math.Max(0, cpuStart));
        int chipStart = Header(L, "Chipset", Math.Max(0, biosStart));
        int spdStart = Header(L, "Memory SPD", Math.Max(0, chipStart));
        int spdEnd = FirstLineEquals(L, "SPD registers", spdStart + 1);   // 十六進位傾印區前緣
        int dispStart = Header(L, "Display Adapters");
        int dispEnd = Header(L, "AI Devices", Math.Max(0, dispStart));

        if (cpuStart >= 0) ParseCpu(L, cpuStart + 1, threadDumps < 0 ? L.Length : threadDumps, r.Cpu);
        ParseBoard(L, biosStart, spdStart < 0 ? L.Length : spdStart, r.Board);
        ParseTimings(L, chipStart < 0 ? 0 : chipStart, spdStart < 0 ? L.Length : spdStart, r.Timings);
        if (spdStart >= 0) ParseSpd(L, spdStart + 1, spdEnd < 0 ? (dispStart < 0 ? L.Length : dispStart) : spdEnd, r.Spd);
        if (dispStart >= 0) ParseGpus(L, dispStart + 1, dispEnd < 0 ? L.Length : dispEnd, r.Gpus);
    }

    private static void ParseCpu(string[] L, int start, int end, CpuDetail c)
    {
        string? G(string label) => Get(L, label, start, end);

        c.Codename = G("Codename") ?? "—";
        c.Specification = G("Specification") ?? "—";
        c.Package = G("Package (platform ID)") ?? "—";
        c.Technology = G("Technology") ?? "—";
        c.CpuId = G("CPUID") ?? "—";
        c.ExtCpuId = G("Extended CPUID") ?? "—";
        c.Stepping = G("Core Stepping") ?? "—";
        c.Microcode = G("Microcode Revision") ?? "—";
        c.CoreSpeed = G("Core Speed") ?? "—";
        c.Multiplier = G("Multiplier x Bus Speed") ?? "—";
        c.StockFreq = G("Stock frequency") ?? "—";
        c.TdpLimit = G("TDP Limit") ?? "—";
        c.Tjmax = G("Tjmax") ?? "—";
        c.CoreVoltage = CleanVid(G("Voltage 0"));
        c.MaxNonTurbo = G("Max non-turbo ratio") ?? "—";
        c.MaxTurbo = G("Max turbo ratio") ?? "—";
        c.MaxEfficiency = G("Max efficiency ratio") ?? "—";
        c.PowerMaxPl1 = G("Power Max (PL1)") ?? "—";
        c.Pl1Window = G("PL1 Time Window") ?? "—";
        c.PowerMaxPl2 = G("Short Power Max (PL2)") ?? "—";
        c.L1D = G("L1 Data cache") ?? "—";
        c.L1I = G("L1 Instruction cache") ?? "—";
        c.L2 = G("L2 cache") ?? "—";
        c.L3 = G("L3 cache") ?? "—";
        c.Instructions = G("Instructions sets") ?? "—";

        // Turbo 各核心倍頻表："Ratio N cores  42x"
        for (int i = start; i < end && i < L.Length; i++)
        {
            var (key, val) = Split(L[i]);
            if (key.StartsWith("Ratio ", StringComparison.Ordinal) && key.EndsWith(" cores", StringComparison.Ordinal))
            {
                string cores = key["Ratio ".Length..^" cores".Length];
                c.TurboRatios.Add($"{cores} 核　{val}");
            }
        }

        c.Loaded = c.Codename != "—" || c.Specification != "—";
    }

    private static void ParseBoard(string[] L, int start, int end, MainboardDetail b)
    {
        if (start < 0) start = 0;
        string? G(string label) => Get(L, label, start, end);

        b.Uefi = G("UEFI") ?? "—";
        b.BiosVendor = G("BIOS Vendor") ?? "—";
        b.BiosMsg = G("BIOS MSG") ?? "—";
        b.BiosDate = G("BIOS Date") ?? "—";
        b.Northbridge = G("Northbridge") ?? "—";
        b.Southbridge = G("Southbridge") ?? "—";
        b.BusSpec = G("Bus Specification") ?? "—";
        b.GraphicInterface = G("Graphic Interface") ?? "—";
        b.PcieLinkWidth = G("PCI-E Link Width") ?? "—";
        b.PcieLinkSpeed = G("PCI-E Link Speed") ?? "—";
        b.MemoryType = G("Memory Type") ?? "—";
        b.MemorySize = G("Memory Size") ?? "—";
        b.Channels = ChannelsZh(G("Channels"));

        // Monitoring / LPCIO 於報告較後段，改為全域唯一標籤搜尋
        b.Model = StripParen(Get(L, "Mainboard Model", 0, L.Length)) ?? "—";
        b.LpcioVendor = Get(L, "LPCIO Vendor", 0, L.Length) ?? "—";
        b.LpcioModel = Get(L, "LPCIO Model", 0, L.Length) ?? "—";

        b.Loaded = b.BiosVendor != "—" || b.Northbridge != "—" || b.Model != "—";
    }

    private static void ParseTimings(string[] L, int start, int end, MemoryTimings t)
    {
        string? G(string label) => Get(L, label, start, end);

        t.MemoryTypeText = G("Memory Type") ?? "—";
        t.ChannelsText = ChannelsZh(G("Channels"));
        t.MemorySizeText = G("Memory Size") ?? "—";
        t.CL = Trim0(G("CAS# latency (CL)"));
        t.TRCD = Trim0(G("RAS# to CAS# delay (tRCD)"));
        t.TRP = Trim0(G("RAS# Precharge (tRP)"));
        t.TRAS = Trim0(G("Cycle Time (tRAS)"));
        t.TRFC = Trim0(G("Row Refresh Cycle Time (tRFC)"));
        t.CommandRate = G("Command Rate (CR)") is { Length: > 0 } cr ? cr : "—";
        t.TCCD = G("tCCD") ?? "—";
        t.TCCDL = G("tCCD_L") ?? "—";
        t.TCCDWR = G("tCCD_WR") ?? "—";
        t.TCCDWRL = G("tCCD_WR_L") ?? "—";
        t.HostBridge = G("Host Bridge") ?? "—";

        var freq = G("Memory Frequency");   // 例："1800.8 MHz (1:36)"
        if (freq is not null)
        {
            var numText = new string(freq.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
            if (double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz) && mhz > 0)
            {
                t.DramFrequencyMHz = mhz;
                int rate = (int)Math.Round(mhz * 2.0 / 10.0) * 10;
                string prefix = t.MemoryTypeText.StartsWith("DDR", StringComparison.OrdinalIgnoreCase) ? t.MemoryTypeText : "DDR";
                t.DataRateText = $"{prefix}-{rate}";
            }
        }

        t.UncoreText = G("Uncore Frequency") ?? "—";
        t.Loaded = t.CL != "—";
        t.Status = t.Loaded ? "已由 CPU-Z 讀取" : (t.Status == "尚未讀取" ? "報告中找不到時序資訊" : t.Status);
    }

    private static void ParseSpd(string[] L, int start, int end, List<SpdModule> outList)
    {
        if (end < 0 || end > L.Length) end = L.Length;
        SpdModule? m = null;
        XmpProfile? xmp = null;
        int mode = 0;   // 0=none 1=jedec 2=xmp

        for (int i = start; i < end; i++)
        {
            var (key, val) = Split(L[i]);
            if (key.Length == 0) continue;

            if (key == "DIMM #")
            {
                m = new SpdModule { Slot = $"DIMM #{val}" };
                outList.Add(m);
                xmp = null; mode = 0;
                continue;
            }
            if (m is null) continue;

            switch (key)
            {
                case "Memory type": m.MemoryType = val; break;
                case "Module format": m.ModuleFormat = val; break;
                case "Module Manufacturer(ID)": m.Manufacturer = BeforeParen(val); break;
                case "Size": m.Size = val; break;
                case "Max bandwidth": m.MaxBandwidth = val; break;
                case "Max JEDEC": m.MaxJedec = val; break;
                case "Part number": m.PartNumber = val.Trim(); break;
                case "Manufacturing date": m.ManufacturingDate = val; break;
                case "Nominal Voltage": m.NominalVoltage = val; break;
                case "XMP": m.Xmp = val; break;
                case "JEDEC timings table": mode = 1; xmp = null; break;
                case "XMP profile":
                    xmp = new XmpProfile { Name = val };
                    m.XmpProfiles.Add(xmp);
                    mode = 0;
                    break;
                case "Specification": if (xmp is not null) xmp.Specification = val; break;
                case "VDD Voltage": if (xmp is not null) xmp.Voltage = val; break;
                case "Max CL": if (xmp is not null) xmp.MaxCL = Trim0(val); break;
                case "XMP timings table": mode = 2; break;
                default:
                    if (mode == 1 && key.StartsWith("JEDEC #", StringComparison.Ordinal))
                        m.Jedec.Add(new SpdTiming { Label = key, Values = val });
                    else if (mode == 2 && xmp is not null && key.StartsWith("XMP #", StringComparison.Ordinal))
                        xmp.Timings.Add(new SpdTiming { Label = key, Values = val });
                    break;
            }
        }

        // CPU-Z 在十六進位傾印區（SPD registers）之前會再印一次「DIMM #」標頭，
        // 該行落在解析範圍的最後一列，會被建成一個完全沒有欄位的空模組
        // （畫面上出現「DIMM #1　—　—」全是破折號的空卡片）→ 這裡剔除。
        outList.RemoveAll(x => x.MemoryType == "—" && x.Size == "—" && x.PartNumber == "—"
                               && x.Jedec.Count == 0 && x.XmpProfiles.Count == 0);
    }

    private static void ParseGpus(string[] L, int start, int end, List<GpuDetail> outList)
    {
        if (end < 0 || end > L.Length) end = L.Length;
        GpuDetail? g = null;
        string perf = "";

        for (int i = start; i < end; i++)
        {
            var (key, val) = Split(L[i]);
            if (key.StartsWith("Display adapter", StringComparison.Ordinal))
            {
                g = new GpuDetail { Primary = key.Contains("(primary)", StringComparison.OrdinalIgnoreCase) };
                outList.Add(g);
                perf = "";
                continue;
            }
            if (g is null) continue;

            switch (key)
            {
                case "Name": g.Name = val; break;
                case "Board Manufacturer": g.BoardManufacturer = val; break;
                case "Board Part Number": g.BoardPartNumber = val; break;
                case "Revision": g.Revision = val; break;
                case "Codename": g.Codename = val; break;
                case "Core family": g.CoreFamily = val; break;
                case "Technology": g.Technology = val; break;
                case "Cores": g.Cores = val; break;
                case "ROP Units": g.RopUnits = val; break;
                case "TM Units": g.TmUnits = val; break;
                case "Memory type": g.MemoryType = val; break;
                case "Memory size": g.MemorySize = val; break;
                case "Memory bus width": g.MemoryBusWidth = val; break;
                case "Vendor ID": g.VendorId = val; break;
                case "Model ID": g.ModelId = val; break;
                case "Revision ID": g.RevisionId = val; break;
                case "Power Limit": g.PowerLimit = val; break;
                case "Thermal Limit": g.ThermalLimit = val; break;
                case "Driver version": g.DriverVersion = val; break;
                case "WDDM Model": g.Wddm = val; break;
                case "Performance Level": perf = val; break;
                case "Core clock":
                    if (perf == "Base") g.BaseCoreClock = val;
                    else if (perf == "Boost") g.BoostCoreClock = val;
                    break;
                case "Memory clock":
                    if (perf == "Base") g.BaseMemClock = val;
                    else if (perf == "Boost") g.BoostMemClock = val;
                    break;
            }
        }
    }

    // ---- 解析輔助 ------------------------------------------------------------

    /// <summary>拆出「鍵&lt;TAB&gt;值」。前導 Tab（子項縮排）先去除，值再去除前後 Tab/空白。</summary>
    private static (string key, string val) Split(string raw)
    {
        var line = raw.TrimStart('\t', ' ');
        int tab = line.IndexOf('\t');
        if (tab <= 0) return ("", "");
        return (line[..tab].Trim(), line[(tab + 1)..].Trim(' ', '\t'));
    }

    private static string? Get(string[] L, string label, int start, int end)
    {
        if (start < 0) start = 0;
        if (end < 0 || end > L.Length) end = L.Length;
        for (int i = start; i < end; i++)
        {
            var (key, val) = Split(L[i]);
            if (key == label) return val.Length > 0 ? val : null;
        }
        return null;
    }

    /// <summary>找出「整行等於（去空白後）」指定字串的第一行索引；找不到回傳 -1。</summary>
    private static int Header(string[] L, string header, int from = 0)
    {
        for (int i = Math.Max(0, from); i < L.Length; i++)
            if (L[i].Trim() == header) return i;
        return -1;
    }

    private static int FirstLineEquals(string[] L, string text, int from)
    {
        for (int i = Math.Max(0, from); i < L.Length; i++)
            if (L[i].Trim() == text) return i;
        return -1;
    }

    private static string ChannelsZh(string? v) => v?.Trim() switch
    {
        "Single" => "單通道", "Dual" => "雙通道", "Triple" => "三通道",
        "Quad" => "四通道", "Hexa" => "六通道", "Octal" => "八通道",
        null or "" => "—", var other => other!
    };

    private static string Trim0(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "—";
        v = v.Trim();
        return v.EndsWith(".0", StringComparison.Ordinal) ? v[..^2] : v;
    }

    /// <summary>"SK Hynix (AD00…)" → "SK Hynix"。</summary>
    private static string BeforeParen(string v)
    {
        int i = v.IndexOf(" (", StringComparison.Ordinal);
        return (i > 0 ? v[..i] : v).Trim();
    }

    /// <summary>"ROG … OMEGA (0x… - 0x…)" → "ROG … OMEGA"。</summary>
    private static string? StripParen(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        int i = v.IndexOf(" (", StringComparison.Ordinal);
        return (i > 0 ? v[..i] : v).Trim();
    }

    /// <summary>"1.08 Volts (VID)" → "1.08 Volts (VID)"（原樣，僅去尾空白）；空值回 —。</summary>
    private static string CleanVid(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();

    // ---- CPU-Z 定位與啟動輔助（沿用原機制） ----------------------------------

    private static string? LocateCpuz()
    {
        var candidates = new List<string>();
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        candidates.Add(Path.Combine(desktop, "图吧工具箱", "处理器工具", "CPUZ", "cpuz_x64.exe"));
        candidates.Add(@"C:\Users\Administrator\Desktop\图吧工具箱\处理器工具\CPUZ\cpuz_x64.exe");

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        try
        {
            var found = Directory.EnumerateFiles(desktop, "cpuz*.exe", SearchOption.AllDirectories).ToList();
            var x64 = found.FirstOrDefault(f => f.Contains("x64", StringComparison.OrdinalIgnoreCase));
            return x64 ?? found.FirstOrDefault();
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string ShortenPath(string path)
    {
        try
        {
            var sb = new StringBuilder(300);
            uint len = GetShortPathName(path, sb, (uint)sb.Capacity);
            if (len > 0 && len < sb.Capacity) return sb.ToString();
        }
        catch { }
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);
}
