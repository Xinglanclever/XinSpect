using System.Management;

namespace XinSpect;

/// <summary>
/// DIMM 插槽規格參考資料庫。
/// 從 SMBIOS 偵測目前記憶體類型，並提供各世代 DIMM 的腳位數、電壓、頻率範圍與關鍵差異。
/// </summary>
public sealed class DimmReferenceService : ObservableObject
{
    private string _detectedType = "（偵測中…）";
    public string DetectedType { get => _detectedType; private set => SetProperty(ref _detectedType, value); }

    private string _formFactor = "—";
    public string FormFactor { get => _formFactor; private set => SetProperty(ref _formFactor, value); }

    private string _voltage = "—";
    public string Voltage { get => _voltage; private set => SetProperty(ref _voltage, value); }

    private string _pinCount = "—";
    public string PinCountText { get => _pinCount; private set => SetProperty(ref _pinCount, value); }

    private string _maxFrequency = "—";
    public string MaxFrequency { get => _maxFrequency; private set => SetProperty(ref _maxFrequency, value); }

    private string _description = "";
    public string Description { get => _description; private set => SetProperty(ref _description, value); }

    private string _generationNote = "";
    public string GenerationNote { get => _generationNote; private set => SetProperty(ref _generationNote, value); }

    private List<string> _keyFacts = [];
    public List<string> KeyFacts { get => _keyFacts; private set => SetProperty(ref _keyFacts, value); }

    private bool _isKnownType;
    public bool IsKnownType { get => _isKnownType; private set => SetProperty(ref _isKnownType, value); }

    private List<DimmGeneration> _allGenerations = [];
    public List<DimmGeneration> AllGenerations { get => _allGenerations; private set => SetProperty(ref _allGenerations, value); }

    public DimmReferenceService() : this(autoDetect: true) { }

    /// <summary>
    /// <paramref name="autoDetect"/> 只給測試用：<c>false</c> 時不自動查 WMI，
    /// 測試才能用注入的查詢驗證逾時與判讀，而不必和建構子丢出去的背景查詢搶同一個欄位。
    /// </summary>
    internal DimmReferenceService(bool autoDetect)
    {
        AllGenerations = BuildAllGenerations();
        if (!autoDetect) return;

        // 建構子是在 MainViewModel 的欄位初始化式裡被呼叫的——主視窗還沒顯示、而且就在 UI 執行緒上。
        // WMI 的列舉沒有可用的逾時（見 WmiGuard 的說明），在這裡同步查詢等於把「視窗出不來」
        // 綁在「WMI 什麼時候回來」上：2026-10-31 那五份只有行程、沒有視窗的曦覽就是卡在這一行。
        // 屬性全是純量／List（沒有 ObservableCollection），背景設值時 WPF 會把 PropertyChanged 封送回 UI。
        _ = Task.Run(() => DetectAndLoad());
    }

    /// <summary>
    /// 讀一次記憶體模組並套用結果。<paramref name="read"/>／<paramref name="timeoutMs"/> 是測試注入點：
    /// 換掉查詢就能重現「WMI 卡住」與各種型別碼，不必真的把一台機器的 WMI 弄壞。
    /// </summary>
    internal void DetectAndLoad(Func<List<DimmModuleRow>?>? read = null, int? timeoutMs = null)
    {
        var (modules, timedOut) = WmiGuard.Probe(read ?? ReadModules,
            timeoutMs ?? WmiGuard.DefaultTimeoutMs);

        if (timedOut)
        {
            // 如實說沒回應，不要停在「（偵測中…）」讓人以為還在跑。
            DetectedType = "—（WMI 無回應）";
            return;
        }

        if (modules is null) { DetectedType = "—（WMI 查詢失敗）"; return; }
        if (modules.Count == 0) { DetectedType = "—（WMI 未回傳記憶體模組）"; return; }

        // Win32_PhysicalMemory 的 MemoryType／FormFactor 是 **CIM 列舉**，
        // 與 SMBIOS 的型別碼是兩套不同的編號（CIM DDR=20，SMBIOS DDR=0x12）。
        // 只有 SMBIOSMemoryType 才是 SMBIOS 碼，所以優先讀它；真的沒有才拿 CIM 值去查 CIM 自己的表。
        // 判讀一直是「看第一條模組就定案」，這裡維持同一個語意。
        var mo = modules[0];
        if (mo.FormFactor > 0 && CimFormFactorName(mo.FormFactor) is { } ffName) FormFactor = ffName;

        if (mo.Smbios > 0)
        {
            DetectedType = SmbiosService.MemoryTypeName((byte)mo.Smbios);
            ApplyTypeData(DetectedType);
            return;
        }

        if (mo.Cim > 0 && CimMemoryTypeName(mo.Cim) is { } cimName)
        {
            DetectedType = cimName;
            ApplyTypeData(cimName);
            return;
        }

        DetectedType = mo.Cim > 0
            ? $"—（未回報型別碼 0x{mo.Cim:X2}）"
            : "—（WMI 未回報型別碼）";
    }

    /// <summary>查 <c>Win32_PhysicalMemory</c>，只留判讀需要的三個欄位；查不到回 <c>null</c>。</summary>
    static List<DimmModuleRow>? ReadModules()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT MemoryType, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory");
            var rows = new List<DimmModuleRow>();
            foreach (ManagementObject mo in searcher.Get())
            {
                rows.Add(new DimmModuleRow(
                    Convert.ToInt32(mo["SMBIOSMemoryType"] ?? 0),
                    Convert.ToInt32(mo["MemoryType"] ?? 0),
                    Convert.ToInt32(mo["FormFactor"] ?? 0)));
            }
            return rows;
        }
        catch
        {
            // 權限不足、命名空間不存在……都走「查詢失敗」那一條，與改動前的行為一致。
            return null;
        }
    }

    /// <summary>一條 <c>Win32_PhysicalMemory</c> 的三個原始欄位（兩個列舉碼不可混用，見上方說明）。</summary>
    internal sealed record DimmModuleRow(int Smbios, int Cim, int FormFactor);

    /// <summary>CIM <c>Win32_PhysicalMemory.MemoryType</c> 的列舉值 → 世代名。</summary>
    /// <remarks>
    /// 不可與 <see cref="SmbiosService.MemoryTypeName"/> 混用：兩套編號在 24 以下差 2
    /// （CIM DDR=20 對 SMBIOS 的 0x14＝DDR2 FB-DIMM），代進去會把 DDR 講成 DDR2 FB-DIMM。
    /// 未知值回 <c>null</c>，由呼叫端顯示「無法判讀」而不是硬吐一個十六進位碼。
    /// </remarks>
    internal static string? CimMemoryTypeName(int code) => code switch
    {
        20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM", 24 => "DDR3", 25 => "FBD2",
        26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4",
        31 => "非揮發性裝置", 32 => "HBM", 33 => "HBM2", 34 => "DDR5", 35 => "LPDDR5",
        36 => "HBM3",
        _ => null,
    };

    /// <summary>CIM <c>Win32_PhysicalMemory.FormFactor</c> 的列舉值 → 封裝名。</summary>
    internal static string? CimFormFactorName(int code) => code switch
    {
        7  => "SIMM", 8 => "DIMM", 11 => "RIMM", 12 => "SODIMM", 13 => "SRIMM",
        17 => "DDR", 18 => "DDR2", 19 => "DDR2 FB-DIMM", 20 => "DDR3", 24 => "DDR4",
        25 => "DDR5",
        _ => null,
    };

    void ApplyTypeData(string typeName)
    {
        var gen = AllGenerations.Find(g => g.TypeName == typeName);
        if (gen is null)
        {
            Description = "此記憶體類型不在內建資料庫中。";
            IsKnownType = false;
            return;
        }

        PinCountText = gen.Pins;
        Voltage = gen.Voltage;
        MaxFrequency = gen.MaxFreq;
        Description = gen.Description;
        GenerationNote = gen.Era;
        KeyFacts = [.. gen.Facts];
        IsKnownType = true;
    }

    static List<DimmGeneration> BuildAllGenerations() =>
    [
        new("DDR5", "DIMM: 288 pin (key 偏右)", "1.1 V", "8400+ MT/s",
            "DDR5 (2020–)",
            "第五代雙倍資料速率同步動態隨機存取記憶體，採用模組內雙通道架構。",
            [
                "DIMM 288 pin，但 key 位置與 DDR4 不同，不可互插",
                "SODIMM 262 pin（筆電）",
                "模組內雙通道：每條 DIMM 內含兩個獨立 32-bit 通道",
                "PMIC 電源管理 IC 移至模組上，降低主機板設計複雜度",
                "ECC on-die：每個 DRAM 晶粒內建堆疊 ECC",
                "XMP 3.0 / EXPO 超頻設定檔",
                "起始速率 4800 MT/s，標準已進展至 8400+ MT/s",
            ]),
        new("DDR4", "DIMM: 288 pin (key 偏左)", "1.2 V", "3200 MT/s",
            "DDR4 (2014–2022)",
            "第四代雙倍資料速率同步動態隨機存取記憶體，目前仍廣泛使用。",
            [
                "DIMM 288 pin，key 偏左與 DDR5 不同",
                "SODIMM 260 pin（筆電）",
                "單通道 64-bit（每條 DIMM）",
                "JEDEC 標準速率 2133–3200 MT/s",
                "XMP 2.0 超頻設定檔，常見至 4800+ MT/s",
                "電壓 1.2 V（標準），LPDDR4 為 1.1 V",
                "Bank Group 概念引入，提升并行存取效率",
            ]),
        new("DDR3", "DIMM: 240 pin", "1.5 V (1.35 V Low)", "2133 MT/s",
            "DDR3 (2007–2014)",
            "第三代雙倍資料速率同步動態隨機存取記憶體。",
            [
                "DIMM 240 pin，key 位置與 DDR2 不同",
                "SODIMM 204 pin（筆電）",
                "JEDEC 標準 800–2133 MT/s",
                "電壓 1.5 V，DDR3L 為 1.35 V",
                "單通道 64-bit（每條 DIMM）",
                "常見於 Intel 2–4 代 Core 與 AMD FX 平台",
            ]),
        new("DDR2", "DIMM: 240 pin", "1.8 V", "1066 MT/s",
            "DDR2 (2003–2009)",
            "第二代雙倍資料速率同步動態隨機存取記憶體。",
            [
                "DIMM 240 pin，但 key 位置與 DDR3 不同，不可互插",
                "SODIMM 200 pin（筆電）",
                "JEDEC 標準 400–1066 MT/s",
                "電壓 1.8 V",
                "常見於 Intel Core 2 與早期 AMD Athlon 64 平台",
            ]),
        new("DDR", "DIMM: 184 pin", "2.5 V", "400 MT/s",
            "DDR (2000–2005)",
            "第一代雙倍資料速率同步動態隨機存取記憶體。",
            [
                "DIMM 184 pin",
                "JEDEC 標準 200–400 MT/s",
                "電壓 2.5 V",
                "單通道 64-bit",
            ]),
    ];

    public sealed record DimmGeneration(
        string TypeName, string Pins, string Voltage, string MaxFreq,
        string Era, string Description, List<string> Facts);
}
