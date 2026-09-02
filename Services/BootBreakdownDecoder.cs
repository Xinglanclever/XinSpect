namespace XinSpect;

/// <summary>開機時被記為「比平常慢」的一個項目。</summary>
public sealed class BootCulprit
{
    public required string Name { get; init; }
    /// <summary>類別：服務、驅動、應用程式、裝置…（由事件編號決定）。</summary>
    public required string Kind { get; init; }
    /// <summary>這個項目總共花掉的時間（毫秒）。</summary>
    public required long TotalMs { get; init; }
    /// <summary>比平常多花的時間（毫秒）——這才是罪證。</summary>
    public required long DegradationMs { get; init; }

    public string TotalText => BootBreakdownDecoder.MsText(TotalMs);
    public string DegradationText => BootBreakdownDecoder.MsText(DegradationMs);
}

/// <summary>開機耗時的判決。</summary>
public sealed class BootVerdict
{
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    public required Severity Severity { get; init; }
}

/// <summary>
/// 開機耗時分解的彙整與判讀（純函式）。
/// </summary>
/// <remarks>
/// 資料來自 Windows 自己的 Diagnostics-Performance 頻道：事件 100 給總時間、主路徑與登入後時間，
/// 事件 101／102／103／106／109／110 直接點名是哪一支應用程式、驅動、服務或裝置比平常慢，附毫秒數。
/// <para>
/// 排序<b>依 degradation（比平常多花的時間）而不是總時間</b>：一支服務本來就要花兩秒不代表它有問題，
/// 它比平常多花兩秒才有問題。這是 Windows 自己的判斷基準，本頁沿用而不另立標準。
/// </para>
/// <para>
/// 界線：只呈現 Windows 記下來的事實與去哪一頁看，<b>不建議刪除或停用任何東西</b>——
/// 那需要知道使用者實際依賴什麼，本程式不知道。
/// </para>
/// </remarks>
public static class BootBreakdownDecoder
{
    /// <summary>總開機時間超過這麼久算慢（毫秒）。</summary>
    private const long SlowBootMs = 30_000, VerySlowBootMs = 60_000;

    /// <summary>單一項目多花這麼久就足以點名（毫秒）。</summary>
    private const long NoticeDegradationMs = 3_000, SeriousDegradationMs = 10_000;

    /// <summary>登入後時間超過主路徑這麼多倍，就把矛頭指向啟動項而不是驅動。</summary>
    private const double PostBootRatio = 1.5;

    /// <summary>只留真的比平常慢的項目，依多花的時間由多到少排列。</summary>
    public static List<BootCulprit> Rank(IReadOnlyList<BootCulprit> items)
        => [.. items.Where(i => i.DegradationMs > 0)
                    .OrderByDescending(i => i.DegradationMs)
                    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>毫秒 → 文字；0 代表沒有數字可寫，顯示破折號而不是 0 ms。</summary>
    public static string MsText(long ms)
        => ms <= 0 ? "—" : ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.00} 秒";

    /// <param name="channelMissing">
    /// 這台機器上根本沒有 Diagnostics-Performance 頻道（Windows Server 不隨附這個頻道）。
    /// 這與「頻道存在但還沒有紀錄」是兩件不同的事，訊息必須分開。
    /// </param>
    public static BootVerdict Judge(long bootMs, long mainPathMs, long postBootMs,
                                    IReadOnlyList<BootCulprit> culprits, bool channelMissing = false)
    {
        if (channelMissing)
            return new BootVerdict
            {
                Headline = "這個 Windows 版本沒有開機效能紀錄",
                Severity = Severity.Neutral,
                Detail = "開機耗時分解靠的是 Windows 的 Diagnostics-Performance 頻道，而這台機器上"
                       + "根本沒有這個頻道——Windows Server 不隨附它（用戶端的 Windows 10／11 才有）。"
                       + "沒有資料來源就是沒有，這張卡片不會改用開機時間戳去推一個數字出來。",
            };

        if (bootMs <= 0)
            return new BootVerdict
            {
                Headline = "讀不到開機耗時紀錄",
                Severity = Severity.Neutral,
                Detail = "Diagnostics-Performance 頻道存在，但裡面還沒有可用的開機事件——"
                       + "可能是頻道被停用，或系統剛安裝還沒累積紀錄。讀不到就是讀不到，"
                       + "本卡片不會估一個開機時間出來。",
            };

        var ranked = Rank(culprits);
        var top = ranked.Count > 0 ? ranked[0] : null;

        // 慢在哪一段：主路徑（驅動與服務初始化）還是登入後（啟動項）
        string phase = postBootMs > mainPathMs * PostBootRatio
            ? $"主路徑花了 {MsText(mainPathMs)}，登入後又花了 {MsText(postBootMs)}——"
              + "時間主要花在登入後，那一段是啟動項與登入時觸發的工作，不是驅動初始化。"
              + "「開機啟動項」那一頁列得出是哪些。"
            : $"主路徑花了 {MsText(mainPathMs)}、登入後 {MsText(postBootMs)}，時間主要花在主路徑"
              + "（核心、驅動與服務初始化）。";

        string who = top is null
            ? "Windows 沒有把任何項目記為比平常慢。"
            : $"被記為比平常慢的第一名是 {top.Name}（{top.Kind}）：總共 {top.TotalText}，"
              + $"其中比平常多花了 {top.DegradationText}。";

        var severity =
            bootMs >= VerySlowBootMs || (top?.DegradationMs ?? 0) >= SeriousDegradationMs ? Severity.Serious
          : bootMs >= SlowBootMs || (top?.DegradationMs ?? 0) >= NoticeDegradationMs ? Severity.Warning
          : Severity.Good;

        string head = severity switch
        {
            Severity.Serious => $"開機花了 {MsText(bootMs)}，明顯偏慢",
            Severity.Warning => $"開機花了 {MsText(bootMs)}",
            _ => $"開機花了 {MsText(bootMs)}，沒有明顯拖慢",
        };

        return new BootVerdict
        {
            Headline = head,
            Severity = severity,
            Detail = $"{phase} {who} 排序依「比平常多花的時間」而不是總時間——"
                   + "一支服務本來就要花兩秒不代表有問題，它比平常多花兩秒才有問題。"
                   + "本卡片只呈現 Windows 記下來的事實，不對該不該停用任何項目表示意見。",
        };
    }
}
