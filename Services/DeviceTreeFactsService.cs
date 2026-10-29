namespace XinSpect;

/// <summary>
/// 裝置樹全圖（U10 第三圈 OS-0001，第 321 輪）：devnode 階層——「讀不到」的裝置在哪一層。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要這一支：</b>第 309 輪的 <c>os.dev.summary</c> 回答了「有幾台有問題」，
/// 但回答不了「問題在哪一層」。一台 USB 裝置讀不到，可能是它自己（裝置層）、
/// 它掛的集線器（橋層）、或整條匯流排（根層）——<b>三個答案的處置完全不同</b>，
/// 而既有事實把三層壓成同一個「N 台有問題」。
/// </para>
/// <para>
/// <b>「根」有兩種，必須分開講：</b>①Windows 回報它沒有父節點（真正的頂層）；
/// ②它有父節點，但那個父不在這份清單裡（父被過濾掉、或父是這台機器沒列舉到的節點）。
/// 兩者在既有的 <see cref="DeviceDiagnosticReport.Roots"/> 裡長得一樣（父找不到就當根），
/// 而<b>第二種是「樹在這裡斷了」，不是「它是頂層」</b>——這一支把兩種分開計數。
/// </para>
/// <para>
/// <b>不揭露 instance ID：</b>那串常含序號（與 <c>os.dev.*</c> 同一條界線）。
/// 鍵用序號、值用「類別＋名稱」辨識；<b>代價是同一類同名的兩個根在文字上分不出來</b>——
/// 這件事寫在總覽的界線裡，不假裝它分得出來。
/// </para>
/// <para>
/// 純函式：輸入是第 309 輪那份 <see cref="DeviceDiagnosticReport"/>（含 <c>Roots</c> 樹），
/// 這支不碰 SetupAPI，也不新增任何讀取面。
/// </para>
/// </remarks>
public static class DeviceTreeFactsService
{
    /// <summary>樹的總覽（靜態鍵）。</summary>
    public const string SummaryKey = "os.devtree.summary";

    /// <summary>逐根家族：<c>os.devtree.root.{序號}</c>（成員＝這台機器列舉出的根節點數，編譯期數不出來）。</summary>
    /// <remarks>
    /// 常數名不含 <c>Key</c>：事實鍵掃描器把「名字含 <c>Key</c> 的字串常數」當成事實鍵，
    /// 家族前綴若叫 <c>...Key</c> 會被當成一把不存在的鍵，讓未覆蓋數虛增（第 312 輪的教訓）。
    /// </remarks>
    public const string RootPrefix = "os.devtree.root.";

    /// <summary>每個根最多列幾個直接子節點；超過由值說明還有幾個沒列出（不靜默截斷）。</summary>
    public const int MaxChildrenListed = 8;

    /// <summary>最多列幾個根；超過由總覽說明還有幾個沒列出。</summary>
    public const int MaxRoots = 200;

    private const string Category = "裝置（PnP）";
    private const string Source = "第 309 輪同一份 SetupAPI 裝置列舉（Parent instance ID 建樹，唯讀）";

    /// <summary>把裝置樹投影成事實；不做 I/O。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DeviceDiagnosticReport report, DateTimeOffset at)
        => Collect(report, at, MaxRoots);

    /// <summary>同上，可指定根的上限（測試用；正式路徑用預設值）。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DeviceDiagnosticReport report, DateTimeOffset at, int maxRoots)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (maxRoots < 1) throw new ArgumentOutOfRangeException(nameof(maxRoots));

        if (report.Devices.Count == 0)
        {
            return
            [
                new HardwareFact(SummaryKey, Category, "裝置樹全圖", "", "台", Source,
                    FactTrustLevel.Reported, false, at, null, FactAvailability.NotApplicable,
                    "列舉回 0 台——這不是「這台機器沒有裝置樹」，是這一輪沒有讀到（見裝置診斷頁的來源狀態）。"),
            ];
        }

        // 從 Roots 走一遍，記下每個節點的層深與所屬根；走不到的就是成環的那一群。
        var depthOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootInfo = new List<(DeviceDiagnosticDevice Root, int Nodes, int Present, int Problems, int MaxDepth)>();

        void Walk(DeviceDiagnosticDevice node, int depth, ref int nodes, ref int present, ref int problems, ref int maxDepth)
        {
            if (!reached.Add(node.InstanceId)) return;   // 防禦：資料若成環也不無限遞迴
            depthOf[node.InstanceId] = depth;
            nodes++;
            if (node.IsPresent) present++;
            if (node.ProblemCode is > 0) problems++;
            if (depth > maxDepth) maxDepth = depth;
            foreach (var child in node.Children) Walk(child, depth + 1, ref nodes, ref present, ref problems, ref maxDepth);
        }

        var ordered = report.Roots
            .OrderBy(static r => r.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in ordered)
        {
            int nodes = 0, present = 0, problems = 0, maxDepth = 0;
            Walk(root, 1, ref nodes, ref present, ref problems, ref maxDepth);
            rootInfo.Add((root, nodes, present, problems, maxDepth));
        }

        var facts = new List<HardwareFact>(Math.Min(rootInfo.Count, maxRoots) + 1);

        for (int i = 0; i < Math.Min(rootInfo.Count, maxRoots); i++)
        {
            var (root, nodes, present, problems, maxDepth) = rootInfo[i];
            facts.Add(new HardwareFact($"{RootPrefix}{(i + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)}",
                Category, $"裝置樹根 {i + 1}", RootValue(root, nodes, present, problems, maxDepth), "台", Source,
                FactTrustLevel.Reported, false, at, nodes));
        }

        facts.Add(SummaryFact(report, rootInfo, depthOf, reached, maxRoots, at));
        return facts.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    private static string RootValue(
        DeviceDiagnosticDevice root, int nodes, int present, int problems, int maxDepth)
    {
        string self = Describe(root);
        string presence = present == nodes
            ? "全部在場"
            : $"在場 {present} 台、不在場 {nodes - present} 台";

        var children = root.Children
            .OrderBy(static c => c.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string childText = children.Count == 0
            ? "沒有直接子節點"
            : "直接子節點 " + children.Count + " 個：" + string.Join("、",
                children.Take(MaxChildrenListed).Select(Describe))
              + (children.Count > MaxChildrenListed ? $"（另有 {children.Count - MaxChildrenListed} 個未列出）" : "");

        string parentFace = root.ParentInstanceId == DeviceDiagnosticUnknown.Value
            ? "Windows 回報它沒有父節點（真正的頂層）"
            : "**它有父節點，但那個父不在這份清單裡**——樹在這裡斷了，不是「它是頂層」";

        return $"{self} 為根：子樹 {nodes} 台（{presence}）、最深 {maxDepth} 層、"
               + $"子樹內有問題碼 {problems} 台；{childText}。{parentFace}。";
    }

    private static HardwareFact SummaryFact(
        DeviceDiagnosticReport report,
        List<(DeviceDiagnosticDevice Root, int Nodes, int Present, int Problems, int MaxDepth)> roots,
        Dictionary<string, int> depthOf,
        HashSet<string> reached,
        int maxRoots,
        DateTimeOffset at)
    {
        int total = report.Devices.Count;
        int orphans = roots.Count(r => r.Root.ParentInstanceId != DeviceDiagnosticUnknown.Value);
        var stranded = report.Devices
            .Where(d => !reached.Contains(d.InstanceId))
            .ToList();

        // 層級分布：讀者問「問題在哪一層」時要的就是這個。
        var byDepth = depthOf.Values
            .GroupBy(static d => d)
            .OrderBy(static g => g.Key)
            .Select(static g => (Depth: g.Key, Count: g.Count()))
            .ToList();

        string histogram = byDepth.Count == 0
            ? "層級分布：（沒有節點）"
            : "層級分布：" + string.Join("、", byDepth.Take(8).Select(x => $"第 {x.Depth} 層 {x.Count} 台"))
              + (byDepth.Count > 8
                  ? $"（第 {byDepth[8].Depth} 層以後另有 {byDepth.Skip(8).Sum(x => x.Count)} 台）"
                  : "");

        int deepest = byDepth.Count == 0 ? 0 : byDepth[^1].Depth;

        string value =
            $"根 {roots.Count} 個、節點 {total} 台（在場 {report.PresentCount}／不在場 {report.GhostCount}）、最深 {deepest} 層；"
            + histogram
            + $"；父節點不在這份清單的根 {orphans} 個"
            + (stranded.Count > 0
                ? $"；成環而未連到任何根的節點 {stranded.Count} 台"
                : "；沒有成環的節點（每個節點都連得到某個根）")
            + (roots.Count > maxRoots ? $"；根清單只列前 {maxRoots} 個，另有 {roots.Count - maxRoots} 個未列出" : "")
            + "。樹由 Windows 回報的 Parent instance ID 建，不是本程式推的；"
            + "「根」有兩種（真的沒有父／父不在清單），兩種在本表分開計數；"
            + "值裡不放 instance ID（那串常含序號），所以同一類同名的兩個根在文字上分不出來。";

        return new HardwareFact(SummaryKey, Category, "裝置樹全圖", value, "台", Source,
            FactTrustLevel.Derived, false, at, total, FactAvailability.Present, null);
    }

    /// <summary>類別＋名稱（不含 instance ID）。</summary>
    private static string Describe(DeviceDiagnosticDevice device)
    {
        string cls = device.DeviceClass == DeviceDiagnosticUnknown.Value ? "未分類" : device.DeviceClass;
        string name = device.FriendlyName != DeviceDiagnosticUnknown.Value
            ? device.FriendlyName
            : device.Description != DeviceDiagnosticUnknown.Value ? device.Description : "（沒有名稱）";
        return $"{cls}「{name}」";
    }
}
