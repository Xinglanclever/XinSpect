namespace XinSpect;

/// <summary>
/// 逐鍵重複性（U10 第三圈 MT-0001，第 312 輪）：同一個鍵連量幾次，看它穩不穩。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要重複量：</b>單次讀值只證明「這一刻是多少」，不證明「下一次還是這個數」。
/// 溫度、風扇、使用率這類即時值的變動幅度本身就是資訊——它決定讀者能不能拿它當基準。
/// </para>
/// <para>
/// <b>三個不做的判斷：</b>①不給「穩定／不穩定」的分數，只寫跨度與漂移；②不比對閾值，
/// 因為「多少算穩」取決於鍵的語意（溫度 2 °C 與記憶體 2 MB 不是同一件事）；
/// ③不補值——某一輪沒讀到就是沒有，不插值、不以前一輪取代。
/// </para>
/// <para>
/// 輸入是<b>已完成的幾輪取樣</b>，這一支不做 I/O、不睡覺，所以測試可以餵任意資料重跑。
/// </para>
/// </remarks>
public static class MeasurementRepeatFactsService
{
    /// <summary>本輪重複量測的總覽（靜態鍵）。</summary>
    public const string SummaryKey = "mt.repeat.summary";

    /// <summary>逐鍵重複性家族：<c>mt.repeat.key.{鍵}</c>（成員＝這一輪實際重複量的鍵）。</summary>
    public const string FamilyPrefix = "mt.repeat.key.";

    /// <summary>至少要兩輪才算「重複」——一輪只是單次讀值。</summary>
    public const int MinimumRounds = 2;

    private const string Category = "量測嚴謹度";
    private const string Source = "本輪重複取樣（同一支讀取器跑多輪）";

    /// <summary>一次取樣的結果：鍵、以及那一輪讀到的值（讀不到就是 null，不是 0）。</summary>
    public sealed record RepeatSample(string Key, string Name, double? Value);

    /// <summary>把幾輪取樣聚合成重複性事實；不做任何 I/O。</summary>
    public static IReadOnlyList<HardwareFact> Collect(
        IReadOnlyList<IReadOnlyList<RepeatSample>> rounds,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(rounds);

        if (rounds.Count < MinimumRounds)
        {
            return
            [
                new HardwareFact(SummaryKey, Category, "重複量測", "", "輪", Source, FactTrustLevel.Reported,
                    false, at, rounds.Count, FactAvailability.NotApplicable,
                    $"只跑了 {rounds.Count} 輪——一輪是單次讀值，不是重複性。")
            ];
        }

        // 以鍵對齊：每個鍵在每一輪的位置可能不同（少了某個感測器），所以按鍵分組而不是按索引。
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var series = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        int presentTotal = 0;
        foreach (var round in rounds)
        {
            foreach (var sample in round)
            {
                if (string.IsNullOrWhiteSpace(sample.Key)) continue;
                names[sample.Key] = sample.Name;
                if (sample.Value is not { } v || double.IsNaN(v) || double.IsInfinity(v)) continue;
                presentTotal++;
                if (!series.TryGetValue(sample.Key, out var list)) series[sample.Key] = list = [];
                list.Add(v);
            }
        }

        if (series.Count == 0)
        {
            return
            [
                new HardwareFact(SummaryKey, Category, "重複量測", "", "輪", Source, FactTrustLevel.Reported,
                    false, at, rounds.Count, FactAvailability.ReadError,
                    $"{rounds.Count} 輪都沒有讀到任何可比較的數值——不是「完全穩定」，是「沒有東西可比」。")
            ];
        }

        var facts = new List<HardwareFact>();
        var stats = new List<(string Key, string Name, int Count, double Min, double Max, double Mean, double Drift)>();

        foreach (var (key, values) in series)
        {
            double min = values.Min(), max = values.Max(), mean = values.Average();
            double drift = values[^1] - values[0];
            stats.Add((key, names[key], values.Count, min, max, mean, drift));

            string value = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "最小 {0:0.###}／最大 {1:0.###}／平均 {2:0.###}；跨度 {3:0.###}；首末漂移 {4:+0.###;-0.###;0}（{5} 輪中 {6} 輪有值）",
                min, max, mean, max - min, drift, rounds.Count, values.Count);

            // 有值但少於總輪數：如實寫成 Unknown 並說明，不補值。
            var availability = values.Count == rounds.Count ? FactAvailability.Present : FactAvailability.Unknown;
            string? reason = values.Count == rounds.Count
                ? null
                : $"{rounds.Count - values.Count} 輪沒有讀到這個鍵（未補值）。";

            facts.Add(new HardwareFact(Slug(FamilyPrefix + key), Category, "重複性：" + names[key], value, "",
                Source, FactTrustLevel.Reported, false, at, max - min, availability, reason));
        }

        facts.Add(SummaryFact(rounds.Count, stats, presentTotal, at));
        return facts.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    private static HardwareFact SummaryFact(
        int roundCount,
        List<(string Key, string Name, int Count, double Min, double Max, double Mean, double Drift)> stats,
        int presentTotal,
        DateTimeOffset at)
    {
        var widest = stats
            .OrderByDescending(s => Math.Abs(s.Drift) / Math.Max(Math.Abs(s.Mean), 1e-9))
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .First();

        double widestPct = Math.Abs(widest.Drift) / Math.Max(Math.Abs(widest.Mean), 1e-9) * 100;

        string value = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} 輪、{1} 把鍵有可比數值（共 {2} 個讀值）；相對漂移最大的是「{3}」：{4:+0.###;-0.###;0}（{5:0.#}%）",
            roundCount, stats.Count, presentTotal, widest.Name, widest.Drift, widestPct);

        return new HardwareFact(SummaryKey, Category, "重複量測", value, "輪", Source,
            FactTrustLevel.Reported, false, at, roundCount, FactAvailability.Present, null);
    }

    /// <summary>鍵只留合法字元（事實鍵允許小寫字母、數字、點、底線、連字號、方括號）。</summary>
    /// <remarks>
    /// 先轉小寫再篩字元：感測器名字常帶大寫（<c>GPU/Fan:RPM</c>），若只篩不轉，
    /// 大寫字母會被整串丟掉，鍵會縮成殘骸（<c>an</c>）而不是可讀的 <c>gpufanrpm</c>。
    /// </remarks>
    private static string Slug(string key)
        => new(key.ToLowerInvariant().Where(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' or '[' or ']').ToArray());
}
