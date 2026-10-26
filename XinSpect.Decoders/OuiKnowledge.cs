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

    /// <summary>本表收錄的 OUI 條數（<c>KnowledgeCoverageTests</c> 與內建清冊用；不手抄）。</summary>
    public static int Count => Known.Count;

    // ── 出處：給人讀的名字，與給機器分類的標記 ──────────────────────────

    /// <summary>
    /// 名稱出處段落的<strong>固定開頭</strong>——與 <see cref="PciIdLookup"/> 同一套做法。
    ///
    /// <para>
    /// <b>為什麼要這個：</b>回收端（<c>KnowledgeLookupCollector</c>）原本是比對
    /// 「值裡有沒有出現某句中文」來分類，而那條路徑在 WMI 查詢失敗時會把
    /// <b>空字串算成命中</b>——查詢根本沒發生，<c>net.oui</c> 卻印「全部命中」。
    /// 現在值裡有一段固定標記，產生端與回收端共用同一組常數。
    /// </para>
    /// </summary>
    public const string OriginClausePrefix = "名稱出處：";

    /// <summary>出處代號：本專案手工收錄的 IEEE 前綴子集。</summary>
    public const string CuratedToken = "手工表";

    /// <summary>出處代號：表裡沒有這個前綴。</summary>
    public const string UnlistedToken = "未收錄";

    /// <summary>出處代號：輸入本身無法解析（不是表的問題）。</summary>
    public const string UnparsableToken = "無法解析";

    /// <summary>一次 OUI 查詢的結果。</summary>
    public enum OuiOrigin
    {
        /// <summary>表裡有這個前綴。</summary>
        Hit,

        /// <summary>格式對，但表裡沒有。</summary>
        Unlisted,

        /// <summary>MAC 字串本身無法解析。</summary>
        Unparsable,
    }

    /// <summary>MAC 字串 → 查詢結果（<b>不是</b>名字；名字看 <see cref="VendorOf"/>）。</summary>
    public static OuiOrigin OriginOf(string mac) =>
        !TryOui(mac, out string oui) ? OuiOrigin.Unparsable
        : Known.TryGetValue(oui, out var vendor) && vendor is not null ? OuiOrigin.Hit
        : OuiOrigin.Unlisted;

    /// <summary>查詢結果 → 值裡用的出處代號。</summary>
    public static string OriginText(OuiOrigin origin) => origin switch
    {
        OuiOrigin.Hit => CuratedToken,
        OuiOrigin.Unlisted => UnlistedToken,
        _ => UnparsableToken,
    };

    /// <summary>
    /// 一行 MAC 對照：`MAC（廠商名） ・ 名稱出處：…`。
    /// 名稱與出處<strong>放在同一行但各佔一段</strong>——回收端讀後面那段，人讀前面那段。
    /// </summary>
    public static string Describe(string mac)
    {
        string subject = string.IsNullOrWhiteSpace(mac) ? "（空 MAC）" : mac;
        return $"{subject}（{VendorOf(mac)}） ・ {OriginClausePrefix}{OriginText(OriginOf(mac))}";
    }

    /// <summary>取出前三個 byte 的 OUI 字串（`XX-XX-XX`）。格式不對回 false。</summary>
    private static bool TryOui(string mac, out string oui)
    {
        oui = "";
        var hex = new string((mac ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        if (hex.Length < 6 || !hex.All(char.IsAsciiHexDigit)) return false;
        oui = $"{hex[0]}{hex[1]}-{hex[2]}{hex[3]}-{hex[4]}{hex[5]}";
        return true;
    }

    /// <summary>MAC 字串（任意分隔）→ OUI 廠商名。未收錄標「未收錄」、格式壞標「無法解析」。</summary>
    public static string VendorOf(string mac)
    {
        if (!TryOui(mac, out string oui)) return "無法解析的 MAC";
        return Known.TryGetValue(oui, out var vendor) && vendor is not null
            ? vendor
            : $"OUI {oui}（未收錄）";
    }
}
