using System.Management;

namespace XinSpect;

/// <summary>NPU 資訊：存在與否、裝置名稱、驅動版本與估計算力。</summary>
public sealed class NpuDetectionService : ObservableObject
{
    // ── 已知 NPU 的估計 TOPS 查表（型號子字串 → 整數 TOPS） ──────────────────
    private static readonly (string Pattern, int Tops)[] KnownTops =
    [
        // Intel；較特定的 pattern 放前面（泛用的 "Core Ultra 200" 會吃掉 "200V"/"200S"）
        ("Core Ultra 200V",   48),  // Lunar Lake
        ("Core Ultra 200S",   13),  // Arrow Lake-S
        ("Core Ultra 300",    50),  // Panther Lake
        ("Panther Lake",      50),
        ("Lunar Lake",        48),
        ("Arrow Lake",        13),
        ("Meteor Lake",       10),
        ("Core Ultra 200",    11),
        ("Core Ultra 100",    10),
        // AMD XDNA（裝置名為 NPU Compute XXXX）
        ("NPU Compute 3700",  45),  // Ryzen AI 300 / AI 9 HX 370 級
        ("NPU Compute 3600",  50),  // Ryzen AI 9 HX 370
        ("NPU Compute 3500",  43),  // Ryzen AI 9 365 級
        ("NPU Compute 3200",  38),  // Ryzen AI 9 300 級（Krackan）
        ("IPU Device 1502",   16),  // Ryzen AI 7000/8000
        // Qualcomm Hexagon
        ("Hexagon 520",       80),  // Snapdragon X2 Elite
        ("Hexagon",           45),  // Snapdragon X Elite
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

    private string _cpuName = "";

    /// <summary>（重新）偵測 NPU。WMI 列舉在背景執行緒，避免進頁凍結；呼叫端 await 後再讀結果。</summary>
    /// <remarks>NPU 裝置名常不帶平台資訊（如「Intel(R) AI Boost」），傳入 CPU 名稱可讓 TOPS 估計多一路比對。</remarks>
    public async Task RefreshAsync(string? cpuName = null)
    {
        _cpuName = cpuName ?? "";
        IsLoading = true;
        NpuPresent = false;
        NpuName = "";
        NpuDriver = "";
        NpuDriverDate = "";
        EstimatedTops = "";
        Status = "偵測中…";

        try
        {
            await Task.Run(() =>
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
        Status = "未偵測到任何 NPU 裝置——這台機器應該沒有 NPU（或晶片組的 NPU 從未列出）；"
                + "若您確定硬體有 NPU，多半是驅動未安裝或裝置名稱未被認出，可回報型號以擴充關鍵字。";
                }
            });
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
            EstimatedTops = LookupTops(NpuName, _cpuName);
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
            EstimatedTops = LookupTops(NpuName, _cpuName);
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

    /// <summary>
    /// 型號 → TOPS 估計。internal 供單元測試釘住優先序（特定型號必須排在泛用之前）。
    /// 兩路比對：①NPU 裝置名（AMD「NPU Compute XXXX」、Qualcomm「Hexagon」直接命中）；
    /// ②CPU 產品名（Intel NPU 裝置名是「Intel(R) AI Boost」，不含平台資訊，只能靠 CPU 名判讀）。
    /// </summary>
    internal static string LookupTops(string deviceName, string? cpuName = null)
    {
        foreach (var (pattern, tops) in KnownTops)
        {
            if (deviceName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return $"~{tops} TOPS（依型號「{pattern}」查表估計，非本機推論實測）";
        }

        if (!string.IsNullOrWhiteSpace(cpuName))
        {
            // Intel Core Ultra：以型號後綴判平台（V＝Lunar Lake、HX/K/F＝Arrow Lake、H 看世代）
            var m = System.Text.RegularExpressions.Regex.Match(
                cpuName, "Core\\s*(?:\\(TM\\))?\\s*Ultra\\s*\\d\\s*(\\d{3})(V|HX|H|K|KF|KS|F|U|E)?\\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string digits = m.Groups[1].Value;
                string suffix = m.Groups[2].Value.ToUpperInvariant();
                (int tops, string platform) = (suffix, digits) switch
                {
                    ("V", _)  => (48, "Lunar Lake"),
                    ("HX", _) => (13, "Arrow Lake"),
                    ("K", _)  => (13, "Arrow Lake"),
                    ("KF", _) => (13, "Arrow Lake"),
                    ("KS", _) => (13, "Arrow Lake"),
                    ("F", _)  => (13, "Arrow Lake"),
                    ("E", _)  => (13, "Arrow Lake"),
                    ("H", _)  => digits.StartsWith("1") ? (10, "Meteor Lake") : (13, "Arrow Lake-H"),
                    ("U", _)  => (10, "Meteor Lake"),
                    (_, _)    => digits.StartsWith("3") ? (50, "Panther Lake")
                               : digits.StartsWith("2") ? (11, "Core Ultra 200（桌面）")
                               : (10, "Meteor Lake"),
                };
                return $"~{tops} TOPS（由 CPU 型號判讀為 {platform}，查表估計，非本機推論實測）";
            }
        }

        // 裝置存在但查表沒有：如實說明資料缺口，而不是給一個編造的數字
        return "—（型號不在查表中；NPU 裝置存在，驅動也沒有提供算力資訊）";
    }
}
