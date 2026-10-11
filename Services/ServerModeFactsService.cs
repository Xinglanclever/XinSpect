using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// Server Core 判定（Vol2 SV-011）：從 <c>InstallationType</c> 登錄值分辨
/// 「Server Core（無桌面體驗）」與「含桌面體驗的 Server／Client」。
/// </summary>
/// <remarks>
/// <b>為什麼讀登錄值而不是猜</b>：<c>InstallationType</c> 是 Windows 安裝時寫入的型別字串
/// （<c>Server Core</c>／<c>Server</c>／<c>Client</c>），比「有沒有 explorer.exe」這種啟發式可靠——
/// 後者在精簡映像或自訂 shell 上會誤判。缺值時如實標不支援，不推測。
/// </remarks>
public static class ServerModeFactsService
{
    private const string Category = "作業系統";
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Func<string?>? installationTypeProbe = null, Func<string?>? productNameProbe = null)
    {
        string? installationType;
        string? productName;
        bool failed = false;
        try
        {
            installationType = (installationTypeProbe ?? ReadInstallationType)();
            productName = (productNameProbe ?? ReadProductName)();
        }
        catch (Exception ex)
        {
            return [Na(at, "登錄讀取失敗：" + ex.Message, FactAvailability.ReadError)];
        }

        if (string.IsNullOrWhiteSpace(installationType))
            return [Na(at, "登錄值 InstallationType 不存在或為空——無法判定安裝型別（不推測）", FactAvailability.NotSupported)];

        string kind = installationType.Trim() switch
        {
            var t when t.Equals("Server Core", StringComparison.OrdinalIgnoreCase) => "Server Core（無桌面體驗）",
            var t when t.Equals("Server", StringComparison.OrdinalIgnoreCase) => "含桌面體驗（Desktop Experience）",
            var t when t.Equals("Client", StringComparison.OrdinalIgnoreCase) => "用戶端（Client）",
            _ => $"未收錄的型別（{installationType.Trim()}）"
        };

        return
        [
            new HardwareFact("sv.core", Category, "安裝型別（Server Core 判定）",
                $"{kind}；InstallationType={(failed ? "讀取失敗" : installationType.Trim())}"
                + (string.IsNullOrWhiteSpace(productName) ? "" : $"；產品名稱 {productName}"),
                "", $@"HKLM\{KeyPath}\InstallationType", FactTrustLevel.Measured, false, at),
        ];
    }

    private static string? ReadInstallationType()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue("InstallationType") as string;
    }

    private static string? ReadProductName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue("ProductName") as string;
    }

    private static HardwareFact Na(DateTimeOffset at, string reason, FactAvailability availability)
        => new("sv.core", Category, "安裝型別（Server Core 判定）", "", "", $@"HKLM\{KeyPath}",
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
