namespace XinSpect;

/// <summary>
/// 視覺化事實（Vol2 第 366–370 項）：VR-004 開機甘特圖（<c>vr.gantt</c>）、VR-005 能力雷達圖（<c>vr.radar</c>）、
/// VR-006 歷史圖縮放（<c>vr.zoom</c>）、VR-007 多軸對照圖（<c>vr.multiaxis</c>）、VR-008 圖表主題匯出（<c>vr.theme</c>）。
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

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        GanttProbe? gantt = null,
        RadarProbe? radar = null,
        ZoomProbe? zoom = null,
        MultiAxisProbe? multiAxis = null,
        ThemeProbe? theme = null)
        =>
        [
            GanttFact(at, gantt),
            RadarFact(at, radar),
            ZoomFact(at, zoom),
            MultiAxisFact(at, multiAxis),
            ThemeFact(at, theme),
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

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
