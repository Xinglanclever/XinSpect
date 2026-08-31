using System.Globalization;

namespace XinSpect;

/// <summary>一支已安裝的驅動程式：裝置、版本、驅動日期、提供者與簽章狀態。</summary>
public sealed class DriverRow
{
    /// <summary>裝置名稱（同一支驅動掛在多個裝置上時只列一次，數量記在 <see cref="Instances"/>）。</summary>
    public string Device { get; init; } = "";

    /// <summary>WMI 回報的裝置類別原文（<c>DISPLAY</c>、<c>NET</c>…）。</summary>
    public string DeviceClass { get; init; } = "";

    public string Version { get; init; } = "";

    /// <summary>驅動日期；WMI 沒給或給的是空值時為 <c>null</c>，不推測。</summary>
    public DateTime? Date { get; init; }

    public string Provider { get; init; } = "";
    public bool Signed { get; init; }

    /// <summary>INF 檔名（<c>oem47.inf</c> 這種 oem 編號就是手動安裝進來的第三方 INF）。</summary>
    public string Inf { get; init; } = "";

    /// <summary>共用這支驅動的裝置實例數。</summary>
    public int Instances { get; init; } = 1;

    /// <summary>判讀文字（一句話說明為什麼被標記，或為什麼沒事）。</summary>
    public string Verdict { get; init; } = "";

    /// <summary>0＝沒有可疑之處、1＝關鍵類別而且驅動明顯老舊、2＝未經簽章。</summary>
    public int Severity { get; init; }

    public string ClassText => DriverAuditDecoder.ClassLabel(DeviceClass);
    public string VersionText => Version.Length > 0 ? Version : "—";
    public string DateText => Date is { } d ? d.ToString("yyyy-MM-dd") : "—";
    public string ProviderText => Provider.Length > 0 ? Provider : "—";
    /// <summary>簽章欄的短標籤。已簽章但被標為老舊時直接寫出被標記的理由，免得欄位只寫「已簽章」卻是橙色。</summary>
    public string SignText => !Signed ? "未簽章" : Severity == 1 ? "日期偏舊" : "已簽章";
    public string InfText => Inf.Length > 0 ? Inf : "—";
    public string InstancesText => Instances > 1 ? $"{Instances} 個裝置共用" : "";
}

/// <summary>
/// 驅動程式稽核的判讀規則（純函式，單元測試涵蓋）。
/// <para>
/// 這裡刻意不做「舊就是該更新」的簡單結論，因為那會製造大量假警報：①很多裝置的驅動就是十幾年沒動過
/// 而且完全正確；②<b>微軟隨附的通用驅動一律標成 2006-06-21</b>，那是佔位值不是發行日期，拿它算年紀會
/// 把上百支正常驅動全部標紅。所以年紀只在<b>關鍵類別</b>（顯示、網路、儲存、晶片組…）且非隨附驅動時才提。
/// </para>
/// </summary>
public static class DriverAuditDecoder
{
    /// <summary>關鍵類別中驅動日期超過幾年才值得一提。</summary>
    public const int OldYears = 5;

    /// <summary>微軟隨附（inbox）驅動的固定佔位日期——不是發行日期。</summary>
    public static readonly DateTime InboxPlaceholderDate = new(2006, 6, 21);

    /// <summary>會直接吃到效能與穩定性、值得盯著版本的類別。</summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    { "DISPLAY", "NET", "SCSIADAPTER", "HDC", "SYSTEM", "USB", "DISKDRIVE" };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DISPLAY"] = "顯示卡", ["MONITOR"] = "螢幕", ["NET"] = "網路介面", ["NETSERVICE"] = "網路服務",
        ["NETTRANS"] = "網路傳輸協定", ["NETCLIENT"] = "網路用戶端", ["MEDIA"] = "音訊",
        ["AUDIOENDPOINT"] = "音訊端點", ["USB"] = "USB 控制器", ["HDC"] = "硬碟控制器",
        ["SCSIADAPTER"] = "儲存控制器", ["DISKDRIVE"] = "磁碟", ["VOLUME"] = "磁碟區",
        ["VOLUMESNAPSHOT"] = "磁碟區快照", ["SYSTEM"] = "系統裝置", ["PROCESSOR"] = "處理器",
        ["COMPUTER"] = "電腦", ["HIDCLASS"] = "人性化介面裝置", ["KEYBOARD"] = "鍵盤", ["MOUSE"] = "滑鼠",
        ["BLUETOOTH"] = "藍牙", ["PRINTER"] = "印表機", ["PRINTQUEUE"] = "列印佇列", ["IMAGE"] = "影像裝置",
        ["CAMERA"] = "相機", ["FIRMWARE"] = "韌體", ["SECURITYDEVICES"] = "安全裝置",
        ["SOFTWARECOMPONENT"] = "軟體元件", ["SOFTWAREDEVICE"] = "軟體裝置", ["LEGACYDRIVER"] = "舊式驅動",
        ["SMARTCARDREADER"] = "智慧卡讀卡機", ["PORTS"] = "連接埠", ["BATTERY"] = "電池",
        ["SENSOR"] = "感測器", ["EXTENSION"] = "驅動延伸",
    };

    /// <summary>類別代號轉繁中；沒收錄的類別直接顯示原文，不藏起來。</summary>
    public static string ClassLabel(string? cls)
        => cls is not { Length: > 0 } c ? "—" : Labels.TryGetValue(c, out var s) ? s : c;

    /// <summary>是否屬於值得盯著版本的關鍵類別。</summary>
    public static bool IsCritical(string? cls)
        => cls is { Length: > 0 } c && Critical.Contains(c);

    /// <summary>CIM_DATETIME（<c>20230815000000.000000-000</c>）→ 日期；空值或全零回 <c>null</c>。</summary>
    public static DateTime? ParseCimDate(string? cim)
    {
        if (cim is not { Length: >= 14 }) return null;
        var head = cim.AsSpan(0, 14);
        if (head.SequenceEqual("00000000000000")) return null;
        return DateTime.TryParseExact(head, "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>是不是微軟隨附驅動的佔位日期（提供者為微軟且日期正好是 2006-06-21）。</summary>
    public static bool IsInboxPlaceholder(string? provider, DateTime? date)
        => date?.Date == InboxPlaceholderDate
           && provider is { Length: > 0 } p
           && p.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>距今多久（只給年月，不編造日數精度）。</summary>
    public static string AgeText(DateTime? date, DateTime now)
    {
        if (date is not { } d) return "日期未提供";
        if (d.Date > now.Date) return "日期在未來";
        int months = (now.Year - d.Year) * 12 + now.Month - d.Month;
        if (now.Day < d.Day) months--;
        if (months < 0) months = 0;
        int y = months / 12, m = months % 12;
        if (y > 0) return m > 0 ? $"{y} 年 {m} 個月" : $"{y} 年";
        return m > 0 ? $"{m} 個月" : "不到一個月";
    }

    /// <summary>單支驅動的判讀：回（一句話說明，嚴重度 0／1／2）。</summary>
    public static (string Verdict, int Severity) Judge(bool signed, DateTime? date,
                                                      string? provider, string? cls, DateTime now)
    {
        if (!signed)
            return ("未經數位簽章。Windows 預設不載入沒有簽章的核心模式驅動，所以它能在這台機器上跑，"
                  + "代表簽章檢查曾被放行——自簽憑證已匯入受信任的發行者、開了測試簽章模式，"
                  + "或它其實是使用者模式的元件。自己改過 INF 的裝置會落在這裡；來源不明的就該查清楚。", 2);

        if (IsInboxPlaceholder(provider, date))
            return ("Windows 隨附的通用驅動。日期 2006-06-21 是微軟給這類驅動的固定佔位值，"
                  + "不是發行日期，因此這裡不拿它算年紀。", 0);

        if (date is not { } d)
            return ("已簽章。這支驅動沒有回報日期，所以不推測它的新舊。", 0);

        string age = AgeText(date, now);
        if (IsCritical(cls) && (now.Date - d.Date).TotalDays >= OldYears * 365.25)
            return ($"已簽章，但驅動日期距今已 {age}，而且屬於「{ClassLabel(cls)}」這種直接吃效能與穩定性的類別。"
                  + "舊不等於壞——不少裝置的驅動就是這麼老而且正確；但這幾類若明顯落後，"
                  + "值得去裝置廠商官網對一下最新版本。", 1);

        return ($"已簽章 ・ 驅動日期距今 {age}。", 0);
    }

    /// <summary>清單搜尋：裝置、類別、版本、提供者、INF 任一命中即算（不分大小寫）。</summary>
    public static bool Matches(DriverRow r, string? needle)
    {
        if (needle is not { Length: > 0 } n) return true;
        return Has(r.Device, n) || Has(r.ClassText, n) || Has(r.DeviceClass, n)
            || Has(r.Version, n) || Has(r.Provider, n) || Has(r.Inf, n);

        static bool Has(string h, string n) => h.Contains(n, StringComparison.OrdinalIgnoreCase);
    }
}
