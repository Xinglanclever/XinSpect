namespace XinSpect;

/// <summary>
/// 視覺化事實（Vol2 第 366–370 項）：VR-004 開機甘特圖（<c>vr.gantt</c>）、VR-005 能力雷達圖（<c>vr.radar</c>）、
/// VR-006 歷史圖縮放（<c>vr.zoom</c>）、VR-007 多軸對照圖（<c>vr.multiaxis</c>）、VR-008 圖表主題匯出（<c>vr.theme</c>）。
/// <para>
/// 第二批（2026-10-14）：VR-001 桑基資料流圖（<c>vr.sankey</c>）、VR-002 時間×感測器熱力圖（<c>vr.heatmap2d</c>）、
/// VR-003 拓撲樹狀圖（<c>vr.tree</c>，<b>只做 2D 縮放樹，3D 明文不做</b>）。
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>這些是「呈現」的事實</b>：描述一份圖表有哪些軸、幾個系列、可縮放範圍、主題。
/// 它們<b>不是資料</b>——資料來自其他事實服務，這裡只陳述呈現層的形狀與界線。
/// </para>
/// <para>
/// <b>紅線：</b>① 唯讀——不寫圖檔、不匯出到外部；② 圖表只呈現既有事實，不新增判決；
/// ③ 缺資料時軸／系列數如實為 0 並標明，不畫空圖冒充有內容；④ 主題匯出只產生字串描述，不落檔；
/// ⑤ 3D 樹狀圖明文不做（<c>vr.tree</c> 不列入本批，產品方向已定）。
/// </para>
/// </remarks>
public static class VisualizationFactsService
{
    private const string Cat = "視覺化";

    public const string GanttKey = "vr.gantt";
    public const string RadarKey = "vr.radar";
    public const string ZoomKey = "vr.zoom";
    public const string MultiAxisKey = "vr.multiaxis";
    public const string ThemeKey = "vr.theme";
    public const string SankeyKey = "vr.sankey";
    public const string HeatmapKey = "vr.heatmap2d";
    public const string TreeKey = "vr.tree";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    /// <summary>（階段數, 總時長 ms）——開機甘特圖的資料形狀。</summary>
    public delegate (int Phases, double TotalMs)? GanttProbe();
    /// <summary>（能力軸數, 有值的軸數）——雷達圖。</summary>
    public delegate (int Axes, int WithValues)? RadarProbe();
    /// <summary>（可縮放樣本數, 最小視窗點數）——歷史圖縮放。</summary>
    public delegate (int Samples, int MinWindow)? ZoomProbe();
    /// <summary>（軸數, 系列數）——多軸對照。</summary>
    public delegate (int Axes, int Series)? MultiAxisProbe();
    /// <summary>（主題名, 是否深色）。</summary>
    public delegate (string Name, bool Dark)? ThemeProbe();
    /// <summary>（節點數, 邊數, 流量總和）——桑基資料流圖。</summary>
    public delegate (int Nodes, int Edges, double Total)? SankeyProbe();
    /// <summary>（感測器數, 時間格數, 有值的格數）——時間×感測器熱力圖。</summary>
    public delegate (int Sensors, int Buckets, int Filled)? HeatmapProbe();
    /// <summary>（節點數, 最大深度, 可折疊節點數）——拓撲樹狀圖。</summary>
    public delegate (int Nodes, int MaxDepth, int Collapsible)? TreeProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        GanttProbe? gantt = null,
        RadarProbe? radar = null,
        ZoomProbe? zoom = null,
        MultiAxisProbe? multiAxis = null,
        ThemeProbe? theme = null,
        SankeyProbe? sankey = null,
        HeatmapProbe? heatmap = null,
        TreeProbe? tree = null)
        =>
        [
            GanttFact(at, gantt),
            RadarFact(at, radar),
            ZoomFact(at, zoom),
            MultiAxisFact(at, multiAxis),
            ThemeFact(at, theme),
            SankeyFact(at, sankey),
            HeatmapFact(at, heatmap),
            TreeFact(at, tree),
        ];

    // ── VR-004 開機甘特圖 ──────────────────────────────────────────────

    public static HardwareFact GanttFact(DateTimeOffset at, GanttProbe? probe = null)
    {
        (int Phases, double TotalMs)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(GanttKey, Cat, "開機甘特圖", "", "", "BootTimingFactsService（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有開機階段資料——不畫空甘特圖冒充有內容");

        var (phases, total) = s.Value;
        return new HardwareFact(GanttKey, Cat, "開機甘特圖",
            $"階段 {phases} 個、合計 {total:0} ms。" +
            "甘特圖是<b>呈現</b>不是量測——資料來自開機階段事實；本項只陳述圖表形狀，" +
            "不新增任何判決、不推論瓶頸。",
            "階段", "開機階段事實（呈現層）", FactTrustLevel.Derived, false, at, phases);
    }

    // ── VR-005 能力雷達圖 ──────────────────────────────────────────────

    public static HardwareFact RadarFact(DateTimeOffset at, RadarProbe? probe = null)
    {
        (int Axes, int WithValues)? s;
        try { s = (probe ?? (() => ReadRadar()))(); }
        catch { s = null; }

        if (s is null || s.Value.Axes == 0)
            return new HardwareFact(RadarKey, Cat, "能力雷達圖", "", "", "CapabilityMatrixService（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有能力矩陣資料——不畫空雷達圖");

        var (axes, withValues) = s.Value;
        return new HardwareFact(RadarKey, Cat, "能力雷達圖",
            $"能力軸 {axes} 條、有值 {withValues} 條。" +
            "<b>缺席不讀成不支援</b>（與能力矩陣同口徑）：沒有值的軸在圖上是缺口，不是 0 分——" +
            "雷達圖是分布呈現，不是評分。",
            "軸", "能力矩陣（呈現層）", FactTrustLevel.Derived, false, at, withValues);
    }

    private static (int, int) ReadRadar()
    {
        // 有值軸數以事實鍵目錄的 cap.* 為準；呈現層預設回實際可列舉數（此處無橫向依賴 → 0）
        int cap = FactKeyCatalog.Keys.Count(k => k.StartsWith("cap.", StringComparison.Ordinal));
        return (cap, cap);
    }

    // ── VR-006 歷史圖縮放 ──────────────────────────────────────────────

    public static HardwareFact ZoomFact(DateTimeOffset at, ZoomProbe? probe = null)
    {
        (int Samples, int MinWindow)? s;
        try { s = (probe ?? (() => (0, 2)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, ZoomKey, Cat, "歷史圖縮放", FactAvailability.ReadError, "縮放狀態不可讀");

        var (samples, minWindow) = s.Value;
        return new HardwareFact(ZoomKey, Cat, "歷史圖縮放",
            $"可縮放樣本 {samples} 點、最小視窗 {minWindow} 點。" +
            "縮放是<b>檢視</b>不是重新量測——放大不會增加樣本數；樣本少時縮到底仍看不出趨勢，" +
            "本項如實陳述樣本量，不假裝放大有意義。",
            "點", "歷史倉（呈現層）", FactTrustLevel.Reported, false, at, samples);
    }

    // ── VR-007 多軸對照圖 ──────────────────────────────────────────────

    public static HardwareFact MultiAxisFact(DateTimeOffset at, MultiAxisProbe? probe = null)
    {
        (int Axes, int Series)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Axes == 0)
            return new HardwareFact(MultiAxisKey, Cat, "多軸對照圖", "", "", "事實並排（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有可對照的資料——不畫空的多軸圖");

        var (axes, series) = s.Value;
        return new HardwareFact(MultiAxisKey, Cat, "多軸對照圖",
            $"軸 {axes} 條、系列 {series} 條。" +
            "多軸圖容易製造<b>虛假的相關</b>（不同單位的線放在一起看起來同步）——" +
            "本項只陳述圖表形狀，並提醒：相關性要有統計支撐（見 TrendSentinel），不能靠目視。",
            "條", "事實並排（呈現層）", FactTrustLevel.Reported, false, at, axes);
    }

    // ── VR-008 圖表主題匯出 ────────────────────────────────────────────

    public static HardwareFact ThemeFact(DateTimeOffset at, ThemeProbe? probe = null)
    {
        (string Name, bool Dark)? s;
        try { s = (probe ?? (() => ("Default", false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, ThemeKey, Cat, "圖表主題匯出", FactAvailability.ReadError, "主題不可讀");

        var (name, dark) = s.Value;
        return new HardwareFact(ThemeKey, Cat, "圖表主題匯出",
            $"目前主題「{name}」（{(dark ? "深色" : "淺色")}）。" +
            "主題匯出只產生<b>描述字串</b>，不落檔、不上傳；主題不影響資料——換主題不會改變任何事實值。",
            "", "主題設定（呈現層）", FactTrustLevel.Reported, false, at, dark ? 1 : 0);
    }

    // ── VR-001 桑基資料流圖 ────────────────────────────────────────────

    /// <summary>
    /// 資料在節點之間流動的量（例如「容量→分割區→已用／未用」）。
    /// </summary>
    /// <remarks>
    /// <b>桑基圖的誠實界線：條寬只代表流量比例，不代表好壞。</b>
    /// 一張「已用 90%」的桑基圖看起來很嚇人，但它只是在陳述容量分配——
    /// 是不是問題取決於那台機器的用途，本項不給判讀。
    /// </remarks>
    public static HardwareFact SankeyFact(DateTimeOffset at, SankeyProbe? probe = null)
    {
        (int Nodes, int Edges, double Total)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(SankeyKey, Cat, "桑基資料流圖", "", "", "事實並排（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有可畫成流的資料——不畫空桑基圖冒充有內容");

        var (nodes, edges, total) = s.Value;
        if (nodes == 0 || edges == 0)
            return new HardwareFact(SankeyKey, Cat, "桑基資料流圖", "", "", "事實並排（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "節點或邊為 0——沒有流動關係就沒有圖，不硬畫");

        return new HardwareFact(SankeyKey, Cat, "桑基資料流圖",
            $"節點 {nodes} 個、邊 {edges} 條、流量合計 {total:0.#}。" +
            "<b>條寬只是比例</b>：桑基圖把「量怎麼分配」畫出來，不判斷分配得好不好——" +
            "同一張圖在閒置機器與滿載機器上可以有截然不同的意義，那要配合情境看。" +
            "節點與邊的明細來自既有事實，本項只陳述圖形狀。",
            "條", "事實並排（呈現層）", FactTrustLevel.Derived, false, at, edges);
    }

    // ── VR-002 時間×感測器熱力圖 ──────────────────────────────────────

    /// <summary>
    /// 熱力圖的誠實界線：<b>缺格要看得出來</b>。
    /// </summary>
    /// <remarks>
    /// 熱力圖最容易騙人的地方是「沒有資料」與「值是 0」長得一樣——一片深藍可能是真的涼，
    /// 也可能是那段時間根本沒取樣。本項把有值格數與總格數並列，缺口在圖上要是缺口。
    /// </remarks>
    public static HardwareFact HeatmapFact(DateTimeOffset at, HeatmapProbe? probe = null)
    {
        (int Sensors, int Buckets, int Filled)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(HeatmapKey, Cat, "時間×感測器熱力圖", "", "", "歷史倉（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有歷史樣本——不畫空熱力圖（空白不是低溫）");

        var (sensors, buckets, filled) = s.Value;
        if (sensors == 0 || buckets == 0)
            return new HardwareFact(HeatmapKey, Cat, "時間×感測器熱力圖", "", "", "歷史倉（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "感測器或時間格為 0——沒有可畫的格");

        double coverage = 100.0 * filled / (sensors * buckets);
        string gap = coverage >= 99.5
            ? "全部格子都有值"
            : $"<b>{(sensors * buckets) - filled} 格沒有值</b>（覆蓋 {coverage:0.#}%）——" +
              "缺格在圖上必須是缺口，不能畫成最低色，否則讀者會把「沒量到」看成「很低」";

        return new HardwareFact(HeatmapKey, Cat, "時間×感測器熱力圖",
            $"感測器 {sensors} 條・時間格 {buckets} 格・有值 {filled} 格。{gap}。" +
            "熱力圖的用途是<b>看型態</b>（哪個時段整體偏高、哪條感測器與眾不同），" +
            "不是看絕對值——顏色深淺經過映射，讀單格顏色不如去讀那一格的事實。",
            "格", "歷史倉（呈現層）", FactTrustLevel.Derived, false, at, filled);
    }

    // ── VR-003 拓撲樹狀圖 ─────────────────────────────────────────────

    /// <summary>
    /// 拓撲樹的誠實界線：<b>只做 2D 可折疊樹，3D 明文不做</b>。
    /// </summary>
    /// <remarks>
    /// 產品方向已定（見本類別紅線）：3D 樹狀圖在可讀性上沒有換到任何東西，
    /// 卻讓「哪個節點連到哪個」變得要靠旋轉才看得懂。這一項回答的是
    /// 「樹有多深、有多少節點可折疊」，不宣稱任何 3D 呈現。
    /// </remarks>
    public static HardwareFact TreeFact(DateTimeOffset at, TreeProbe? probe = null)
    {
        (int Nodes, int MaxDepth, int Collapsible)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(TreeKey, Cat, "拓撲樹狀圖", "", "", "事實並排（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有可排成樹的階層資料——不畫空樹");

        var (nodes, maxDepth, collapsible) = s.Value;
        if (nodes == 0)
            return new HardwareFact(TreeKey, Cat, "拓撲樹狀圖", "", "", "事實並排（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "節點為 0——沒有階層就沒有樹");

        return new HardwareFact(TreeKey, Cat, "拓撲樹狀圖",
            $"節點 {nodes} 個・最大深度 {maxDepth} 層・可折疊 {collapsible} 個。" +
            "<b>只有 2D 可折疊樹，3D 明文不做</b>：3D 版本要靠旋轉才看得懂父子關係，" +
            "在可讀性上沒有換到任何東西；本項只陳述樹的形狀，不宣稱任何 3D 呈現。" +
            "樹的資料來自既有事實（PCIe／裝置樹），本項不新增層級判斷。",
            "個", "事實並排（呈現層）", FactTrustLevel.Derived, false, at, nodes);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
