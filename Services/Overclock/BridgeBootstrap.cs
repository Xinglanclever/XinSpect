using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;

namespace XinSpect;

// ───────────────────────────────────────────────────────────────────────────
// 把內建的 net48 橋接程式（XtuBridge.exe）在首次使用時解壓到
// %LOCALAPPDATA%\XinSpect\bridge\，供 XtuOcEngine 以獨立程序啟動並操作硬體。
//
// 緣由：.NET 10 已移除 Intel XTU SDK 建構所需的舊版 WCF（System.ServiceModel 4.0.0.0），
// 故將 SDK 承載於原生 .NET Framework 4.8 執行階段的外掛程式；本類別負責「把工具內建進 App」——
// 橋接程式以內嵌資源隨主程式一同發佈，執行時自動落地，使用者無須另行安裝任何檔案。
//
// 完整性（安全）：本程式以 requireAdministrator 執行，解壓目錄卻在使用者設定檔內——
// 若以「檔案大小相同」就沿用既有副本，同一使用者的中完整性程序（瀏覽器沙箱、外掛等）
// 可預先植入同長度的惡意 exe，待下次啟動（含開機自啟）被本行程以高權限執行。
// 因此兩道防線並用：
//   1) 目錄 ACL 收緊為 SYSTEM + Administrators（停用繼承），使未提升的程序寫不進來；
//   2) 內容一律以 SHA256 對內嵌正本驗證，驗不過就重解壓；無法覆寫時寧可讓
//      超頻引擎不可用，也絕不執行無法確認出處的二進位。
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
        // 1) 內嵌資源優先：以內容雜湊判斷是否需覆寫（避免每次啟動都重寫）
        var asm = typeof(BridgeBootstrap).Assembly;
        string? exeRes = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase)
                                 && !n.EndsWith(".config", StringComparison.OrdinalIgnoreCase));
        if (exeRes is not null)
        {
            try { SecureBridgeDir(); } catch { /* ACL 收緊失敗不阻斷啟動；內容驗證仍在 */ }

            string exe = Path.Combine(BridgeDir, ExeName);
            string expectedHash;
            using (var s = asm.GetManifestResourceStream(exeRes)!)
                expectedHash = FileHash.Of(s);
            exe = ExtractVerified(asm, exeRes, exe, expectedHash);

            // 若組態檔（binding redirects 等）也被內嵌，一併解壓到同目錄（檔名跟著實際使用的 exe 走）
            string? cfgRes = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(ExeName + ".config", StringComparison.OrdinalIgnoreCase));
            if (cfgRes is not null) ExtractResource(asm, cfgRes, exe + ".config");

            return exe;
        }

        // 2) 先前已解壓的副本（開發情境：組件未內嵌時才會走到；內容即本機建置產物）
        string cached = Path.Combine(BridgeDir, ExeName);
        if (File.Exists(cached)) return cached;

        // 3) App 目錄旁（發佈時附帶）或開發建置輸出
        foreach (var cand in DiskCandidates())
            if (File.Exists(cand)) return cand;

        throw new FileNotFoundException(
            "組件未內嵌 XtuBridge.exe，且磁碟上找不到可用副本。請先建置 Bridge 專案。");
    }

    // 把解壓目錄的 ACL 收緊：停用繼承，僅留 SYSTEM 與 Administrators 完整控制。
    // 執行到此時行程必為已提升（app.manifest 為 requireAdministrator），Administrators
    // SID 在提升權杖中為啟用狀態，自己仍可寫入；同一使用者的未提升程序則被擋在外。
    private static void SecureBridgeDir()
    {
        var di = Directory.CreateDirectory(BridgeDir);
        var sec = di.GetAccessControl();
        sec.SetAccessRuleProtection(true, false);   // 停用繼承，規則僅保留下方明列的兩條
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                 })
            sec.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }

    // 落地橋接程式本體：以 SHA256 對內嵌正本驗證，回傳「本次應使用的 exe 路徑」。
    // 既有副本驗得過＝就是本版本的內嵌正本，免寫（也避免與執行中的舊實例爭用檔案）。
    private static string ExtractVerified(Assembly asm, string resName, string destPath, string expectedHash)
    {
        if (FileHash.Of(destPath) == expectedHash) return destPath;

        string tmp = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var s = asm.GetManifestResourceStream(resName)!)
        using (var fs = File.Create(tmp)) s.CopyTo(fs);
        if (FileHash.Of(tmp) != expectedHash)
        {
            try { File.Delete(tmp); } catch { }
            throw new IOException("橋接程式解壓後未通過完整性驗證，已中止（可能是磁碟問題）。");
        }

        try
        {
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
            CleanupStaleCopies(destPath);
            return destPath;
        }
        catch
        {
            // 正式檔名被占用（前一個橋接實例仍在執行，Windows 不允許刪除執行中的 exe 映像）。
            // 此時既不沿用驗不過的舊檔，也不因此讓超頻功能不可用：改以帶隨機尾綴的檔名
            // 落地這份已驗證的正本並直接使用；下次正式檔名空出來時會自動換回。
            try
            {
                string alt = destPath + "." + Guid.NewGuid().ToString("N")[..8] + ".exe";
                File.Move(tmp, alt);
                return alt;
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw new IOException("橋接程式落地失敗：正式檔名被占用，且暫存目錄無法寫入。");
            }
        }
    }

    // 換回正式檔名後，清掉先前留下的替代檔名副本（可能仍被某個執行中的橋接實例使用，
    // 刪除失敗就留給下次；目錄已由 ACL 限定只有系統管理員可寫，不會累積外來檔案）。
    private static void CleanupStaleCopies(string canonicalPath)
    {
        try
        {
            string dir = Path.GetDirectoryName(canonicalPath)!;
            string stem = Path.GetFileNameWithoutExtension(canonicalPath);
            foreach (var f in Directory.EnumerateFiles(dir, stem + ".*.exe"))
                if (!string.Equals(f, canonicalPath, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { /* 執行中的實例會鎖檔，留給下次 */ }
        }
        catch { /* 清理失敗不影響啟動 */ }
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

    // 組態檔（非可執行內容）：以資源大小判斷是否需覆寫；寫暫存檔再原子換名，避免與執行中的舊實例爭用檔案。
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
            // 舊副本被占用（可能有前一個橋接實例仍在）→ 清掉暫存檔，沿用既有檔
            try { File.Delete(tmp); } catch { }
        }
    }
}
