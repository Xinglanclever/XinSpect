namespace XinSpect;

/// <summary>
/// 一次歷史快照的最小投影：機器識別、時間、以及「這台機器的型號」。
/// </summary>
/// <param name="AnonymousMachineId">匿名機器識別（快照本來就只存雜湊）。</param>
/// <param name="CapturedAtUtc">快照時間。</param>
/// <param name="ModelKey">型號識別（廠商＋型號的組合，已正規化）；讀不到為 <c>null</c>。</param>
/// <param name="ModelText">型號原文（給人看）；讀不到為 <c>null</c>。</param>
/// <param name="Facts">這份快照的事實（鍵 → 數值），供逐鍵比對。</param>
public sealed record FleetSnapshot(
    string AnonymousMachineId,
    DateTimeOffset CapturedAtUtc,
    string? ModelKey,
    string? ModelText,
    IReadOnlyDictionary<string, double> Facts);

/// <summary>
/// 同型號族群基線（U10 第三圈 EN-0001）——<b>只在本機彙總，不上傳、不跨機傳輸</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>回答什麼：</b>「這台機器跟<b>同型號的其他本機快照</b>比起來，有沒有哪一項明顯不同」。
/// 候選項的理由寫得很好：「同一型號換過三台，只有本機基線看得出這台與同型不同」。
/// </para>
/// <para>
/// <b>三個必須寫死的界線：</b>
/// </para>
/// <list type="number">
/// <item><b>不上傳。</b>全部計算在本機完成，只讀本地快照目錄。這不是「先上傳再匿名」，
/// 而是<b>根本不連網</b>。</item>
/// <item><b>「同型號」是字串相等，不是產品知識。</b>型號取自快照裡的字串欄位並正規化
/// （去空白、轉大寫）。兩台機器「同型號」只代表那兩個字串一樣——
/// 不代表同一個 SKU、同一批、或同樣的散熱設計。</item>
/// <item><b>樣本數就是樣本數。</b>兩份快照也能算「族群」，但那個族群的標準差沒有意義。
/// 所以樣本少於 <see cref="MinSamplesForSpread"/> 時<b>只給中位數與範圍，不給 z 分數</b>。
/// </list>
/// </remarks>
public static class FleetBaselineFactsService
{
    public const string Category = "基線學習";
    public const string SummaryKey = "fleet.summary";
    public const string BoundaryKey = "fleet.boundary";
    public const string PeerPrefix = "fleet.peer.";

    /// <summary>算標準差／z 分數所需的最少樣本數。少於這個數字只給中位數與範圍。</summary>
    public const int MinSamplesForSpread = 4;

    private const string Source = "本機快照目錄（唯讀，不上傳）";

    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<FleetSnapshot> snapshots, DateTimeOffset at)
    {
        if (snapshots.Count == 0)
            return
            [
                new HardwareFact(SummaryKey, Category, "同型號族群基線", "", "份", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "本機沒有任何可用的快照——「沒有快照」與「沒有同型號的機器」是兩件事："
                    + "前者是還沒存過，後者要至少兩份**同型號**快照才說得出來"),
                BoundaryFact(at),
            ];

        var typed = snapshots.Where(s => !string.IsNullOrWhiteSpace(s.ModelKey)).ToList();
        if (typed.Count == 0)
            return
            [
                new HardwareFact(SummaryKey, Category, "同型號族群基線", "", "份", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    $"{snapshots.Count} 份快照裡沒有一份帶得出型號——**分不出族群就沒有族群**，"
                    + "不會用「全部快照」冒充同型號"),
                BoundaryFact(at),
            ];

        var facts = new List<HardwareFact>();
        var groups = typed.GroupBy(s => s.ModelKey!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        int index = 0;
        int multiGroups = 0;
        var groupFaces = new List<string>();
        foreach (var g in groups)
        {
            if (g.Count() < 2) continue;
            multiGroups++;
            index++;
            var members = g.OrderBy(s => s.CapturedAtUtc).ToList();
            facts.Add(PeerFact(index, g.Key, members, at));
            groupFaces.Add($"{members[0].ModelText ?? g.Key}×{members.Count}");
        }

        facts.Insert(0, SummaryFact(snapshots.Count, typed.Count, groups.Count, multiGroups, groupFaces, at));
        facts.Add(BoundaryFact(at));
        return facts;
    }

    private static HardwareFact SummaryFact(int total, int typed, int groups, int multiGroups,
        List<string> faces, DateTimeOffset at)
    {
        string value = $"{total} 份本機快照・其中 {typed} 份帶得出型號、分成 {groups} 個型號"
            + (multiGroups == 0
                ? "・**沒有一個型號出現兩次以上**——分不出「同型不同台」，所以這一輪給不出基線"
                : $"・其中有 {multiGroups} 個型號出現兩次以上：{string.Join("、", faces)}")
            + "・全部計算在本機完成：**不上傳、不跨機傳輸、不查外部資料庫**"
            + "・界線：型號是字串相等，不是 SKU 知識（見 fleet.boundary）";

        return new HardwareFact(SummaryKey, Category, "同型號族群基線", value, "份", Source,
            multiGroups > 0 ? FactTrustLevel.Derived : FactTrustLevel.Unknown, false, at,
            multiGroups > 0 ? multiGroups : null,
            multiGroups > 0 ? FactAvailability.Present : FactAvailability.NotApplicable,
            multiGroups > 0 ? null : "沒有任何型號出現兩次以上——不是「沒有差異」，是「沒有可以比的第二台」");
    }

    private static HardwareFact PeerFact(int index, string modelKey, IReadOnlyList<FleetSnapshot> members,
        DateTimeOffset at)
    {
        var latest = members[^1];
        // 只比「每一份都有數值」的鍵——缺一份就不是同一個分母。
        var common = members
            .Select(m => m.Facts)
            .Aggregate((a, b) => a.Keys.Intersect(b.Keys, StringComparer.Ordinal)
                .ToDictionary(k => k, k => a[k], StringComparer.Ordinal));
        var comparable = common
            .Where(kv => members.All(m => m.Facts.TryGetValue(kv.Key, out double v) && !double.IsNaN(v)))
            .ToList();

        bool enough = members.Count >= MinSamplesForSpread;

        // 找「這台（最新那一份）」離中位數最遠的鍵。這是這一輪真正要回答的那一句：
        // 「這台與同型不同」——所以排序看的是「離群程度」，不是變化幅度。
        var outliers = new List<(string Key, double Value, double Median, double Dev, double? Z)>();
        foreach (var (key, _) in comparable)
        {
            var values = members.Select(m => m.Facts[key]).OrderBy(v => v).ToList();
            double median = Median(values);
            double current = latest.Facts[key];
            double dev = current - median;
            double? z = null;
            if (enough)
            {
                double mean = values.Average();
                double variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
                double sd = Math.Sqrt(variance);
                z = sd > 0 ? (current - mean) / sd : null;
            }
            outliers.Add((key, current, median, dev, z));
        }

        var top = outliers
            .OrderByDescending(o => o.Z is { } z ? Math.Abs(z) : Math.Abs(o.Dev))
            .Take(5)
            .ToList();

        string spreadFace;
        if (top.Count == 0)
        {
            spreadFace = "這幾份快照沒有共同的可比數值鍵——**沒有共同分母就不比**";
        }
        else if (enough)
        {
            spreadFace = string.Join("；", top.Select(o =>
                $"{o.Key}：(最新) {Fmt(o.Value)}（族群中位 {Fmt(o.Median)}"
                + (o.Z is { } z ? "、z " + z.ToString("+0.0;-0.0") : "、z 算不出（標準差 0）") + "）"));
        }
        else
        {
            spreadFace = string.Join("；", top.Select(o =>
                $"{o.Key}：(最新) {Fmt(o.Value)}（族群中位 {Fmt(o.Median)}、差 {o.Dev.ToString("+0.#;-0.#")}）"))
                + $"・只有 {members.Count} 份樣本，**不給 z 分數**（少於 {MinSamplesForSpread} 份時標準差不值得算）";
        }

        string value = $"型號 {members[0].ModelText ?? modelKey}（{members.Count} 份快照，最新 {latest.CapturedAtUtc:yyyy-MM-dd}）"
            + $"・共 {comparable.Count} 個鍵在每一份都有數值"
            + $"・{spreadFace}"
            + "・界線：這是**同一台機器不同時間**或**同型號不同台**的快照放在一起比，"
            + "兩者混在一起時「差異」可能只是時間造成的；"
            + "**本鍵不判讀差異是好是壞**——它只說「離族群多遠」";

        return new HardwareFact(PeerPrefix + index.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
            Category, $"同型號族群 {index}", value, "份", Source, FactTrustLevel.Derived, false, at,
            members.Count);
    }

    private static double Median(List<double> sorted)
    {
        int n = sorted.Count;
        if (n == 0) return double.NaN;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static string Fmt(double v) =>
        Math.Abs(v) >= 1000 ? v.ToString("N0") : v.ToString("0.###");

    private static HardwareFact BoundaryFact(DateTimeOffset at) =>
        new(BoundaryKey, Category, "同型號族群基線的界線宣告",
            "① **不上傳**：全部計算在本機完成，只讀本地快照目錄——不是「先上傳再匿名」，"
            + "是根本不連網；②「同型號」是**字串相等**（正規化後），不是 SKU 知識："
            + "兩台機器型號字串一樣，不代表同一批、同樣散熱、同樣韌體版本；"
            + $"③ 樣本少於 {MinSamplesForSpread} 份時**只給中位數與範圍，不給 z 分數**——"
            + "兩三個樣本的標準差是裝飾品；④ 只比**每一份都有數值**的鍵，"
            + "缺一份就不是同一個分母；⑤ 同一台機器不同時間的快照與同型號不同台的快照"
            + "會混在一起，本版**不區分**——「差異」可能只是時間造成的；"
            + "⑥ **不判讀差異是好是壞**：離群不等於故障，中位數也不等於正常",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);
}
