using System.Management;

namespace XinSpect;

/// <summary>透過 WMI 讀取一次性的靜態硬體 / 系統資訊。</summary>
public static class SystemInfoService
{
    public static SystemSummary GetSystemSummary()
    {
        var s = new SystemSummary
        {
            HostName = SafeEnv(Environment.MachineName),
            UserName = SafeEnv($"{Environment.UserDomainName}\\{Environment.UserName}"),
            OsArch = Environment.Is64BitOperatingSystem ? "64 位元" : "32 位元",
        };

        foreach (var o in Query("SELECT Caption, Version, BuildNumber, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
        {
            s.OsName = Str(o, "Caption");
            s.OsVersion = $"{Str(o, "Version")}（組建 {Str(o, "BuildNumber")}）";
            s.InstallDate = Date(o, "InstallDate");
            var boot = DateOrNull(o, "LastBootUpTime");
            if (boot is DateTime bt)
            {
                s.BootTime = bt.ToString("yyyy-MM-dd HH:mm:ss");
                s.Uptime = FormatUptime(DateTime.Now - bt);
            }
            break;
        }

        foreach (var o in Query("SELECT Manufacturer, Model, SystemType, NumberOfLogicalProcessors FROM Win32_ComputerSystem"))
        {
            s.SystemManufacturer = Str(o, "Manufacturer");
            s.SystemModel = Str(o, "Model");
            s.SystemType = Str(o, "SystemType");
            s.LogicalProcessors = (int)Num(o, "NumberOfLogicalProcessors");
            break;
        }

        foreach (var o in Query("SELECT IdentifyingNumber, UUID, Name FROM Win32_ComputerSystemProduct"))
        {
            s.SystemSku = Str(o, "IdentifyingNumber");
            s.SystemUuid = Str(o, "UUID");
            break;
        }

        foreach (var o in Query("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard"))
        {
            s.BoardVendor = Str(o, "Manufacturer");
            s.BoardModel = Str(o, "Product");
            s.BoardVersion = Str(o, "Version");
            s.BoardSerial = Str(o, "SerialNumber");
            break;
        }

        foreach (var o in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            s.BiosVendor = Str(o, "Manufacturer");
            s.BiosVersion = Str(o, "SMBIOSBIOSVersion");
            s.BiosDate = Date(o, "ReleaseDate");
            break;
        }

        return s;
    }

    public static CpuStatic GetCpu()
    {
        var c = new CpuStatic();
        foreach (var o in Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, " +
                                "NumberOfEnabledCore, MaxClockSpeed, ExtClock, CurrentVoltage, AddressWidth, DataWidth, " +
                                "SocketDesignation, L2CacheSize, L3CacheSize, ProcessorId, Description, Version, Revision, " +
                                "VirtualizationFirmwareEnabled, SecondLevelAddressTranslationExtensions " +
                                "FROM Win32_Processor"))
        {
            c.Name = Str(o, "Name");
            c.Manufacturer = Str(o, "Manufacturer");
            c.Cores = (int)Num(o, "NumberOfCores");
            c.Threads = (int)Num(o, "NumberOfLogicalProcessors");
            c.EnabledCores = (int)Num(o, "NumberOfEnabledCore");
            c.MaxClockMHz = Num(o, "MaxClockSpeed");
            c.ExtClockMHz = Num(o, "ExtClock");
            double mv = Num(o, "CurrentVoltage");
            // 依 WMI/SMBIOS 規範：僅當位元 7 設定時，低 7 位才是「電壓 × 10」的有效值；
            // 位元 7 未設定時電壓改由 VoltageCaps 表示，CurrentVoltage 本身不含有效電壓——
            // 此時回 0（下游顯示為「—」），不臆造數字（多數現代 CPU 此值本就不可靠）。
            c.CurrentVoltage = (mv > 0 && ((int)mv & 0x80) != 0) ? ((int)mv & 0x7F) / 10.0 : 0;
            c.AddressWidth = (int)Num(o, "AddressWidth");
            c.DataWidth = (int)Num(o, "DataWidth");
            c.Socket = Str(o, "SocketDesignation");
            c.L2Cache = KB(Num(o, "L2CacheSize"));
            c.L3Cache = KB(Num(o, "L3CacheSize"));
            c.ProcessorId = Str(o, "ProcessorId");
            c.Description = Str(o, "Description");
            c.Version = Str(o, "Version");
            var rev = Num(o, "Revision");
            c.Revision = rev > 0 ? ((int)rev).ToString() : "—";
            bool vt = false; try { vt = o["VirtualizationFirmwareEnabled"] is bool b && b; } catch { }
            c.Virtualization = vt ? "已啟用" : "停用或未支援";
            bool slat = false; try { slat = o["SecondLevelAddressTranslationExtensions"] is bool s && s; } catch { }
            c.Slat = slat ? "支援（EPT / NPT）" : "不支援";
            ParseFms(c.Description, c);
            break; // 只取第一顆（一般家用/工作站單插槽）
        }
        return c;
    }

    /// <summary>由 WMI Description（例："Intel64 Family 6 Model 85 Stepping 4"）解析 CPUID 家族/型號/步進。</summary>
    private static void ParseFms(string desc, CpuStatic c)
    {
        if (string.IsNullOrWhiteSpace(desc)) return;
        var m = System.Text.RegularExpressions.Regex.Match(desc,
            @"Family\s+(\d+)\s+Model\s+(\d+)\s+Stepping\s+(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return;
        c.Family = m.Groups[1].Value;
        c.ModelNo = m.Groups[2].Value;
        c.SteppingId = m.Groups[3].Value;
    }

    public static List<MemoryModuleInfo> GetMemoryModules()
    {
        var list = new List<MemoryModuleInfo>();
        foreach (var o in Query("SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, Capacity, " +
                                "Speed, ConfiguredClockSpeed, FormFactor, SMBIOSMemoryType, ConfiguredVoltage " +
                                "FROM Win32_PhysicalMemory"))
        {
            var m = new MemoryModuleInfo
            {
                Slot = $"{Str(o, "BankLabel")} / {Str(o, "DeviceLocator")}".Trim(' ', '/'),
                Manufacturer = Str(o, "Manufacturer"),
                PartNumber = Str(o, "PartNumber"),
                CapacityGB = Num(o, "Capacity") / 1073741824.0,
                RatedSpeedMHz = (int)Num(o, "Speed"),
                ConfiguredSpeedMHz = (int)Num(o, "ConfiguredClockSpeed"),
                FormFactor = FormFactor((int)Num(o, "FormFactor")),
                MemoryType = MemType((int)Num(o, "SMBIOSMemoryType")),
            };
            double mv = Num(o, "ConfiguredVoltage");
            m.Voltage = mv > 0 ? $"{mv / 1000.0:0.###} V" : "—";
            list.Add(m);
        }
        return list;
    }

    /// <summary>偵測音效裝置（WMI Win32_SoundDevice）。回傳裝置名稱清單。</summary>
    public static List<string> GetSoundDevices()
    {
        var list = new List<string>();
        foreach (var o in Query("SELECT Name, Manufacturer, Status FROM Win32_SoundDevice"))
        {
            string name = Str(o, "Name");
            if (name == "—") continue;
            if (!list.Contains(name)) list.Add(name);
        }
        return list;
    }

    /// <summary>偵測已安裝的實體網路卡（WMI Win32_NetworkAdapter，PhysicalAdapter=TRUE，含未連線者）。</summary>
    public static List<NicInfo> GetNetworkAdapters()
    {
        var list = new List<NicInfo>();
        foreach (var o in Query("SELECT Name, Manufacturer, AdapterType, Speed, MACAddress, PhysicalAdapter " +
                                "FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE"))
        {
            string mac = Str(o, "MACAddress");
            if (mac == "—") continue;                     // 無 MAC 者多為虛擬 / 樁介面，略過
            double bps = Num(o, "Speed");
            list.Add(new NicInfo
            {
                Name = Str(o, "Name"),
                Manufacturer = Str(o, "Manufacturer"),
                TypeText = Str(o, "AdapterType"),
                // 連線中斷時 WMI 的 Speed 常為 Int64.MaxValue 等哨兵值，超出合理上限即視為未知
                SpeedText = bps > 0 && bps < 1e12 ? $"{bps / 1_000_000.0:0} Mbps" : "—",
                Mac = mac,
            });
        }
        return list;
    }

    // ---- helpers ----------------------------------------------------------
    private static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        ManagementObjectCollection? col = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            col = searcher.Get();
        }
        catch { yield break; }
        // 每個 ManagementBaseObject 皆包一個未受管 COM 物件，取用後須逐一釋放，集合本身亦然，
        // 否則長時間執行（每 5 秒查詢磁碟容量等）會累積 COM 控制代碼洩漏。
        try
        {
            foreach (ManagementBaseObject o in col)
            {
                try { yield return o; }
                finally { o.Dispose(); }
            }
        }
        finally { col.Dispose(); }
    }

    private static string Str(ManagementBaseObject o, string p)
    {
        try { return o[p]?.ToString()?.Trim() is { Length: > 0 } v ? v : "—"; }
        catch { return "—"; }
    }

    private static double Num(ManagementBaseObject o, string p)
    {
        try { return o[p] is { } v ? Convert.ToDouble(v) : 0; }
        catch { return 0; }
    }

    private static string Date(ManagementBaseObject o, string p)
        => DateOrNull(o, p) is DateTime d ? d.ToString("yyyy-MM-dd") : "—";

    private static DateTime? DateOrNull(ManagementBaseObject o, string p)
    {
        try
        {
            var raw = o[p]?.ToString();
            if (string.IsNullOrEmpty(raw)) return null;
            return ManagementDateTimeConverter.ToDateTime(raw);
        }
        catch { return null; }
    }

    private static string SafeEnv(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;
    private static string KB(double kb) => kb > 0 ? (kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb:0} KB") : "—";

    private static string FormatUptime(TimeSpan t)
        => $"{(int)t.TotalDays} 天 {t.Hours} 小時 {t.Minutes} 分";

    private static string FormFactor(int code) => code switch
    {
        8 => "DIMM", 12 => "SODIMM", 13 => "SRIMM", 11 => "RIMM", 9 => "TSOP", _ => code > 0 ? $"型式 {code}" : "—"
    };

    // 依 SMBIOS 規範 Type 17（Memory Device）之 Memory Type 欄位對照。
    // 注意：此處讀的是 SMBIOSMemoryType（原始 SMBIOS 碼），與舊版 Win32_PhysicalMemory.MemoryType
    // 列舉（20=DDR、21=DDR2）不同——DDR/DDR2 兩者相差 2，切勿混用。
    private static string MemType(int code) => code switch
    {
        18 => "DDR", 19 => "DDR2", 20 => "DDR2 FB-DIMM",
        24 => "DDR3", 25 => "FBD2", 26 => "DDR4",
        27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4",
        34 => "DDR5", 35 => "LPDDR5",
        _ => code > 0 ? $"類型 {code}" : "—"
    };
}
