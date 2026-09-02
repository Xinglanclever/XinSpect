namespace XinSpect;

/// <summary>Windows 授權的白話判讀。</summary>
public sealed class LicenseVerdict
{
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    public required Severity Severity { get; init; }
}

/// <summary>
/// Windows 授權狀態與通道的判讀（純函式）。
/// </summary>
/// <remarks>
/// 一般使用者真正想知道的是三件事：<b>這台是不是正版、重裝之後會不會掉、能不能移到新電腦</b>。
/// 前者看授權狀態碼，後兩者看授權通道——隨機版（OEM）綁在這台主機板上，零售版可以轉移。
/// <para>
/// 界線：本頁<b>不提供任何啟用、變更或移除授權的動作</b>，只把 Windows 自己回報的狀態翻成人話。
/// 完整金鑰預設遮蔽（只顯示 Windows 一向公開的後五碼），要看得自己按一下。
/// </para>
/// </remarks>
public static class LicenseDecoder
{
    /// <summary>SoftwareLicensingProduct.LicenseStatus 的官方定義。</summary>
    public static (string Text, Severity Severity) StatusText(uint status) => status switch
    {
        0 => ("未授權", Severity.Critical),
        1 => ("已授權（正版啟用）", Severity.Good),
        2 => ("初始寬限期內（尚未啟用）", Severity.Warning),
        3 => ("寬限期已過但仍可用（需要啟用）", Severity.Warning),
        4 => ("非正版寬限期", Severity.Serious),
        5 => ("通知模式（啟用已失效）", Severity.Serious),
        6 => ("延長寬限期", Severity.Warning),
        _ => ($"未收錄的狀態碼 {status}（不猜它的意思）", Severity.Neutral),
    };

    /// <summary>
    /// 授權通道 → 白話。判斷依據是 Windows 自己在 Description 裡寫的通道字樣，
    /// 那幾個關鍵字是英文常數（不隨系統語言翻譯），所以比對它們是安全的。
    /// </summary>
    public static string ChannelText(string? description)
    {
        string d = description ?? "";
        if (d.Contains("OEM", StringComparison.OrdinalIgnoreCase))
            return "隨機版（OEM）：授權綁在這台主機板上。重裝同一台機器會自動啟用，但不能移到別的電腦。";
        if (d.Contains("Retail", StringComparison.OrdinalIgnoreCase))
            return "零售版（Retail）：可以轉移到另一台電腦，轉移前先在舊機器上解除連結。";
        if (d.Contains("Volume", StringComparison.OrdinalIgnoreCase))
            return "大量授權（Volume）：由公司或學校的授權伺服器管理，個人通常無法自行轉移。";
        if (d.Length == 0) return "通道讀不到（Windows 沒有回報），因此不猜能不能轉移。";
        return $"通道字樣：{d}（未收錄的類型，不硬套解釋）";
    }

    /// <summary>後五碼永遠可以顯示——那是 Windows 自己在「設定」裡就會給的資訊，不是祕密。</summary>
    public static string PartialKeyText(string? partial)
        => string.IsNullOrWhiteSpace(partial) ? "讀不到" : $"…-{partial.Trim()}";

    /// <summary>完整金鑰的遮蔽形式：只留最後五碼，其餘一律星號。</summary>
    public static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "讀不到（這台機器的韌體沒有內嵌金鑰）";
        string k = key.Trim();
        string tail = k.Length >= 5 ? k[^5..] : k;
        return $"XXXXX-XXXXX-XXXXX-XXXXX-{tail}";
    }

    public static LicenseVerdict Judge(uint status, string? description, bool firmwareKeyPresent)
    {
        var (statusText, severity) = StatusText(status);
        string channel = ChannelText(description);
        string firmware = firmwareKeyPresent
            ? "這台機器的韌體裡內嵌了 Windows 金鑰（品牌機與預裝 Windows 的機器通常有），重裝時會自動帶入，不必手動輸入。"
            : "韌體裡沒有內嵌金鑰：自組機或後來自行升級的機器通常是這樣，重裝前請先確認自己的金鑰或已連結的微軟帳戶。";

        return new LicenseVerdict
        {
            Headline = statusText,
            Severity = severity,
            Detail = $"{channel} {firmware}",
        };
    }
}
