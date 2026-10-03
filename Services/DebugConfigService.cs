using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// WP16 虛擬化／核心面：核心除錯與測試簽章的開機參數（登錄檔 SystemStartOptions，usermode 唯讀）。
/// Windows 把 BCD 選出的開機參數原樣記在這裡——解析只認關鍵字（DEBUG／DEBUGPORT／TESTSIGNING），
/// 沒有關鍵字＝未啟用（這是 Present 的「沒有」），登錄值整個讀不到才是 ReadError。原始字串全文附上可稽核。
/// </summary>
public static class DebugConfigService
{
    private const string Category = "系統與軟體";
    private const string Source = "登錄檔 Control\\SystemStartOptions（開機參數原樣）";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Func<string?>? probe = null)
    {
        string? options = (probe ?? ReadStartOptions)();
        if (options is null)
            return new[] { "dbg.start_options", "dbg.kernel", "dbg.testsigning" }.Select(key =>
                new HardwareFact(key, Category, NameOf(key), "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "SystemStartOptions 讀取失敗——開機參數讀不到就是不猜")).ToList();

        bool kernelDebug = options.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t.Equals("DEBUG", StringComparison.OrdinalIgnoreCase));
        string? debugPort = options.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.StartsWith("DEBUGPORT=", StringComparison.OrdinalIgnoreCase));
        bool testSigning = options.Contains("TESTSIGNING", StringComparison.OrdinalIgnoreCase);

        return
        [
            new HardwareFact("dbg.start_options", Category, "開機參數（原樣）", options, "", Source,
                FactTrustLevel.Reported, false, at, null),
            new HardwareFact("dbg.kernel", Category, "核心除錯",
                kernelDebug ? $"啟用{(debugPort is null ? "" : $"（{debugPort}）")}" : "未啟用（開機參數沒有 DEBUG 關鍵字）", "", Source,
                FactTrustLevel.Reported, false, at, null),
            new HardwareFact("dbg.testsigning", Category, "測試簽章",
                testSigning ? "啟用（TESTSIGNING 關鍵字存在——核心允許載入測試簽章驅動）" : "未啟用", "", Source,
                FactTrustLevel.Reported, false, at, null),
        ];
    }

    private static string NameOf(string key) => key switch
    {
        "dbg.start_options" => "開機參數（原樣）",
        "dbg.kernel" => "核心除錯",
        _ => "測試簽章",
    };

    /// <summary>登錄檔通路（極薄）：不存在或失敗回 null。</summary>
    public static string? ReadStartOptions()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            return key?.GetValue("SystemStartOptions") as string;
        }
        catch { return null; }
    }
}
