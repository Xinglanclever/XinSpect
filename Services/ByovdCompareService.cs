using System.IO;
using System.Security.Cryptography;

namespace XinSpect;

/// <summary>
/// BYOVD 逐驅動比對：把載入中的核心模組對上微軟「建議的驅動程式封鎖規則」。
/// 清單檔由使用者提供（自微軟文件下載 XML，**零出網**）——比對兩道：檔名規則（不分大小寫）
/// 與雜湊規則（SHA-256／SHA-1 對實檔計算）。命中逐條列模組＋規則依據；缺清單＝NotSupported
/// （不是錯誤）、清單解析失敗＝ReadError——與既有一致的三態哲學。
/// </summary>
public static class ByovdCompareService
{
    private const string Category = "系統與軟體";

    /// <summary>預設尋找路徑（使用者把微軟清單 XML 放進來即生效）。</summary>
    public static string? DefaultBlocklistPath()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Rules", "byovd-blocklist.xml"),
                     Path.Combine(AppContext.BaseDirectory, "byovd-blocklist.xml"),
                 })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, string? blocklistPath,
        Func<IReadOnlyList<KernelModuleEntry>> kernelModules)
    {
        blocklistPath ??= DefaultBlocklistPath();
        if (blocklistPath is null || !File.Exists(blocklistPath))
            return [new HardwareFact("byovd.rules", Category, "微軟建議驅動封鎖清單", "", "",
                "微軟建議的驅動程式封鎖規則 XML（使用者提供）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported,
                "未提供封鎖清單檔：可自微軟文件下載「建議的驅動程式封鎖規則」XML，放到程式目錄 Rules\\byovd-blocklist.xml 或以 --byovd 指定——缺檔不是錯誤，只是這道比對未啟用")];

        string xml = File.ReadAllText(blocklistPath);
        var rules = ByovdBlocklistDecoder.Parse(xml);
        if (rules.Count == 0)
            return [new HardwareFact("byovd.rules", Category, "微軟建議驅動封鎖清單", "", "",
                "微軟建議的驅動程式封鎖規則 XML（使用者提供）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, $"清單解析失敗或沒有 FileRule：{blocklistPath}——壞清單不解碼")];

        var modules = kernelModules();
        var hashCache = new Dictionary<string, (string? Sha256, string? Sha1)>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<HardwareFact>();
        int nameRules = rules.Count(r => r.FileName is not null);
        int hashRules = rules.Count(r => r.Sha256 is not null || r.Sha1 is not null);

        foreach (var module in modules)
        {
            string file = Path.GetFileName(module.Path);
            var rule = rules.FirstOrDefault(r =>
                r.FileName is not null && file.Length > 0 &&
                string.Equals(r.FileName, file, StringComparison.OrdinalIgnoreCase));
            if (rule is not null)
            {
                hits.Add(Hit($"byovd.hit.{hits.Count}", module.Path, rule.FriendlyName ?? rule.FileName!,
                    "檔名", at));
                continue;
            }
            if (hashRules == 0) continue;

            var (sha256, sha1) = HashOf(module.Path, hashCache);
            var hashRule = rules.FirstOrDefault(r =>
                (r.Sha256 is not null && sha256 == r.Sha256) ||
                (r.Sha1 is not null && sha1 == r.Sha1));
            if (hashRule is not null)
                hits.Add(Hit($"byovd.hit.{hits.Count}", module.Path,
                    hashRule.FriendlyName ?? "雜湊規則", "SHA-256", at));
        }

        var facts = new List<HardwareFact>
        {
            new("byovd.rules", Category, "微軟建議驅動封鎖清單",
                $"{rules.Count} 條規則（檔名 {nameRules}、雜湊 {hashRules}）", "條",
                $"微軟建議的驅動程式封鎖規則 XML（{blocklistPath}）", FactTrustLevel.Reported, false, at, rules.Count),
            new("byovd.hits", Category, "封鎖清單命中",
                hits.Count == 0 ? "0 個（載入中的核心模組沒有命中清單）" : $"{hits.Count} 個命中",
                "個", "載入中核心模組 vs 微軟建議封鎖規則", FactTrustLevel.Derived, false, at, hits.Count),
        };
        facts.AddRange(hits);
        return facts;
    }

    private static HardwareFact Hit(string key, string modulePath, string ruleName, string basis, DateTimeOffset at) =>
        new(key, Category, $"BYOVD 命中 {Path.GetFileName(modulePath)}",
            $"{modulePath} ↔ 規則「{ruleName}」（依{basis}）——此驅動在微軟建議封鎖清單上，攻擊面事實而非中毒判決", "",
            "載入中核心模組 vs 微軟建議封鎖規則", FactTrustLevel.Derived, false, at, null);

    private static (string? Sha256, string? Sha1) HashOf(string path, Dictionary<string, (string?, string?)> cache)
    {
        if (cache.TryGetValue(path, out var cached)) return cached;
        (string?, string?) result = (null, null);
        try
        {
            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                result = (Convert.ToHexStringLower(SHA256.HashData(fs)),
                    Convert.ToHexStringLower(SHA1.HashData(fs)));
            }
        }
        catch { result = (null, null); }
        cache[path] = result;
        return result;
    }
}
