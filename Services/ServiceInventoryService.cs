using System.Management;

namespace XinSpect;

/// <summary>一個 Windows 服務的盤點項目（WMI Win32_Service 彙整前形態）。</summary>
public sealed record SvcEntry(string Name, string DisplayName, string StartMode, string State, string StartName, string PathName);

/// <summary>
/// WP15 系統與軟體層：服務盤點（WMI Win32_Service，usermode 零特權）。
/// 通路層極薄（Fetch 失敗回 null），彙整（總數／執行中／啟動模式／非系統目錄）是純函式、
/// 由注入探測釘值測試。「非系統目錄」＝執行檔路徑不含引號內的 \Windows\——這些是第三方
/// 常駐面，數量與名稱如實列出，不下安全結論。
/// </summary>
public static class ServiceInventoryService
{
    private const string Category = "系統與軟體";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<SvcEntry>?>? probe = null)
    {
        var entries = (probe ?? FetchWmi)();
        if (entries is null)
            return Unavailable(at, "WMI Win32_Service 查詢失敗——服務盤點讀不到就是不猜");

        int running = entries.Count(e => e.State == "Running");
        int auto = entries.Count(e => e.StartMode == "Auto");
        int disabled = entries.Count(e => e.StartMode == "Disabled");
        var thirdParty = entries.Where(e => !IsWindowsDirectory(e.PathName)).ToList();

        string thirdValue = thirdParty.Count == 0
            ? "0 個"
            : $"{thirdParty.Count} 個；例如 " + string.Join("、", thirdParty.Take(3).Select(e => e.Name)) +
              (thirdParty.Count > 3 ? " 等" : "");
        return
        [
            Fact("svc.total", "服務總數", entries.Count.ToString(), "個", "Win32_Service 全列（含驅動服務以外的 Win32 服務）", at, entries.Count),
            Fact("svc.running", "執行中服務", running.ToString(), "個", "", at, running),
            Fact("svc.start.auto", "自動啟動服務", auto.ToString(), "個", "StartMode＝Auto", at, auto),
            Fact("svc.start.disabled", "停用服務", disabled.ToString(), "個", "StartMode＝Disabled", at, disabled),
            Fact("svc.nonwindows", "非系統目錄的服務", thirdValue, "", "執行檔不在 \\Windows\\ 下——第三方常駐面；數量本身不下安全結論", at, thirdParty.Count),
        ];
    }

    /// <summary>執行檔是否落在 Windows 目錄（大小寫不敏感；路徑可能帶引號或含參數——只比對前段）。</summary>
    public static bool IsWindowsDirectory(string pathName)
    {
        if (string.IsNullOrWhiteSpace(pathName)) return false;
        string trimmed = pathName.Trim().TrimStart('"');
        int end = trimmed.IndexOf('"');
        if (end > 0) trimmed = trimmed[..end];
        int exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) trimmed = trimmed[..(exe + 4)];
        return trimmed.Contains("\\Windows\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>WMI 通路（極薄）：任何失敗回 null，由 Collect 標三態。</summary>
    public static IReadOnlyList<SvcEntry>? FetchWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, StartMode, State, StartName, PathName FROM Win32_Service");
            var entries = new List<SvcEntry>();
            foreach (var m in searcher.Get())
            {
                entries.Add(new SvcEntry(
                    m["Name"]?.ToString() ?? "", m["DisplayName"]?.ToString() ?? "",
                    m["StartMode"]?.ToString() ?? "", m["State"]?.ToString() ?? "",
                    m["StartName"]?.ToString() ?? "", m["PathName"]?.ToString() ?? ""));
            }
            return entries;
        }
        catch { return null; }
    }

    private static HardwareFact Fact(string key, string name, string value, string unit, string note,
        DateTimeOffset at, double? numeric)
    {
        string v = string.IsNullOrEmpty(note) ? value : $"{value}（{note}）";
        return new HardwareFact(key, Category, name, v, unit, "WMI Win32_Service（服務盤點）",
            FactTrustLevel.Reported, false, at, numeric);
    }

    private static IReadOnlyList<HardwareFact> Unavailable(DateTimeOffset at, string reason) =>
        new[] { "svc.total", "svc.running", "svc.start.auto", "svc.start.disabled", "svc.nonwindows" }.Select(key =>
            new HardwareFact(key, Category, key switch
            {
                "svc.total" => "服務總數",
                "svc.running" => "執行中服務",
                "svc.start.auto" => "自動啟動服務",
                "svc.start.disabled" => "停用服務",
                _ => "非系統目錄的服務",
            }, "", "", "WMI Win32_Service（服務盤點）",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason)).ToList();
}
