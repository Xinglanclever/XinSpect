using System.IO;

namespace XinSpect;

/// <summary>
/// 尋找由部署器獨立安裝的 BlueSquadronBridge.exe。
/// 正式安裝時，部署器會先依 Release SHA256SUMS 驗證橋接檔，再放到主程式旁；
/// 本類只負責解析本機橋接路徑，保持主程式不內嵌這個獨立元件。
/// </summary>
internal static class BlueSquadronBootstrap
{
    private const string ExeName = "BlueSquadronBridge.exe";

    public static string BridgeDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XinSpect", "bluesquadron");

    public static string EnsureExtracted()
    {
        string cached = Path.Combine(BridgeDir, ExeName);
        string? installed = ResolveLocalPath(DiskCandidates(), cached, File.Exists);
        if (installed is not null) return installed;

        throw new FileNotFoundException(
            "找不到 BlueSquadronBridge.exe。請執行 XinSpectDeploy，選擇桌面版部署，讓部署器下載並驗證獨立橋接程式。");
    }

    internal static string? ResolveLocalPath(
        IEnumerable<string> installedCandidates, string cachedPath, Func<string, bool> fileExists)
    {
        // 部署器放在主程式旁的版本優先，確保更新後會用到新橋接檔；
        // 舊版快取只作從其他位置啟動主程式時的回退。
        foreach (string candidate in installedCandidates)
            if (fileExists(candidate)) return candidate;
        return fileExists(cachedPath) ? cachedPath : null;
    }

    private static IEnumerable<string> DiskCandidates()
    {
        string bas = AppContext.BaseDirectory;
        yield return Path.Combine(bas, "bluesquadron", ExeName);
        yield return Path.Combine(bas, ExeName);

        // 開發建置：自執行檔目錄逐層向上找專案根目錄，
        // 相容標準 bin\\Debug\\... 與自訂 BaseOutputPath（如 obj\\_run\\... ）两種佈局
        string? dir = Path.GetFullPath(bas);
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            foreach (var cfg in new[] { "Debug", "Release" })
            {
                yield return Path.GetFullPath(Path.Combine(dir,
                    "BlueSquadron", "bin", cfg, "net10.0-windows", "win-x64", ExeName));
            }
            dir = Path.GetDirectoryName(dir);
        }
    }

}
