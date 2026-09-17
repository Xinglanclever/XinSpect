using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace XinSpect;

/// <summary>
/// 把 BlueSquadronBridge.exe 解壓到 %LOCALAPPDATA%\XinSpect\bluesquadron\，
/// 並確保目錄 ACL 僅限 SYSTEM + Administrators。
/// 沿用 BridgeBootstrap 的完整性模型：SHA256 校驗 + ACL 鎖定。
/// </summary>
internal static class BlueSquadronBootstrap
{
    private const string ExeName = "BlueSquadronBridge.exe";

    public static string BridgeDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XinSpect", "bluesquadron");

    public static string EnsureExtracted()
    {
        // 1) 內嵌資源（正式發佈）
        var asm = typeof(BlueSquadronBootstrap).Assembly;
        string? exeRes = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase));
        if (exeRes is not null)
        {
            try { SecureDir(); } catch { }
            string exe = Path.Combine(BridgeDir, ExeName);
            return ExtractVerified(asm, exeRes, exe);
        }

        // 2) 已存副本
        string cached = Path.Combine(BridgeDir, ExeName);
        if (File.Exists(cached)) return cached;

        // 3) 磁碟候選（開發建置）
        foreach (var cand in DiskCandidates())
            if (File.Exists(cand)) return cand;

        throw new FileNotFoundException(
            "找不到 BlueSquadronBridge.exe。請先建置 BlueSquadron 專案。");
    }

    private static void SecureDir()
    {
        var di = Directory.CreateDirectory(BridgeDir);
        var sec = di.GetAccessControl();
        sec.SetAccessRuleProtection(true, false);
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

    private static string ExtractVerified(Assembly asm, string resName, string destPath)
    {
        string expectedHash;
        using (var s = asm.GetManifestResourceStream(resName)!)
            expectedHash = HashOf(s);
        if (File.Exists(destPath) && HashOf(destPath) == expectedHash)
            return destPath;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string tmp = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var s = asm.GetManifestResourceStream(resName)!)
        using (var fs = File.Create(tmp)) s.CopyTo(fs);

        if (HashOf(tmp) != expectedHash)
        {
            try { File.Delete(tmp); } catch { }
            throw new IOException("BlueSquadronBridge 解壓後未通過完整性驗證。");
        }

        try
        {
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
            return destPath;
        }
        catch
        {
            string alt = destPath + "." + Guid.NewGuid().ToString("N")[..8] + ".exe";
            try { File.Move(tmp, alt); return alt; }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }
    }

    private static IEnumerable<string> DiskCandidates()
    {
        string bas = AppContext.BaseDirectory;
        yield return Path.Combine(bas, "bluesquadron", ExeName);
        yield return Path.Combine(bas, ExeName);
        // 開發建置
        yield return Path.GetFullPath(Path.Combine(bas, "..", "..", "..", "..",
            "BlueSquadron", "bin", "Debug", "net10.0-windows", "win-x64", ExeName));
        yield return Path.GetFullPath(Path.Combine(bas, "..", "..", "..", "..",
            "BlueSquadron", "bin", "Release", "net10.0-windows", "win-x64", ExeName));
    }

    private static string HashOf(Stream s)
    {
        s.Position = 0;
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private static string HashOf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch { return ""; }
    }
}
