namespace XinSpect;

/// <summary>網卡的一條進階屬性（原樣呈現，不翻譯、不解讀）。</summary>
public sealed class NetAdapterProperty
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    /// <summary>對應的登錄關鍵字（如 <c>*RSS</c>、<c>*InterruptModeration</c>）；沒有就留破折號。</summary>
    public required string Keyword { get; init; }
}

/// <summary>
/// 網卡進階屬性與 RSS 的判讀（純函式）。
/// </summary>
/// <remarks>
/// 進階屬性一律<b>原樣呈現</b>：那些名稱與值是驅動自己提供的字串（而且已經被翻譯過），
/// 每家廠商的命名都不同，去解讀它的意思就是猜。這裡只把它們攤開，並附上登錄關鍵字——
/// 那個關鍵字才是跨語言唯一穩定的識別，也是使用者上網查或用 PowerShell 改的依據。
/// <para>
/// RSS 是唯一值得下判斷的一項：它決定收包處理能不能分到多顆核心上。
/// 「開著」不等於「有效」——只有一條接收佇列時，所有處理仍落在同一顆核。
/// </para>
/// </remarks>
public static class NetAdapterDecoder
{
    public static NetAdapterProperty Property(string name, string value, string keyword) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? "（未命名）" : name.Trim(),
        Value = string.IsNullOrWhiteSpace(value) ? "—" : value.Trim(),
        Keyword = string.IsNullOrWhiteSpace(keyword) ? "—" : keyword.Trim(),
    };

    /// <summary>
    /// 判讀 RSS：關閉、開著但只有一條佇列、以及真的分散在多條佇列上，三者的後果完全不同。
    /// </summary>
    public static (string Text, Severity Severity) JudgeRss(bool enabled, int queues, int logicalProcessors)
    {
        if (!enabled)
            return ("RSS 關閉：這張網卡的收包處理全部落在單一核心上（通常是收到中斷的那一顆）。"
                  + "高流量時那顆核會先滿，其他核閒著也幫不上——「中斷落在哪顆核」那張卡若點名某顆核被網卡打爆，"
                  + "原因常常就在這裡。", Severity.Warning);

        if (queues <= 0)
            return ("RSS 已啟用，但接收佇列數讀不到（驅動沒有回報）。讀不到就是讀不到，本頁不猜一個數字。",
                    Severity.Neutral);

        if (queues == 1)
            return ("RSS 已啟用，但只有一條接收佇列——效果跟關掉一樣，所有收包處理仍然落在同一顆核上。",
                    Severity.Warning);

        string ratio = logicalProcessors > 0
            ? $"這台機器有 {logicalProcessors} 顆邏輯處理器，收包能攤到其中 {queues} 顆上。"
            : "";
        return ($"RSS 已啟用，{queues} 條接收佇列：收包處理會分散到多顆核心。{ratio}", Severity.Good);
    }
}
