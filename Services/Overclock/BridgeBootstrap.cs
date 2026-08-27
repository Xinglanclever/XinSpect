using System.IO;
using System.Reflection;

namespace XinSpect;

// ───────────────────────────────────────────────────────────────────────────
// 把內建的 net48 橋接程式（XtuBridge.exe）在首次使用時解壓到
// %LOCALAPPDATA%\XinSpect\bridge\，供 XtuOcEngine 以獨立程序啟動並操作硬體。
//
// 緣由：.NET 10 已移除 Intel XTU SDK 建構所需的舊版 WCF（System.ServiceModel 4.0.0.0），
// 故將 SDK 承載於原生 .NET Framework 4.8 執行階段的外掛程式；本類別負責「把工具內建進 App」——
// 橋接程式以內嵌資源隨主程式一同發佈，執行時自動落地，使用者無須另行安裝任何檔案。
// ───────────────────────────────────────────────────────────────────────────
internal static class BridgeBootstrap
{
    private const string ExeName = "XtuBridge.exe";

    /// <summary>橋接程式的解壓目錄（每位使用者可寫，無需系統管理員權限）。</summary>
    public static string BridgeDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XinSpect", "bridge");

    /// <summary>
    /// 確保 XtuBridge.exe 就緒並回傳其完整路徑。
    /// 依序嘗試：內嵌資源（正式發佈）→ 已解壓的副本 → App 目錄旁／開發建置輸出。
    /// 全都找不到時擲出例外，交由上層轉為「引擎不可用」的誠實回報。
    /// </summary>
    public static string EnsureExtracted()
    {
        // 1) 內嵌資源優先：解壓（含大小比對，避免每次啟動都覆寫）
        var asm = typeof(BridgeBootstrap).Assembly;
        string? exeRes = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase)
                                 && !n.EndsWith(".config", StringComparison.OrdinalIgnoreCase));
        if (exeRes is not null)
        {
            Directory.CreateDirectory(BridgeDir);
            string exe = Path.Combine(BridgeDir, ExeName);
            ExtractResource(asm, exeRes, exe);

            // 若組態檔（binding redirects 等）也被內嵌，一併解壓到同目錄
            string? cfgRes = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(ExeName + ".config", StringComparison.OrdinalIgnoreCase));
            if (cfgRes is not null) ExtractResource(asm, cfgRes, exe + ".config");

            return exe;
        }

        // 2) 先前已解壓的副本
        string cached = Path.Combine(BridgeDir, ExeName);
        if (File.Exists(cached)) return cached;

        // 3) App 目錄旁（發佈時附帶）或開發建置輸出
        foreach (var cand in DiskCandidates())
            if (File.Exists(cand)) return cand;

        throw new FileNotFoundException(
            "組件未內嵌 XtuBridge.exe，且磁碟上找不到可用副本。請先建置 Bridge 專案。");
    }

    private static IEnumerable<string> DiskCandidates()
    {
        string bas = AppContext.BaseDirectory;
        yield return Path.Combine(bas, "bridge", ExeName);
        yield return Path.Combine(bas, ExeName);
        // 開發建置：由 App 輸出目錄回溯到同層 Bridge 專案輸出（bin\{Release|Debug}\net48）
        yield return Path.GetFullPath(Path.Combine(bas, "..", "..", "..", "..", "Bridge", "bin", "Release", "net48", ExeName));
        yield return Path.GetFullPath(Path.Combine(bas, "..", "..", "..", "..", "Bridge", "bin", "Debug", "net48", ExeName));
    }

    // 以資源大小判斷是否需覆寫；寫暫存檔再原子換名，避免與執行中的舊實例爭用檔案。
    private static void ExtractResource(Assembly asm, string resName, string destPath)
    {
        using var s = asm.GetManifestResourceStream(resName);
        if (s is null) return;
        if (File.Exists(destPath) && new FileInfo(destPath).Length == s.Length) return;   // 已是同版本

        string tmp = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = File.Create(tmp)) s.CopyTo(fs);
        try
        {
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        }
        catch
        {
            // 舊副本被占用（可能有前一個橋接實例仍在）→ 清掉暫存檔，沿用既有 exe
            try { File.Delete(tmp); } catch { }
        }
    }
}
