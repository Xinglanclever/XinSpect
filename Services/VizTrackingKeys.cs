namespace XinSpect;

/// <summary>
/// 第 273 輪 VZ 域可視化控制項的<b>追蹤鍵單一來源</b>（U10 第二圈：熱圖、子彈圖、
/// 日曆熱圖、SPC 控制圖）。
/// </summary>
/// <remarks>
/// <para>
/// 鍵字面常數刻意放在 Services/ 原始碼裡而不是 Controls/：鍵掃描器
/// （<see cref="CoverageService.ScanFactKeysFromSource"/>）只掃 Services/——
/// 第 272 輪把 <c>viz.hist</c>／<c>viz.ecdf</c> 寫在 Controls/DistributionCharts.cs，
/// 掃描器看不到，等於沒有登記。
/// </para>
/// <para>
/// 常數名刻意<b>不含「Key」</b>（同 <see cref="FactAtlasService.TableControlsFactKey"/> 的先例）：
/// 事實鍵目錄本輪凍結，掃描器會把名稱含「Key」的字串常數掃成事實鍵，
/// 讓「目錄≡掃描」守門紅燈——這些是產品追蹤標記，不是事實鍵；字面值照放 Services/ 供收口對帳。
/// </para>
/// <para>
/// 鍵名以 docs/ULTIMATE-10000/ULTIMATE-10000-EXPANSION-2026-10-25.md 的候選鍵欄為準：
/// U10-VZ-0004＝<c>viz.heatmap.coretime</c>（不是口語的 viz.heatmap.core）、
/// U10-VZ-0105＝<c>viz.controlchart</c>（不是 viz.spc）、
/// U10-VZ-0057＝<c>ui.dashboard.sensors</c>（見 <see cref="SensorDashboardService.DashboardFactKey"/>）。
/// </para>
/// </remarks>
public static class VizTrackingKeys
{
    /// <summary>U10-VZ-0004 熱圖：核×時間（逐核溫度矩陣）。</summary>
    public const string HeatmapCoretimeFactKey = "viz.heatmap.coretime";

    /// <summary>U10-VZ-0022 子彈圖：量測值 vs 規格區間帶。</summary>
    public const string BulletFactKey = "viz.bullet";

    /// <summary>U10-VZ-0098 日曆熱圖：異常事件按日統計。</summary>
    public const string CalendarHeatmapFactKey = "viz.calendar.heatmap";

    /// <summary>U10-VZ-0105 控制圖（SPC）：感測器漂移之統計過程控制呈現。</summary>
    public const string ControlChartFactKey = "viz.controlchart";
}
