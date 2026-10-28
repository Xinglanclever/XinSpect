namespace XinSpect;

/// <summary>
/// 逐鍵時間戳出處（U10 第三圈 MT-0003，第 310 輪）：回答「這份報告裡的數字是什麼時候量的」。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要一支服務：</b>每一筆事實都帶著 <see cref="HardwareFact.MeasuredAtUtc"/>，但沒有任何地方
/// 把「這些時間戳散得多開」講出來。於是一份看起來同時量的報告，可能混著剛剛讀的感測器、
/// 五分鐘前的 SMART、以及上一次開機留下的東西——讀者無從分辨。
/// </para>
/// <para>
/// <b>只做三件事，而且都是可以從資料本身複算的：</b>
/// ①時間範圍（最早、最晚、跨度）；②被混合的世代數（不同的量測時刻有幾組，取到秒）；
/// ③哪些域明顯落後於最新的那一刻（<c>mt.stamp.lag.{域}</c>）。
/// <b>不判斷「這樣好不好」</b>——落後 3 秒與落後 3 天都是事實，判定交給讀者。
/// </para>
/// <para>
/// 輸入是<b>這一輪已收集完的其它事實</b>，輸出不含自己：與 <c>CoverageFactsService</c> 同一個規矩，
/// 否則每跑一次時間戳就會多出一把新鍵，範圍永遠多一筆。
/// </para>
/// </remarks>
public static class MeasurementStampFactsService
{
    /// <summary>本輪事實的時間範圍（靜態鍵）。</summary>
    public const string RangeKey = "mt.stamp.range";

    /// <summary>逐域落後家族：<c>mt.stamp.lag.0001</c>…（只收明顯落後的域，依落後量排序）。</summary>
    public const string LagPrefix = "mt.stamp.lag.";

    /// <summary>落後超過這個秒數才算「不是同一刻量的」。</summary>
    public const int LagThresholdSeconds = 5;

    /// <summary>最多列幾個落後的域（超過由範圍鍵說明還有幾個）。</summary>
    public const int MaxLagFacts = 20;

    private const string Category = "量測嚴謹度";

    /// <summary>從已收集的事實推算時間戳出處；不做任何 I/O。</summary>
    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<HardwareFact> facts, DateTimeOffset now)
        => Collect(facts, now, MaxLagFacts);

    /// <summary>同上，可指定逐域明細的上限（測試用；正式路徑用預設值）。</summary>
    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<HardwareFact> facts, DateTimeOffset now, int maxLagFacts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (maxLagFacts < 1) throw new ArgumentOutOfRangeException(nameof(maxLagFacts));

        if (facts.Count == 0)
        {
            return
            [
                new HardwareFact(RangeKey, Category, "事實時間範圍", "", "", "本輪事實集（不需硬體）",
                    FactTrustLevel.Reported, false, now, null, FactAvailability.NotApplicable,
                    "這一輪沒有任何事實，時間範圍無從計算——不是「時間一致」，是「沒有東西可算」。")
            ];
        }

        var timed = facts
            .Where(static f => f.MeasuredAtUtc != default)
            .ToList();
        if (timed.Count == 0)
        {
            return
            [
                new HardwareFact(RangeKey, Category, "事實時間範圍", "", "", "本輪事實集（不需硬體）",
                    FactTrustLevel.Reported, false, now, null, FactAvailability.ReadError,
                    "所有事實的時間戳都是預設值——這份報告說不出「什麼時候量的」。")
            ];
        }

        var oldest = timed.Min(static f => f.MeasuredAtUtc);
        var newest = timed.Max(static f => f.MeasuredAtUtc);
        var span = newest - oldest;
        int generations = timed
            .Select(static f => f.MeasuredAtUtc.ToUnixTimeSeconds())
            .Distinct()
            .Count();

        // 逐域：這一域最舊的一筆，比全體最新的那一筆落後多少。
        var lags = timed
            .GroupBy(static f => DomainOf(f.Key), StringComparer.Ordinal)
            .Select(g => (Domain: g.Key, Oldest: g.Min(static f => f.MeasuredAtUtc), Count: g.Count()))
            .Select(x => (x.Domain, x.Count, Lag: newest - x.Oldest))
            .Where(x => x.Lag.TotalSeconds > LagThresholdSeconds)
            .OrderByDescending(x => x.Lag)
            .ThenBy(x => x.Domain, StringComparer.Ordinal)
            .ToList();

        var factsOut = new List<HardwareFact>
        {
            RangeFact(oldest, newest, span, generations, timed.Count, facts.Count, lags.Count, maxLagFacts, now)
        };

        int index = 0;
        foreach (var (domain, count, lag) in lags.Take(maxLagFacts))
        {
            // 鍵一定要帶序號：家族前綴本身不是鍵，重複的前綴會被快照的鍵檢查退掉。
            string key = LagPrefix + (++index).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            factsOut.Add(new HardwareFact(key, Category, "落後的域：" + domain, Describe(domain, count, lag),
                "", "本輪事實集（不需硬體）", FactTrustLevel.Reported, false, now,
                (long)lag.TotalSeconds, FactAvailability.Present, null));
        }

        return factsOut;
    }

    private static HardwareFact RangeFact(
        DateTimeOffset oldest,
        DateTimeOffset newest,
        TimeSpan span,
        int generations,
        int timedCount,
        int totalCount,
        int laggingDomains,
        int maxLagFacts,
        DateTimeOffset now)
    {
        string value = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "最早 {0:yyyy-MM-ddTHH:mm:ssZ}／最晚 {1:yyyy-MM-ddTHH:mm:ssZ}；跨度 {2}；" +
            "不同量測時刻 {3} 組、{4} 筆有時間戳（共 {5} 筆）",
            oldest.ToUniversalTime(), newest.ToUniversalTime(), Describe(span), generations, timedCount, totalCount);

        if (laggingDomains > 0)
            value += string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "；{0} 個域的最舊一筆落後最新一刻超過 {1} 秒（這份報告不是同一瞬間量的）",
                laggingDomains, LagThresholdSeconds);
        if (laggingDomains > maxLagFacts)
            value += "；逐域明細只列前 " + maxLagFacts + " 個，另有 " + (laggingDomains - maxLagFacts) + " 個未列出";
        if (totalCount > timedCount)
            value += "；" + (totalCount - timedCount) + " 筆沒有時間戳（未計入範圍）";

        return new HardwareFact(RangeKey, Category, "事實時間範圍", value, "", "本輪事實集（不需硬體）",
            FactTrustLevel.Reported, false, now, (long)span.TotalSeconds, FactAvailability.Present, null);
    }

    private static string Describe(string domain, int count, TimeSpan lag)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} 域 {1} 筆，最舊一筆落後最新一刻 {2}",
            domain, count, Describe(lag));

    /// <summary>跨度的人話：秒級講秒，跨日講天。</summary>
    private static string Describe(TimeSpan span)
    {
        if (span.TotalSeconds < 1) return "不到 1 秒";
        if (span.TotalMinutes < 1) return span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " 秒";
        if (span.TotalHours < 1) return span.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " 分";
        if (span.TotalDays < 1) return span.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " 小時";
        return span.TotalDays.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " 天";
    }

    private static string DomainOf(string key)
    {
        int dot = key.IndexOf('.');
        return dot > 0 ? key[..dot] : key;
    }
}
