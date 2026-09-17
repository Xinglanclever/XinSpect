using System.Management;

namespace XinSpect;

/// <summary>NPU 資訊：存在與否、裝置名稱、驅動版本與估計算力。</summary>
public sealed class NpuDetectionService : ObservableObject
{
    // ── 已知 NPU 的估計 TOPS 查表（型號子字串 → 整數 TOPS） ──────────────────
    private static readonly (string Pattern, int Tops)[] KnownTops =
    [
        ("Meteor Lake",       10),
        ("Lunar Lake",        48),
        ("Arrow Lake",        13),
        ("Core Ultra 200V",   48),
        ("Core Ultra 200S",   13),
        ("Core Ultra 200",    11),
        ("Core Ultra 100",    10),
        ("NPU Compute 3700",  45),  // Ryzen AI 300
        ("NPU Compute 3600",  50),  // Ryzen AI 9 HX 370
        ("IPU Device 1502",   16),  // Ryzen AI 7000/8000
        ("Hexagon",           45),  // Qualcomm Snapdragon X Elite
    ];

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    private string _status = "尚未偵測";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool _npuPresent;
    public bool NpuPresent { get => _npuPresent; private set => SetProperty(ref _npuPresent, value); }

    private string _npuName = "";
    public string NpuName { get => _npuName; private set => SetProperty(ref _npuName, value); }

    private string _npuDriver = "";
    public string NpuDriver { get => _npuDriver; private set => SetProperty(ref _npuDriver, value); }

    private string _npuDriverDate = "";
    public string NpuDriverDate { get => _npuDriverDate; private set => SetProperty(ref _npuDriverDate, value); }

    private string _estimatedTops = "";
    public string EstimatedTops { get => _estimatedTops; private set => SetProperty(ref _estimatedTops, value); }

    /// <summary>（重新）偵測 NPU。</summary>
    public void Refresh()
    {
        IsLoading = true;
        NpuPresent = false;
        NpuName = "";
        NpuDriver = "";
        NpuDriverDate = "";
        EstimatedTops = "";
        Status = "偵測中…";

        try
        {
            // 策略一：搜尋 PnP 裝置名稱／描述中含 NPU / Neural / VPU / AI Accelerator / AMD IPU / XDNA / Hexagon
            var found = TryFindByName();
            if (!found)
            {
                // 策略二：以 PCI 類別碼 0x0B40（Processing Accelerators：Neural Network）搜尋
                found = TryFindByPciClass();
            }

            if (!found)
            {
                Status = "未偵測到 NPU。此機器可能不具備 NPU 或驅動未安裝。";
            }
        }
        catch (Exception ex)
        {
            Status = "偵測失敗：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool TryFindByName()
    {
        string[] keywords = ["NPU", "Neural", "VPU", "AI Accelerator", "AMD IPU", "XDNA", "Hexagon"];

        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT Name, Description, PNPDeviceID, DeviceID, CompatibleID, HardwareID FROM Win32_PnPEntity");

        foreach (ManagementObject dev in searcher.Get())
        {
            string name = dev["Name"]?.ToString() ?? "";
            string desc = dev["Description"]?.ToString() ?? "";
            string combined = name + " " + desc;

            bool match = false;
            foreach (var kw in keywords)
            {
                if (MatchesKeyword(combined, kw))
                {
                    match = true;
                    break;
                }
            }
            if (!match) continue;

            NpuPresent = true;
            NpuName = name.Length > 0 ? name : desc;
            FillDriverInfo(dev);
            EstimatedTops = LookupTops(NpuName);
            Status = "已偵測到 NPU";
            return true;
        }
        return false;
    }

    /// <summary>
    /// 關鍵字比對。短的全大寫縮寫（NPU／VPU／IPU）必須是<b>獨立詞</b>，
    /// 否則任何含這三個字母的裝置名都會中——例如 "gvinput Device" 裡的 gvi<b>npu</b>t
    /// 就會被誤判成 NPU。（本機實測：X299 平台沒有任何 NPU，卻回報偵測到了。）
    /// 長字串（Neural、AI Accelerator…）用一般子字串比對即可。
    /// </summary>
    private static bool MatchesKeyword(string text, string keyword)
    {
        if (keyword.Length <= 4 && keyword.All(char.IsUpper))
            return System.Text.RegularExpressions.Regex.IsMatch(
                text,
                $@"(?<![A-Za-z0-9]){System.Text.RegularExpressions.Regex.Escape(keyword)}(?![A-Za-z0-9])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryFindByPciClass()
    {
        // PCI class 0x0B40 = Processing Accelerators: Neural Network
        // In Win32_PnPEntity, PCI devices have PNPDeviceID like PCI\VEN_xxxx&DEV_xxxx&SUBSYS_xxxx&REV_xx
        // and ClassGuid. We search for compatible IDs containing "PCI\CC_0B40"
        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT Name, Description, PNPDeviceID, DeviceID, CompatibleID, HardwareID FROM Win32_PnPEntity");

        foreach (ManagementObject dev in searcher.Get())
        {
            var compatIds = dev["CompatibleID"] as string[];
            if (compatIds is null) continue;

            bool match = false;
            foreach (var id in compatIds)
            {
                if (id.Contains("CC_0B40", StringComparison.OrdinalIgnoreCase))
                {
                    match = true;
                    break;
                }
            }
            if (!match) continue;

            string name = dev["Name"]?.ToString() ?? "";
            string desc = dev["Description"]?.ToString() ?? "";

            NpuPresent = true;
            NpuName = name.Length > 0 ? name : desc;
            FillDriverInfo(dev);
            EstimatedTops = LookupTops(NpuName);
            Status = "已偵測到 NPU（PCI 類別 0x0B40）";
            return true;
        }
        return false;
    }

    private void FillDriverInfo(ManagementObject pnpEntity)
    {
        try
        {
            // 用 PnP 裝置的 DeviceID 查 Win32_PnPSignedDriver 取得驅動版本與日期
            string? deviceId = pnpEntity["PNPDeviceID"] as string ?? pnpEntity["DeviceID"] as string;
            if (deviceId is null) return;

            string escaped = deviceId.Replace("\\", "\\\\");
            using var drvSearch = new ManagementObjectSearcher(
                "root\\CIMV2",
                $"SELECT DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceID='{escaped}'");

            foreach (ManagementObject drv in drvSearch.Get())
            {
                NpuDriver = drv["DriverVersion"]?.ToString() ?? "—";
                string? raw = drv["DriverDate"]?.ToString();
                if (raw is { Length: >= 8 })
                {
                    // WMI DriverDate 格式：yyyyMMddHHmmss.ffffff+zzz
                    NpuDriverDate = raw[..4] + "-" + raw[4..6] + "-" + raw[6..8];
                }
                break;
            }
        }
        catch
        {
            // 驅動資訊為附加功能，取不到不影響偵測結果
        }
    }

    private static string LookupTops(string deviceName)
    {
        foreach (var (pattern, tops) in KnownTops)
        {
            if (deviceName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return $"~{tops} TOPS（依型號查表估計）";
        }
        return "—（型號不在查表中）";
    }
}
