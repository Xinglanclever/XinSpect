namespace XinSpect;

/// <summary>
/// MAC OUI（前三組織唯一識別碼）名稱對照的純解碼器——比照 <see cref="SuperIoKnowledge"/> 的
/// 知識庫模式，只收錄大廠（IEEE OUI 登記的知名前綴子集）；未收錄如實標。
/// </summary>
public static class OuiKnowledge
{
    /// <summary>知名 OUI 前綴（IEEE 登記，常見子集）。新增條目時同步更新測試。</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A0-AF-1D"] = "Intel", ["00-1B-21"] = "Intel", ["8C-16-45"] = "Intel",
        ["00-E0-4C"] = "Realtek", ["10-7B-44"] = "Realtek",
        ["04-D9-F5"] = "ASUS", ["AC-22-0B"] = "ASUS",
        ["00-15-5D"] = "Microsoft（Hyper-V 虛擬）",
        ["00-03-93"] = "Apple", ["AC-DE-48"] = "Apple",
        ["00-1A-11"] = "GIGABYTE", ["94-DE-80"] = "GIGABYTE",
        ["00-50-56"] = "VMware 虛擬", ["00-0C-29"] = "VMware 虛擬",
        ["00-1C-14"] = "HTC", ["00-0A-E4"] = "NVIDIA",
        ["D8-BB-C1"] = "Microsoft（Surface）", ["9C-B6-D0"] = "Rivet Networks（Killer）",
    };

    /// <summary>MAC 字串（任意分隔）→ OUI 廠商名。未收錄標「未收錄」、格式壞標「無法解析」。</summary>
    public static string VendorOf(string mac)
    {
        var hex = new string((mac ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        if (hex.Length < 6 || !hex.All(char.IsAsciiHexDigit)) return "無法解析的 MAC";
        var oui = $"{hex[0]}{hex[1]}-{hex[2]}{hex[3]}-{hex[4]}{hex[5]}";
        return Known.TryGetValue(oui, out var vendor) && vendor is not null
            ? vendor
            : $"OUI {oui}（未收錄）";
    }
}
