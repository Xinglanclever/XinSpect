using System.Collections.ObjectModel;
using System.Management;

namespace XinSpect;

/// <summary>
/// 供電模組分析服務：VRM 相位估算、GPU 供電偵測與電源接頭參考。
/// </summary>
public sealed class PowerDeliveryService : ObservableObject
{
    private string _motherboardModel = "";
    public string MotherboardModel { get => _motherboardModel; private set => SetProperty(ref _motherboardModel, value); }

    private string _cpuName = "";
    public string CpuName { get => _cpuName; private set => SetProperty(ref _cpuName, value); }

    private string _gpuName = "";
    public string GpuName { get => _gpuName; private set => SetProperty(ref _gpuName, value); }

    private string _vrmPhaseDescription = "";
    public string VrmPhaseDescription { get => _vrmPhaseDescription; private set => SetProperty(ref _vrmPhaseDescription, value); }

    private double? _vrmTempCelsius;
    public double? VrmTempCelsius { get => _vrmTempCelsius; private set => SetProperty(ref _vrmTempCelsius, value); }

    public int? CpuTdp
    {
        get => _cpuTdp;
        private set { if (SetProperty(ref _cpuTdp, value)) OnPropertyChanged(nameof(CpuTdpText)); }
    }
    private int? _cpuTdp;

    /// <summary>功耗顯示字串；推估不到時明說，不給一個看起來像真值的預設瓦數。</summary>
    public string CpuTdpText => CpuTdp is int t and > 0
        ? $"{t}W（推估）"
        : "—（型號不在估算表中）";

    private string _vrmCapability = "";
    public string VrmCapability { get => _vrmCapability; private set => SetProperty(ref _vrmCapability, value); }

    private string _vrmTierAdvice = "";
    public string VrmTierAdvice { get => _vrmTierAdvice; private set => SetProperty(ref _vrmTierAdvice, value); }

    private string _gpuConnectorType = "";
    public string GpuConnectorType { get => _gpuConnectorType; private set => SetProperty(ref _gpuConnectorType, value); }

    private int _gpuConnectorWattage;
    public int GpuConnectorWattage { get => _gpuConnectorWattage; private set => SetProperty(ref _gpuConnectorWattage, value); }

    private double _gpuPowerLimit;
    public double GpuPowerLimit { get => _gpuPowerLimit; private set => SetProperty(ref _gpuPowerLimit, value); }

    private string _gpuPowerSummary = "";
    public string GpuPowerSummary { get => _gpuPowerSummary; private set => SetProperty(ref _gpuPowerSummary, value); }

    private ObservableCollection<ConnectorReferenceEntry> _connectorReference = [];
    public ObservableCollection<ConnectorReferenceEntry> ConnectorReference { get => _connectorReference; private set => SetProperty(ref _connectorReference, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    /// <summary>電源接頭參考表（靜態）。</summary>
    public static readonly IReadOnlyList<ConnectorReferenceEntry> ConnectorReferenceTable =
    [
        new("PCIe 插槽",       75,  "主機板 PCIe 插槽本身可供 75W"),
        new("6-pin",           75,  "6 針 PCIe 輔助電源（3 條 12V）"),
        new("8-pin (6+2)",     150, "8 針 PCIe 輔助電源（3 條 12V + 1 條 sense）"),
        new("雙 8-pin",        300, "2 x 8-pin（高階卡常見，如 RTX 3080）"),
        new("三 8-pin",        450, "3 x 8-pin（旗艦卡，如 RTX 3090）"),
        new("12VHPWR (16-pin)",600, "12+4 pin 新規格（RTX 4090 等）"),
        new("12V-2x6 (16-pin)",600, "12VHPWR 改良版，加強接觸可靠度"),
    ];

    public void Refresh()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            ReadPlatformInfo();
            AnalyseVrm();
            AnalyseGpuPower();
            ConnectorReference = new(ConnectorReferenceTable);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"分析失敗：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ==================================================================
    //  平台資訊讀取
    // ==================================================================

    private void ReadPlatformInfo()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Product, Manufacturer FROM Win32_BaseBoard");
            foreach (var o in s.Get()) { MotherboardModel = $"{o["Manufacturer"]} {o["Product"]}".Trim(); break; }
        }
        catch (Exception ex) { Diag.Swallow("PowerDelivery.Board", ex, "主機板型號讀不到"); MotherboardModel = "未偵測到"; }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in s.Get()) { CpuName = o["Name"]?.ToString()?.Trim() ?? ""; break; }
        }
        catch (Exception ex) { Diag.Swallow("PowerDelivery.CPU", ex, "CPU 名稱讀不到"); CpuName = "未偵測到"; }

        try
        {
            // 虛擬／遠端顯示配接器常排在真顯卡前面（本機 MuMu、GameViewer 都在 TITAN Xp 之前）；
            // 取第一個會在沒有獨顯的判斷上整個失準，後面的功耗與接頭推估全部連帶錯。
            using var s = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (var o in s.Get())
            {
                string name = o["Name"]?.ToString()?.Trim() ?? "";
                string pnp = o["PNPDeviceID"]?.ToString()?.Trim() ?? "";
                if (name.Length == 0) continue;
                if (GpuAnalysisService.IsVirtualDisplay(name)) continue;
                if (!pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;
                GpuName = name;
                break;
            }
        }
        catch (Exception ex) { Diag.Swallow("PowerDelivery.GPU", ex, "GPU 名稱讀不到"); GpuName = "未偵測到"; }

        CpuTdp = EstimateCpuTdp(CpuName);
    }

    // ==================================================================
    //  VRM 分析
    // ==================================================================

    private void AnalyseVrm()
    {
        var (phases, tier) = EstimateVrmPhases(MotherboardModel);
        VrmPhaseDescription = phases;
        VrmTempCelsius = null;

        VrmCapability = tier switch
        {
            VrmTier.Premium => $"高階 VRM，可輕鬆支撐 {CpuTdpText} TDP 並留有超頻裕度",
            VrmTier.Mainstream => $"主流 VRM，滿足 {CpuTdpText} TDP 日常使用綽綽有餘",
            VrmTier.Budget => $"入門 VRM，{CpuTdpText} TDP 在高負載下可能溫度偏高",
            _ => "無法評估（主機板型號不在資料庫中）",
        };

        VrmTierAdvice = tier switch
        {
            VrmTier.Premium => "這張主機板的供電設計屬於高階等級，長時間全核心負載或超頻均無須擔心。",
            VrmTier.Mainstream => "供電足以應付預設 TDP；若計畫長時間解鎖功耗限制，建議留意 VRM 溫度。",
            VrmTier.Budget => "入門級供電，建議維持預設功耗限制。高負載時確保機殼有良好的氣流通過 VRM 散熱片。",
            _ => "未知主機板型號，無法給出具體建議。可搭配溫度監控工具觀察 VRM 區域溫度。",
        };
    }

    // ==================================================================
    //  GPU 供電分析
    // ==================================================================

    private void AnalyseGpuPower()
    {
        var (connector, wattage, tdp) = EstimateGpuPower(GpuName);
        GpuConnectorType = connector;
        GpuConnectorWattage = wattage;
        GpuPowerLimit = tdp;

        int totalPower = wattage + 75;
        GpuPowerSummary = tdp > 0
            ? $"推估 TDP {tdp:F0}W，接頭 {connector} 可供 {wattage}W + 插槽 75W = 共 {totalPower}W"
            : "無法推估 GPU TDP（型號不在資料庫中）";
    }

    // ==================================================================
    //  估算資料庫
    // ==================================================================

    /// <summary>
    /// 由型號字串推估 TDP（瓦）。查不到回 <c>null</c>——不回一個看起來合理的預設值。
    /// </summary>
    /// <remarks>
    /// 先前未知型號一律回 65 W，於是 i9-7980XE（實際 165 W）被描述成「65W TDP 留有超頻裕度」，
    /// 那是會誤導人的低估值。查不到就說查不到，讓 UI 顯示「—」。
    /// </remarks>
    internal static int? EstimateCpuTdp(string cpuName)
    {
        string upper = cpuName.ToUpperInvariant();

        // 後綴（U/H/HX/K/KF/KS/T）要先判，否則 i7-13700H 會先被數字規則攔成桌上型功耗。
        bool mobileU = upper.Contains("U ") || upper.Contains("U]") || upper.Contains("U CPU");
        bool mobileH = upper.Contains("H ") || upper.Contains("H]") || upper.Contains("HX") || upper.Contains("H CPU");

        if (upper.Contains("14900K") || upper.Contains("13900K")) return 253;
        if (upper.Contains("14700K") || upper.Contains("13700K")) return 253;
        if (upper.Contains("14600K") || upper.Contains("13600K")) return 181;
        if (upper.Contains("12900K")) return 241;
        if (upper.Contains("12700K")) return 190;
        if (upper.Contains("12600K")) return 150;

        // HEDT / 工作站（X299、X399、TRX40、C62x 這些平台）
        if (upper.Contains("7980XE") || upper.Contains("7960X") || upper.Contains("7940X")) return 165;
        if (upper.Contains("7980") || upper.Contains("7960") || upper.Contains("7940")) return 165;
        if (upper.Contains("7900X") && upper.Contains("I9-7")) return 140;   // Skylake-X 7900X
        if (upper.Contains("7820X") || upper.Contains("7800X")) return 140;
        if (upper.Contains("THREADRIPPER")) return 280;
        if (upper.Contains("XEON")) return 165;

        if (upper.Contains("14900") || upper.Contains("13900")) return 65;
        if (upper.Contains("14700") || upper.Contains("13700")) return 65;

        if (upper.Contains("9950X")) return 170;
        if (upper.Contains("9900X")) return 120;
        if (upper.Contains("9700X") || upper.Contains("9600X")) return 65;
        if (upper.Contains("7950X")) return 170;
        if (upper.Contains("7900X")) return 170;
        if (upper.Contains("7700X") || upper.Contains("7600X")) return 105;
        if (upper.Contains("5950X") || upper.Contains("5900X")) return 105;
        if (upper.Contains("5800X")) return 105;
        if (upper.Contains("5600X") || upper.Contains("5600")) return 65;

        if (mobileU) return 15;
        if (mobileH) return 45;

        return null;   // 不在估算表中——誠實回未知
    }

    internal static (string phases, VrmTier tier) EstimateVrmPhases(string boardModel)
    {
        string upper = boardModel.ToUpperInvariant();
        if (upper.Contains("MAXIMUS") || upper.Contains("CROSSHAIR"))
            return ("推估 16+2 相以上（旗艦系列）", VrmTier.Premium);
        if (upper.Contains("STRIX") && (upper.Contains("Z790") || upper.Contains("X670") || upper.Contains("Z890") || upper.Contains("X870")))
            return ("推估 14+2 相（STRIX 高階）", VrmTier.Premium);
        if (upper.Contains("STRIX"))
            return ("推估 10+2 相（STRIX 系列）", VrmTier.Mainstream);
        if (upper.Contains("TUF"))
            return ("推估 12+2 相（TUF 系列）", VrmTier.Mainstream);
        if (upper.Contains("PRIME") && upper.Contains("ASUS"))
            return ("推估 8+1 相（PRIME 入門系列）", VrmTier.Budget);
        if (upper.Contains("MEG") || upper.Contains("GODLIKE") || upper.Contains("ACE"))
            return ("推估 16+2 相以上（MEG 旗艦系列）", VrmTier.Premium);
        if (upper.Contains("MPG"))
            return ("推估 12+2 相（MPG 系列）", VrmTier.Mainstream);
        if (upper.Contains("MAG"))
            return ("推估 10+2 相（MAG 系列）", VrmTier.Mainstream);
        if (upper.Contains("PRO") && upper.Contains("MSI"))
            return ("推估 8+1 相（PRO 入門系列）", VrmTier.Budget);
        if (upper.Contains("AORUS") && (upper.Contains("MASTER") || upper.Contains("XTREME")))
            return ("推估 16+2 相以上（AORUS 旗艦）", VrmTier.Premium);
        if (upper.Contains("AORUS") && upper.Contains("PRO"))
            return ("推估 12+2 相（AORUS PRO）", VrmTier.Mainstream);
        if (upper.Contains("AORUS") && upper.Contains("ELITE"))
            return ("推估 12+1 相（AORUS ELITE）", VrmTier.Mainstream);
        if (upper.Contains("GAMING") && upper.Contains("GIGABYTE"))
            return ("推估 8+2 相（GAMING 系列）", VrmTier.Budget);
        if (upper.Contains("TAICHI") || upper.Contains("AQUA"))
            return ("推估 16+2 相以上（Taichi 旗艦）", VrmTier.Premium);
        if (upper.Contains("STEEL LEGEND") || upper.Contains("PG"))
            return ("推估 10+2 相（Steel Legend / PG 系列）", VrmTier.Mainstream);
        if (upper.Contains("Z790") || upper.Contains("Z890") || upper.Contains("X670") || upper.Contains("X870"))
            return ("推估 12+ 相（高階晶片組主機板）", VrmTier.Mainstream);
        if (upper.Contains("B760") || upper.Contains("B650") || upper.Contains("B550"))
            return ("推估 8-10 相（主流晶片組主機板）", VrmTier.Mainstream);
        if (upper.Contains("H610") || upper.Contains("A520") || upper.Contains("A620"))
            return ("推估 6+1 相（入門晶片組主機板）", VrmTier.Budget);
        return ("無法推估（主機板型號不在資料庫中）", VrmTier.Unknown);
    }

    internal static (string connector, int wattage, double tdp) EstimateGpuPower(string gpuName)
    {
        string upper = gpuName.ToUpperInvariant();
        if (upper.Contains("4090")) return ("12VHPWR (16-pin)", 600, 450);
        if (upper.Contains("4080")) return ("12VHPWR (16-pin)", 600, 320);
        if (upper.Contains("4070 TI SUPER")) return ("12VHPWR (16-pin)", 600, 285);
        if (upper.Contains("4070 TI")) return ("12VHPWR (16-pin)", 600, 285);
        if (upper.Contains("4070 SUPER")) return ("12VHPWR (16-pin)", 600, 220);
        if (upper.Contains("4070")) return ("8-pin (6+2)", 150, 200);
        if (upper.Contains("4060 TI")) return ("8-pin (6+2)", 150, 160);
        if (upper.Contains("4060")) return ("8-pin (6+2)", 150, 115);
        if (upper.Contains("3090")) return ("三 8-pin", 450, 350);
        if (upper.Contains("3080")) return ("雙 8-pin", 300, 320);
        if (upper.Contains("3070")) return ("雙 8-pin", 300, 220);
        if (upper.Contains("3060 TI")) return ("8-pin (6+2)", 150, 200);
        if (upper.Contains("3060")) return ("8-pin (6+2)", 150, 170);
        if (upper.Contains("7900 XTX")) return ("雙 8-pin", 300, 355);
        if (upper.Contains("7900 XT")) return ("雙 8-pin", 300, 315);
        if (upper.Contains("7800 XT")) return ("雙 8-pin", 300, 263);
        if (upper.Contains("7700 XT")) return ("8-pin (6+2)", 150, 245);
        if (upper.Contains("7600")) return ("8-pin (6+2)", 150, 165);
        if (upper.Contains("6950 XT")) return ("雙 8-pin", 300, 335);
        if (upper.Contains("6900 XT")) return ("雙 8-pin", 300, 300);
        if (upper.Contains("6800 XT")) return ("雙 8-pin", 300, 300);
        if (upper.Contains("6800")) return ("雙 8-pin", 300, 250);
        if (upper.Contains("6700 XT")) return ("8-pin (6+2)", 150, 230);
        if (upper.Contains("6600")) return ("8-pin (6+2)", 150, 132);
        if (upper.Contains("UHD") || upper.Contains("VEGA") || upper.Contains("RADEON(TM) GRAPHICS"))
            return ("無（內建顯示）", 0, 0);
        if (upper.Contains("TITAN")) return ("雙 8-pin", 300, 250);
        if (upper.Contains("QUADRO") || upper.Contains("RTX A")) return ("8-pin (6+2)", 150, 130);
        return ("未知", 0, 0);
    }
}

public enum VrmTier { Unknown, Budget, Mainstream, Premium }

public sealed record ConnectorReferenceEntry(string ConnectorName, int Wattage, string Description);
