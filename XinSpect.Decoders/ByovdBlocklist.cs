using System.Xml.Linq;

namespace XinSpect;

/// <summary>微軟建議驅動封鎖清單的一條規則（檔名規則或雜湊規則，取自 XML 可得屬性）。</summary>
public sealed record ByovdBlockRule(string? FileName, string? Sha256, string? Sha1, string? FriendlyName);

/// <summary>
/// 微軟「建議的驅動程式封鎖規則」XML 的純解碼器（WDAC SiPolicy 格式的 FileRule 子集）。
/// 屬性名寬容對待（FileName／Hash／MinimumSHA256Hash／SHA256Hash／SHA1Hash）——
/// 微軟文件版本的屬性拼法有差異，規則語意一律視為 deny。解析失敗回空清單（呼叫方標三態）。
/// </summary>
public static class ByovdBlocklistDecoder
{
    /// <summary>解析封鎖清單 XML。非 XML／無 FileRule 回空清單——不猜。</summary>
    public static IReadOnlyList<ByovdBlockRule> Parse(string xml)
    {
        try
        {
            var root = XElement.Parse(xml);
            var rules = new List<ByovdBlockRule>();
            foreach (var el in root.Descendants().Where(e =>
                         e.Name.LocalName == "FileRule" || e.Name.LocalName == "FileAttributeRule"))
            {
                string? fileName = Attr(el, "FileName") is { Length: > 0 } fn ? fn.ToLowerInvariant() : null;
                string? sha256 = NormHex(FirstAttr(el, "MinimumSHA256Hash", "SHA256Hash", "Hash"));
                string? sha1 = NormHex(FirstAttr(el, "SHA1Hash", "MinimumSHA1Hash"));
                string? friendly = Attr(el, "FriendlyName") is { Length: > 0 } f ? f : null;
                if (fileName is not null || sha256 is not null || sha1 is not null)
                    rules.Add(new ByovdBlockRule(fileName, sha256, sha1, friendly));
            }
            return rules;
        }
        catch { return []; }
    }

    private static string? Attr(XElement el, string name) => el.Attribute(name)?.Value;

    private static string? FirstAttr(XElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.Attribute(n)?.Value is { Length: > 0 } v) return v;
        return null;
    }

    /// <summary>雜湊字串正規化：去空白、小寫（比對用）。空回 null。</summary>
    private static string? NormHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = new string(value.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 0 ? null : hex;
    }
}
